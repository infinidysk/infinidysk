#![allow(clippy::unwrap_used)] // Test helpers outside `#[test]` fns; panics are the assertions.

use std::sync::Arc;
use std::time::Duration;

use infinidysk_nntp::{Downloads, Endpoint, Pool, PoolConfig, TlsMode, Usage};
use infinidysk_testkit::{Corpus, Impairment, LoopbackServer};
use tokio::task::JoinSet;

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

    // Past several idle timeouts the floor survives on keepalives and still serves.
    tokio::time::sleep(config.idle_timeout * 5).await;
    assert_eq!(server.open_connections(), 2);
    pool.stat(&corpus.articles[0].message_id).await.unwrap();
    assert!(matches!(
        pool.stat("missing@test").await,
        Err(infinidysk_nntp::Error::NotFound)
    ));

    // A used-up quota releases the floor.
    usage.set_cap(Some(1));
    tokio::time::sleep(config.idle_timeout * 3).await;
    assert_eq!(server.open_connections(), 0);
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
