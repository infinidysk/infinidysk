//! SAB API listing benchmarks over a seeded database. Scenario and metric names match the
//! original suite's `sab-api` report: 50 queued jobs, 500 completed jobs (50 `tv`).
//!
//! `cargo run -p infinidysk-bench --bin sab-api --release -- [--json <path>]`

use std::error::Error;
use std::fmt::Write as _;
use std::sync::Arc;
use std::time::Instant;

use axum::Router;
use axum::body::Body;
use axum::http::Request;
use http_body_util::BodyExt;
use infinidysk::config::ConfigStore;
use infinidysk::sab::{Sab, router};
use infinidysk_core::{BlobId, ItemId, ItemKind, JobId};
use infinidysk_db::{Db, HistoryEntry, Item, JobStatus, QueueEntry};
use infinidysk_nntp::Client;
use infinidysk_queue::Queue;
use serde_json::Value;
use tokio::sync::RwLock;
use tower::ServiceExt;

const QUEUE: usize = 50;
const HISTORY: usize = 500;
const TV_HISTORY: usize = 50;

// History ignores `limit` (download clients ask for short pages and lose track of older
// jobs), so `pageSize` carries the original's history `limit`.
// ponytail: no `cpuSeconds`/`dbCommands`; std has no process CPU clock and sqlx no command
// counter. Add if a regression needs them.
const SCENARIOS: &[(&str, &str, &str)] = &[
    ("queue-limit10", "queue", "limit=10"),
    ("queue-limit50", "queue", "limit=50"),
    ("queue-limit0", "queue", "limit=0"),
    ("history-limit10-start0", "history", "pageSize=10&start=0"),
    (
        "history-limit10-start490",
        "history",
        "pageSize=10&start=490",
    ),
    ("history-limit0", "history", "pageSize=0"),
    ("history-category-filter", "history", "pageSize=10&cat=tv"),
];

fn main() -> Result<(), Box<dyn Error>> {
    let json_path = match std::env::args().skip(1).collect::<Vec<_>>().as_slice() {
        [] => None,
        [flag, path] if flag == "--json" => Some(path.clone()),
        _ => return Err("usage: [--json <path>]".into()),
    };
    let rt = tokio::runtime::Builder::new_current_thread()
        .enable_all()
        .build()?;
    let path = std::env::temp_dir().join(format!("infinidysk-sab-bench-{}.sqlite", ItemId::new()));
    let result = rt.block_on(run(&format!("sqlite:{}", path.display()), json_path));
    for suffix in ["", "-wal", "-shm"] {
        let _ = std::fs::remove_file(format!("{}{suffix}", path.display()));
    }
    result
}

async fn run(url: &str, json_path: Option<String>) -> Result<(), Box<dyn Error>> {
    let db = Db::connect(url).await?;
    seed(&db).await?;
    let vars = [("INFINIDYSK__SAB__API_KEY".to_owned(), "key".to_owned())];
    let config = ConfigStore::load(db.clone(), vars).await?;
    let queue = Arc::new(Queue::new(db, Arc::new(Client::new(Vec::new()))));
    let sab = Sab::new(queue, Arc::new(RwLock::new(config))).expect("the fetcher builds");
    let app = router(Arc::new(sab));

    let mut json = String::new();
    for (name, mode, query) in SCENARIOS {
        let started = Instant::now();
        let body = get(&app, &format!("/api?mode={mode}&{query}&apikey=key")).await?;
        let elapsed_ms = started.elapsed().as_secs_f64() * 1000.0;
        let rows = body[mode]["slots"].as_array().map_or(0, Vec::len);
        let total = body[mode]["noofslots"].as_u64().unwrap_or_default();
        println!("{name} rows={rows} total={total} elapsed_ms={elapsed_ms:.3}");
        if !json.is_empty() {
            json.push(',');
        }
        write!(
            json,
            r#""{name}":{{"deterministic":{{"rowsReturned":{rows},"totalCount":{total}}},"timing":{{"elapsedMs":{elapsed_ms:.3}}}}}"#
        )?;
    }
    if let Some(path) = json_path {
        let report = format!(r#"{{"schemaVersion":1,"report":"sab-api","scenarios":{{{json}}}}}"#);
        std::fs::write(path, report + "\n")?;
    }
    Ok(())
}

async fn get(app: &Router, uri: &str) -> Result<Value, Box<dyn Error>> {
    let response = app
        .clone()
        .oneshot(Request::get(uri).body(Body::empty())?)
        .await?;
    let body = response.into_body().collect().await?.to_bytes();
    Ok(serde_json::from_slice(&body)?)
}

async fn seed(db: &Db) -> Result<(), Box<dyn Error>> {
    for i in 0..QUEUE {
        let job = QueueEntry {
            id: JobId::new(),
            created_at: i64::try_from(i)?,
            file_name: format!("queued-{i}.nzb"),
            job_name: format!("queued-{i}"),
            category: "tv".to_owned(),
            priority: 0,
            post_processing: 0,
            nzb_blob: BlobId::new(),
            nzb_size: 1024,
            total_bytes: 1 << 30,
            paused: false,
            retry_at: None,
            attempts: 0,
            stuck_count: 0,
            last_error: None,
            content_hash: None,
        };
        db.enqueue(&job, b"nzb").await?;
    }
    let content = db
        .item(ItemId::CONTENT)
        .await?
        .ok_or("/content is missing")?;
    let tv = Item::child(&content, "tv", ItemKind::Directory)?;
    let movies = Item::child(&content, "movies", ItemKind::Directory)?;
    db.insert(&[tv.clone(), movies.clone()], &[]).await?;
    for i in 0..HISTORY {
        let (parent, category) = if i < TV_HISTORY {
            (&tv, "tv")
        } else {
            (&movies, "movies")
        };
        let dir = Item::child(parent, &format!("done-{i}"), ItemKind::Directory)?;
        let job = HistoryEntry {
            id: JobId::new(),
            created_at: i64::try_from(i)?,
            file_name: format!("done-{i}.nzb"),
            job_name: format!("done-{i}"),
            category: category.to_owned(),
            status: JobStatus::Completed,
            total_bytes: 1 << 30,
            download_seconds: 1,
            fail_message: None,
            download_dir: Some(dir.id),
            nzb_blob: None,
            storage: None,
        };
        db.add_history(&job, &[dir], &[]).await?;
    }
    Ok(())
}
