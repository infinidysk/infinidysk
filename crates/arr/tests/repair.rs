//! `replace` against a fake Radarr.
#![allow(clippy::unwrap_used)] // Test helpers outside `#[test]` fns; panics are the assertions.

use std::sync::{Arc, Mutex};
use std::time::{Duration, Instant};

use axum::Json;
use axum::extract::{Request, State};
use axum::http::StatusCode;
use axum::response::{IntoResponse, Response};
use infinidysk_arr::{Arr, Error, Profile, owner_of_file, replace};
use serde_json::json;
use tokio::net::TcpListener;

/// How the fake Radarr answers the grab lookup and the blocklist call.
#[derive(Clone, Copy)]
enum Grab {
    Missing,
    Blocklists,
    BlocklistFails,
}

#[derive(Default)]
struct Calls {
    log: Vec<String>,
    blocklisted: bool,
}

type Shared = (Grab, Arc<Mutex<Calls>>);

/// Answers like Radarr for movie 3 held in file 7, and logs every call. Once the
/// release is blocklisted, its own search has queued a replacement.
async fn radarr(State((grab, calls)): State<Shared>, req: Request) -> Response {
    let call = format!("{} {}", req.method(), req.uri());
    let mut calls = calls.lock().unwrap();
    calls.log.push(call.clone());
    match call.as_str() {
        "GET /api/v3/moviefile/7" => {
            Json(json!({ "id": 7, "movieId": 3, "path": "/movies/A/a.mkv" })).into_response()
        }
        "GET /api/v3/history?downloadId=job&eventType=1&pageSize=1" => match grab {
            Grab::Missing => Json(json!({ "records": [] })).into_response(),
            Grab::Blocklists | Grab::BlocklistFails => {
                Json(json!({ "records": [{ "id": 12, "data": {} }] })).into_response()
            }
        },
        "POST /api/v3/history/failed/12" => match grab {
            Grab::BlocklistFails => StatusCode::INTERNAL_SERVER_ERROR.into_response(),
            Grab::Missing | Grab::Blocklists => {
                calls.blocklisted = true;
                Json(json!({})).into_response()
            }
        },
        "GET /api/v3/queue/details?movieId=3" if calls.blocklisted => {
            Json(json!([{ "downloadId": "other" }])).into_response()
        }
        "GET /api/v3/queue/details?movieId=3" => Json(json!([])).into_response(),
        _ => Json(json!({})).into_response(),
    }
}

/// Runs `replace` for download `job` with `wait` against a fake Radarr; returns its
/// result, the calls it made and how long it took.
async fn repair(grab: Grab, wait: Duration) -> (Result<(), Error>, Vec<String>, Duration) {
    let calls = Arc::new(Mutex::new(Calls::default()));
    let listener = TcpListener::bind("127.0.0.1:0").await.unwrap();
    let url = format!("http://{}", listener.local_addr().unwrap());
    let app = axum::Router::new()
        .fallback(radarr)
        .with_state((grab, Arc::clone(&calls)));
    tokio::spawn(async move { axum::serve(listener, app).await });
    let arr = Arr {
        id: uuid::Uuid::nil(),
        name: "radarr".to_owned(),
        profile: Profile::Radarr,
        url,
        api_key: "k".to_owned(),
    };
    let http = reqwest::Client::new();
    let owner = owner_of_file(&http, &arr, 7).await.unwrap().unwrap();
    let start = Instant::now();
    let result = tokio::time::timeout(
        Duration::from_secs(5),
        replace(&http, &arr, Some("job"), &owner, wait),
    )
    .await
    .expect("replace finished within 5 s");
    let elapsed = start.elapsed();
    let log = calls.lock().unwrap().log.clone();
    (result, log, elapsed)
}

#[tokio::test(flavor = "multi_thread")]
async fn without_a_grab_it_searches_at_once() {
    let (result, calls, _) = repair(Grab::Missing, Duration::from_secs(60)).await;
    result.unwrap();
    assert_eq!(
        calls,
        [
            "GET /api/v3/moviefile/7",
            "GET /api/v3/queue/details?movieId=3",
            "GET /api/v3/history?downloadId=job&eventType=1&pageSize=1",
            "DELETE /api/v3/moviefile/7",
            "POST /api/v3/command",
        ]
    );
}

#[tokio::test(flavor = "multi_thread")]
async fn after_a_blocklist_it_waits_for_the_arr_search() {
    let wait = Duration::from_secs(1);
    let (result, calls, elapsed) = repair(Grab::Blocklists, wait).await;
    result.unwrap();
    assert!(elapsed >= wait, "returned after {elapsed:?}");
    assert_eq!(
        calls,
        [
            "GET /api/v3/moviefile/7",
            "GET /api/v3/queue/details?movieId=3",
            "GET /api/v3/history?downloadId=job&eventType=1&pageSize=1",
            "POST /api/v3/history/failed/12",
            "GET /api/v3/queue/details?movieId=3",
        ]
    );
}

#[tokio::test(flavor = "multi_thread")]
async fn a_failed_blocklist_neither_deletes_nor_searches() {
    let (result, calls, _) = repair(Grab::BlocklistFails, Duration::from_secs(60)).await;
    assert!(
        matches!(result, Err(Error::Rejected { status, .. }) if status == 500),
        "{result:?}"
    );
    assert_eq!(
        calls,
        [
            "GET /api/v3/moviefile/7",
            "GET /api/v3/queue/details?movieId=3",
            "GET /api/v3/history?downloadId=job&eventType=1&pageSize=1",
            "POST /api/v3/history/failed/12",
        ]
    );
}
