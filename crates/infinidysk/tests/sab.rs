//! The SAB API as a download client drives it.
#![allow(clippy::unwrap_used)] // Test helpers outside `#[test]` fns; panics are the assertions.

use std::sync::Arc;
use std::time::Duration;

use axum::Router;
use axum::body::Body;
use axum::http::{Request, StatusCode, header};
use http_body_util::BodyExt;
use infinidysk::config::ConfigStore;
use infinidysk::sab::{Sab, router};
use infinidysk_core::{ItemId, ItemKind, JobId};
use infinidysk_db::{Db, HistoryEntry, Item, JobStatus};
use infinidysk_nntp::{Client, Downloads, Endpoint, Pool, PoolConfig, Provider, TlsMode};
use infinidysk_queue::Queue;
use serde_json::Value;
use tokio::sync::RwLock;
use tower::ServiceExt;

const NZB: &str = r#"<?xml version="1.0"?><nzb><file poster="p" date="0" subject="a.mkv"><groups><group>a.b</group></groups><segments><segment bytes="100" number="1">a@b</segment></segments></file></nzb>"#;

async fn send(app: &Router, req: Request<Body>) -> (StatusCode, Value) {
    let res = app.clone().oneshot(req).await.unwrap();
    let code = res.status();
    let body = res.into_body().collect().await.unwrap().to_bytes();
    (code, serde_json::from_slice(&body).unwrap())
}

fn get(query: &str) -> Request<Body> {
    Request::get(format!("/api?{query}&output=json"))
        .body(Body::empty())
        .unwrap()
}

/// A SAB API over a fresh database whose queue worker never runs.
async fn setup() -> (Router, Db) {
    let path = std::env::temp_dir().join(format!("infinidysk-sab-{}.sqlite", ItemId::new()));
    let db = Db::connect(&format!("sqlite:{}", path.display()))
        .await
        .unwrap();
    let env = [
        ("INFINIDYSK__SAB__API_KEY".to_owned(), "key".to_owned()),
        ("INFINIDYSK__MOUNT__DIR".to_owned(), "/mnt/x/".to_owned()),
    ];
    let config = ConfigStore::load(db.clone(), env).await.unwrap();
    // Never contacted: the queue worker is not running.
    let endpoint =
        Endpoint::new("127.0.0.1", 9, TlsMode::Plain, None, Duration::from_secs(1)).unwrap();
    let config_pool = PoolConfig {
        max_connections: 1,
        pipeline_depth: 1,
        idle_timeout: Duration::from_secs(1),
        min_connections: 0,
    };
    let pool = Pool::new(endpoint, config_pool, Downloads::new(1)).unwrap();
    let client = Client::new([Provider {
        pool,
        key: String::new(),
        name: String::new(),
        priority: 0,
        backbone: None,
    }]);
    let queue = Arc::new(Queue::new(db.clone(), Arc::new(client)));
    let sab = Sab::new(queue, Arc::new(RwLock::new(config))).unwrap();
    (router(Arc::new(sab)), db)
}

#[tokio::test]
async fn add_list_report_storage_and_delete() {
    let (app, db) = setup().await;

    let (code, body) = send(&app, get("mode=version")).await;
    assert_eq!(
        (code, body["version"].as_str()),
        (StatusCode::OK, Some("4.5.1"))
    );
    let (code, body) = send(&app, get("mode=queue&apikey=nope")).await;
    assert_eq!(code, StatusCode::UNAUTHORIZED);
    assert_eq!(body["error"], "API Key Incorrect");
    let (code, _) = send(&app, get("mode=bogus&apikey=key")).await;
    assert_eq!(code, StatusCode::BAD_REQUEST);
    let (code, body) = send(&app, get("mode=config&name=speedlimit&value=50&apikey=key")).await;
    assert_eq!(
        (code, body["status"].as_bool()),
        (StatusCode::OK, Some(true))
    );

    let multipart = format!(
        "--b\r\nContent-Disposition: form-data; name=\"nzbfile\"; filename=\"job.nzb.gz\"\r\n\r\n{NZB}\r\n--b--\r\n"
    );
    let req = Request::post("/api?mode=addfile&CAT=tv&priority=1&apikey=key")
        .header(header::CONTENT_TYPE, "multipart/form-data; boundary=b")
        .body(Body::from(multipart))
        .unwrap();
    let (code, body) = send(&app, req).await;
    assert_eq!(code, StatusCode::OK, "{body}");
    let id = body["nzo_ids"][0].as_str().unwrap().to_owned();

    let (_, body) = send(&app, get("mode=queue&cat=tv&apikey=key")).await;
    let slot = &body["queue"]["slots"][0];
    assert_eq!(body["queue"]["noofslots"], 1);
    assert_eq!(
        (
            slot["nzo_id"].as_str(),
            slot["filename"].as_str(),
            slot["priority"].as_str()
        ),
        (Some(id.as_str()), Some("job"), Some("High"))
    );

    let content = db.item(ItemId::CONTENT).await.unwrap().unwrap();
    let tv = Item::child(&content, "tv", ItemKind::Directory).unwrap();
    let dir = Item::child(&tv, "done", ItemKind::Directory).unwrap();
    let kept_dir = Item::child(&tv, "kept", ItemKind::Directory).unwrap();
    db.insert(&[tv, dir.clone(), kept_dir.clone()], &[])
        .await
        .unwrap();
    let done = HistoryEntry {
        id: JobId::new(),
        created_at: 5_000,
        file_name: "done.nzb".into(),
        job_name: "done".into(),
        category: "tv".into(),
        status: JobStatus::Completed,
        total_bytes: 10,
        download_seconds: 1,
        fail_message: None,
        download_dir: Some(dir.id),
        nzb_blob: None,
        storage: None,
    };
    let gone = HistoryEntry {
        id: JobId::new(),
        created_at: 6_000,
        download_dir: None,
        ..done.clone()
    };
    // Handed off elsewhere (an import from C#): reported as stored.
    let kept = HistoryEntry {
        id: JobId::new(),
        created_at: 4_000,
        download_dir: Some(kept_dir.id),
        storage: Some("/data/completed-downloads/tv/kept".into()),
        ..done.clone()
    };
    db.add_history(&done, &[], &[]).await.unwrap();
    db.add_history(&gone, &[], &[]).await.unwrap();
    db.add_history(&kept, &[], &[]).await.unwrap();

    let (_, body) = send(&app, get("mode=history&limit=1&apikey=key")).await;
    let slots = body["history"]["slots"].as_array().unwrap();
    assert_eq!(slots.len(), 3, "limit is ignored");
    assert_eq!(slots[0]["status"], "Failed");
    assert_eq!(
        slots[0]["fail_message"],
        "Download folder no longer exists."
    );
    assert_eq!(slots[1]["storage"], "/mnt/x/completed-symlinks/tv/done");
    assert_eq!(slots[1]["completed"], 5);
    assert_eq!(slots[2]["storage"], "/data/completed-downloads/tv/kept");

    let (_, body) = send(&app, get("mode=history&failed_only=1&apikey=key")).await;
    assert_eq!(body["history"]["noofslots"], 1);

    // A failed job is queued again from its stored NZB; a completed one is not.
    let queued = db.queue().await.unwrap().remove(0);
    let failed = HistoryEntry {
        id: queued.id,
        status: JobStatus::Failed,
        file_name: queued.file_name.clone(),
        nzb_blob: Some(queued.nzb_blob),
        download_dir: None,
        ..done.clone()
    };
    assert!(db.finish_job(&failed, &[], &[], &[], &[]).await.unwrap());
    let (code, body) = send(
        &app,
        get(&format!("mode=retry&value={id},{}&apikey=key", done.id)),
    )
    .await;
    assert_eq!(code, StatusCode::OK, "{body}");
    assert_eq!(
        body["failed"][0]["error"],
        "Only failed history items can be retried."
    );
    let id = body["nzo_id"].as_str().unwrap().to_owned();
    let requeued = db.queue().await.unwrap();
    assert_eq!(
        (requeued.len(), requeued[0].file_name.as_str()),
        (1, "job.nzb")
    );
    assert_eq!(requeued[0].id.to_string(), id);

    let req = Request::post("/api?mode=queue&name=delete&apikey=key")
        .header(header::CONTENT_TYPE, "application/x-www-form-urlencoded")
        .body(Body::from(format!("value={id}")))
        .unwrap();
    send(&app, req).await;
    let (_, body) = send(&app, get("mode=history&name=delete&value=all&apikey=key")).await;
    assert_eq!(body["status"], true);
    assert_eq!(db.queue().await.unwrap().len(), 0);
    assert_eq!(db.history().await.unwrap().len(), 0);
    assert!(
        db.item(dir.id).await.unwrap().is_some(),
        "downloads outlive history"
    );
}

#[tokio::test]
async fn addurl_follows_redirects_and_records_failures() {
    use axum::response::Redirect;
    use axum::routing::get as route;

    let indexer = Router::new()
        .route(
            "/r",
            route(|| async { Redirect::to("/get/job.nzb?apikey=secret") }),
        )
        .route(
            "/get/job.nzb",
            route(|headers: header::HeaderMap| async move {
                if headers
                    .get(header::USER_AGENT)
                    .is_some_and(|ua| ua == "SABnzbd/5.1.3")
                {
                    Ok(NZB)
                } else {
                    Err(StatusCode::FORBIDDEN)
                }
            }),
        );
    let listener = tokio::net::TcpListener::bind("127.0.0.1:0").await.unwrap();
    let base = format!("http://{}", listener.local_addr().unwrap());
    tokio::spawn(async move { axum::serve(listener, indexer).await });
    let (app, db) = setup().await;

    let (code, body) = send(&app, get(&format!("mode=addurl&name={base}/r&apikey=key"))).await;
    assert_eq!(code, StatusCode::OK, "{body}");
    let queued = db.queue().await.unwrap();
    assert_eq!((queued.len(), queued[0].file_name.as_str()), (1, "job.nzb"));
    assert_eq!(
        body["nzo_ids"][0].as_str(),
        Some(queued[0].id.to_string().as_str())
    );

    for (url, reason) in [
        (
            "http://169.254.169.254/latest".to_owned(),
            "The URL points to a link-local or cloud-metadata address.",
        ),
        (
            format!("{base}/gone"),
            "The indexer answered 404 Not Found.",
        ),
    ] {
        let (code, body) = send(&app, get(&format!("mode=addurl&name={url}&apikey=key"))).await;
        assert_eq!(
            (code, &body["status"]),
            (StatusCode::OK, &Value::Bool(true))
        );
        let id = body["nzo_ids"][0].as_str().unwrap();
        let history = db.history().await.unwrap();
        let (failed, _) = history
            .iter()
            .find(|(h, _)| h.id.to_string() == id)
            .unwrap();
        assert_eq!(
            (failed.status, failed.fail_message.as_deref()),
            (JobStatus::Failed, Some(reason))
        );
    }
}
