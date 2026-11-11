//! Run inside a disposable release image with /dev/fuse and mount permission.
#![cfg(target_os = "linux")]
#![allow(clippy::unwrap_used)] // Test setup and behavioral assertions.

use std::fmt::Write as _;
use std::path::Path;
use std::time::Duration;

use infinidysk_core::{ItemId, JobId};
use infinidysk_db::{Db, JobStatus, repair::Owner};
use infinidysk_testkit::{Article, LoopbackServer, post};
use serde_json::{Value, json};
use sha2::{Digest as _, Sha256};
use tokio::{net::TcpListener, process::Command, time};

const ARTICLE: usize = 512;
const PLAIN: &[u8] = include_bytes!("../../archive/tests/fixtures/Rar5.none.rar");
const ENCRYPTED: &[u8] = include_bytes!("../../archive/tests/fixtures/aes-5hp.rar");

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
    // par2 create -s512 -c2 -n1 headers.par2 plain.rar encrypted.rar
    let files = [
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
    .map(|(name, data)| (name, data, post(name, name, data, ARTICLE)));
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
