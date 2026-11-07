//! The server answers the SAB API, the admin API and `WebDAV` on one port, applies settings
//! live and shuts down on cancel.
#![allow(clippy::unwrap_used)] // Test helpers outside `#[test]` fns; panics are the assertions.

use std::net::SocketAddr;
use std::process::Stdio;
use std::sync::Mutex;
use std::sync::atomic::{AtomicUsize, Ordering};
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
    let mut settings = serde_json::from_str::<Value>(&body).unwrap()["settings"].take();
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
    let mut settings = serde_json::from_str::<Value>(&body).unwrap()["settings"].take();
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
    let body = r#"{"note":"sabkey leaked, adminkey too","include_traces":true}"#;
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
    assert!(!all.contains("sabkey") && !all.contains("adminkey"));
    let mut note = String::new();
    zip.by_name("NOTE.txt")
        .unwrap()
        .read_to_string(&mut note)
        .unwrap();
    assert_eq!(note, "[REDACTED] leaked, [REDACTED] too\n");
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
    let mut settings = serde_json::from_str::<Value>(&body).unwrap()["settings"].take();
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
    let mut settings = serde_json::from_str::<Value>(&body).unwrap()["settings"].take();
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
