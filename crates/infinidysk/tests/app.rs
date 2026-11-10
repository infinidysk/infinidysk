//! The server answers the SAB API, the admin API and `WebDAV` on one port, applies settings
//! live and shuts down on cancel.
#![allow(clippy::unwrap_used)] // Test helpers outside `#[test]` fns; panics are the assertions.

use std::net::SocketAddr;
use std::process::Stdio;
use std::sync::atomic::{AtomicUsize, Ordering};
use std::sync::{Arc, Mutex};
use std::time::Duration;

use axum::Json;
use axum::extract::{Request, State};
use infinidysk::app::{Boot, Exit, serve};
use infinidysk_core::{ItemId, ItemKind, JobId};
use infinidysk_db::{HistoryEntry, Item, JobStatus};
use serde_json::{Value, json};
use tokio::io::{AsyncReadExt, AsyncWriteExt};
use tokio::net::{TcpListener, TcpStream};
use tokio_util::sync::CancellationToken;

const NZB: &str = r#"<?xml version="1.0"?><nzb><file poster="p" date="0" subject="a.mkv"><groups><group>a.b</group></groups><segments><segment bytes="100" number="1">a@b</segment></segments></file></nzb>"#;

/// Sends one raw HTTP/1.1 request; returns the status line and body.
async fn send(addr: SocketAddr, request: &str) -> (String, String) {
    let mut stream = TcpStream::connect(addr).await.unwrap();
    stream.write_all(request.as_bytes()).await.unwrap();
    let mut response = String::new();
    stream.read_to_string(&mut response).await.unwrap();
    let (head, body) = response.split_once("\r\n\r\n").unwrap();
    (head.lines().next().unwrap().to_owned(), body.to_owned())
}

async fn status(addr: SocketAddr, request: &str) -> String {
    send(addr, request).await.0
}

fn settings_from_view(body: &str) -> Value {
    let view: Value = serde_json::from_str(body).unwrap();
    let mut settings = view["settings"].clone();
    settings["indexers_revision"] = view["indexers_revision"].clone();
    settings
}

fn admin_request(method: &str, path: &str, body: &str) -> String {
    format!(
        "{method} {path} HTTP/1.1\r\nHost: x\r\nConnection: close\r\nX-Api-Key: adminkey\r\n\
         Content-Type: application/json\r\nContent-Length: {}\r\n\r\n{body}",
        body.len()
    )
}

type Server = tokio::task::JoinHandle<anyhow::Result<Exit>>;

/// Starts a fresh install with the `WebDAV` password `pw`, admin key `adminkey` and SAB
/// key `sabkey`; also returns its database URL.
async fn start() -> (SocketAddr, CancellationToken, Server, String) {
    start_from(None).await
}

/// Like [`start`], beside a C# `/config` whose `db.sqlite` holds `csharp_db`.
async fn start_from(csharp_db: Option<&[u8]>) -> (SocketAddr, CancellationToken, Server, String) {
    let path = std::env::temp_dir().join(format!("infinidysk-app-{}.sqlite", ItemId::new()));
    if let Some(data) = csharp_db {
        std::fs::create_dir_all(path.with_extension("config")).unwrap();
        std::fs::write(path.with_extension("config").join("db.sqlite"), data).unwrap();
    }
    let listener = TcpListener::bind("127.0.0.1:0").await.unwrap();
    let addr = listener.local_addr().unwrap();
    let boot = Boot {
        listen: addr,
        database_url: format!("sqlite:{}", path.display()),
        database_source: infinidysk::database::Source::Env,
        uid: 0,
        gid: 0,
        trace_minutes: 0,
        metrics_path: path.with_extension("metrics.sqlite"),
        log_dir: path.with_extension("logs"),
        home: path.with_extension("config"),
        config_dir: path.with_extension("config"),
        ffprobe: "ffprobe".to_owned(),
    };
    // No providers: a fresh install starts and is configured from the UI.
    let vars = [
        ("INFINIDYSK__WEBDAV__PASSWORD".to_owned(), "pw".to_owned()),
        (
            "INFINIDYSK__AUTH__API_KEY".to_owned(),
            "adminkey".to_owned(),
        ),
        ("INFINIDYSK__SAB__API_KEY".to_owned(), "sabkey".to_owned()),
    ];
    let cancel = CancellationToken::new();
    let database_url = boot.database_url.clone();
    let server = tokio::spawn({
        let cancel = cancel.clone();
        async move { serve(listener, &boot, vars, cancel).await }
    });
    (addr, cancel, server, database_url)
}

#[tokio::test]
async fn a_failed_csharp_import_serves_only_the_ui_and_why() {
    let (addr, cancel, server, _) = start_from(Some(b"not a database")).await;
    let request = |method: &str, path: &str| {
        format!(
            "{method} {path} HTTP/1.1\r\nHost: x\r\nConnection: close\r\nContent-Length: 0\r\n\r\n"
        )
    };
    let failed = wait_for_failed_import(addr).await;
    assert_eq!(failed["state"], "failed");
    assert!(failed["error"].is_string());
    // Arrs and media servers wait instead of acting on an empty install.
    for (method, path) in [
        ("GET", "/api?mode=queue"),
        ("PROPFIND", "/"),
        ("GET", "/api/v1/auth"),
    ] {
        assert_eq!(
            status(addr, &request(method, path)).await,
            "HTTP/1.1 503 Service Unavailable",
            "{method} {path}"
        );
    }
    let (code, body) = send(addr, &request("GET", "/overview")).await;
    assert_eq!(code, "HTTP/1.1 200 OK");
    assert!(body.starts_with("<!doctype html>"));
    let (code, body) = send(addr, &request("GET", "/healthz")).await;
    assert_eq!(code, "HTTP/1.1 503 Service Unavailable");
    assert_eq!(
        serde_json::from_str::<Value>(&body).unwrap(),
        json!({"status": "unhealthy", "checks": {"import": "fail"}})
    );
    assert!(infinidysk::healthcheck::probe(addr).await.is_err());

    // A retry runs the import again; it fails the same way here.
    let retry = request("POST", "/api/v1/upgrade/retry");
    assert_eq!(status(addr, &retry).await, "HTTP/1.1 202 Accepted");
    assert_eq!(wait_for_failed_import(addr).await["state"], "failed");
    cancel.cancel();
    server.await.unwrap().unwrap();
}

async fn wait_for_failed_import(addr: SocketAddr) -> Value {
    tokio::time::timeout(std::time::Duration::from_secs(5), async {
        loop {
            let (code, body) = send(
                addr,
                "GET /api/v1/upgrade/status HTTP/1.1\r\nHost: x\r\nConnection: close\r\n\r\n",
            )
            .await;
            assert_eq!(code, "HTTP/1.1 200 OK");
            let status: Value = serde_json::from_str(&body).unwrap();
            if status["state"] == "failed" {
                return status;
            }
            assert_eq!(status["state"], "importing");
            tokio::task::yield_now().await;
        }
    })
    .await
    .unwrap()
}

#[tokio::test]
async fn a_restart_request_stops_the_server_for_a_restart() {
    let (addr, _cancel, server, _) = start().await;
    let (code, _) = send(addr, &admin_request("POST", "/api/v1/restart", "")).await;
    assert_eq!(code, "HTTP/1.1 202 Accepted");
    assert_eq!(server.await.unwrap().unwrap(), Exit::Restart);
}

/// After an import from 1.x, the System page lists 1.x data still on disk; signed in only.
#[tokio::test]
async fn lists_1x_data_left_after_the_import() {
    let (addr, cancel, server, database_url) = start().await;
    let get = admin_request("GET", "/api/v1/upgrade/leftovers", "");
    let (code, body) = send(addr, &get).await;
    assert_eq!(code, "HTTP/1.1 200 OK");
    let view: Value = serde_json::from_str(&body).unwrap();
    assert_eq!(view["imported"], false);
    assert_eq!(view["present"], false);

    let config = std::path::PathBuf::from(database_url.trim_start_matches("sqlite:"))
        .with_extension("config");
    std::fs::create_dir_all(config.join("blobs")).unwrap();
    std::fs::write(config.join("db.sqlite"), vec![1u8; 8192]).unwrap();
    // Not imported from 1.x: a db.sqlite there means nothing.
    let (_, body) = send(addr, &get).await;
    assert_eq!(
        serde_json::from_str::<Value>(&body).unwrap()["present"],
        false
    );

    let db = infinidysk_db::Db::connect(&database_url).await.unwrap();
    db.write_config(
        &[("upgrade.state", "\"done\""), ("upgrade.report", "\"r\"")],
        &[],
    )
    .await
    .unwrap();
    let (_, body) = send(addr, &get).await;
    let view: Value = serde_json::from_str(&body).unwrap();
    assert_eq!(view["imported"], true);
    assert_eq!(view["present"], true);
    assert!(view["in_use"].as_str().unwrap().contains("skipped"));
    assert!(view["scan"].is_null());

    db.write_config(
        &[
            ("upgrade.download_ids", "true"),
            ("upgrade.skipped_retried.5", "true"),
        ],
        &[],
    )
    .await
    .unwrap();
    db.close().await;
    let scan = admin_request("POST", "/api/v1/upgrade/leftovers/scan", "");
    let (code, body) = send(addr, &scan).await;
    assert_eq!(code, "HTTP/1.1 200 OK");
    let view: Value = serde_json::from_str(&body).unwrap();
    assert!(view["in_use"].is_null());
    assert_eq!(view["scan"]["gone"], false);
    assert!(view["scan"]["bytes"].as_u64().unwrap() > 0);

    // Dismissing the notice leaves the import report's own notice alone.
    let dismiss = admin_request("POST", "/api/v1/upgrade/leftovers/dismiss", "");
    assert_eq!(status(addr, &dismiss).await, "HTTP/1.1 204 No Content");
    let (_, body) = send(addr, &get).await;
    assert_eq!(
        serde_json::from_str::<Value>(&body).unwrap()["dismissed"],
        true
    );
    let report = admin_request("GET", "/api/v1/upgrade/report", "");
    let (_, body) = send(addr, &report).await;
    assert_eq!(serde_json::from_str::<Value>(&body).unwrap()["seen"], false);

    // A library folder inside `blobs` makes it 2.x data.
    let library = config.join("blobs/library");
    std::fs::create_dir_all(&library).unwrap();
    let (_, body) = send(addr, &admin_request("GET", "/api/v1/settings", "")).await;
    let mut settings = settings_from_view(&body);
    settings["imports"]["library_dirs"] = json!([{ "path": library, "arr_path": "" }]);
    let put = admin_request("PUT", "/api/v1/settings", &settings.to_string());
    assert_eq!(status(addr, &put).await, "HTTP/1.1 200 OK");
    let (_, body) = send(addr, &get).await;
    let view: Value = serde_json::from_str(&body).unwrap();
    let blobs = view["scan"]["items"]
        .as_array()
        .unwrap()
        .iter()
        .find(|i| i["path"].as_str().unwrap().ends_with("/blobs"))
        .unwrap();
    assert_eq!(blobs["status"], "not_assessed");
    assert_eq!(blobs["reason"], "live_data");

    for (method, path) in [
        ("GET", "/api/v1/upgrade/leftovers"),
        ("POST", "/api/v1/upgrade/leftovers/scan"),
        ("POST", "/api/v1/upgrade/leftovers/dismiss"),
    ] {
        let anonymous = format!(
            "{method} {path} HTTP/1.1\r\nHost: x\r\nConnection: close\r\nContent-Length: 0\r\n\r\n"
        );
        assert_eq!(
            status(addr, &anonymous).await,
            "HTTP/1.1 401 Unauthorized",
            "{method} {path}"
        );
    }
    cancel.cancel();
    server.await.unwrap().unwrap();
}

/// The boot file, the default disk cache and the shown SQLite file use `Boot::home`.
#[tokio::test]
async fn keeps_the_boot_file_and_disk_cache_in_the_config_folder() {
    use infinidysk::database::{BootFile, Migration, MigrationState, Source};
    let home = std::env::temp_dir().join(format!("infinidysk-home-{}", ItemId::new()));
    std::fs::create_dir_all(&home).unwrap();
    let sqlite = home.join("infinidysk.sqlite");
    // A move to PostgreSQL left from before a restart; its target never answers.
    BootFile {
        url: None,
        migration: Some(Migration {
            target: "postgres://u:p@127.0.0.1:1/x".to_owned(),
            state: MigrationState::Copying,
            schema_version: 0,
        }),
    }
    .save(&home, (0, 0))
    .unwrap();
    let listener = TcpListener::bind("127.0.0.1:0").await.unwrap();
    let addr = listener.local_addr().unwrap();
    let boot = Boot {
        listen: addr,
        database_url: format!("sqlite:{}", sqlite.display()),
        database_source: Source::Default,
        uid: 0,
        gid: 0,
        trace_minutes: 0,
        metrics_path: home.join("metrics.sqlite"),
        log_dir: home.join("logs"),
        home: home.clone(),
        config_dir: home.clone(),
        ffprobe: "ffprobe".to_owned(),
    };
    let vars = [
        ("INFINIDYSK__WEBDAV__PASSWORD".to_owned(), "pw".to_owned()),
        (
            "INFINIDYSK__AUTH__API_KEY".to_owned(),
            "adminkey".to_owned(),
        ),
        ("INFINIDYSK__SAB__API_KEY".to_owned(), "sabkey".to_owned()),
        (
            "INFINIDYSK__PERFORMANCE__DISK_CACHE_ENABLED".to_owned(),
            "true".to_owned(),
        ),
    ];
    let cancel = CancellationToken::new();
    let server = tokio::spawn({
        let cancel = cancel.clone();
        async move { serve(listener, &boot, vars, cancel).await }
    });

    let (code, body) = send(addr, &admin_request("GET", "/api/v1/database", "")).await;
    assert_eq!(code, "HTTP/1.1 200 OK");
    let database: Value = serde_json::from_str(&body).unwrap();
    assert_eq!(database["migration"]["state"], "copying");

    let (_, body) = send(addr, &admin_request("GET", "/api/v1/status", "")).await;
    let status_body: Value = serde_json::from_str(&body).unwrap();
    // The running cache reports its folder resolved (`/var` is `/private/var` on macOS).
    assert_eq!(
        status_body["disk_cache"]["dir"],
        std::fs::canonicalize(home.join("cache"))
            .unwrap()
            .display()
            .to_string()
    );
    assert_eq!(database["sqlite_path"], sqlite.display().to_string());

    let (_, body) = send(addr, &admin_request("GET", "/api/v1/performance", "")).await;
    let performance: Value = serde_json::from_str(&body).unwrap();
    let cache = home.join("cache").display().to_string();
    assert_eq!(performance["effective"]["disk_cache_dir"], cache);
    for preset in performance["presets"].as_array().unwrap() {
        assert_eq!(preset["tuning"]["disk_cache_dir"], cache);
    }

    // A mount over the config folder would hide its files, even with the disk cache elsewhere.
    let (_, body) = send(addr, &admin_request("GET", "/api/v1/settings", "")).await;
    let mut settings = settings_from_view(&body);
    settings["mount"]["dir"] = home.display().to_string().into();
    settings["performance"]["disk_cache_dir"] = std::env::temp_dir()
        .join("infinidysk-elsewhere")
        .display()
        .to_string()
        .into();
    let put = admin_request("PUT", "/api/v1/settings", &settings.to_string());
    let (code, body) = send(addr, &put).await;
    assert_eq!(code, "HTTP/1.1 400 Bad Request");
    assert_eq!(
        serde_json::from_str::<Value>(&body).unwrap()["path"],
        "mount.dir"
    );

    let cancel_move = admin_request("DELETE", "/api/v1/database/migration", "");
    assert_eq!(status(addr, &cancel_move).await, "HTTP/1.1 200 OK");
    assert!(BootFile::load(&home).unwrap().migration.is_none());

    cancel.cancel();
    server.await.unwrap().unwrap();
}

/// The real binary with only `PATH`, `HOME` and `TMPDIR` from this process, so a developer's
/// `INFINIDYSK_*` or 1.x variables never reach it.
fn binary() -> tokio::process::Command {
    let mut command = tokio::process::Command::new(env!("CARGO_BIN_EXE_infinidysk"));
    command
        .env_clear()
        .env("RUST_LOG", "info")
        .kill_on_drop(true);
    for key in ["PATH", "HOME", "TMPDIR"] {
        if let Some(value) = std::env::var_os(key) {
            command.env(key, value);
        }
    }
    command
}

/// A config folder or saved settings that cannot be used stop startup with one warning,
/// even with backtraces on.
#[tokio::test]
async fn an_unusable_config_dir_stops_startup_with_one_warning() {
    let root = std::env::temp_dir().join(format!("infinidysk-config-dir-{}", ItemId::new()));
    let home = root.join("home");
    std::fs::create_dir_all(&home).unwrap();
    let file = root.join("file");
    std::fs::write(&file, b"").unwrap();
    let home_text = home.display().to_string();
    let cases = [
        (file.display().to_string(), None, "creating"),
        ("relative".to_owned(), None, "absolute path"),
        // A mount over the config folder would hide its files.
        (
            home_text.clone(),
            Some(home_text.clone()),
            "must not contain the config folder",
        ),
    ];
    for (dir, mount, reason) in cases {
        let mut command = binary();
        command
            .env("INFINIDYSK_CONFIG_DIR", &dir)
            .env("INFINIDYSK_LISTEN", "127.0.0.1:0")
            .env("RUST_BACKTRACE", "1")
            .env("NO_COLOR", "1");
        if let Some(mount) = &mount {
            command.env("INFINIDYSK__MOUNT__DIR", mount);
        }
        let output = tokio::time::timeout(Duration::from_secs(60), command.output())
            .await
            .expect("startup did not stop")
            .unwrap();
        let printed = format!(
            "{}{}",
            String::from_utf8_lossy(&output.stdout),
            String::from_utf8_lossy(&output.stderr)
        );
        assert_eq!(output.status.code(), Some(1), "{dir}: {printed}");
        let warnings: Vec<&str> = printed.lines().filter(|l| l.contains(" WARN ")).collect();
        assert_eq!(warnings.len(), 1, "{dir}: {printed}");
        assert!(
            warnings[0].contains("reason=") && warnings[0].contains(reason),
            "{dir}: {printed}"
        );
        assert!(
            !printed.contains("Error:") && !printed.contains("backtrace"),
            "{dir}: {printed}"
        );
    }
    std::fs::remove_dir_all(&root).unwrap();
}

/// `INFINIDYSK_CONFIG_DIR` decides where the real binary makes its database and logs, and
/// where it looks for a 1.x install to import.
#[tokio::test]
async fn the_binary_keeps_its_files_in_the_config_dir() {
    let root = std::env::temp_dir().join(format!("infinidysk-config-dir-{}", ItemId::new()));
    let home = root.join("home");
    std::fs::create_dir_all(&home).unwrap();
    // A 1.x install the importer finds and cannot read.
    std::fs::write(home.join("db.sqlite"), b"not a database").unwrap();
    let port = TcpListener::bind("127.0.0.1:0")
        .await
        .unwrap()
        .local_addr()
        .unwrap()
        .port();
    let addr = SocketAddr::from(([127, 0, 0, 1], port));
    let mut child = binary()
        .env("INFINIDYSK_CONFIG_DIR", &home)
        .env("INFINIDYSK_LISTEN", addr.to_string())
        .env("INFINIDYSK__MOUNT__DIR", root.join("mnt"))
        .stdout(Stdio::null())
        .stderr(Stdio::null())
        .spawn()
        .unwrap();
    let failed = tokio::time::timeout(Duration::from_secs(60), async {
        while TcpStream::connect(addr).await.is_err() {
            tokio::time::sleep(Duration::from_millis(100)).await;
        }
        wait_for_failed_import(addr).await
    })
    .await
    .expect("the server did not answer");
    assert_eq!(failed["state"], "failed");
    assert!(home.join("infinidysk.sqlite").is_file());
    assert!(home.join("logs").is_dir());
    child.kill().await.unwrap();
    std::fs::remove_dir_all(&root).unwrap();
}

#[tokio::test]
async fn healthz_answers_without_sign_in_and_the_probe_reads_it() {
    let (addr, cancel, server, _) = start().await;
    let get = "GET /healthz HTTP/1.1\r\nHost: x\r\nConnection: close\r\n\r\n";
    let (code, body) = send(addr, get).await;
    let report: Value = serde_json::from_str(&body).unwrap();
    assert_eq!(report["checks"]["database"], "pass");
    // The switch status answers without sign-in; starting a switch does not.
    let cutover = "GET /api/v1/database/cutover HTTP/1.1\r\nHost: x\r\nConnection: close\r\n\r\n";
    let (cutover_code, cutover_body) = send(addr, cutover).await;
    assert_eq!(cutover_code, "HTTP/1.1 200 OK");
    let cutover_status: Value = serde_json::from_str(&cutover_body).unwrap();
    assert_eq!(cutover_status["state"], "none");
    let start = "POST /api/v1/database/cutover HTTP/1.1\r\nHost: x\r\nConnection: close\r\nContent-Length: 0\r\n\r\n";
    assert_eq!(send(addr, start).await.0, "HTTP/1.1 401 Unauthorized");
    assert_eq!(report["checks"]["queue"], "pass");
    assert_eq!(code == "HTTP/1.1 200 OK", report["status"] == "healthy");
    let probed = infinidysk::healthcheck::probe(addr).await;
    assert_eq!(probed.is_ok(), report["status"] == "healthy", "{probed:?}");
    // Without FUSE the mount does not count.
    #[cfg(not(target_os = "linux"))]
    {
        assert_eq!(report["checks"]["mount"], "off");
        assert_eq!(code, "HTTP/1.1 200 OK");
        assert_eq!(probed.unwrap(), body);
    }
    cancel.cancel();
    server.await.unwrap().unwrap();
    assert!(infinidysk::healthcheck::probe(addr).await.is_err());
}

#[tokio::test]
async fn serves_every_api_on_one_port_and_applies_settings_live() {
    let (addr, cancel, server, database_url) = start().await;

    let get = "GET /api?mode=version&output=json HTTP/1.1\r\nHost: x\r\nConnection: close\r\n\r\n";
    assert_eq!(status(addr, get).await, "HTTP/1.1 200 OK");
    let propfind = "PROPFIND / HTTP/1.1\r\nHost: x\r\nDepth: 0\r\nConnection: close\r\n";
    assert_eq!(
        status(addr, &format!("{propfind}\r\n")).await,
        "HTTP/1.1 401 Unauthorized"
    );
    // admin:pw
    let admin_pw = format!("{propfind}Authorization: Basic YWRtaW46cHc=\r\n\r\n");
    assert_eq!(status(addr, &admin_pw).await, "HTTP/1.1 207 Multi-Status");

    // Browsers get the UI from any path the APIs and WebDAV do not own, client routes included.
    for page in ["/", "/queue"] {
        let get = format!("GET {page} HTTP/1.1\r\nHost: x\r\nConnection: close\r\n\r\n");
        let (code, body) = send(addr, &get).await;
        assert_eq!(code, "HTTP/1.1 200 OK");
        assert!(body.starts_with("<!doctype html>"), "{body}");
    }
    let missing = "GET /assets/x.js HTTP/1.1\r\nHost: x\r\nConnection: close\r\n\r\n";
    assert_eq!(status(addr, missing).await, "HTTP/1.1 404 Not Found");
    let dav = "GET /content HTTP/1.1\r\nHost: x\r\nConnection: close\r\n\r\n";
    assert_eq!(status(addr, dav).await, "HTTP/1.1 401 Unauthorized");

    let spec = "GET /api/v1/openapi.json HTTP/1.1\r\nHost: x\r\nConnection: close\r\n\r\n";
    let (code, body) = send(addr, spec).await;
    assert_eq!(code, "HTTP/1.1 200 OK");
    let spec: Value = serde_json::from_str(&body).unwrap();
    assert!(spec["paths"]["/api/v1/settings"]["put"].is_object());
    let anonymous = "GET /api/v1/settings HTTP/1.1\r\nHost: x\r\nConnection: close\r\n\r\n";
    assert_eq!(status(addr, anonymous).await, "HTTP/1.1 401 Unauthorized");

    // Renaming the WebDAV user applies at once; the masked password round-trips unchanged.
    let (code, body) = send(addr, &admin_request("GET", "/api/v1/settings", "")).await;
    assert_eq!(code, "HTTP/1.1 200 OK");
    let mut settings = settings_from_view(&body);
    assert_eq!(settings["webdav"]["password"], "********");
    settings["webdav"]["user"] = "dav".into();
    settings["network"]["trusted_proxies"] = serde_json::json!(["172.18.0.0/16"]);
    let put = admin_request("PUT", "/api/v1/settings", &settings.to_string());
    assert_eq!(status(addr, &put).await, "HTTP/1.1 200 OK");
    let (_, body) = send(addr, &admin_request("GET", "/api/v1/settings", "")).await;
    assert_eq!(
        serde_json::from_str::<Value>(&body).unwrap()["settings"]["network"]["trusted_proxies"],
        serde_json::json!(["172.18.0.0/16"])
    );
    assert_eq!(status(addr, &admin_pw).await, "HTTP/1.1 401 Unauthorized");
    // dav:pw
    let dav_pw = format!("{propfind}Authorization: Basic ZGF2OnB3\r\n\r\n");
    assert_eq!(status(addr, &dav_pw).await, "HTTP/1.1 207 Multi-Status");

    // A refused setting names its path, so the UI can show the error on its field.
    let mut relative = settings.clone();
    relative["mount"]["dir"] = "mnt".into();
    let put = admin_request("PUT", "/api/v1/settings", &relative.to_string());
    let (code, body) = send(addr, &put).await;
    assert_eq!(code, "HTTP/1.1 400 Bad Request");
    assert_eq!(
        serde_json::from_str::<Value>(&body).unwrap()["path"],
        "mount.dir"
    );

    // Keys can be revealed; passwords cannot.
    let (code, body) = send(
        addr,
        &admin_request("GET", "/api/v1/settings/keys/sab.api_key", ""),
    )
    .await;
    assert_eq!(code, "HTTP/1.1 200 OK");
    assert_eq!(
        serde_json::from_str::<Value>(&body).unwrap()["value"],
        "sabkey"
    );
    let password = admin_request("GET", "/api/v1/settings/keys/webdav.password", "");
    assert_eq!(status(addr, &password).await, "HTTP/1.1 404 Not Found");

    // Tracing turns on for a while and off again; nonsense is refused.
    let (code, body) = send(
        addr,
        &admin_request("PUT", "/api/v1/tracing", r#"{"minutes":15}"#),
    )
    .await;
    assert_eq!(code, "HTTP/1.1 200 OK");
    assert!(serde_json::from_str::<Value>(&body).unwrap()["until"].is_i64());
    let too_long = admin_request("PUT", "/api/v1/tracing", r#"{"minutes":100000}"#);
    assert_eq!(status(addr, &too_long).await, "HTTP/1.1 400 Bad Request");
    let off = admin_request("PUT", "/api/v1/tracing", r#"{"minutes":0}"#);
    assert_eq!(status(addr, &off).await, "HTTP/1.1 200 OK");
    let (_, body) = send(addr, &admin_request("GET", "/api/v1/tracing", "")).await;
    assert!(serde_json::from_str::<Value>(&body).unwrap()["until"].is_null());
    let missing = admin_request("GET", "/api/v1/traces/1", "");
    assert_eq!(status(addr, &missing).await, "HTTP/1.1 404 Not Found");

    // Adding an NZB announces a queue change on the event stream.
    let mut events = TcpStream::connect(addr).await.unwrap();
    let subscribe = admin_request("GET", "/api/v1/events", "");
    events.write_all(subscribe.as_bytes()).await.unwrap();
    let mut seen = Vec::new();
    let mut buf = [0; 4096];
    while !String::from_utf8_lossy(&seen).contains("text/event-stream") {
        let n = events.read(&mut buf).await.unwrap();
        seen.extend_from_slice(&buf[..n]);
    }
    let multipart = format!(
        "--b\r\nContent-Disposition: form-data; name=\"nzbfile\"; filename=\"job.nzb\"\r\n\r\n{NZB}\r\n--b--\r\n"
    );
    let add = format!(
        "POST /api?mode=addfile&apikey=sabkey HTTP/1.1\r\nHost: x\r\nConnection: close\r\n\
         Content-Type: multipart/form-data; boundary=b\r\nContent-Length: {}\r\n\r\n{multipart}",
        multipart.len()
    );
    assert_eq!(status(addr, &add).await, "HTTP/1.1 200 OK");
    while !String::from_utf8_lossy(&seen).contains("event: queue") {
        let n = events.read(&mut buf).await.unwrap();
        assert_ne!(n, 0, "stream ended early");
        seen.extend_from_slice(&buf[..n]);
    }

    // The UI uploads into a configured category; unknown ones are refused.
    for (category, expected) in [
        ("tv", "HTTP/1.1 201 Created"),
        ("nope", "HTTP/1.1 400 Bad Request"),
    ] {
        let form = format!(
            "--b\r\nContent-Disposition: form-data; name=\"category\"\r\n\r\n{category}\r\n\
             --b\r\nContent-Disposition: form-data; name=\"nzb\"; filename=\"up.nzb\"\r\n\r\n{NZB}\r\n--b--\r\n"
        );
        let upload = format!(
            "POST /api/v1/queue HTTP/1.1\r\nHost: x\r\nConnection: close\r\nX-Api-Key: adminkey\r\n\
             Content-Type: multipart/form-data; boundary=b\r\nContent-Length: {}\r\n\r\n{form}",
            form.len()
        );
        assert_eq!(status(addr, &upload).await, expected, "{category}");
    }

    // The explorer lists the whole tree and keeps /content itself.
    let (code, body) = send(addr, &admin_request("GET", "/api/v1/files?path=/", "")).await;
    assert_eq!(code, "HTTP/1.1 200 OK");
    assert_eq!(serde_json::from_str::<Value>(&body).unwrap()["total"], 5);
    let flat = admin_request(
        "GET",
        "/api/v1/files?health=unchecked&sort=size&order=desc",
        "",
    );
    assert_eq!(status(addr, &flat).await, "HTTP/1.1 200 OK");
    let missing = admin_request("GET", "/api/v1/files?path=/content/nope", "");
    assert_eq!(status(addr, &missing).await, "HTTP/1.1 404 Not Found");

    // It keeps /content, offered categories, and other category folders until no files
    // are left in them.
    let db = infinidysk_db::Db::connect(&database_url).await.unwrap();
    let content = db.item(ItemId::CONTENT).await.unwrap().unwrap();
    let tv = db.item_by_path("/content/tv").await.unwrap().unwrap();
    let emptied = Item::child(&content, "gone", ItemKind::Directory).unwrap();
    let job = Item::child(&emptied, "job", ItemKind::Directory).unwrap();
    let full = Item::child(&content, "kept", ItemKind::Directory).unwrap();
    let file = Item::child(&full, "e.mkv", ItemKind::NzbFile).unwrap();
    db.insert(&[emptied.clone(), job, full.clone(), file], &[])
        .await
        .unwrap();
    db.close().await;
    let refused = "HTTP/1.1 400 Bad Request";
    for (id, kept, removed) in [
        (content.id, json!("root"), refused),
        (tv.id, json!("configured"), refused),
        (full.id, json!("not_empty"), refused),
        (emptied.id, Value::Null, "HTTP/1.1 204 No Content"),
    ] {
        let removal = admin_request("GET", &format!("/api/v1/files/{id}/removal"), "");
        let (_, body) = send(addr, &removal).await;
        assert_eq!(serde_json::from_str::<Value>(&body).unwrap()["kept"], kept);
        let remove = admin_request("DELETE", &format!("/api/v1/files/{id}"), "");
        assert_eq!(status(addr, &remove).await, removed, "{id}");
    }

    // A signed link opens its one path without the download key.
    let link = admin_request("POST", "/api/v1/links", r#"{"path":"/README"}"#);
    let (_, body) = send(addr, &link).await;
    let url = serde_json::from_str::<Value>(&body).unwrap()["url"]
        .as_str()
        .unwrap()
        .to_owned();
    let view = |url: &str| format!("GET {url} HTTP/1.1\r\nHost: x\r\nConnection: close\r\n\r\n");
    assert_eq!(status(addr, &view(&url)).await, "HTTP/1.1 200 OK");
    let other = url.replace("/view/README", "/view/content");
    assert_eq!(
        status(addr, &view(&other)).await,
        "HTTP/1.1 401 Unauthorized"
    );

    // Shutdown ends the open stream instead of waiting out the drain timeout.
    let started = std::time::Instant::now();
    cancel.cancel();
    server.await.unwrap().unwrap();
    assert!(started.elapsed() < std::time::Duration::from_secs(3));
}

#[tokio::test]
async fn support_pack_holds_diagnostics_without_secrets() {
    use std::io::Read as _;

    let (addr, cancel, server, _) = start().await;
    let (code, created) = send(
        addr,
        &admin_request(
            "POST",
            "/api/v1/indexers",
            &json!({
                "name": "mock-support-indexer",
                "base_url": "https://indexer.example/api",
                "api_key": "mock-indexer-key",
                "enabled": true
            })
            .to_string(),
        ),
    )
    .await;
    assert_eq!(code, "HTTP/1.1 200 OK");
    assert!(!created.contains("mock-indexer-key"));
    let body =
        r#"{"note":"sabkey leaked, adminkey too; mock-indexer-key too","include_traces":true}"#;
    let mut stream = TcpStream::connect(addr).await.unwrap();
    let request = admin_request("POST", "/api/v1/support-pack", body);
    stream.write_all(request.as_bytes()).await.unwrap();
    let mut response = Vec::new();
    stream.read_to_end(&mut response).await.unwrap();
    let split = response.windows(4).position(|w| w == b"\r\n\r\n").unwrap();
    let head = String::from_utf8_lossy(&response[..split]);
    assert!(head.starts_with("HTTP/1.1 200 OK"), "{head}");
    assert!(head.contains("application/zip"));

    let mut zip = zip::ZipArchive::new(std::io::Cursor::new(&response[split + 4..])).unwrap();
    let mut all = String::new();
    for i in 0..zip.len() {
        zip.by_index(i).unwrap().read_to_string(&mut all).unwrap();
    }
    assert!(
        !all.contains("sabkey") && !all.contains("adminkey") && !all.contains("mock-indexer-key")
    );
    let mut note = String::new();
    zip.by_name("NOTE.txt")
        .unwrap()
        .read_to_string(&mut note)
        .unwrap();
    assert_eq!(note, "[REDACTED] leaked, [REDACTED] too; [REDACTED] too\n");
    for name in [
        "README.txt",
        "manifest.json",
        "settings.json",
        "system.json",
        "status/queue.json",
        "logs/recent.jsonl",
        "traces/index.json",
    ] {
        assert!(zip.by_name(name).is_ok(), "{name}");
    }

    let long = format!(r#"{{"note":"{}"}}"#, "a".repeat(10_001));
    let request = admin_request("POST", "/api/v1/support-pack", &long);
    assert_eq!(status(addr, &request).await, "HTTP/1.1 400 Bad Request");
    cancel.cancel();
    server.await.unwrap().unwrap();
}

/// Refresh commands the fake Sonarr received.
static REFRESHES: AtomicUsize = AtomicUsize::new(0);
/// Webhooks the fake Arrs were asked to save: (path, body).
static HOOKS: Mutex<Vec<(String, Value)>> = Mutex::new(Vec::new());
/// The fake Sonarr's queue records, and the removals it was asked for (path and query).
static QUEUE: Mutex<Vec<Value>> = Mutex::new(Vec::new());
static REMOVED: Mutex<Vec<String>> = Mutex::new(Vec::new());

/// The distinct values of a webhook's `on*` event flags.
fn events(hook: &Value) -> std::collections::BTreeSet<bool> {
    let flags = hook.as_object().unwrap().iter();
    flags
        .filter(|(k, _)| k.starts_with("on"))
        .map(|(_, v)| v.as_bool().unwrap())
        .collect()
}

/// Answers like a Sonarr under `/sonarr` holding two masked SAB clients, and a Chaptarr
/// under `/chaptarr` holding two unmasked ones; `app` is our server.
async fn fake_arr(State(app): State<SocketAddr>, req: Request) -> Json<Value> {
    let sab = |id: i64, field: &str, category: &str, key: &str| {
        json!({
            "id": id, "name": format!("sab {id}"), "enable": true, "implementation": "Sabnzbd",
            "fields": [
                { "name": "host", "value": "127.0.0.1" },
                { "name": "port", "value": app.port() },
                { "name": "useSsl", "value": false },
                { "name": "urlBase", "value": "/sab" },
                { "name": "apiKey", "value": key },
                { "name": field, "value": category },
            ],
        })
    };
    let path = req.uri().path().to_owned();
    Json(match path.as_str() {
        // Sonarr v4: only the v3 API.
        "/sonarr/api" => json!({ "current": "v3", "deprecated": [] }),
        "/sonarr/api/v3/system/status" => json!({ "appName": "Sonarr", "version": "4.0.0" }),
        "/sonarr/api/v3/downloadclient" => json!([
            sab(1, "tvCategory", "TV", "********"),
            sab(2, "tvCategory", "anime", "********"),
            { "id": 3, "name": "torrents", "enable": true, "implementation": "QBittorrent", "fields": [] },
        ]),
        "/sonarr/api/v3/downloadclient/1" => sab(1, "tvCategory", "TV", "********"),
        "/sonarr/api/v3/downloadclient/2" => sab(2, "tvCategory", "anime", "********"),
        "/sonarr/api/v3/downloadclient/test" => {
            let body = axum::body::to_bytes(req.into_body(), usize::MAX)
                .await
                .unwrap();
            let client: Value = serde_json::from_slice(&body).unwrap();
            let fields = client["fields"].as_array().unwrap();
            let base = fields.iter().find(|f| f["name"] == "urlBase").unwrap()["value"]
                .as_str()
                .unwrap()
                .to_owned();
            // Client 1 holds our key; client 2 is another SABnzbd that answers here.
            let key = if client["id"] == 1 { "sabkey" } else { "wrong" };
            reqwest::get(format!(
                "http://{app}{base}/api?mode=get_config&apikey={key}"
            ))
            .await
            .unwrap();
            json!({})
        }
        "/chaptarr/api/v1/system/status" => json!({ "appName": "Chaptarr", "version": "0.9.0" }),
        "/sonarr/api/v3/notification" | "/chaptarr/api/v1/notification"
            if req.method() == axum::http::Method::GET =>
        {
            json!([])
        }
        "/sonarr/api/v3/notification" | "/chaptarr/api/v1/notification" => {
            assert_eq!(req.method(), axum::http::Method::POST);
            assert_eq!(req.uri().query(), Some("forceSave=true"));
            let body = axum::body::to_bytes(req.into_body(), usize::MAX)
                .await
                .unwrap();
            let hook: Value = serde_json::from_slice(&body).unwrap();
            // Any event left on enables it, and the Arr tests an enabled create.
            assert_eq!(events(&hook), [false].into());
            json!({ "id": 9 })
        }
        "/sonarr/api/v3/notification/9" | "/chaptarr/api/v1/notification/9" => {
            assert_eq!(req.method(), axum::http::Method::PUT);
            assert_eq!(req.uri().query(), Some("forceSave=true"));
            let body = axum::body::to_bytes(req.into_body(), usize::MAX)
                .await
                .unwrap();
            let hook: Value = serde_json::from_slice(&body).unwrap();
            assert_eq!(hook["id"], 9);
            assert_eq!(events(&hook), [true].into());
            HOOKS.lock().unwrap().push((path.clone(), hook));
            json!({})
        }
        "/sonarr/api/v3/command" => {
            let body = axum::body::to_bytes(req.into_body(), usize::MAX)
                .await
                .unwrap();
            let command: Value = serde_json::from_slice(&body).unwrap();
            assert_eq!(command["name"], "RefreshMonitoredDownloads");
            REFRESHES.fetch_add(1, Ordering::SeqCst);
            json!({})
        }
        "/chaptarr/api/v1/downloadclient" => {
            let mut books = sab(1, "ebookCategory", "books", "sabkey");
            books["fields"]
                .as_array_mut()
                .unwrap()
                .push(json!({ "name": "audiobookCategory", "value": "audiobooks" }));
            json!([books, sab(2, "audiobookCategory", "other", "otherkey")])
        }
        "/sonarr/api/v3/queue" => json!({ "records": QUEUE.lock().unwrap().clone() }),
        "/chaptarr/api/v1/queue" => json!({ "records": [] }),
        removed if removed.starts_with("/sonarr/api/v3/queue/") => {
            assert_eq!(req.method(), axum::http::Method::DELETE);
            let query = req.uri().query().unwrap_or_default();
            REMOVED.lock().unwrap().push(format!("{removed}?{query}"));
            let id: i64 = removed.rsplit('/').next().unwrap().parse().unwrap();
            QUEUE.lock().unwrap().retain(|r| r["id"] != id);
            json!({})
        }
        other => panic!("unexpected call to {other}"),
    })
}

#[tokio::test]
async fn tests_an_arr_connection_before_and_after_saving() {
    let (addr, cancel, server, _) = start().await;
    let arr_listener = TcpListener::bind("127.0.0.1:0").await.unwrap();
    let arr = arr_listener.local_addr().unwrap();
    let fake = axum::Router::new().fallback(fake_arr).with_state(addr);
    tokio::spawn(async move { axum::serve(arr_listener, fake).await });
    let test = |body: Value| admin_request("POST", "/api/v1/arr/test", &body.to_string());
    let id = "0192f0a0-0000-7000-8000-000000000001";
    let url = format!("http://{arr}/sonarr");

    let (code, body) = send(
        addr,
        &test(json!({ "name": "", "profile": "sonarr", "url": url, "api_key": "k" })),
    )
    .await;
    assert_eq!(code, "HTTP/1.1 200 OK");
    assert_eq!(
        serde_json::from_str::<Value>(&body).unwrap()["version"],
        "4.0.0"
    );
    let (_, body) = send(
        addr,
        &test(json!({ "name": "x", "profile": "radarr", "url": url, "api_key": "k" })),
    )
    .await;
    assert_eq!(
        serde_json::from_str::<Value>(&body).unwrap()["error"],
        "it answers as Sonarr, not Radarr"
    );
    let masked =
        json!({ "id": id, "name": "x", "profile": "sonarr", "url": url, "api_key": "********" });
    assert_eq!(
        status(addr, &test(masked.clone())).await,
        "HTTP/1.1 400 Bad Request"
    );

    // Disabled, so discovery leaves it alone.
    let (_, body) = send(addr, &admin_request("GET", "/api/v1/settings", "")).await;
    let mut settings = settings_from_view(&body);
    settings["arr_instances"] = json!([
        { "id": id, "name": "x", "profile": "sonarr", "url": url, "api_key": "k", "enabled": false },
    ]);
    let put = admin_request("PUT", "/api/v1/settings", &settings.to_string());
    assert_eq!(status(addr, &put).await, "HTTP/1.1 200 OK");
    let (_, body) = send(addr, &test(masked)).await;
    assert_eq!(
        serde_json::from_str::<Value>(&body).unwrap()["version"],
        "4.0.0"
    );

    cancel.cancel();
    server.await.unwrap().unwrap();
}

#[tokio::test]
async fn discovers_which_arr_download_clients_are_this_installation() {
    let (addr, cancel, server, database_url) = start().await;
    let arr_listener = TcpListener::bind("127.0.0.1:0").await.unwrap();
    let arr = arr_listener.local_addr().unwrap();
    let fake = axum::Router::new().fallback(fake_arr).with_state(addr);
    tokio::spawn(async move { axum::serve(arr_listener, fake).await });

    let (_, body) = send(addr, &admin_request("GET", "/api/v1/settings", "")).await;
    let mut settings = settings_from_view(&body);
    settings["arr_instances"] = json!([
        { "name": "series", "profile": "sonarr", "url": format!("http://{arr}/sonarr"), "api_key": "k" },
        { "name": "books", "profile": "chaptarr", "url": format!("http://{arr}/chaptarr"), "api_key": "k" },
        { "name": "mislabeled", "profile": "whisparr", "url": format!("http://{arr}/sonarr"), "api_key": "k" },
        { "name": "off", "profile": "radarr", "url": format!("http://{arr}/off"), "api_key": "k", "enabled": false },
    ]);
    let put = admin_request("PUT", "/api/v1/settings", &settings.to_string());
    assert_eq!(status(addr, &put).await, "HTTP/1.1 200 OK");

    let request = admin_request("GET", "/api/v1/arr/status", "");
    let deadline = std::time::Instant::now() + std::time::Duration::from_secs(10);
    let found = loop {
        let (code, body) = send(addr, &request).await;
        assert_eq!(code, "HTTP/1.1 200 OK");
        let found: Value = serde_json::from_str(&body).unwrap();
        let instances = found["instances"].as_array().unwrap();
        if instances.len() == 3
            && instances.iter().all(|i| i["checked_at_ms"].is_i64())
            && instances
                .iter()
                .filter(|i| i["webhook_url"].is_string())
                .count()
                == 2
        {
            break found;
        }
        assert!(std::time::Instant::now() < deadline, "{found}");
        tokio::time::sleep(std::time::Duration::from_millis(50)).await;
    };
    let clients = |name: &str| -> Vec<(String, String)> {
        let instance = found["instances"]
            .as_array()
            .unwrap()
            .iter()
            .find(|i| i["name"] == name)
            .unwrap();
        instance["clients"]
            .as_array()
            .unwrap()
            .iter()
            .map(|c| {
                let categories: Vec<&str> = c["categories"]
                    .as_array()
                    .unwrap()
                    .iter()
                    .map(|c| c.as_str().unwrap())
                    .collect();
                (
                    categories.join(","),
                    c["binding"].as_str().unwrap().to_owned(),
                )
            })
            .collect()
    };
    let pairs = |list: &[(&str, &str)]| -> Vec<(String, String)> {
        list.iter()
            .map(|(a, b)| ((*a).to_owned(), (*b).to_owned()))
            .collect()
    };
    assert_eq!(
        clients("series"),
        pairs(&[("TV", "verified"), ("anime", "unverified")])
    );
    assert_eq!(
        clients("books"),
        pairs(&[("audiobooks,books", "verified"), ("other", "other")])
    );
    assert_eq!(clients("mislabeled"), pairs(&[]));
    let mislabeled = found["instances"]
        .as_array()
        .unwrap()
        .iter()
        .find(|i| i["name"] == "mislabeled")
        .unwrap();
    assert_eq!(mislabeled["error"], "it answers as Sonarr, not Whisparr");
    let categories: Vec<(String, Value)> = found["categories"]
        .as_array()
        .unwrap()
        .iter()
        .map(|c| {
            (
                c["category"].as_str().unwrap().to_owned(),
                c["media"].clone(),
            )
        })
        .collect();
    assert_eq!(
        categories,
        [
            ("audiobooks".to_owned(), json!(["audio"])),
            ("books".to_owned(), json!(["book"])),
            ("tv".to_owned(), json!(["video"])),
        ]
    );
    assert!(!found.to_string().contains("sabkey"));

    // A job finishing in an owned category (here an invalid NZB, failed at once) tells
    // the owning Arr after the coalescing window.
    let multipart = "--b\r\nContent-Disposition: form-data; name=\"nzbfile\"; filename=\"bad.nzb\"\r\n\r\nnot xml\r\n--b--\r\n";
    let add = format!(
        "POST /api?mode=addfile&cat=tv&apikey=sabkey HTTP/1.1\r\nHost: x\r\nConnection: close\r\n\
         Content-Type: multipart/form-data; boundary=b\r\nContent-Length: {}\r\n\r\n{multipart}",
        multipart.len()
    );
    send(addr, &add).await;
    let deadline = std::time::Instant::now() + std::time::Duration::from_secs(15);
    while REFRESHES.load(Ordering::SeqCst) == 0 {
        assert!(std::time::Instant::now() < deadline, "no refresh sent");
        tokio::time::sleep(std::time::Duration::from_millis(100)).await;
    }

    webhooks(addr, &found, &database_url).await;
    queue_management(addr, &database_url).await;

    cancel.cancel();
    server.await.unwrap().unwrap();
}

/// Downloads stuck in an Arr queue for a known reason are removed; others are shown.
async fn queue_management(addr: SocketAddr, database_url: &str) {
    let db = infinidysk_db::Db::connect(database_url).await.unwrap();
    let (sample, unknown) = (JobId::new(), JobId::new());
    for job in [sample, unknown] {
        let history = HistoryEntry {
            id: job,
            created_at: 1,
            file_name: "job.nzb".to_owned(),
            job_name: "job".to_owned(),
            category: "tv".to_owned(),
            status: JobStatus::Completed,
            total_bytes: 1,
            download_seconds: 1,
            fail_message: None,
            download_dir: None,
            nzb_blob: None,
            storage: None,
        };
        db.add_history(&history, &[], &[]).await.unwrap();
    }
    db.close().await;
    let record = |id: i64, job: JobId, client: &str, message: &str| {
        json!({
            "id": id, "title": "release", "downloadId": job.to_string(), "downloadClient": client,
            "status": "completed", "trackedDownloadState": "importBlocked", "episodeId": id,
            "statusMessages": [{ "title": "e.mkv", "messages": [message] }],
        })
    };
    *QUEUE.lock().unwrap() = vec![
        record(7, sample, "sab 1", "Sample"),
        record(8, unknown, "sab 1", "Unknown Series"),
        // A second episode of the same pack repeats the message; it is shown once.
        record(10, unknown, "sab 1", "Unknown Series"),
        // Not our verified client, so left alone.
        record(9, sample, "sab 2", "Sample"),
    ];
    let deadline = std::time::Instant::now() + std::time::Duration::from_secs(25);
    while REMOVED.lock().unwrap().is_empty() {
        assert!(std::time::Instant::now() < deadline, "nothing removed");
        tokio::time::sleep(std::time::Duration::from_millis(100)).await;
    }
    assert_eq!(
        *REMOVED.lock().unwrap(),
        ["/sonarr/api/v3/queue/7?removeFromClient=true&blocklist=true&skipRedownload=false"]
    );
    let issues = loop {
        let (_, body) = send(addr, &admin_request("GET", "/api/v1/arr/status", "")).await;
        let found: Value = serde_json::from_str(&body).unwrap();
        let series = found["instances"]
            .as_array()
            .unwrap()
            .iter()
            .find(|i| i["name"] == "series")
            .unwrap()
            .clone();
        if series["queue_issues"] != json!([]) {
            break series["queue_issues"].clone();
        }
        assert!(std::time::Instant::now() < deadline, "no issues shown");
        tokio::time::sleep(std::time::Duration::from_millis(100)).await;
    };
    assert_eq!(
        issues,
        json!([{ "title": "release", "messages": ["Unknown Series"] }])
    );
}

/// Our webhook is on each Arr using us, and its deliveries track which items it owns.
async fn webhooks(addr: SocketAddr, found: &Value, database_url: &str) {
    let series = found["instances"]
        .as_array()
        .unwrap()
        .iter()
        .find(|i| i["name"] == "series")
        .unwrap();
    let id = series["id"].as_str().unwrap();
    assert_eq!(
        series["webhook_url"],
        format!(
            "http://127.0.0.1:{}/sab/api/v1/arr/{id}/webhook",
            addr.port()
        )
    );
    let hooks = HOOKS.lock().unwrap().clone();
    let paths: Vec<&str> = hooks.iter().map(|(p, _)| p.as_str()).collect();
    assert_eq!(paths.len(), 2, "{paths:?}");
    let (_, hook) = hooks
        .iter()
        .find(|(p, _)| p.starts_with("/sonarr"))
        .unwrap();
    assert!(hook["name"].as_str().unwrap().starts_with("infinidysk-"));
    assert_eq!(
        hook["fields"][3],
        json!({ "name": "password", "value": "sabkey" })
    );

    // A finished job holding one file, written beside the running server.
    let db = infinidysk_db::Db::connect(database_url).await.unwrap();
    let tv = db.item_by_path("/content/tv").await.unwrap().unwrap();
    let dir = Item::child(&tv, "job", ItemKind::Directory).unwrap();
    let file = Item::child(&dir, "e.mkv", ItemKind::NzbFile).unwrap();
    let job = JobId::new();
    let history = HistoryEntry {
        id: job,
        created_at: 1,
        file_name: "job.nzb".to_owned(),
        job_name: "job".to_owned(),
        category: "tv".to_owned(),
        status: JobStatus::Completed,
        total_bytes: 1,
        download_seconds: 1,
        fail_message: None,
        download_dir: Some(dir.id),
        nzb_blob: None,
        storage: None,
    };
    db.add_history(&history, &[dir, file.clone()], &[])
        .await
        .unwrap();

    let deliver = |auth: &str, body: Value| {
        let body = body.to_string();
        format!(
            "POST /api/v1/arr/{id}/webhook HTTP/1.1\r\nHost: x\r\nConnection: close\r\n\
             Authorization: Basic {auth}\r\nContent-Type: application/json\r\n\
             Content-Length: {}\r\n\r\n{body}",
            body.len()
        )
    };
    // infinidysk:sabkey, infinidysk:wrong
    let (ok, wrong) = ("aW5maW5pZHlzazpzYWJrZXk=", "aW5maW5pZHlzazp3cm9uZw==");
    let test = json!({ "eventType": "Test" });
    assert_eq!(
        status(addr, &deliver(wrong, test.clone())).await,
        "HTTP/1.1 401 Unauthorized"
    );
    assert_eq!(status(addr, &deliver(ok, test)).await, "HTTP/1.1 200 OK");
    let imported = json!({
        "eventType": "Download", "downloadId": job.to_string(),
        "episodeFile": { "id": 9, "path": "/lib/S/e.mkv", "sourcePath": "/mnt/tv/job/e.mkv" },
    });
    assert_eq!(
        status(addr, &deliver(ok, imported)).await,
        "HTTP/1.1 200 OK"
    );
    let deadline = std::time::Instant::now() + std::time::Duration::from_secs(10);
    while db.owner_instances(file.id).await.unwrap().is_empty() {
        assert!(std::time::Instant::now() < deadline, "owner not recorded");
        tokio::time::sleep(std::time::Duration::from_millis(50)).await;
    }
    let deleted = json!({
        "eventType": "EpisodeFileDelete",
        "episodeFile": { "id": 9, "path": "/lib/S/e.mkv" },
    });
    assert_eq!(status(addr, &deliver(ok, deleted)).await, "HTTP/1.1 200 OK");
    while db.item(file.id).await.unwrap().is_some() {
        assert!(std::time::Instant::now() < deadline, "item not deleted");
        tokio::time::sleep(std::time::Duration::from_millis(50)).await;
    }

    // A STRM handed off for a video maps back to it by stem.
    let dir = Item::child(&tv, "job2", ItemKind::Directory).unwrap();
    let file = Item::child(&dir, "f.mkv", ItemKind::NzbFile).unwrap();
    let job = JobId::new();
    let history = HistoryEntry {
        id: job,
        job_name: "job2".to_owned(),
        download_dir: Some(dir.id),
        storage: Some("/strm/tv/job2".to_owned()),
        ..history
    };
    db.add_history(&history, &[dir, file.clone()], &[])
        .await
        .unwrap();
    let imported = json!({
        "eventType": "Download", "downloadId": job.to_string(),
        "episodeFile": { "id": 10, "path": "/lib/S/f.strm", "sourcePath": "/data/tv/job2/f.strm" },
    });
    assert_eq!(
        status(addr, &deliver(ok, imported)).await,
        "HTTP/1.1 200 OK"
    );
    while db.owner_instances(file.id).await.unwrap().is_empty() {
        assert!(
            std::time::Instant::now() < deadline,
            "STRM owner not recorded"
        );
        tokio::time::sleep(std::time::Duration::from_millis(50)).await;
    }
    db.close().await;
}

#[tokio::test]
async fn indexer_create_defaults_enabled_and_preserves_explicit_false() {
    let (addr, cancel, server, _) = start().await;

    let (code, schema) = send(addr, &admin_request("GET", "/api/v1/openapi.json", "")).await;
    assert_eq!(code, "HTTP/1.1 200 OK");
    let schema: Value = serde_json::from_str(&schema).unwrap();
    assert_eq!(
        schema["components"]["schemas"]["Indexer"]["properties"]["enabled"]["default"],
        true
    );
    let post = &schema["paths"]["/api/v1/indexers"]["post"];
    assert_eq!(
        post["requestBody"]["content"]["application/json"]["schema"]["$ref"],
        "#/components/schemas/IndexerCreate"
    );
    let create = &schema["components"]["schemas"]["IndexerCreate"];
    let required = create["required"]
        .as_array()
        .expect("create required fields");
    for field in ["name", "base_url", "api_key"] {
        assert!(
            required.iter().any(|value| value == field),
            "{field} must be required"
        );
    }
    for field in ["id", "enabled"] {
        assert!(
            !required.iter().any(|value| value == field),
            "{field} must be optional"
        );
    }
    assert!(create["properties"].get("id").is_none());
    assert_eq!(
        create["properties"]["enabled"]["description"],
        "Defaults to true when omitted."
    );
    assert_eq!(
        post["responses"]["200"]["content"]["application/json"]["schema"]["$ref"],
        "#/components/schemas/IndexerRead"
    );
    assert_eq!(
        schema["paths"]["/api/v1/indexers"]["get"]["responses"]["200"]["content"]["application/json"]
            ["schema"]["items"]["$ref"],
        "#/components/schemas/IndexerRead"
    );
    assert_eq!(
        schema["paths"]["/api/v1/indexers/{id}"]["put"]["requestBody"]["content"]["application/json"]
            ["schema"]["$ref"],
        "#/components/schemas/IndexerUpdate"
    );
    assert_eq!(
        schema["paths"]["/api/v1/indexers/{id}"]["put"]["responses"]["200"]["content"]["application/json"]
            ["schema"]["$ref"],
        "#/components/schemas/IndexerRead"
    );
    assert!(schema["components"]["schemas"]["Indexer"]["properties"]["id"].is_object());
    assert!(schema["components"]["schemas"]["Indexer"]["properties"]["enabled"].is_object());

    let mut ids = Vec::new();
    for (name, enabled, expected) in [
        ("Omitted", None, true),
        ("Explicitly disabled", Some(false), false),
    ] {
        let mut payload = json!({
            "name": name,
            "base_url": "https://indexer.invalid/api",
            "api_key": "mock-secret"
        });
        if let Some(enabled) = enabled {
            payload["enabled"] = json!(enabled);
        }
        let (code, response) = send(
            addr,
            &admin_request("POST", "/api/v1/indexers", &payload.to_string()),
        )
        .await;
        assert_eq!(code, "HTTP/1.1 200 OK");
        assert!(!response.contains("mock-secret"));
        let created: Value = serde_json::from_str(&response).unwrap();
        assert_eq!(created["enabled"], expected);
        assert_eq!(created["api_key"], "********");
        ids.push(created["id"].as_str().unwrap().to_owned());
    }

    let (code, response) = send(addr, &admin_request("GET", "/api/v1/indexers", "")).await;
    assert_eq!(code, "HTTP/1.1 200 OK");
    let saved: Value = serde_json::from_str(&response).unwrap();
    assert_eq!(saved[0]["enabled"], true);
    assert_eq!(saved[1]["enabled"], false);
    assert!(!response.contains("mock-secret"));

    for id in ids {
        assert_eq!(
            status(
                addr,
                &admin_request("DELETE", &format!("/api/v1/indexers/{id}"), "")
            )
            .await,
            "HTTP/1.1 204 No Content"
        );
    }
    cancel.cancel();
    server.await.unwrap().unwrap();
}

#[tokio::test]
async fn search_cache_http_overflow_retains_recent_queueable_results() {
    use std::fmt::Write as _;
    let (addr, cancel, server, _) = start().await;
    let listener = TcpListener::bind("127.0.0.1:0").await.unwrap();
    let mock_addr = listener.local_addr().unwrap();
    let mock = tokio::spawn(async move {
        loop {
            let Ok((mut socket, _)) = listener.accept().await else {
                return;
            };
            let mut buffer = [0; 4096];
            let Ok(length) = socket.read(&mut buffer).await else {
                return;
            };
            let request = String::from_utf8_lossy(&buffer[..length]);
            let line = request.lines().next().unwrap_or("");
            let (status, payload) = if line.contains("t=search") {
                let batch = line
                    .split("q=batch")
                    .nth(1)
                    .unwrap_or("0")
                    .split('&')
                    .next()
                    .unwrap_or("0");
                let mut xml = String::from("<rss><channel>");
                for n in 0..450 {
                    write!(
                        xml,
                        "<item><title>Batch {batch} item {n}</title><enclosure url=\"http://{mock_addr}/nzb/{batch}-{n}\" length=\"100\"/></item>"
                    ).unwrap();
                }
                xml.push_str("</channel></rss>");
                ("200 OK", xml)
            } else if line.starts_with("GET /nzb/") {
                ("200 OK", NZB.to_owned())
            } else {
                ("404 Not Found", String::new())
            };
            let response = format!(
                "HTTP/1.1 {status}\r\nContent-Length: {}\r\nConnection: close\r\n\r\n{payload}",
                payload.len()
            );
            socket.write_all(response.as_bytes()).await.unwrap();
        }
    });
    let (code, _) = send(
        addr,
        &admin_request(
            "POST",
            "/api/v1/indexers",
            &json!({
                "name": "bulk mock", "base_url": format!("http://{mock_addr}"),
                "api_key": "mock-secret", "enabled": true
            })
            .to_string(),
        ),
    )
    .await;
    assert_eq!(code, "HTTP/1.1 200 OK");

    let mut oldest = String::new();
    let mut retained = String::new();
    let mut newest = String::new();
    for batch in 0..25 {
        let path = format!("/api/v1/indexers/search?q=batch{batch}&limit=500");
        let (code, response) = send(addr, &admin_request("GET", &path, "")).await;
        assert_eq!(code, "HTTP/1.1 200 OK", "batch {batch}");
        assert!(!response.contains("mock-secret"));
        let result: Value = serde_json::from_str(&response).unwrap();
        let rows = result["results"].as_array().unwrap();
        assert_eq!(rows.len(), 450);
        assert_eq!(rows[0]["can_queue"], true);
        if batch == 0 {
            oldest = rows[0]["nzb_ref"].as_str().unwrap().to_owned();
        }
        if batch == 5 {
            retained = rows[0]["nzb_ref"].as_str().unwrap().to_owned();
        }
        if batch == 24 {
            newest = rows[449]["nzb_ref"].as_str().unwrap().to_owned();
        }
    }
    let payload = |reference: &str| {
        json!({
            "nzb_ref": reference, "category": "movies"
        })
        .to_string()
    };
    assert_eq!(
        status(
            addr,
            &admin_request("POST", "/api/v1/indexers/search/queue", &payload(&oldest))
        )
        .await,
        "HTTP/1.1 404 Not Found"
    );
    for (reference, expected) in [
        (&retained, "HTTP/1.1 201 Created"),
        (&newest, "HTTP/1.1 200 OK"),
    ] {
        let (code, queued) = send(
            addr,
            &admin_request("POST", "/api/v1/indexers/search/queue", &payload(reference)),
        )
        .await;
        assert_eq!(code, expected, "{queued}");
    }
    mock.abort();
    cancel.cancel();
    server.await.unwrap().unwrap();
}

#[tokio::test]
async fn two_indexer_nzb_downloads_can_run_in_parallel() {
    let (addr, cancel, server, _) = start().await;
    let listener = TcpListener::bind("127.0.0.1:0").await.unwrap();
    let mock_addr = listener.local_addr().unwrap();
    let active = Arc::new(AtomicUsize::new(0));
    let peak = Arc::new(AtomicUsize::new(0));
    let mock_active = Arc::clone(&active);
    let mock_peak = Arc::clone(&peak);
    let mock = tokio::spawn(async move {
        while let Ok((mut socket, _)) = listener.accept().await {
            let active = Arc::clone(&mock_active);
            let peak = Arc::clone(&mock_peak);
            tokio::spawn(async move {
                let mut buf = [0u8; 4096];
                let length = socket.read(&mut buf).await.unwrap();
                let request = String::from_utf8_lossy(&buf[..length]);
                let (status, body) = if request.contains("t=search") {
                    (
                        "200 OK",
                        format!(
                            "<rss><channel><item><title>First</title><link>http://{mock_addr}/nzb/1</link></item><item><title>Second</title><link>http://{mock_addr}/nzb/2</link></item></channel></rss>"
                        ),
                    )
                } else if request.starts_with("GET /nzb/") {
                    let count = active.fetch_add(1, Ordering::SeqCst) + 1;
                    peak.fetch_max(count, Ordering::SeqCst);
                    tokio::time::sleep(Duration::from_millis(150)).await;
                    active.fetch_sub(1, Ordering::SeqCst);
                    ("200 OK", NZB.to_owned())
                } else {
                    ("404 Not Found", String::new())
                };
                let response = format!(
                    "HTTP/1.1 {status}\r\nContent-Length: {}\r\nConnection: close\r\n\r\n{body}",
                    body.len()
                );
                socket.write_all(response.as_bytes()).await.unwrap();
            });
        }
    });
    let (code, _) = send(addr, &admin_request(
        "POST",
        "/api/v1/indexers",
        &json!({"name": "Concurrent mock", "base_url": format!("http://{mock_addr}"), "api_key": "test-key"}).to_string(),
    )).await;
    assert_eq!(code, "HTTP/1.1 200 OK");
    let (code, search) = send(
        addr,
        &admin_request("GET", "/api/v1/indexers/search?q=two&limit=2", ""),
    )
    .await;
    assert_eq!(code, "HTTP/1.1 200 OK");
    let search: Value = serde_json::from_str(&search).unwrap();
    let rows = search["results"].as_array().unwrap();
    assert_eq!(rows.len(), 2);
    assert!(rows.iter().all(|row| row["can_queue"] == true));
    let req = |reference: &str| {
        admin_request(
            "POST",
            "/api/v1/indexers/search/queue",
            &json!({"nzb_ref":reference, "category":"movies"}).to_string(),
        )
    };
    let first = req(rows[0]["nzb_ref"].as_str().unwrap());
    let second = req(rows[1]["nzb_ref"].as_str().unwrap());
    let (a, b) = tokio::join!(send(addr, &first), send(addr, &second));
    assert!(
        matches!(a.0.as_str(), "HTTP/1.1 201 Created" | "HTTP/1.1 200 OK"),
        "{}",
        a.1
    );
    assert!(
        matches!(b.0.as_str(), "HTTP/1.1 201 Created" | "HTTP/1.1 200 OK"),
        "{}",
        b.1
    );
    assert_ne!(a.0, b.0);
    let a: Value = serde_json::from_str(&a.1).unwrap();
    let b: Value = serde_json::from_str(&b.1).unwrap();
    assert_eq!(a["id"], b["id"]);
    assert_ne!(a["status"], b["status"]);
    assert!(peak.load(Ordering::SeqCst) >= 2);
    mock.abort();
    cancel.cancel();
    server.await.unwrap().unwrap();
}

#[tokio::test]
async fn delayed_search_does_not_restore_references_after_reactivation() {
    let (addr, cancel, server, _) = start().await;
    let listener = TcpListener::bind("127.0.0.1:0").await.unwrap();
    let mock_addr = listener.local_addr().unwrap();
    let (started_tx, started_rx) = tokio::sync::oneshot::channel();
    let (release_tx, release_rx) = tokio::sync::oneshot::channel();
    let mock = tokio::spawn(async move {
        let (mut socket, _) = listener.accept().await.unwrap();
        let mut request = [0u8; 4096];
        let length = socket.read(&mut request).await.unwrap();
        assert!(String::from_utf8_lossy(&request[..length]).contains("t=search"));
        started_tx.send(()).unwrap();
        release_rx.await.unwrap();
        let xml = format!(
            "<rss><channel><item><title>Delayed result</title><link>http://{mock_addr}/nzb</link></item></channel></rss>"
        );
        let response = format!(
            "HTTP/1.1 200 OK\r\nContent-Length: {}\r\nConnection: close\r\n\r\n{xml}",
            xml.len()
        );
        socket.write_all(response.as_bytes()).await.unwrap();
    });
    let (code, created) = send(
        addr,
        &admin_request(
            "POST",
            "/api/v1/indexers",
            &json!({
                "name": "Delayed mock", "base_url": format!("http://{mock_addr}"),
                "api_key": "test-key", "enabled": true
            })
            .to_string(),
        ),
    )
    .await;
    assert_eq!(code, "HTTP/1.1 200 OK");
    let indexer: Value = serde_json::from_str(&created).unwrap();
    let id = indexer["id"].as_str().unwrap();
    let request = admin_request("GET", "/api/v1/indexers/search?q=delayed&limit=5", "");
    let pending = tokio::spawn(async move { send(addr, &request).await });
    tokio::time::timeout(Duration::from_secs(5), started_rx)
        .await
        .unwrap()
        .unwrap();
    for enabled in [false, true] {
        let (code, _) = send(
            addr,
            &admin_request(
                "PATCH",
                &format!("/api/v1/indexers/{id}/enabled"),
                &json!({"enabled": enabled}).to_string(),
            ),
        )
        .await;
        assert_eq!(code, "HTTP/1.1 200 OK");
    }
    release_tx.send(()).unwrap();
    let (code, result) = pending.await.unwrap();
    assert_eq!(code, "HTTP/1.1 200 OK");
    let result: Value = serde_json::from_str(&result).unwrap();
    assert_eq!(result["results"].as_array().unwrap().len(), 0);
    assert_eq!(result["indexers"][0]["ok"], false);
    mock.await.unwrap();
    cancel.cancel();
    server.await.unwrap().unwrap();
}

#[tokio::test]
async fn concurrent_indexer_searches_have_a_global_external_work_limit() {
    let (addr, cancel, server, _) = start().await;
    let listener = TcpListener::bind("127.0.0.1:0").await.unwrap();
    let mock_addr = listener.local_addr().unwrap();
    let active = Arc::new(AtomicUsize::new(0));
    let peak = Arc::new(AtomicUsize::new(0));
    let mock_active = Arc::clone(&active);
    let mock_peak = Arc::clone(&peak);
    let mock = tokio::spawn(async move {
        while let Ok((mut socket, _)) = listener.accept().await {
            let active = Arc::clone(&mock_active);
            let peak = Arc::clone(&mock_peak);
            tokio::spawn(async move {
                let mut buf = [0u8; 4096];
                let length = socket.read(&mut buf).await.unwrap();
                assert!(String::from_utf8_lossy(&buf[..length]).contains("t=search"));
                let count = active.fetch_add(1, Ordering::SeqCst) + 1;
                peak.fetch_max(count, Ordering::SeqCst);
                tokio::time::sleep(Duration::from_millis(80)).await;
                active.fetch_sub(1, Ordering::SeqCst);
                let xml = "<rss><channel><item><title>Result</title><link>https://example.invalid/nzb</link></item></channel></rss>";
                let response = format!(
                    "HTTP/1.1 200 OK\r\nContent-Length: {}\r\nConnection: close\r\n\r\n{xml}",
                    xml.len()
                );
                socket.write_all(response.as_bytes()).await.unwrap();
            });
        }
    });
    for n in 0..20 {
        let (code, _) = send(
            addr,
            &admin_request(
                "POST",
                "/api/v1/indexers",
                &json!({
                    "name": format!("Indexer {n}"),
                    "base_url": format!("http://{mock_addr}"),
                    "api_key": "test-key"
                })
                .to_string(),
            ),
        )
        .await;
        assert_eq!(code, "HTTP/1.1 200 OK");
    }
    let (code, body) = send(
        addr,
        &admin_request("GET", "/api/v1/indexers/search?q=bulk&limit=5", ""),
    )
    .await;
    assert_eq!(code, "HTTP/1.1 200 OK");
    let body: Value = serde_json::from_str(&body).unwrap();
    assert_eq!(body["results"].as_array().unwrap().len(), 20);
    assert!(
        body["indexers"]
            .as_array()
            .unwrap()
            .iter()
            .all(|row| row["ok"] == true)
    );
    let peak = peak.load(Ordering::SeqCst);
    assert!(peak > 1, "legitimate indexers should search in parallel");
    assert!(peak <= 12, "external search concurrency was {peak}");
    mock.abort();
    cancel.cancel();
    server.await.unwrap().unwrap();
}

#[tokio::test]
async fn search_nzb_reference_queues_using_selected_categories() {
    let (addr, cancel, server, db_url) = start().await;
    let listener = TcpListener::bind("127.0.0.1:0").await.unwrap();
    let mock_addr = listener.local_addr().unwrap();
    let mock = tokio::spawn(async move {
        loop {
            let Ok((mut socket, _)) = listener.accept().await else {
                return;
            };
            let mut buffer = [0; 4096];
            let Ok(length) = socket.read(&mut buffer).await else {
                return;
            };
            let request = String::from_utf8_lossy(&buffer[..length]);
            let first_line = request.lines().next().unwrap_or("");
            let (code, payload) = if first_line.starts_with("GET /api?t=search") {
                assert!(first_line.contains("apikey=mock-secret"));
                let name = first_line
                    .split("q=")
                    .nth(1)
                    .unwrap_or("movies")
                    .split('&')
                    .next()
                    .unwrap_or("movies");
                let url = match name {
                    "blocked" => "http://169.254.169.254/latest".to_owned(),
                    "relative" => "/api?t=get&amp;id=relative".to_owned(),
                    "foreign-secret" => "https://cdn.example/get?apikey=mock-secret".to_owned(),
                    "foreign-path" => "https://cdn.example/nzb/mock-secret/file".to_owned(),
                    "foreign-query" => "https://cdn.example/nzb?token=prefixmock-secretsuffix".to_owned(),
                    "foreign-encoded" => "https://cdn.example/nzb/%256d%256f%2563%256b%252d%2573%2565%2563%2572%2565%2574".to_owned(),
                    "cdn" => "https://cdn.example/nzb/download?token=public".to_owned(),
                    "secretpath" => format!("http://{mock_addr}/nzb/mock-secret.nzb"),
                    _ => format!("http://{mock_addr}/nzb/{name}?apikey=mock-secret"),
                };
                if name == "guid" {
                    ("200 OK", "<rss><channel><item><title>Only GUID</title><guid>1234</guid></item></channel></rss>".to_owned())
                } else {
                    (
                        "200 OK",
                        format!(
                            "<rss><channel><item><title>Result {name}</title><enclosure url=\"{url}\" length=\"100\"/></item></channel></rss>"
                        ),
                    )
                }
            } else if first_line
                .starts_with("GET /api?t=get&id=relative&apikey=mock-secret HTTP/1.1")
            {
                ("200 OK", NZB.replace("a@b", "relative@b"))
            } else if first_line.contains("/nzb/redirect") {
                ("302 Found", String::new())
            } else if first_line.contains("/nzb/badname") {
                ("200 OK", NZB.replace("a@b", "badname@b"))
            } else if first_line.contains("/nzb/secretname") {
                ("200 OK", NZB.replace("a@b", "secretname@b"))
            } else if first_line.contains("/nzb/mock-secret.nzb") {
                ("200 OK", NZB.replace("a@b", "secretpath@b"))
            } else if first_line.contains("/nzb/missing") {
                ("404 Not Found", "mock-secret".to_owned())
            } else if first_line.contains("/nzb/invalid") {
                ("200 OK", "This is not an NZB".to_owned())
            } else if first_line.contains("/nzb/rejected") {
                ("200 OK", NZB.replace("a@b", "rejected@b"))
            } else if first_line.contains("/nzb/tv") {
                ("200 OK", NZB.replace("a@b", "tv@b"))
            } else if first_line.contains("/nzb/movies") {
                ("200 OK", NZB.to_owned())
            } else {
                ("404 Not Found", "unknown".to_owned())
            };
            let extra = if first_line.contains("/nzb/redirect-secret") {
                "Location: https://cdn.example/nzb/%256d%256f%2563%256b%252d%2573%2565%2563%2572%2565%2574\r\n"
            } else if first_line.contains("/nzb/redirect") {
                "Location: http://169.254.169.254/latest\r\n"
            } else if first_line.contains("/nzb/badname") {
                "Content-Disposition: attachment; filename=\"../unsafe.nzb\"\r\n"
            } else if first_line.contains("/nzb/secretname") {
                "Content-Disposition: attachment; filename=\"mock-secret.nzb\"\r\n"
            } else {
                ""
            };
            let response = format!(
                "HTTP/1.1 {code}\r\n{extra}Content-Length: {}\r\nConnection: close\r\n\r\n{payload}",
                payload.len()
            );
            socket.write_all(response.as_bytes()).await.unwrap();
        }
    });

    let (code, created) = send(addr, &admin_request("POST", "/api/v1/indexers", &json!({
        "name": "queue mock", "base_url": format!("http://{mock_addr}"), "api_key": "mock-secret",
        "enabled": true
    }).to_string())).await;
    assert_eq!(code, "HTTP/1.1 200 OK");
    let mut indexer: Value = serde_json::from_str(&created).unwrap();
    let id = indexer["id"].as_str().unwrap().to_owned();

    let queue_path = "/api/v1/indexers/search/queue";
    let anonymous = format!(
        "POST {queue_path} HTTP/1.1\r\nHost: x\r\nConnection: close\r\nContent-Length: 0\r\n\r\n"
    );
    assert_eq!(status(addr, &anonymous).await, "HTTP/1.1 401 Unauthorized");

    let (code, spec) = send(addr, &admin_request("GET", "/api/v1/openapi.json", "")).await;
    assert_eq!(code, "HTTP/1.1 200 OK");
    let spec: Value = serde_json::from_str(&spec).unwrap();
    assert_eq!(
        spec["paths"][queue_path]["post"]["requestBody"]["content"]["application/json"]["schema"]["$ref"],
        "#/components/schemas/SearchQueueRequest"
    );
    let item_schema = &spec["components"]["schemas"]["SearchResult"]["properties"];
    assert!(item_schema.get("download_url").is_none());
    assert_eq!(item_schema["can_queue"]["type"], "boolean");

    let lookup = |q: &str| format!("/api/v1/indexers/search?q={q}&limit=1");
    let body = |reference: &str, category: &str| {
        json!({
            "nzb_ref": reference,
            "category": category
        })
        .to_string()
    };
    let (code, search) = send(addr, &admin_request("GET", &lookup("movies"), "")).await;
    assert_eq!(code, "HTTP/1.1 200 OK");
    assert!(!search.contains("mock-secret"));
    assert!(!search.contains("http://"));
    let search: Value = serde_json::from_str(&search).unwrap();
    let reference = search["results"][0]["nzb_ref"].as_str().unwrap().to_owned();
    assert_eq!(search["results"][0]["can_queue"], true);

    for bad in [
        body("https://example.com/nzb", "movies"),
        body(&(reference.clone() + "x"), "movies"),
        body("", "movies"),
    ] {
        let (code, reply) = send(addr, &admin_request("POST", queue_path, &bad)).await;
        assert_eq!(code, "HTTP/1.1 404 Not Found");
        assert!(!reply.contains("mock-secret"));
    }
    let (code, _) = send(
        addr,
        &admin_request("POST", queue_path, &body(&reference, "unknown")),
    )
    .await;
    assert_eq!(code, "HTTP/1.1 400 Bad Request");
    let (code, _) = send(
        addr,
        &admin_request("POST", queue_path, &body(&reference, "")),
    )
    .await;
    assert_eq!(code, "HTTP/1.1 400 Bad Request");
    let (code, _) = send(
        addr,
        &admin_request(
            "POST",
            queue_path,
            &json!({"nzb_ref": reference, "category": "movies", "url": "http://169.254.169.254"})
                .to_string(),
        ),
    )
    .await;
    assert_eq!(code, "HTTP/1.1 422 Unprocessable Entity");

    let (code, queued) = send(
        addr,
        &admin_request("POST", queue_path, &body(&reference, "movies")),
    )
    .await;
    assert_eq!(code, "HTTP/1.1 201 Created", "{queued}");
    let created_movie: Value = serde_json::from_str(&queued).unwrap();
    assert!(created_movie["id"].is_string());
    assert_eq!(created_movie["status"], "created");
    let (code, duplicate) = send(
        addr,
        &admin_request("POST", queue_path, &body(&reference, "movies")),
    )
    .await;
    assert_eq!(code, "HTTP/1.1 200 OK", "{duplicate}");
    let duplicate: Value = serde_json::from_str(&duplicate).unwrap();
    assert_eq!(duplicate["status"], "existing");
    assert_eq!(duplicate["id"], created_movie["id"]);
    let (code, duplicate_category) = send(
        addr,
        &admin_request("POST", queue_path, &body(&reference, "tv")),
    )
    .await;
    assert_eq!(code, "HTTP/1.1 200 OK");
    let duplicate_category: Value = serde_json::from_str(&duplicate_category).unwrap();
    assert_eq!(duplicate_category["status"], "existing");
    assert_eq!(duplicate_category["id"], created_movie["id"]);

    let db = infinidysk_db::Db::connect(&db_url).await.unwrap();
    db.reject_release("ignored", &["rejected@b"], i64::MAX / 2)
        .await
        .unwrap();
    let (_, search) = send(addr, &admin_request("GET", &lookup("rejected"), "")).await;
    let search: Value = serde_json::from_str(&search).unwrap();
    let reject_ref = search["results"][0]["nzb_ref"].as_str().unwrap();
    let (code, rejected) = send(
        addr,
        &admin_request("POST", queue_path, &body(reject_ref, "movies")),
    )
    .await;
    assert_eq!(code, "HTTP/1.1 422 Unprocessable Entity", "{rejected}");
    let rejected: Value = serde_json::from_str(&rejected).unwrap();
    assert_eq!(rejected["status"], "rejected");
    assert!(rejected["id"].is_string());
    assert!(
        db.queue()
            .await
            .unwrap()
            .iter()
            .all(|job| job.file_name != "Result rejected.nzb")
    );
    db.close().await;

    let (code, search) = send(addr, &admin_request("GET", &lookup("tv"), "")).await;
    assert_eq!(code, "HTTP/1.1 200 OK");
    let search: Value = serde_json::from_str(&search).unwrap();
    let tv_ref = search["results"][0]["nzb_ref"].as_str().unwrap();
    let (code, queued) = send(
        addr,
        &admin_request("POST", queue_path, &body(tv_ref, "tv")),
    )
    .await;
    assert_eq!(code, "HTTP/1.1 201 Created", "{queued}");

    let (code, queue) = send(addr, &admin_request("GET", "/api/v1/queue", "")).await;
    assert_eq!(code, "HTTP/1.1 200 OK");
    let queue: Value = serde_json::from_str(&queue).unwrap();
    let jobs = queue.as_array().unwrap();
    assert!(
        jobs.iter()
            .any(|j| j["category"] == "movies" && j["file_name"] == "Result movies.nzb"),
        "{jobs:?}"
    );
    assert!(
        jobs.iter()
            .any(|j| j["category"] == "tv" && j["file_name"] == "Result tv.nzb"),
        "{jobs:?}"
    );

    for (query, expected_file_name) in [
        ("relative", "Result relative.nzb"),
        ("badname", "Result badname.nzb"),
        ("secretname", "Result secretname.nzb"),
        ("secretpath", "Result secretpath.nzb"),
    ] {
        let (_, search) = send(addr, &admin_request("GET", &lookup(query), "")).await;
        assert!(!search.contains("mock-secret"));
        let search: Value = serde_json::from_str(&search).unwrap();
        assert_eq!(search["results"][0]["can_queue"], true);
        let ref_id = search["results"][0]["nzb_ref"].as_str().unwrap();
        let (code, reply) = send(
            addr,
            &admin_request("POST", queue_path, &body(ref_id, "movies")),
        )
        .await;
        assert_eq!(code, "HTTP/1.1 201 Created", "{reply}");
        let (_, queue) = send(addr, &admin_request("GET", "/api/v1/queue", "")).await;
        let queue: Value = serde_json::from_str(&queue).unwrap();
        assert!(
            queue
                .as_array()
                .unwrap()
                .iter()
                .any(|j| j["file_name"] == expected_file_name && j["category"] == "movies"),
            "{queue}"
        );
    }

    let (_, cdn) = send(addr, &admin_request("GET", &lookup("cdn"), "")).await;
    let cdn: Value = serde_json::from_str(&cdn).unwrap();
    assert_eq!(cdn["results"][0]["can_queue"], true);

    for query in [
        "guid",
        "blocked",
        "foreign-secret",
        "foreign-path",
        "foreign-query",
        "foreign-encoded",
    ] {
        let (_, search) = send(addr, &admin_request("GET", &lookup(query), "")).await;
        let search: Value = serde_json::from_str(&search).unwrap();
        let ref_id = search["results"][0]["nzb_ref"].as_str().unwrap();
        assert_eq!(search["results"][0]["can_queue"], false);
        let (code, reply) = send(
            addr,
            &admin_request("POST", queue_path, &body(ref_id, "movies")),
        )
        .await;
        assert_eq!(code, "HTTP/1.1 404 Not Found");
        assert!(!reply.contains("mock-secret"));
    }
    for (query, expected) in [
        ("invalid", "HTTP/1.1 400 Bad Request"),
        ("missing", "HTTP/1.1 503 Service Unavailable"),
        ("redirect", "HTTP/1.1 503 Service Unavailable"),
        ("redirect-secret", "HTTP/1.1 503 Service Unavailable"),
    ] {
        let (_, search) = send(addr, &admin_request("GET", &lookup(query), "")).await;
        let search: Value = serde_json::from_str(&search).unwrap();
        let ref_id = search["results"][0]["nzb_ref"].as_str().unwrap();
        assert_eq!(search["results"][0]["can_queue"], true);
        let (code, reply) = send(
            addr,
            &admin_request("POST", queue_path, &body(ref_id, "movies")),
        )
        .await;
        assert_eq!(code, expected);
        assert!(!reply.contains("mock-secret"));
        assert!(!reply.contains("apikey"));
    }

    indexer["enabled"] = json!(false);
    let (code, _) = send(
        addr,
        &admin_request(
            "PATCH",
            &format!("/api/v1/indexers/{id}/enabled"),
            &json!({"enabled":false}).to_string(),
        ),
    )
    .await;
    assert_eq!(code, "HTTP/1.1 200 OK");
    let (code, _) = send(
        addr,
        &admin_request("POST", queue_path, &body(&reference, "movies")),
    )
    .await;
    assert_eq!(code, "HTTP/1.1 404 Not Found");

    indexer["enabled"] = json!(true);
    let (code, _) = send(
        addr,
        &admin_request(
            "PATCH",
            &format!("/api/v1/indexers/{id}/enabled"),
            &json!({"enabled":true}).to_string(),
        ),
    )
    .await;
    assert_eq!(code, "HTTP/1.1 200 OK");
    assert_eq!(
        status(
            addr,
            &admin_request("POST", queue_path, &body(&reference, "movies"))
        )
        .await,
        "HTTP/1.1 404 Not Found"
    );
    let (_, fresh) = send(addr, &admin_request("GET", &lookup("movies"), "")).await;
    let fresh: Value = serde_json::from_str(&fresh).unwrap();
    let fresh_ref = fresh["results"][0]["nzb_ref"].as_str().unwrap().to_owned();
    let (_, listing) = send(addr, &admin_request("GET", "/api/v1/indexers", "")).await;
    let listing: Value = serde_json::from_str(&listing).unwrap();
    let mut updated = listing.as_array().unwrap()[0].clone();
    updated["name"] = json!("Revised mock");
    updated["api_key"] = json!("mock-secret");
    let (code, _) = send(
        addr,
        &admin_request(
            "PUT",
            &format!("/api/v1/indexers/{id}"),
            &updated.to_string(),
        ),
    )
    .await;
    assert_eq!(code, "HTTP/1.1 200 OK");
    assert_eq!(
        status(
            addr,
            &admin_request("POST", queue_path, &body(&fresh_ref, "movies"))
        )
        .await,
        "HTTP/1.1 404 Not Found"
    );
    let (_, after_update) = send(addr, &admin_request("GET", &lookup("movies"), "")).await;
    let after_update: Value = serde_json::from_str(&after_update).unwrap();
    let current_ref = after_update["results"][0]["nzb_ref"].as_str().unwrap();
    assert_ne!(current_ref, fresh_ref);
    assert_eq!(
        status(
            addr,
            &admin_request("DELETE", &format!("/api/v1/indexers/{id}"), "")
        )
        .await,
        "HTTP/1.1 204 No Content"
    );
    let (code, _) = send(
        addr,
        &admin_request("POST", queue_path, &body(current_ref, "movies")),
    )
    .await;
    assert_eq!(code, "HTTP/1.1 404 Not Found");

    mock.abort();
    cancel.cancel();
    server.await.unwrap().unwrap();
}

#[tokio::test]
async fn searches_enabled_indexers_and_keeps_partial_results_secure() {
    let (addr, cancel, server, _) = start().await;
    let (_, schema) = send(addr, &admin_request("GET", "/api/v1/openapi.json", "")).await;
    let schema: Value = serde_json::from_str(&schema).unwrap();
    let params = schema["paths"]["/api/v1/indexers/search"]["get"]["parameters"]
        .as_array()
        .unwrap();
    assert!(
        params
            .iter()
            .any(|p| p["name"] == "q" && p["in"] == "query" && p["required"] == true)
    );
    assert!(
        params
            .iter()
            .any(|p| p["name"] == "limit" && p["in"] == "query" && p["required"] != true)
    );
    let path = "/api/v1/indexers/search?q=sample&limit=7";
    let unauth = format!("GET {path} HTTP/1.1\r\nHost: x\r\nConnection: close\r\n\r\n");
    assert_eq!(status(addr, &unauth).await, "HTTP/1.1 401 Unauthorized");
    assert_eq!(
        status(
            addr,
            &admin_request("GET", "/api/v1/indexers/search?q=&limit=7", "")
        )
        .await,
        "HTTP/1.1 400 Bad Request"
    );
    assert_eq!(
        status(
            addr,
            &admin_request("GET", "/api/v1/indexers/search?q=sample&limit=501", "")
        )
        .await,
        "HTTP/1.1 400 Bad Request"
    );
    let (code, initial) = send(addr, &admin_request("GET", path, "")).await;
    assert_eq!(code, "HTTP/1.1 200 OK");
    let initial: Value = serde_json::from_str(&initial).unwrap();
    assert_eq!(initial["results"], json!([]));
    assert_eq!(initial["indexers"], json!([]));

    let responder = |status: &'static str, xml: &'static str| async move {
        let listener = TcpListener::bind("127.0.0.1:0").await.unwrap();
        let url = format!("http://{}", listener.local_addr().unwrap());
        let task = tokio::spawn(async move {
            let (mut socket, _) = listener.accept().await.unwrap();
            let mut bytes = [0; 4096];
            let count = socket.read(&mut bytes).await.unwrap();
            let request = std::str::from_utf8(&bytes[..count]).unwrap();
            assert!(request.starts_with(
                "GET /api?t=search&q=sample&limit=7&extended=1&apikey=mock-secret HTTP/1.1"
            ));
            let response = format!(
                "HTTP/1.1 {status}\r\nContent-Length: {}\r\nConnection: close\r\n\r\n{xml}",
                xml.len()
            );
            socket.write_all(response.as_bytes()).await.unwrap();
        });
        (url, task)
    };
    let (first, first_task) = responder(
        "200 OK",
        "<rss><channel><item><title>Sample A</title><enclosure url=\"https://example.invalid/get?apikey=mock-secret&amp;id=1\" length=\"101\"/></item></channel></rss>",
    ).await;
    let (second, second_task) = responder(
        "200 OK",
        "<rss xmlns:nz=\"http://www.newznab.com/DTD/2010/feeds/attributes/\"><channel><item><title>Sample B</title><guid>second</guid><nz:attr name=\"size\" value=\"202\"/></item></channel></rss>",
    ).await;
    let (failed, failed_task) = responder("401 Unauthorized", "mock-secret").await;
    let add = |name: &str, url: &str, enabled: bool| {
        admin_request(
            "POST",
            "/api/v1/indexers",
            &json!({
                "name": name, "base_url": url, "api_key": "mock-secret", "enabled": enabled
            })
            .to_string(),
        )
    };
    let (_, first_created) = send(addr, &add("First", &first, true)).await;
    let first_id = serde_json::from_str::<Value>(&first_created).unwrap()["id"]
        .as_str()
        .unwrap()
        .to_owned();
    let (_, failed_created) = send(addr, &add("Broken", &failed, true)).await;
    let failed_id = serde_json::from_str::<Value>(&failed_created).unwrap()["id"]
        .as_str()
        .unwrap()
        .to_owned();
    let (_, second_created) = send(addr, &add("Second", &second, true)).await;
    let second_id = serde_json::from_str::<Value>(&second_created).unwrap()["id"]
        .as_str()
        .unwrap()
        .to_owned();
    let (_, disabled_created) = send(addr, &add("Disabled", "http://127.0.0.1:1/api", false)).await;
    assert_eq!(
        serde_json::from_str::<Value>(&disabled_created).unwrap()["enabled"],
        false
    );

    let (code, response) = send(addr, &admin_request("GET", path, "")).await;
    assert_eq!(code, "HTTP/1.1 200 OK");
    assert!(!response.contains("mock-secret"));
    assert!(!response.contains("apikey"));
    assert!(!response.contains("https://"));
    let parsed: Value = serde_json::from_str(&response).unwrap();
    let results = parsed["results"].as_array().unwrap();
    assert_eq!(results.len(), 2);
    assert_eq!(results[0]["title"], "Sample A");
    assert_eq!(results[0]["size_bytes"], 101);
    assert_eq!(results[0]["indexer_id"], first_id);
    assert_eq!(results[0]["indexer_name"], "First");
    assert!(uuid::Uuid::parse_str(results[0]["nzb_ref"].as_str().unwrap()).is_ok());
    assert_eq!(results[1]["title"], "Sample B");
    assert_eq!(results[1]["size_bytes"], 202);
    assert_eq!(results[1]["indexer_id"], second_id);
    let indexers = parsed["indexers"].as_array().unwrap();
    assert_eq!(indexers.len(), 3);
    assert_eq!(indexers[0]["result_count"], 1);
    assert_eq!(indexers[0]["ok"], true);
    assert_eq!(indexers[1]["indexer_id"], failed_id);
    assert_eq!(indexers[1]["ok"], false);
    assert_eq!(indexers[1]["error"], "Indexer authentication failed.");
    assert_eq!(indexers[2]["ok"], true);

    first_task.await.unwrap();
    second_task.await.unwrap();
    failed_task.await.unwrap();
    cancel.cancel();
    server.await.unwrap().unwrap();
}

#[tokio::test]
async fn indexer_tabs_do_not_overwrite_concurrent_url_key_changes() {
    let (addr, cancel, server, _) = start().await;
    let (code, created) = send(
        addr,
        &admin_request(
            "POST",
            "/api/v1/indexers",
            &json!({
                "name": "Session indexer",
                "base_url": "https://first.example/api",
                "api_key": "mock-first-key",
                "enabled": true
            })
            .to_string(),
        ),
    )
    .await;
    assert_eq!(code, "HTTP/1.1 200 OK");
    assert!(!created.contains("mock-first-key"));
    let original: Value = serde_json::from_str(&created).unwrap();
    let stale_tab = original.clone();
    let id = original["id"].as_str().unwrap().to_owned();
    let path = format!("/api/v1/indexers/{id}");
    let (_, old_settings) = send(addr, &admin_request("GET", "/api/v1/settings", "")).await;
    let mut stale_settings = settings_from_view(&old_settings);
    assert!(original["revision"].as_str().is_some_and(|s| s.len() >= 32));

    let mut first_tab = original.clone();
    first_tab["base_url"] = json!("https://second.example/api");
    first_tab["api_key"] = json!("mock-second-key");
    let (code, updated) = send(addr, &admin_request("PUT", &path, &first_tab.to_string())).await;
    assert_eq!(code, "HTTP/1.1 200 OK", "{updated}");
    assert!(!updated.contains("mock-second-key"));
    let updated: Value = serde_json::from_str(&updated).unwrap();
    assert_ne!(original["revision"], updated["revision"]);
    stale_settings["health"]["check_percent"] = json!(9);
    assert_eq!(
        status(
            addr,
            &admin_request("PUT", "/api/v1/settings", &stale_settings.to_string())
        )
        .await,
        "HTTP/1.1 409 Conflict"
    );

    let mut stale_edit = stale_tab.clone();
    stale_edit["name"] = json!("Old tab name");
    assert_eq!(
        status(addr, &admin_request("PUT", &path, &stale_edit.to_string())).await,
        "HTTP/1.1 409 Conflict"
    );

    let (code, toggled) = send(
        addr,
        &admin_request(
            "PATCH",
            &format!("{path}/enabled"),
            &json!({"enabled":false}).to_string(),
        ),
    )
    .await;
    assert_eq!(code, "HTTP/1.1 200 OK");
    let toggled: Value = serde_json::from_str(&toggled).unwrap();
    assert_eq!(toggled["base_url"], "https://second.example/api");
    assert_eq!(toggled["name"], "Session indexer");
    assert_eq!(toggled["enabled"], false);
    assert_eq!(toggled["api_key"], "********");
    assert_ne!(toggled["revision"], updated["revision"]);

    let (_, current_settings) = send(addr, &admin_request("GET", "/api/v1/settings", "")).await;
    let mut current_settings = settings_from_view(&current_settings);
    current_settings["health"]["check_percent"] = json!(8);
    let (code, saved) = send(
        addr,
        &admin_request("PUT", "/api/v1/settings", &current_settings.to_string()),
    )
    .await;
    assert_eq!(code, "HTTP/1.1 200 OK", "{saved}");
    let saved: Value = serde_json::from_str(&saved).unwrap();
    assert_eq!(
        saved["settings"]["indexers"][0]["base_url"],
        "https://second.example/api"
    );
    assert_eq!(saved["settings"]["indexers"][0]["enabled"], false);
    assert_eq!(saved["settings"]["health"]["check_percent"], 8);

    let (_, listing) = send(addr, &admin_request("GET", "/api/v1/indexers", "")).await;
    let listing: Value = serde_json::from_str(&listing).unwrap();
    assert_eq!(listing[0]["revision"], toggled["revision"]);
    assert_eq!(listing[0]["base_url"], "https://second.example/api");
    let mut masked_origin_change = toggled.clone();
    masked_origin_change["base_url"] = json!("https://third.example/api");
    let (code, rejected) = send(
        addr,
        &admin_request("PUT", &path, &masked_origin_change.to_string()),
    )
    .await;
    assert_eq!(code, "HTTP/1.1 400 Bad Request");
    assert!(rejected.contains("API key"));

    let (_, response) = send(addr, &admin_request("GET", "/api/v1/settings", "")).await;
    let mut settings = settings_from_view(&response);
    settings["indexers"][0]["base_url"] = json!("https://third.example/api");
    assert_eq!(
        status(
            addr,
            &admin_request("PUT", "/api/v1/settings", &settings.to_string())
        )
        .await,
        "HTTP/1.1 409 Conflict"
    );

    let (_, listing) = send(addr, &admin_request("GET", "/api/v1/indexers", "")).await;
    let listing: Value = serde_json::from_str(&listing).unwrap();
    assert_eq!(listing[0]["base_url"], "https://second.example/api");

    let mut explicit = toggled;
    explicit["base_url"] = json!("https://third.example/api");
    explicit["api_key"] = json!("mock-third-key");
    let (code, changed) = send(addr, &admin_request("PUT", &path, &explicit.to_string())).await;
    assert_eq!(code, "HTTP/1.1 200 OK", "{changed}");
    assert!(!changed.contains("mock-third-key"));
    let changed: Value = serde_json::from_str(&changed).unwrap();
    assert_eq!(changed["base_url"], "https://third.example/api");
    assert_eq!(changed["enabled"], false);
    assert_eq!(changed["api_key"], "********");

    let (code, spec) = send(addr, &admin_request("GET", "/api/v1/openapi.json", "")).await;
    assert_eq!(code, "HTTP/1.1 200 OK");
    let spec: Value = serde_json::from_str(&spec).unwrap();
    assert_eq!(
        spec["paths"]["/api/v1/indexers/{id}/enabled"]["patch"]["requestBody"]["content"]["application/json"]
            ["schema"]["$ref"],
        "#/components/schemas/IndexerEnabled"
    );
    assert_eq!(
        spec["paths"]["/api/v1/indexers/{id}"]["put"]["requestBody"]["content"]["application/json"]
            ["schema"]["$ref"],
        "#/components/schemas/IndexerUpdate"
    );
    assert!(spec["components"]["schemas"]["IndexerRead"]["properties"]["revision"].is_object());

    cancel.cancel();
    server.await.unwrap().unwrap();
}

#[tokio::test]
async fn indexer_writes_reject_credentials_in_base_urls() {
    let (addr, cancel, server, _) = start().await;
    let endpoint = "/api/v1/indexers";
    let settings_path = "/api/v1/settings";
    let safe = "https://indexer.invalid/proxy?source=public+feed";
    let secret_url = "https://indexer.invalid/proxy?source=fake%2520key";
    let mut input = json!({"name": "Indexer", "base_url": safe, "api_key": "fake key"});
    let (code, body) = send(addr, &admin_request("POST", endpoint, &input.to_string())).await;
    assert_eq!(code, "HTTP/1.1 200 OK");
    let mut indexer: Value = serde_json::from_str(&body).unwrap();
    let edit = format!("{endpoint}/{}", indexer["id"].as_str().unwrap());

    input["base_url"] = json!(secret_url);
    let (create_code, create_body) =
        send(addr, &admin_request("POST", endpoint, &input.to_string())).await;
    indexer["base_url"] = json!(secret_url);
    let (edit_code, edit_body) =
        send(addr, &admin_request("PUT", &edit, &indexer.to_string())).await;
    for body in [create_body, edit_body] {
        assert!(!body.contains(secret_url));
    }
    assert_eq!(create_code, "HTTP/1.1 400 Bad Request");
    assert_eq!(edit_code, "HTTP/1.1 400 Bad Request");
    indexer["api_key"] = json!("replacement-key");
    let (code, body) = send(addr, &admin_request("PUT", &edit, &indexer.to_string())).await;
    assert_eq!(code, "HTTP/1.1 400 Bad Request");
    assert!(!body.contains(secret_url));

    let (_, body) = send(addr, &admin_request("GET", settings_path, "")).await;
    let mut settings = settings_from_view(&body);
    assert_eq!(settings["indexers"][0]["base_url"], safe);
    assert_eq!(settings["indexers"][0]["api_key"], "********");
    settings["indexers"][0]["base_url"] = json!(secret_url);
    assert_eq!(
        status(
            addr,
            &admin_request("PUT", settings_path, &settings.to_string())
        )
        .await,
        "HTTP/1.1 409 Conflict"
    );
    settings["indexers"][0]["base_url"] = json!(safe);
    let (code, body) = send(
        addr,
        &admin_request("PUT", settings_path, &settings.to_string()),
    )
    .await;
    assert_eq!(code, "HTTP/1.1 200 OK");
    assert_eq!(
        serde_json::from_str::<Value>(&body).unwrap()["settings"]["indexers"][0]["base_url"],
        safe
    );
    cancel.cancel();
    server.await.unwrap().unwrap();
}

#[tokio::test]
async fn manages_multiple_indexers_through_admin_http() {
    let (addr, cancel, server, _) = start().await;
    let anonymous = |method: &str, path: &str| {
        format!(
            "{method} {path} HTTP/1.1\r\nHost: x\r\nConnection: close\r\nContent-Length: 0\r\n\r\n"
        )
    };
    for (method, path) in [
        ("GET", "/api/v1/indexers"),
        ("POST", "/api/v1/indexers"),
        (
            "PUT",
            "/api/v1/indexers/0192f0a0-0000-7000-8000-000000000001",
        ),
        (
            "DELETE",
            "/api/v1/indexers/0192f0a0-0000-7000-8000-000000000001",
        ),
        (
            "POST",
            "/api/v1/indexers/0192f0a0-0000-7000-8000-000000000001/test",
        ),
    ] {
        assert_eq!(
            status(addr, &anonymous(method, path)).await,
            "HTTP/1.1 401 Unauthorized"
        );
    }
    let (code, body) = send(addr, &admin_request("GET", "/api/v1/indexers", "")).await;
    assert_eq!(code, "HTTP/1.1 200 OK");
    assert_eq!(serde_json::from_str::<Value>(&body).unwrap(), json!([]));

    let (_, body) = send(addr, &admin_request("GET", "/api/v1/settings", "")).await;
    let mut settings = settings_from_view(&body);
    settings["health"]["check_percent"] = json!(9);
    assert_eq!(
        status(
            addr,
            &admin_request("PUT", "/api/v1/settings", &settings.to_string())
        )
        .await,
        "HTTP/1.1 200 OK"
    );

    let first_listener = TcpListener::bind("127.0.0.1:0").await.unwrap();
    let first_addr = first_listener.local_addr().unwrap();
    let first = tokio::spawn(async move {
        for _ in 0..2 {
            let (mut connection, _) = first_listener.accept().await.unwrap();
            let mut data = [0; 4096];
            let count = connection.read(&mut data).await.unwrap();
            let request = std::str::from_utf8(&data[..count]).unwrap();
            assert!(request.starts_with("GET /api?t=caps&apikey=first-secret HTTP/1.1"));
            let payload = "<caps/>";
            connection.write_all(
                format!("HTTP/1.1 200 OK\r\nContent-Length: {}\r\nConnection: close\r\n\r\n{payload}", payload.len()).as_bytes()
            ).await.unwrap();
        }
    });
    let second_listener = TcpListener::bind("127.0.0.1:0").await.unwrap();
    let second_addr = second_listener.local_addr().unwrap();
    let second = tokio::spawn(async move {
        let (mut connection, _) = second_listener.accept().await.unwrap();
        let mut data = [0; 4096];
        let count = connection.read(&mut data).await.unwrap();
        let request = std::str::from_utf8(&data[..count]).unwrap();
        assert!(request.starts_with("GET /api?t=caps&apikey=second-secret HTTP/1.1"));
        connection
            .write_all(
                b"HTTP/1.1 401 Unauthorized\r\nContent-Length: 0\r\nConnection: close\r\n\r\n",
            )
            .await
            .unwrap();
    });
    let create = |name: &str, url: String, key: &str| {
        admin_request(
            "POST",
            "/api/v1/indexers",
            &json!({
                "name": name, "base_url": url, "api_key": key, "enabled": true
            })
            .to_string(),
        )
    };
    let (code, first_created) = send(
        addr,
        &create("First", format!("http://{first_addr}/api"), "first-secret"),
    )
    .await;
    assert_eq!(code, "HTTP/1.1 200 OK");
    assert!(!first_created.contains("first-secret"));
    let a: Value = serde_json::from_str(&first_created).unwrap();
    assert_eq!(a["api_key"], "********");
    let id_a = a["id"].as_str().unwrap();
    let (code, second_created) = send(
        addr,
        &create("Second", format!("http://{second_addr}"), "second-secret"),
    )
    .await;
    assert_eq!(code, "HTTP/1.1 200 OK");
    assert!(!second_created.contains("second-secret"));
    let b: Value = serde_json::from_str(&second_created).unwrap();
    let id_b = b["id"].as_str().unwrap();
    assert_ne!(id_a, id_b);

    let (code, body) = send(addr, &admin_request("GET", "/api/v1/indexers", "")).await;
    assert_eq!(code, "HTTP/1.1 200 OK");
    assert!(!body.contains("first-secret") && !body.contains("second-secret"));
    assert_eq!(
        serde_json::from_str::<Value>(&body)
            .unwrap()
            .as_array()
            .unwrap()
            .len(),
        2
    );
    let (code, body) = send(
        addr,
        &admin_request("POST", &format!("/api/v1/indexers/{id_a}/test"), ""),
    )
    .await;
    assert_eq!(code, "HTTP/1.1 200 OK");
    assert_eq!(
        serde_json::from_str::<Value>(&body).unwrap()["success"],
        true
    );
    let (code, body) = send(
        addr,
        &admin_request("POST", &format!("/api/v1/indexers/{id_b}/test"), ""),
    )
    .await;
    assert_eq!(code, "HTTP/1.1 200 OK");
    assert!(!body.contains("second-secret"));
    assert_eq!(
        serde_json::from_str::<Value>(&body).unwrap()["success"],
        false
    );

    let mut edited = a.clone();
    edited["name"] = json!("Updated");
    edited["enabled"] = json!(false);
    let (code, changed) = send(
        addr,
        &admin_request(
            "PUT",
            &format!("/api/v1/indexers/{id_a}"),
            &edited.to_string(),
        ),
    )
    .await;
    assert_eq!(code, "HTTP/1.1 200 OK");
    assert!(!changed.contains("first-secret"));
    let changed: Value = serde_json::from_str(&changed).unwrap();
    assert_eq!(changed["name"], "Updated");
    assert_eq!(changed["enabled"], false);

    edited["enabled"] = json!(true);
    edited["revision"] = changed["revision"].clone();
    assert_eq!(
        status(
            addr,
            &admin_request(
                "PUT",
                &format!("/api/v1/indexers/{id_a}"),
                &edited.to_string()
            )
        )
        .await,
        "HTTP/1.1 200 OK"
    );
    let (code, body) = send(
        addr,
        &admin_request("POST", &format!("/api/v1/indexers/{id_a}/test"), ""),
    )
    .await;
    assert_eq!(code, "HTTP/1.1 200 OK");
    assert_eq!(
        serde_json::from_str::<Value>(&body).unwrap()["success"],
        true
    );
    first.await.unwrap();
    second.await.unwrap();

    let unknown = "0192f0a0-0000-7000-8000-000000000001";
    assert_eq!(
        status(
            addr,
            &admin_request("POST", &format!("/api/v1/indexers/{unknown}/test"), "")
        )
        .await,
        "HTTP/1.1 404 Not Found"
    );
    assert_eq!(
        status(
            addr,
            &admin_request("DELETE", &format!("/api/v1/indexers/{unknown}"), "")
        )
        .await,
        "HTTP/1.1 404 Not Found"
    );
    assert_eq!(
        status(
            addr,
            &admin_request(
                "PUT",
                &format!("/api/v1/indexers/{unknown}"),
                &edited.to_string()
            )
        )
        .await,
        "HTTP/1.1 400 Bad Request"
    );
    assert_eq!(
        status(
            addr,
            &admin_request("POST", "/api/v1/indexers/not-a-uuid/test", "")
        )
        .await,
        "HTTP/1.1 400 Bad Request"
    );

    let (code, body) = send(addr, &admin_request("GET", "/api/v1/settings", "")).await;
    assert_eq!(code, "HTTP/1.1 200 OK");
    assert!(!body.contains("first-secret") && !body.contains("second-secret"));
    let all: Value = serde_json::from_str(&body).unwrap();
    assert_eq!(all["settings"]["health"]["check_percent"], 9);
    assert_eq!(all["settings"]["indexers"].as_array().unwrap().len(), 2);
    assert_eq!(
        status(
            addr,
            &admin_request("GET", "/api/v1/settings/keys/indexers", "")
        )
        .await,
        "HTTP/1.1 404 Not Found"
    );

    assert_eq!(
        status(
            addr,
            &admin_request("DELETE", &format!("/api/v1/indexers/{id_b}"), "")
        )
        .await,
        "HTTP/1.1 204 No Content"
    );
    assert_eq!(
        status(
            addr,
            &admin_request("DELETE", &format!("/api/v1/indexers/{id_a}"), "")
        )
        .await,
        "HTTP/1.1 204 No Content"
    );
    let (_, body) = send(addr, &admin_request("GET", "/api/v1/indexers", "")).await;
    assert_eq!(serde_json::from_str::<Value>(&body).unwrap(), json!([]));
    cancel.cancel();
    server.await.unwrap().unwrap();
}

#[tokio::test]
async fn many_indexer_results_have_a_fixed_http_response_budget_and_order() {
    use std::fmt::Write as _;

    let (addr, cancel, server, _) = start().await;
    let listener = TcpListener::bind("127.0.0.1:0").await.unwrap();
    let mock_addr = listener.local_addr().unwrap();
    let mock = tokio::spawn(async move {
        let mut xml = String::from("<rss><channel>");
        for n in 0..450 {
            write!(
                xml,
                "<item><title>{n}-{}</title><link>http://{mock_addr}/nzb/{n}</link></item>",
                "x".repeat(300)
            )
            .unwrap();
        }
        xml.push_str("</channel></rss>");
        while let Ok((mut socket, _)) = listener.accept().await {
            let xml = xml.clone();
            tokio::spawn(async move {
                let mut buffer = [0u8; 4096];
                let Ok(n) = socket.read(&mut buffer).await else {
                    return;
                };
                let request = String::from_utf8_lossy(&buffer[..n]);
                let (status, body) =
                    if request.contains("t=search") && request.contains("apikey=k31") {
                        ("503 Service Unavailable", String::new())
                    } else if request.contains("t=search") {
                        if request.contains("apikey=k0") {
                            tokio::time::sleep(Duration::from_millis(100)).await;
                        }
                        ("200 OK", xml)
                    } else {
                        ("200 OK", NZB.to_owned())
                    };
                let reply = format!(
                    "HTTP/1.1 {status}\r\nContent-Length: {}\r\nConnection: close\r\n\r\n{body}",
                    body.len()
                );
                let _ = socket.write_all(reply.as_bytes()).await;
            });
        }
    });
    let mut ids = Vec::new();
    for n in 0..32 {
        let key = format!("k{n}");
        let mut name = key.repeat(128 / key.len());
        name.push_str(&"N".repeat(128 - name.len()));
        let (code, body) = send(
            addr,
            &admin_request(
                "POST",
                "/api/v1/indexers",
                &json!({
                    "name": name,
                    "api_key": key,
                    "base_url": format!("http://{mock_addr}"),
                })
                .to_string(),
            ),
        )
        .await;
        assert_eq!(code, "HTTP/1.1 200 OK", "{body}");
        let created: Value = serde_json::from_str(&body).unwrap();
        ids.push(created["id"].as_str().unwrap().to_owned());
    }
    let (code, body) = send(
        addr,
        &admin_request("GET", "/api/v1/indexers/search?q=large&limit=500", ""),
    )
    .await;
    assert_eq!(code, "HTTP/1.1 200 OK");
    assert!(
        body.len() <= 1_000_000,
        "search response uses {} bytes",
        body.len()
    );
    assert!(body.len() > 800_000, "the byte budget was not exercised");
    let response: Value = serde_json::from_str(&body).unwrap();
    let rows = response["results"].as_array().unwrap();
    let statuses = response["indexers"].as_array().unwrap();
    assert_eq!(statuses.len(), 32);
    assert!(rows.len() < 32 * 450);
    assert!(
        rows.len() < 1_000,
        "the byte budget must limit results before the count limit"
    );
    assert_eq!(
        statuses
            .iter()
            .map(|s| s["indexer_id"].as_str().unwrap())
            .collect::<Vec<_>>(),
        ids.iter().map(String::as_str).collect::<Vec<_>>()
    );
    assert!(statuses[..31].iter().all(|s| s["ok"] == true));
    assert_eq!(statuses[31]["ok"], false);
    assert!(statuses[31]["error"].is_string());
    assert!(statuses.iter().any(|s| s["error"].is_string()));
    assert_eq!(
        statuses
            .iter()
            .map(|s| s["result_count"].as_u64().unwrap())
            .sum::<u64>(),
        rows.len() as u64
    );
    let mut last = 0;
    for row in rows {
        let position = ids.iter().position(|id| row["indexer_id"] == *id).unwrap();
        assert!(position >= last);
        last = position;
        assert_eq!(row["can_queue"], true);
    }
    let first = rows.first().unwrap()["nzb_ref"].as_str().unwrap();
    let (status, _) = send(
        addr,
        &admin_request(
            "POST",
            "/api/v1/indexers/search/queue",
            &json!({"nzb_ref": first, "category": "movies"}).to_string(),
        ),
    )
    .await;
    assert_eq!(status, "HTTP/1.1 201 Created");
    mock.abort();
    cancel.cancel();
    server.await.unwrap().unwrap();
}

#[tokio::test]
async fn stale_settings_tabs_cannot_overwrite_any_indexer_change() {
    let (addr, cancel, server, _) = start().await;
    let snapshot = async || {
        let (code, body) = send(addr, &admin_request("GET", "/api/v1/settings", "")).await;
        assert_eq!(code, "HTTP/1.1 200 OK");
        serde_json::from_str::<Value>(&body).unwrap()
    };
    let submit = |view: &Value, omit_indexers: bool| {
        let mut body = view["settings"].clone();
        if omit_indexers {
            body.as_object_mut().unwrap().remove("indexers");
        }
        body["indexers_revision"] = view["indexers_revision"].clone();
        body.to_string()
    };
    let tab_a = snapshot().await;
    let tab_b = snapshot().await;
    let (code, created) = send(
        addr,
        &admin_request(
            "POST",
            "/api/v1/indexers",
            &json!({"name":"alpha","base_url":"https://indexer.invalid/api","api_key":"one"})
                .to_string(),
        ),
    )
    .await;
    assert_eq!(code, "HTTP/1.1 200 OK");
    let indexer: Value = serde_json::from_str(&created).unwrap();
    let id = indexer["id"].as_str().unwrap();
    let first = admin_request("PUT", "/api/v1/settings", &submit(&tab_a, false));
    let second = admin_request("PUT", "/api/v1/settings", &submit(&tab_b, false));
    let (first, second) = tokio::join!(status(addr, &first), status(addr, &second));
    assert_eq!(first, "HTTP/1.1 409 Conflict");
    assert_eq!(second, "HTTP/1.1 409 Conflict");
    let current = snapshot().await;
    assert_ne!(tab_a["indexers_revision"], current["indexers_revision"]);
    assert_eq!(
        status(
            addr,
            &admin_request("PUT", "/api/v1/settings", &submit(&current, true))
        )
        .await,
        "HTTP/1.1 200 OK"
    );
    let mut edited = indexer.clone();
    edited["api_key"] = json!("two");
    assert_eq!(
        status(
            addr,
            &admin_request(
                "PUT",
                &format!("/api/v1/indexers/{id}"),
                &edited.to_string()
            )
        )
        .await,
        "HTTP/1.1 200 OK"
    );
    assert_eq!(
        status(
            addr,
            &admin_request("PUT", "/api/v1/settings", &submit(&current, false))
        )
        .await,
        "HTTP/1.1 409 Conflict"
    );
    let before_toggle = snapshot().await;
    assert_eq!(
        status(
            addr,
            &admin_request(
                "PATCH",
                &format!("/api/v1/indexers/{id}/enabled"),
                r#"{"enabled":false}"#
            ),
        )
        .await,
        "HTTP/1.1 200 OK"
    );
    assert_eq!(
        status(
            addr,
            &admin_request("PUT", "/api/v1/settings", &submit(&before_toggle, false))
        )
        .await,
        "HTTP/1.1 409 Conflict"
    );
    let before_delete = snapshot().await;
    assert_eq!(
        status(
            addr,
            &admin_request("DELETE", &format!("/api/v1/indexers/{id}"), "")
        )
        .await,
        "HTTP/1.1 204 No Content"
    );
    assert_eq!(
        status(
            addr,
            &admin_request("PUT", "/api/v1/settings", &submit(&before_delete, false))
        )
        .await,
        "HTTP/1.1 409 Conflict"
    );
    let last = snapshot().await;
    assert_eq!(last["settings"]["indexers"].as_array().unwrap().len(), 0);
    cancel.cancel();
    server.await.unwrap().unwrap();
}
