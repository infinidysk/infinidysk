//! Maintenance jobs end to end. Remove unlinked files: a dry run lists only files
//! outside the download history that no library symlink points to, confirming it
//! removes them, and a real run removes a file whose link went away. Remove dead links:
//! only our links to deleted items go, and the fake Sonarr that lists one drops it and
//! searches.
#![allow(clippy::unwrap_used)] // Test helpers outside `#[test]` fns; panics are the assertions.

use std::fs;
use std::net::SocketAddr;
use std::path::Path;
use std::sync::Mutex;
use std::time::{Duration, Instant};

use axum::Json;
use axum::extract::Request;
use infinidysk::app::{Boot, serve};
use infinidysk_core::{ItemId, ItemKind, JobId};
use infinidysk_db::{Db, HistoryEntry, Item, JobStatus};
use serde_json::{Value, json};
use tokio::io::{AsyncReadExt, AsyncWriteExt};
use tokio::net::{TcpListener, TcpStream};
use tokio::task::JoinHandle;
use tokio_util::sync::CancellationToken;

const JOB: &str = "/api/v1/maintenance/remove-unlinked";
const DEAD: &str = "/api/v1/maintenance/remove-dead-links";

/// Calls the fake Sonarr received that change something: (method and path, body).
static CALLS: Mutex<Vec<(String, Value)>> = Mutex::new(Vec::new());

async fn admin(addr: SocketAddr, method: &str, path: &str, body: &str) -> (String, Value) {
    let request = format!(
        "{method} {path} HTTP/1.1\r\nHost: x\r\nConnection: close\r\nX-Api-Key: adminkey\r\n\
         Content-Type: application/json\r\nContent-Length: {}\r\n\r\n{body}",
        body.len()
    );
    let mut stream = TcpStream::connect(addr).await.unwrap();
    stream.write_all(request.as_bytes()).await.unwrap();
    let mut response = String::new();
    stream.read_to_string(&mut response).await.unwrap();
    let (head, body) = response.split_once("\r\n\r\n").unwrap();
    let status = head.lines().next().unwrap().to_owned();
    (status, serde_json::from_str(body).unwrap_or(Value::Null))
}

/// Starts a run and returns its report once it ends.
async fn run(addr: SocketAddr, path: &str, body: Value) -> Value {
    let job = path.split('/').nth(4).unwrap();
    let (code, started) = admin(addr, "POST", path, &body.to_string()).await;
    assert_eq!(code, "HTTP/1.1 200 OK", "{started}");
    let deadline = Instant::now() + Duration::from_secs(30);
    loop {
        let (_, view) = admin(addr, "GET", "/api/v1/maintenance", "").await;
        let report = &view["jobs"]
            .as_array()
            .unwrap()
            .iter()
            .find(|j| j["job"] == job)
            .unwrap()["report"];
        if view["running"].is_null() && !report.is_null() {
            return report.clone();
        }
        assert!(Instant::now() < deadline, "job did not end: {view}");
        tokio::time::sleep(Duration::from_millis(50)).await;
    }
}

/// A one-minute maintenance window half a day away, so a scheduled job never holds the slot.
fn closed_window() -> [(String, String); 2] {
    let start = jiff::Zoned::now()
        .time()
        .wrapping_add(jiff::Span::new().hours(12));
    let end = start.wrapping_add(jiff::Span::new().minutes(1));
    [
        (
            "INFINIDYSK__MAINTENANCE__WINDOW__START".to_owned(),
            start.strftime("%H:%M").to_string(),
        ),
        (
            "INFINIDYSK__MAINTENANCE__WINDOW__END".to_owned(),
            end.strftime("%H:%M").to_string(),
        ),
    ]
}

/// Serves an install whose database is `root/infinidysk.sqlite` and mount `root/mnt`.
async fn start(
    root: &Path,
    extra: &[(&str, &str)],
) -> (
    SocketAddr,
    JoinHandle<anyhow::Result<infinidysk::app::Exit>>,
    CancellationToken,
) {
    let listener = TcpListener::bind("127.0.0.1:0").await.unwrap();
    let addr = listener.local_addr().unwrap();
    let boot = Boot {
        listen: addr,
        database_url: format!("sqlite:{}", root.join("infinidysk.sqlite").display()),
        database_source: infinidysk::database::Source::Env,
        uid: 0,
        gid: 0,
        trace_minutes: 0,
        metrics_path: root.join("metrics.sqlite"),
        log_dir: root.join("logs"),
        home: root.join("config"),
        config_dir: root.join("config"),
        ffprobe: "ffprobe".to_owned(),
    };
    let mut vars = vec![
        (
            "INFINIDYSK__AUTH__API_KEY".to_owned(),
            "adminkey".to_owned(),
        ),
        (
            "INFINIDYSK__MOUNT__DIR".to_owned(),
            root.join("mnt").display().to_string(),
        ),
    ];
    vars.extend(closed_window());
    vars.extend(
        extra
            .iter()
            .map(|(k, v)| ((*k).to_owned(), (*v).to_owned())),
    );
    let cancel = CancellationToken::new();
    let server = tokio::spawn({
        let cancel = cancel.clone();
        async move { serve(listener, &boot, vars, cancel).await }
    });
    (addr, server, cancel)
}

/// A finished download with `names` in its folder under `/content/tv`.
async fn download(db: &Db, folder: &str, names: &[&str]) -> (JobId, Vec<Item>) {
    let tv = db.item_by_path("/content/tv").await.unwrap().unwrap();
    let mut dir = Item::child(&tv, folder, ItemKind::Directory).unwrap();
    // Older than any run, so the cleanup may consider them.
    dir.created_at = 1;
    let mut items = vec![dir.clone()];
    for name in names {
        let mut file = Item::child(&dir, name, ItemKind::NzbFile).unwrap();
        file.created_at = 1;
        items.push(file);
    }
    let job = JobId::new();
    let history = HistoryEntry {
        id: job,
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
    db.add_history(&history, &items, &[]).await.unwrap();
    (job, items.split_off(1))
}

#[tokio::test]
async fn removes_only_files_no_library_link_points_to() {
    let root = std::env::temp_dir().join(format!("infinidysk-maintenance-{}", ItemId::new()));
    let (lib, mount) = (root.join("lib"), root.join("mnt"));
    fs::create_dir_all(&lib).unwrap();
    fs::create_dir_all(mount.join(".ids")).unwrap();

    let db_path = root.join("infinidysk.sqlite");
    let (addr, server, cancel) = start(&root, &[]).await;

    let (code, refused) = admin(addr, "POST", &format!("{JOB}/run"), "{}").await;
    assert_eq!(
        code, "HTTP/1.1 400 Bad Request",
        "no library folders: {refused}"
    );

    let (_, view) = admin(addr, "GET", "/api/v1/settings", "").await;
    let mut settings = view["settings"].clone();
    settings["imports"]["library_dirs"] = json!([{ "path": lib, "arr_path": "/tv" }]);
    settings["health"]["enabled"] = json!(false);
    let (code, _) = admin(addr, "PUT", "/api/v1/settings", &settings.to_string()).await;
    assert_eq!(code, "HTTP/1.1 200 OK");

    // `kept` stays in the history; `old` and `gone` were cleared from it. Six of old's
    // files are in the library, one is not, and nothing links to gone's file.
    let db = Db::connect(&format!("sqlite:{}", db_path.display()))
        .await
        .unwrap();
    let (_, kept) = download(&db, "kept", &["k.mkv"]).await;
    let names = [
        "a.mkv", "b.mkv", "c.mkv", "d.mkv", "e.mkv", "f.mkv", "u.mkv",
    ];
    let (old_job, old) = download(&db, "old", &names).await;
    let (gone_job, gone) = download(&db, "gone", &["g.mkv"]).await;
    db.delete_history(&[old_job, gone_job]).await.unwrap();
    let pointer = |item: &Item| format!("{}/{}", mount.display(), item.id.ids_path());
    for (index, item) in old[..6].iter().enumerate() {
        let target = if index == 0 {
            format!("{}.mkv", pointer(item))
        } else {
            pointer(item)
        };
        std::os::unix::fs::symlink(target, lib.join(&item.name)).unwrap();
    }
    let exists = async |item: &Item| db.item_by_path(&item.path).await.unwrap().is_some();

    let report = run(addr, &format!("{JOB}/run"), json!({ "dry_run": true })).await;
    assert_eq!(report["outcome"], "finished", "{report}");
    assert_eq!(report["checked"], 8);
    assert_eq!(report["unlinked"], 2);
    assert_eq!(report["removed"], 0);
    let (_, page) = admin(addr, "GET", &format!("{JOB}/report"), "").await;
    let listed: Vec<&str> = page["files"]
        .as_array()
        .unwrap()
        .iter()
        .map(|f| f["path"].as_str().unwrap())
        .collect();
    assert_eq!(listed, [gone[0].path.as_str(), old[6].path.as_str()]);
    assert!(
        exists(&old[6]).await && exists(&gone[0]).await,
        "a dry run deletes nothing"
    );

    let confirm = json!({ "at_ms": report["at_ms"] });
    let report = run(addr, &format!("{JOB}/report/confirm"), confirm).await;
    assert_eq!(report["trigger"], "review", "{report}");
    assert_eq!(report["removed"], 2);
    assert_eq!(report["folders_removed"], 1, "gone's folder is empty now");
    assert!(!exists(&old[6]).await && !exists(&gone[0]).await);
    assert!(db.item_by_path("/content/tv/gone").await.unwrap().is_none());
    for item in old[..6].iter().chain(&kept) {
        assert!(exists(item).await, "{} was removed", item.path);
    }

    // Unlinking one more file in the library: a real run removes just that one.
    fs::remove_file(lib.join(&old[5].name)).unwrap();
    let report = run(addr, &format!("{JOB}/run"), json!({ "dry_run": false })).await;
    assert_eq!(report["outcome"], "finished", "{report}");
    assert_eq!(
        (report["checked"].as_u64(), report["removed"].as_u64()),
        (Some(6), Some(1))
    );
    assert!(!exists(&old[5]).await);
    assert!(exists(&old[0]).await && exists(&kept[0]).await);

    db.close().await;
    cancel.cancel();
    server.await.unwrap().unwrap();
    let _ = fs::remove_dir_all(root);
}

/// After an upgrade the app mounts elsewhere than the C# install did, and library links
/// still lead into the C# mount folder the stored settings name. They stay ours; a link
/// into an unrelated folder does not.
#[tokio::test]
async fn links_into_the_upgraded_installs_mount_folder_stay_ours() {
    let root = std::env::temp_dir().join(format!("infinidysk-old-mount-{}", ItemId::new()));
    let (lib, mount, old) = (root.join("lib"), root.join("mnt"), root.join("old"));
    fs::create_dir_all(&lib).unwrap();
    fs::create_dir_all(mount.join(".ids")).unwrap();

    // Started once after the upgrade already, recording only the current mount.
    let db = Db::connect(&format!(
        "sqlite:{}",
        root.join("infinidysk.sqlite").display()
    ))
    .await
    .unwrap();
    let stored_mount = json!({ "dir": old }).to_string();
    let form = json!({ "form": null, "mount_dirs": [mount] }).to_string();
    db.write_config(&[("mount", &stored_mount), ("library.form", &form)], &[])
        .await
        .unwrap();

    let (addr, server, cancel) = start(&root, &[]).await;
    let (_, view) = admin(addr, "GET", "/api/v1/settings", "").await;
    let mut settings = view["settings"].clone();
    settings["imports"]["library_dirs"] = json!([{ "path": lib, "arr_path": "/tv" }]);
    settings["health"]["enabled"] = json!(false);
    let (code, _) = admin(addr, "PUT", "/api/v1/settings", &settings.to_string()).await;
    assert_eq!(code, "HTTP/1.1 200 OK");

    let (_, live) = download(&db, "live", &["l.mkv"]).await;
    let link = |name: &str, dir: &Path, id: ItemId| {
        let target = format!("{}/{}", dir.display(), id.ids_path());
        std::os::unix::fs::symlink(target, lib.join(name)).unwrap();
    };
    link("live.mkv", &old, live[0].id);
    link("dead.mkv", &old, ItemId::new());
    link("foreign.mkv", &root.join("elsewhere"), ItemId::new());

    let report = run(addr, &format!("{DEAD}/run"), json!({ "dry_run": true })).await;
    assert_eq!(
        (report["checked"].as_u64(), report["unlinked"].as_u64()),
        (Some(2), Some(1)),
        "{report}"
    );
    let (_, page) = admin(addr, "GET", &format!("{DEAD}/report"), "").await;
    assert!(
        page["files"][0]["path"]
            .as_str()
            .unwrap()
            .ends_with("/dead.mkv"),
        "{page}"
    );

    db.close().await;
    cancel.cancel();
    server.await.unwrap().unwrap();
    let _ = fs::remove_dir_all(root);
}

/// A Sonarr listing `a.mkv` (file 9) and `b.mkv` (file 10) under `/tv/Show`, whose record
/// of file 10 has moved on to another path since.
async fn fake_sonarr(req: Request) -> Json<Value> {
    let path = req.uri().path().to_owned();
    let method = req.method().to_string();
    let body: Value = serde_json::from_slice(
        &axum::body::to_bytes(req.into_body(), usize::MAX)
            .await
            .unwrap(),
    )
    .unwrap_or(Value::Null);
    let file = |id: i64, name: &str| json!({ "id": id, "path": format!("/tv/Show/{name}"), "seriesId": 5 });
    Json(match (method.as_str(), path.as_str()) {
        ("GET", "/api/v3/system/status") => json!({ "appName": "Sonarr", "version": "4.0.0" }),
        ("GET", "/api/v3/series") => json!([{ "id": 5 }]),
        ("GET", "/api/v3/episodefile") => json!([file(9, "a.mkv"), file(10, "b.mkv")]),
        ("GET", "/api/v3/episodefile/9") => file(9, "a.mkv"),
        ("GET", "/api/v3/episodefile/10") => file(10, "b2.mkv"),
        ("GET", "/api/v3/episode") => json!([
            { "id": 101, "episodeFileId": 9, "monitored": true },
            { "id": 102, "episodeFileId": 10, "monitored": true },
        ]),
        ("GET", "/api/v3/rootfolder") => json!([{ "path": "/tv" }]),
        ("DELETE", _) | ("POST", "/api/v3/command") => {
            CALLS
                .lock()
                .unwrap()
                .push((format!("{method} {path}"), body));
            json!({})
        }
        ("GET", _) => json!([]),
        _ => json!({}),
    })
}

#[tokio::test]
async fn removes_dead_links_and_has_the_arr_search_again() {
    let root = std::env::temp_dir().join(format!("infinidysk-dead-links-{}", ItemId::new()));
    let (lib, mount) = (root.join("lib"), root.join("mnt"));
    let show = lib.join("Show");
    fs::create_dir_all(&show).unwrap();
    fs::create_dir_all(mount.join(".ids")).unwrap();

    let sonarr_listener = TcpListener::bind("127.0.0.1:0").await.unwrap();
    let sonarr = sonarr_listener.local_addr().unwrap();
    let fake = axum::Router::new().fallback(fake_sonarr);
    tokio::spawn(async move { axum::serve(sonarr_listener, fake).await });

    let (addr, server, cancel) =
        start(&root, &[("INFINIDYSK__IMPORTS__STRM_KEY", "strmkey")]).await;
    let (_, view) = admin(addr, "GET", "/api/v1/settings", "").await;
    let mut settings = view["settings"].clone();
    assert_eq!(
        settings["maintenance"]["scheduled"],
        json!(["remove-dead-links"]),
        "on by default"
    );
    settings["arr_instances"] = json!([{
        "id": ItemId::new().0, "name": "series", "profile": "sonarr",
        "url": format!("http://{sonarr}"), "api_key": "k",
    }]);
    settings["imports"]["library_dirs"] = json!([{ "path": lib, "arr_path": "/tv" }]);
    settings["network"]["public_url"] = json!("http://media.example");
    settings["health"]["enabled"] = json!(false);
    let (code, _) = admin(addr, "PUT", "/api/v1/settings", &settings.to_string()).await;
    assert_eq!(code, "HTTP/1.1 200 OK");

    // One live link; links and a STRM file to deleted items; a link into someone else's
    // mount and a real file, which are not ours.
    let db = Db::connect(&format!(
        "sqlite:{}",
        root.join("infinidysk.sqlite").display()
    ))
    .await
    .unwrap();
    let (_, live) = download(&db, "live", &["l.mkv"]).await;
    let pointer = |id: ItemId| format!("{}/{}", mount.display(), id.ids_path());
    let link = |name: &str, target: &str| {
        std::os::unix::fs::symlink(target, show.join(name)).unwrap();
    };
    link("live.mkv", &pointer(live[0].id));
    link("a.mkv", &pointer(ItemId::new()));
    link("b.mkv", &pointer(ItemId::new()));
    link(
        "foreign.mkv",
        &format!("/elsewhere/{}", ItemId::new().ids_path()),
    );
    let gone = ItemId::new().ids_path();
    let key = infinidysk_webdav::strm_key("strmkey", &gone);
    fs::write(
        show.join("c.strm"),
        format!("http://media.example/view/{gone}?downloadKey={key}&extension=mkv"),
    )
    .unwrap();
    fs::write(show.join("real.mkv"), "x").unwrap();
    let present = |name: &str| fs::symlink_metadata(show.join(name)).is_ok();

    // Three of four links are dead: far more than a usual run, so it holds for review.
    let report = run(addr, &format!("{DEAD}/run"), json!({ "dry_run": true })).await;
    assert_eq!(report["outcome"], "held", "{report}");
    assert_eq!(
        (report["checked"].as_u64(), report["unlinked"].as_u64()),
        (Some(4), Some(3))
    );
    let (_, page) = admin(addr, "GET", &format!("{DEAD}/report"), "").await;
    let mut listed: Vec<(String, Value)> = page["files"]
        .as_array()
        .unwrap()
        .iter()
        .map(|f| {
            let name = f["path"].as_str().unwrap().rsplit('/').next().unwrap();
            (name.to_owned(), f["arrs"].clone())
        })
        .collect();
    listed.sort_by(|a, b| a.0.cmp(&b.0));
    assert_eq!(
        listed,
        [
            ("a.mkv".to_owned(), json!(["series"])),
            ("b.mkv".to_owned(), json!(["series"])),
            ("c.strm".to_owned(), json!([])),
        ]
    );
    assert!(["a.mkv", "b.mkv", "c.strm"].iter().all(|n| present(n)));
    assert!(CALLS.lock().unwrap().is_empty(), "a dry run calls no Arr");

    let confirm = json!({ "at_ms": report["at_ms"] });
    let report = run(addr, &format!("{DEAD}/report/confirm"), confirm).await;
    assert_eq!(report["trigger"], "review", "{report}");
    assert_eq!(report["removed"], 3);
    for name in ["a.mkv", "b.mkv", "c.strm"] {
        assert!(!present(name), "{name} is still there");
    }
    for name in ["live.mkv", "foreign.mkv", "real.mkv"] {
        assert!(present(name), "{name} was removed");
    }
    // File 10's record moved to another path, so Sonarr keeps it.
    assert_eq!(
        *CALLS.lock().unwrap(),
        [
            ("DELETE /api/v3/episodefile/9".to_owned(), Value::Null),
            (
                "POST /api/v3/command".to_owned(),
                json!({ "name": "EpisodeSearch", "episodeIds": [101] })
            ),
        ]
    );

    db.close().await;
    cancel.cancel();
    server.await.unwrap().unwrap();
    let _ = fs::remove_dir_all(root);
}
