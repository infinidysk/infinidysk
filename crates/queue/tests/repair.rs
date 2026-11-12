use std::fmt::Write as _;
use std::sync::Arc;
use std::time::Duration;

use infinidysk_nntp::{Client, Downloads, Endpoint, Pool, PoolConfig, Provider, TlsMode};
use infinidysk_par2::Reader;
use infinidysk_queue::{
    nzb,
    repair::{Damage, Error, Limits, resolve},
};
use infinidysk_testkit::{Article, LoopbackServer, post};
use tokio_util::sync::CancellationToken;

const INDEX: &[u8] = include_bytes!("../../par2/tests/fixtures/set.par2");

fn data(length: usize) -> Vec<u8> {
    (0..length)
        .map(|i| ((i * 31 + i / 251) % 256).to_le_bytes()[0])
        .collect()
}

fn document(files: &[(&str, &[Article])]) -> nzb::Nzb {
    nzb::parse(document_xml(files).as_bytes()).expect("valid synthetic NZB")
}

fn document_xml(files: &[(&str, &[Article])]) -> String {
    let mut xml = String::from("<nzb>");
    for (name, articles) in files {
        write!(xml, "<file subject=\"{name}\"><segments>").expect("String writes cannot fail");
        for (i, article) in articles.iter().enumerate() {
            write!(
                xml,
                "<segment bytes=\"1\" number=\"{}\">{}</segment>",
                i + 1,
                article.message_id
            )
            .expect("String writes cannot fail");
        }
        xml.push_str("</segments></file>");
    }
    xml.push_str("</nzb>");
    xml
}

fn client(server: &LoopbackServer) -> Arc<Client> {
    let endpoint = Endpoint::new(
        "127.0.0.1",
        server.addr().port(),
        TlsMode::Plain,
        None,
        Duration::from_secs(3),
    )
    .expect("valid loopback endpoint");
    let pool = Pool::new(
        endpoint,
        PoolConfig {
            max_connections: 4,
            pipeline_depth: 4,
            idle_timeout: Duration::from_secs(3),
            min_connections: 0,
        },
        Downloads::new(32),
    )
    .expect("valid pool");
    Arc::new(Client::new([Provider {
        pool,
        key: String::new(),
        name: String::new(),
        priority: 0,
        backbone: None,
    }]))
}

fn limits() -> Limits {
    Limits {
        requests: 100,
        decoded_bytes: 512_000_000,
        memory_bytes: 64_000_000,
        comparisons: 1_000_000,
        retained_bytes: 1_000_000,
    }
}

#[tokio::test]
async fn healthy_verification_reads_each_posted_article_once() {
    let a = post("single-pass-a", "a.bin", &data(100_000), 7000);
    let b = post("single-pass-b", "b.bin", &data(8192), 3000);
    let expected = a.len() + b.len();
    let nzb = Arc::new(document(&[("one", &a), ("two", &b)]));
    let server = LoopbackServer::start(a.into_iter().chain(b)).await.unwrap();
    let reader = metadata();
    let result = infinidysk_queue::repair::execute(
        &client(&server),
        nzb,
        Arc::new(reader),
        infinidysk_par2::read(INDEX).set_id.unwrap(),
        limits(),
        true,
        infinidysk_queue::repair::Context::default(),
        &CancellationToken::new(),
    )
    .await
    .unwrap();
    assert_eq!(result.verified_bytes, 108_192);
    assert_eq!(
        server.body_commands(),
        expected,
        "layout discovery, slice checks and file hashing must share the same bytes"
    );
}

#[tokio::test]
async fn single_pass_verification_still_requires_the_whole_file_hash() {
    let a = post("file-hash-a", "a.bin", &data(100_000), 7000);
    let b = post("file-hash-b", "b.bin", &data(8192), 3000);
    let nzb = Arc::new(document(&[("one", &a), ("two", &b)]));
    let server = LoopbackServer::start(a.into_iter().chain(b)).await.unwrap();
    let mut index = INDEX.to_vec();
    let mut at = 0;
    while at < index.len() {
        let length = usize::try_from(u64::from_le_bytes(
            index[at + 8..at + 16].try_into().unwrap(),
        ))
        .unwrap();
        if &index[at + 48..at + 64] == b"PAR 2.0\0FileDesc" {
            index[at + 80] ^= 1;
            let hash = infinidysk_par2::hash_16k(&index[at + 32..at + length]);
            index[at + 16..at + 32].copy_from_slice(&hash);
            break;
        }
        at += length;
    }
    let mut reader = Reader::default();
    reader.push(&index);
    assert!(matches!(
        infinidysk_queue::repair::execute(
            &client(&server),
            nzb,
            Arc::new(reader),
            infinidysk_par2::read(INDEX).set_id.unwrap(),
            limits(),
            true,
            infinidysk_queue::repair::Context::default(),
            &CancellationToken::new()
        )
        .await,
        Err(Error::Content(infinidysk_par2::Error::FileChecksum))
    ));
}

#[tokio::test]
async fn source_reads_overlap_a_stalled_first_article_and_cancel_cleanly() {
    let a = post("parallel-a", "a.bin", &data(100_000), 7000);
    let b = post("parallel-b", "b.bin", &data(8192), 3000);
    let nzb = document(&[("one", &a), ("two", &b)]);
    let server = LoopbackServer::start_stalling(a.into_iter().chain(b), 0, Duration::from_secs(3))
        .await
        .unwrap();
    let client = client(&server);
    let parent = CancellationToken::new();
    let running = tokio::spawn({
        let client = Arc::clone(&client);
        let cancel = parent.clone();
        async move {
            let reader = metadata();
            let set = reader
                .repair_set(infinidysk_par2::read(INDEX).set_id.unwrap())
                .unwrap();
            resolve(
                &client,
                &nzb,
                &set,
                Limits {
                    memory_bytes: 256_000_000,
                    ..limits()
                },
                &cancel,
            )
            .await
        }
    });
    let overlap = tokio::time::timeout(Duration::from_millis(500), async {
        while server.body_commands() < 2 {
            tokio::time::sleep(Duration::from_millis(5)).await;
        }
    })
    .await;
    parent.cancel();
    assert!(matches!(running.await.unwrap(), Err(Error::Cancelled)));
    assert!(
        overlap.is_ok(),
        "another connection should fetch while the first article waits"
    );
    tokio::time::timeout(Duration::from_secs(4), async {
        while client.free_permits() != Some(32) {
            tokio::time::sleep(Duration::from_millis(5)).await;
        }
    })
    .await
    .unwrap();
}

#[tokio::test]
async fn resolves_obfuscated_whole_sources_without_using_subject_names() {
    let a = post("a", "obfuscated", &data(100_000), 7000);
    let b = post("b", "obfuscated", &data(8192), 3000);
    let nzb = document(&[("b.bin", &a), ("a.bin", &b)]);
    let server = LoopbackServer::start(a.into_iter().chain(b)).await.unwrap();
    let mut reader = Reader::default();
    reader.push(INDEX);
    let set = reader
        .repair_set(infinidysk_par2::read(INDEX).set_id.unwrap())
        .unwrap();
    let cancel = CancellationToken::new();
    let resolved = resolve(&client(&server), &nzb, &set, limits(), &cancel)
        .await
        .unwrap();
    assert_eq!(resolved.len(), 2);
    for layout in &resolved {
        let expected = &set.sources()[layout.source()].file;
        assert_eq!(layout.set_id(), set.id());
        assert_eq!(layout.file_id(), expected.id);
        let articles = layout.articles();
        assert_eq!(articles.first().unwrap().range.start, 0);
        assert_eq!(articles.last().unwrap().range.end, expected.length);
        assert_eq!(nzb.files[layout.file()].segments.len(), articles.len());
        assert!(
            articles
                .iter()
                .all(|article| article.id.is_some() && article.damage.is_none())
        );
    }
    assert!(!cancel.is_cancelled());
}

fn metadata() -> Reader {
    let mut reader = Reader::default();
    reader.push(INDEX);
    reader.push(include_bytes!("../../par2/tests/fixtures/set.vol0+3.par2"));
    reader
}

#[tokio::test]
async fn discovery_uses_the_index_before_downloading_recovery_volumes() {
    let index = post("index", "set.par2", INDEX, 7000);
    let volume = post(
        "volume",
        "set.vol0+3.par2",
        include_bytes!("../../par2/tests/fixtures/set.vol0+3.par2"),
        2000,
    );
    let nzb = document(&[("set.vol0+3.par2", &volume), ("set.par2", &index)]);
    let server = LoopbackServer::start(index.into_iter().chain(volume))
        .await
        .unwrap();
    let (reader, _, _) = infinidysk_queue::repair::discover(
        &client(&server),
        &nzb,
        limits(),
        &CancellationToken::new(),
    )
    .await
    .unwrap();
    assert_eq!(server.body_commands(), 1);
    assert!(
        reader
            .repair_set(reader.sets()[0])
            .unwrap()
            .recovery()
            .is_empty()
    );
}

#[tokio::test]
async fn verification_can_use_whole_file_hashes_without_slice_packets() {
    let a = post("a", "obfuscated", &data(100_000), 7000);
    let b = post("b", "obfuscated", &data(8192), 3000);
    let nzb = document(&[("one", &a), ("two", &b)]);
    let expected_reads = a.len() + b.len();
    let server = LoopbackServer::start(a.into_iter().chain(b)).await.unwrap();
    let mut reader = Reader::default();
    let mut packets = INDEX;
    while !packets.is_empty() {
        let len = usize::try_from(u64::from_le_bytes(packets[8..16].try_into().unwrap())).unwrap();
        if &packets[48..64] != b"PAR 2.0\0IFSC\0\0\0\0" {
            reader.push(&packets[..len]);
        }
        packets = &packets[len..];
    }
    let set = reader.sets()[0];
    let result = infinidysk_queue::repair::execute(
        &client(&server),
        Arc::new(nzb),
        Arc::new(reader),
        set,
        limits(),
        true,
        infinidysk_queue::repair::Context::default(),
        &CancellationToken::new(),
    )
    .await
    .unwrap();
    assert_eq!(result.verified_bytes, 108_192);
    assert_eq!(server.body_commands(), expected_reads);
    assert_eq!(
        result.patches,
        [] as [(infinidysk_core::repair::Patch, Vec<u8>); 0]
    );
    assert!(
        result
            .sources
            .iter()
            .all(|source| source.proof.slice_md5.is_empty())
    );
}

#[tokio::test]
async fn bounded_execution_verifies_whole_sources_before_returning_replacement_articles() {
    let a = post("a", "obfuscated", &data(100_000), 7000);
    let b = post("b", "obfuscated", &data(8192), 3000);
    let nzb = document(&[("one", &a), ("two", &b)]);
    let server = LoopbackServer::start(
        a.into_iter()
            .enumerate()
            .filter_map(|(i, article)| (i != 3).then_some(article))
            .chain(b),
    )
    .await
    .unwrap();
    let reader = metadata();
    let set_id = infinidysk_par2::read(INDEX).set_id.unwrap();
    let decoded_bytes = infinidysk_nntp::MAX_DECODED_LEN as u64 + 1_000_000;
    let progress = Arc::new(infinidysk_queue::repair::Progress::default());
    let result = infinidysk_queue::repair::execute(
        &client(&server),
        Arc::new(nzb),
        Arc::new(reader),
        set_id,
        Limits {
            decoded_bytes,
            ..limits()
        },
        false,
        infinidysk_queue::repair::Context {
            progress: Some(Arc::clone(&progress)),
            ..infinidysk_queue::repair::Context::default()
        },
        &CancellationToken::new(),
    )
    .await
    .unwrap();
    assert_eq!(result.patches.len(), 1);
    let (completed, missing) = progress.requests();
    assert!(completed > missing && missing > 0);
    assert_eq!(result.patches[0].0.source.range.start, 21_000);
    assert_eq!(result.patches[0].1, data(100_000)[21_000..28_000]);
    assert!(result.transferred_bytes > 108_192);
    assert_eq!(
        result.remaining.decoded_bytes + result.transferred_bytes,
        decoded_bytes,
        "clean missing replies carry no decoded article bytes"
    );
    assert_eq!(result.verified_bytes, 108_192);
    let source = &result.sources[0];
    let mut target = infinidysk_core::FileMeta::Nzb(infinidysk_core::NzbFile {
        segments: source.segments.clone(),
        proof: None,
    });
    let mut covered = Vec::new();
    infinidysk_queue::repair::refresh_metadata(&mut target, &result.sources, &mut covered);
    assert_eq!(covered, [true]);
    if let infinidysk_core::FileMeta::Nzb(file) = &mut target {
        file.segments.items[0].id = "unrelated@loopback".to_owned();
    }
    covered.clear();
    infinidysk_queue::repair::refresh_metadata(&mut target, &result.sources, &mut covered);
    assert_eq!(covered, [false]);
}

#[tokio::test]
async fn missing_reads_still_obey_the_request_limit_without_spending_payload_bytes() {
    let a = post("absent-a", "a.bin", &data(100_000), 7000);
    let b = post("absent-b", "b.bin", &data(8192), 3000);
    let nzb = document(&[("one", &a), ("two", &b)]);
    let server = LoopbackServer::start([]).await.unwrap();
    let reader = metadata();
    let set = reader.repair_set(reader.sets()[0]).unwrap();
    let result = resolve(
        &client(&server),
        &nzb,
        &set,
        Limits {
            requests: 2,
            decoded_bytes: infinidysk_nntp::MAX_DECODED_LEN as u64 + 1_000_000,
            ..limits()
        },
        &CancellationToken::new(),
    )
    .await;
    assert!(matches!(result, Err(Error::Limit("request"))), "{result:?}");
    assert_eq!(server.body_commands(), 2);
}

#[tokio::test]
async fn corrupt_reads_keep_the_conservative_payload_charge() {
    let mut a = post("corrupt-budget-a", "a.bin", &data(100_000), 7000);
    let b = post("corrupt-budget-b", "b.bin", &data(8192), 3000);
    a[0].wire = b"invalid body\r\n.\r\n".to_vec();
    let nzb = document(&[("one", &a), ("two", &b)]);
    let server = LoopbackServer::start(a.into_iter().chain(b)).await.unwrap();
    let reader = metadata();
    let set = reader.repair_set(reader.sets()[0]).unwrap();
    let result = resolve(
        &client(&server),
        &nzb,
        &set,
        Limits {
            decoded_bytes: infinidysk_nntp::MAX_DECODED_LEN as u64 + 1_000_000,
            ..limits()
        },
        &CancellationToken::new(),
    )
    .await;
    assert!(
        matches!(result, Err(Error::Limit("decoded byte"))),
        "{result:?}"
    );
}

#[tokio::test]
async fn missing_reads_report_progress_while_another_reply_waits() {
    let a = post("progress-a", "a.bin", &data(100_000), 7000);
    let b = post("progress-b", "b.bin", &data(8192), 3000);
    let nzb = Arc::new(document(&[("one", &a), ("two", &b)]));
    let first = a[0].message_id.clone();
    let server = LoopbackServer::start([a[0].clone()]).await.unwrap();
    server.stall_article(&first, 1, Duration::from_secs(1));
    let reader = Arc::new(metadata());
    let set = reader.sets()[0];
    let progress = Arc::new(infinidysk_queue::repair::Progress::default());
    let cancel = CancellationToken::new();
    let task = tokio::spawn({
        let client = client(&server);
        let progress = Arc::clone(&progress);
        let cancel = cancel.clone();
        async move {
            infinidysk_queue::repair::execute(
                &client,
                nzb,
                reader,
                set,
                Limits {
                    memory_bytes: 256_000_000,
                    ..limits()
                },
                false,
                infinidysk_queue::repair::Context {
                    progress: Some(progress),
                    ..infinidysk_queue::repair::Context::default()
                },
                &cancel,
            )
            .await
        }
    });
    let observed = tokio::time::timeout(Duration::from_secs(5), async {
        while progress.requests().1 == 0 {
            tokio::time::sleep(Duration::from_millis(5)).await;
        }
    })
    .await;
    cancel.cancel();
    assert!(matches!(task.await.unwrap(), Err(Error::Cancelled)));
    assert!(
        observed.is_ok(),
        "missing results must be visible before the batch finishes"
    );
    let (completed, missing) = progress.requests();
    assert!(missing > 0 && completed >= missing);
}

#[tokio::test]
async fn legacy_candidates_become_patches_only_after_complete_par2_verification() {
    let a = post("a", "obfuscated", &data(100_000), 7000);
    let b = post("b", "obfuscated", &data(8192), 3000);
    let id = a[3].message_id.clone();
    let nzb = document(&[("one", &a), ("two", &b)]);
    let server = LoopbackServer::start(
        a.into_iter()
            .enumerate()
            .filter_map(|(i, article)| (i != 3).then_some(article))
            .chain(b),
    )
    .await
    .unwrap();
    let candidates: Arc<infinidysk_nntp::RepairLookup> = Arc::new(Box::new(move |key| {
        let infinidysk_nntp::RepairKey::Article(found) = key else {
            return None;
        };
        if found != id {
            return None;
        }
        Some(Box::pin(async {
            Ok(Some(infinidysk_nntp::Body {
                data: data(100_000)[21_000..28_000].to_vec().into(),
                offset: 21_000,
                file_size: 100_000,
                part: Some(4),
                total: Some(15),
            }))
        }))
    }));
    let result = infinidysk_queue::repair::execute(
        &client(&server),
        Arc::new(nzb),
        Arc::new(metadata()),
        infinidysk_par2::read(INDEX).set_id.unwrap(),
        limits(),
        false,
        infinidysk_queue::repair::Context {
            source: None,
            candidates: Some(candidates),
            probe: None,
            progress: None,
        },
        &CancellationToken::new(),
    )
    .await
    .unwrap();
    assert_eq!(result.patches.len(), 1);
    assert_eq!(result.patches[0].1, data(100_000)[21_000..28_000]);
}

#[tokio::test]
async fn missing_and_omitted_parts_keep_exact_source_ranges() {
    for omitted in [false, true] {
        let a = post("a", "obfuscated", &data(100_000), 7000);
        let b = post("b", "obfuscated", &data(8192), 3000);
        let mut nzb = document(&[("obfuscated", &a), ("obfuscated", &b)]);
        if omitted {
            nzb.files[0].segments.remove(3);
            nzb.files[0].gap = true;
        }
        let server = LoopbackServer::start(
            a.into_iter()
                .enumerate()
                .filter_map(|(i, article)| (i != 3).then_some(article))
                .chain(b),
        )
        .await
        .unwrap();
        let reader = metadata();
        let set = reader
            .repair_set(infinidysk_par2::read(INDEX).set_id.unwrap())
            .unwrap();
        let resolved = resolve(
            &client(&server),
            &nzb,
            &set,
            limits(),
            &CancellationToken::new(),
        )
        .await
        .unwrap();
        let layout = resolved.iter().find(|layout| layout.file() == 0).unwrap();
        let article = &layout.articles()[3];
        assert_eq!(article.range, 21_000..28_000);
        assert_eq!(article.id.is_none(), omitted);
        assert_eq!(
            article.damage,
            Some(if omitted {
                Damage::Omitted
            } else {
                Damage::Missing
            })
        );
    }
}

#[tokio::test]
async fn later_slice_proofs_identify_a_source_when_its_prefix_is_unavailable_or_corrupt() {
    for corrupt in [false, true] {
        let mut a = post("a", "obfuscated", &data(100_000), 7000);
        let b = post("b", "obfuscated", &data(8192), 3000);
        let nzb = document(&[("obfuscated", &a), ("obfuscated", &b)]);
        if corrupt {
            let at = a[0].wire.windows(7).position(|w| w == b"pcrc32=").unwrap();
            a[0].wire[at + 7..at + 15].copy_from_slice(b"00000000");
        } else {
            a.remove(0);
        }
        let server = LoopbackServer::start(a.into_iter().chain(b)).await.unwrap();
        let reader = metadata();
        let set = reader
            .repair_set(infinidysk_par2::read(INDEX).set_id.unwrap())
            .unwrap();
        let resolved = resolve(
            &client(&server),
            &nzb,
            &set,
            limits(),
            &CancellationToken::new(),
        )
        .await
        .unwrap();
        let layout = resolved.iter().find(|layout| layout.file() == 0).unwrap();
        assert_eq!(layout.articles()[0].range, 0..7000);
        assert_eq!(
            layout.articles()[0].damage,
            Some(if corrupt {
                Damage::Corrupt
            } else {
                Damage::Missing
            })
        );
    }
}

#[tokio::test]
async fn a_valid_yenc_crc_does_not_make_a_different_file_a_source_match() {
    let a = post("a", "a.bin", &vec![7; 100_000], 7000);
    let b = post("b", "b.bin", &data(8192), 3000);
    let nzb = document(&[("a.bin", &a), ("b.bin", &b)]);
    let server = LoopbackServer::start(a.into_iter().chain(b)).await.unwrap();
    let reader = metadata();
    let set = reader
        .repair_set(infinidysk_par2::read(INDEX).set_id.unwrap())
        .unwrap();
    assert!(matches!(
        resolve(
            &client(&server),
            &nzb,
            &set,
            limits(),
            &CancellationToken::new()
        )
        .await,
        Err(Error::Unavailable(_))
    ));
}

#[tokio::test]
async fn adjacent_missing_parts_do_not_get_guessed_boundaries() {
    let a = post("a", "obfuscated", &data(100_000), 7000);
    let b = post("b", "obfuscated", &data(8192), 3000);
    let nzb = document(&[("obfuscated", &a), ("obfuscated", &b)]);
    let server = LoopbackServer::start(a.into_iter().skip(2).chain(b))
        .await
        .unwrap();
    let reader = metadata();
    let set = reader
        .repair_set(infinidysk_par2::read(INDEX).set_id.unwrap())
        .unwrap();
    assert!(matches!(
        resolve(
            &client(&server),
            &nzb,
            &set,
            limits(),
            &CancellationToken::new()
        )
        .await,
        Err(Error::Unavailable(_))
    ));
}

#[tokio::test]
async fn duplicate_article_ownership_is_rejected_before_fetching() {
    let a = post("a", "obfuscated", &data(100_000), 7000);
    let nzb = document(&[("one", &a), ("two", &a)]);
    let server = LoopbackServer::start(a).await.unwrap();
    let reader = metadata();
    let set = reader
        .repair_set(infinidysk_par2::read(INDEX).set_id.unwrap())
        .unwrap();
    assert!(matches!(
        resolve(
            &client(&server),
            &nzb,
            &set,
            limits(),
            &CancellationToken::new()
        )
        .await,
        Err(Error::Ambiguous(_))
    ));
    assert_eq!(server.body_commands(), 0);
}

#[tokio::test]
async fn distinct_postings_of_the_same_content_are_ambiguous() {
    let a = post("a", "obfuscated", &data(100_000), 7000);
    let copy = post("copy", "obfuscated", &data(100_000), 7000);
    let b = post("b", "obfuscated", &data(8192), 3000);
    let nzb = document(&[("one", &a), ("two", &copy), ("other", &b)]);
    let server = LoopbackServer::start(a.into_iter().chain(copy).chain(b))
        .await
        .unwrap();
    let reader = metadata();
    let set = reader
        .repair_set(infinidysk_par2::read(INDEX).set_id.unwrap())
        .unwrap();
    assert!(matches!(
        resolve(
            &client(&server),
            &nzb,
            &set,
            limits(),
            &CancellationToken::new()
        )
        .await,
        Err(Error::Ambiguous(_))
    ));
}

#[tokio::test]
async fn source_resolution_honors_all_work_limits() {
    let a = post("a", "obfuscated", &data(100_000), 7000);
    let b = post("b", "obfuscated", &data(8192), 3000);
    let nzb = document(&[("one", &a), ("two", &b)]);
    let server = LoopbackServer::start(a.into_iter().chain(b)).await.unwrap();
    let reader = metadata();
    let set = reader
        .repair_set(infinidysk_par2::read(INDEX).set_id.unwrap())
        .unwrap();
    for limit in [
        Limits {
            requests: 0,
            ..limits()
        },
        Limits {
            decoded_bytes: 0,
            ..limits()
        },
        Limits {
            memory_bytes: 0,
            ..limits()
        },
        Limits {
            comparisons: 0,
            ..limits()
        },
    ] {
        assert!(matches!(
            resolve(
                &client(&server),
                &nzb,
                &set,
                limit,
                &CancellationToken::new()
            )
            .await,
            Err(Error::Limit(_))
        ));
    }
}

#[tokio::test]
async fn provider_failures_are_not_misreported_as_missing_content() {
    let a = post("a", "obfuscated", &data(100_000), 7000);
    let nzb = document(&[("one", &a)]);
    let server = LoopbackServer::start_limited(a, 0).await.unwrap();
    let reader = metadata();
    let set = reader
        .repair_set(infinidysk_par2::read(INDEX).set_id.unwrap())
        .unwrap();
    assert!(matches!(
        resolve(
            &client(&server),
            &nzb,
            &set,
            limits(),
            &CancellationToken::new()
        )
        .await,
        Err(Error::Source(_))
    ));
}

#[tokio::test]
async fn resolved_article_spans_reconstruct_missing_bytes_from_real_parity() {
    use infinidysk_core::{
        BlobId, ByteRange, JobId, PatchId, blob,
        repair::{Patch, Source as Posted},
    };
    use infinidysk_db::{Db, QueueEntry, repair::Owner};
    use infinidysk_nntp::patch::Store;
    use sha2::{Digest as _, Sha256};

    let a = post("a", "obfuscated", &data(100_000), 7000);
    let b = post("b", "obfuscated", &data(8192), 3000);
    let xml = document_xml(&[("one", &a), ("two", &b)]);
    let nzb = nzb::parse(xml.as_bytes()).unwrap();
    let root = std::env::temp_dir().join(format!("infinidysk-published-{}", JobId::new()));
    std::fs::create_dir(&root).unwrap();
    let url = format!("sqlite:{}", root.join("data.sqlite").display());
    let db = Db::connect(&url).await.unwrap();
    let job = QueueEntry {
        id: JobId::new(),
        created_at: 1,
        file_name: "input.nzb".into(),
        job_name: "input".into(),
        category: String::new(),
        priority: 0,
        post_processing: 3,
        nzb_blob: BlobId::new(),
        nzb_size: xml.len() as u64,
        total_bytes: 108_192,
        paused: false,
        retry_at: None,
        attempts: 0,
        stuck_count: 0,
        last_error: None,
        content_hash: None,
    };
    db.enqueue(&job, &blob::encode_nzb(xml.as_bytes()).unwrap())
        .await
        .unwrap();
    let original = db
        .repair_source(Owner::Queue(job.id))
        .await
        .unwrap()
        .unwrap();
    let server = LoopbackServer::start(
        a.into_iter()
            .enumerate()
            .filter_map(|(i, article)| (i != 3).then_some(article))
            .chain(b),
    )
    .await
    .unwrap();
    let reader = metadata();
    let set = reader
        .repair_set(infinidysk_par2::read(INDEX).set_id.unwrap())
        .unwrap();
    let resolved = resolve(
        &client(&server),
        &nzb,
        &set,
        limits(),
        &CancellationToken::new(),
    )
    .await
    .unwrap();
    let mut missing = Vec::new();
    let mut available = Vec::new();
    for index in 0..set.inputs().len() {
        let (source, _) = set.source(index).unwrap();
        let layout = resolved
            .iter()
            .find(|layout| layout.source() == source)
            .unwrap();
        let source_bytes = data(usize::try_from(set.sources()[source].file.length).unwrap());
        let mut bytes = Vec::new();
        let mut lost = false;
        for (article, range) in layout.spans(&set, index).unwrap() {
            let article = &layout.articles()[article];
            lost |= article.damage.is_some();
            let offset = usize::try_from(article.range.start).unwrap();
            bytes.extend_from_slice(&source_bytes[offset + range.start..offset + range.end]);
        }
        if lost {
            missing.push(index);
        } else {
            available.push((index, bytes));
        }
    }
    assert_eq!(missing.len(), 2);
    let mut repair = infinidysk_par2::Repair::new(
        set.slice_size(),
        set.inputs(),
        &missing,
        set.recovery(),
        2_000_000,
        || false,
    )
    .unwrap();
    for (index, bytes) in available {
        repair.feed(index, &bytes, || false).unwrap();
    }
    let recovered = repair.finish(|| false).unwrap();
    for recovered in &recovered {
        let (_, range) = set.source(recovered.index).unwrap();
        assert_eq!(
            recovered.data,
            data(usize::try_from(range.end).unwrap())[usize::try_from(range.start).unwrap()..]
        );
    }
    let layout = resolved.iter().find(|layout| layout.file() == 0).unwrap();
    let article = &layout.articles()[3];
    let mut payload = Vec::new();
    for recovered in &recovered {
        let (_, range) = set.source(recovered.index).unwrap();
        let start = article.range.start.max(range.start);
        let end = article.range.end.min(range.end);
        if start < end {
            payload.extend_from_slice(
                &recovered.data[usize::try_from(start - range.start).unwrap()
                    ..usize::try_from(end - range.start).unwrap()],
            );
        }
    }
    assert_eq!(payload.len(), 7000);
    let protected = &set.sources()[layout.source()].file;
    let mut repaired = data(100_000);
    repaired[21_000..28_000].copy_from_slice(&payload);
    let mut verifier = infinidysk_par2::Verifier::new(
        protected.length,
        protected.hash,
        set.slice_size() as u64,
        &protected.slices,
    )
    .unwrap();
    verifier.push(&repaired, || false).unwrap();
    verifier.finish(|| false).unwrap();
    let patch = Patch {
        id: PatchId::new(),
        source: Posted {
            set: layout.set_id(),
            file: layout.file_id(),
            range: ByteRange {
                start: article.range.start,
                end: article.range.end,
            },
            file_length: protected.length,
            message_id: article.id.clone(),
            part: Some(4),
            total: Some(15),
        },
        sha256: Sha256::digest(&payload).into(),
    };
    let store = Store::open(&root.join("repairs"), 1_000_000).unwrap();
    let durable = store.write(&patch, &payload, || false).unwrap();
    let id = article.id.as_deref().unwrap();
    assert!(
        db.article_patch(&original, id).await.unwrap().is_none(),
        "durable file alone is invisible"
    );
    assert!(
        db.publish_patches(&original, std::slice::from_ref(durable.patch()))
            .await
            .unwrap()
    );
    drop(durable);
    drop(store);
    db.close().await;
    let db = Db::connect(&url).await.unwrap();
    let store = Store::open(&root.join("repairs"), 1_000_000).unwrap();
    let committed = db.article_patch(&original, id).await.unwrap().unwrap();
    assert_eq!(store.read(&committed).unwrap().data.as_ref(), payload);
    assert!(store.used_bytes().unwrap() < 10_000);
    drop(store);
    db.close().await;
    std::fs::remove_dir_all(root).unwrap();
}

async fn wait_for_body(server: &LoopbackServer) {
    tokio::time::timeout(Duration::from_secs(5), async {
        while server.body_commands() == 0 {
            tokio::task::yield_now().await;
        }
    })
    .await
    .expect("source read started");
}

#[tokio::test]
async fn cancellation_stops_a_pending_source_read() {
    let a = post("a", "obfuscated", &data(100_000), 7000);
    let nzb = document(&[("one", &a)]);
    let server = LoopbackServer::start_stalling(a, 0, Duration::from_secs(30))
        .await
        .unwrap();
    let client = client(&server);
    let cancel = CancellationToken::new();
    let stop = cancel.clone();
    let running = tokio::spawn(async move {
        let reader = metadata();
        let set = reader
            .repair_set(infinidysk_par2::read(INDEX).set_id.unwrap())
            .unwrap();
        resolve(&client, &nzb, &set, limits(), &stop).await
    });
    wait_for_body(&server).await;
    cancel.cancel();
    assert!(matches!(
        tokio::time::timeout(Duration::from_secs(2), running)
            .await
            .unwrap()
            .unwrap(),
        Err(Error::Cancelled)
    ));
}

#[tokio::test]
async fn aborting_a_caller_stops_its_worker_without_cancelling_the_parent() {
    let a = post("a", "obfuscated", &data(100_000), 7000);
    let nzb = document(&[("one", &a)]);
    let server = LoopbackServer::start_stalling(a, 0, Duration::from_secs(30))
        .await
        .unwrap();
    let client = client(&server);
    let cancel = CancellationToken::new();
    let stop = cancel.clone();
    let running = {
        let client = Arc::clone(&client);
        tokio::spawn(async move {
            let reader = metadata();
            let set = reader
                .repair_set(infinidysk_par2::read(INDEX).set_id.unwrap())
                .unwrap();
            resolve(&client, &nzb, &set, limits(), &stop).await
        })
    };
    wait_for_body(&server).await;
    running.abort();
    assert!(running.await.unwrap_err().is_cancelled());
    assert!(!cancel.is_cancelled());
    let reader = metadata();
    let set = reader
        .repair_set(infinidysk_par2::read(INDEX).set_id.unwrap())
        .unwrap();
    // A source-less call proves admission reopened without waiting for transport draining.
    let result = tokio::time::timeout(
        Duration::from_secs(3),
        resolve(&client, &nzb::Nzb::default(), &set, limits(), &cancel),
    )
    .await
    .unwrap();
    assert!(matches!(result, Err(Error::Unavailable(_))));
    assert!(!cancel.is_cancelled());
}

fn replace_wire(article: &mut Article, from: &[u8], to: &[u8]) {
    let start = article
        .wire
        .windows(from.len())
        .position(|part| part == from)
        .expect("synthetic article has the header field");
    article
        .wire
        .splice(start..start + from.len(), to.iter().copied());
}

#[tokio::test]
async fn contradictory_yenc_evidence_does_not_create_a_layout() {
    for change in 0..4 {
        let mut a = post("a", "obfuscated", &data(100_000), 7000);
        let b = post("b", "obfuscated", &data(8192), 3000);
        let nzb = document(&[("one", &a), ("two", &b)]);
        match change {
            0 => replace_wire(&mut a[1], b"size=100000 name=", b"size=100001 name="),
            1 => replace_wire(&mut a[1], b"part=2 total=", b"part=3 total="),
            2 => replace_wire(&mut a[1], b"begin=7001 end=14000", b"begin=7002 end=14001"),
            _ => replace_wire(&mut a[0], b"total=15 ", b"total=4294967295 "),
        }
        let server = LoopbackServer::start(a.into_iter().chain(b)).await.unwrap();
        let reader = metadata();
        let set = reader
            .repair_set(infinidysk_par2::read(INDEX).set_id.unwrap())
            .unwrap();
        assert!(matches!(
            resolve(
                &client(&server),
                &nzb,
                &set,
                limits(),
                &CancellationToken::new()
            )
            .await,
            Err(Error::Unavailable(_) | Error::Limit(_))
        ));
    }
}

#[tokio::test]
async fn extreme_nzb_part_numbers_hit_metadata_limits_before_allocating_or_fetching() {
    let a = post("a", "obfuscated", &data(100_000), 7000);
    let mut nzb = document(&[("one", &a)]);
    nzb.files[0].segments[0].number = i64::MAX;
    let server = LoopbackServer::start(a).await.unwrap();
    let reader = metadata();
    let set = reader
        .repair_set(infinidysk_par2::read(INDEX).set_id.unwrap())
        .unwrap();
    assert!(matches!(
        resolve(
            &client(&server),
            &nzb,
            &set,
            limits(),
            &CancellationToken::new()
        )
        .await,
        Err(Error::Limit(_))
    ));
    assert_eq!(server.body_commands(), 0);
}

#[tokio::test]
async fn archive_sources_include_complete_posted_volumes_and_ciphertext_without_warming_cache() {
    // Independent index: par2 create -s512 -c1 -n1 sources.par2 over these unchanged fixtures.
    let volumes: [&[u8]; 3] = [
        include_bytes!("../../archive/tests/fixtures/Rar5.none.rar"),
        include_bytes!("../../archive/tests/fixtures/aes-5hp.rar"),
        include_bytes!("../../archive/tests/fixtures/7Zip.Copy.Aes.7z"),
    ];
    let posted: Vec<_> = volumes
        .iter()
        .enumerate()
        .map(|(i, bytes)| post(&format!("archive-{i}"), "obfuscated", bytes, 7000))
        .collect();
    let nzb = document(&[
        ("one", &posted[0]),
        ("two", &posted[1]),
        ("three", &posted[2]),
    ]);
    let server = LoopbackServer::start(posted.into_iter().flatten())
        .await
        .unwrap();
    let client = client(&server);
    let warmed = Arc::new(std::sync::atomic::AtomicUsize::new(0));
    let inserts = Arc::clone(&warmed);
    client.set_sink(Box::new(move |_, workload, _, drained| {
        if workload == infinidysk_nntp::Workload::Queue
            || (drained && workload == infinidysk_nntp::Workload::Stream)
        {
            inserts.fetch_add(1, std::sync::atomic::Ordering::Relaxed);
        }
    }));
    let index = include_bytes!("fixtures/source-volumes.par2");
    let mut reader = Reader::default();
    reader.push(index);
    let set = reader
        .repair_set(infinidysk_par2::read(index).set_id.unwrap())
        .unwrap();
    let resolved = resolve(&client, &nzb, &set, limits(), &CancellationToken::new())
        .await
        .unwrap();
    assert_eq!(resolved.len(), 3);
    for layout in resolved {
        let volume = volumes[layout.file()];
        assert_eq!(layout.articles().first().unwrap().range.start, 0);
        assert_eq!(
            layout.articles().last().unwrap().range.end,
            volume.len() as u64
        );
        for index in set.sources()[layout.source()].slices.clone() {
            let mut bytes = Vec::new();
            for (article, range) in layout.spans(&set, index).unwrap() {
                let offset = usize::try_from(layout.articles()[article].range.start).unwrap();
                bytes.extend_from_slice(&volume[offset + range.start..offset + range.end]);
            }
            assert!(
                set.inputs()[index]
                    .checksum
                    .matches(set.slice_size() as u64, &bytes)
            );
        }
    }
    let verified = infinidysk_queue::repair::execute(
        &client,
        Arc::new(nzb),
        Arc::new(reader),
        infinidysk_par2::read(index).set_id.unwrap(),
        limits(),
        true,
        infinidysk_queue::repair::Context::default(),
        &CancellationToken::new(),
    )
    .await
    .unwrap();
    assert_eq!(
        verified.verified_bytes,
        volumes
            .iter()
            .map(|volume| volume.len() as u64)
            .sum::<u64>()
    );
    assert_eq!(warmed.load(std::sync::atomic::Ordering::Relaxed), 0);
}

#[tokio::test]
async fn rejected_candidates_release_temporary_layout_memory() {
    let mut posted: Vec<_> = (0..8)
        .map(|i| post(&format!("noise-{i}"), "noise", &[7; 32], 32))
        .collect();
    posted.push(post("a", "obfuscated", &data(100_000), 7000));
    posted.push(post("b", "obfuscated", &data(8192), 3000));
    let entries: Vec<_> = posted
        .iter()
        .map(|articles| ("obfuscated", articles.as_slice()))
        .collect();
    let mut nzb = document(&entries);
    for file in &mut nzb.files[..8] {
        file.segments[0].number = 25_000;
    }
    let server = LoopbackServer::start(posted.into_iter().flatten())
        .await
        .unwrap();
    let reader = metadata();
    let set = reader
        .repair_set(infinidysk_par2::read(INDEX).set_id.unwrap())
        .unwrap();
    let limit = Limits {
        memory_bytes: 40_000_000,
        ..limits()
    };
    let resolved = resolve(
        &client(&server),
        &nzb,
        &set,
        limit,
        &CancellationToken::new(),
    )
    .await
    .unwrap();
    assert_eq!(resolved.len(), 2);
}
