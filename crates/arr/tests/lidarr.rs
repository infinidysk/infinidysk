//! Lidarr settings we read and never change.
#![allow(clippy::unwrap_used)] // Test helpers outside `#[test]` fns; panics are the assertions.

use std::sync::{Arc, Mutex};

use axum::Json;
use axum::extract::Request;
use infinidysk_arr::{Arr, Profile, writes_audio_tags};
use serde_json::json;
use tokio::net::TcpListener;

/// An Arr whose metadata settings write tags as `mode`; logs each call.
async fn arr(profile: Profile, mode: &'static str) -> (Arr, Arc<Mutex<Vec<String>>>) {
    let log: Arc<Mutex<Vec<String>>> = Arc::default();
    let listener = TcpListener::bind("127.0.0.1:0").await.unwrap();
    let url = format!("http://{}", listener.local_addr().unwrap());
    let handler = {
        let log = Arc::clone(&log);
        move |req: Request| {
            log.lock()
                .unwrap()
                .push(format!("{} {}", req.method(), req.uri()));
            async move {
                Json(json!({
                    "metadataSource": "", "writeAudioTags": mode,
                    "scrubAudioTags": false, "embedCoverArt": true, "id": 1,
                }))
            }
        }
    };
    tokio::spawn(async move { axum::serve(listener, axum::Router::new().fallback(handler)).await });
    let arr = Arr {
        id: uuid::Uuid::nil(),
        name: "lidarr".to_owned(),
        profile,
        url,
        api_key: "k".to_owned(),
    };
    (arr, log)
}

#[tokio::test(flavor = "multi_thread")]
async fn only_a_lidarr_that_tags_files_is_reported() {
    let http = reqwest::Client::new();
    for (mode, tags) in [
        ("no", false),
        ("newFiles", true),
        ("allFiles", true),
        ("sync", true),
    ] {
        let (lidarr, log) = arr(Profile::Lidarr, mode).await;
        assert_eq!(
            writes_audio_tags(&http, &lidarr).await.unwrap(),
            tags,
            "{mode}"
        );
        assert_eq!(
            *log.lock().unwrap(),
            ["GET /api/v1/config/metadataprovider"]
        );
    }
    let (sonarr, log) = arr(Profile::Sonarr, "allFiles").await;
    assert!(!writes_audio_tags(&http, &sonarr).await.unwrap());
    assert!(log.lock().unwrap().is_empty());
}
