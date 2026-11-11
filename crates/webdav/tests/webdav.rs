//! `WebDAV` and `/view` over a seeded tree and a loopback NNTP server.
#![allow(clippy::unwrap_used)] // Test helpers outside `#[test]` fns; panics are the assertions.

use std::fmt::Write as _;
use std::sync::atomic::{AtomicU64, Ordering};
use std::sync::{Arc, RwLock};
use std::time::Duration;

use axum::Router;
use axum::body::Body;
use axum::http::{Request, StatusCode, header};
use axum::response::Response;
use http_body_util::BodyExt;
use infinidysk_core::{
    BlobId, FileMeta, ItemId, ItemKind, JobId, NzbFile, Segment, Segments, blob,
};
use infinidysk_db::{Db, HistoryEntry, Item, JobStatus};
use infinidysk_nntp::{Client, Downloads, Endpoint, Pool, PoolConfig, Provider, TlsMode};
use infinidysk_queue::Queue;
use infinidysk_stream::Budget;
use infinidysk_testkit::{LoopbackServer, post};
use infinidysk_vfs::Vfs;
use infinidysk_webdav::{Access, Auth, WebDav, router, sign_view, strm_key};
use sha2::{Digest as _, Sha256};
use tower::ServiceExt;

const ARTICLE: usize = 4096;
/// `user:pass`.
const BASIC: &str = "Basic dXNlcjpwYXNz";

async fn setup(data: &[u8]) -> (LoopbackServer, Router, Item, [Arc<AtomicU64>; 2], Arc<Vfs>) {
    let articles = post("a", "a.mkv", data, ARTICLE);
    let server = LoopbackServer::start(articles.clone()).await.unwrap();
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
    let client = Arc::new(Client::new([Provider {
        pool,
        key: String::new(),
        name: String::new(),
        priority: 0,
        backbone: None,
    }]));

    let path = std::env::temp_dir().join(format!("infinidysk-webdav-{}.sqlite", ItemId::new()));
    let db = Db::connect(&format!("sqlite:{}", path.display()))
        .await
        .unwrap();
    let content = db.item(ItemId::CONTENT).await.unwrap().unwrap();
    let tv = Item::child(&content, "tv", ItemKind::Directory).unwrap();
    let job = Item::child(&tv, "my job", ItemKind::Directory).unwrap();
    let meta = FileMeta::Nzb(NzbFile {
        segments: Segments {
            items: articles
                .iter()
                .enumerate()
                .map(|(i, a)| Segment {
                    id: a.message_id.clone(),
                    size: u32::try_from(ARTICLE.min(data.len() - i * ARTICLE)).unwrap(),
                    fallbacks: Vec::new(),
                })
                .collect(),
            ..Segments::default()
        },
        proof: None,
    });
    let mut file = Item::child(&job, "a.mkv", ItemKind::NzbFile).unwrap();
    let blob_id = BlobId::new();
    file.size = Some(meta.size());
    file.meta_blob = Some(blob_id);
    let encoded = blob::encode_meta(&meta).unwrap();
    db.insert(std::slice::from_ref(&tv), &[]).await.unwrap();
    let history = HistoryEntry {
        id: JobId::new(),
        created_at: 1,
        file_name: "job.nzb".into(),
        job_name: "my job".into(),
        category: "tv".into(),
        status: JobStatus::Completed,
        total_bytes: 0,
        download_seconds: 0,
        fail_message: None,
        download_dir: Some(job.id),
        nzb_blob: None,
        storage: None,
    };
    db.add_history(&history, &[job, file.clone()], &[(blob_id, &encoded)])
        .await
        .unwrap();

    let vfs = Arc::new(Vfs::new(
        db.clone(),
        Arc::clone(&client),
        Budget::new(1 << 20),
        1 << 20,
        "/mnt",
    ));
    let (legacy, previous) = (Arc::new(AtomicU64::new(0)), Arc::new(AtomicU64::new(0)));
    let dav = WebDav {
        vfs: Arc::clone(&vfs),
        queue: Arc::new(Queue::new(db, client)),
        access: RwLock::new(Access {
            auth: Auth::Basic {
                user: "user".into(),
                password: "pass".into(),
            },
            download_key: Some("key".into()),
            link_key: Some("link".into()),
            strm_key: Some("strm".into()),
            previous_strm: vec![("old".into(), Arc::clone(&previous))],
            legacy_strm: Some(Arc::clone(&legacy)),
            trusted_proxies: Vec::new(),
        }),
    };
    (server, router(Arc::new(dav)), file, [legacy, previous], vfs)
}

async fn send(
    app: &Router,
    method: &str,
    uri: &str,
    headers: &[(&str, &str)],
    body: Vec<u8>,
) -> Response {
    let mut request = Request::builder().method(method).uri(uri);
    for (name, value) in headers {
        request = request.header(*name, *value);
    }
    app.clone()
        .oneshot(request.body(Body::from(body)).unwrap())
        .await
        .unwrap()
}

async fn body(response: Response) -> Vec<u8> {
    response
        .into_body()
        .collect()
        .await
        .unwrap()
        .to_bytes()
        .to_vec()
}

#[tokio::test]
async fn publication_waits_for_http_bytes_then_refreshes_the_remaining_response() {
    let data = vec![23; 4 * ARTICLE];
    let (_server, app, file, _, vfs) = setup(&data).await;
    let fence = vfs.read_fence(file.id);
    let response = send(
        &app,
        "GET",
        "/content/tv/my%20job/a.mkv",
        &[("authorization", BASIC)],
        Vec::new(),
    )
    .await;
    assert_eq!(response.status(), StatusCode::OK);
    let mut response = response.into_body();
    assert!(
        tokio::time::timeout(Duration::from_millis(20), fence.write())
            .await
            .is_err()
    );
    let first = response
        .frame()
        .await
        .unwrap()
        .unwrap()
        .into_data()
        .unwrap();
    let received = first.len();
    let held = first.slice(1..3);
    drop(first);
    assert!(
        tokio::time::timeout(Duration::from_millis(20), fence.write())
            .await
            .is_err()
    );
    drop(held);
    let mut publication = fence.write().await;
    publication.retire().unwrap();
    drop(publication);
    assert_eq!(
        response.collect().await.unwrap().to_bytes().as_ref(),
        &data[received..]
    );
    drop(fence.write().await);
}

#[tokio::test]
async fn serves_the_tree_over_webdav() {
    let data: Vec<u8> = (0..3 * ARTICLE + 123)
        .map(|i| u8::try_from(i * 7 % 251).unwrap())
        .collect();
    let (_server, app, file, [legacy, previous], _) = setup(&data).await;
    let auth = [("authorization", BASIC)];

    let r = send(&app, "PROPFIND", "/", &[], Vec::new()).await;
    assert_eq!(r.status(), StatusCode::UNAUTHORIZED);
    let r = send(
        &app,
        "PROPFIND",
        "/",
        &[("authorization", "Basic dXNlcjp4")],
        Vec::new(),
    )
    .await;
    assert_eq!(r.status(), StatusCode::UNAUTHORIZED, "wrong password");

    let r = send(&app, "OPTIONS", "/", &auth, Vec::new()).await;
    assert!(r.headers()["allow"].to_str().unwrap().contains("PROPFIND"));

    let r = send(
        &app,
        "PROPFIND",
        "/",
        &[auth[0], ("depth", "1")],
        Vec::new(),
    )
    .await;
    assert_eq!(r.status(), StatusCode::MULTI_STATUS);
    let xml = String::from_utf8(body(r).await).unwrap();
    for href in [
        "<D:href>/</D:href>",
        "<D:href>/content</D:href>",
        "<D:href>/.ids</D:href>",
    ] {
        assert!(xml.contains(href), "{href} in {xml}");
    }
    let r = send(
        &app,
        "PROPFIND",
        "/",
        &[auth[0], ("depth", "infinity")],
        Vec::new(),
    )
    .await;
    assert_eq!(r.status(), StatusCode::FORBIDDEN);

    let r = send(
        &app,
        "PROPFIND",
        "/completed-symlinks/tv/my%20job",
        &[auth[0], ("depth", "1")],
        Vec::new(),
    )
    .await;
    let xml = String::from_utf8(body(r).await).unwrap();
    assert!(
        xml.contains("<D:href>/completed-symlinks/tv/my%20job/a.mkv.rclonelink</D:href>"),
        "{xml}"
    );
    let id = file.id.to_string();
    let target = format!(
        "/mnt/.ids/{}/{}/{}/{}/{}/{id}",
        &id[..1],
        &id[1..2],
        &id[2..3],
        &id[3..4],
        &id[4..5]
    );
    assert!(xml.contains(&format!(
        "<D:getcontentlength>{}</D:getcontentlength>",
        target.len()
    )));
    let r = send(
        &app,
        "GET",
        "/completed-symlinks/tv/my%20job/a.mkv.rclonelink",
        &auth,
        Vec::new(),
    )
    .await;
    assert_eq!(body(r).await, target.as_bytes());

    let path = "/content/tv/my%20job/a.mkv";
    let r = send(&app, "GET", path, &auth, Vec::new()).await;
    assert_eq!(r.status(), StatusCode::OK);
    assert_eq!(r.headers()[header::CONTENT_TYPE], "video/x-matroska");
    assert!(body(r).await == data, "whole file");
    let r = send(
        &app,
        "GET",
        path,
        &[auth[0], ("range", "bytes=4090-4109")],
        Vec::new(),
    )
    .await;
    assert_eq!(r.status(), StatusCode::PARTIAL_CONTENT);
    assert_eq!(
        r.headers()[header::CONTENT_RANGE].to_str().unwrap(),
        format!("bytes 4090-4109/{}", data.len())
    );
    assert_eq!(body(r).await, data[4090..4110]);
    let r = send(
        &app,
        "GET",
        path,
        &[auth[0], ("range", "bytes=-5")],
        Vec::new(),
    )
    .await;
    assert_eq!(body(r).await, data[data.len() - 5..]);
    let past = format!("bytes={}-", data.len());
    let r = send(&app, "GET", path, &[auth[0], ("range", &past)], Vec::new()).await;
    assert_eq!(r.status(), StatusCode::RANGE_NOT_SATISFIABLE);
    let r = send(&app, "HEAD", path, &auth, Vec::new()).await;
    assert_eq!(
        r.headers()[header::CONTENT_LENGTH].to_str().unwrap(),
        data.len().to_string()
    );

    let r = send(
        &app,
        "GET",
        "/view/content/tv/my%20job/a.mkv?downloadKey=key",
        &[("range", "bytes=0-9")],
        Vec::new(),
    )
    .await;
    assert_eq!(body(r).await, data[..10]);
    let r = send(
        &app,
        "GET",
        "/view/content/tv/my%20job/a.mkv?downloadKey=nope",
        &[],
        Vec::new(),
    )
    .await;
    assert_eq!(r.status(), StatusCode::UNAUTHORIZED);

    let token = sign_view("link", "/content/tv/my job/a.mkv", u64::MAX);
    let expired = sign_view("link", "/content/tv/my job/a.mkv", 1);
    let other = sign_view("link", "/content/tv/my job/b.mkv", u64::MAX);
    for (token, want) in [
        (&token, StatusCode::PARTIAL_CONTENT),
        (&expired, StatusCode::UNAUTHORIZED),
        (&other, StatusCode::UNAUTHORIZED),
    ] {
        let uri = format!("/view/content/tv/my%20job/a.mkv?token={token}&player=p-1");
        let r = send(&app, "GET", &uri, &[("range", "bytes=0-9")], Vec::new()).await;
        assert_eq!(r.status(), want, "{token}");
    }

    // STRM URLs as C# wrote them, per item under `/.ids`.
    let id = file.id.to_string();
    let shards: Vec<String> = id.chars().take(5).map(String::from).collect();
    let ids = format!(".ids/{}/{id}", shards.join("/"));
    let earlier = Sha256::digest(format!("{ids}_strm"))
        .iter()
        .fold(String::new(), |mut s, b| {
            write!(s, "{b:02x}").unwrap();
            s
        });
    for (key, want) in [
        (strm_key("strm", &ids), StatusCode::PARTIAL_CONTENT),
        (earlier, StatusCode::PARTIAL_CONTENT),
        (strm_key("old", &ids), StatusCode::PARTIAL_CONTENT),
        (strm_key("other", &ids), StatusCode::UNAUTHORIZED),
    ] {
        let uri = format!("/view/{ids}?downloadKey={key}&extension=mkv");
        let r = send(&app, "GET", &uri, &[("range", "bytes=0-9")], Vec::new()).await;
        assert_eq!(r.status(), want, "{key}");
    }
    assert_eq!(
        legacy.load(Ordering::Relaxed),
        1,
        "the earlier form is counted"
    );
    assert_eq!(
        previous.load(Ordering::Relaxed),
        1,
        "a replaced key is counted"
    );
    let r = send(
        &app,
        "GET",
        &format!(
            "/view/content/tv/my%20job/a.mkv?downloadKey={}",
            strm_key("strm", &ids)
        ),
        &[],
        Vec::new(),
    )
    .await;
    assert_eq!(
        r.status(),
        StatusCode::UNAUTHORIZED,
        "a STRM key opens its item only"
    );

    let nzb =
        b"<nzb><file poster=\"p\" date=\"0\" subject=\"x.mkv\"><groups><group>a.b</group></groups>\
        <segments><segment bytes=\"10\" number=\"1\">x@y</segment></segments></file></nzb>";
    let r = send(&app, "PUT", "/nzbs/movies/x.nzb", &auth, nzb.to_vec()).await;
    assert_eq!(r.status(), StatusCode::CREATED);
    let r = send(&app, "GET", "/nzbs/movies/x.nzb", &auth, Vec::new()).await;
    assert_eq!(body(r).await, nzb);
    let r = send(
        &app,
        "PUT",
        "/nzbs/movies/bad.nzb",
        &auth,
        b"not xml".to_vec(),
    )
    .await;
    assert_eq!(r.status(), StatusCode::BAD_REQUEST);
    let r = send(&app, "DELETE", "/nzbs/movies/x.nzb", &auth, Vec::new()).await;
    assert_eq!(r.status(), StatusCode::NO_CONTENT);
    let r = send(&app, "DELETE", "/nzbs/movies/x.nzb", &auth, Vec::new()).await;
    assert_eq!(r.status(), StatusCode::NOT_FOUND);

    let r = send(&app, "DELETE", path, &auth, Vec::new()).await;
    assert_eq!(r.status(), StatusCode::FORBIDDEN, "content is read-only");
    let r = send(&app, "PUT", path, &auth, Vec::new()).await;
    assert_eq!(r.status(), StatusCode::FORBIDDEN);
}

#[tokio::test]
async fn deleted_completed_links_go_while_content_stays() {
    let data = vec![7; ARTICLE + 5];
    let (_server, app, _file, _, _) = setup(&data).await;
    let auth = [("authorization", BASIC)];
    let status = |method: &'static str, uri: &'static str| {
        let app = &app;
        async move { send(app, method, uri, &auth, Vec::new()).await.status() }
    };
    let listing = |uri: &'static str| {
        let app = &app;
        async move {
            let r = send(app, "PROPFIND", uri, &[auth[0], ("depth", "1")], Vec::new()).await;
            assert_eq!(r.status(), StatusCode::MULTI_STATUS, "{uri}");
            String::from_utf8(body(r).await).unwrap()
        }
    };
    let job = "/completed-symlinks/tv/my%20job";
    let link = "/completed-symlinks/tv/my%20job/a.mkv.rclonelink";

    for uri in ["/completed-symlinks", "/completed-symlinks/tv"] {
        assert_eq!(status("DELETE", uri).await, StatusCode::FORBIDDEN, "{uri}");
    }
    for uri in [
        "/completed-symlinks/tv/my%20job/a.mkv",
        "/completed-symlinks/tv/missing",
    ] {
        assert_eq!(status("DELETE", uri).await, StatusCode::NOT_FOUND, "{uri}");
    }

    assert_eq!(status("DELETE", link).await, StatusCode::NO_CONTENT);
    assert_eq!(status("GET", link).await, StatusCode::NOT_FOUND);
    assert_eq!(status("DELETE", link).await, StatusCode::NOT_FOUND);
    assert!(!listing(job).await.contains("a.mkv"));

    assert_eq!(status("DELETE", job).await, StatusCode::NO_CONTENT);
    assert_eq!(status("PROPFIND", job).await, StatusCode::NOT_FOUND);
    assert!(!listing("/completed-symlinks/tv").await.contains("my%20job"));

    let r = send(&app, "GET", "/content/tv/my%20job/a.mkv", &auth, Vec::new()).await;
    assert!(body(r).await == data, "content stays readable");
}

#[tokio::test]
async fn unreachable_providers_answer_retryable_503_before_the_body() {
    let data = vec![7; 2 * ARTICLE];
    let (server, app, _, _, _) = setup(&data).await;
    drop(server);
    let path = "/content/tv/my%20job/a.mkv";
    for range in [None, Some("bytes=4090-")] {
        let mut headers = vec![("authorization", BASIC)];
        headers.extend(range.map(|r| ("range", r)));
        let r = send(&app, "GET", path, &headers, Vec::new()).await;
        assert_eq!(r.status(), StatusCode::SERVICE_UNAVAILABLE, "{range:?}");
        assert_eq!(r.headers()[header::RETRY_AFTER], "5");
    }
}
