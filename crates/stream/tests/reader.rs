#![allow(clippy::unwrap_used)] // Test helpers outside `#[test]` fns; panics are the assertions.

use std::io::SeekFrom;
use std::sync::Arc;
use std::time::Duration;

use infinidysk_core::trace::{Event, Step, Trace};
use infinidysk_core::wait::{Stage, Watch};
use infinidysk_nntp::{Client, Downloads, Endpoint, Pool, PoolConfig, Provider, TlsMode};
use infinidysk_stream::{
    Budget, Cache, CacheConfig, DiskConfig, Fault, Filler, Gaps, Layout, Report, Segment,
    SegmentReader,
};
use infinidysk_testkit::{Corpus, Impairment, LoopbackServer};
use tokio::io::{AsyncReadExt, AsyncSeekExt};

const ARTICLE_SIZE: usize = 16 * 1024;
const MISSING: usize = 5;

#[tokio::test]
async fn waiting_segment_admission_leaves_memory_for_other_readers() {
    let client = Arc::new(Client::new([]));
    let cache = Arc::new(Cache::new(true, 1024));
    cache.insert("admission@test", bytes::Bytes::from_static(b"new"));
    let budget = Budget::new(3);
    let layout = Arc::new(Layout::new(vec![Segment {
        message_id: "admission@test".into(),
        size: 3,
        begin: None,
    }]));
    let make_reader = || {
        SegmentReader::new(Arc::clone(&client), Arc::clone(&layout), 1)
            .with_cache(Arc::clone(&cache))
            .with_budget(budget.clone())
    };
    let entered = Arc::new(tokio::sync::Notify::new());
    let (release, waiting) = tokio::sync::watch::channel(false);
    let mut blocked = make_reader().with_access(Arc::new({
        let entered = Arc::clone(&entered);
        move |_| {
            let (entered, mut waiting) = (Arc::clone(&entered), waiting.clone());
            Box::pin(async move {
                entered.notify_one();
                while !*waiting.borrow_and_update() {
                    waiting.changed().await.map_err(std::io::Error::other)?;
                }
                Ok(())
            })
        }
    }));
    let blocked = tokio::spawn(async move {
        let mut bytes = Vec::new();
        blocked.read_to_end(&mut bytes).await.unwrap();
        bytes
    });
    tokio::time::timeout(Duration::from_secs(1), entered.notified())
        .await
        .unwrap();
    {
        let mut free = make_reader();
        let mut bytes = Vec::new();
        tokio::time::timeout(Duration::from_secs(1), free.read_to_end(&mut bytes))
            .await
            .unwrap()
            .unwrap();
        assert_eq!(bytes, b"new");
    }
    release.send_replace(true);
    assert_eq!(blocked.await.unwrap(), b"new");
}

#[tokio::test]
async fn segment_admission_covers_cache_hits_and_known_hole_filler() {
    for known_hole in [false, true] {
        let client = Arc::new(Client::new([]));
        let cache = Arc::new(Cache::new(true, 1024));
        cache.insert("admission@test", bytes::Bytes::from_static(b"old"));
        let gaps = Arc::new(Gaps::default());
        if known_hole {
            gaps.refresh([0].into_iter().collect(), false);
        }
        let mut reader = SegmentReader::new(
            client,
            Arc::new(Layout::new(vec![Segment {
                message_id: "admission@test".into(),
                size: 3,
                begin: None,
            }])),
            1,
        )
        .with_cache(cache)
        .with_gaps(gaps)
        .with_access(Arc::new(|id| {
            Box::pin(async move {
                assert_eq!(id, "admission@test");
                Err(std::io::Error::other("publication blocks old bytes"))
            })
        }));
        let mut bytes = Vec::new();
        let error = reader.read_to_end(&mut bytes).await.unwrap_err();
        assert!(error.to_string().contains("publication blocks old bytes"));
        assert_eq!(bytes, [] as [u8; 0]);
    }
}

#[tokio::test]
async fn omitted_articles_keep_separate_source_keys_and_never_use_an_empty_id_cache_entry() {
    use infinidysk_core::{BlobId, ByteRange, repair::Omitted};
    use infinidysk_nntp::{Body, RepairKey};
    let first = Omitted {
        nzb: BlobId::new(),
        generation: 0,
        set: [1; 16],
        file: [2; 16],
        range: ByteRange {
            start: 100,
            end: 103,
        },
        file_length: 200,
    };
    let second = Omitted {
        set: [3; 16],
        ..first
    };
    let missing = Omitted {
        nzb: BlobId::new(),
        ..first
    };
    let client = Arc::new(Client::new([]));
    client.set_repairs(Box::new(move |key| {
        let RepairKey::Omitted(key) = key else {
            return None;
        };
        let data = if *key == first {
            b"abc"
        } else if *key == second {
            b"def"
        } else {
            return None;
        };
        Some(Box::pin(async {
            Ok(Some(Body {
                data: bytes::Bytes::from_static(data),
                offset: 100,
                file_size: 200,
                part: Some(2),
                total: Some(3),
            }))
        }))
    }));
    let layout = Arc::new(
        Layout::new(
            (0..3)
                .map(|_| Segment {
                    message_id: String::new(),
                    size: 3,
                    begin: Some(100),
                })
                .collect(),
        )
        .with_omitted([(0, first), (1, second), (2, missing)])
        .unwrap(),
    );
    let cache = Arc::new(Cache::new(true, 1_000_000));
    cache.insert("", bytes::Bytes::from_static(b"bad"));
    for known in [false, true] {
        let gaps = Arc::new(Gaps::default());
        if known {
            gaps.refresh([0, 1, 2].into_iter().collect(), false);
        }
        let mut reader = SegmentReader::new(Arc::clone(&client), Arc::clone(&layout), 3)
            .with_cache(Arc::clone(&cache))
            .with_gaps(gaps);
        let mut bytes = Vec::new();
        reader.read_to_end(&mut bytes).await.unwrap();
        assert_eq!(bytes, b"abcdef\0\0\0");
    }
    let mut reader = SegmentReader::new(client, layout, 3)
        .with_cache(cache)
        .with_strict();
    let mut bytes = Vec::new();
    assert!(reader.read_to_end(&mut bytes).await.is_err());
    assert_eq!(bytes, b"abcdef");
}

#[tokio::test]
async fn a_repaired_article_is_never_padded_to_fit_stale_layout_sizes() {
    let client = Arc::new(Client::new([]));
    client.set_repairs(Box::new(|_| {
        Some(Box::pin(async {
            Ok(Some(infinidysk_nntp::Body {
                data: bytes::Bytes::from_static(b"abc"),
                offset: 0,
                file_size: 3,
                part: None,
                total: None,
            }))
        }))
    }));
    let layout = Arc::new(Layout::new(vec![Segment {
        message_id: "repair@loopback".into(),
        size: 4,
        begin: Some(0),
    }]));
    for cached in [false, true] {
        let mut reader = SegmentReader::new(Arc::clone(&client), Arc::clone(&layout), 1);
        if cached {
            reader = reader.with_cache(Arc::new(Cache::new(true, 1_000_000)));
        }
        let error = reader.read_to_end(&mut Vec::new()).await.unwrap_err();
        assert_eq!(error.kind(), std::io::ErrorKind::InvalidData);
    }
}

#[tokio::test]
async fn repaired_articles_beat_cached_bytes_and_known_holes_in_order() {
    for (cached, known, strict, disk) in [
        (false, false, false, false),
        (true, false, false, false),
        (true, true, false, false),
        (false, true, true, false),
        (true, false, false, true),
    ] {
        let corpus = Corpus::generate(1025, 12, ARTICLE_SIZE);
        let served = corpus
            .articles
            .iter()
            .enumerate()
            .filter_map(|(i, a)| (i != MISSING).then_some(a.clone()));
        let server = LoopbackServer::start(served).await.unwrap();
        let (client, layout) = setup(&corpus, &server);
        let id = corpus.articles[MISSING].message_id.clone();
        let body = infinidysk_nntp::Body {
            data: bytes::Bytes::copy_from_slice(
                &corpus.data[MISSING * ARTICLE_SIZE..(MISSING + 1) * ARTICLE_SIZE],
            ),
            offset: (MISSING * ARTICLE_SIZE) as u64,
            file_size: corpus.data.len() as u64,
            part: Some(6),
            total: Some(12),
        };
        let repaired_id = id.clone();
        client.set_repairs(Box::new(move |id| {
            if !matches!(id, infinidysk_nntp::RepairKey::Article(id) if id == repaired_id) {
                return None;
            }
            let body = body.clone();
            Some(Box::pin(async move {
                tokio::time::sleep(Duration::from_millis(20)).await;
                Ok(Some(body))
            }))
        }));
        let cache = Arc::new(Cache::new(!disk, 1_000_000));
        let dir = std::env::temp_dir().join(format!(
            "infinidysk-repair-cache-{}",
            infinidysk_core::ItemId::new()
        ));
        if disk {
            cache
                .configure(&CacheConfig {
                    memory_enabled: false,
                    memory_bytes: 1_000_000,
                    disk: Some(DiskConfig {
                        dir: dir.clone(),
                        max_bytes: 1_000_000,
                        max_age: Duration::from_secs(3600),
                    }),
                })
                .await;
        }
        cache.insert(&id, bytes::Bytes::from(vec![9; ARTICLE_SIZE]));
        if disk {
            tokio::time::timeout(Duration::from_secs(5), async {
                while cache.stats().disk.is_none_or(|s| s.items != 1) {
                    tokio::time::sleep(Duration::from_millis(10)).await;
                }
            })
            .await
            .unwrap();
        }
        let gaps = Arc::new(Gaps::default());
        if known {
            gaps.refresh([MISSING].into_iter().collect(), true);
        }
        let mut reader = SegmentReader::new(client, layout, 8)
            .with_gaps(gaps)
            .with_budget(Budget::new(4 * ARTICLE_SIZE));
        if cached {
            reader = reader.with_cache(Arc::clone(&cache));
        }
        if strict {
            reader = reader.with_strict();
        }
        let mut actual = Vec::new();
        tokio::time::timeout(Duration::from_secs(5), reader.read_to_end(&mut actual))
            .await
            .unwrap()
            .unwrap();
        assert_eq!(actual, corpus.data);
        assert_eq!(server.body_commands(), 11);
        if disk {
            drop(reader);
            tokio::time::timeout(Duration::from_secs(5), async {
                while cache.stats().disk.is_none_or(|s| s.items != 12) {
                    tokio::time::sleep(Duration::from_millis(10)).await;
                }
            })
            .await
            .unwrap();
            drop(cache);
            std::fs::remove_dir_all(dir).unwrap();
        }
    }
}

#[tokio::test]
async fn reads_and_seeks_across_segments_zero_filling_missing_ones() {
    let corpus = Corpus::generate(1025, 24, ARTICLE_SIZE);
    let mut served = corpus.articles.clone();
    served.remove(MISSING);
    let server = LoopbackServer::start(served).await.unwrap();
    let (client, layout) = setup(&corpus, &server);
    let mut expected = corpus.data.clone();
    expected[MISSING * ARTICLE_SIZE..(MISSING + 1) * ARTICLE_SIZE].fill(0);

    let mut reader = SegmentReader::new(client, layout, 8);
    let mut all = Vec::new();
    reader.read_to_end(&mut all).await.unwrap();
    assert!(all == expected, "sequential read");

    // Backwards out of the window, forwards inside it, far forwards, from the end.
    for (seek, len) in [
        (
            SeekFrom::Start(2 * ARTICLE_SIZE as u64 + 7),
            3 * ARTICLE_SIZE,
        ),
        (SeekFrom::Current(i64::try_from(ARTICLE_SIZE).unwrap()), 100),
        (SeekFrom::Start(20 * ARTICLE_SIZE as u64 - 1), 2),
        (SeekFrom::End(-100), 100),
    ] {
        let pos = usize::try_from(reader.seek(seek).await.unwrap()).unwrap();
        let mut buf = vec![0; len];
        reader.read_exact(&mut buf).await.unwrap();
        assert!(buf == expected[pos..pos + len], "read at {pos}");
    }
    assert_eq!(reader.read(&mut [0; 8]).await.unwrap(), 0, "end of file");

    let (client, layout) = setup(&corpus, &server);
    let mut strict = SegmentReader::new(client, layout, 8).with_strict();
    let mut head = vec![0; MISSING * ARTICLE_SIZE];
    strict.read_exact(&mut head).await.unwrap();
    assert_eq!(head, corpus.data[..head.len()]);
    assert!(
        strict.read(&mut [0; 8]).await.is_err(),
        "strict reads fail instead of zero-filling"
    );

    let (client, layout) = setup(&corpus, &server);
    let mut ts = SegmentReader::new(client, layout, 8).with_filler(Filler::Ts188);
    let mut all = Vec::new();
    ts.read_to_end(&mut all).await.unwrap();
    let gap = MISSING * ARTICLE_SIZE;
    assert_eq!(
        all[gap..gap + ARTICLE_SIZE],
        Filler::Ts188.fill(gap as u64, ARTICLE_SIZE),
        "null packets on the file's packet grid"
    );
    assert_eq!(all[..gap], corpus.data[..gap]);
}

#[tokio::test]
async fn known_holes_skip_providers_and_too_many_in_a_row_fail() {
    let corpus = Corpus::generate(1025, 12, ARTICLE_SIZE);
    let server = LoopbackServer::start(corpus.articles.clone())
        .await
        .unwrap();
    let gaps = Arc::new(Gaps::default());
    gaps.refresh([3, 4].into(), false);
    let (client, layout) = setup(&corpus, &server);
    let mut reader = SegmentReader::new(client, layout, 8).with_gaps(Arc::clone(&gaps));
    let mut all = Vec::new();
    reader.read_to_end(&mut all).await.unwrap();
    assert_eq!(server.body_commands(), 10, "known holes are not requested");
    assert!(
        all[3 * ARTICLE_SIZE..5 * ARTICLE_SIZE]
            .iter()
            .all(|&b| b == 0)
    );

    gaps.refresh([3, 4, 5].into(), false);
    let (client, layout) = setup(&corpus, &server);
    let mut reader = SegmentReader::new(client, layout, 8).with_gaps(gaps);
    assert!(reader.read_to_end(&mut Vec::new()).await.is_err());
}

#[tokio::test]
async fn reading_back_into_the_previous_segment_fetches_nothing_again() {
    let corpus = Corpus::generate(1025, 12, ARTICLE_SIZE);
    let server = LoopbackServer::start(corpus.articles.clone())
        .await
        .unwrap();
    let (client, layout) = setup(&corpus, &server);
    let mut reader = SegmentReader::new(client, layout, 1);
    let mut buf = vec![0; 2 * ARTICLE_SIZE];
    reader.read_exact(&mut buf).await.unwrap();
    let fetched = server.body_commands();

    reader.seek(SeekFrom::Start(10)).await.unwrap();
    let mut back = vec![0; 100];
    reader.read_exact(&mut back).await.unwrap();
    assert_eq!(back, corpus.data[10..110]);
    assert_eq!(server.body_commands(), fetched);
}

#[tokio::test]
async fn bounded_reads_fetch_beyond_the_hint_only_on_demand() {
    let corpus = Corpus::generate(1025, 12, ARTICLE_SIZE);
    let server = LoopbackServer::start(corpus.articles.clone())
        .await
        .unwrap();
    for (end, start) in [
        (0, 0),
        (ARTICLE_SIZE, ARTICLE_SIZE - 1),
        (3 * ARTICLE_SIZE + 7, 3 * ARTICLE_SIZE + 2),
    ] {
        let (client, layout) = setup(&corpus, &server);
        let trace = Arc::new(Trace::new(10_000));
        let mut reader = SegmentReader::new(client, layout, 8)
            .with_trace(Arc::clone(&trace))
            .with_strict();
        reader.set_end(end as u64);
        reader.seek(SeekFrom::Start(start as u64)).await.unwrap();
        let mut byte = [0; 1];
        reader.read_exact(&mut byte).await.unwrap();
        assert_eq!(byte[0], corpus.data[start]);
        let index = u32::try_from(start / ARTICLE_SIZE).unwrap();
        assert_eq!(
            last_queued(&trace),
            index,
            "no speculative fetch past the hint"
        );

        let next = (start / ARTICLE_SIZE + 1) * ARTICLE_SIZE;
        reader.seek(SeekFrom::Start(next as u64)).await.unwrap();
        reader.read_exact(&mut byte).await.unwrap();
        assert_eq!(byte[0], corpus.data[next]);
        assert_eq!(
            last_queued(&trace),
            index + 1,
            "an explicit read still works"
        );
    }
}

#[tokio::test]
async fn the_window_opens_fully_once_the_first_segment_is_read() {
    let corpus = Corpus::generate(1025, 64, ARTICLE_SIZE);
    let server = LoopbackServer::start(corpus.articles.clone())
        .await
        .unwrap();
    let (client, layout) = setup(&corpus, &server);
    let mut reader = SegmentReader::new(client, layout, 32);
    reader.read_exact(&mut [0; 1]).await.unwrap();

    // The read segment plus a full window, without further reads.
    for _ in 0..200 {
        if server.body_commands() >= 33 {
            break;
        }
        tokio::time::sleep(Duration::from_millis(10)).await;
    }
    assert_eq!(server.body_commands(), 33);
}

#[tokio::test]
async fn the_window_opens_fully_when_the_reader_keeps_up_with_streaming_bodies() {
    const SIZE: usize = 256 * 1024;
    let corpus = Corpus::generate(1025, 48, SIZE);
    // A second per article, so every read is served from a body still arriving.
    let impairment = Impairment {
        bandwidth: Some(SIZE as u64),
        ..Impairment::default()
    };
    let server = LoopbackServer::start_impaired(corpus.articles.clone(), impairment)
        .await
        .unwrap();
    let (client, layout) = setup(&corpus, &server);
    let trace = Arc::new(Trace::new(10_000));
    let mut reader = SegmentReader::new(client, layout, 32).with_trace(Arc::clone(&trace));
    reader.read_exact(&mut [0; 10]).await.unwrap();
    reader.seek(SeekFrom::Start(SIZE as u64)).await.unwrap();
    reader.read_exact(&mut [0; 10]).await.unwrap();
    assert_eq!(last_queued(&trace), 32, "segment 1 plus a full window");
}

#[tokio::test]
async fn large_articles_ramp_the_window_up_instead_of_queueing_it_at_once() {
    const SIZE: usize = 1024 * 1024;
    let corpus = Corpus::generate(1025, 40, SIZE);
    let server = LoopbackServer::start(corpus.articles.clone())
        .await
        .unwrap();
    let (client, layout) = setup(&corpus, &server);
    let trace = Arc::new(Trace::new(10_000));
    let mut reader = SegmentReader::new(client, layout, 32).with_trace(Arc::clone(&trace));
    reader.read_exact(&mut [0; 1]).await.unwrap();
    // At most one segment read: 8 or 16 queued, not the full 32.
    let queued = last_queued(&trace);
    assert!(queued <= 16, "queued up to segment {queued}");
}

fn last_queued(trace: &Trace) -> u32 {
    trace
        .records()
        .0
        .iter()
        .filter_map(|r| match r.event {
            Event::Segment {
                segment,
                step: Step::Queued,
                hedge: false,
            } => Some(segment),
            _ => None,
        })
        .max()
        .unwrap()
}

#[tokio::test]
async fn readers_sharing_a_budget_smaller_than_a_segment_still_finish() {
    let corpus = Corpus::generate(1025, 12, ARTICLE_SIZE);
    let server = LoopbackServer::start(corpus.articles.clone())
        .await
        .unwrap();
    let (client, layout) = setup(&corpus, &server);
    let budget = Budget::new(ARTICLE_SIZE / 2);

    let read = |offset: u64| {
        let mut reader = SegmentReader::new(Arc::clone(&client), Arc::clone(&layout), 8)
            .with_budget(budget.clone());
        async move {
            reader.seek(SeekFrom::Start(offset)).await.unwrap();
            let mut all = Vec::new();
            reader.read_to_end(&mut all).await.unwrap();
            all
        }
    };
    let half = corpus.data.len() / 2;
    let (a, b) = tokio::join!(read(0), read(half as u64));
    assert!(a == corpus.data, "first reader");
    assert!(b == corpus.data[half..], "second reader");
    assert_eq!(budget.available(), ARTICLE_SIZE / 2, "budget returned");
}

#[tokio::test]
async fn an_idle_reader_holding_the_budget_does_not_freeze_another() {
    let corpus = Corpus::generate(1025, 12, ARTICLE_SIZE);
    let server = LoopbackServer::start(corpus.articles.clone())
        .await
        .unwrap();
    let (client, layout) = setup(&corpus, &server);
    let budget = Budget::new(4 * ARTICLE_SIZE);
    let reader = || {
        SegmentReader::new(Arc::clone(&client), Arc::clone(&layout), 8).with_budget(budget.clone())
    };
    // Its window takes the whole budget and queues more; it never reads again.
    let mut idle = reader();
    idle.read_exact(&mut [0; 1]).await.unwrap();

    let mut all = Vec::new();
    tokio::time::timeout(Duration::from_secs(10), reader().read_to_end(&mut all))
        .await
        .expect("the second reader finishes")
        .unwrap();
    assert!(all == corpus.data, "second reader");
}

#[tokio::test]
async fn readers_share_fetches_and_reread_from_the_cache() {
    let corpus = Corpus::generate(1025, 12, ARTICLE_SIZE);
    let server = LoopbackServer::start(corpus.articles.clone())
        .await
        .unwrap();
    let (client, layout) = setup(&corpus, &server);
    let cache = Arc::new(Cache::new(true, 64 * ARTICLE_SIZE as u64));

    let read = || {
        let mut reader = SegmentReader::new(Arc::clone(&client), Arc::clone(&layout), 8)
            .with_cache(Arc::clone(&cache));
        async move {
            let mut all = Vec::new();
            reader.read_to_end(&mut all).await.unwrap();
            all
        }
    };
    let (a, b) = tokio::join!(read(), read());
    assert!(a == corpus.data && b == corpus.data, "concurrent readers");
    assert_eq!(server.body_commands(), 12, "one request per segment");
    assert!(read().await == corpus.data, "cached reread");
    assert_eq!(server.body_commands(), 12, "reread from memory");
    let stats = cache.stats();
    assert_eq!((stats.misses, stats.memory_hits + stats.joined), (12, 24));
}

#[tokio::test]
async fn the_disk_cache_survives_a_restart() {
    let corpus = Corpus::generate(1025, 12, ARTICLE_SIZE);
    let server = LoopbackServer::start(corpus.articles.clone())
        .await
        .unwrap();
    let (client, layout) = setup(&corpus, &server);
    let dir = std::env::temp_dir().join(format!("infinidysk-disk-cache-{}", std::process::id()));
    let config = CacheConfig {
        memory_enabled: false,
        memory_bytes: 1 << 20,
        disk: Some(DiskConfig {
            dir: dir.clone(),
            max_bytes: 1 << 30,
            max_age: Duration::from_secs(3600),
        }),
    };
    let read = |cache: &Arc<Cache>| {
        let mut reader = SegmentReader::new(Arc::clone(&client), Arc::clone(&layout), 8)
            .with_cache(Arc::clone(cache));
        async move {
            let mut all = Vec::new();
            reader.read_to_end(&mut all).await.unwrap();
            all
        }
    };

    let cache = Arc::new(Cache::new(false, 1 << 20));
    cache.configure(&config).await;
    assert!(read(&cache).await == corpus.data, "first read");
    for _ in 0..500 {
        if cache.stats().disk.is_some_and(|d| d.items == 12) {
            break;
        }
        tokio::time::sleep(Duration::from_millis(10)).await;
    }
    drop(cache);

    let cache = Arc::new(Cache::new(false, 1 << 20));
    cache.configure(&config).await;
    for _ in 0..500 {
        if cache.stats().disk.is_some_and(|d| d.items == 12) {
            break;
        }
        tokio::time::sleep(Duration::from_millis(10)).await;
    }
    assert!(read(&cache).await == corpus.data, "read from disk");
    assert_eq!(server.body_commands(), 12, "nothing fetched twice");
    assert_eq!(cache.stats().disk_hits, 12);
    std::fs::remove_dir_all(dir).unwrap();
}

#[tokio::test]
async fn a_stalled_segment_is_hedged_instead_of_blocking_the_reader() {
    const STALL: Duration = Duration::from_secs(4);
    let corpus = Corpus::generate(1025, 12, ARTICLE_SIZE);
    let server = LoopbackServer::start_stalling(corpus.articles.clone(), 3, STALL)
        .await
        .unwrap();
    let (client, layout) = setup(&corpus, &server);

    let started = std::time::Instant::now();
    let trace = Arc::new(Trace::new(10_000));
    let mut all = Vec::new();
    SegmentReader::new(client, layout, 8)
        .with_trace(Arc::clone(&trace))
        .read_to_end(&mut all)
        .await
        .unwrap();
    assert!(all == corpus.data, "sequential read");
    assert!(
        started.elapsed() < STALL / 2,
        "took {:?}",
        started.elapsed()
    );
    // The trace blames the wait on the provider and shows the hedge that ended it.
    let summary = trace.summary();
    assert_eq!(summary.served, 12);
    assert!(summary.first_byte_ns.is_some());
    assert!(summary.hedges >= 1, "{summary:?}");
    let provider = summary.causes.latency + summary.causes.transfer + summary.causes.decode;
    assert!(provider >= summary.longest_stall_ns / 2, "{summary:?}");
}

#[tokio::test]
async fn response_waits_use_spare_capacity_without_moving_the_stall() {
    const SIZE: usize = 1_000_000;
    const PREFETCH: usize = 8;
    const READ_BYTES: usize = PREFETCH * SIZE;
    const STALL: Duration = Duration::from_secs(4);
    let corpus = Corpus::generate(1025, 16, SIZE);
    let config = PoolConfig {
        max_connections: 4,
        pipeline_depth: 4,
        idle_timeout: Duration::from_secs(5),
        min_connections: 0,
    };

    // (shared permits, alternative's priority, slow bodies instead of slow status lines)
    for (permits, priority, transfer) in
        [(32, 0, false), (8, 0, false), (32, 1, false), (32, 0, true)]
    {
        let slow = LoopbackServer::start_impaired(
            corpus.articles.clone(),
            Impairment {
                handshake_delay: Duration::from_millis(50),
                response_delay: if transfer { Duration::ZERO } else { STALL },
                bandwidth: transfer.then_some(500_000),
            },
        )
        .await
        .unwrap();
        let fast = LoopbackServer::start(corpus.articles.clone())
            .await
            .unwrap();
        let (_, layout) = setup(&corpus, &slow);
        let downloads = Downloads::new(permits);
        let mut providers = Vec::new();
        for (index, server) in [&slow, &fast].into_iter().enumerate() {
            let endpoint = Endpoint::new(
                "127.0.0.1",
                server.addr().port(),
                TlsMode::Plain,
                None,
                Duration::from_secs(5),
            )
            .unwrap();
            let pool = Pool::new(endpoint, config, Arc::clone(&downloads)).unwrap();
            if index == 1 {
                pool.prewarm(config.max_connections).await;
            }
            providers.push(Provider {
                pool,
                key: format!("p{index}"),
                name: String::new(),
                priority: if index == 0 { 0 } else { priority },
                backbone: None,
            });
        }
        let client = Arc::new(Client::new(providers));
        let trace = Arc::new(Trace::new(10_000));
        let mut reader = SegmentReader::new(client, layout, PREFETCH)
            .with_cache(Arc::new(Cache::new(true, 16_000_000)))
            .with_budget(Budget::new(64_000_000))
            .with_trace(Arc::clone(&trace));
        reader.set_end(READ_BYTES as u64);
        let mut received = vec![0; READ_BYTES];

        let first = tokio::time::timeout(
            Duration::from_millis(250),
            reader.read_exact(&mut received[..1]),
        )
        .await;
        let consumed = if transfer {
            first
                .expect("the status line and first body bytes arrive promptly")
                .unwrap();
            1
        } else {
            assert!(first.is_err(), "original status lines are still blocked");
            assert_eq!(
                fast.body_commands(),
                0,
                "the cold first pool took the whole window"
            );
            assert_eq!(downloads.available_permits(), permits - PREFETCH);
            0
        };

        let result = tokio::time::timeout(
            Duration::from_millis(1500),
            reader.read_exact(&mut received[consumed..]),
        )
        .await;
        if permits > PREFETCH && priority == 0 && !transfer {
            result
                .expect("every head is rescued before the originals reply")
                .unwrap();
            assert!(received == corpus.data[..READ_BYTES], "exact bytes");
            assert!(trace.summary().hedges > 0);
            assert!(fast.body_commands() > 0);
        } else {
            assert!(result.is_err(), "no eligible pre-response hedge");
            assert_eq!(trace.summary().hedges, 0);
            assert_eq!(fast.body_commands(), 0);
        }
    }
}

#[tokio::test]
async fn the_segment_being_waited_on_is_served_before_it_fully_arrives() {
    const SIZE: usize = 256 * 1024;
    let corpus = Corpus::generate(1025, 2, SIZE);
    // A second per article.
    let impairment = Impairment {
        bandwidth: Some(SIZE as u64),
        ..Impairment::default()
    };
    let server = LoopbackServer::start_impaired(corpus.articles.clone(), impairment)
        .await
        .unwrap();
    let (client, layout) = setup(&corpus, &server);
    let trace = Arc::new(Trace::new(10_000));
    let mut reader = SegmentReader::new(client, layout, 1).with_trace(Arc::clone(&trace));
    reader
        .seek(SeekFrom::Start(SIZE as u64 + 10))
        .await
        .unwrap();

    let started = std::time::Instant::now();
    let mut buf = vec![0; 1000];
    reader.read_exact(&mut buf).await.unwrap();
    assert!(
        started.elapsed() < Duration::from_millis(500),
        "took {:?}",
        started.elapsed()
    );
    assert_eq!(buf, corpus.data[SIZE + 10..SIZE + 1010]);
    // The wait ended with the streamed prefix, not the whole body.
    let summary = trace.summary();
    assert_eq!(summary.served, 0);
    assert!(summary.first_byte_ns.is_some(), "{summary:?}");
    assert!(summary.longest_stall_ns < 500_000_000, "{summary:?}");
}

#[tokio::test]
async fn a_prefetched_segment_streams_in_before_it_becomes_the_head() {
    const SIZE: usize = 256 * 1024;
    let corpus = Corpus::generate(1025, 3, SIZE);
    // A second per article.
    let impairment = Impairment {
        bandwidth: Some(SIZE as u64),
        ..Impairment::default()
    };
    let server = LoopbackServer::start_impaired(corpus.articles.clone(), impairment)
        .await
        .unwrap();
    let (client, layout) = setup(&corpus, &server);
    let mut reader = SegmentReader::new(client, layout, 3);
    reader.set_end(corpus.data.len() as u64 - 1);
    reader.read_exact(&mut [0; 10]).await.unwrap();
    tokio::time::sleep(Duration::from_millis(300)).await;
    reader
        .seek(SeekFrom::Start(SIZE as u64 + 10))
        .await
        .unwrap();
    let started = std::time::Instant::now();
    let mut buf = vec![0; 1000];
    reader.read_exact(&mut buf).await.unwrap();
    let waited = started.elapsed();
    drop(reader);
    assert_eq!(buf, corpus.data[SIZE + 10..SIZE + 1010]);
    // Waiting for the whole article would take about 700 ms more.
    assert!(waited < Duration::from_millis(200), "{waited:?}");
}

#[tokio::test]
async fn a_body_stalled_midway_is_hedged() {
    const STALL: Duration = Duration::from_secs(4);
    let corpus = Corpus::generate(1025, 12, ARTICLE_SIZE);
    let server = LoopbackServer::start_stalling_midway(corpus.articles.clone(), 3, STALL)
        .await
        .unwrap();
    let (client, layout) = setup(&corpus, &server);

    let started = std::time::Instant::now();
    let mut all = Vec::new();
    SegmentReader::new(client, layout, 8)
        .read_to_end(&mut all)
        .await
        .unwrap();
    assert!(all == corpus.data, "sequential read");
    assert!(
        started.elapsed() < STALL / 2,
        "took {:?}",
        started.elapsed()
    );
}

#[tokio::test]
async fn a_streamed_segment_that_fails_its_crc_is_still_reported() {
    const SIZE: usize = 256 * 1024;
    let corpus = Corpus::generate(1025, 2, SIZE);
    let mut bad = corpus.articles.clone();
    // Flip an encoded data byte on the third line: it decodes, but the CRC fails.
    let wire = &mut bad[0].wire;
    let line = wire
        .iter()
        .enumerate()
        .filter(|&(_, &b)| b == b'\n')
        .nth(2)
        .unwrap()
        .0;
    let at = line
        + 1
        + wire[line + 1..]
            .iter()
            .position(|&b| b.is_ascii_lowercase() && b != b'z')
            .unwrap();
    wire[at] += 1;
    // A second per article: the reader streams segment 0 whole from the first provider
    // and moves on while the second one, holding the same corrupt copy, is retried.
    let impairment = Impairment {
        bandwidth: Some(SIZE as u64),
        ..Impairment::default()
    };
    let a = LoopbackServer::start_impaired(bad.clone(), impairment)
        .await
        .unwrap();
    let b = LoopbackServer::start_impaired(bad, impairment)
        .await
        .unwrap();
    let (client, layout) = setup_on(&corpus, &[&a, &b]);
    let (tx, mut rx) = tokio::sync::mpsc::unbounded_channel();
    let report: Report = Arc::new(move |index, fault| {
        let _ = tx.send((index, fault));
    });
    let mut reader = SegmentReader::new(client, layout, 1).with_report(report);
    reader.read_to_end(&mut Vec::new()).await.unwrap();
    drop(reader);
    let reported = tokio::time::timeout(Duration::from_secs(3), rx.recv()).await;
    assert_eq!(reported.unwrap(), Some((0, Fault::Corrupt)));
}

fn setup(corpus: &Corpus, server: &LoopbackServer) -> (Arc<Client>, Arc<Layout>) {
    setup_on(corpus, &[server])
}

#[tokio::test]
async fn the_watch_shows_what_a_blocked_read_waits_on() {
    let corpus = Corpus::generate(1025, 12, ARTICLE_SIZE);
    // Never greets, so every fetch waits for a connection.
    let server = LoopbackServer::start_hanging(corpus.articles.clone(), 0, 0, Duration::ZERO)
        .await
        .unwrap();
    let (client, layout) = setup(&corpus, &server);
    let budget = Budget::new(64 * ARTICLE_SIZE);
    let watch = Arc::new(Watch::default());
    let mut reader = SegmentReader::new(client, layout, 8)
        .with_budget(budget.clone())
        .with_watch(Arc::clone(&watch));

    let read = tokio::time::timeout(Duration::from_secs(1), reader.read(&mut [0; 1])).await;
    assert!(read.is_err(), "the read blocks");
    let (segment, wait) = watch.head().unwrap();
    assert_eq!(segment, 0);
    let wait = wait.unwrap();
    assert_eq!((wait.stage, wait.provider), (Stage::Connection, Some(0)));
    assert!(wait.elapsed >= Duration::from_millis(500), "{wait:?}");
    assert!(watch.budget() > 0, "read-ahead holds budget");

    drop(reader);
    for _ in 0..200 {
        if watch.budget() == 0 {
            break;
        }
        tokio::time::sleep(Duration::from_millis(10)).await;
    }
    assert_eq!(watch.budget(), 0);
    assert_eq!(budget.available(), 64 * ARTICLE_SIZE);
}

#[tokio::test]
async fn a_reader_joining_a_fetch_shows_the_fetch_it_waits_on() {
    let corpus = Corpus::generate(1025, 12, ARTICLE_SIZE);
    let server = LoopbackServer::start_hanging(corpus.articles.clone(), 0, 0, Duration::ZERO)
        .await
        .unwrap();
    let (client, layout) = setup(&corpus, &server);
    let cache = Arc::new(Cache::new(true, 64 * ARTICLE_SIZE as u64));
    let reader = |watch: &Arc<Watch>| {
        SegmentReader::new(Arc::clone(&client), Arc::clone(&layout), 8)
            .with_cache(Arc::clone(&cache))
            .with_watch(Arc::clone(watch))
    };
    let (first, second) = (Arc::new(Watch::default()), Arc::new(Watch::default()));
    let (mut a, mut b) = (reader(&first), reader(&second));

    let wait = Duration::from_millis(300);
    assert!(
        tokio::time::timeout(wait, a.read(&mut [0; 1]))
            .await
            .is_err()
    );
    assert!(
        tokio::time::timeout(wait, b.read(&mut [0; 1]))
            .await
            .is_err()
    );
    let (_, Some(led)) = first.head().unwrap() else {
        panic!("the first reader has a fetch")
    };
    let (segment, Some(joined)) = second.head().unwrap() else {
        panic!("the second reader has a fetch")
    };
    assert_eq!(segment, 0);
    assert!(!led.joined && joined.joined);
    assert_eq!(joined.flight, led.flight);
    assert_eq!(
        (joined.stage, joined.provider),
        (Stage::Connection, Some(0))
    );
    assert!(
        joined.elapsed >= wait,
        "the leader's time in stage: {joined:?}"
    );
}

#[tokio::test]
async fn the_watch_counts_the_articles_each_provider_delivered() {
    let corpus = Corpus::generate(1025, 12, ARTICLE_SIZE);
    let mut partial = corpus.articles.clone();
    partial.remove(MISSING);
    let primary = LoopbackServer::start(partial).await.unwrap();
    let backup = LoopbackServer::start(corpus.articles.clone())
        .await
        .unwrap();
    let (client, layout) = setup_on(&corpus, &[&primary, &backup]);
    let watch = Arc::new(Watch::default());
    let mut reader = SegmentReader::new(client, layout, 8).with_watch(Arc::clone(&watch));
    reader.read_to_end(&mut Vec::new()).await.unwrap();

    let size = ARTICLE_SIZE as u64;
    let mut supplied: Vec<_> = watch
        .supplied()
        .iter()
        .map(|s| (s.provider, s.articles, s.bytes))
        .collect();
    supplied.sort_unstable();
    assert_eq!(supplied, [(0, 11, 11 * size), (1, 1, size)]);
}

/// [`setup`] with `servers` as providers, tried in order.
fn setup_on(corpus: &Corpus, servers: &[&LoopbackServer]) -> (Arc<Client>, Arc<Layout>) {
    let article_size = corpus.data.len() / corpus.articles.len();
    let config = PoolConfig {
        max_connections: 4,
        pipeline_depth: 4,
        idle_timeout: Duration::from_secs(5),
        min_connections: 0,
    };
    let layout = Layout::new(
        corpus
            .articles
            .iter()
            .map(|a| Segment {
                message_id: a.message_id.clone(),
                size: article_size as u64,
                begin: None,
            })
            .collect(),
    );
    let client = Client::new(servers.iter().zip(0..).map(|(server, priority)| {
        let endpoint = Endpoint::new(
            "127.0.0.1",
            server.addr().port(),
            TlsMode::Plain,
            None,
            Duration::from_secs(5),
        )
        .unwrap();
        Provider {
            pool: Pool::new(endpoint, config, Downloads::new(32)).unwrap(),
            key: format!("p{priority}"),
            name: String::new(),
            priority,
            backbone: None,
        }
    }));
    (Arc::new(client), Arc::new(layout))
}
