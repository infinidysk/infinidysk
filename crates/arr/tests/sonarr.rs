//! Which API we talk to a Sonarr through, and the calls that differ between v3 and v5.
#![allow(clippy::unwrap_used)] // Test helpers outside `#[test]` fns; panics are the assertions.

use std::sync::{Arc, Mutex};

use axum::Json;
use axum::extract::Request;
use axum::http::StatusCode;
use axum::response::{IntoResponse, Response};
use infinidysk_arr::queue::{self, Entry};
use infinidysk_arr::webhook::{self, Hook};
use infinidysk_arr::{Arr, Error, Profile, refresh_downloads};
use serde_json::{Value, json};
use tokio::net::TcpListener;

type Log = Arc<Mutex<Vec<String>>>;

/// Answers `GET /api` with `info` (`None`: not found) and every other call with
/// `answer(path)`; logs each call with its query. Rejects the API key `bad`.
async fn sonarr(info: Option<Value>, answer: fn(&str) -> Value) -> (Arr, Log) {
    let log: Log = Arc::default();
    let listener = TcpListener::bind("127.0.0.1:0").await.unwrap();
    let url = format!("http://{}/sonarr", listener.local_addr().unwrap());
    let handler = {
        let log = Arc::clone(&log);
        move |req: Request| {
            let log = Arc::clone(&log);
            let info = info.clone();
            async move {
                assert_eq!(req.headers()["X-Sonarr-Client"], "infinidysk");
                log.lock()
                    .unwrap()
                    .push(format!("{} {}", req.method(), req.uri()));
                let path = req.uri().path().trim_start_matches("/sonarr");
                let response: Response = match (path, &info) {
                    _ if req.headers()["X-Api-Key"] == "bad" => {
                        StatusCode::UNAUTHORIZED.into_response()
                    }
                    ("/api", Some(info)) => Json(info.clone()).into_response(),
                    ("/api", None) => StatusCode::NOT_FOUND.into_response(),
                    (path, _) => Json(answer(path)).into_response(),
                };
                response
            }
        }
    };
    let app = axum::Router::new().fallback(handler);
    tokio::spawn(async move { axum::serve(listener, app).await });
    let arr = Arr {
        id: uuid::Uuid::nil(),
        name: "sonarr".to_owned(),
        profile: Profile::Sonarr,
        url,
        api_key: "k".to_owned(),
    };
    (arr, log)
}

/// What `GET /api` says on Sonarr v4.
fn v4() -> Value {
    json!({ "current": "v3", "deprecated": [] })
}

/// What `GET /api` says on Sonarr v5.
fn v5() -> Value {
    json!({ "current": "v5", "deprecated": ["v3"] })
}

fn calls(log: &Log) -> Vec<String> {
    std::mem::take(&mut *log.lock().unwrap())
}

fn queue_page(path: &str) -> Value {
    match path {
        "/api/v5/queue" => json!({ "records": [{
            "id": 7, "title": "pack", "downloadId": "job", "downloadClient": "sab",
            "status": "completed", "trackedDownloadState": "importBlocked",
            "episodeIds": [11, 12, 21], "statusMessages": [],
        }]}),
        "/api/v3/notification" | "/api/v5/connection" => json!([{ "id": 4, "name": "Infinidysk" }]),
        _ => json!({}),
    }
}

#[tokio::test(flavor = "multi_thread")]
async fn sonarr_v5_is_spoken_to_through_its_v5_api() {
    let http = reqwest::Client::new();
    let (arr, log) = sonarr(Some(v5()), queue_page).await;

    let entries = queue::queue(&http, &arr).await.unwrap();
    assert_eq!(
        entries,
        [Entry {
            id: 7,
            title: "pack".to_owned(),
            download_id: "job".to_owned(),
            client: "sab".to_owned(),
            status: "completed".to_owned(),
            state: "importBlocked".to_owned(),
            messages: Vec::new(),
            media: vec![("episode", 11), ("episode", 12), ("episode", 21)],
        }]
    );
    queue::remove(&http, &arr, 7, Some("Import blocked: sample"), true)
        .await
        .unwrap();
    queue::remove(&http, &arr, 7, Some("Import blocked: sample"), false)
        .await
        .unwrap();
    queue::remove(&http, &arr, 7, None, false).await.unwrap();
    refresh_downloads(&http, &arr).await.unwrap();
    let hook = Hook {
        name: "infinidysk",
        url: "http://x/hook",
        username: "u",
        password: "p",
    };
    webhook::register(&http, &arr, &hook).await.unwrap();

    let calls = calls(&log);
    let page = "page=1&pageSize=200&includeUnknownSeriesItems=true\
                &includeUnknownMovieItems=true&includeUnknownArtistItems=true\
                &includeUnknownAuthorItems=true";
    assert_eq!(
        calls,
        [
            "GET /sonarr/api".to_owned(),
            format!("GET /sonarr/api/v5/queue?{page}"),
            "GET /sonarr/api".to_owned(),
            "DELETE /sonarr/api/v5/queue/7?removalMethod=removeFromClient\
             &blocklistMethod=blocklistAndSearch&message=Import+blocked%3A+sample"
                .to_owned(),
            "GET /sonarr/api".to_owned(),
            "DELETE /sonarr/api/v5/queue/7?removalMethod=removeFromClient\
             &blocklistMethod=blocklistOnly&message=Import+blocked%3A+sample"
                .to_owned(),
            "GET /sonarr/api".to_owned(),
            "DELETE /sonarr/api/v5/queue/7?removalMethod=removeFromClient\
             &blocklistMethod=doNotBlocklist"
                .to_owned(),
            "GET /sonarr/api".to_owned(),
            "POST /sonarr/api/v5/command".to_owned(),
            "GET /sonarr/api".to_owned(),
            "GET /sonarr/api/v5/connection".to_owned(),
            "PUT /sonarr/api/v5/connection/4?skipTesting=true&skipValidation=warnings".to_owned(),
        ]
    );
}

#[tokio::test(flavor = "multi_thread")]
async fn sonarr_v4_and_older_keep_the_v3_api() {
    let http = reqwest::Client::new();
    for info in [Some(v4()), None, Some(json!({}))] {
        let (arr, log) = sonarr(info, queue_page).await;
        queue::remove(&http, &arr, 7, Some("Import blocked: sample"), true)
            .await
            .unwrap();
        let hook = Hook {
            name: "infinidysk",
            url: "http://x/hook",
            username: "u",
            password: "p",
        };
        webhook::register(&http, &arr, &hook).await.unwrap();
        assert_eq!(
            calls(&log),
            [
                "GET /sonarr/api",
                "DELETE /sonarr/api/v3/queue/7?removeFromClient=true&blocklist=true\
                 &skipRedownload=false",
                "GET /sonarr/api",
                "GET /sonarr/api/v3/notification",
                "PUT /sonarr/api/v3/notification/4?forceSave=true",
            ]
        );
    }
}

#[tokio::test(flavor = "multi_thread")]
async fn a_sonarr_without_an_api_we_speak_is_left_alone() {
    let http = reqwest::Client::new();
    let (arr, log) = sonarr(
        Some(json!({ "current": "v6", "deprecated": [] })),
        queue_page,
    )
    .await;
    let error = queue::queue(&http, &arr).await.unwrap_err();
    assert!(
        matches!(&error, Error::Unsupported(v) if v == "v6"),
        "{error}"
    );
    assert_eq!(calls(&log), ["GET /sonarr/api"]);
}

#[tokio::test(flavor = "multi_thread")]
async fn a_rejected_api_key_is_not_taken_for_an_older_sonarr() {
    let http = reqwest::Client::new();
    let (mut arr, log) = sonarr(Some(v5()), queue_page).await;
    arr.api_key = "bad".to_owned();
    let error = queue::remove(&http, &arr, 7, None, false)
        .await
        .unwrap_err();
    assert!(
        matches!(
            &error,
            Error::Rejected {
                status: reqwest::StatusCode::UNAUTHORIZED,
                ..
            }
        ),
        "{error}"
    );
    assert_eq!(calls(&log), ["GET /sonarr/api"]);
}
