//! A healthy file is rescheduled; a file with a missing article is confirmed twice,
//! replaced through Sonarr, hidden until the replacement lands, and its release is refused
//! on re-import.
#![allow(clippy::unwrap_used)] // Test helpers outside `#[test]` fns; panics are the assertions.

use std::fmt::Write as _;
use std::future::Future;
use std::pin::Pin;
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::{Arc, Mutex};
use std::time::Duration;

use axum::Json;
use axum::extract::{Request, State};
use infinidysk_core::{ItemId, JobId};
use infinidysk_db::{Db, Defect, HealthResult, HealthState, Item, JobStatus, Recheck, Repair};
use infinidysk_health::{Arr, Health, Profile, Settings, Unverified};
use infinidysk_nntp::{Articles, Client, Downloads, Endpoint, Pool, PoolConfig, Provider, TlsMode};
use infinidysk_queue::import::{self, Output, Verifier};
use infinidysk_queue::{Queue, Submission};
use infinidysk_testkit::{Article, LoopbackServer, post};
use serde_json::{Value, json};
use tokio::net::TcpListener;
use tokio::sync::watch;

const ARTICLE: usize = 16 << 10;

fn client(server: &LoopbackServer) -> Arc<Client> {
    let endpoint = Endpoint::new(
        "127.0.0.1",
        server.addr().port(),
        TlsMode::Plain,
        None,
        Duration::from_secs(5),
    )
    .unwrap();
    let config = PoolConfig {
        max_connections: 4,
        pipeline_depth: 4,
        idle_timeout: Duration::from_secs(5),
        min_connections: 0,
    };
    let pool = Pool::new(endpoint, config, Downloads::new(32)).unwrap();
    Arc::new(Client::new([Provider {
        pool,
        key: String::new(),
        name: String::new(),
        priority: 0,
        backbone: None,
    }]))
}

fn nzb(articles: &[Article], len: usize, name: &str) -> Vec<u8> {
    let mut xml = format!(
        "<?xml version=\"1.0\"?><nzb><file poster=\"p\" date=\"0\" subject=\"{name}\">\
         <groups><group>a.b</group></groups><segments>",
    );
    for (i, a) in articles.iter().enumerate() {
        let payload = ARTICLE.min(len - i * ARTICLE);
        let bytes = payload + payload / 50 + 1;
        let id = &a.message_id;
        write!(
            xml,
            "<segment bytes=\"{bytes}\" number=\"{}\">{id}</segment>",
            i + 1
        )
        .unwrap();
    }
    xml.push_str("</segments></file></nzb>");
    xml.into_bytes()
}

type Log = Arc<(Mutex<Vec<String>>, AtomicBool)>;

#[tokio::test]
async fn pending_par2_repair_defers_arr_replacement_and_releases_write_admission() {
    let (articles, len) = mkv("par2-health");
    let server = LoopbackServer::start(articles.clone()).await.unwrap();
    let (db, item, _) = import(&server, &articles, len).await;
    let (_arr_tx, arr) = watch::channel(Vec::new());
    let (_settings_tx, settings) = watch::channel(Settings::default());
    let health = Health::new(db.clone(), client(&server), arr, settings)
        .unwrap()
        .with_par2();
    let (_, mut status) = db.health(item.id).await.unwrap().unwrap();
    status.state = HealthState::Repairing;
    status.next_check = Some(0);
    db.set_health(item.id, &status, Recheck::None)
        .await
        .unwrap();
    assert!(health.step().await.unwrap());
    let (_, status) = db.health(item.id).await.unwrap().unwrap();
    assert_eq!(status.state, HealthState::Repairing);
    assert_eq!(
        db.par2_result(infinidysk_db::repair::Owner::Item(item.id), false)
            .await
            .unwrap()
            .unwrap()
            .state,
        0
    );
    let _paused = tokio::time::timeout(Duration::from_secs(1), db.pause_writes())
        .await
        .unwrap();
}

/// A 100 KB MKV posted under `prefix`.
fn mkv(prefix: &str) -> (Vec<Article>, usize) {
    let mut data: Vec<u8> = (0..100_000u32)
        .map(|i| u8::try_from(i % 251).unwrap())
        .collect();
    data[..4].copy_from_slice(&[0x1A, 0x45, 0xDF, 0xA3]);
    (post(prefix, "a.mkv", &data, ARTICLE), data.len())
}

/// A fresh database holding `articles` imported as `/content/tv/job/job.mkv`.
async fn import(server: &LoopbackServer, articles: &[Article], len: usize) -> (Db, Item, JobId) {
    import_at(server, articles, len, "a.mkv", 5).await
}

type Checked<'a> = Pin<
    Box<dyn Future<Output = Result<Option<infinidysk_db::FirstCheck>, import::Error>> + Send + 'a>,
>;

/// Runs the first health check of a 100 % import, as the app does.
struct FirstCheck;

impl Verifier for FirstCheck {
    fn verify<'a>(
        &'a self,
        articles: &'a dyn Articles,
        output: &'a Output,
        posted: Option<i64>,
    ) -> Checked<'a> {
        Box::pin(async move {
            let name = output.path.last().unwrap();
            infinidysk_health::first_check(articles, &output.meta, name, posted, true)
                .await
                .map_err(|error| match error {
                    Unverified::Damaged(message) => import::Error::Missing(message),
                    Unverified::Inconclusive(error) => import::Error::Retry(error),
                })
        })
    }
}

/// A fresh database holding `articles` of file `name` imported at a `percent` sample as
/// `/content/tv/job/job.<ext>`.
async fn import_at(
    server: &LoopbackServer,
    articles: &[Article],
    len: usize,
    name: &str,
    percent: u8,
) -> (Db, Item, JobId) {
    let path = std::env::temp_dir().join(format!("infinidysk-health-{}.sqlite", ItemId::new()));
    let db = Db::connect(&format!("sqlite:{}", path.display()))
        .await
        .unwrap();
    let queue = Queue::new(db.clone(), client(server));
    queue.set_verifier(Arc::new(FirstCheck));
    queue.update_rules(|r| r.check_percent = percent);
    let job = Submission {
        file_name: "job.nzb",
        category: "tv",
        priority: 0,
        post_processing: 0,
    };
    let job = queue
        .enqueue(&job, &nzb(articles, len, name))
        .await
        .unwrap();
    assert!(queue.process_next().await);
    let ext = name.rsplit('.').next().unwrap();
    let item = db
        .item_by_path(&format!("/content/tv/job/job.{ext}"))
        .await
        .unwrap()
        .unwrap();
    (db, item, job)
}

#[tokio::test]
async fn a_full_import_is_the_first_health_check() {
    let (articles, len) = mkv("first");
    let server = LoopbackServer::start(articles.clone()).await.unwrap();
    let (db, item, _) = import_at(&server, &articles, len, "a.mkv", 5).await;
    let (_, status) = db.health(item.id).await.unwrap().unwrap();
    assert_eq!(
        status.state,
        HealthState::Unchecked,
        "a sample proves little"
    );
    assert_eq!(db.item_checks(item.id, 5).await.unwrap(), []);

    let (db, item, _) = import_at(&server, &articles, len, "a.mkv", 100).await;
    let (_, status) = db.health(item.id).await.unwrap().unwrap();
    assert_eq!(status.state, HealthState::Healthy);
    assert!(status.last_check.is_some() && status.next_check > status.last_check);
    let checks = db.item_checks(item.id, 5).await.unwrap();
    assert_eq!(checks.len(), 1);
    assert_eq!(checks[0].result, HealthResult::Healthy);
    assert!(
        db.media_probe(item.id)
            .await
            .unwrap()
            .requested_at
            .is_some()
    );
}

/// A running [`sonarr`], as the only Arr instance.
async fn fake_sonarr() -> (Log, watch::Receiver<Vec<Arr>>) {
    let log: Log = Arc::default();
    let listener = TcpListener::bind("127.0.0.1:0").await.unwrap();
    let url = format!("http://{}", listener.local_addr().unwrap());
    let app = axum::Router::new()
        .fallback(sonarr)
        .with_state(Arc::clone(&log));
    tokio::spawn(async move { axum::serve(listener, app).await });
    let (_tx, arr) = watch::channel(vec![Arr {
        id: uuid::Uuid::nil(),
        name: "sonarr".to_owned(),
        profile: Profile::Sonarr,
        url,
        api_key: "k".to_owned(),
    }]);
    (log, arr)
}

/// Answers like Sonarr for one episode imported from a handed-off STRM and logs every
/// call; its file has moved since import. Once the episode file is deleted, a
/// replacement (file 8) shows up.
async fn sonarr(State(state): State<Log>, req: Request) -> Json<Value> {
    let call = format!("{} {}", req.method(), req.uri());
    let event = req
        .uri()
        .query()
        .unwrap_or_default()
        .contains("eventType=3");
    state.0.lock().unwrap().push(call.clone());
    if call == "DELETE /api/v3/episodefile/7" {
        state.1.store(true, Ordering::Relaxed);
    }
    let replaced = state.1.load(Ordering::Relaxed);
    Json(match req.uri().path() {
        "/api" => json!({ "current": "v3", "deprecated": [] }),
        "/api/v3/history" if event => json!({ "records": [{
            "id": 21, "episodeId": 5,
            "data": { "droppedPath": "/data/strm/tv/job/job.strm", "importedPath": "/tv/Show/a.strm" },
        }]}),
        "/api/v3/history" => json!({ "records": [{ "id": 11, "episodeId": 5, "data": {} }] }),
        "/api/v3/episode/5" => json!({ "episodeFileId": if replaced { 8 } else { 7 } }),
        "/api/v3/episode" => json!([{ "id": 5 }]),
        "/api/v3/episodefile/7" => json!({ "id": 7, "path": "/tv/Show/S01E01.strm" }),
        "/api/v3/episodefile/8" => json!({ "id": 8, "path": "/tv/Show/b.mkv" }),
        "/api/v3/queue/details" => json!([]),
        _ => json!({}),
    })
}

#[tokio::test(flavor = "multi_thread")]
#[allow(clippy::significant_drop_tightening)] // False positive: `Arc`s moved into `Queue` and `Health`.
async fn missing_article_is_replaced_through_sonarr() {
    let mut data: Vec<u8> = (0..100_000u32)
        .map(|i| u8::try_from(i % 251).unwrap())
        .collect();
    data[..4].copy_from_slice(&[0x1A, 0x45, 0xDF, 0xA3]);
    let articles = post("a", "a.mkv", &data, ARTICLE);
    let full = LoopbackServer::start(articles.clone()).await.unwrap();
    let path = std::env::temp_dir().join(format!("infinidysk-health-{}.sqlite", ItemId::new()));
    let db = Db::connect(&format!("sqlite:{}", path.display()))
        .await
        .unwrap();
    let queue = Queue::new(db.clone(), client(&full));
    let job = Submission {
        file_name: "job.nzb",
        category: "tv",
        priority: 0,
        post_processing: 0,
    };
    let job = queue
        .enqueue(&job, &nzb(&articles, data.len(), "a.mkv"))
        .await
        .unwrap();
    assert!(queue.process_next().await);
    assert_eq!(
        db.history_entry(job).await.unwrap().unwrap().status,
        JobStatus::Completed
    );
    let item = db
        .item_by_path("/content/tv/job/job.mkv")
        .await
        .unwrap()
        .unwrap();
    // The webhook recorded file 7; a rename since is not a replacement.
    db.record_owner(
        uuid::Uuid::nil(),
        item.id,
        Some(7),
        "/tv/Show/S01E01.strm",
        Some(job),
    )
    .await
    .unwrap();

    let log: Log = Arc::default();
    let listener = TcpListener::bind("127.0.0.1:0").await.unwrap();
    let url = format!("http://{}", listener.local_addr().unwrap());
    let app = axum::Router::new()
        .fallback(sonarr)
        .with_state(Arc::clone(&log));
    tokio::spawn(async move { axum::serve(listener, app).await });
    let (_tx, arr) = watch::channel(vec![Arr {
        id: uuid::Uuid::nil(),
        name: "sonarr".to_owned(),
        profile: Profile::Sonarr,
        url,
        api_key: "k".to_owned(),
    }]);
    let (_settings_tx, settings) = watch::channel(Settings::default());

    let health = Health::new(db.clone(), client(&full), arr.clone(), settings.clone()).unwrap();
    assert!(health.step().await.unwrap());
    assert!(
        !health.step().await.unwrap(),
        "rescheduled after a healthy check"
    );
    let state = |db: Db| async move { db.health(item.id).await.unwrap().map(|(_, s)| s.state) };
    assert_eq!(state(db.clone()).await, Some(HealthState::Healthy));

    // Segment 1 is in every sample; 16 KB of 100 KB is too much to play through.
    let mut gone = articles.clone();
    gone.remove(1);
    let partial = LoopbackServer::start(gone).await.unwrap();
    let health = Health::new(db.clone(), client(&partial), arr, settings)
        .unwrap()
        .with_arr_wait(Duration::ZERO);
    assert!(health.request_check(item.id).await.unwrap());
    assert!(health.step().await.unwrap());
    assert_eq!(state(db.clone()).await, Some(HealthState::Suspect));
    assert!(!health.step().await.unwrap(), "the confirming check waits");
    assert!(log.0.lock().unwrap().is_empty(), "one miss changes nothing");
    // Health finds the owner from the download folder, not the SAB history row.
    assert_eq!(db.delete_history(&[job]).await.unwrap(), 1);

    // Playback hitting the hole rechecks at once, but confirms only 15 minutes after the
    // first miss.
    health.report(item.id, &[1], &[]).await.unwrap();
    assert!(health.step().await.unwrap());
    assert_eq!(state(db.clone()).await, Some(HealthState::Suspect));
    let (_, mut status) = db.health(item.id).await.unwrap().unwrap();
    status.suspect_since = Some(status.suspect_since.unwrap() - 15 * 60_000);
    status.next_check = Some(0);
    db.set_health(item.id, &status, Recheck::None)
        .await
        .unwrap();
    assert!(health.step().await.unwrap());
    assert_eq!(state(db.clone()).await, Some(HealthState::Repairing));
    assert!(health.step().await.unwrap());
    assert_eq!(state(db.clone()).await, Some(HealthState::Hidden));
    assert_eq!(
        log.0.lock().unwrap().clone(),
        [
            "GET /api".to_owned(),
            format!("GET /api/v3/history?downloadId={job}&eventType=3&pageSize=1000"),
            "GET /api/v3/episode/5".to_owned(),
            "GET /api/v3/episodefile/7".to_owned(),
            "GET /api".to_owned(),
            "GET /api/v3/queue/details?episodeIds=5".to_owned(),
            format!("GET /api/v3/history?downloadId={job}&eventType=1&pageSize=1"),
            "POST /api/v3/history/failed/11".to_owned(),
            "DELETE /api/v3/episodefile/7".to_owned(),
            "POST /api/v3/command".to_owned(),
        ]
    );

    // Hourly reconciliation sees the replacement and drops the broken file.
    let (_, mut status) = db.health(item.id).await.unwrap().unwrap();
    status.next_check = Some(0);
    db.set_health(item.id, &status, Recheck::None)
        .await
        .unwrap();
    assert!(health.step().await.unwrap());
    assert!(db.item(item.id).await.unwrap().is_none());

    let results: Vec<_> = db
        .health_checks(infinidysk_db::HealthFilter::default(), 10)
        .await
        .unwrap()
        .into_iter()
        .map(|(c, _)| (c.result, c.repair))
        .collect();
    assert_eq!(
        results,
        [
            (HealthResult::Missing, Repair::Replaced),
            (HealthResult::Missing, Repair::ReplacementRequested),
            (HealthResult::Missing, Repair::None),
            (HealthResult::Missing, Repair::None),
            (HealthResult::Healthy, Repair::None),
        ]
    );

    // The same articles under another name are refused at once, never queued.
    let copy = Submission {
        file_name: "copy.nzb",
        category: "tv",
        priority: 0,
        post_processing: 0,
    };
    let copy = queue
        .enqueue(&copy, &nzb(&articles, data.len(), "a.mkv"))
        .await
        .unwrap();
    assert!(!queue.process_next().await);
    assert_eq!(
        db.history_entry(copy).await.unwrap().unwrap().status,
        JobStatus::Failed
    );
}

#[tokio::test(flavor = "multi_thread")]
async fn unreadable_file_does_not_block_the_next() {
    let mut data: Vec<u8> = (0..40_000u32)
        .map(|i| u8::try_from(i % 251).unwrap())
        .collect();
    data[..4].copy_from_slice(&[0x1A, 0x45, 0xDF, 0xA3]);
    let (bad, good) = (
        post("c", "c.mkv", &data, ARTICLE),
        post("d", "d.mkv", &data, ARTICLE),
    );
    let server = LoopbackServer::start([bad.clone(), good.clone()].concat())
        .await
        .unwrap();
    let path = std::env::temp_dir().join(format!("infinidysk-health-{}.sqlite", ItemId::new()));
    let db = Db::connect(&format!("sqlite:{}", path.display()))
        .await
        .unwrap();
    let queue = Queue::new(db.clone(), client(&server));
    for (name, articles) in [("bad", &bad), ("good", &good)] {
        let job = Submission {
            file_name: &format!("{name}.nzb"),
            category: "tv",
            priority: 0,
            post_processing: 0,
        };
        queue
            .enqueue(&job, &nzb(articles, data.len(), "x.mkv"))
            .await
            .unwrap();
        assert!(queue.process_next().await);
    }
    drop(queue);
    let item = |name: &'static str| {
        let db = db.clone();
        async move {
            db.item_by_path(&format!("/content/tv/{name}/{name}.mkv"))
                .await
                .unwrap()
                .unwrap()
        }
    };
    let (bad, good) = (item("bad").await, item("good").await);
    db.update_blob(bad.meta_blob.unwrap(), b"not a blob")
        .await
        .unwrap();
    db.request_recheck(bad.id, Recheck::Playback).await.unwrap();
    let (_tx, arr) = watch::channel(Vec::new());
    let (_settings_tx, settings) = watch::channel(Settings::default());
    let health = Health::new(db.clone(), client(&server), arr, settings).unwrap();

    assert!(health.step().await.unwrap());
    assert!(health.step().await.unwrap());
    let status = |id| {
        let db = db.clone();
        async move { db.health(id).await.unwrap().unwrap().1 }
    };
    let bad = status(bad.id).await;
    assert_eq!(bad.state, HealthState::Unchecked);
    assert!(bad.next_check.unwrap() > infinidysk_db::now_ms());
    assert_eq!(status(good.id).await.state, HealthState::Healthy);
}

#[tokio::test(flavor = "multi_thread")]
async fn playback_misses_a_minute_apart_do_not_repair() {
    let (articles, len) = mkv("e");
    let full = LoopbackServer::start(articles.clone()).await.unwrap();
    let (db, item, _) = import(&full, &articles, len).await;
    let mut gone = articles;
    gone.remove(1);
    let partial = LoopbackServer::start(gone).await.unwrap();
    let (_tx, arr) = watch::channel(Vec::new());
    let (_settings_tx, settings) = watch::channel(Settings::default());
    let health = Health::new(db.clone(), client(&partial), arr, settings).unwrap();

    health.report(item.id, &[1], &[]).await.unwrap();
    let (_, mut status) = db.health(item.id).await.unwrap().unwrap();
    let first = status.suspect_since.unwrap() - 60_000;
    status.suspect_since = Some(first);
    db.set_health(item.id, &status, Recheck::None)
        .await
        .unwrap();
    health.report(item.id, &[1], &[]).await.unwrap();
    assert!(health.step().await.unwrap());
    let (_, status) = db.health(item.id).await.unwrap().unwrap();
    assert_eq!(status.state, HealthState::Suspect);
    assert_eq!(status.next_check, Some(first + 15 * 60_000));
}

#[tokio::test(flavor = "multi_thread")]
async fn a_fallback_id_does_not_hide_a_hole_playback_hits() {
    let (articles, len) = mkv("g");
    let server = LoopbackServer::start(articles.clone()).await.unwrap();
    let (db, item, _) = import(&server, &articles, len).await;
    let blob = item.meta_blob.unwrap();
    let mut meta =
        infinidysk_core::blob::decode_meta(&db.blob(blob).await.unwrap().unwrap()).unwrap();
    let infinidysk_core::FileMeta::Nzb(f) = &mut meta else {
        panic!("a plain file")
    };
    // Segment 1 is in every sample.
    let segment = &mut f.segments.items[1];
    segment.fallbacks = vec![std::mem::replace(&mut segment.id, "absent@x".to_owned())];
    db.update_blob(blob, &infinidysk_core::blob::encode_meta(&meta).unwrap())
        .await
        .unwrap();
    let (_tx, arr) = watch::channel(Vec::new());
    let (_settings_tx, settings) = watch::channel(Settings::default());
    let health = Health::new(db.clone(), client(&server), arr, settings).unwrap();

    assert!(health.request_check(item.id).await.unwrap());
    assert!(health.step().await.unwrap());
    let (_, status) = db.health(item.id).await.unwrap().unwrap();
    assert_eq!(status.state, HealthState::Suspect);
}

#[tokio::test(flavor = "multi_thread")]
async fn a_wrong_part_playback_rejects_stays_corrupt() {
    let (articles, len) = mkv("h");
    let full = LoopbackServer::start(articles.clone()).await.unwrap();
    let (db, item, _) = import(&full, &articles, len).await;
    let blob = item.meta_blob.unwrap();
    let meta = infinidysk_core::blob::decode_meta(&db.blob(blob).await.unwrap().unwrap()).unwrap();
    let infinidysk_core::FileMeta::Nzb(f) = &meta else {
        panic!("a plain file")
    };
    assert!(f.segments.sizes_trusted);
    // Segment 1's id serves segment 2's article: same size, decodes cleanly.
    let mut wrong = articles.clone();
    wrong[1] = Article {
        message_id: articles[1].message_id.clone(),
        ..articles[2].clone()
    };
    let wrong = LoopbackServer::start(wrong).await.unwrap();
    let (_tx, arr) = watch::channel(Vec::new());
    let (_settings_tx, settings) = watch::channel(Settings::default());
    let health = Health::new(db.clone(), client(&wrong), arr, settings).unwrap();

    health.report(item.id, &[], &[1]).await.unwrap();
    assert!(health.step().await.unwrap());
    let (_, status) = db.health(item.id).await.unwrap().unwrap();
    assert_eq!(status.state, HealthState::Suspect);
    let meta = infinidysk_core::blob::decode_meta(&db.blob(blob).await.unwrap().unwrap()).unwrap();
    let infinidysk_core::FileMeta::Nzb(f) = &meta else {
        panic!("a plain file")
    };
    assert_eq!(f.segments.corrupt, [1]);
}

#[tokio::test(flavor = "multi_thread")]
async fn open_file_is_not_repaired() {
    let (articles, len) = mkv("f");
    let server = LoopbackServer::start(articles.clone()).await.unwrap();
    let (db, item, _) = import(&server, &articles, len).await;
    let (log, arr) = fake_sonarr().await;
    let (_settings_tx, settings) = watch::channel(Settings::default());
    let health = Health::new(db.clone(), client(&server), arr, settings)
        .unwrap()
        .with_in_use(move |id| id == item.id);

    assert!(health.request_repair(item.id).await.unwrap());
    assert!(health.step().await.unwrap());
    let (_, status) = db.health(item.id).await.unwrap().unwrap();
    assert_eq!(status.state, HealthState::Repairing);
    assert!(status.next_check.unwrap() > infinidysk_db::now_ms());
    assert!(log.0.lock().unwrap().is_empty(), "no Arr call");
}

#[tokio::test(flavor = "multi_thread")]
async fn carried_over_file_is_repaired_through_its_recorded_download() {
    let (articles, len) = mkv("g");
    let server = LoopbackServer::start(articles.clone()).await.unwrap();
    let (source, item, job) = import(&server, &articles, len).await;
    // A copy into a fresh install, where the download folder has no job record.
    let path = std::env::temp_dir().join(format!("infinidysk-health-{}.sqlite", ItemId::new()));
    let db = Db::connect(&format!("sqlite:{}", path.display()))
        .await
        .unwrap();
    let mut items = Vec::new();
    for p in ["/content/tv", "/content/tv/job", "/content/tv/job/job.mkv"] {
        items.push(source.item_by_path(p).await.unwrap().unwrap());
    }
    let mut blobs = Vec::new();
    for id in items
        .iter()
        .flat_map(|i| [i.meta_blob, i.nzb_blob])
        .flatten()
    {
        if !blobs.iter().any(|(b, _)| *b == id) {
            blobs.push((id, source.blob(id).await.unwrap().unwrap()));
        }
    }
    let blobs: Vec<_> = blobs.iter().map(|(id, b)| (*id, b.as_slice())).collect();
    db.insert(&items, &blobs).await.unwrap();
    assert_eq!(db.job_at("/content/tv/job").await.unwrap(), None);
    db.record_owner(
        uuid::Uuid::nil(),
        item.id,
        Some(7),
        "/tv/Show/S01E01.strm",
        Some(job),
    )
    .await
    .unwrap();
    let (log, arr) = fake_sonarr().await;
    let (_settings_tx, settings) = watch::channel(Settings::default());
    let health = Health::new(db.clone(), client(&server), arr, settings)
        .unwrap()
        .with_arr_wait(Duration::ZERO);

    assert!(health.request_repair(item.id).await.unwrap());
    assert!(health.step().await.unwrap());
    let (_, status) = db.health(item.id).await.unwrap().unwrap();
    assert_eq!(status.state, HealthState::Hidden);
    assert_eq!(
        log.0.lock().unwrap()[1],
        format!("GET /api/v3/history?downloadId={job}&eventType=3&pageSize=1000")
    );
}

#[tokio::test(flavor = "multi_thread")]
async fn carried_over_file_without_a_download_is_replaced_by_search() {
    let (articles, len) = mkv("g");
    let server = LoopbackServer::start(articles.clone()).await.unwrap();
    let (source, item, _) = import(&server, &articles, len).await;
    let path = std::env::temp_dir().join(format!("infinidysk-health-{}.sqlite", ItemId::new()));
    let db = Db::connect(&format!("sqlite:{}", path.display()))
        .await
        .unwrap();
    let mut items = Vec::new();
    for p in ["/content/tv", "/content/tv/job", "/content/tv/job/job.mkv"] {
        items.push(source.item_by_path(p).await.unwrap().unwrap());
    }
    let mut blobs = Vec::new();
    for id in items
        .iter()
        .flat_map(|i| [i.meta_blob, i.nzb_blob])
        .flatten()
    {
        if !blobs.iter().any(|(b, _)| *b == id) {
            blobs.push((id, source.blob(id).await.unwrap().unwrap()));
        }
    }
    let blobs: Vec<_> = blobs.iter().map(|(id, b)| (*id, b.as_slice())).collect();
    db.insert(&items, &blobs).await.unwrap();
    // Adopted from a library link: the Arr's file id, no download.
    db.record_owner(
        uuid::Uuid::nil(),
        item.id,
        Some(7),
        "/tv/Show/S01E01.strm",
        None,
    )
    .await
    .unwrap();
    let (log, arr) = fake_sonarr().await;
    let (_settings_tx, settings) = watch::channel(Settings::default());
    let health = Health::new(db.clone(), client(&server), arr, settings).unwrap();

    assert!(health.request_repair(item.id).await.unwrap());
    assert!(health.step().await.unwrap());
    let (_, status) = db.health(item.id).await.unwrap().unwrap();
    assert_eq!(status.state, HealthState::Hidden);
    assert_eq!(
        log.0.lock().unwrap().clone(),
        [
            "GET /api",
            "GET /api/v3/episodefile/7",
            "GET /api/v3/episode?episodeFileId=7",
            "GET /api",
            "GET /api/v3/queue/details?episodeIds=5",
            "DELETE /api/v3/episodefile/7",
            "POST /api/v3/command",
        ]
    );
}

/// Answers like Lidarr or Chaptarr (`/api/v1`) or Whisparr v3 (`/api/v3`) for one file
/// imported from `job/job.mkv`, and logs every call with its body.
async fn other_arr(State(log): State<Log>, req: Request) -> Json<Value> {
    let (parts, body) = req.into_parts();
    let body = axum::body::to_bytes(body, 1 << 16).await.unwrap();
    let mut call = format!("{} {}", parts.method, parts.uri);
    if !body.is_empty() {
        write!(call, " {}", String::from_utf8_lossy(&body)).unwrap();
    }
    log.0.lock().unwrap().push(call);
    let query = parts.uri.query().unwrap_or_default();
    let dropped = "/data/completed/tv/job/job.mkv";
    let path = parts.uri.path();
    let (root, path) = path.split_at(7);
    let v1 = root == "/api/v1";
    Json(match path {
        "/system/status" => json!({ "appName": "Whisparr", "version": "3.0.0.1" }),
        "/history" if query.contains("eventType=3") && v1 => json!({ "records": [{
            "id": 22, "trackId": 31, "albumId": 9, "bookId": 5,
            "data": { "droppedPath": dropped, "importedPath": "/music/A/01.mp4" },
        }]}),
        "/history" if query.contains("eventType=3") => json!({ "records": [{
            "id": 22, "movieId": 3,
            "data": { "droppedPath": dropped, "importedPath": "/movies/A/a.mkv" },
        }]}),
        "/history" => json!({ "records": [{ "id": 12, "data": {} }] }),
        "/track/31" => json!({ "trackFileId": 41 }),
        "/trackfile/41" => json!({ "id": 41, "albumId": 9, "path": "/music/A/01.mp4" }),
        // An ebook of the same book does not stand in for the broken file.
        "/bookfile" => json!([
            { "id": 52, "bookId": 5, "path": "/music/A/a.epub", "dateAdded": "2030-01-01T00:00:00Z" },
            { "id": 51, "bookId": 5, "path": "/music/A/01.mp4" },
        ]),
        "/movie/3" => json!({ "movieFile": { "id": 7, "path": "/movies/A/a.mkv" } }),
        "/queue/details" => json!([]),
        _ => json!({}),
    })
}

/// Repairs the imported file through one `profile` instance served by [`other_arr`];
/// returns the calls it made.
async fn repair_through(profile: Profile) -> (Vec<String>, JobId) {
    let (articles, len) = mkv("o");
    let server = LoopbackServer::start(articles.clone()).await.unwrap();
    let (db, item, job) = import(&server, &articles, len).await;
    let log: Log = Arc::default();
    let listener = TcpListener::bind("127.0.0.1:0").await.unwrap();
    let url = format!("http://{}", listener.local_addr().unwrap());
    let app = axum::Router::new()
        .fallback(other_arr)
        .with_state(Arc::clone(&log));
    tokio::spawn(async move { axum::serve(listener, app).await });
    let (_tx, arr) = watch::channel(vec![Arr {
        id: uuid::Uuid::nil(),
        name: "arr".to_owned(),
        profile,
        url,
        api_key: "k".to_owned(),
    }]);
    let (_settings_tx, settings) = watch::channel(Settings::default());
    let health = Health::new(db.clone(), client(&server), arr, settings)
        .unwrap()
        .with_arr_wait(Duration::ZERO);

    assert!(health.request_repair(item.id).await.unwrap());
    assert!(health.step().await.unwrap());
    let (_, status) = db.health(item.id).await.unwrap().unwrap();
    assert_eq!(status.state, HealthState::Hidden);
    let calls = log.0.lock().unwrap().clone();
    (calls, job)
}

#[tokio::test(flavor = "multi_thread")]
async fn a_broken_track_gets_its_album_searched_through_lidarr() {
    let (calls, job) = repair_through(Profile::Lidarr).await;
    assert_eq!(
        calls,
        [
            format!("GET /api/v1/history?downloadId={job}&eventType=3&pageSize=1000"),
            "GET /api/v1/track/31".to_owned(),
            "GET /api/v1/trackfile/41".to_owned(),
            "GET /api/v1/queue/details?albumIds=9".to_owned(),
            format!("GET /api/v1/history?downloadId={job}&eventType=1&pageSize=1"),
            "POST /api/v1/history/failed/12 {}".to_owned(),
            "DELETE /api/v1/trackfile/41".to_owned(),
            r#"POST /api/v1/command {"albumIds":[9],"name":"AlbumSearch"}"#.to_owned(),
        ]
    );
}

#[tokio::test(flavor = "multi_thread")]
async fn a_broken_book_file_gets_its_book_searched_through_chaptarr() {
    let (calls, job) = repair_through(Profile::Chaptarr).await;
    assert_eq!(
        calls,
        [
            format!("GET /api/v1/history?downloadId={job}&eventType=3&pageSize=1000"),
            "GET /api/v1/bookfile?bookId=5".to_owned(),
            "GET /api/v1/queue/details?bookIds=5".to_owned(),
            format!("GET /api/v1/history?downloadId={job}&eventType=1&pageSize=1"),
            "POST /api/v1/history/failed/12 {}".to_owned(),
            "DELETE /api/v1/bookfile/51".to_owned(),
            r#"POST /api/v1/command {"bookIds":[5],"name":"BookSearch"}"#.to_owned(),
        ]
    );
}

#[tokio::test(flavor = "multi_thread")]
async fn whisparr_v3_repairs_like_radarr() {
    let (calls, job) = repair_through(Profile::Whisparr).await;
    assert_eq!(
        calls,
        [
            "GET /api/v3/system/status".to_owned(),
            format!("GET /api/v3/history?downloadId={job}&eventType=3&pageSize=1000"),
            "GET /api/v3/movie/3".to_owned(),
            "GET /api/v3/queue/details?movieId=3".to_owned(),
            format!("GET /api/v3/history?downloadId={job}&eventType=1&pageSize=1"),
            "POST /api/v3/history/failed/12 {}".to_owned(),
            "DELETE /api/v3/moviefile/7".to_owned(),
            r#"POST /api/v3/command {"movieIds":[3],"name":"MoviesSearch"}"#.to_owned(),
        ]
    );
}

#[tokio::test(flavor = "multi_thread")]
async fn a_file_an_arr_owns_but_cannot_repair_is_kept() {
    let (articles, len) = mkv("s");
    let server = LoopbackServer::start(articles.clone()).await.unwrap();
    let (db, item, job) = import(&server, &articles, len).await;
    let sportarr = uuid::Uuid::from_u128(1);
    db.record_owner(sportarr, item.id, None, "/sports/a.mkv", Some(job))
        .await
        .unwrap();
    // Nothing listens; any call would leave the file waiting on a retry.
    let (_tx, arr) = watch::channel(vec![Arr {
        id: sportarr,
        name: "sportarr".to_owned(),
        profile: Profile::Sportarr,
        url: "http://127.0.0.1:9".to_owned(),
        api_key: "k".to_owned(),
    }]);
    let (_settings_tx, settings) = watch::channel(Settings::default());
    let health = Health::new(db.clone(), client(&server), arr, settings).unwrap();

    assert!(health.request_repair(item.id).await.unwrap());
    assert!(health.step().await.unwrap());
    let (_, status) = db.health(item.id).await.unwrap().unwrap();
    assert_eq!(status.state, HealthState::ActionNeeded);
    assert!(db.item(item.id).await.unwrap().is_some());
}

#[tokio::test(flavor = "multi_thread")]
async fn a_co_owner_that_cannot_repair_does_not_stop_one_that_can() {
    let (articles, len) = mkv("m");
    let server = LoopbackServer::start(articles.clone()).await.unwrap();
    let (db, item, job) = import(&server, &articles, len).await;
    let sportarr = uuid::Uuid::from_u128(1);
    db.record_owner(sportarr, item.id, None, "/sports/a.mkv", Some(job))
        .await
        .unwrap();
    db.record_owner(
        uuid::Uuid::nil(),
        item.id,
        Some(7),
        "/tv/Show/S01E01.strm",
        Some(job),
    )
    .await
    .unwrap();
    let (log, sonarr) = fake_sonarr().await;
    let mut instances = vec![Arr {
        id: sportarr,
        name: "sportarr".to_owned(),
        profile: Profile::Sportarr,
        url: "http://127.0.0.1:9".to_owned(),
        api_key: "k".to_owned(),
    }];
    instances.extend(sonarr.borrow().iter().cloned());
    let (_tx, arr) = watch::channel(instances);
    let (_settings_tx, settings) = watch::channel(Settings::default());
    let health = Health::new(db.clone(), client(&server), arr, settings)
        .unwrap()
        .with_arr_wait(Duration::ZERO);

    assert!(health.request_repair(item.id).await.unwrap());
    assert!(health.step().await.unwrap());
    let (_, status) = db.health(item.id).await.unwrap().unwrap();
    assert_eq!(status.state, HealthState::Hidden);
    let calls = log.0.lock().unwrap().clone();
    assert!(
        calls.iter().any(|c| c == "DELETE /api/v3/episodefile/7"),
        "{calls:?}"
    );
    assert!(
        calls.iter().any(|c| c == "POST /api/v3/command"),
        "{calls:?}"
    );
}

#[tokio::test(flavor = "multi_thread")]
async fn carried_over_file_waits_for_the_owner_backfill_before_deletion() {
    let (articles, len) = mkv("g");
    let server = LoopbackServer::start(articles.clone()).await.unwrap();
    let (source, item, _) = import(&server, &articles, len).await;
    let path = std::env::temp_dir().join(format!("infinidysk-health-{}.sqlite", ItemId::new()));
    let db = Db::connect(&format!("sqlite:{}", path.display()))
        .await
        .unwrap();
    let mut items = Vec::new();
    for p in ["/content/tv", "/content/tv/job", "/content/tv/job/job.mkv"] {
        items.push(source.item_by_path(p).await.unwrap().unwrap());
    }
    let mut blobs = Vec::new();
    for id in items
        .iter()
        .flat_map(|i| [i.meta_blob, i.nzb_blob])
        .flatten()
    {
        if !blobs.iter().any(|(b, _)| *b == id) {
            blobs.push((id, source.blob(id).await.unwrap().unwrap()));
        }
    }
    let blobs: Vec<_> = blobs.iter().map(|(id, b)| (*id, b.as_slice())).collect();
    db.insert(&items, &blobs).await.unwrap();
    let (log, arr) = fake_sonarr().await;
    let (_settings_tx, settings) = watch::channel(Settings::default());
    let health = Health::new(db.clone(), client(&server), arr, settings).unwrap();

    assert!(health.request_repair(item.id).await.unwrap());
    assert!(health.step().await.unwrap());
    let (_, mut status) = db.health(item.id).await.unwrap().unwrap();
    assert_eq!(status.state, HealthState::Repairing);
    assert!(status.next_check.unwrap() > infinidysk_db::now_ms());
    assert!(log.0.lock().unwrap().is_empty(), "no Arr call");

    db.write_config(&[(infinidysk_health::BACKFILLED_KEY, "1")], &[])
        .await
        .unwrap();
    status.next_check = Some(0);
    db.set_health(item.id, &status, Recheck::None)
        .await
        .unwrap();
    assert!(health.step().await.unwrap());
    assert!(db.item(item.id).await.unwrap().is_none());
}

#[tokio::test(flavor = "multi_thread")]
async fn broken_mp4_container_is_confirmed_then_repaired() {
    // The `ftyp` box covers the first segment; the next box claims more than the file.
    let mut data: Vec<u8> = (0..100_000u32)
        .map(|i| u8::try_from(i % 251).unwrap())
        .collect();
    data[..8].copy_from_slice(&[0, 0, 0x4e, 0x20, b'f', b't', b'y', b'p']);
    data[20_000..20_008].copy_from_slice(&[0, 0x0f, 0x42, 0x40, b'm', b'o', b'o', b'v']);
    let articles = post("b", "b.mp4", &data, ARTICLE);
    let server = LoopbackServer::start(articles.clone()).await.unwrap();
    let path = std::env::temp_dir().join(format!("infinidysk-health-{}.sqlite", ItemId::new()));
    let db = Db::connect(&format!("sqlite:{}", path.display()))
        .await
        .unwrap();
    let queue = Queue::new(db.clone(), client(&server));
    let job = Submission {
        file_name: "job.nzb",
        category: "tv",
        priority: 0,
        post_processing: 0,
    };
    queue
        .enqueue(&job, &nzb(&articles, data.len(), "b.mp4"))
        .await
        .unwrap();
    assert!(queue.process_next().await);
    drop(queue);
    let item = db
        .item_by_path("/content/tv/job/job.mp4")
        .await
        .unwrap()
        .unwrap();
    let (_tx, arr) = watch::channel(Vec::new());
    let (_settings_tx, settings) = watch::channel(Settings::default());
    let health = Health::new(db.clone(), client(&server), arr, settings).unwrap();
    let state = || async { db.health(item.id).await.unwrap().map(|(_, s)| s.state) };

    assert!(health.step().await.unwrap());
    assert_eq!(state().await, Some(HealthState::Suspect));
    let probe = db.media_probe(item.id).await.unwrap();
    let defect = probe.defect.unwrap();
    assert_eq!((defect.offset, defect.size), (20_000, 1_000_000));
    assert!(probe.structural_hold);
    assert!(!health.step().await.unwrap(), "the confirming check waits");

    // Fifteen minutes later the same defect is confirmed.
    let first = Defect {
        seen_at: 0,
        ..defect
    };
    db.set_structural(item.id, Some(&first), true)
        .await
        .unwrap();
    let (_, mut status) = db.health(item.id).await.unwrap().unwrap();
    status.next_check = Some(0);
    db.set_health(item.id, &status, Recheck::None)
        .await
        .unwrap();
    assert!(health.step().await.unwrap());
    drop(health);
    assert_eq!(state().await, Some(HealthState::Repairing));
    let results: Vec<_> = db
        .health_checks(infinidysk_db::HealthFilter::default(), 10)
        .await
        .unwrap()
        .into_iter()
        .map(|(c, _)| c.result)
        .collect();
    assert_eq!(results, [HealthResult::Damaged, HealthResult::Damaged]);
}

#[tokio::test(flavor = "multi_thread")]
async fn a_full_import_holds_a_broken_mp4() {
    let mut data: Vec<u8> = (0..100_000u32)
        .map(|i| u8::try_from(i % 251).unwrap())
        .collect();
    data[..8].copy_from_slice(&[0, 0, 0x4e, 0x20, b'f', b't', b'y', b'p']);
    data[20_000..20_008].copy_from_slice(&[0, 0x0f, 0x42, 0x40, b'm', b'o', b'o', b'v']);
    let articles = post("c", "c.mp4", &data, ARTICLE);
    let server = LoopbackServer::start(articles.clone()).await.unwrap();
    let (db, item, _) = import_at(&server, &articles, data.len(), "c.mp4", 100).await;
    let (_, status) = db.health(item.id).await.unwrap().unwrap();
    assert_eq!(status.state, HealthState::Suspect);
    let probe = db.media_probe(item.id).await.unwrap();
    let defect = probe.defect.unwrap();
    assert_eq!((defect.offset, defect.size), (20_000, 1_000_000));
    assert!(probe.structural_hold);
    assert_eq!(probe.requested_at, None, "no analysis of a broken file");
    let checks = db.item_checks(item.id, 5).await.unwrap();
    assert_eq!(checks.len(), 1);
    assert_eq!(checks[0].result, HealthResult::Damaged);

    let (_tx, arr) = watch::channel(Vec::new());
    let (_settings_tx, settings) = watch::channel(Settings::default());
    let health = Health::new(db.clone(), client(&server), arr, settings).unwrap();
    assert!(!health.step().await.unwrap(), "the confirming check waits");
}
