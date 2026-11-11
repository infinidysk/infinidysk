#![allow(clippy::unwrap_used)] // Test helpers outside `#[test]` fns; panics are the assertions.

use std::sync::Arc;
use std::time::{Duration, Instant};

use infinidysk_nntp::{Bandwidth, Connection, Credentials, Endpoint, Error, TlsMode};
use infinidysk_testkit::{Article, Corpus, LoopbackServer};

const ARTICLE_SIZE: usize = 64 * 1024;

fn corpus_with_corrupt_crc() -> (Corpus, Vec<Article>) {
    let corpus = Corpus::generate(1025, 3, ARTICLE_SIZE);
    let mut corrupt = corpus.articles[0].clone();
    corrupt.message_id = "corrupt@loopback".into();
    let at = corrupt
        .wire
        .windows(7)
        .position(|w| w == b"pcrc32=")
        .unwrap()
        + 7;
    corrupt.wire[at] = if corrupt.wire[at] == b'0' { b'1' } else { b'0' };
    let mut articles = corpus.articles.clone();
    articles.push(corrupt);
    (corpus, articles)
}

async fn pipelined_fetch(server: &LoopbackServer, tls: TlsMode, corpus: &Corpus) {
    let credentials = Credentials {
        username: "user".into(),
        password: "pass".into(),
    };
    let endpoint = Endpoint::new(
        "localhost",
        server.addr().port(),
        tls,
        Some(credentials),
        Duration::from_secs(5),
    )
    .unwrap();
    let mut conn = Connection::connect(&endpoint).await.unwrap();

    conn.send_body("missing@loopback").unwrap();
    conn.send_body("corrupt@loopback").unwrap();
    for article in &corpus.articles {
        conn.send_body(&article.message_id).unwrap();
    }
    conn.flush().await.unwrap();

    let missing = conn.read_body().await.unwrap_err();
    assert!(matches!(missing, Error::NotFound), "{missing:?}");
    let corrupt = conn.read_body().await.unwrap_err();
    assert!(matches!(corrupt, Error::CrcMismatch { .. }), "{corrupt:?}");
    assert!(missing.is_connection_reusable() && corrupt.is_connection_reusable());

    for index in 0..corpus.articles.len() {
        let body = conn.read_body().await.unwrap();
        let start = index * ARTICLE_SIZE;
        assert_eq!(body.offset, start as u64);
        assert_eq!(body.file_size, corpus.data.len() as u64);
        assert_eq!(body.data, corpus.data[start..start + ARTICLE_SIZE]);
    }
    assert_eq!(conn.pending(), 0);
}

#[tokio::test]
async fn rejected_login_explains_itself() {
    let listener = tokio::net::TcpListener::bind("127.0.0.1:0").await.unwrap();
    let port = listener.local_addr().unwrap().port();
    tokio::spawn(async move {
        use tokio::io::{AsyncBufReadExt, AsyncWriteExt};
        let (stream, _) = listener.accept().await.unwrap();
        let (read, mut write) = stream.into_split();
        write.write_all(b"200 ready\r\n").await.unwrap();
        let mut lines = tokio::io::BufReader::new(read).lines();
        while let Some(line) = lines.next_line().await.unwrap() {
            let reply: &[u8] = if line.contains("PASS") {
                b"481 Authentication failed\r\n"
            } else {
                b"381 Password required\r\n"
            };
            write.write_all(reply).await.unwrap();
        }
    });
    let credentials = Credentials {
        username: "user".into(),
        password: "wrong".into(),
    };
    let endpoint = Endpoint::new(
        "127.0.0.1",
        port,
        TlsMode::Plain,
        Some(credentials),
        Duration::from_secs(5),
    )
    .unwrap();
    let Err(error) = Connection::connect(&endpoint).await else {
        panic!("a rejected login fails the connect")
    };
    assert!(
        matches!(error, Error::AuthRejected { code: 481, .. }),
        "{error:?}"
    );
    assert_eq!(
        error.connect_problem(),
        "The server rejected the username or password: 481 Authentication failed"
    );
}

#[tokio::test]
async fn pipelined_bodies_over_plain_tcp() {
    let (corpus, articles) = corpus_with_corrupt_crc();
    let server = LoopbackServer::start(articles).await.unwrap();
    pipelined_fetch(&server, TlsMode::Plain, &corpus).await;
}

#[tokio::test]
async fn pipelined_bodies_over_tls() {
    let (corpus, articles) = corpus_with_corrupt_crc();
    let server = LoopbackServer::start_tls(articles).await.unwrap();
    pipelined_fetch(&server, TlsMode::Unverified, &corpus).await;
}

#[tokio::test]
async fn download_limit_waits_outside_the_read_timeout_and_lifts_live() {
    let corpus = Corpus::generate(1025, 1, 512 * 1024);
    let server = LoopbackServer::start(corpus.articles.clone())
        .await
        .unwrap();
    let bandwidth = Arc::new(Bandwidth::new(Some(100_000)));
    let endpoint = Endpoint::new(
        "127.0.0.1",
        server.addr().port(),
        TlsMode::Plain,
        None,
        Duration::from_millis(500),
    )
    .unwrap()
    .with_bandwidth(Arc::clone(&bandwidth));
    let mut conn = Connection::connect(&endpoint).await.unwrap();

    let lift = tokio::spawn(async move {
        tokio::time::sleep(Duration::from_secs(1)).await;
        bandwidth.set_limit(None);
    });
    let started = Instant::now();
    let body = conn.body(&corpus.articles[0].message_id).await.unwrap();
    let elapsed = started.elapsed();
    lift.await.unwrap();

    assert_eq!(body.data, corpus.data);
    // Unlimited takes milliseconds; held to the limit to the end it takes over 5 s.
    assert!(
        elapsed > Duration::from_millis(900) && elapsed < Duration::from_secs(3),
        "{elapsed:?}"
    );
}
