#![allow(clippy::unwrap_used)] // Test helpers outside `#[test]` fns; panics are the assertions.

use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::{Arc, Mutex};
use std::time::Duration;

use infinidysk_core::trace::Probe;
use infinidysk_nntp::{
    Client, Downloads, Endpoint, Error, Phase, Pool, PoolConfig, Progress, Provider, TlsMode,
    Usage, Workload,
};
use infinidysk_testkit::{Article, Corpus, Impairment, LoopbackServer, post};
use tokio::io::{AsyncBufReadExt, AsyncWriteExt, BufReader};
use tokio::net::TcpListener;

const ARTICLE_SIZE: usize = 16 * 1024;

fn provider(
    port: u16,
    priority: u32,
    backbone: Option<&str>,
    downloads: &Arc<Downloads>,
) -> Provider {
    let endpoint = Endpoint::new(
        "127.0.0.1",
        port,
        TlsMode::Plain,
        None,
        Duration::from_secs(5),
    )
    .unwrap();
    let config = PoolConfig {
        max_connections: 2,
        pipeline_depth: 2,
        idle_timeout: Duration::from_secs(5),
        min_connections: 0,
    };
    Provider {
        pool: Pool::new(endpoint, config, Arc::clone(downloads)).unwrap(),
        key: port.to_string(),
        name: port.to_string(),
        priority,
        backbone: backbone.map(str::to_owned),
    }
}

async fn body(client: &Client, id: &str) -> Result<infinidysk_nntp::Body, Error> {
    client.body(id, Workload::Stream, None).await
}

#[tokio::test]
async fn an_unexpected_reply_keeps_its_text_without_control_characters() {
    let listener = TcpListener::bind("127.0.0.1:0").await.unwrap();
    let port = listener.local_addr().unwrap().port();
    tokio::spawn(async move {
        let (stream, _) = listener.accept().await.unwrap();
        let (read, mut write) = stream.into_split();
        write.write_all(b"200 ready\r\n").await.unwrap();
        let mut lines = BufReader::new(read).lines();
        while lines.next_line().await.unwrap().is_some() {
            write
                .write_all(b"400 Too many \x1b[31mconnections\r\n")
                .await
                .unwrap();
        }
    });
    let client = Client::new([provider(port, 0, None, &Downloads::new(1))]);
    let Err(Error::Status { code, reply }) = body(&client, "a@test").await else {
        panic!("an unexpected reply fails with its status")
    };
    assert_eq!((code, &*reply), (400, "Too many [31mconnections"));
}

#[tokio::test]
#[allow(clippy::significant_drop_tightening)] // False positive: each client is used until replaced.
async fn client_fails_over_and_reports_misses_conclusively() {
    let corpus = Corpus::generate(1025, 8, ARTICLE_SIZE);
    let full = LoopbackServer::start(corpus.articles.clone())
        .await
        .unwrap();
    let empty = LoopbackServer::start([]).await.unwrap();
    let dead = TcpListener::bind("127.0.0.1:0")
        .await
        .unwrap()
        .local_addr()
        .unwrap()
        .port();
    let downloads = Downloads::new(16);

    let client = Client::new([
        provider(dead, 0, None, &downloads),
        provider(empty.addr().port(), 0, None, &downloads),
        provider(full.addr().port(), 1, None, &downloads),
    ]);
    for (index, article) in corpus.articles.iter().enumerate() {
        let body = body(&client, &article.message_id).await.unwrap();
        let start = index * ARTICLE_SIZE;
        assert_eq!(body.data, corpus.data[start..start + ARTICLE_SIZE]);
    }
    client
        .stat(&corpus.articles[0].message_id, Workload::Health, None)
        .await
        .unwrap();
    // The dead provider's circuit is open, so a miss everywhere else is inconclusive.
    assert!(matches!(
        body(&client, "missing@test").await,
        Err(Error::Unavailable)
    ));
    // Attempts by outcome: [retrieved, not found, cancelled, ...].
    let stats = client.stats();
    assert!(stats[0].connect_failures >= 1);
    assert!(!stats[0].connected);
    assert!(
        stats[0]
            .connect_error
            .as_deref()
            .is_some_and(|e| e.starts_with("Could not reach the server")),
        "{:?}",
        stats[0].connect_error
    );
    assert!(stats[1].connected && stats[1].connect_error.is_none());
    assert_eq!(stats[0].workloads[0].attempts.iter().sum::<u64>(), 0);
    assert_eq!(stats[1].workloads[0].attempts[..3], [0, 9, 0]);
    // The health check skipped the provider that just lacked the article.
    assert_eq!(stats[1].workloads[2].attempts[..3], [0, 0, 0]);
    assert_eq!(client.missing_hits(), 1);
    assert_eq!(stats[2].workloads[0].attempts[..3], [8, 1, 0]);
    assert_eq!(stats[2].workloads[0].bytes, corpus.data.len() as u64);
    assert_eq!(stats[2].workloads[2].attempts[..3], [1, 0, 0]);
    assert_eq!(stats[2].workloads[0].latency.iter().sum::<u64>(), 9);

    let client = Client::new([
        provider(empty.addr().port(), 0, None, &downloads),
        provider(full.addr().port(), 1, None, &downloads),
    ]);
    assert!(matches!(
        body(&client, "missing@test").await,
        Err(Error::NotFound)
    ));
    // A repeat miss is answered without asking anyone.
    let asked = || {
        client
            .stats()
            .iter()
            .map(|s| s.workloads[0].attempts.iter().sum::<u64>())
            .sum::<u64>()
    };
    let before = asked();
    assert!(matches!(
        body(&client, "missing@test").await,
        Err(Error::NotFound)
    ));
    assert_eq!(asked(), before);
    assert_eq!(client.missing_hits(), 2);
    // An article the NZB never listed is missing without asking anyone.
    assert!(matches!(body(&client, "").await, Err(Error::NotFound)));
    assert_eq!(asked(), before);

    // A miss on one backbone covers its other providers.
    let client = Client::new([
        provider(empty.addr().port(), 0, Some("shared"), &downloads),
        provider(full.addr().port(), 1, Some("shared"), &downloads),
    ]);
    assert!(matches!(
        body(&client, &corpus.articles[0].message_id).await,
        Err(Error::NotFound)
    ));

    // Without providers nothing is proven missing; a swap takes effect for new requests.
    client.replace([]);
    assert!(matches!(
        body(&client, &corpus.articles[0].message_id).await,
        Err(Error::Unavailable)
    ));
    client.replace([provider(full.addr().port(), 0, None, &downloads)]);
    body(&client, &corpus.articles[0].message_id).await.unwrap();
    // Counters follow the provider across the swap; a caller that leaves mid-request
    // still completes exactly once, as cancelled.
    // Polled once (sent), then dropped.
    tokio::select! {
        biased;
        _ = body(&client, &corpus.articles[1].message_id) => panic!("answered on first poll"),
        () = std::future::ready(()) => {}
    }
    let attempts = || client.stats()[0].workloads[0].attempts;
    for _ in 0..500 {
        if attempts()[2] > 0 {
            break;
        }
        tokio::time::sleep(Duration::from_millis(10)).await;
    }
    assert_eq!(attempts()[..3], [1, 0, 1]);
    assert_eq!(client.provider_names().len(), 2);
    assert_eq!(downloads.available_permits(), 16);
}

#[tokio::test]
async fn only_failures_the_caller_sees_count_as_failed() {
    let corpus = Arc::new(Corpus::generate(1025, 8, ARTICLE_SIZE));
    let server = LoopbackServer::start_dropping(corpus.articles.clone(), 5)
        .await
        .unwrap();
    let downloads = Downloads::new(16);
    let endpoint = Endpoint::new(
        "127.0.0.1",
        server.addr().port(),
        TlsMode::Plain,
        None,
        Duration::from_secs(5),
    )
    .unwrap();
    let config = PoolConfig {
        max_connections: 1,
        pipeline_depth: 4,
        idle_timeout: Duration::from_secs(5),
        min_connections: 0,
    };
    let client = Arc::new(Client::new([Provider {
        pool: Pool::new(endpoint, config, Arc::clone(&downloads)).unwrap(),
        ..provider(server.addr().port(), 0, None, &downloads)
    }]));

    let mut tasks = tokio::task::JoinSet::new();
    for article in &corpus.articles {
        let (client, id) = (Arc::clone(&client), article.message_id.clone());
        tasks.spawn(async move { body(&client, &id).await.map(|_| ()) });
    }
    while let Some(task) = tasks.join_next().await {
        task.unwrap().unwrap();
    }
    // [retrieved, not found, cancelled, timeout, auth, network, ...]: the requests
    // pipelined behind the one that hit the drop were re-sent, not failed.
    let stats = &client.stats()[0].workloads[0];
    assert_eq!(stats.attempts[..6], [8, 0, 0, 0, 0, 1]);
    assert_eq!(stats.failed, 0, "the drop was retried; no caller saw it");

    // A provider that drops every request fails the caller once, after its retries.
    let broken = LoopbackServer::start_dropping(corpus.articles.clone(), 0)
        .await
        .unwrap();
    let client = Client::new([provider(broken.addr().port(), 0, None, &downloads)]);
    assert!(body(&client, &corpus.articles[0].message_id).await.is_err());
    let stats = &client.stats()[0].workloads[0];
    assert_eq!((stats.attempts[5], stats.failed), (3, 1));
}

/// The article with one digit of its `pcrc32` flipped, so it decodes with a CRC mismatch.
fn corrupted(article: &Article) -> Article {
    let mut article = article.clone();
    let at = article
        .wire
        .windows(7)
        .position(|w| w == b"pcrc32=")
        .unwrap()
        + 7;
    article.wire[at] = if article.wire[at] == b'0' { b'1' } else { b'0' };
    article
}

#[derive(Clone, Copy, Debug)]
enum Kind {
    Dropping,
    Refusing,
    Corrupt,
    Missing,
    Full,
}

async fn start(kind: Kind, corpus: &Corpus) -> LoopbackServer {
    let articles = corpus.articles.clone();
    match kind {
        Kind::Dropping => LoopbackServer::start_dropping(articles, 0).await,
        Kind::Refusing => LoopbackServer::start_limited(articles, 0).await,
        Kind::Corrupt => LoopbackServer::start([corrupted(&articles[0])]).await,
        Kind::Missing => LoopbackServer::start([]).await,
        Kind::Full => LoopbackServer::start(articles).await,
    }
    .unwrap()
}

#[tokio::test]
async fn a_corrupt_copy_is_damage_only_when_no_provider_failed() {
    use Kind::{Corrupt, Dropping, Full, Missing, Refusing};
    type Check = fn(&Result<Vec<u8>, Error>, &[u8]) -> bool;
    let corpus = Corpus::generate(1025, 1, ARTICLE_SIZE);
    let id = &corpus.articles[0].message_id;
    let cases: [(&[Kind], Check); 7] = [
        (&[Corrupt], |r, _| {
            matches!(r, Err(Error::CrcMismatch { .. }))
        }),
        (&[Missing, Corrupt], |r, _| {
            matches!(r, Err(Error::CrcMismatch { .. }))
        }),
        (&[Dropping, Corrupt], |r, _| {
            matches!(r, Err(Error::Unavailable))
        }),
        (&[Refusing, Corrupt], |r, _| {
            matches!(r, Err(Error::Unavailable))
        }),
        (&[Corrupt, Dropping], |r, _| matches!(r, Err(Error::Closed))),
        (&[Corrupt, Refusing], |r, _| {
            matches!(r, Err(Error::Status { code: 502, .. }))
        }),
        (&[Dropping, Corrupt, Full], |r, data| {
            r.as_deref().ok() == Some(data)
        }),
    ];
    for (kinds, check) in cases {
        let downloads = Downloads::new(4);
        let mut servers = Vec::new();
        for &kind in kinds {
            servers.push(start(kind, &corpus).await);
        }
        let priorities = (0..).zip(&servers);
        let client = Client::new(
            priorities.map(|(priority, s)| provider(s.addr().port(), priority, None, &downloads)),
        );

        let result = body(&client, id).await.map(|b| b.data.to_vec());
        assert!(
            check(&result, &corpus.data),
            "{kinds:?}: {:?}",
            result.map(|d| d.len())
        );
        let corrupt = kinds.iter().position(|k| matches!(k, Corrupt)).unwrap();
        assert_eq!(servers[corrupt].body_commands(), 1, "{kinds:?}");
        let rescued = matches!(kinds.last(), Some(Full));
        assert_eq!(client.rescues().len(), usize::from(rescued), "{kinds:?}");
        assert_eq!(downloads.available_permits(), 4, "{kinds:?}");
    }
}

#[tokio::test]
#[allow(clippy::significant_drop_tightening)] // False positive: the client is used to the end.
async fn client_fails_over_from_a_wrong_part() {
    let corpus = Corpus::generate(1025, 2, ARTICLE_SIZE);
    // This provider answers the first article's id with the second part.
    let mut swapped = corpus.articles[1].clone();
    swapped
        .message_id
        .clone_from(&corpus.articles[0].message_id);
    let wrong = LoopbackServer::start([swapped]).await.unwrap();
    let full = LoopbackServer::start(corpus.articles.clone())
        .await
        .unwrap();
    let downloads = Downloads::new(16);
    let client = Client::new([
        provider(wrong.addr().port(), 0, None, &downloads),
        provider(full.addr().port(), 1, None, &downloads),
    ]);
    let id = &corpus.articles[0].message_id;

    let body = client
        .body_with(id, Workload::Stream, None, None, Some(0))
        .await
        .unwrap();
    assert_eq!(body.data, corpus.data[..ARTICLE_SIZE]);
    // Without an expected offset the wrong part is taken as is.
    let body = client
        .body_with(id, Workload::Stream, None, None, None)
        .await
        .unwrap();
    assert_eq!(body.offset, ARTICLE_SIZE as u64);
}

/// Answers every BODY with `chunks`, written 100 ms apart so each arrives as its own read.
async fn raw_body_server(chunks: Vec<Vec<u8>>) -> u16 {
    let listener = TcpListener::bind("127.0.0.1:0").await.unwrap();
    let port = listener.local_addr().unwrap().port();
    let chunks = Arc::new(chunks);
    tokio::spawn(async move {
        while let Ok((stream, _)) = listener.accept().await {
            stream.set_nodelay(true).unwrap();
            let chunks = Arc::clone(&chunks);
            tokio::spawn(async move {
                let (read, mut write) = stream.into_split();
                write.write_all(b"200 ready\r\n").await.unwrap();
                let mut lines = BufReader::new(read).lines();
                while let Ok(Some(line)) = lines.next_line().await {
                    if !line.starts_with("BODY ") {
                        write.write_all(b"500 unsupported\r\n").await.unwrap();
                        continue;
                    }
                    for (index, chunk) in chunks.iter().enumerate() {
                        if index > 0 {
                            tokio::time::sleep(Duration::from_millis(100)).await;
                        }
                        write.write_all(chunk).await.unwrap();
                        write.flush().await.unwrap();
                    }
                }
            });
        }
    });
    port
}

/// `data` yEnc-encoded and the terminator, with no `=yend`.
fn trailerless(head: &[u8], data: &[u8]) -> Vec<u8> {
    [head, &infinidysk_yenc::encode(data, 128), b"\r\n.\r\n"].concat()
}

const REPLY: &[u8] = b"222 0 <a@test> body follows\r\n";

#[tokio::test]
#[allow(clippy::significant_drop_tightening)] // False positive: the client is used to the end.
async fn short_trailerless_body_fails_over() {
    let data = b"abcdefgh";
    let head = [REPLY, b"=ybegin line=128 size=8 name=x\r\n"].concat();
    let short = raw_body_server(vec![trailerless(&head, &data[..4])]).await;
    let full = LoopbackServer::start(post("t", "x", data, data.len()))
        .await
        .unwrap();
    let downloads = Downloads::new(16);
    let client = Client::new([
        provider(short, 0, None, &downloads),
        provider(full.addr().port(), 1, None, &downloads),
    ]);
    let body = body(&client, "t-0@loopback").await.unwrap();
    assert_eq!(&*body.data, data);
    assert_eq!(downloads.available_permits(), 16);
}

#[tokio::test]
async fn a_complete_trailerless_part_with_a_split_header_is_kept() {
    let first = [REPLY, b"=ybegin part=1 line=128 size=100 name=x\r\n=yp"].concat();
    let rest = trailerless(b"art begin=1 end=4\r\n", b"abcd");
    let port = raw_body_server(vec![first, rest]).await;
    let downloads = Downloads::new(16);
    let client = Client::new([provider(port, 0, None, &downloads)]);
    let body = body(&client, "a@test").await.unwrap();
    assert_eq!(&*body.data, b"abcd");
    assert_eq!(downloads.available_permits(), 16);
}

#[tokio::test]
async fn a_complete_trailerless_article_is_kept() {
    let head = [REPLY, b"=ybegin line=128 size=8 name=x\r\n"].concat();
    let port = raw_body_server(vec![trailerless(&head, b"abcdefgh")]).await;
    let downloads = Downloads::new(16);
    let client = Client::new([provider(port, 0, None, &downloads)]);
    let body = body(&client, "a@test").await.unwrap();
    assert_eq!(&*body.data, b"abcdefgh");
    assert_eq!(downloads.available_permits(), 16);
}

#[tokio::test]
#[allow(clippy::significant_drop_tightening)] // Early drops would not change the test.
async fn progress_copies_nothing_until_wanted_then_sends_from_the_start() {
    const SIZE: usize = 256 * 1024;
    let corpus = Corpus::generate(1025, 1, SIZE);
    // A second per article, written a quarter second apart.
    let impairment = Impairment {
        bandwidth: Some(SIZE as u64),
        ..Impairment::default()
    };
    let server = LoopbackServer::start_impaired(corpus.articles.clone(), impairment)
        .await
        .unwrap();
    let downloads = Downloads::new(16);
    let client = Client::new([provider(server.addr().port(), 0, None, &downloads)]);
    let (tx, mut rx) = tokio::sync::mpsc::unbounded_channel();
    let progress = Progress {
        tx,
        wanted: Arc::new(AtomicBool::new(false)),
    };
    let id = &corpus.articles[0].message_id;

    let fetch = client.body_with(id, Workload::Stream, None, Some(&progress), None);
    let arm = async {
        // Part of the body is in by now.
        tokio::time::sleep(Duration::from_millis(300)).await;
        let quiet = rx.is_empty();
        progress.wanted.store(true, Ordering::Relaxed);
        quiet
    };
    let (body, quiet) = tokio::join!(fetch, arm);
    let body = body.unwrap();
    assert!(quiet, "nothing is sent before it is wanted");
    let mut streamed = Vec::new();
    while let Ok((at, chunk)) = rx.try_recv() {
        assert_eq!(at, streamed.len(), "each chunk starts where the last ended");
        streamed.extend_from_slice(&chunk);
    }
    assert!(streamed == body.data, "the chunks add up to the whole body");
}

#[tokio::test]
async fn lease_holds_background_work_and_playback_preempts_it() {
    let corpus = Corpus::generate(1025, 1, ARTICLE_SIZE);
    let server = LoopbackServer::start(corpus.articles.clone())
        .await
        .unwrap();
    let port = server.addr().port();
    let downloads = Downloads::new(16);
    let client = Arc::new(Client::new([provider(port, 0, None, &downloads)]));
    let id = corpus.articles[0].message_id.clone();

    let lease = client
        .exclusive(&port.to_string(), Duration::from_secs(1))
        .await
        .unwrap();
    let queued = tokio::spawn({
        let (client, id) = (Arc::clone(&client), id.clone());
        async move { client.stat(&id, Workload::Queue, None).await }
    });
    tokio::time::sleep(Duration::from_millis(100)).await;
    assert!(!queued.is_finished());
    assert!(!lease.token().is_cancelled());

    body(&client, &id).await.unwrap();
    assert!(lease.token().is_cancelled());
    drop(lease);
    queued.await.unwrap().unwrap();
}

#[tokio::test]
#[allow(clippy::significant_drop_tightening)] // The client must outlive the drained reply.
async fn bodies_drained_after_the_caller_left_reach_the_sink() {
    let corpus = Corpus::generate(1025, 1, ARTICLE_SIZE);
    let impairment = Impairment {
        response_delay: Duration::from_millis(100),
        ..Impairment::default()
    };
    let server = LoopbackServer::start_impaired(corpus.articles.clone(), impairment)
        .await
        .unwrap();
    let downloads = Downloads::new(16);
    let client = Client::new([provider(server.addr().port(), 0, None, &downloads)]);
    let seen = Arc::new(Mutex::new(Vec::new()));
    client.set_sink(Box::new({
        let seen = Arc::clone(&seen);
        move |id, _, body, drained| {
            seen.lock()
                .unwrap()
                .push((id.to_owned(), body.data.len(), drained));
        }
    }));
    let id = &corpus.articles[0].message_id;

    let left = tokio::time::timeout(Duration::from_millis(50), body(&client, id)).await;
    assert!(
        left.is_err(),
        "the caller leaves while the reply is pending"
    );
    tokio::time::sleep(Duration::from_millis(200)).await;
    assert_eq!(*seen.lock().unwrap(), [(id.clone(), ARTICLE_SIZE, true)]);
}

#[tokio::test]
#[allow(clippy::significant_drop_tightening)] // False positive: the client is used to the end.
async fn a_stalled_stream_read_fails_over_without_retrying() {
    let corpus = Corpus::generate(1025, 1, ARTICLE_SIZE);
    let stalled =
        LoopbackServer::start_stalling(corpus.articles.clone(), 0, Duration::from_secs(60))
            .await
            .unwrap();
    let backup = LoopbackServer::start(corpus.articles.clone())
        .await
        .unwrap();
    let downloads = Downloads::new(16);
    let timeout = Duration::from_millis(300);
    let port = stalled.addr().port();
    let endpoint = Endpoint::new("127.0.0.1", port, TlsMode::Plain, None, timeout).unwrap();
    let config = PoolConfig {
        max_connections: 2,
        pipeline_depth: 1,
        idle_timeout: Duration::from_secs(5),
        min_connections: 0,
    };
    let client = Client::new([
        Provider {
            pool: Pool::new(endpoint, config, Arc::clone(&downloads)).unwrap(),
            ..provider(port, 0, None, &downloads)
        },
        provider(backup.addr().port(), 1, None, &downloads),
    ]);

    let started = std::time::Instant::now();
    body(&client, &corpus.articles[0].message_id).await.unwrap();
    assert!(started.elapsed() < timeout * 2, "{:?}", started.elapsed());
    assert_eq!((stalled.body_commands(), backup.body_commands()), (1, 1));
}

/// A provider whose new handshakes hang once one connection is open, that connection
/// busy with a BODY held for 600 ms, and a healthy backup one level down.
async fn hung_connects(
    corpus: &Corpus,
) -> (
    LoopbackServer,
    LoopbackServer,
    Arc<Client>,
    tokio::task::JoinHandle<Result<(), Error>>,
) {
    let flaky =
        LoopbackServer::start_hanging(corpus.articles.clone(), 1, 1, Duration::from_millis(600))
            .await
            .unwrap();
    let backup = LoopbackServer::start(corpus.articles.clone())
        .await
        .unwrap();
    let downloads = Downloads::new(16);
    let port = flaky.addr().port();
    let timeout = Duration::from_secs(1);
    let endpoint = Endpoint::new("127.0.0.1", port, TlsMode::Plain, None, timeout).unwrap();
    let config = PoolConfig {
        max_connections: 2,
        pipeline_depth: 2,
        idle_timeout: Duration::from_secs(5),
        min_connections: 0,
    };
    let client = Arc::new(Client::new([
        Provider {
            pool: Pool::new(endpoint, config, Arc::clone(&downloads)).unwrap(),
            ..provider(port, 0, None, &downloads)
        },
        provider(backup.addr().port(), 1, None, &downloads),
    ]));
    body(&client, &corpus.articles[0].message_id).await.unwrap();
    let busy = tokio::spawn({
        let (client, id) = (Arc::clone(&client), corpus.articles[1].message_id.clone());
        async move { body(&client, &id).await.map(|_| ()) }
    });
    tokio::time::sleep(Duration::from_millis(50)).await;
    (flaky, backup, client, busy)
}

#[tokio::test]
async fn a_hung_new_connection_does_not_stall_playback() {
    let corpus = Corpus::generate(1025, 3, ARTICLE_SIZE);
    let (_flaky, backup, client, busy) = hung_connects(&corpus).await;

    let started = std::time::Instant::now();
    body(&client, &corpus.articles[2].message_id).await.unwrap();
    assert!(
        started.elapsed() < Duration::from_millis(900),
        "{:?}",
        started.elapsed()
    );
    busy.await.unwrap().unwrap();
    assert_eq!(
        backup.body_commands(),
        0,
        "pipelined on the live connection"
    );
}

#[tokio::test]
async fn a_request_waiting_on_a_hung_connect_fails_over() {
    let corpus = Corpus::generate(1025, 3, ARTICLE_SIZE);
    let (flaky, backup, client, busy) = hung_connects(&corpus).await;

    client
        .body(&corpus.articles[2].message_id, Workload::Queue, None)
        .await
        .unwrap();
    busy.await.unwrap().unwrap();
    assert_eq!((flaky.body_commands(), backup.body_commands()), (2, 1));
}

#[tokio::test]
async fn a_hedge_waits_for_an_idle_connection_instead_of_pipelining() {
    // At 2 MB/s per connection: a 2 MB article holds one connection ~1 s, 400 KB the
    // other ~0.2 s.
    let article = |size, id: &str| {
        let mut article = Corpus::generate(1025, 1, size).articles.remove(0);
        article.message_id = id.to_owned();
        article
    };
    let small = Corpus::generate(1025, 1, ARTICLE_SIZE);
    let impairment = Impairment {
        bandwidth: Some(2_000_000),
        ..Impairment::default()
    };
    let articles = [
        article(2_000_000, "slow@loopback"),
        article(400_000, "short@loopback"),
        small.articles[0].clone(),
    ];
    let server = LoopbackServer::start_impaired(articles, impairment)
        .await
        .unwrap();
    let downloads = Downloads::new(16);
    let client = Arc::new(Client::new([provider(
        server.addr().port(),
        0,
        None,
        &downloads,
    )]));
    let mut busy = tokio::task::JoinSet::new();
    for id in ["slow@loopback", "short@loopback"] {
        let client = Arc::clone(&client);
        busy.spawn(async move { body(&client, id).await.map(drop) });
        tokio::time::sleep(Duration::from_millis(50)).await;
    }

    let started = std::time::Instant::now();
    client
        .hedge(&small.articles[0].message_id, None, None)
        .await
        .unwrap();
    assert!(
        started.elapsed() < Duration::from_millis(600),
        "queued behind the slow article: {:?}",
        started.elapsed()
    );
    while let Some(task) = busy.join_next().await {
        task.unwrap().unwrap();
    }
}

fn single_connection(port: u16, depth: usize, downloads: &Arc<Downloads>) -> Pool {
    let endpoint = Endpoint::new(
        "127.0.0.1",
        port,
        TlsMode::Plain,
        None,
        Duration::from_secs(5),
    )
    .unwrap();
    let config = PoolConfig {
        max_connections: 1,
        pipeline_depth: depth,
        idle_timeout: Duration::from_secs(5),
        min_connections: 0,
    };
    Pool::new(endpoint, config, Arc::clone(downloads)).unwrap()
}

#[tokio::test]
async fn a_large_article_takes_a_whole_depth_of_permits() {
    let large = Corpus::generate(1025, 1, 2_097_152);
    let impairment = Impairment {
        bandwidth: Some(2_000_000),
        ..Impairment::default()
    };
    let server = LoopbackServer::start_impaired(large.articles.clone(), impairment)
        .await
        .unwrap();
    let downloads = Downloads::new(16);
    let plain = provider(server.addr().port(), 0, None, &downloads);
    let client = Arc::new(Client::new([Provider {
        pool: single_connection(server.addr().port(), 8, &downloads).with_weight_large(true),
        ..plain
    }]));
    let probe = Probe {
        trace: None,
        wait: None,
        watch: None,
        segment: 0,
        hedge: false,
        size: 2_097_152,
    };
    let fetch = tokio::spawn({
        let (client, id) = (Arc::clone(&client), large.articles[0].message_id.clone());
        async move {
            client
                .body(&id, Workload::Stream, Some(&probe))
                .await
                .map(drop)
        }
    });
    tokio::time::sleep(Duration::from_millis(300)).await;
    assert_eq!(
        downloads.available_permits(),
        8,
        "not ceil(size / 1 MB) = 3"
    );
    fetch.await.unwrap().unwrap();
    assert_eq!(downloads.available_permits(), 16);
}

#[tokio::test]
async fn playback_finds_a_connection_imports_leave_free() {
    let large = Corpus::generate(1025, 1, 4_000_000);
    let small = Corpus::generate(1025, 1, ARTICLE_SIZE);
    for (max_connections, workload) in [(4, Workload::Queue), (10, Workload::Health)] {
        let ids: Vec<_> = (0..max_connections + 2)
            .map(|index| format!("background-{index}@loopback"))
            .collect();
        let mut articles = vec![small.articles[0].clone()];
        for id in &ids {
            let mut article = large.articles[0].clone();
            article.message_id.clone_from(id);
            articles.push(article);
        }
        let impairment = Impairment {
            bandwidth: Some(2_000_000),
            ..Impairment::default()
        };
        let server = LoopbackServer::start_impaired(articles, impairment)
            .await
            .unwrap();
        let port = server.addr().port();
        let downloads = Downloads::new(max_connections * 2);
        let endpoint = Endpoint::new(
            "127.0.0.1",
            port,
            TlsMode::Plain,
            None,
            Duration::from_secs(5),
        )
        .unwrap();
        let config = PoolConfig {
            max_connections,
            pipeline_depth: 2,
            idle_timeout: Duration::from_secs(5),
            min_connections: 0,
        };
        let client = Arc::new(Client::new([Provider {
            pool: Pool::new(endpoint, config, Arc::clone(&downloads)).unwrap(),
            ..provider(port, 0, None, &downloads)
        }]));
        let mut busy = tokio::task::JoinSet::new();
        for id in &ids {
            let (client, id) = (Arc::clone(&client), id.clone());
            busy.spawn(async move { client.body(&id, workload, None).await.map(drop) });
        }
        tokio::time::timeout(Duration::from_millis(600), async {
            while client.stats()[0].busy != ids.len() {
                tokio::time::sleep(Duration::from_millis(10)).await;
            }
        })
        .await
        .unwrap();
        assert_eq!(
            downloads.available_permits(),
            max_connections * 2 - ids.len()
        );
        let background_connections = client.stats()[0].connections.len();

        let started = std::time::Instant::now();
        let received = body(&client, &small.articles[0].message_id).await.unwrap();
        let elapsed = started.elapsed();
        eprintln!("{max_connections} connections: playback response took {elapsed:?}");
        assert!(
            elapsed < Duration::from_millis(600),
            "playback queued behind background BODYs: {elapsed:?}"
        );
        assert_eq!(received.data, small.data);
        assert_eq!(background_connections, max_connections - 1);
        tokio::time::timeout(Duration::from_secs(15), async {
            while let Some(task) = busy.join_next().await {
                task.unwrap().unwrap();
            }
        })
        .await
        .unwrap();
    }
}

#[tokio::test]
async fn pipelined_requests_count_once_per_connection() {
    let corpus = Corpus::generate(1025, 4, 2_000_000);
    let impairment = Impairment {
        bandwidth: Some(2_000_000),
        ..Impairment::default()
    };
    let server = LoopbackServer::start_impaired(corpus.articles.clone(), impairment)
        .await
        .unwrap();
    let downloads = Downloads::new(16);
    let client = Arc::new(Client::new([provider(
        server.addr().port(),
        0,
        None,
        &downloads,
    )]));
    let mut busy = tokio::task::JoinSet::new();
    for article in &corpus.articles {
        let (client, id) = (Arc::clone(&client), article.message_id.clone());
        busy.spawn(async move { client.body(&id, Workload::Queue, None).await.map(drop) });
    }
    tokio::time::sleep(Duration::from_millis(300)).await;

    let stats = &client.stats()[0];
    assert_eq!((stats.busy, stats.active), (4, 2));
    for connection in &stats.connections {
        assert_eq!(
            (connection.phase, connection.requests),
            (Phase::Transfer, 2),
            "{connection:?}"
        );
        assert!(connection.since_byte.unwrap() < Duration::from_millis(200));
    }
    assert_eq!(stats.connections.len(), 2);
    while let Some(task) = busy.join_next().await {
        task.unwrap().unwrap();
    }
}

#[tokio::test]
#[allow(clippy::significant_drop_tightening)] // False positive: the client is used to the end.
async fn used_up_quota_falls_back_and_is_never_forced() {
    let corpus = Corpus::generate(1025, 2, ARTICLE_SIZE);
    let server = LoopbackServer::start(corpus.articles.clone())
        .await
        .unwrap();
    let port = server.addr().port();
    let downloads = Downloads::new(16);
    let usage = Arc::new(Usage::default());
    usage.set_cap(Some(1));
    let metered = || {
        let endpoint = Endpoint::new(
            "127.0.0.1",
            port,
            TlsMode::Plain,
            None,
            Duration::from_secs(5),
        )
        .unwrap()
        .with_usage(Arc::clone(&usage));
        let config = PoolConfig {
            max_connections: 1,
            pipeline_depth: 1,
            idle_timeout: Duration::from_secs(5),
            min_connections: 0,
        };
        Provider {
            pool: Pool::new(endpoint, config, Arc::clone(&downloads)).unwrap(),
            key: "metered".to_owned(),
            ..provider(port, 0, None, &downloads)
        }
    };
    let client = Client::new([metered(), provider(port, 1, None, &downloads)]);

    body(&client, &corpus.articles[0].message_id).await.unwrap();
    assert!(
        usage.used() > ARTICLE_SIZE as u64,
        "socket bytes are counted"
    );
    assert!(usage.exhausted());
    body(&client, &corpus.articles[1].message_id).await.unwrap();
    let stats = client.stats();
    assert_eq!(stats[0].workloads[0].attempts[0], 1);
    assert_eq!(
        stats[1].workloads[0].attempts[0], 1,
        "the fallback served it"
    );

    client.replace([metered()]);
    assert!(matches!(
        body(&client, &corpus.articles[0].message_id).await,
        Err(Error::Unavailable)
    ));
}

#[tokio::test]
async fn warm_keeps_the_floor_only_where_eligible_and_never_during_a_lease() {
    let servers = [
        LoopbackServer::start([]).await.unwrap(),
        LoopbackServer::start([]).await.unwrap(),
        LoopbackServer::start([]).await.unwrap(),
    ];
    let downloads = Downloads::new(16);
    let used_up = Arc::new(Usage::default());
    used_up.set_cap(Some(1));
    used_up.set_used(1);
    let floored = |server: &LoopbackServer, priority, floor, usage: Option<&Arc<Usage>>| {
        let port = server.addr().port();
        let mut endpoint = Endpoint::new(
            "127.0.0.1",
            port,
            TlsMode::Plain,
            None,
            Duration::from_secs(5),
        )
        .unwrap();
        if let Some(usage) = usage {
            endpoint = endpoint.with_usage(Arc::clone(usage));
        }
        let config = PoolConfig {
            max_connections: 4,
            pipeline_depth: 2,
            idle_timeout: Duration::from_secs(5),
            min_connections: floor,
        };
        Provider {
            pool: Pool::new(endpoint, config, Arc::clone(&downloads)).unwrap(),
            ..provider(port, priority, None, &downloads)
        }
    };
    let client = Client::new([
        floored(&servers[0], 0, 2, None),
        floored(&servers[1], 0, 2, Some(&used_up)),
        floored(&servers[2], 1, 0, None),
    ]);
    let open = || servers.each_ref().map(LoopbackServer::open_connections);
    let settle = |want: [usize; 3]| async move {
        for _ in 0..100 {
            if open() == want {
                return;
            }
            tokio::time::sleep(Duration::from_millis(10)).await;
        }
        assert_eq!(open(), want);
    };

    client.warm().await;
    settle([2, 0, 0]).await;

    let primary = servers[0].addr().port().to_string();
    let lease = client
        .exclusive(&primary, Duration::from_secs(1))
        .await
        .unwrap();
    settle([0, 0, 0]).await;
    client.warm().await;
    settle([0, 0, 0]).await;

    drop(lease);
    client.warm().await;
    settle([2, 0, 0]).await;
}

#[tokio::test]
async fn background_connects_that_fail_are_counted_without_a_trip() {
    let dead = TcpListener::bind("127.0.0.1:0")
        .await
        .unwrap()
        .local_addr()
        .unwrap()
        .port();
    let downloads = Downloads::new(16);
    let endpoint = Endpoint::new(
        "127.0.0.1",
        dead,
        TlsMode::Plain,
        None,
        Duration::from_secs(5),
    )
    .unwrap();
    let config = PoolConfig {
        max_connections: 2,
        pipeline_depth: 2,
        idle_timeout: Duration::from_secs(5),
        min_connections: 2,
    };
    let client = Client::new([Provider {
        pool: Pool::new(endpoint, config, Arc::clone(&downloads)).unwrap(),
        ..provider(dead, 0, None, &downloads)
    }]);

    client.warm().await;
    let stats = &client.stats()[0];
    assert_eq!((stats.connect_failures, stats.trips), (2, 0));
    assert!(stats.open_for.is_none());

    // Inside the connect backoff the request fails without trying to connect.
    assert!(body(&client, "a@test").await.is_err());
    assert_eq!(client.stats()[0].connect_failures, 2);
}
