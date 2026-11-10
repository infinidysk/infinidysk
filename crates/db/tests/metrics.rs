use infinidysk_core::ItemId;
use infinidysk_db::metrics::{BreakerEvent, Grain, Metrics, SessionRecord};

const MINUTE: i64 = 60_000;
const DAY: i64 = 24 * 60 * MINUTE;

fn temp_path() -> std::path::PathBuf {
    std::env::temp_dir().join(format!("infinidysk-metrics-{}.sqlite", ItemId::new()))
}

fn remove(path: &std::path::Path) {
    for suffix in ["", "-wal", "-shm"] {
        let _ = std::fs::remove_file(format!("{}{suffix}", path.display()));
    }
}

#[tokio::test]
async fn pruning_keeps_lifetime_totals_equal_to_everything_recorded() {
    let path = temp_path();
    let metrics = Metrics::open(&path).await.expect("open");
    assert!(!metrics.recreated());
    // Three hours of minutes, the last one written twice.
    let mut sum = 0;
    let mut peak = 0;
    for minute in 0..180_u64 {
        let (bytes, rate) = (minute * 7 + 1, (minute * 37) % 101);
        let counters = [("bytes".to_owned(), bytes), ("peak.rate".to_owned(), rate)];
        metrics
            .write(
                i64::try_from(minute).expect("small") * MINUTE + 5,
                &counters,
                &[],
                &[],
            )
            .await
            .expect("write");
        (sum, peak) = (sum + bytes, peak.max(rate));
    }
    let hours = metrics.series(Grain::Hour, 0, 1).await.expect("series");
    let hourly: u64 = hours
        .iter()
        .filter(|(_, k, _)| k == "bytes")
        .map(|r| r.2)
        .sum();
    assert_eq!(hourly, sum, "hour rows fold every minute");
    assert_eq!(hours.iter().filter(|(_, k, _)| k == "bytes").count(), 3);

    metrics.prune(8 * DAY).await.expect("prune");
    assert!(
        metrics
            .series(Grain::Minute, 0, 1)
            .await
            .expect("series")
            .is_empty(),
        "minute rows pruned"
    );
    let mut lifetime = metrics.lifetime().await.expect("lifetime");
    lifetime.sort();
    assert_eq!(
        lifetime,
        [("bytes".to_owned(), sum), ("peak.rate".to_owned(), peak)]
    );
    metrics.close().await;
    remove(&path);
}

#[tokio::test]
async fn compacting_returns_unused_pages_to_the_disk() {
    let path = temp_path();
    let metrics = Metrics::open(&path).await.expect("open");
    let counters: Vec<_> = (0..200_u64).map(|n| (format!("counter.{n}"), n)).collect();
    for minute in 0..120 {
        metrics
            .write(minute * MINUTE, &counters, &[], &[])
            .await
            .expect("write");
    }
    metrics.prune(8 * DAY).await.expect("prune");
    let before = metrics.stats().await.expect("stats");
    assert_eq!(
        before.path.canonicalize().expect("path"),
        path.canonicalize().expect("path")
    );
    assert!(before.free_bytes > 0, "pruning left unused pages");

    metrics.compact().await.expect("compact");
    let after = metrics.stats().await.expect("stats");
    assert_eq!(after.free_bytes, 0);
    assert!(after.size_bytes < before.size_bytes);
    let wal = std::fs::metadata(format!("{}-wal", path.display())).map_or(0, |m| m.len());
    assert_eq!(wal, 0, "the WAL was truncated");
    metrics.close().await;
    remove(&path);
}

#[tokio::test]
async fn records_pick_the_busiest_hour_and_day_by_provider_bytes() {
    let path = temp_path();
    let metrics = Metrics::open(&path).await.expect("open");
    let hour = 60 * MINUTE;
    for (at, a, b) in [(0, 5, 5), (hour, 8, 0), (DAY, 6, 6), (DAY + hour, 1, 0)] {
        let counters = [
            ("p.a.stream.bytes".to_owned(), a),
            ("p.b.queue.bytes".to_owned(), b),
            ("served.webdav".to_owned(), 100),
            ("rescue.a.b.missing".to_owned(), 1),
        ];
        metrics.write(at, &counters, &[], &[]).await.expect("write");
    }
    let records = metrics.records().await.expect("records");
    assert_eq!(records.busiest_hour, Some((DAY, 12)));
    assert_eq!(records.busiest_day, Some((0, 18)));
    assert_eq!(records.first_hour, Some(0));
    let rescues = metrics
        .sum(Grain::Hour, hour, DAY + hour, "rescue.")
        .await
        .expect("sum");
    assert_eq!(rescues, 2);
    metrics.close().await;
    remove(&path);
}

#[tokio::test]
async fn outages_keep_their_provider_key_as_long_as_hour_rows() {
    let path = temp_path();
    let metrics = Metrics::open(&path).await.expect("open");
    let outage = BreakerEvent {
        at: DAY,
        provider_key: Some("p1".to_owned()),
        provider: "Old name".to_owned(),
        open_ms: 10 * MINUTE,
    };
    metrics
        .write(DAY, &[], &[], std::slice::from_ref(&outage))
        .await
        .expect("write");

    let none: Vec<BreakerEvent> = Vec::new();
    // Still open five minutes into the window, so the window shows it.
    let since = DAY + 5 * MINUTE;
    assert_eq!(
        metrics.breaker_events(since).await.expect("events"),
        std::slice::from_ref(&outage)
    );
    assert_eq!(
        metrics
            .breaker_events(DAY + 11 * MINUTE)
            .await
            .expect("events"),
        none
    );

    metrics.prune(30 * DAY).await.expect("prune");
    assert_eq!(metrics.breaker_events(0).await.expect("events"), [outage]);
    metrics.prune(500 * DAY).await.expect("prune");
    assert_eq!(metrics.breaker_events(0).await.expect("events"), none);
    metrics.close().await;
    remove(&path);
}

#[tokio::test]
async fn reads_keep_their_item() {
    let path = temp_path();
    let metrics = Metrics::open(&path).await.expect("open");
    let read = |item, closed_at| SessionRecord {
        item,
        opened_at: 0,
        closed_at,
        path: "/content/a/file.mkv".to_owned(),
        delivery: "webdav".to_owned(),
        client: "test".to_owned(),
        size: 10,
        served: 10,
        first_byte_ms: Some(1),
        stalls: 0,
        stall_ms: 0,
        seeks: 0,
        end_reason: "eof".to_owned(),
    };
    let (a, b) = (ItemId::new(), ItemId::new());
    let reads = [
        read(Some(a), 4),
        read(Some(b), 3),
        read(Some(a), 2),
        read(None, 1),
    ];
    metrics.write(0, &[], &reads, &[]).await.expect("write");
    assert_eq!(metrics.sessions(10).await.expect("sessions"), reads);
    assert_eq!(
        metrics.item_sessions(a, 10).await.expect("item sessions"),
        [reads[0].clone(), reads[2].clone()]
    );
    let top = metrics.top_items(2, 10).await.expect("top items");
    let summed: Vec<_> = top
        .iter()
        .map(|t| (t.item, t.reads, t.served, t.last_closed_at))
        .collect();
    assert_eq!(summed, [(a, 2, 20, 4), (b, 1, 10, 3)]);
    metrics.close().await;
    remove(&path);
}

#[tokio::test]
async fn usage_groups_reads_by_program_without_pids_or_addresses() {
    let path = temp_path();
    let metrics = Metrics::open(&path).await.expect("open");
    let read = |delivery: &str, client: &str, served| SessionRecord {
        item: None,
        opened_at: 1,
        closed_at: 5,
        path: "/content/a/file.mkv".to_owned(),
        delivery: delivery.to_owned(),
        client: client.to_owned(),
        size: 100,
        served,
        first_byte_ms: None,
        stalls: 0,
        stall_ms: 0,
        seeks: 0,
        end_reason: "eof".to_owned(),
    };
    let reads = [
        read("fuse", "player (pid 1, tid 2) · uid 0 gid 0", 10),
        read("fuse", "player (pid 3) · uid 0 gid 0", 20),
        read("fuse", "silo › ffmpeg (pid 5) · uid 0 gid 0", 7),
        read("fuse", "silo › ffmpeg (pid 6, tid 7) · uid 0 gid 0", 8),
        read("fuse", "other › ffmpeg (pid 8) · uid 0 gid 0", 3),
        read("fuse", "ffmpeg (pid 9) · uid 0 gid 0", 4),
        read("fuse", "pid 4 · uid 0 gid 0", 1),
        read("webdav", "agent/1.0 · 10.0.0.1", 5),
        read("webdav", "10.0.0.2", 2),
    ];
    metrics.write(0, &[], &reads, &[]).await.expect("write");
    let usage: Vec<_> = metrics
        .usage(0)
        .await
        .expect("usage")
        .into_iter()
        .map(|u| (u.delivery, u.program, u.reads, u.served, u.open_ms))
        .collect();
    let row = |d: &str, p: &str, reads, served, ms| (d.to_owned(), p.to_owned(), reads, served, ms);
    assert_eq!(
        usage,
        [
            row("fuse", "player", 2, 30, 8),
            row("fuse", "silo › ffmpeg", 2, 15, 8),
            row("webdav", "agent/1.0", 1, 5, 4),
            row("fuse", "ffmpeg", 1, 4, 4),
            row("fuse", "other › ffmpeg", 1, 3, 4),
            row("webdav", "10.0.0.2", 1, 2, 4),
            row("fuse", "", 1, 1, 4),
        ]
    );
    metrics.close().await;
    remove(&path);
}

#[tokio::test]
async fn an_unreadable_store_is_moved_aside_and_replaced() {
    let path = temp_path();
    let junk = b"not a database, just some bytes long enough to look like one";
    std::fs::write(&path, junk).expect("write");
    let metrics = Metrics::open(&path).await.expect("open");
    assert!(metrics.recreated());
    metrics
        .write(0, &[("bytes".to_owned(), 1)], &[], &[])
        .await
        .expect("write");
    metrics.close().await;
    let aside = std::path::PathBuf::from(format!("{}.unreadable", path.display()));
    assert_eq!(std::fs::read(&aside).expect("kept"), junk);
    remove(&path);
    remove(&aside);
}

#[tokio::test]
async fn a_store_with_unknown_migrations_is_left_alone() {
    let path = temp_path();
    let metrics = Metrics::open(&path).await.expect("open");
    metrics
        .write(0, &[("bytes".to_owned(), 7)], &[], &[])
        .await
        .expect("write");
    metrics.close().await;
    // As if a newer build had migrated it.
    let pool = sqlx::SqlitePool::connect(&format!("sqlite:{}", path.display()))
        .await
        .expect("raw");
    sqlx::query(
        "INSERT INTO _sqlx_migrations (version, description, success, checksum, execution_time) \
         VALUES (9999, 'future', TRUE, x'00', 0)",
    )
    .execute(&pool)
    .await
    .expect("future migration");
    pool.close().await;

    assert!(Metrics::open(&path).await.is_err(), "refuses to touch it");
    let pool = sqlx::SqlitePool::connect(&format!("sqlite:{}", path.display()))
        .await
        .expect("still there");
    let (bytes,): (i64,) = sqlx::query_as("SELECT value FROM lifetime WHERE key = 'bytes'")
        .fetch_one(&pool)
        .await
        .expect("history kept");
    assert_eq!(bytes, 7);
    pool.close().await;
    remove(&path);
}
