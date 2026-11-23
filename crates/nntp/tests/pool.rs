#![allow(clippy::unwrap_used)] // Test helpers outside `#[test]` fns; panics are the assertions.

use std::sync::Arc;
use std::time::Duration;

use infinidysk_nntp::{Downloads, Endpoint, Pool, PoolConfig, TlsMode, Usage};
use infinidysk_testkit::{Corpus, Impairment, LoopbackServer};
use tokio::io::{AsyncBufReadExt, AsyncWriteExt, BufReader};
use tokio::net::{TcpListener, TcpStream};
use tokio::task::JoinSet;
use tokio::time::timeout;

const ARTICLE_SIZE: usize = 16 * 1024;
const DOWNLOADS: usize = 64;

fn pool(server: &LoopbackServer, config: PoolConfig, downloads: &Arc<Downloads>) -> Arc<Pool> {
    let endpoint = Endpoint::new(
        "127.0.0.1",
        server.addr().port(),
        TlsMode::Plain,
        None,
        Duration::from_secs(5),
    )
    .unwrap();
    Arc::new(Pool::new(endpoint, config, Arc::clone(downloads)).unwrap())
}

/// Fetches every article concurrently and checks each lands at its offset.
async fn fetch_all(pool: &Arc<Pool>, corpus: &Arc<Corpus>) {
    let mut tasks = JoinSet::new();
    for index in 0..corpus.articles.len() {
        let (pool, corpus) = (Arc::clone(pool), Arc::clone(corpus));
        tasks.spawn(async move {
            let body = pool.body(&corpus.articles[index].message_id).await.unwrap();
            let start = index * ARTICLE_SIZE;
            assert_eq!(body.offset, start as u64);
            assert_eq!(body.data, corpus.data[start..start + ARTICLE_SIZE]);
        });
    }
    while let Some(task) = tasks.join_next().await {
        task.unwrap();
    }
}

#[tokio::test]
#[allow(clippy::significant_drop_tightening)] // False positive: the pool is used until the end.
async fn pool_stays_within_limits_and_survives_cancellation() {
    let corpus = Arc::new(Corpus::generate(1025, 48, ARTICLE_SIZE));
    let server = LoopbackServer::start(corpus.articles.clone())
        .await
        .unwrap();
    let downloads = Downloads::new(DOWNLOADS);
    let config = PoolConfig {
        max_connections: 3,
        pipeline_depth: 4,
        idle_timeout: Duration::from_millis(200),
        min_connections: 0,
    };
    let pool = pool(&server, config, &downloads);

    // Abandon requests at every stage; the connection must stay in sync.
    for wait in [0, 1, 2, 5] {
        let id = &corpus.articles[wait].message_id;
        let _ = tokio::time::timeout(Duration::from_micros(wait as u64 * 50), pool.body(id)).await;
    }
    fetch_all(&pool, &corpus).await;

    assert!(server.peak_connections() <= config.max_connections);
    assert_eq!(downloads.available_permits(), DOWNLOADS);
    tokio::time::sleep(config.idle_timeout * 3).await;
    assert_eq!(server.open_connections(), 0, "idle connections are closed");
    fetch_all(&pool, &corpus).await;
}

#[tokio::test]
async fn pool_retries_requests_lost_to_dropped_connections() {
    let corpus = Arc::new(Corpus::generate(1025, 24, ARTICLE_SIZE));
    let server = LoopbackServer::start_dropping(corpus.articles.clone(), 5)
        .await
        .unwrap();
    let downloads = Downloads::new(DOWNLOADS);
    let config = PoolConfig {
        max_connections: 4,
        pipeline_depth: 4,
        idle_timeout: Duration::from_secs(5),
        min_connections: 0,
    };
    let pool = pool(&server, config, &downloads);

    fetch_all(&pool, &corpus).await;
    assert_eq!(downloads.available_permits(), DOWNLOADS);
}

#[tokio::test]
#[allow(clippy::significant_drop_tightening)] // False positive: the pool is used until the end.
async fn pool_prewarms_up_to_its_limit_and_keeps_its_warm_floor() {
    let corpus = Arc::new(Corpus::generate(1025, 8, ARTICLE_SIZE));
    let server = LoopbackServer::start(corpus.articles.clone())
        .await
        .unwrap();
    let downloads = Downloads::new(DOWNLOADS);
    let config = PoolConfig {
        max_connections: 4,
        pipeline_depth: 2,
        idle_timeout: Duration::from_millis(100),
        min_connections: 2,
    };
    let usage = Arc::new(Usage::default());
    let endpoint = Endpoint::new(
        "127.0.0.1",
        server.addr().port(),
        TlsMode::Plain,
        None,
        Duration::from_secs(5),
    )
    .unwrap()
    .with_usage(Arc::clone(&usage));
    let pool = Arc::new(Pool::new(endpoint, config, Arc::clone(&downloads)).unwrap());

    pool.prewarm(10).await;
    pool.prewarm(10).await;
    assert_eq!(server.open_connections(), 4);
    fetch_all(&pool, &corpus).await;
    assert_eq!(server.peak_connections(), 4);

    // Past several idle timeouts the floor stays open, silent, and still serves; the
    // connections above it said QUIT.
    tokio::time::sleep(config.idle_timeout * 5).await;
    assert_eq!(server.open_connections(), 2);
    assert_eq!(server.quit_commands(), 2);
    pool.stat(&corpus.articles[0].message_id).await.unwrap();
    assert!(matches!(
        pool.stat("missing@test").await,
        Err(infinidysk_nntp::Error::NotFound)
    ));

    // A used-up quota releases the floor.
    usage.set_cap(Some(1));
    tokio::time::sleep(config.idle_timeout * 3).await;
    assert_eq!(server.open_connections(), 0);
    assert_eq!(server.quit_commands(), 4);
}

#[tokio::test]
async fn a_warm_connection_the_provider_hangs_up_on_is_replaced_and_dropping_the_pool_quits() {
    let corpus = Arc::new(Corpus::generate(1025, 2, ARTICLE_SIZE));
    let server = LoopbackServer::start(corpus.articles.clone())
        .await
        .unwrap();
    let downloads = Downloads::new(DOWNLOADS);
    let config = PoolConfig {
        max_connections: 2,
        pipeline_depth: 2,
        idle_timeout: Duration::from_secs(30),
        min_connections: 1,
    };
    let pool = pool(&server, config, &downloads);
    pool.prewarm(1).await;
    assert_eq!(server.open_connections(), 1);

    // Noticed right away, well before any idle timeout, so prewarm opens a new one.
    server.hang_up();
    tokio::time::sleep(Duration::from_millis(100)).await;
    assert_eq!(server.open_connections(), 0);
    pool.prewarm(1).await;
    assert_eq!(server.open_connections(), 1);
    pool.stat(&corpus.articles[0].message_id).await.unwrap();
    assert_eq!(server.peak_connections(), 1);
    assert_eq!(server.quit_commands(), 0);

    drop(pool);
    tokio::time::sleep(Duration::from_millis(100)).await;
    assert_eq!(server.quit_commands(), 1);
    assert_eq!(server.open_connections(), 0);
}

async fn scripted_pool(max: usize, idle_timeout: Duration) -> (Pool, TcpListener, Arc<Usage>) {
    let listener = TcpListener::bind("127.0.0.1:0").await.unwrap();
    let usage = Arc::new(Usage::default());
    let endpoint = Endpoint::new(
        "127.0.0.1",
        listener.local_addr().unwrap().port(),
        TlsMode::Plain,
        None,
        Duration::from_secs(3),
    )
    .unwrap()
    .with_usage(Arc::clone(&usage));
    let config = PoolConfig {
        max_connections: max,
        min_connections: 0,
        pipeline_depth: 1,
        idle_timeout,
    };
    (
        Pool::new(endpoint, config, Downloads::new(2)).unwrap(),
        listener,
        usage,
    )
}

async fn greet(listener: &TcpListener) -> BufReader<TcpStream> {
    let (mut socket, _) = timeout(Duration::from_secs(3), listener.accept())
        .await
        .unwrap()
        .unwrap();
    socket.write_all(b"200 ready\r\n").await.unwrap();
    BufReader::new(socket)
}

async fn expect_command(socket: &mut BufReader<TcpStream>, expected: &str) {
    let mut line = String::new();
    timeout(Duration::from_secs(3), socket.read_line(&mut line))
        .await
        .unwrap()
        .unwrap();
    assert_eq!(line.trim_end(), expected);
}

async fn answer_stat(listener: &TcpListener) {
    let mut next = greet(listener).await;
    expect_command(&mut next, "STAT <a@test>").await;
    next.get_mut()
        .write_all(b"223 0 <a@test>\r\n")
        .await
        .unwrap();
}

#[tokio::test]
async fn retiring_socket_holds_its_only_slot_until_quit_finishes() {
    let (pool, listener, _) = scripted_pool(1, Duration::from_millis(20)).await;
    let ((), mut first) = tokio::join!(pool.prewarm(1), greet(&listener));
    expect_command(&mut first, "QUIT").await;
    let (stat, ()) = tokio::join!(pool.stat("a@test"), async {
        assert!(
            timeout(Duration::from_millis(100), listener.accept())
                .await
                .is_err(),
            "a replacement connected before the provider answered QUIT"
        );
        first.get_mut().write_all(b"205 closing\r\n").await.unwrap();
        drop(first);
        answer_stat(&listener).await;
    });
    stat.unwrap();
}

#[tokio::test]
async fn partial_idle_notice_does_not_block_stat_on_a_spare_slot() {
    let (pool, listener, usage) = scripted_pool(2, Duration::from_secs(30)).await;
    let ((), mut first) = tokio::join!(pool.prewarm(1), greet(&listener));
    let before = usage.used();
    first.get_mut().write_all(b"400 idle").await.unwrap();
    timeout(Duration::from_secs(1), async {
        while usage.used() == before {
            tokio::task::yield_now().await;
        }
    })
    .await
    .unwrap();
    let (stat, ()) = timeout(Duration::from_millis(250), async {
        tokio::join!(pool.stat("a@test"), answer_stat(&listener))
    })
    .await
    .expect("STAT must not wait for the rest of the 400 line");
    stat.unwrap();
}

#[tokio::test]
async fn pool_shares_live_connections_when_the_provider_refuses_more() {
    let corpus = Arc::new(Corpus::generate(1025, 48, ARTICLE_SIZE));
    let server = LoopbackServer::start_limited(corpus.articles.clone(), 2)
        .await
        .unwrap();
    let downloads = Downloads::new(DOWNLOADS);
    let config = PoolConfig {
        max_connections: 6,
        pipeline_depth: 2,
        idle_timeout: Duration::from_secs(5),
        min_connections: 0,
    };
    let pool = pool(&server, config, &downloads);

    fetch_all(&pool, &corpus).await;
    assert_eq!(downloads.available_permits(), DOWNLOADS);
}

#[tokio::test]
#[allow(clippy::significant_drop_tightening)] // False positive: the pool is used to the end.
async fn connects_that_never_reach_the_provider_do_not_shrink_the_pool() {
    let corpus = Arc::new(Corpus::generate(1025, 48, ARTICLE_SIZE));
    let server = LoopbackServer::start(corpus.articles.clone())
        .await
        .unwrap();
    let downloads = Downloads::new(DOWNLOADS);
    let config = PoolConfig {
        max_connections: 4,
        pipeline_depth: 2,
        idle_timeout: Duration::from_secs(10),
        min_connections: 0,
    };
    let pool = pool(&server, config, &downloads);
    pool.prewarm(2).await;

    // New connects are refused at TCP; the two open connections carry every request.
    server.refuse().await;
    fetch_all(&pool, &corpus).await;
    assert_eq!(server.peak_connections(), 2);

    server.resume().await.unwrap();
    tokio::time::sleep(Duration::from_millis(1500)).await;
    fetch_all(&pool, &corpus).await;
    assert_eq!(
        server.peak_connections(),
        config.max_connections,
        "the pool opens its full limit once the backoff ends"
    );
}

#[tokio::test]
async fn connects_that_succeed_after_a_refusal_raise_the_limit_again() {
    use std::sync::atomic::{AtomicBool, AtomicUsize, Ordering};
    use tokio::io::{AsyncBufReadExt, AsyncReadExt, AsyncWriteExt, BufReader};
    use tokio::net::{TcpListener, TcpStream};

    // Answers STAT, except that the first command on any connection drops it.
    async fn serve(stream: TcpStream, dropped: Arc<AtomicBool>) {
        let mut stream = BufReader::new(stream);
        stream.get_mut().write_all(b"200 ready\r\n").await.unwrap();
        let mut line = String::new();
        while stream.read_line(&mut line).await.unwrap_or(0) > 0 {
            if !dropped.swap(true, Ordering::SeqCst) {
                return;
            }
            stream
                .get_mut()
                .write_all(b"223 0 <a@test>\r\n")
                .await
                .unwrap();
            line.clear();
        }
    }

    let listener = TcpListener::bind("127.0.0.1:0").await.unwrap();
    let port = listener.local_addr().unwrap().port();
    let accepted = Arc::new(AtomicUsize::new(0));
    let dropped = Arc::new(AtomicBool::new(false));
    let server = tokio::spawn({
        let (accepted, dropped) = (Arc::clone(&accepted), Arc::clone(&dropped));
        async move {
            let mut pending = Vec::new();
            for _ in 0..4 {
                pending.push(listener.accept().await.unwrap().0);
                accepted.fetch_add(1, Ordering::SeqCst);
            }
            // One is refused while the other three are still waiting for their greeting.
            let mut refused = pending.remove(0);
            refused
                .write_all(b"502 too many connections\r\n")
                .await
                .unwrap();
            let _ = refused.read(&mut [0]).await;
            for stream in pending {
                tokio::spawn(serve(stream, Arc::clone(&dropped)));
            }
            loop {
                let stream = listener.accept().await.unwrap().0;
                accepted.fetch_add(1, Ordering::SeqCst);
                tokio::spawn(serve(stream, Arc::clone(&dropped)));
            }
        }
    });
    let endpoint = Endpoint::new(
        "127.0.0.1",
        port,
        TlsMode::Plain,
        None,
        Duration::from_secs(5),
    )
    .unwrap();
    let config = PoolConfig {
        max_connections: 4,
        pipeline_depth: 2,
        idle_timeout: Duration::from_secs(30),
        min_connections: 0,
    };
    let pool = Pool::new(endpoint, config, Downloads::new(DOWNLOADS)).unwrap();

    let wait = Duration::from_secs(3);
    tokio::time::timeout(wait, pool.prewarm(4)).await.unwrap();
    // One of the three connections drops; the request is retried on another.
    tokio::time::timeout(wait, pool.stat("a@test"))
        .await
        .unwrap()
        .unwrap();
    tokio::time::timeout(wait, pool.prewarm(3)).await.unwrap();
    server.abort();
    assert_eq!(
        accepted.load(Ordering::SeqCst),
        5,
        "three connects succeeded, so the dropped one is replaced"
    );
}

#[tokio::test]
#[allow(clippy::significant_drop_tightening)] // False positive: the pool is used to the end.
async fn cold_start_requests_wait_for_their_own_connection_rather_than_queue() {
    let corpus = Arc::new(Corpus::generate(1025, 2, ARTICLE_SIZE));
    let response = Duration::from_millis(300);
    let impairment = Impairment {
        handshake_delay: Duration::from_millis(50),
        response_delay: response,
        ..Impairment::default()
    };
    let server = LoopbackServer::start_impaired(corpus.articles.clone(), impairment)
        .await
        .unwrap();
    let downloads = Downloads::new(DOWNLOADS);
    let config = PoolConfig {
        max_connections: 2,
        pipeline_depth: 2,
        idle_timeout: Duration::from_secs(5),
        min_connections: 0,
    };
    let pool = pool(&server, config, &downloads);

    // A stream's prewarm holds every slot while the first segments are requested.
    tokio::spawn({
        let pool = Arc::clone(&pool);
        async move { pool.prewarm(2).await }
    });
    tokio::time::sleep(Duration::from_millis(5)).await;
    let started = std::time::Instant::now();
    let (a, b) = tokio::join!(
        pool.body(&corpus.articles[0].message_id),
        pool.body(&corpus.articles[1].message_id),
    );
    a.unwrap();
    b.unwrap();
    assert!(
        started.elapsed() < response * 2,
        "one request queued behind the other: {:?}",
        started.elapsed()
    );
}
