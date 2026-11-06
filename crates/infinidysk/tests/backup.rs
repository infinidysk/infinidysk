//! Backups end to end: the backup job, download, upload, restore at the next start, undo,
//! and rolling back a restored database that does not open.
#![allow(clippy::unwrap_used)] // Test helpers outside `#[test]` fns; panics are the assertions.

use std::fmt::Write as _;
use std::fs;
use std::net::SocketAddr;
use std::os::unix::fs::PermissionsExt as _;
use std::path::{Path, PathBuf};
use std::time::{Duration, Instant};

use infinidysk::app::{Boot, Exit, serve};
use infinidysk_core::{ItemId, ItemKind, JobId};
use infinidysk_db::{Db, HistoryEntry, Item, JobStatus};
use serde_json::{Value, json};
use tokio::io::{AsyncReadExt, AsyncWriteExt};
use tokio::net::{TcpListener, TcpStream};
use tokio::task::JoinHandle;
use tokio_util::sync::CancellationToken;

const BACKUP_JOB: &str = "/api/v1/maintenance/backup-database/run";

struct Reply {
    code: u16,
    head: String,
    body: Vec<u8>,
}

impl Reply {
    fn json(&self) -> Value {
        serde_json::from_slice(&self.body).unwrap_or(Value::Null)
    }
}

async fn request(
    addr: SocketAddr,
    method: &str,
    path: &str,
    headers: &[(&str, String)],
    body: &[u8],
) -> Reply {
    let mut head = format!(
        "{method} {path} HTTP/1.1\r\nHost: x\r\nConnection: close\r\nX-Api-Key: adminkey\r\n"
    );
    for (k, v) in headers {
        let _ = write!(head, "{k}: {v}\r\n");
    }
    head.push_str("\r\n");
    let mut stream = TcpStream::connect(addr).await.unwrap();
    stream.write_all(head.as_bytes()).await.unwrap();
    stream.write_all(body).await.unwrap();
    let mut response = Vec::new();
    stream.read_to_end(&mut response).await.unwrap();
    let split = response.windows(4).position(|w| w == b"\r\n\r\n").unwrap();
    let head = String::from_utf8_lossy(&response[..split]).into_owned();
    let code = head.split_whitespace().nth(1).unwrap().parse().unwrap();
    Reply {
        code,
        head,
        body: response[split + 4..].to_vec(),
    }
}

async fn admin(addr: SocketAddr, method: &str, path: &str, body: &str) -> Reply {
    let headers = [
        ("Content-Type", "application/json".to_owned()),
        ("Content-Length", body.len().to_string()),
    ];
    request(addr, method, path, &headers, body.as_bytes()).await
}

async fn backups(addr: SocketAddr) -> Value {
    let reply = admin(addr, "GET", "/api/v1/backups", "").await;
    assert_eq!(reply.code, 200);
    reply.json()
}

/// Runs the backup job and returns the name of the new backup.
async fn back_up(addr: SocketAddr) -> String {
    let reply = admin(addr, "POST", BACKUP_JOB, "{}").await;
    assert_eq!(reply.code, 200, "{}", reply.json());
    let deadline = Instant::now() + Duration::from_secs(60);
    loop {
        let view = admin(addr, "GET", "/api/v1/maintenance", "").await.json();
        let report = &view["jobs"]
            .as_array()
            .unwrap()
            .iter()
            .find(|j| j["job"] == "backup-database")
            .unwrap()["report"];
        if view["running"].is_null() && !report.is_null() {
            assert_eq!(report["outcome"], "finished", "{report}");
            assert!(report["backup"]["size_bytes"].as_u64().unwrap() > 0);
            return report["backup"]["name"].as_str().unwrap().to_owned();
        }
        assert!(Instant::now() < deadline, "backup did not end: {view}");
        tokio::time::sleep(Duration::from_millis(50)).await;
    }
}

type Server = JoinHandle<anyhow::Result<Exit>>;

fn db_url(root: &Path) -> String {
    format!("sqlite:{}", root.join("infinidysk.sqlite").display())
}

fn live(root: &Path, suffix: &str) -> PathBuf {
    root.join(format!("infinidysk.sqlite{suffix}"))
}

/// Serves an install whose database is `root/infinidysk.sqlite`, with `mount` as the mount
/// folder when set.
async fn launch(root: &Path, mount: Option<&Path>) -> (SocketAddr, Server, CancellationToken) {
    let listener = TcpListener::bind("127.0.0.1:0").await.unwrap();
    let addr = listener.local_addr().unwrap();
    let boot = Boot {
        listen: addr,
        database_url: db_url(root),
        database_source: infinidysk::database::Source::Env,
        uid: 0,
        gid: 0,
        trace_minutes: 0,
        metrics_path: root.join("metrics.sqlite"),
        log_dir: root.join("logs"),
        home: root.to_owned(),
        config_dir: root.to_owned(),
        ffprobe: "ffprobe".to_owned(),
    };
    let mut vars = vec![(
        "INFINIDYSK__AUTH__API_KEY".to_owned(),
        "adminkey".to_owned(),
    )];
    if let Some(mount) = mount {
        vars.push((
            "INFINIDYSK__MOUNT__DIR".to_owned(),
            mount.display().to_string(),
        ));
    }
    let cancel = CancellationToken::new();
    let server = tokio::spawn({
        let cancel = cancel.clone();
        async move { serve(listener, &boot, vars, cancel).await }
    });
    (addr, server, cancel)
}

/// [`launch`], once the server answers.
async fn start(root: &Path) -> (SocketAddr, Server, CancellationToken) {
    let (addr, server, cancel) = launch(root, Some(&root.join("mnt"))).await;
    backups(addr).await;
    (addr, server, cancel)
}

async fn ended(server: Server) -> Exit {
    tokio::time::timeout(Duration::from_secs(60), server)
        .await
        .expect("the server did not stop")
        .unwrap()
        .unwrap()
}

async fn stop(server: Server, cancel: &CancellationToken) {
    cancel.cancel();
    assert_eq!(ended(server).await, Exit::Stopped);
}

fn root() -> PathBuf {
    let root = std::env::temp_dir().join(format!("infinidysk-backup-e2e-{}", ItemId::new()));
    fs::create_dir_all(root.join("mnt")).unwrap();
    root
}

/// A finished download named `folder` under `/content/tv`.
async fn download(root: &Path, folder: &str) {
    let db = Db::connect(&db_url(root)).await.unwrap();
    add(&db, folder).await;
    db.close().await;
}

async fn add(db: &Db, folder: &str) {
    let tv = db.item_by_path("/content/tv").await.unwrap().unwrap();
    let dir = Item::child(&tv, folder, ItemKind::Directory).unwrap();
    let file = Item::child(&dir, "episode.mkv", ItemKind::NzbFile).unwrap();
    let history = HistoryEntry {
        id: JobId::new(),
        created_at: 1,
        file_name: format!("{folder}.nzb"),
        job_name: folder.to_owned(),
        category: "tv".to_owned(),
        status: JobStatus::Completed,
        total_bytes: 1,
        download_seconds: 1,
        fail_message: None,
        download_dir: Some(dir.id),
        nzb_blob: None,
        storage: None,
    };
    db.add_history(&history, &[dir, file], &[]).await.unwrap();
}

async fn has(root: &Path, folder: &str) -> bool {
    let db = Db::connect(&db_url(root)).await.unwrap();
    let found = db
        .item_by_path(&format!("/content/tv/{folder}"))
        .await
        .unwrap()
        .is_some();
    db.close().await;
    found
}

/// Starts a restore and waits for the server to restart.
async fn restore(addr: SocketAddr, server: Server, name: &str) -> String {
    let reply = admin(addr, "POST", &format!("/api/v1/backups/{name}/restore"), "").await;
    assert_eq!(reply.code, 202, "{}", reply.json());
    let attempt = reply.json()["attempt"].as_str().unwrap().to_owned();
    assert_eq!(ended(server).await, Exit::Restart);
    attempt
}

async fn undo(addr: SocketAddr, server: Server) {
    let reply = admin(addr, "POST", "/api/v1/backups/undo", "").await;
    assert_eq!(reply.code, 202, "{}", reply.json());
    assert_eq!(ended(server).await, Exit::Restart);
}

fn mode(path: &Path) -> u32 {
    fs::metadata(path).unwrap().permissions().mode() & 0o777
}

fn intent(root: &Path) -> Option<Value> {
    fs::read(live(root, ".restore.json"))
        .ok()
        .map(|b| serde_json::from_slice(&b).unwrap())
}

#[tokio::test]
async fn backs_up_downloads_restores_and_undoes() {
    let root = root();
    let (addr, server, _) = start(&root).await;
    download(&root, "a").await;
    let name = back_up(addr).await;
    let file = root.join("backups").join(&name);
    assert_eq!(mode(&file), 0o600);
    assert_eq!(
        mode(&root.join("backups").join(format!("{name}.json"))),
        0o600
    );
    let view = backups(addr).await;
    let listed = &view["backups"][0];
    assert_eq!(listed["name"], name.as_str());
    assert_eq!(listed["kind"], "manual");
    assert!(listed["sha256"].is_string() && listed["verified_ms"].is_i64());
    assert!(listed["items"].as_u64().unwrap() >= 1);
    let range = request(
        addr,
        "GET",
        &format!("/api/v1/backups/{name}/file"),
        &[("Range", "bytes=0-15".to_owned())],
        b"",
    )
    .await;
    assert_eq!(range.code, 206, "{}", range.head);
    assert_eq!(range.body, b"SQLite format 3\0");

    download(&root, "b").await;
    let attempt = restore(addr, server, &name).await;
    assert!(intent(&root).is_some());

    let (addr, server, _) = start(&root).await;
    let view = backups(addr).await;
    assert!(has(&root, "a").await && !has(&root, "b").await);
    assert_eq!(view["last_restore"]["attempt"], attempt.as_str(), "{view}");
    assert_eq!(view["last_restore"]["backup"], name.as_str());
    assert!(view["undo"]["size_bytes"].as_u64().unwrap() > 0);
    assert!(intent(&root).is_none());

    undo(addr, server).await;
    let (addr, server, cancel) = start(&root).await;
    let view = backups(addr).await;
    assert!(has(&root, "b").await);
    assert!(
        view["undo"].is_null() && view["last_restore"].is_null(),
        "{view}"
    );
    stop(server, &cancel).await;
    fs::remove_dir_all(root).unwrap();
}

#[tokio::test]
async fn uploads_a_backup_and_rejects_other_files() {
    let root = root();
    let (addr, server, cancel) = start(&root).await;
    let upload = async |body: &[u8]| {
        let headers = [("Content-Length", body.len().to_string())];
        request(
            addr,
            "PUT",
            "/api/v1/backups/upload?name=mine.sqlite",
            &headers,
            body,
        )
        .await
    };
    assert_eq!(upload(b"not a database").await.code, 400);
    let name = back_up(addr).await;
    let bytes = fs::read(root.join("backups").join(&name)).unwrap();
    let made = upload(&bytes).await;
    assert_eq!(made.code, 201, "{}", made.json());
    let uploaded = made.json();
    assert_eq!(uploaded["kind"], "uploaded");
    assert_eq!(uploaded["source_name"], "mine.sqlite");
    let source = backups(addr).await["backups"]
        .as_array()
        .unwrap()
        .iter()
        .find(|b| b["name"] == name.as_str())
        .unwrap()["sha256"]
        .clone();
    assert_eq!(uploaded["sha256"], source);
    let bare = request(addr, "PUT", "/api/v1/backups/upload", &[], b"").await;
    assert_eq!(bare.code, 400);
    stop(server, &cancel).await;
    fs::remove_dir_all(root).unwrap();
}

/// Writes a restore intent at `requested` and `restore` as the staged copy.
fn stage(root: &Path, restore: &[u8]) {
    fs::write(live(root, ".restore"), restore).unwrap();
    let intent = json!({
        "format": 1,
        "attempt": "00000000-0000-0000-0000-000000000001",
        "action": "restore",
        "phase": "requested",
        "backup": "x.sqlite",
        "backup_created_ms": 1,
    });
    fs::write(live(root, ".restore.json"), intent.to_string()).unwrap();
}

/// A backup copy that records a migration newer than this build.
async fn future_backup(root: &Path) -> Vec<u8> {
    edited_backup(
        root,
        "INSERT INTO _sqlx_migrations (version, description, success, checksum, execution_time) \
         SELECT max(version) + 1, 'future', 1, x'00', 0 FROM _sqlx_migrations",
        None,
    )
    .await
}

/// A backup copy whose stored settings do not parse.
async fn broken_settings_backup(root: &Path) -> Vec<u8> {
    edited_backup(
        root,
        "INSERT INTO config (key, value) VALUES ('health', 'not json')",
        None,
    )
    .await
}

/// A backup copy whose saved mount folder is `root`, the config folder, so it would hold the
/// default disk cache.
async fn mount_over_config_backup(root: &Path) -> Vec<u8> {
    let mount = json!({ "dir": root.display().to_string() });
    edited_backup(
        root,
        "INSERT OR REPLACE INTO config (key, value) VALUES ('mount', $1)",
        Some(mount.to_string()),
    )
    .await
}

async fn edited_backup(root: &Path, sql: &'static str, bind: Option<String>) -> Vec<u8> {
    use sqlx::ConnectOptions as _;
    let (addr, server, cancel) = start(root).await;
    let name = back_up(addr).await;
    stop(server, &cancel).await;
    let path = root.join("backups").join(&name);
    let mut conn = sqlx::sqlite::SqliteConnectOptions::new()
        .filename(&path)
        .connect()
        .await
        .unwrap();
    let mut query = sqlx::query(sql);
    if let Some(value) = bind {
        query = query.bind(value);
    }
    query.execute(&mut conn).await.unwrap();
    sqlx::Connection::close(conn).await.unwrap();
    fs::read(path).unwrap()
}

fn junk() -> Vec<u8> {
    vec![0x5a; 64 * 1024]
}

#[tokio::test]
async fn an_open_failure_records_a_rollback_and_restarts() {
    for case in ["junk", "future", "settings", "mount"] {
        let root = root();
        let restored = match case {
            "future" => future_backup(&root).await,
            "settings" => broken_settings_backup(&root).await,
            "mount" => mount_over_config_backup(&root).await,
            _ => {
                let (_, server, cancel) = start(&root).await;
                stop(server, &cancel).await;
                junk()
            }
        };
        let before: Vec<Option<Vec<u8>>> = ["", "-wal", "-shm", "-journal"]
            .iter()
            .map(|s| fs::read(live(&root, s)).ok())
            .collect();
        stage(&root, &restored);
        let mnt = root.join("mnt");
        let (_, server, _) = launch(&root, (case != "mount").then_some(mnt.as_path())).await;
        assert_eq!(ended(server).await, Exit::Restart, "case: {case}");
        let i = intent(&root).unwrap();
        assert_eq!(i["phase"], "rolling-back", "{i}");
        assert!(i["failed"].is_string());
        // The failed open may still write the header's counters; the page layout stays.
        let now = fs::read(live(&root, "")).unwrap();
        assert_eq!(now[..18], restored[..18], "case: {case}");
        assert_eq!(now.len(), restored.len(), "case: {case}");
        let undo: Vec<Option<Vec<u8>>> = ["", "-wal", "-shm", "-journal"]
            .iter()
            .map(|s| fs::read(live(&root, &format!(".before-restore{s}"))).ok())
            .collect();
        assert_eq!(undo, before, "case: {case}");
        fs::remove_dir_all(root).unwrap();
    }
}

#[tokio::test]
async fn a_restore_that_does_not_open_rolls_back_in_a_fresh_process() {
    for future in [false, true] {
        let root = root();
        let restored = if future {
            future_backup(&root).await
        } else {
            junk()
        };
        // The live set keeps download A only in its WAL.
        let build = root.join("build");
        fs::create_dir_all(build.join("mnt")).unwrap();
        let (_, server, cancel) = start(&build).await;
        stop(server, &cancel).await;
        let db = Db::connect(&db_url(&build)).await.unwrap();
        add(&db, "a").await;
        for s in ["", "-wal"] {
            let _ = fs::remove_file(live(&root, s));
            fs::copy(live(&build, s), live(&root, s)).unwrap();
        }
        db.close().await;
        for s in ["-shm", "-journal"] {
            let _ = fs::remove_file(live(&root, s));
        }
        assert!(fs::metadata(live(&root, "-wal")).unwrap().len() > 0);
        stage(&root, &restored);

        let port = TcpListener::bind("127.0.0.1:0")
            .await
            .unwrap()
            .local_addr()
            .unwrap()
            .port();
        let addr = SocketAddr::from(([127, 0, 0, 1], port));
        let mut child = tokio::process::Command::new(env!("CARGO_BIN_EXE_infinidysk"))
            .env("INFINIDYSK_DATABASE_URL", db_url(&root))
            .env("INFINIDYSK_LISTEN", addr.to_string())
            .env("INFINIDYSK_METRICS_PATH", root.join("metrics.sqlite"))
            .env("INFINIDYSK_LOG_DIR", root.join("logs"))
            .env("INFINIDYSK__AUTH__API_KEY", "adminkey")
            .env("INFINIDYSK__MOUNT__DIR", root.join("mnt"))
            .stdout(std::process::Stdio::null())
            .stderr(std::process::Stdio::null())
            .kill_on_drop(true)
            .spawn()
            .unwrap();
        let deadline = Instant::now() + Duration::from_secs(60);
        let view = loop {
            // The process restarts once, so a request may be cut off; that panics the task.
            let reply = tokio::spawn(admin(addr, "GET", "/api/v1/backups", "")).await;
            if let Ok(reply) = reply
                && reply.code == 200
            {
                break reply.json();
            }
            assert!(Instant::now() < deadline, "the server did not come back");
            tokio::time::sleep(Duration::from_millis(100)).await;
        };
        assert!(view["last_restore"]["failed"].is_string(), "{view}");
        assert!(intent(&root).is_none());
        assert!(fs::metadata(live(&root, ".restore")).is_err());
        for s in ["", "-wal", "-shm", "-journal"] {
            assert!(fs::metadata(live(&root, &format!(".before-restore{s}"))).is_err());
        }
        child.kill().await.unwrap();
        assert!(has(&root, "a").await, "future: {future}");
        fs::remove_dir_all(root).unwrap();
    }
}

#[tokio::test]
async fn a_failed_source_manifest_update_still_restores() {
    let root = root();
    let (addr, server, _) = start(&root).await;
    let name = back_up(addr).await;
    restore(addr, server, &name).await;
    let (addr, server, _) = start(&root).await;
    let first = backups(addr).await["last_restore"]["at_ms"]
        .as_i64()
        .unwrap();
    download(&root, "c").await;
    fs::create_dir(root.join("backups").join(format!("{name}.json.tmp"))).unwrap();
    restore(addr, server, &name).await;
    let (addr, server, _) = start(&root).await;
    let view = backups(addr).await;
    assert!(!has(&root, "c").await);
    assert!(view["undo"].is_object(), "{view}");
    assert!(view["last_restore"]["at_ms"].as_i64().unwrap() > first);
    undo(addr, server).await;
    let (_, server, cancel) = start(&root).await;
    assert!(has(&root, "c").await);
    stop(server, &cancel).await;
    fs::remove_dir_all(root).unwrap();
}

#[tokio::test]
async fn a_failed_intent_publish_leaves_no_restore() {
    let root = root();
    let (addr, server, _) = start(&root).await;
    let name = back_up(addr).await;
    restore(addr, server, &name).await;
    let (addr, server, cancel) = start(&root).await;
    let before = backups(addr).await;
    download(&root, "c").await;
    fs::create_dir(live(&root, ".restore.json.tmp")).unwrap();
    let reply = admin(addr, "POST", &format!("/api/v1/backups/{name}/restore"), "").await;
    assert_eq!(reply.code, 202);
    let attempt = reply.json()["attempt"].clone();
    let deadline = Instant::now() + Duration::from_secs(60);
    let view = loop {
        let view = backups(addr).await;
        if view["attempt"]["id"] == attempt && view["attempt"]["state"] == "failed" {
            break view;
        }
        assert!(
            Instant::now() < deadline,
            "the restore did not fail: {view}"
        );
        tokio::time::sleep(Duration::from_millis(50)).await;
    };
    assert!(view["attempt"]["reason"].is_string());
    assert_eq!(view["task"]["action"], "restore");
    assert!(!server.is_finished());
    assert!(intent(&root).is_none());
    assert!(fs::metadata(live(&root, ".restore")).is_err());
    assert!(fs::metadata(live(&root, ".restore.part")).is_err());
    stop(server, &cancel).await;
    let (addr, server, cancel) = start(&root).await;
    assert!(has(&root, "c").await);
    let after = backups(addr).await;
    assert_eq!(after["last_restore"], before["last_restore"]);
    assert_eq!(after["undo"], before["undo"]);
    stop(server, &cancel).await;
    fs::remove_dir_all(root).unwrap();
}

#[tokio::test]
async fn verify_and_restore_refuse_a_changed_backup() {
    use sqlx::ConnectOptions as _;
    let root = root();
    let (addr, server, cancel) = start(&root).await;
    download(&root, "a").await;
    let name = back_up(addr).await;
    let path = root.join("backups").join(&name);
    let sha = backups(addr).await["backups"][0]["sha256"].clone();
    let mut conn = sqlx::sqlite::SqliteConnectOptions::new()
        .filename(&path)
        .connect()
        .await
        .unwrap();
    sqlx::query("UPDATE items SET name = 'changed' WHERE name = 'episode.mkv'")
        .execute(&mut conn)
        .await
        .unwrap();
    sqlx::Connection::close(conn).await.unwrap();
    let check = infinidysk_db::check_file(&path, || false).await.unwrap();
    assert_eq!(check.failure(), None);

    let reply = admin(addr, "POST", &format!("/api/v1/backups/{name}/verify"), "").await;
    assert_eq!(reply.code, 202);
    let deadline = Instant::now() + Duration::from_secs(60);
    let listed = loop {
        let listed = backups(addr).await["backups"][0].clone();
        if listed["verify_error"].is_string() {
            break listed;
        }
        assert!(Instant::now() < deadline, "verify did not finish: {listed}");
        tokio::time::sleep(Duration::from_millis(50)).await;
    };
    assert!(
        listed["verify_error"]
            .as_str()
            .unwrap()
            .contains("checksum")
    );
    assert_eq!(listed["sha256"], sha);

    let reply = admin(addr, "POST", &format!("/api/v1/backups/{name}/restore"), "").await;
    assert_eq!(reply.code, 202);
    let deadline = Instant::now() + Duration::from_secs(60);
    loop {
        let view = backups(addr).await;
        if view["task"]["action"] == "restore" {
            assert!(
                view["task"]["reason"]
                    .as_str()
                    .unwrap()
                    .contains("checksum")
            );
            break;
        }
        assert!(Instant::now() < deadline, "restore did not fail: {view}");
        tokio::time::sleep(Duration::from_millis(50)).await;
    }
    assert!(intent(&root).is_none());
    stop(server, &cancel).await;
    fs::remove_dir_all(root).unwrap();
}

#[tokio::test]
async fn restore_rejects_unknown_and_unsafe_names() {
    let root = root();
    let (addr, server, cancel) = start(&root).await;
    let missing = admin(addr, "POST", "/api/v1/backups/nope.sqlite/restore", "").await;
    assert_eq!(missing.code, 404);
    let unsafe_name = admin(addr, "POST", "/api/v1/backups/..%2Fx.sqlite/restore", "").await;
    assert_eq!(unsafe_name.code, 400);
    stop(server, &cancel).await;
    fs::remove_dir_all(root).unwrap();
}
