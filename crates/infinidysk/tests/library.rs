//! Library conversion end to end: a library folder switches between symlinks and STRM
//! files, the fake Sonarr rescans, and our owner rows follow its new file ids.
#![allow(clippy::unwrap_used)] // Test helpers outside `#[test]` fns; panics are the assertions.

use std::fs;
use std::net::SocketAddr;
use std::path::{Path, PathBuf};
use std::sync::Mutex;
use std::time::{Duration, Instant};

use axum::Json;
use axum::extract::{Request, State};
use infinidysk::app::{Boot, serve};
use infinidysk_core::{ItemId, ItemKind, JobId};
use infinidysk_db::{Conversion, Db, HistoryEntry, Item, JobStatus};
use serde_json::{Value, json};
use tokio::io::{AsyncReadExt, AsyncWriteExt};
use tokio::net::{TcpListener, TcpStream};
use tokio_util::sync::CancellationToken;

/// Calls the fake Sonarr received that change something: (path, body).
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

/// Polls the library state until `done` holds.
async fn until(addr: SocketAddr, what: &str, done: impl Fn(&Value) -> bool) -> Value {
    let deadline = Instant::now() + Duration::from_secs(30);
    loop {
        let (_, view) = admin(addr, "GET", "/api/v1/library", "").await;
        if done(&view) {
            return view;
        }
        assert!(Instant::now() < deadline, "{what}: {view}");
        tokio::time::sleep(Duration::from_millis(50)).await;
    }
}

async fn convert(addr: SocketAddr, preview: Value) -> Value {
    let (code, _) = admin(
        addr,
        "POST",
        "/api/v1/library/preview",
        &preview.to_string(),
    )
    .await;
    assert_eq!(code, "HTTP/1.1 200 OK");
    let view = until(addr, "preview", |v| v["preview_state"] == "ready").await;
    let (code, _) = admin(addr, "POST", "/api/v1/library/confirm", "").await;
    assert_eq!(code, "HTTP/1.1 200 OK");
    until(addr, "conversion", |v| {
        v["state"] == "idle" && v["pending_remaps"] == 0 && v["form_current"] == true
    })
    .await;
    view["preview"].clone()
}

/// A Sonarr whose one episode file is whichever of `e.mkv` or `e.strm` is in the
/// library folder (`/tv` on its side), with id 9 or 19.
async fn fake_sonarr(State(lib): State<PathBuf>, req: Request) -> Json<Value> {
    let path = req.uri().path().to_owned();
    let method = req.method().clone();
    let body: Value = serde_json::from_slice(
        &axum::body::to_bytes(req.into_body(), usize::MAX)
            .await
            .unwrap(),
    )
    .unwrap_or(Value::Null);
    let season = lib.join("Show/Season 1");
    let file = if fs::symlink_metadata(season.join("e.mkv")).is_ok() {
        json!({ "id": 9, "path": "/tv/Show/Season 1/e.mkv", "seriesId": 5,
                "quality": { "quality": { "name": "WEBDL-1080p" } }, "releaseGroup": "GRP" })
    } else {
        json!({ "id": 19, "path": "/tv/Show/Season 1/e.strm", "seriesId": 5,
                "quality": { "quality": { "name": "SDTV" } } })
    };
    let file_id = file["id"].clone();
    Json(match (method.as_str(), path.as_str()) {
        ("GET", "/api/v3/system/status") => json!({ "appName": "Sonarr", "version": "4.0.0" }),
        ("GET", "/api/v3/series") => json!([{ "id": 5 }]),
        ("GET", "/api/v3/episodefile") => json!([file]),
        ("GET", "/api/v3/episode") => {
            json!([{ "id": 101, "episodeFileId": file_id, "monitored": true }])
        }
        ("GET", "/api/v3/rootfolder") => json!([{ "path": "/tv" }]),
        ("GET", "/api/v3/command/77") => json!({ "id": 77, "status": "completed" }),
        ("POST", "/api/v3/command") if body["name"] == "RescanSeries" => {
            CALLS.lock().unwrap().push((path, body));
            json!({ "id": 77 })
        }
        ("PUT", "/api/v3/episodefile/bulk" | "/api/v3/episode/monitor") => {
            CALLS.lock().unwrap().push((path, body));
            json!({})
        }
        ("GET", _) => json!([]),
        _ => json!({}),
    })
}

fn link(path: &Path, target: &str) {
    std::os::unix::fs::symlink(target, path).unwrap();
}

#[tokio::test]
#[allow(clippy::many_single_char_names)] // Items are named after their files.
async fn converts_a_library_both_ways_and_rewrites_on_key_rotation() {
    let root = std::env::temp_dir().join(format!("infinidysk-library-{}", ItemId::new()));
    let (lib, strm, mount) = (root.join("lib"), root.join("strm"), root.join("mnt"));
    let season = lib.join("Show/Season 1");
    fs::create_dir_all(&season).unwrap();
    fs::create_dir_all(mount.join(".ids")).unwrap();
    fs::create_dir_all(&strm).unwrap();

    let sonarr_listener = TcpListener::bind("127.0.0.1:0").await.unwrap();
    let sonarr = sonarr_listener.local_addr().unwrap();
    let fake = axum::Router::new()
        .fallback(fake_sonarr)
        .with_state(lib.clone());
    tokio::spawn(async move { axum::serve(sonarr_listener, fake).await });

    let db_path = root.join("infinidysk.sqlite");
    let listener = TcpListener::bind("127.0.0.1:0").await.unwrap();
    let addr = listener.local_addr().unwrap();
    let boot = Boot {
        listen: addr,
        database_url: format!("sqlite:{}", db_path.display()),
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
    let vars = [
        (
            "INFINIDYSK__AUTH__API_KEY".to_owned(),
            "adminkey".to_owned(),
        ),
        (
            "INFINIDYSK__MOUNT__DIR".to_owned(),
            mount.display().to_string(),
        ),
    ];
    let cancel = CancellationToken::new();
    let server = tokio::spawn({
        let cancel = cancel.clone();
        async move { serve(listener, &boot, vars, cancel).await }
    });

    let (_, view) = admin(addr, "GET", "/api/v1/settings", "").await;
    let mut settings = view["settings"].clone();
    settings["indexers_revision"] = view["indexers_revision"].clone();
    let sonarr_id = ItemId::new().0;
    let chaptarr_id = ItemId::new().0;
    settings["arr_instances"] = json!([{
        "id": sonarr_id, "name": "series", "profile": "sonarr",
        "url": format!("http://{sonarr}"), "api_key": "k",
    }, {
        "id": chaptarr_id, "name": "books", "profile": "chaptarr",
        "url": "http://127.0.0.1:9", "api_key": "k",
    }]);
    settings["imports"]["library_dirs"] = json!([{ "path": lib, "arr_path": "/tv" }]);
    settings["imports"]["strm_dir"] = json!(strm);
    settings["network"]["public_url"] = json!("http://media.example");
    // The items have no metadata to check.
    settings["health"]["enabled"] = json!(false);
    let (code, _) = admin(addr, "PUT", "/api/v1/settings", &settings.to_string()).await;
    assert_eq!(code, "HTTP/1.1 200 OK");

    // Items: an episode Sonarr owns, a video nobody owns, one whose STRM name is taken,
    // one a crash left half converted, and an audiobook Chaptarr owns.
    let db = Db::connect(&format!("sqlite:{}", db_path.display()))
        .await
        .unwrap();
    let tv = db.item_by_path("/content/tv").await.unwrap().unwrap();
    let dir = Item::child(&tv, "job", ItemKind::Directory).unwrap();
    let [e, f, g, h, k, b] = ["e.mkv", "f.mkv", "g.mkv", "h.mkv", "k.mkv", "b.mp4"]
        .map(|name| Item::child(&dir, name, ItemKind::NzbFile).unwrap());
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
    let items = [
        dir,
        e.clone(),
        f.clone(),
        g.clone(),
        h.clone(),
        k.clone(),
        b.clone(),
    ];
    db.add_history(&history, &items, &[]).await.unwrap();
    let pointer = |item: &Item| format!("{}/{}", mount.display(), item.id.ids_path());
    for item in [&e, &f, &g, &h] {
        link(&season.join(&item.name), &pointer(item));
    }
    // Written by hand, relative to the mount.
    link(
        &season.join(&b.name),
        &format!("../../../mnt/{}", b.id.ids_path()),
    );
    fs::write(lib.join("x.mkv"), "a real file").unwrap();
    fs::write(season.join("g.strm"), "not ours").unwrap();

    db.record_owner(
        sonarr_id,
        e.id,
        Some(9),
        "/tv/Show/Season 1/e.mkv",
        Some(job),
    )
    .await
    .unwrap();
    db.record_owner(
        chaptarr_id,
        b.id,
        Some(4),
        "/tv/Show/Season 1/b.mp4",
        Some(job),
    )
    .await
    .unwrap();
    // A crash after writing h's STRM and before moving on.
    let h_strm = season.join("h.strm");
    fs::write(&h_strm, "written before the crash").unwrap();
    db.put_conversion(&Conversion {
        source: season.join("h.mkv").display().to_string(),
        target: h_strm.display().to_string(),
        item: h.id,
        state: 2,
        from_link: true,
        to_link: false,
        original: pointer(&h),
        written: "written before the crash".to_owned(),
        owners: "[]".to_owned(),
        reason: None,
    })
    .await
    .unwrap();
    // A crash after removing k's source, in a folder no longer configured.
    let gone = root.join("gone");
    fs::create_dir_all(&gone).unwrap();
    let k_strm = gone.join("k.strm");
    fs::write(&k_strm, "written before the crash").unwrap();
    db.record_owner(sonarr_id, k.id, None, "/gone/k.strm", Some(job))
        .await
        .unwrap();
    let owned = json!([{
        "instance": sonarr_id, "from": "/gone/k.mkv", "to": "/gone/k.strm",
        "file_id": null, "media": null, "details": null, "monitor": [], "settled": false,
    }]);
    db.put_conversion(&Conversion {
        source: gone.join("k.mkv").display().to_string(),
        target: k_strm.display().to_string(),
        item: k.id,
        state: 3,
        from_link: true,
        to_link: false,
        original: pointer(&k),
        written: "written before the crash".to_owned(),
        owners: owned.to_string(),
        reason: None,
    })
    .await
    .unwrap();

    // Symlinks to STRM files.
    let preview = convert(addr, json!({ "strategy": "strm" })).await;
    assert_eq!(preview["folders"][0]["ok"], true, "{preview}");
    assert_eq!(preview["folders"][0]["arr_roots"], json!(["series: /tv"]));
    assert_eq!(preview["arrs"], json!([{ "name": "series", "files": 1 }]));
    assert_eq!(preview["warnings"][0], "Plex cannot play STRM files.");
    let strm_of = |name: &str| fs::read_to_string(season.join(name)).unwrap();
    assert!(
        strm_of("e.strm").starts_with(&format!(
            "http://media.example/view/{}?downloadKey=",
            e.id.ids_path()
        )),
        "{}",
        strm_of("e.strm")
    );
    assert!(strm_of("f.strm").ends_with("&extension=mkv"));
    assert!(fs::symlink_metadata(season.join("e.mkv")).is_err());
    assert_eq!(
        strm_of("g.strm"),
        "not ours",
        "a foreign file is never replaced"
    );
    assert!(fs::symlink_metadata(season.join("g.mkv")).is_ok());
    assert_eq!(
        fs::read_link(season.join("b.mp4"))
            .unwrap()
            .display()
            .to_string(),
        pointer(&b),
        "Chaptarr's file stays a symlink, pointed at the mount"
    );
    assert!(fs::symlink_metadata(season.join("b.strm")).is_err());
    assert_eq!(strm_of("h.strm"), "written before the crash");
    assert!(
        fs::symlink_metadata(season.join("h.mkv")).is_err(),
        "the interrupted conversion finished"
    );
    assert_eq!(
        fs::read_to_string(lib.join("x.mkv")).unwrap(),
        "a real file"
    );
    let owners = db.item_owners(e.id).await.unwrap();
    assert_eq!(owners[0].1.path, "/tv/Show/Season 1/e.strm");
    assert_eq!(owners[0].1.file_id, Some(19));
    let calls = std::mem::take(&mut *CALLS.lock().unwrap());
    assert_eq!(
        calls,
        [
            (
                "/api/v3/command".to_owned(),
                json!({ "name": "RescanSeries", "seriesId": 5 })
            ),
            (
                "/api/v3/episodefile/bulk".to_owned(),
                json!([{ "id": 19, "quality": { "quality": { "name": "WEBDL-1080p" } },
                         "releaseGroup": "GRP" }])
            ),
            (
                "/api/v3/episode/monitor".to_owned(),
                json!({ "episodeIds": [101], "monitored": true })
            ),
        ]
    );
    let (_, view) = admin(addr, "GET", "/api/v1/library", "").await;
    let problems = view["progress"]["problems"].to_string();
    assert!(problems.contains("g.strm already exists"), "{problems}");
    assert!(
        problems.contains("outside the library folders"),
        "{problems}"
    );
    tokio::time::sleep(Duration::from_millis(300)).await;
    let (_, later) = admin(addr, "GET", "/api/v1/library", "").await;
    assert_eq!(
        later["progress"]["problems_total"], view["progress"]["problems_total"],
        "a kept row is not retried at once"
    );
    assert_eq!(
        later["progress"]["problems"]
            .to_string()
            .matches("outside the library folders")
            .count(),
        1,
        "and is listed once"
    );
    assert_eq!(
        fs::read_to_string(&k_strm).unwrap(),
        "written before the crash"
    );
    assert_eq!(
        db.item_owners(k.id).await.unwrap()[0].1.path,
        "/gone/k.strm",
        "an unreachable conversion keeps its owner"
    );
    assert!(
        db.conversions()
            .await
            .unwrap()
            .iter()
            .any(|c| c.item == k.id && c.state == 3),
        "and its journal entry"
    );

    // A new key rewrites every STRM in place; the old one keeps working meanwhile.
    let before = strm_of("e.strm");
    convert(addr, json!({ "new_key": true })).await;
    assert_ne!(strm_of("e.strm"), before);
    assert_eq!(strm_of("e.strm").len(), before.len());
    let (_, view) = admin(addr, "GET", "/api/v1/library", "").await;
    assert_eq!(view["previous_keys"].as_array().unwrap().len(), 1);
    assert!(view["previous_keys"][0]["retires_ms"].is_i64());
    let (_, view) = admin(addr, "POST", "/api/v1/library/keys/revoke", "").await;
    assert_eq!(view["previous_keys"], json!([]));

    // And back to symlinks.
    convert(addr, json!({ "strategy": "symlinks" })).await;
    let target = fs::read_link(season.join("e.mkv")).unwrap();
    assert_eq!(target.display().to_string(), pointer(&e));
    assert!(fs::symlink_metadata(season.join("e.strm")).is_err());
    let owners = db.item_owners(e.id).await.unwrap();
    assert_eq!(owners[0].1.path, "/tv/Show/Season 1/e.mkv");
    assert_eq!(owners[0].1.file_id, Some(9));

    db.close().await;
    cancel.cancel();
    server.await.unwrap().unwrap();
    let _ = fs::remove_dir_all(root);
}
