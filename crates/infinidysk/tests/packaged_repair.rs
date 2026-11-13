//! Run inside a disposable release image with /dev/fuse and mount permission.
#![cfg(target_os = "linux")]
#![allow(clippy::unwrap_used)] // Test setup and behavioral assertions.

use std::fmt::Write as _;
use std::fs::OpenOptions;
use std::path::Path;
use std::process::Stdio;
use std::time::Duration;

use infinidysk_core::{
    BlobId, FileMeta, ItemId, ItemKind, JobId, NzbFile, Segment, Segments, blob,
};
use infinidysk_db::{Db, Item, JobStatus, repair::Owner, repair_jobs::Job};
use infinidysk_testkit::{Article, LoopbackServer, post};
use serde_json::{Value, json};
use sha2::{Digest as _, Sha256};
use tokio::{
    net::TcpListener,
    process::{Child, Command},
    time,
};

const ARTICLE: usize = 512;
const PLAIN: &[u8] = include_bytes!("../../archive/tests/fixtures/Rar5.none.rar");
const ENCRYPTED: &[u8] = include_bytes!("../../archive/tests/fixtures/aes-5hp.rar");

fn headers() -> [(&'static str, &'static [u8], Vec<Article>); 4] {
    // par2 create -s512 -c2 -n1 headers.par2 plain.rar encrypted.rar
    [
        ("plain.rar", PLAIN),
        ("encrypted.rar", ENCRYPTED),
        (
            "headers.par2",
            include_bytes!("fixtures/headers.par2").as_slice(),
        ),
        (
            "headers.vol0+2.par2",
            include_bytes!("fixtures/headers.vol0+2.par2").as_slice(),
        ),
    ]
    .map(|(name, data)| (name, data, post(name, name, data, ARTICLE)))
}

fn nzb(files: &[(&str, &[u8], Vec<Article>)]) -> String {
    let mut xml = "<nzb>".to_owned();
    for (name, data, articles) in files {
        write!(xml, "<file subject=\"{name}\"><segments>").unwrap();
        for (i, article) in articles.iter().enumerate() {
            let length = (data.len() - i * ARTICLE).min(ARTICLE);
            write!(
                xml,
                "<segment bytes=\"{length}\" number=\"{}\">{}</segment>",
                i + 1,
                article.message_id
            )
            .unwrap();
        }
        xml.push_str("</segments></file>");
    }
    xml.push_str("</nzb>");
    xml
}

#[tokio::test(flavor = "multi_thread")]
#[ignore = "needs the packaged binary, /dev/fuse, and permission to mount"]
async fn packaged_worker_recovers_missing_plain_and_encrypted_archive_headers() {
    let files = headers();
    let provider =
        LoopbackServer::start(files.iter().enumerate().flat_map(|(i, (_, _, articles))| {
            // Both archive headers are unavailable from the provider throughout the test.
            articles.iter().skip(usize::from(i < 2)).cloned()
        }))
        .await
        .unwrap();
    let listener = TcpListener::bind("127.0.0.1:0").await.unwrap();
    let addr = listener.local_addr().unwrap();
    drop(listener);
    let base = format!("http://{addr}");
    let root = std::env::temp_dir().join(format!("infinidysk-packaged-repair-{}", ItemId::new()));
    std::fs::create_dir(&root).unwrap();
    let mount = root.join("mount");
    let database = format!("sqlite:{}", root.join("data.sqlite").display());
    let providers = json!([{"name":"fixture", "hostname":"127.0.0.1", "port": provider.addr().port(), "tls":false}]);
    let mut app = Command::new("/usr/local/bin/infinidysk")
        .env("INFINIDYSK_LISTEN", addr.to_string())
        .env("INFINIDYSK_DATABASE_URL", &database)
        .env("INFINIDYSK_EXPERIMENTS", "par2-repair")
        .env("INFINIDYSK__MOUNT__DIR", &mount)
        .env("INFINIDYSK__PROVIDERS", providers.to_string())
        .env("INFINIDYSK__AUTH__API_KEY", "fixture-admin")
        .env("INFINIDYSK__SAB__API_KEY", "fixture-sab")
        .env("INFINIDYSK__WEBDAV__PASSWORD", "fixture-dav")
        .kill_on_drop(true)
        .spawn()
        .unwrap();
    let http = reqwest::Client::builder()
        .timeout(Duration::from_secs(5))
        .build()
        .unwrap();
    time::timeout(Duration::from_secs(15), async {
        loop {
            assert!(app.try_wait().unwrap().is_none(), "packaged server exited");
            if let Ok(response) = http.get(format!("{base}/healthz")).send().await
                && response.status().is_success()
            {
                assert_eq!(
                    response.json::<Value>().await.unwrap()["checks"]["mount"],
                    "pass"
                );
                break;
            }
            time::sleep(Duration::from_millis(50)).await;
        }
    })
    .await
    .unwrap();
    let xml = nzb(&files);
    let multipart = format!(
        "--b\r\nContent-Disposition: form-data; name=\"nzbfile\"; filename=\"headers{{{{test}}}}.nzb\"\r\n\r\n{xml}\r\n--b--\r\n"
    );
    let submitted = http
        .post(format!("{base}/api?mode=addfile&apikey=fixture-sab"))
        .header("Content-Type", "multipart/form-data; boundary=b")
        .body(multipart)
        .send()
        .await
        .unwrap()
        .error_for_status()
        .unwrap()
        .json::<Value>()
        .await
        .unwrap();
    let job: JobId = submitted["nzo_ids"][0].as_str().unwrap().parse().unwrap();
    let db = Db::connect(&database).await.unwrap();
    let history = time::timeout(Duration::from_secs(40), async {
        let mut parked = false;
        loop {
            if let Some(history) = db.history_entry(job).await.unwrap() {
                assert_eq!(
                    history.status,
                    JobStatus::Completed,
                    "{:?}",
                    history.fail_message
                );
                assert!(parked, "import must wait for repair before completion");
                break history;
            }
            parked |= db
                .par2_result(Owner::Queue(job), false)
                .await
                .unwrap()
                .is_some_and(|job| matches!(job.state, 0 | 1 | 4));
            time::sleep(Duration::from_millis(20)).await;
        }
    })
    .await
    .unwrap();
    let directory = db
        .item(history.download_dir.unwrap())
        .await
        .unwrap()
        .unwrap();
    // SHA-256 of the member extracted by 7-Zip from the unchanged fixture.
    let plain_hash = [
        0xb2, 0x51, 0xc7, 0x50, 0x1f, 0xb0, 0xf5, 0x5d, 0xd4, 0xa9, 0x2f, 0xea, 0xbe, 0x0a, 0x6f,
        0x57, 0x33, 0xbc, 0x40, 0xa0, 0x26, 0x79, 0x49, 0x81, 0x55, 0xfa, 0xe9, 0xb3, 0x01, 0x38,
        0xfc, 0x53,
    ];
    // Encrypted fixture plaintext is defined independently in archive/tests/encrypted.rs.
    let decrypted: Vec<_> = (0..9001u32)
        .map(|i| (i * 7 + i / 251).to_le_bytes()[0])
        .collect();
    for (member, expected) in [
        ("jpg/test.jpg", plain_hash),
        ("data.bin", Sha256::digest(decrypted).into()),
    ] {
        let path = format!("{}/{member}", directory.path);
        let item = db.item_by_path(&path).await.unwrap().unwrap();
        let status = http
            .get(format!("{base}/api/v1/files/{}/par2", item.id))
            .header("X-Api-Key", "fixture-admin")
            .send()
            .await
            .unwrap()
            .error_for_status()
            .unwrap()
            .json::<Value>()
            .await
            .unwrap();
        assert_eq!(status["repair"]["state"], "repaired");
        assert_eq!(status["repair"]["stored_bytes"], 2 * ARTICLE);
        let completed = status["repair"]["completed_requests"].as_u64().unwrap();
        let missing = status["repair"]["missing_requests"].as_u64().unwrap();
        assert!(completed > missing && missing > 0);
        assert_eq!(
            status["repair"]["verified_bytes"],
            PLAIN.len() + ENCRYPTED.len()
        );
        let dav = http
            .get(format!("{base}{path}"))
            .basic_auth("admin", Some("fixture-dav"))
            .send()
            .await
            .unwrap()
            .error_for_status()
            .unwrap()
            .bytes()
            .await
            .unwrap();
        let from_mount = tokio::fs::read(mount.join(Path::new(path.trim_start_matches('/'))))
            .await
            .unwrap();
        assert_eq!(dav.as_ref(), from_mount);
        assert_eq!(<[u8; 32]>::from(Sha256::digest(&from_mount)), expected);
    }
    let settings = http
        .get(format!("{base}/api/v1/settings"))
        .header("X-Api-Key", "fixture-admin")
        .send()
        .await
        .unwrap()
        .error_for_status()
        .unwrap()
        .json::<Value>()
        .await
        .unwrap();
    let mut settings = settings["settings"].clone();
    settings["repair"]["memory_mb"] = json!(512);
    settings["repair"]["storage_gb"] = json!(20);
    let saved = http
        .put(format!("{base}/api/v1/settings"))
        .header("X-Api-Key", "fixture-admin")
        .json(&settings)
        .send()
        .await
        .unwrap()
        .error_for_status()
        .unwrap()
        .json::<Value>()
        .await
        .unwrap();
    assert_eq!(saved["settings"]["repair"]["storage_gb"], 20);
    assert_eq!(saved["restart_required"], false);
    assert_eq!(
        http.post(format!("{base}/api/v1/health/par2-scan?repair=true"))
            .header("X-Api-Key", "fixture-admin")
            .send()
            .await
            .unwrap()
            .status(),
        reqwest::StatusCode::ACCEPTED
    );
    let backlog = http
        .get(format!("{base}/api/v1/health/par2-scan"))
        .header("X-Api-Key", "fixture-admin")
        .send()
        .await
        .unwrap()
        .error_for_status()
        .unwrap()
        .json::<Value>()
        .await
        .unwrap();
    assert_eq!(backlog["scan"]["repair"], true);
    assert!(backlog["scan"]["total_files"].as_u64().unwrap() >= 2);
    assert_eq!(backlog["limits"]["memory_mb"], 512);
    assert_eq!(backlog["limits"]["storage_gb"], 20);
    assert!(backlog["storage_used_bytes"].as_u64().unwrap() >= (2 * ARTICLE) as u64);
    assert!(backlog["jobs"][0]["id"].is_string());
    assert_eq!(
        http.delete(format!("{base}/api/v1/health/par2-scan"))
            .header("X-Api-Key", "fixture-admin")
            .send()
            .await
            .unwrap()
            .status(),
        reqwest::StatusCode::NO_CONTENT
    );
    db.close().await;
    let pid = app.id().unwrap().to_string();
    assert!(
        Command::new("sh")
            .args(["-c", "kill -TERM \"$1\"", "sh", &pid])
            .status()
            .await
            .unwrap()
            .success()
    );
    assert!(
        time::timeout(Duration::from_secs(15), app.wait())
            .await
            .unwrap()
            .unwrap()
            .success()
    );
    std::fs::remove_dir_all(root).unwrap();
}

async fn wait_ready(app: &mut Child, http: &reqwest::Client, base: &str) {
    time::timeout(Duration::from_secs(15), async {
        loop {
            assert!(app.try_wait().unwrap().is_none(), "packaged server exited");
            if let Ok(response) = http.get(format!("{base}/healthz")).send().await
                && response.status().is_success()
            {
                break;
            }
            time::sleep(Duration::from_millis(20)).await;
        }
    })
    .await
    .unwrap();
}

async fn wait_repair_result(db: &Db, item: ItemId, state: i16) -> Job {
    time::timeout(Duration::from_secs(20), async {
        loop {
            if let Some(job) = db.par2_result(Owner::Item(item), false).await.unwrap() {
                if job.state == state {
                    break job;
                }
                assert!(
                    !matches!(job.state, 2 | 3 | 5 | 6),
                    "unexpected repair result {}: {}",
                    job.state,
                    job.reason
                );
            }
            time::sleep(Duration::from_millis(20)).await;
        }
    })
    .await
    .unwrap()
}

async fn terminate(app: &mut Child) -> Duration {
    let started = time::Instant::now();
    let pid = app.id().unwrap().to_string();
    assert!(
        Command::new("sh")
            .args(["-c", "kill -TERM \"$1\"", "sh", &pid])
            .status()
            .await
            .unwrap()
            .success()
    );
    assert!(
        time::timeout(Duration::from_secs(10), app.wait())
            .await
            .unwrap()
            .unwrap()
            .success()
    );
    started.elapsed()
}

#[tokio::test(flavor = "multi_thread")]
#[ignore = "needs the packaged binary inside a disposable image"]
async fn packaged_worker_reports_memory_limits_and_resumes_after_shutdown() {
    let files = headers();
    let missing = [
        files[0].2[0].message_id.clone(),
        files[1].2[0].message_id.clone(),
    ];
    let stalled = files[0].2[1].message_id.clone();
    let provider = LoopbackServer::start(
        files
            .iter()
            .enumerate()
            .flat_map(|(i, (_, _, articles))| articles.iter().skip(usize::from(i < 2)).cloned()),
    )
    .await
    .unwrap();
    let listener = TcpListener::bind("127.0.0.1:0").await.unwrap();
    let addr = listener.local_addr().unwrap();
    drop(listener);
    let base = format!("http://{addr}");
    let root = std::env::temp_dir().join(format!("infinidysk-repair-faults-{}", ItemId::new()));
    std::fs::create_dir(&root).unwrap();
    let database = format!("sqlite:{}", root.join("data.sqlite").display());
    let db = Db::connect(&database).await.unwrap();
    let parent = db.item(ItemId::CONTENT).await.unwrap().unwrap();
    let mut item = Item::child(&parent, "fault-source.rar", ItemKind::NzbFile).unwrap();
    let meta = BlobId::new();
    let source = BlobId::new();
    item.meta_blob = Some(meta);
    item.nzb_blob = Some(source);
    item.size = Some(u64::try_from(PLAIN.len()).unwrap());
    let metadata = blob::encode_meta(&FileMeta::Nzb(NzbFile {
        segments: Segments {
            items: files[0]
                .2
                .iter()
                .enumerate()
                .map(|(i, article)| Segment {
                    id: article.message_id.clone(),
                    size: u32::try_from((PLAIN.len() - i * ARTICLE).min(ARTICLE)).unwrap(),
                    fallbacks: vec![],
                })
                .collect(),
            sizes_trusted: true,
            missing: vec![0],
            corrupt: vec![],
        },
        proof: None,
    }))
    .unwrap();
    db.insert(
        &[item.clone()],
        &[
            (meta, &metadata),
            (source, &blob::encode_nzb(nzb(&files).as_bytes()).unwrap()),
        ],
    )
    .await
    .unwrap();
    let output = OpenOptions::new()
        .create(true)
        .append(true)
        .open(root.join("output.log"))
        .unwrap();
    let providers = json!([{"name":"fixture", "hostname":"127.0.0.1", "port":provider.addr().port(), "tls":false}]).to_string();
    let start = || {
        Command::new("/usr/local/bin/infinidysk")
            .env("INFINIDYSK_CONFIG_DIR", &root)
            .env("INFINIDYSK_LISTEN", addr.to_string())
            .env("INFINIDYSK_DATABASE_URL", &database)
            .env("INFINIDYSK_EXPERIMENTS", "par2-repair")
            .env("INFINIDYSK__MOUNT__FUSE", "false")
            .env("INFINIDYSK__MOUNT__DIR", root.join("mount"))
            .env("INFINIDYSK__HEALTH__ENABLED", "false")
            .env("INFINIDYSK__PROVIDERS", &providers)
            .env("INFINIDYSK__AUTH__API_KEY", "fixture-admin")
            .env("INFINIDYSK__WEBDAV__PASSWORD", "fixture-dav")
            .stdout(Stdio::from(output.try_clone().unwrap()))
            .stderr(Stdio::from(output.try_clone().unwrap()))
            .kill_on_drop(true)
            .spawn()
            .unwrap()
    };
    let http = reqwest::Client::builder()
        .timeout(Duration::from_secs(5))
        .build()
        .unwrap();
    let mut app = start();
    wait_ready(&mut app, &http, &base).await;
    let status: Value = http
        .get(format!("{base}/api/v1/status"))
        .header("X-Api-Key", "fixture-admin")
        .send()
        .await
        .unwrap()
        .error_for_status()
        .unwrap()
        .json()
        .await
        .unwrap();
    println!("Packaged revision: {}", status["commit"]);
    let settings: Value = http
        .get(format!("{base}/api/v1/settings"))
        .header("X-Api-Key", "fixture-admin")
        .send()
        .await
        .unwrap()
        .error_for_status()
        .unwrap()
        .json()
        .await
        .unwrap();
    let mut settings = settings["settings"].clone();
    settings["repair"]["memory_mb"] = json!(128);
    http.put(format!("{base}/api/v1/settings"))
        .header("X-Api-Key", "fixture-admin")
        .json(&settings)
        .send()
        .await
        .unwrap()
        .error_for_status()
        .unwrap();
    let action = format!("{base}/api/v1/files/{}/par2", item.id);
    assert_eq!(
        http.post(&action)
            .header("X-Api-Key", "fixture-admin")
            .send()
            .await
            .unwrap()
            .status(),
        reqwest::StatusCode::ACCEPTED
    );
    let failed = wait_repair_result(&db, item.id, 5).await;
    let memory =
        regex::Regex::new(r"needs another (\d+) working-memory bytes; (\d+) bytes available")
            .unwrap();
    let numbers = memory.captures(&failed.reason).unwrap();
    assert!(numbers[1].parse::<u64>().unwrap() > numbers[2].parse::<u64>().unwrap());
    assert!(
        failed.reason.contains("Discovering PAR2 files")
            || failed
                .reason
                .contains("Verifying source files and reconstructing damage")
    );
    assert!(
        failed
            .reason
            .contains("Increase working memory in Settings > PAR2 repair and retry.")
    );
    assert_eq!(
        (failed.attempts, failed.stored_bytes, failed.verified_bytes),
        (1, 0, 0)
    );
    for article in &missing {
        assert_eq!(db.article_repairs(article).await.unwrap(), []);
    }
    wait_ready(&mut app, &http, &base).await;
    let failed_view: Value = http
        .get(&action)
        .header("X-Api-Key", "fixture-admin")
        .send()
        .await
        .unwrap()
        .error_for_status()
        .unwrap()
        .json()
        .await
        .unwrap();
    assert_eq!(failed_view["repair"]["state"], "unavailable");
    assert_eq!(failed_view["repair"]["reason"], failed.reason);
    println!("128 MB refusal: {}; saved bytes: 0", failed.reason);

    settings["repair"]["memory_mb"] = json!(256);
    let saved: Value = http
        .put(format!("{base}/api/v1/settings"))
        .header("X-Api-Key", "fixture-admin")
        .json(&settings)
        .send()
        .await
        .unwrap()
        .error_for_status()
        .unwrap()
        .json()
        .await
        .unwrap();
    assert_eq!(saved["restart_required"], false);
    provider.stall_article(&stalled, 1, Duration::from_secs(30));
    assert_eq!(
        http.post(&action)
            .header("X-Api-Key", "fixture-admin")
            .send()
            .await
            .unwrap()
            .status(),
        reqwest::StatusCode::ACCEPTED
    );
    time::timeout(Duration::from_secs(15), async {
        loop {
            if provider.stalled(&stalled) > 0
                && db
                    .par2_result(Owner::Item(item.id), false)
                    .await
                    .unwrap()
                    .is_some_and(|job| job.state == 1 && job.completed_requests > 0)
            {
                break;
            }
            time::sleep(Duration::from_millis(20)).await;
        }
    })
    .await
    .unwrap();
    let elapsed = terminate(&mut app).await;
    let interrupted = db
        .par2_result(Owner::Item(item.id), false)
        .await
        .unwrap()
        .unwrap();
    assert_eq!(interrupted.id, failed.id);
    assert_eq!(
        (
            interrupted.state,
            interrupted.attempts,
            interrupted.started_at
        ),
        (4, 0, None)
    );
    assert_eq!(interrupted.reason, "interrupted; retrying");
    for article in &missing {
        assert_eq!(db.article_repairs(article).await.unwrap(), []);
    }
    println!(
        "SIGTERM exit: {} ms; persisted state: deferred; attempts used: 0",
        elapsed.as_millis()
    );

    provider.stall_article(&stalled, 0, Duration::ZERO);
    app = start();
    wait_ready(&mut app, &http, &base).await;
    let repaired = wait_repair_result(&db, item.id, 3).await;
    assert_eq!(repaired.id, failed.id);
    assert_eq!(repaired.attempts, 1);
    assert_eq!(repaired.stored_bytes, i64::try_from(2 * ARTICLE).unwrap());
    assert_eq!(
        repaired.verified_bytes,
        i64::try_from(PLAIN.len() + ENCRYPTED.len()).unwrap()
    );
    for article in &missing {
        assert_eq!(db.article_repairs(article).await.unwrap().len(), 1);
    }
    let served = http
        .get(format!("{base}{}", item.path))
        .basic_auth("admin", Some("fixture-dav"))
        .send()
        .await
        .unwrap()
        .error_for_status()
        .unwrap()
        .bytes()
        .await
        .unwrap();
    assert_eq!(served.as_ref(), PLAIN);
    println!(
        "Automatic resume: repaired; attempts used: 1; stored bytes: {}; complete WebDAV bytes match",
        repaired.stored_bytes
    );
    terminate(&mut app).await;
    let logs = std::fs::read_to_string(root.join("output.log")).unwrap();
    assert_eq!(logs.matches("repair did not finish").count(), 1);
    assert!(!logs.contains("panicked at") && !logs.contains("stack backtrace"));
    assert!(
        !logs.contains("ERROR"),
        "unexpected error logged by packaged server"
    );
    db.close().await;
    drop(output);
    std::fs::remove_dir_all(root).unwrap();
}
