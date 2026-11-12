//! Queue → import → `/content` against a loopback NNTP server.
#![allow(clippy::unwrap_used)] // Test helpers outside `#[test]` fns; panics are the assertions.

use std::fmt::Write as _;
use std::pin::Pin;
use std::sync::Arc;
use std::sync::atomic::{AtomicBool, Ordering};
use std::time::Duration;

use infinidysk_core::{FileMeta, ItemId, blob, media};
use infinidysk_db::{Db, Handoff, JobStatus};
use infinidysk_nntp::{Client, Downloads, Endpoint, Pool, PoolConfig, Provider, TlsMode};
use infinidysk_queue::import::{self, Inspection};
use infinidysk_queue::{HandoffJob, HandoffWriter, Queue, Submission, Written};
use infinidysk_testkit::{Article, LoopbackServer, post};
use infinidysk_yenc::crc32;

fn fixture(path: &str) -> Vec<u8> {
    std::fs::read(format!("{}/../{path}", env!("CARGO_MANIFEST_DIR"))).expect("fixture exists")
}

/// `len` bytes of `fill` behind a Matroska signature.
fn mkv(len: usize, fill: u8) -> Vec<u8> {
    let mut data = vec![fill; len];
    data[..4].copy_from_slice(&[0x1A, 0x45, 0xDF, 0xA3]);
    data
}

/// A 7z archive storing `files` uncompressed, in order, in one folder.
fn sevenzip(files: &[(&str, &[u8])]) -> Vec<u8> {
    fn num(out: &mut Vec<u8>, v: usize) {
        let extra = (0..7).find(|&i| v < 1 << (7 * (i + 1))).unwrap();
        out.push((0xFF00u16 >> extra).to_le_bytes()[0] | v.to_le_bytes()[extra]);
        out.extend_from_slice(&v.to_le_bytes()[..extra]);
    }
    let packed: Vec<u8> = files.iter().flat_map(|(_, d)| d.iter().copied()).collect();
    // Main streams: one pack stream, one Copy folder, one substream per file.
    let mut h = vec![0x01, 0x04, 0x06, 0x00, 0x01, 0x09];
    num(&mut h, packed.len());
    h.extend([0x00, 0x07, 0x0B, 0x01, 0x00, 0x01, 0x01, 0x00, 0x0C]);
    num(&mut h, packed.len());
    h.extend([0x00, 0x08, 0x0D]);
    num(&mut h, files.len());
    h.push(0x09);
    for (_, data) in &files[..files.len() - 1] {
        num(&mut h, data.len());
    }
    h.extend([0x00, 0x00, 0x05]);
    num(&mut h, files.len());
    let names: Vec<u8> = files
        .iter()
        .flat_map(|(n, _)| n.encode_utf16().chain([0]).flat_map(u16::to_le_bytes))
        .collect();
    h.push(0x11);
    num(&mut h, names.len() + 1);
    h.push(0x00);
    h.extend(names);
    h.extend([0x00, 0x00]);

    let mut start = (packed.len() as u64).to_le_bytes().to_vec();
    start.extend((h.len() as u64).to_le_bytes());
    start.extend(crc32(0, &h).to_le_bytes());
    let mut out = b"7z\xBC\xAF\x27\x1C\x00\x04".to_vec();
    out.extend(crc32(0, &start).to_le_bytes());
    out.extend(start);
    out.extend(packed);
    out.extend(h);
    out
}

/// The bytes a multipart file maps to inside the single posted `volume`.
fn read_parts(meta: &FileMeta, volume: &[u8]) -> Vec<u8> {
    let FileMeta::Multipart(m) = meta else {
        panic!("archive members are multipart")
    };
    m.parts
        .iter()
        .flat_map(|p| {
            let covered: u64 = p.segments.items.iter().map(|s| u64::from(s.size)).sum();
            assert!(p.offset <= p.data.start && p.offset + covered >= p.data.end);
            let range =
                usize::try_from(p.data.start).unwrap()..usize::try_from(p.data.end).unwrap();
            volume[range].to_vec()
        })
        .collect()
}

/// One NZB `<file>` per posted `(subject, articles, file length, article size)`, `bytes`
/// padded like yEnc overhead.
fn nzb(files: &[(&str, &[Article], usize, usize)]) -> Vec<u8> {
    let mut xml = String::from("<?xml version=\"1.0\"?>\n<nzb>\n");
    for (subject, articles, len, size) in files {
        write!(
            xml,
            "<file poster=\"p\" date=\"0\" subject=\"{subject}\"><groups><group>a.b</group></groups><segments>"
        )
        .unwrap();
        for (i, a) in articles.iter().enumerate() {
            let id = &a.message_id;
            let payload = (*size).min(len - i * size);
            let bytes = payload + payload / 50 + 1;
            write!(
                xml,
                "<segment bytes=\"{bytes}\" number=\"{}\">{id}</segment>",
                i + 1
            )
            .unwrap();
        }
        xml.push_str("</segments></file>\n");
    }
    xml.push_str("</nzb>\n");
    xml.into_bytes()
}

async fn setup(articles: Vec<Article>) -> (LoopbackServer, Queue) {
    setup_on(LoopbackServer::start(articles).await.unwrap()).await
}

async fn setup_on(server: LoopbackServer) -> (LoopbackServer, Queue) {
    let queue = queue_on(&[&server]).await;
    (server, queue)
}

/// A queue whose providers are `servers`, tried in order.
async fn queue_on(servers: &[&LoopbackServer]) -> Queue {
    let path = std::env::temp_dir().join(format!("infinidysk-queue-{}.sqlite", ItemId::new()));
    queue_at(servers, &path).await
}

/// [`queue_on`] with its SQLite database at `path`.
async fn queue_at(servers: &[&LoopbackServer], path: &std::path::Path) -> Queue {
    // Background work leaves one slot for playback; four remain for imports.
    queue_sized(servers, path, 5).await
}

/// [`queue_at`] with `max_connections` per provider.
async fn queue_sized(
    servers: &[&LoopbackServer],
    path: &std::path::Path,
    max_connections: usize,
) -> Queue {
    let config = PoolConfig {
        max_connections,
        pipeline_depth: 4,
        idle_timeout: Duration::from_secs(5),
        min_connections: 0,
    };
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
    let db = Db::connect(&format!("sqlite:{}", path.display()))
        .await
        .unwrap();
    Queue::new(db, Arc::new(client))
}

async fn run(queue: &Queue, db: &Db, xml: &[u8]) -> infinidysk_db::HistoryEntry {
    let job = Submission {
        file_name: "job.nzb",
        category: "tv",
        priority: 0,
        post_processing: 0,
    };
    let id = queue.enqueue(&job, xml).await.unwrap();
    assert!(queue.process_next().await);
    db.history_entry(id).await.unwrap().unwrap()
}

async fn meta(db: &Db, path: &str) -> Option<FileMeta> {
    let item = db.item_by_path(path).await.unwrap()?;
    let raw = db.blob(item.meta_blob.unwrap()).await.unwrap().unwrap();
    Some(blob::decode_meta(&raw).unwrap())
}

#[tokio::test(flavor = "multi_thread")]
async fn stored_rar_volumes_become_one_member() {
    let mut articles = Vec::new();
    let mut files = Vec::new();
    for i in 1..=3 {
        let name = format!("x.part0{i}.rar");
        let vol = fixture(&format!(
            "archive/tests/fixtures/Rar5.multi.none.part0{i}.rar"
        ));
        files.push((
            name.clone(),
            post(&format!("r{i}"), &name, &vol, 16 << 10),
            vol.len(),
        ));
    }
    for (_, a, _) in &files {
        articles.extend(a.iter().cloned());
    }
    let xml = nzb(&files
        .iter()
        .map(|(n, a, len)| (n.as_str(), a.as_slice(), *len, 16 << 10))
        .collect::<Vec<_>>());
    let (_server, queue) = setup(articles).await;
    let db = queue.db().clone();

    let history = run(&queue, &db, &xml).await;
    assert_eq!(
        history.status,
        JobStatus::Completed,
        "{:?}",
        history.fail_message
    );
    let Some(FileMeta::Multipart(m)) = meta(&db, "/content/tv/job/stored.bin").await else {
        panic!("member imported as multipart")
    };
    let ranges: Vec<_> = m.parts.iter().map(|p| (p.data.start, p.data.end)).collect();
    assert_eq!(ranges, [(44, 100_044), (45, 100_045), (49, 100_049)]);
    for p in &m.parts {
        let covered: u64 = p.segments.items.iter().map(|s| u64::from(s.size)).sum();
        assert!(p.offset <= p.data.start && p.offset + covered >= p.data.end);
    }
}

fn vint(mut v: u64, out: &mut Vec<u8>) {
    loop {
        let b = u8::try_from(v & 0x7f).unwrap();
        v >>= 7;
        if v == 0 {
            out.push(b);
            return;
        }
        out.push(b | 0x80);
    }
}

/// One member chunk inside a RAR5 volume.
struct Chunk<'a> {
    name: &'a str,
    total: u64,
    data: &'a [u8],
    split_before: bool,
    split_after: bool,
}

/// A stored RAR5 volume; `number` is zero-based.
fn rar5_volume(number: u32, last: bool, chunks: &[Chunk]) -> Vec<u8> {
    fn block(fields: &[u8]) -> Vec<u8> {
        let mut header = Vec::new();
        vint(fields.len() as u64, &mut header);
        header.extend_from_slice(fields);
        let mut out = crc32(0, &header).to_le_bytes().to_vec();
        out.extend(header);
        out
    }
    let mut out = b"Rar!\x1a\x07\x01\x00".to_vec();
    let mut main = Vec::new();
    vint(1, &mut main);
    vint(0, &mut main);
    if number == 0 {
        vint(0x1, &mut main);
    } else {
        vint(0x3, &mut main);
        vint(u64::from(number), &mut main);
    }
    out.extend(block(&main));
    for c in chunks {
        let mut f = Vec::new();
        vint(2, &mut f);
        vint(
            0x2 | u64::from(c.split_before) << 3 | u64::from(c.split_after) << 4,
            &mut f,
        );
        vint(c.data.len() as u64, &mut f);
        // File flags, total size, attributes, stored, host OS, name.
        vint(0, &mut f);
        vint(c.total, &mut f);
        for _ in 0..3 {
            vint(0, &mut f);
        }
        vint(c.name.len() as u64, &mut f);
        f.extend_from_slice(c.name.as_bytes());
        out.extend(block(&f));
        out.extend_from_slice(c.data);
    }
    let mut end = Vec::new();
    vint(5, &mut end);
    vint(0, &mut end);
    vint(u64::from(!last), &mut end);
    out.extend(block(&end));
    out
}

/// `name` split evenly over `vols` RAR5 volumes.
fn rar5_set(name: &str, data: &[u8], vols: usize) -> Vec<Vec<u8>> {
    let parts: Vec<&[u8]> = data.chunks(data.len().div_ceil(vols)).collect();
    let n = parts.len();
    (0..n)
        .map(|i| {
            let chunk = Chunk {
                name,
                total: data.len() as u64,
                data: parts[i],
                split_before: i > 0,
                split_after: i + 1 < n,
            };
            rar5_volume(u32::try_from(i).unwrap(), i + 1 == n, &[chunk])
        })
        .collect()
}

/// Imports `vols` posted as one RAR set.
async fn import_rar(vols: &[Vec<u8>]) -> infinidysk_db::HistoryEntry {
    let named: Vec<_> = vols
        .iter()
        .enumerate()
        .map(|(i, v)| (format!("k3j9x0q2.part{:02}.rar", i + 1), v.clone()))
        .collect();
    import_files(&named).await
}

/// Imports `files` posted in this order as one release.
async fn import_files(files: &[(String, Vec<u8>)]) -> infinidysk_db::HistoryEntry {
    let posted: Vec<_> = files
        .iter()
        .enumerate()
        .map(|(i, (name, v))| (name, post(&format!("v{i}"), name, v, 16 << 10), v.len()))
        .collect();
    let xml = nzb(&posted
        .iter()
        .map(|(n, a, len)| (n.as_str(), a.as_slice(), *len, 16 << 10))
        .collect::<Vec<_>>());
    let articles = posted.into_iter().flat_map(|(_, a, _)| a).collect();
    let (_server, queue) = setup(articles).await;
    let db = queue.db().clone();
    run(&queue, &db, &xml).await
}

#[tokio::test(flavor = "multi_thread")]
async fn obfuscated_rar_sets_listed_out_of_order_import() {
    let a = rar5_set("Clip.A.mkv", &mkv(40_000, 5), 3);
    let b = rar5_set("Clip.B.mkv", &mkv(40_000, 6), 3);
    let order = [(&b, 2), (&a, 1), (&b, 0), (&a, 2), (&a, 0), (&b, 1)];
    let files: Vec<_> = order
        .iter()
        .enumerate()
        .map(|(i, (set, v))| (format!("q{i}x7k2m9p4w"), set[*v].clone()))
        .collect();
    let history = import_files(&files).await;
    assert_eq!(
        history.status,
        JobStatus::Completed,
        "{:?}",
        history.fail_message
    );
}

#[tokio::test(flavor = "multi_thread")]
async fn an_incomplete_non_media_rar_member_is_left_out() {
    let media = rar5_set("Clip.mkv", &mkv(40_000, 5), 2);
    let other = [7u8; 5000];
    for (split_before, split_after) in [(true, false), (false, true)] {
        let mut vols = media.clone();
        vols.push(rar5_volume(
            2,
            true,
            &[Chunk {
                name: "other.bin",
                total: 15_000,
                data: &other,
                split_before,
                split_after,
            }],
        ));
        let history = import_rar(&vols).await;
        assert_eq!(
            history.status,
            JobStatus::Completed,
            "{:?}",
            history.fail_message
        );
    }
    let mut cut = rar5_set("Clip.mkv", &mkv(40_000, 5), 3);
    cut.pop();
    let history = import_rar(&cut).await;
    assert_eq!(history.status, JobStatus::Failed);
    assert!(
        history
            .fail_message
            .unwrap()
            .contains("RAR volumes are missing")
    );
}

#[tokio::test(flavor = "multi_thread")]
async fn rar_volumes_whose_posts_claim_wrong_sizes_import() {
    let vols = rar5_set("Clip.mkv", &mkv(100_000, 5), 3);
    let posted: Vec<_> = vols
        .iter()
        .enumerate()
        .map(|(v, data)| {
            let name = format!("k3j9x0q2.part{:02}.rar", v + 1);
            let mut articles = post(&format!("v{v}"), &name, data, 16 << 10);
            // A smaller file of more parts, different in every article; offsets stay right.
            for (i, a) in articles.iter_mut().enumerate() {
                let line = a.wire.iter().position(|&b| b == b'\n').unwrap();
                let claim = format!(
                    "=ybegin part={} total={} line=128 size={} name={name}\r\n",
                    i + 1,
                    40 + i,
                    5000 + 7 * i
                );
                a.wire.splice(..=line, claim.into_bytes());
            }
            (name, articles, data.len())
        })
        .collect();
    let all: Vec<Article> = posted.iter().flat_map(|(_, a, _)| a.clone()).collect();
    let import = |listed: Vec<(String, Vec<Article>, usize)>| {
        let all = all.clone();
        async move {
            let xml = nzb(&listed
                .iter()
                .map(|(n, a, len)| (n.as_str(), a.as_slice(), *len, 16 << 10))
                .collect::<Vec<_>>());
            let (_server, queue) = setup(all).await;
            let db = queue.db().clone();
            run(&queue, &db, &xml).await
        }
    };

    let history = import(posted.clone()).await;
    assert_eq!(
        history.status,
        JobStatus::Completed,
        "{:?}",
        history.fail_message
    );

    // Listing the last article twice adds nothing.
    let mut repeated = posted.clone();
    let again = repeated[1].1[2].clone();
    repeated[1].1.push(again);
    repeated[1].2 += 16 << 10;
    let history = import(repeated).await;
    assert_eq!(
        history.status,
        JobStatus::Completed,
        "{:?}",
        history.fail_message
    );

    // An NZB that leaves out the end of a volume is not taken for the whole volume.
    let mut cut = posted;
    cut[1].1.pop();
    let history = import(cut).await;
    assert_eq!(history.status, JobStatus::Failed);
}

/// Posts `data` as one file named `name` and imports it alone.
async fn import_one(name: &str, data: &[u8]) -> (LoopbackServer, Db, infinidysk_db::HistoryEntry) {
    let articles = post("o", name, data, 16 << 10);
    let xml = nzb(&[(name, &articles, data.len(), 16 << 10)]);
    let (server, queue) = setup(articles).await;
    let db = queue.db().clone();
    let history = run(&queue, &db, &xml).await;
    (server, db, history)
}

#[tokio::test(flavor = "multi_thread")]
async fn rar_volumes_inside_a_7z_are_read_through_the_outer_segments() {
    let vols: Vec<Vec<u8>> = (1..=3)
        .map(|i| {
            fixture(&format!(
                "archive/tests/fixtures/Rar5.multi.none.part0{i}.rar"
            ))
        })
        .collect();
    let outer = sevenzip(&[
        ("sub/in.part01.rar", &vols[0]),
        ("sub/in.part02.rar", &vols[1]),
        ("sub/in.part03.rar", &vols[2]),
    ]);
    let (_server, db, history) = import_one("x.7z", &outer).await;
    assert_eq!(
        history.status,
        JobStatus::Completed,
        "{:?}",
        history.fail_message
    );
    let meta = meta(&db, "/content/tv/job/sub/stored.bin").await.unwrap();
    let expected = [
        &vols[0][44..100_044],
        &vols[1][45..100_045],
        &vols[2][49..100_049],
    ]
    .concat();
    assert_eq!(read_parts(&meta, &outer), expected);
}

#[tokio::test(flavor = "multi_thread")]
async fn archives_open_three_levels_deep_and_compressed_inner_members_fail() {
    let clip = mkv(40_000, 7);
    let five = sevenzip(&[("Deep.Clip.mkv", &mkv(1000, 8))]);
    let four = sevenzip(&[("Layer.Five.7z", &five)]);
    let three = sevenzip(&[("Layer.Four.7z", &four)]);
    let two = sevenzip(&[("Shallow.Clip.mkv", &clip), ("Layer.Three.7z", &three)]);
    let one = sevenzip(&[("Layer.Two.7z", &two)]);
    let (_server, db, history) = import_one("Layer.One.7z", &one).await;
    assert_eq!(
        history.status,
        JobStatus::Completed,
        "{:?}",
        history.fail_message
    );
    let clip_meta = meta(&db, "/content/tv/job/Shallow.Clip.mkv").await.unwrap();
    assert_eq!(read_parts(&clip_meta, &one), clip);
    let opaque = meta(&db, "/content/tv/job/Layer.Five.7z").await.unwrap();
    assert_eq!(read_parts(&opaque, &one), five);

    let lzma = fixture("archive/tests/fixtures/7Zip.LZMA.7z");
    let (_server, _db, history) = import_one("x.7z", &sevenzip(&[("in.7z", &lzma)])).await;
    assert_eq!(history.status, JobStatus::Failed);
    let message = history.fail_message.unwrap();
    assert!(
        message.contains("compressed 7z archives cannot be streamed"),
        "{message}"
    );
}

#[tokio::test(flavor = "multi_thread")]
async fn par2_names_obfuscated_files() {
    let data: Vec<u8> = (0..100_000usize)
        .map(|i| ((i * 31 + i / 251) % 256).to_le_bytes()[0])
        .collect();
    let a = post("a", "0123abcdef", &data, 32 << 10);
    let par2 = fixture("par2/tests/fixtures/set.par2");
    let p = post("p", "x.par2", &par2, 32 << 10);
    let xml = nzb(&[
        ("0123abcdef", &a, data.len(), 32 << 10),
        ("\"x.par2\"", &p, par2.len(), 32 << 10),
    ]);
    let (_server, queue) = setup([a.clone(), p.clone()].concat()).await;
    let db = queue.db().clone();

    let history = run(&queue, &db, &xml).await;
    assert_eq!(
        history.status,
        JobStatus::Completed,
        "{:?}",
        history.fail_message
    );
    let Some(FileMeta::Nzb(f)) = meta(&db, "/content/tv/job/a.bin").await else {
        panic!("deobfuscated file imported")
    };
    assert_eq!(f.proof.map(|p| p.file_length), Some(100_000));
}

/// Fixtures: `par2 create -c0 -s4096 one.par2 one.bin` and `-s8192 two.par2 two.bin`.
#[tokio::test(flavor = "multi_thread")]
async fn every_par2_index_names_its_file() {
    let data: Vec<u8> = (0..60_000usize)
        .map(|i| ((i * 31 + i / 251) % 256).to_le_bytes()[0])
        .collect();
    let mut articles = Vec::new();
    let mut files = Vec::new();
    for (i, (bin, par2)) in [(&data[..30_000], "one"), (&data[30_000..], "two")]
        .into_iter()
        .enumerate()
    {
        let index = fixture(&format!("queue/tests/fixtures/{par2}.par2"));
        let obfuscated = format!("{i}123abcdef");
        let (a, p) = (
            post(&format!("a{i}"), &obfuscated, bin, 32 << 10),
            post(&format!("p{i}"), &format!("{i}.par2"), &index, 32 << 10),
        );
        articles.extend(a.iter().chain(&p).cloned());
        files.push((
            obfuscated,
            a,
            bin.len(),
            format!("\"{i}.par2\""),
            p,
            index.len(),
        ));
    }
    let xml = nzb(&files
        .iter()
        .flat_map(|(n, a, len, pn, p, plen)| {
            [
                (n.as_str(), a.as_slice(), *len, 32 << 10),
                (pn.as_str(), p.as_slice(), *plen, 32 << 10),
            ]
        })
        .collect::<Vec<_>>());
    let (_server, queue) = setup(articles).await;
    let db = queue.db().clone();

    let history = run(&queue, &db, &xml).await;
    assert_eq!(
        history.status,
        JobStatus::Completed,
        "{:?}",
        history.fail_message
    );
    for (name, slice_size) in [("one", 4096), ("two", 8192)] {
        let Some(FileMeta::Nzb(f)) = meta(&db, &format!("/content/tv/job/{name}.bin")).await else {
            panic!("{name}.bin deobfuscated")
        };
        assert_eq!(f.proof.map(|p| p.slice_size), Some(slice_size));
    }
}

#[tokio::test(flavor = "multi_thread")]
async fn missing_media_fails_and_missing_sidecars_are_dropped() {
    let c = post("c", "Clip.mkv", &mkv(4000, 1), 1000);
    let s = post("s", "Clip.srt", &[2; 4000], 1000);
    let xml = nzb(&[("Clip.mkv", &c, 4000, 1000), ("Clip.srt", &s, 4000, 1000)]);
    let (_server, queue) = setup([c.clone(), s[..3].to_vec()].concat()).await;
    let db = queue.db().clone();

    let history = run(&queue, &db, &xml).await;
    assert_eq!(
        history.status,
        JobStatus::Completed,
        "{:?}",
        history.fail_message
    );
    // Short stems count as obfuscated, so the video takes the release name.
    assert!(meta(&db, "/content/tv/job/job.mkv").await.is_some());
    assert!(meta(&db, "/content/tv/job/job.srt").await.is_none());

    let (_server, queue) = setup([c[..1].to_vec(), c[2..].to_vec()].concat()).await;
    let db = queue.db().clone();
    let history = run(&queue, &db, &xml).await;
    assert_eq!(history.status, JobStatus::Failed);
    let reason = history.fail_message.unwrap();
    assert!(reason.starts_with("Missing articles: job.mkv"), "{reason}");
}

#[tokio::test(flavor = "multi_thread")]
async fn an_nzb_without_the_first_segment_blames_the_nzb() {
    let c = post("c", "Clip.mkv", &mkv(4000, 1), 1000);
    let xml = nzb(&[("Clip.mkv", &c[1..], 3000, 1000)]);
    let (_server, queue) = setup(c).await;
    let db = queue.db().clone();
    let history = run(&queue, &db, &xml).await;
    assert_eq!(history.status, JobStatus::Failed);
    let reason = history.fail_message.unwrap();
    assert!(reason.contains("the NZB lacks segments"), "{reason}");
}

#[tokio::test(flavor = "multi_thread")]
async fn an_nzb_missing_a_middle_segment_fails_without_downloading_the_rest() {
    let c = post("c", "Clip.mkv", &mkv(10_000, 1), 1000);
    let listed = [&c[..4], &c[5..]].concat();
    let xml = nzb(&[("Clip.mkv", &listed, 10_000, 1000)]);
    let (server, queue) = setup(c).await;
    let db = queue.db().clone();
    let history = run(&queue, &db, &xml).await;
    assert_eq!(history.status, JobStatus::Failed);
    let reason = history.fail_message.unwrap();
    assert!(reason.contains("the NZB lacks segments"), "{reason}");
    assert!(server.body_commands() <= 2, "{}", server.body_commands());
}

/// Fails its first write, then hands jobs off below `/strm`, where a folder `job` exists.
struct Handoffs(AtomicBool);

impl HandoffWriter for Handoffs {
    fn taken<'a>(
        &'a self,
        _: &'a str,
        folder: &'a str,
    ) -> Pin<Box<dyn Future<Output = Result<bool, String>> + Send + 'a>> {
        Box::pin(std::future::ready(Ok(folder == "job")))
    }

    fn write<'a>(
        &'a self,
        job: HandoffJob<'a>,
    ) -> Pin<Box<dyn Future<Output = Result<Written, String>> + Send + 'a>> {
        Box::pin(async move {
            if self.0.swap(false, Ordering::Relaxed) {
                return Err("disk full".to_owned());
            }
            let storage = format!("/strm/{}/{}", job.category, job.folder);
            let files = job
                .files
                .iter()
                .map(|f| Handoff {
                    path: format!("{storage}/{}", f.path.join("/")),
                    item: f.item,
                    job: Some(job.job),
                    size: None,
                })
                .collect();
            Ok(Written {
                storage,
                files,
                ..Written::default()
            })
        })
    }

    fn discard(&self, _: &Written) {}
}

#[tokio::test(flavor = "multi_thread")]
async fn a_job_completes_only_once_handed_off() {
    let c = post("c", "Clip.mkv", &mkv(4000, 1), 1000);
    let xml = nzb(&[("Clip.mkv", &c, 4000, 1000)]);
    let (_server, queue) = setup(c).await;
    let db = queue.db().clone();
    queue.set_handoff(Arc::new(Handoffs(AtomicBool::new(true))));

    let job = Submission {
        file_name: "job.nzb",
        category: "tv",
        priority: 0,
        post_processing: 0,
    };
    let id = queue.enqueue(&job, &xml).await.unwrap();
    assert!(queue.process_next().await);
    assert_eq!(db.history_entry(id).await.unwrap(), None);
    let queued = db.queue().await.unwrap();
    let reason = queued[0].last_error.as_deref().unwrap();
    assert!(reason.contains("disk full"), "{reason}");

    db.retry_job(id, 0, reason).await.unwrap();
    assert!(queue.process_next().await);
    let history = db.history_entry(id).await.unwrap().unwrap();
    assert_eq!(history.status, JobStatus::Completed);
    assert_eq!(
        history.storage.as_deref(),
        Some("/strm/tv/job (2)"),
        "the folder taken on disk is skipped"
    );
    let item = db
        .item_by_path("/content/tv/job (2)/job.mkv")
        .await
        .unwrap();
    assert_eq!(
        db.handoff_item("/strm/tv/job (2)/job.mkv").await.unwrap(),
        item.map(|i| i.id)
    );
}

/// Panics on its first write.
struct PanicsOnce(AtomicBool);

impl HandoffWriter for PanicsOnce {
    fn taken<'a>(
        &'a self,
        _: &'a str,
        _: &'a str,
    ) -> Pin<Box<dyn Future<Output = Result<bool, String>> + Send + 'a>> {
        Box::pin(std::future::ready(Ok(false)))
    }

    fn write<'a>(
        &'a self,
        _: HandoffJob<'a>,
    ) -> Pin<Box<dyn Future<Output = Result<Written, String>> + Send + 'a>> {
        let panics = self.0.swap(false, Ordering::Relaxed);
        Box::pin(async move {
            assert!(!panics, "injected handoff panic");
            Ok(Written::default())
        })
    }

    fn discard(&self, _: &Written) {}
}

#[tokio::test(flavor = "multi_thread")]
async fn a_crashing_import_fails_its_job_and_the_queue_goes_on() {
    let c = post("c", "Clip One.mkv", &mkv(4000, 1), 1000);
    let d = post("d", "Clip Two.mkv", &mkv(4000, 2), 1000);
    let (_server, queue) = setup([c.clone(), d.clone()].concat()).await;
    let db = queue.db().clone();
    queue.set_concurrency(1);
    queue.set_handoff(Arc::new(PanicsOnce(AtomicBool::new(true))));
    let job = Submission {
        file_name: "job.nzb",
        category: "tv",
        priority: 0,
        post_processing: 0,
    };
    for (name, articles) in [("Clip One.mkv", &c), ("Clip Two.mkv", &d)] {
        let xml = nzb(&[(name, articles, 4000, 1000)]);
        queue.enqueue(&job, &xml).await.unwrap();
    }
    let queue = Arc::new(queue);
    let worker = tokio::spawn({
        let queue = Arc::clone(&queue);
        async move { queue.run().await }
    });
    tokio::time::timeout(Duration::from_secs(10), async {
        while db.history().await.unwrap().len() < 2 {
            tokio::time::sleep(Duration::from_millis(10)).await;
        }
    })
    .await
    .expect("both jobs finished");
    assert!(!worker.is_finished());
    worker.abort();
    let mut results: Vec<_> = db
        .history()
        .await
        .unwrap()
        .into_iter()
        .map(|(h, _)| (h.status, h.fail_message))
        .collect();
    results.sort_by_key(|(status, _)| *status == JobStatus::Failed);
    assert_eq!(
        results,
        [
            (JobStatus::Completed, None),
            (
                JobStatus::Failed,
                Some("internal error while importing".to_owned())
            )
        ]
    );
}

#[tokio::test(flavor = "multi_thread")]
async fn a_job_whose_result_cannot_be_written_is_not_rerun_at_once() {
    use sqlx::{ConnectOptions as _, sqlite::SqliteConnectOptions};

    // Not a Matroska signature: the import fails after its first article.
    let articles = post("c", "Clip.mkv", &[0x55; 3000], 1000);
    let server = LoopbackServer::start(articles.clone()).await.unwrap();
    let path = std::env::temp_dir().join(format!("infinidysk-queue-{}.sqlite", ItemId::new()));
    let queue = Arc::new(queue_at(&[&server], &path).await);
    let job = Submission {
        file_name: "job.nzb",
        category: "tv",
        priority: 0,
        post_processing: 0,
    };
    let id = queue
        .enqueue(&job, &nzb(&[("Clip.mkv", &articles, 3000, 1000)]))
        .await
        .unwrap();
    // Every write to the job fails, as on a full disk.
    let mut conn = SqliteConnectOptions::new()
        .filename(&path)
        .connect()
        .await
        .unwrap();
    sqlx::raw_sql(
        "CREATE TRIGGER no_update BEFORE UPDATE ON queue BEGIN SELECT RAISE(ABORT, 'disk full'); END;
         CREATE TRIGGER no_delete BEFORE DELETE ON queue BEGIN SELECT RAISE(ABORT, 'disk full'); END;",
    )
    .execute(&mut conn)
    .await
    .unwrap();

    let worker = tokio::spawn({
        let queue = Arc::clone(&queue);
        async move { queue.run().await }
    });
    tokio::time::sleep(Duration::from_secs(2)).await;
    worker.abort();
    assert!(queue.db().history_entry(id).await.unwrap().is_none());
    let bodies = server.body_commands();
    assert!(bodies <= articles.len(), "{bodies} BODY commands");
}

#[tokio::test(flavor = "multi_thread")]
async fn a_wrong_article_on_one_provider_falls_over_to_the_next() {
    let right = post("c", "Clip.mkv", &mkv(10_000, 1), 1000);
    let mut wrong = post("o", "Other.mkv", &mkv(10_000, 9), 1000)[3].clone();
    wrong.message_id.clone_from(&right[9].message_id);
    let mut served = right.clone();
    served[9] = wrong;
    let bad = LoopbackServer::start(served).await.unwrap();
    let good = LoopbackServer::start(right.clone()).await.unwrap();
    let queue = queue_on(&[&bad, &good]).await;
    let db = queue.db().clone();

    let history = run(&queue, &db, &nzb(&[("Clip.mkv", &right, 10_000, 1000)])).await;
    assert_eq!(
        history.status,
        JobStatus::Completed,
        "{:?}",
        history.fail_message
    );
    let Some(FileMeta::Nzb(f)) = meta(&db, "/content/tv/job/job.mkv").await else {
        panic!("clip imported")
    };
    assert!(f.segments.items.iter().all(|s| s.size == 1000));
}

#[tokio::test(flavor = "multi_thread")]
async fn an_nzb_listing_a_part_twice_imports() {
    let c = post("c", "Clip.mkv", &mkv(10_000, 1), 1000);
    let mut repost = c[4].clone();
    repost.message_id = "repost@test".to_owned();
    let listed = [&c[..5], &[repost.clone()], &c[5..]].concat();
    let (_server, queue) = setup([c.clone(), vec![repost]].concat()).await;
    let db = queue.db().clone();

    let history = run(&queue, &db, &nzb(&[("Clip.mkv", &listed, 11_000, 1000)])).await;
    assert_eq!(
        history.status,
        JobStatus::Completed,
        "{:?}",
        history.fail_message
    );
    let Some(FileMeta::Nzb(f)) = meta(&db, "/content/tv/job/job.mkv").await else {
        panic!("clip imported")
    };
    let ids: Vec<_> = f.segments.items.iter().map(|s| s.id.as_str()).collect();
    let posted: Vec<_> = c.iter().map(|a| a.message_id.as_str()).collect();
    assert_eq!(ids, posted);
    assert!(f.segments.items.iter().all(|s| s.size == 1000));
}

#[tokio::test(flavor = "multi_thread")]
async fn media_with_the_wrong_signature_fails() {
    let c = post("c", "Clip.mkv", &[1; 4000], 1000);
    let xml = nzb(&[("Clip.mkv", &c, 4000, 1000)]);
    let (_server, queue) = setup(c).await;
    let db = queue.db().clone();

    let history = run(&queue, &db, &xml).await;
    assert_eq!(history.status, JobStatus::Failed);
    assert_eq!(
        history.fail_message.as_deref(),
        Some("Not a valid mkv file: job.mkv")
    );
}

#[tokio::test(flavor = "multi_thread")]
async fn samples_and_blocked_files_are_dropped_and_owners_need_their_media() {
    let m = post("m", "Clip.mkv", &mkv(10_000, 1), 1000);
    let p = post("p", "Clip-proof.mkv", &mkv(1000, 2), 1000);
    let n = post("n", "notes.txt", &[3; 1000], 1000);
    let xml = nzb(&[
        ("Clip.mkv", &m, 10_000, 1000),
        ("Clip-proof.mkv", &p, 1000, 1000),
        ("notes.txt", &n, 1000, 1000),
    ]);
    let (_server, queue) = setup([m.clone(), p, n].concat()).await;
    let db = queue.db().clone();
    queue.update_rules(|r| r.blocklist = vec!["NOTES*".to_owned()]);

    let history = run(&queue, &db, &xml).await;
    assert_eq!(
        history.status,
        JobStatus::Completed,
        "{:?}",
        history.fail_message
    );
    let files = db
        .children(
            db.item_by_path("/content/tv/job")
                .await
                .unwrap()
                .unwrap()
                .id,
        )
        .await
        .unwrap();
    let names: Vec<_> = files.iter().map(|i| i.name.as_str()).collect();
    assert_eq!(names, ["job.mkv"]);

    queue.update_rules(|r| {
        r.owners = [("tv".to_owned(), vec![media::Kind::Audio, media::Kind::Book])].into();
    });
    let history = run(&queue, &db, &nzb(&[("Clip.mkv", &m, 10_000, 1000)])).await;
    assert_eq!(
        history.fail_message.as_deref(),
        Some("No audio or book files found.")
    );
}

#[tokio::test(flavor = "multi_thread")]
async fn categories_get_folders_before_any_download() {
    let (_server, queue) = setup(Vec::new()).await;
    queue
        .add_categories(&["movies", "movies", " "])
        .await
        .unwrap();

    let folders = queue.db().children(ItemId::CONTENT).await.unwrap();
    let names: Vec<_> = folders.iter().map(|i| i.name.as_str()).collect();
    assert_eq!(names, ["movies"]);
}

#[tokio::test(flavor = "multi_thread")]
async fn a_stalled_job_does_not_hold_up_the_next_one() {
    let c = post("c", "Clip One.mkv", &mkv(4000, 1), 1000);
    let d = post("d", "Clip Two.mkv", &mkv(4000, 2), 1000);
    // The first BODY, which belongs to whichever job starts first, stalls.
    let server =
        LoopbackServer::start_stalling([c.clone(), d.clone()].concat(), 0, Duration::from_secs(3))
            .await
            .unwrap();
    // Enough connections that no request of the other job pipelines behind the stalled one.
    let path = std::env::temp_dir().join(format!("infinidysk-queue-{}.sqlite", ItemId::new()));
    let queue = queue_sized(&[&server], &path, 16).await;
    let db = queue.db().clone();
    let job = Submission {
        file_name: "job.nzb",
        category: "tv",
        priority: 0,
        post_processing: 0,
    };
    queue
        .enqueue(&job, &nzb(&[("Clip One.mkv", &c, 4000, 1000)]))
        .await
        .unwrap();
    queue
        .enqueue(&job, &nzb(&[("Clip Two.mkv", &d, 4000, 1000)]))
        .await
        .unwrap();
    let queue = Arc::new(queue);
    let worker = tokio::spawn({
        let queue = Arc::clone(&queue);
        async move { queue.run().await }
    });
    let finished = |n: usize| {
        let db = db.clone();
        async move {
            while db.history().await.unwrap().len() < n {
                tokio::time::sleep(Duration::from_millis(10)).await;
            }
        }
    };

    tokio::time::timeout(Duration::from_secs(2), finished(1))
        .await
        .expect("the other job finished during the stall");
    tokio::time::timeout(Duration::from_secs(10), finished(2))
        .await
        .expect("the stalled job finished after it");
    worker.abort();
    let mut folders: Vec<_> = db
        .history()
        .await
        .unwrap()
        .into_iter()
        .map(|(h, path)| {
            assert_eq!(h.status, JobStatus::Completed, "{:?}", h.fail_message);
            path.unwrap()
        })
        .collect();
    folders.sort();
    assert_eq!(folders, ["/content/tv/job", "/content/tv/job (2)"]);
}

#[tokio::test(flavor = "multi_thread")]
async fn a_stuck_attempt_is_requeued_and_retried() {
    let c = post("c", "Clip.mkv", &mkv(4000, 1), 1000);
    let server = LoopbackServer::start_stalling(c.clone(), 0, Duration::from_secs(5))
        .await
        .unwrap();
    let (_server, queue) = setup_on(server).await;
    let queue = queue.with_stall_timeout(Duration::from_millis(300));
    let job = Submission {
        file_name: "job.nzb",
        category: "tv",
        priority: 0,
        post_processing: 0,
    };
    let id = queue
        .enqueue(&job, &nzb(&[("Clip.mkv", &c, 4000, 1000)]))
        .await
        .unwrap();
    assert!(queue.process_next().await);
    let queued = queue.db().queue().await.unwrap();
    assert_eq!(queued.len(), 1, "requeued, not failed");
    assert_eq!(queued[0].stuck_count, 1);
    assert!(queue.process_next().await);
    let done = queue.db().history_entry(id).await.unwrap().unwrap();
    assert_eq!(done.status, JobStatus::Completed, "{:?}", done.fail_message);
}

#[tokio::test(flavor = "multi_thread")]
async fn paused_queue_and_paused_jobs_wait_for_resume() {
    let c = post("c", "Clip.mkv", &mkv(4000, 1), 1000);
    let xml = nzb(&[("Clip.mkv", &c, 4000, 1000)]);
    let (_server, queue) = setup(c).await;
    queue.set_paused(true).await.unwrap();
    queue.restore().await.unwrap();
    assert!(queue.is_paused(), "the pause is stored");
    let job = Submission {
        file_name: "job.nzb",
        category: "tv",
        priority: -2,
        post_processing: 0,
    };
    let id = queue.enqueue(&job, &xml).await.unwrap();
    assert!(!queue.process_next().await, "the queue is paused");
    queue.set_paused(false).await.unwrap();
    assert!(!queue.process_next().await, "the job was added paused");
    assert_eq!(queue.pause_jobs(&[id], false).await.unwrap(), 1);
    assert!(queue.process_next().await);
}

#[tokio::test(flavor = "multi_thread")]
async fn the_same_nzb_queues_once_and_invalid_ones_fail_at_once() {
    let c = post("c", "Clip.mkv", &mkv(4000, 1), 1000);
    let xml = nzb(&[("Clip.mkv", &c, 4000, 1000)]);
    let (_server, queue) = setup(c).await;
    let db = queue.db().clone();
    let job = |category| Submission {
        file_name: "job.nzb",
        category,
        priority: 0,
        post_processing: 0,
    };
    let id = queue.enqueue(&job("tv"), &xml).await.unwrap();
    assert_eq!(queue.enqueue(&job("movies"), &xml).await.unwrap(), id);
    let queued = db.queue().await.unwrap();
    assert_eq!((queued.len(), queued[0].category.as_str()), (1, "tv"));
    assert!(queue.process_next().await);
    assert_ne!(
        queue.enqueue(&job("tv"), &xml).await.unwrap(),
        id,
        "a finished job does not count"
    );

    assert!(queue.enqueue(&job("tv"), b"<nzb/>").await.is_err());
    let failed = db
        .history()
        .await
        .unwrap()
        .into_iter()
        .filter(|(h, _)| h.status == JobStatus::Failed)
        .count();
    assert_eq!(failed, 1, "the invalid NZB is in history");
}

/// Posts `xml` as the file `release.nzb` and returns the NZB of a job holding only that.
fn wrap(prefix: &str, xml: &[u8], articles: &mut Vec<Article>) -> Vec<u8> {
    let posted = post(prefix, "release.nzb", xml, 1000);
    let outer = nzb(&[("release.nzb", &posted, xml.len(), 1000)]);
    articles.extend(posted);
    outer
}

async fn names_in(db: &Db, path: &str) -> Vec<String> {
    let dir = db.item_by_path(path).await.unwrap().unwrap();
    let files = db.children(dir.id).await.unwrap();
    files.into_iter().map(|i| i.name).collect()
}

#[tokio::test(flavor = "multi_thread")]
async fn a_job_holding_only_an_nzb_imports_the_release_it_names() {
    let clip = post("c", "Clip.mkv", &mkv(4000, 1), 1000);
    let release = nzb(&[("Clip.mkv", &clip, 4000, 1000)]);
    let inner = post("n", "release.nzb", &release, 1000);
    let info = post("i", "release.nfo", &[3; 500], 1000);
    let outer = nzb(&[
        ("release.nzb", &inner, release.len(), 1000),
        ("release.nfo", &info, 500, 1000),
    ]);
    let (_server, queue) = setup([clip, inner, info].concat()).await;
    let db = queue.db().clone();
    queue.update_rules(|r| {
        r.owners = [("tv".to_owned(), vec![media::Kind::Video])].into();
    });

    let history = run(&queue, &db, &outer).await;
    assert_eq!(
        history.status,
        JobStatus::Completed,
        "{:?}",
        history.fail_message
    );
    assert_eq!(names_in(&db, "/content/tv/job").await, ["job.mkv"]);
    let stored = db.blob(history.nzb_blob.unwrap()).await.unwrap().unwrap();
    assert_eq!(blob::decode_nzb(&stored).unwrap(), release);
    let listed: u64 = infinidysk_queue::nzb::parse(&release)
        .unwrap()
        .files
        .iter()
        .map(infinidysk_queue::nzb::File::bytes)
        .sum();
    assert_eq!(history.total_bytes, listed);
}

#[tokio::test(flavor = "multi_thread")]
async fn an_nzb_inside_a_stored_archive_is_followed() {
    let clip = post("c", "Clip.mkv", &mkv(4000, 1), 1000);
    let release = nzb(&[("Clip.mkv", &clip, 4000, 1000)]);
    let archive = sevenzip(&[("release.nzb", &release)]);
    let pack = post("p", "pack.7z", &archive, 1000);
    let outer = nzb(&[("pack.7z", &pack, archive.len(), 1000)]);
    let (_server, queue) = setup([clip, pack].concat()).await;
    let db = queue.db().clone();

    let history = run(&queue, &db, &outer).await;
    assert_eq!(
        history.status,
        JobStatus::Completed,
        "{:?}",
        history.fail_message
    );
    assert_eq!(names_in(&db, "/content/tv/job").await, ["job.mkv"]);
}

#[tokio::test(flavor = "multi_thread")]
async fn an_nzb_inside_a_rar_or_gzipped_is_followed() {
    use std::io::Write as _;

    let clip = post("c", "Clip.mkv", &mkv(4000, 1), 1000);
    let release = nzb(&[("Clip.mkv", &clip, 4000, 1000)]);
    let chunk = Chunk {
        name: "release.nzb",
        total: release.len() as u64,
        data: &release,
        split_before: false,
        split_after: false,
    };
    let rar = rar5_volume(0, true, &[chunk]);
    let packed = post("r", "pack.rar", &rar, 1000);
    let in_rar = nzb(&[("pack.rar", &packed, rar.len(), 1000)]);
    let mut gz = flate2::write::GzEncoder::new(Vec::new(), flate2::Compression::default());
    gz.write_all(&release).unwrap();
    let mut articles = [clip, packed].concat();
    let gzipped = wrap("g", &gz.finish().unwrap(), &mut articles);
    let (_server, queue) = setup(articles).await;
    let db = queue.db().clone();

    for outer in [in_rar, gzipped] {
        let history = run(&queue, &db, &outer).await;
        assert_eq!(
            history.status,
            JobStatus::Completed,
            "{:?}",
            history.fail_message
        );
        db.delete_item(history.download_dir.unwrap()).await.unwrap();
    }
}

#[tokio::test(flavor = "multi_thread")]
async fn a_retried_inner_nzb_opens_with_its_own_password() {
    let archive = fixture("archive/tests/fixtures/aes-5.rar");
    let packed = post("a", "pack.rar", &archive, 1000);
    let release = String::from_utf8(nzb(&[("pack.rar", &packed, archive.len(), 1000)]))
        .unwrap()
        .replacen(
            "<nzb>",
            "<nzb><head><meta type=\"password\">test</meta></head>",
            1,
        );
    let mut articles = packed;
    let outer = wrap("n", release.as_bytes(), &mut articles);
    let (_server, queue) = setup(articles).await;
    let db = queue.db().clone();
    for file_name in ["job{{wrong}}.nzb", "job{{2024}} - password=wrong.nzb"] {
        let job = Submission {
            file_name,
            category: "tv",
            priority: 0,
            post_processing: 0,
        };

        let id = queue.enqueue(&job, &outer).await.unwrap();
        assert!(queue.process_next().await);
        let history = db.history_entry(id).await.unwrap().unwrap();
        assert_eq!(
            history.status,
            JobStatus::Completed,
            "{file_name}: {:?}",
            history.fail_message
        );
        db.delete_item(history.download_dir.unwrap()).await.unwrap();

        let id = queue.retry(id).await.unwrap();
        assert!(queue.process_next().await);
        let history = db.history_entry(id).await.unwrap().unwrap();
        assert_eq!(
            history.status,
            JobStatus::Completed,
            "{file_name} retried: {:?}",
            history.fail_message
        );
        db.delete_item(history.download_dir.unwrap()).await.unwrap();
    }
}

#[tokio::test(flavor = "multi_thread")]
async fn a_blocklisted_nzb_is_not_followed() {
    let clip = post("c", "Clip.mkv", &mkv(4000, 1), 1000);
    let release = nzb(&[("Clip.mkv", &clip, 4000, 1000)]);
    let archive = sevenzip(&[("release.nzb", &release)]);
    let pack = post("p", "pack.7z", &archive, 1000);
    let in_7z = nzb(&[("pack.7z", &pack, archive.len(), 1000)]);
    let mut articles = [clip, pack].concat();
    let plain = wrap("n", &release, &mut articles);
    let (_server, queue) = setup(articles).await;
    let db = queue.db().clone();
    queue.update_rules(|r| r.blocklist = vec!["*.nzb".to_owned()]);

    for outer in [plain, in_7z] {
        let history = run(&queue, &db, &outer).await;
        assert_eq!(
            history.fail_message.as_deref(),
            Some("No importable files found.")
        );
    }
}

#[tokio::test(flavor = "multi_thread")]
async fn an_nzb_next_to_other_files_stays_a_file() {
    let release = b"<nzb/>";
    let inner = post("n", "release.nzb", release, 1000);
    let notes = post("t", "notes.txt", &[3; 500], 1000);
    let outer = nzb(&[
        ("release.nzb", &inner, release.len(), 1000),
        ("notes.txt", &notes, 500, 1000),
    ]);
    let (_server, queue) = setup([inner, notes].concat()).await;
    let db = queue.db().clone();

    let history = run(&queue, &db, &outer).await;
    assert_eq!(
        history.status,
        JobStatus::Completed,
        "{:?}",
        history.fail_message
    );
    let mut names = names_in(&db, "/content/tv/job").await;
    names.sort();
    assert_eq!(names, ["notes.txt", "release.nzb"]);
}

#[tokio::test(flavor = "multi_thread")]
async fn several_or_unusable_inner_nzbs_fail_the_job() {
    let clip = post("c", "Clip.mkv", &mkv(4000, 1), 1000);
    let release = nzb(&[("Clip.mkv", &clip, 4000, 1000)]);
    let a = post("a", "a.nzb", &release, 1000);
    let b = post("b", "b.nzb", &release, 1000);
    let several = nzb(&[
        ("a.nzb", &a, release.len(), 1000),
        ("b.nzb", &b, release.len(), 1000),
    ]);
    let mut articles = [clip, a, b].concat();
    let unusable = wrap("u", b"<nzb/>", &mut articles);
    let (_server, queue) = setup(articles).await;
    let db = queue.db().clone();

    let history = run(&queue, &db, &several).await;
    assert_eq!(
        history.fail_message.as_deref(),
        Some("The download contains several NZB files; only one is supported.")
    );
    let history = run(&queue, &db, &unusable).await;
    assert_eq!(
        history.fail_message.as_deref(),
        Some(
            "The NZB file in this download is not usable: \
             NZB contains no file with a valid segment"
        )
    );
    assert!(db.item_by_path("/content/tv/job").await.unwrap().is_none());
}

#[tokio::test(flavor = "multi_thread")]
async fn inner_nzbs_are_followed_three_deep_and_no_further() {
    let clip = post("c", "Clip.mkv", &mkv(4000, 1), 1000);
    let mut articles = clip.clone();
    let mut xml = nzb(&[("Clip.mkv", &clip, 4000, 1000)]);
    for depth in 0..3 {
        xml = wrap(&format!("n{depth}"), &xml, &mut articles);
    }
    let deepest = wrap("n3", &xml, &mut articles);
    let (_server, queue) = setup(articles).await;
    let db = queue.db().clone();

    let history = run(&queue, &db, &deepest).await;
    assert_eq!(
        history.fail_message.as_deref(),
        Some("The download contains NZB files nested too deep.")
    );
    let history = run(&queue, &db, &xml).await;
    assert_eq!(
        history.status,
        JobStatus::Completed,
        "{:?}",
        history.fail_message
    );
}

/// Inspects `xml` submitted as `job.nzb`.
async fn inspect(queue: &Queue, xml: &[u8], password: Option<&str>) -> Inspection {
    let slot = queue.start_inspection().expect("no inspection is running");
    slot.inspect("job.nzb", xml, password).await.unwrap()
}

/// The NZB and the articles of `files`, posted as one release.
fn release_of(files: &[(&str, Vec<u8>)]) -> (Vec<u8>, Vec<Article>) {
    let posted: Vec<_> = files
        .iter()
        .enumerate()
        .map(|(i, (name, v))| (*name, post(&format!("v{i}"), name, v, 16 << 10), v.len()))
        .collect();
    let xml = nzb(&posted
        .iter()
        .map(|(n, a, len)| (*n, a.as_slice(), *len, 16 << 10))
        .collect::<Vec<_>>());
    let articles = posted.into_iter().flat_map(|(_, a, _)| a).collect();
    (xml, articles)
}

/// Posts `files` as one release, inspects it, checks that nothing was stored, then
/// imports it.
async fn inspect_then_import(
    files: &[(&str, Vec<u8>)],
) -> (Inspection, infinidysk_db::HistoryEntry, Db) {
    let (xml, articles) = release_of(files);
    inspect_then_import_nzb(&xml, articles, None).await
}

/// Inspects `xml` with `password`, checks that nothing was stored, then imports it
/// without a password.
async fn inspect_then_import_nzb(
    xml: &[u8],
    articles: Vec<Article>,
    password: Option<&str>,
) -> (Inspection, infinidysk_db::HistoryEntry, Db) {
    let (_server, queue) = setup(articles).await;
    let db = queue.db().clone();
    let report = inspect(&queue, xml, password).await;
    assert!(db.queue().await.unwrap().is_empty(), "queued");
    assert!(db.history().await.unwrap().is_empty(), "in history");
    let content = db.children(ItemId::CONTENT).await.unwrap();
    assert!(content.is_empty(), "published");
    let history = run(&queue, &db, xml).await;
    (report, history, db)
}

/// Paths and sizes of the files below `/content/tv/job`.
async fn published(db: &Db) -> Vec<(String, u64)> {
    let mut files = Vec::new();
    let mut dirs = vec![db.item_by_path("/content/tv/job").await.unwrap().unwrap()];
    while let Some(dir) = dirs.pop() {
        for item in db.children(dir.id).await.unwrap() {
            match item.size {
                Some(size) => files.push((item.path["/content/tv/job/".len()..].to_owned(), size)),
                None => dirs.push(item),
            }
        }
    }
    files.sort();
    files
}

#[tokio::test(flavor = "multi_thread")]
async fn an_inspection_lists_what_the_import_publishes_and_stores_nothing() {
    let vols = rar5_set("Clip.A.mkv", &mkv(40_000, 5), 3);
    let split = mkv(40_000, 6);
    // `set.par2` names this file `a.bin`.
    let indexed: Vec<u8> = (0..100_000usize)
        .map(|i| ((i * 31 + i / 251) % 256).to_le_bytes()[0])
        .collect();
    let (report, history, db) = inspect_then_import(&[
        ("k3j9x0q2.part01.rar", vols[0].clone()),
        ("k3j9x0q2.part02.rar", vols[1].clone()),
        ("k3j9x0q2.part03.rar", vols[2].clone()),
        ("0a1b2c3d4e5f.mkv", mkv(20_000, 6)),
        ("0a1b2c3d4e5f.en.srt", vec![7; 3000]),
        ("notes.nfo", vec![8; 3000]),
        ("0123abcdef", indexed),
        ("x.par2", fixture("par2/tests/fixtures/set.par2")),
        ("Clip.B.mkv.001", split[..20_000].to_vec()),
        ("Clip.B.mkv.002", split[20_000..].to_vec()),
    ])
    .await;
    assert_eq!(
        history.status,
        JobStatus::Completed,
        "{:?}",
        history.fail_message
    );
    assert!(report.manifest_complete && report.failure.is_none());
    let mut planned: Vec<_> = report
        .files
        .iter()
        .map(|f| (f.planned_name.clone(), f.size.unwrap()))
        .collect();
    planned.sort();
    assert_eq!(planned, published(&db).await);
    let listed: Vec<_> = report
        .files
        .iter()
        .map(|f| {
            (
                f.raw_path.as_str(),
                f.planned_name.as_str(),
                f.source,
                f.set,
            )
        })
        .collect();
    assert_eq!(
        listed,
        [
            (
                "0a1b2c3d4e5f.mkv",
                "job.mkv",
                import::NameSource::Subject,
                None
            ),
            (
                "0a1b2c3d4e5f.en.srt",
                "job.en.srt",
                import::NameSource::Subject,
                None
            ),
            ("a.bin", "a.bin", import::NameSource::Par2, None),
            (
                "Clip.A.mkv",
                "Clip.A.mkv",
                import::NameSource::ArchiveHeader,
                Some(0)
            ),
            (
                "Clip.B.mkv",
                "Clip.B.mkv",
                import::NameSource::Subject,
                Some(1)
            ),
        ]
    );
    let sets: Vec<_> = report
        .archives
        .iter()
        .map(|s| (s.kind, s.depth, s.encryption, s.access(), s.streamable()))
        .collect();
    let plain = |kind| {
        (
            kind,
            0,
            import::Encrypted::None,
            import::Access::Plain,
            Some(true),
        )
    };
    assert_eq!(
        sets,
        [
            plain(import::ArchiveType::Rar5),
            plain(import::ArchiveType::Split)
        ]
    );
    assert!(report.article_requests > 0 && report.timings.archives.is_some());
}

#[tokio::test(flavor = "multi_thread")]
async fn an_inspection_fails_like_the_import() {
    use import::{FailureKind, Step};

    let lzma = fixture("archive/tests/fixtures/7Zip.LZMA.7z");
    let compressed = sevenzip(&[("in.7z", &lzma)]);
    let mut cut = rar5_set("Clip.mkv", &mkv(40_000, 5), 3);
    cut.pop();
    let clip = post("c", "Clip.mkv", &mkv(4000, 1), 1000);
    let release = nzb(&[("Clip.mkv", &clip, 4000, 1000)]);
    let cases = [
        (
            release_of(&[("x.7z", compressed.clone())]),
            FailureKind::Unsupported,
            Step::Archives,
            vec![],
        ),
        // An import stops at the compressed member before it looks for an NZB file,
        (
            release_of(&[(
                "x.7z",
                sevenzip(&[("in.7z", &lzma), ("release.nzb", &release)]),
            )]),
            FailureKind::Unsupported,
            Step::Archives,
            vec![],
        ),
        // and before it reads a broken archive after it.
        (
            release_of(&[("x.7z", compressed), ("y.7z", vec![8; 3000])]),
            FailureKind::Unsupported,
            Step::Archives,
            vec![],
        ),
        (
            release_of(&[
                ("k3j9x0q2.part01.rar", cut[0].clone()),
                ("k3j9x0q2.part02.rar", cut[1].clone()),
            ]),
            FailureKind::MissingArticles,
            Step::Archives,
            vec![],
        ),
        (
            release_of(&[("notes.nfo", vec![8; 3000])]),
            FailureKind::Other,
            Step::Archives,
            vec![],
        ),
        // No provider has the first article of the video.
        (
            (release.clone(), clip[1..].to_vec()),
            FailureKind::MissingArticles,
            Step::Layouts,
            vec!["Clip.mkv"],
        ),
        // The NZB leaves out the first segment; every article it lists is there.
        (
            (nzb(&[("Clip.mkv", &clip[1..], 3000, 1000)]), clip.clone()),
            FailureKind::MissingArticles,
            Step::Layouts,
            vec![],
        ),
    ];
    for ((xml, articles), kind, step, missing) in cases {
        let (report, history, _db) = inspect_then_import_nzb(&xml, articles, None).await;
        assert_eq!(history.status, JobStatus::Failed);
        let failure = report.failure.expect("the import failed");
        assert_eq!((failure.kind, failure.step), (kind, step));
        assert_eq!(Some(failure.message), history.fail_message);
        assert_eq!(report.missing_first_segments, missing);
    }
}

#[tokio::test(flavor = "multi_thread")]
async fn an_inspection_reports_nesting_and_archives_left_unopened() {
    let five = sevenzip(&[("Deep.Clip.mkv", &mkv(1000, 8))]);
    let four = sevenzip(&[("Layer.Five.7z", &five)]);
    let three = sevenzip(&[("Layer.Four.7z", &four)]);
    let two = sevenzip(&[
        ("Shallow.Clip.mkv", &mkv(40_000, 7)),
        ("sub/Layer.Three.7z", &three),
    ]);
    let one = sevenzip(&[("Layer.Two.7z", &two)]);
    let a = rar5_set("Clip.A.mkv", &mkv(40_000, 5), 2);
    let b = rar5_set("Clip.B.mkv", &mkv(40_000, 6), 2);
    let (report, history, _db) = inspect_then_import(&[
        ("Layer.One.7z", one),
        ("a.part01.rar", a[0].clone()),
        ("a.part02.rar", a[1].clone()),
        ("b.part01.rar", b[0].clone()),
        ("b.part02.rar", b[1].clone()),
    ])
    .await;
    assert_eq!(
        history.status,
        JobStatus::Completed,
        "{:?}",
        history.fail_message
    );
    assert!(!report.manifest_complete, "Layer.Five.7z stays a file");
    assert!(report.nested_archive_opaque);
    assert!(report.failure.is_none() && !report.limit_exceeded);
    let sets: Vec<_> = report.archives.iter().map(|s| (s.kind, s.depth)).collect();
    assert_eq!(
        sets,
        [
            (import::ArchiveType::Rar5, 0),
            (import::ArchiveType::Rar5, 0),
            (import::ArchiveType::SevenZip, 0),
            (import::ArchiveType::SevenZip, 1),
            (import::ArchiveType::SevenZip, 2),
            (import::ArchiveType::SevenZip, 3),
        ]
    );
    let mut files: Vec<_> = report
        .files
        .iter()
        .map(|f| (f.raw_path.as_str(), f.set))
        .collect();
    files.sort_unstable();
    assert_eq!(
        files,
        [
            ("Clip.A.mkv", Some(0)),
            ("Clip.B.mkv", Some(1)),
            ("Shallow.Clip.mkv", Some(3)),
            ("sub/Layer.Five.7z", Some(5)),
        ]
    );
}

/// Posts archive fixtures under `names` and inspects them with `password`.
async fn inspect_fixtures(names: &[(&str, &str)], password: Option<&str>) -> Inspection {
    let posted: Vec<_> = names
        .iter()
        .enumerate()
        .map(|(i, (name, file))| {
            let data = fixture(&format!("archive/tests/fixtures/{file}"));
            (
                *name,
                post(&format!("v{i}"), name, &data, 16 << 10),
                data.len(),
            )
        })
        .collect();
    let xml = nzb(&posted
        .iter()
        .map(|(n, a, len)| (*n, a.as_slice(), *len, 16 << 10))
        .collect::<Vec<_>>());
    let articles = posted.into_iter().flat_map(|(_, a, _)| a).collect();
    let (_server, queue) = setup(articles).await;
    inspect(&queue, &xml, password).await
}

#[tokio::test(flavor = "multi_thread")]
async fn an_inspection_says_what_a_password_opened() {
    use import::{Access, ArchiveType, Encrypted, FailureKind};

    let volumes = [
        ("p.part1.rar", "aes-5v.part1.rar"),
        ("p.part2.rar", "aes-5v.part2.rar"),
        ("p.part3.rar", "aes-5v.part3.rar"),
    ];
    let member = |report: &Inspection| {
        let [file] = &report.files[..] else {
            panic!("one member: {:?}", report.files)
        };
        (file.raw_path.clone(), file.size, file.set)
    };
    let data_bin = ("data.bin".to_owned(), Some(9001), Some(0));

    // RAR5 stores a check value: the right password is confirmed, a wrong one caught.
    let right = inspect_fixtures(&volumes, Some("test")).await;
    let set = &right.archives[0];
    assert_eq!(
        (set.kind, set.encryption, set.access(), set.streamable()),
        (
            ArchiveType::Rar5,
            Encrypted::Data,
            Access::PasswordValidated,
            Some(true)
        )
    );
    assert!(right.manifest_complete && right.failure.is_none());
    assert!(!right.password_not_validated);
    assert_eq!(member(&right), data_bin);

    // Without a password the headers still list the member.
    for (password, message) in [(None, "has no password"), (Some("nope"), "wrong archive")] {
        let locked = inspect_fixtures(&volumes, password).await;
        let set = &locked.archives[0];
        assert_eq!(
            (set.encryption, set.access(), set.streamable()),
            (Encrypted::Data, Access::PasswordRequired, Some(true))
        );
        assert!(locked.manifest_complete, "{password:?}");
        assert_eq!(member(&locked), data_bin);
        let failure = locked.failure.expect("an import fails");
        assert_eq!(failure.kind, FailureKind::Password);
        assert!(failure.message.contains(message), "{}", failure.message);
    }

    // Encrypted headers hide the members, and so whether they can be streamed.
    let hidden = inspect_fixtures(&[("p.rar", "aes-5hp.rar")], None).await;
    let set = &hidden.archives[0];
    assert_eq!(
        (set.encryption, set.access(), set.streamable()),
        (Encrypted::Headers, Access::PasswordRequired, None)
    );
    assert!(!hidden.manifest_complete && hidden.files.is_empty());
    assert_eq!(hidden.failure.unwrap().kind, FailureKind::Password);
    let shown = inspect_fixtures(&[("p.rar", "aes-5hp.rar")], Some("test")).await;
    assert_eq!(shown.archives[0].access(), Access::PasswordValidated);
    assert_eq!(member(&shown), data_bin);

    // RAR4 and 7z derive a key from any password; the headers then do not decode.
    for (posted, file) in [("p.rar", "aes-4hp.rar"), ("p.7z", "aes-7he.7z")] {
        let wrong = inspect_fixtures(&[(posted, file)], Some("nope")).await;
        assert_eq!(
            wrong.archives[0].access(),
            Access::PasswordRequired,
            "{file}"
        );
        assert!(!wrong.manifest_complete && !wrong.password_not_validated);
        assert_eq!(wrong.failure.unwrap().kind, FailureKind::Password, "{file}");
    }

    // RAR4 data has nothing to check a password against.
    let unchecked = inspect_fixtures(&[("p.rar", "aes-4.rar")], Some("nope")).await;
    let set = &unchecked.archives[0];
    assert_eq!(
        (set.kind, set.encryption, set.access()),
        (ArchiveType::Rar4, Encrypted::Data, Access::Unknown)
    );
    assert!(unchecked.password_not_validated && unchecked.failure.is_none());

    // Compressed members are listed, and no password makes them streamable.
    let compressed = inspect_fixtures(&[("p.7z", "7Zip.LZMA.7z")], None).await;
    let set = &compressed.archives[0];
    assert_eq!(
        (set.kind, set.access(), set.streamable()),
        (ArchiveType::SevenZip, Access::Unsupported, Some(false))
    );
    assert!(compressed.manifest_complete && !compressed.files.is_empty());
    assert_eq!(compressed.failure.unwrap().kind, FailureKind::Unsupported);
}

#[tokio::test(flavor = "multi_thread")]
async fn an_inspection_reads_remembered_misses_but_adds_none() {
    let c = post("c", "Clip.mkv", &mkv(4000, 1), 1000);
    let xml = nzb(&[("Clip.mkv", &c, 4000, 1000)]);
    // The first provider lacks the first article; the second has every one.
    let partial = LoopbackServer::start(c[1..].to_vec()).await.unwrap();
    let full = LoopbackServer::start(c.clone()).await.unwrap();
    let queue = queue_on(&[&partial, &full]).await;
    let db = queue.db().clone();
    let asked = |before: usize| partial.body_commands() - before;

    let report = inspect(&queue, &xml, None).await;
    assert!(report.manifest_complete, "{:?}", report.failure);
    assert!(
        report.missing_first_segments.is_empty(),
        "the second provider had it"
    );
    let first = asked(0);
    let before = partial.body_commands();
    inspect(&queue, &xml, None).await;
    assert_eq!(asked(before), first, "the miss was not remembered");

    // An import remembers the miss, and an inspection then skips that provider for it.
    let history = run(&queue, &db, &xml).await;
    assert_eq!(history.status, JobStatus::Completed);
    let before = partial.body_commands();
    inspect(&queue, &xml, None).await;
    assert_eq!(asked(before), first - 1, "a remembered miss is honoured");
}

#[tokio::test(flavor = "multi_thread")]
async fn an_inspection_of_a_rejected_release_fetches_nothing() {
    let c = post("c", "Clip.mkv", &mkv(4000, 1), 1000);
    let xml = nzb(&[("Clip.mkv", &c, 4000, 1000)]);
    let (server, queue) = setup(c.clone()).await;
    let ids: Vec<&str> = c.iter().map(|a| a.message_id.as_str()).collect();
    queue
        .db()
        .reject_release("other", &ids[..1], i64::MAX)
        .await
        .unwrap();
    let report = inspect(&queue, &xml, None).await;
    let failure = report.failure.expect("health replaced this release");
    assert_eq!(
        (failure.kind, failure.step),
        (import::FailureKind::Rejected, import::Step::Starting)
    );
    assert!(!report.manifest_complete);
    assert_eq!(server.body_commands(), 0, "nothing was fetched");
    assert!(queue.db().history().await.unwrap().is_empty(), "in history");
}

#[tokio::test(flavor = "multi_thread")]
async fn an_inspection_follows_an_nzb_file_like_the_import() {
    // The inner NZB opens its archive with its own password, not the request's.
    let archive = fixture("archive/tests/fixtures/aes-5.rar");
    let packed = post("a", "pack.rar", &archive, 1000);
    let release = String::from_utf8(nzb(&[("pack.rar", &packed, archive.len(), 1000)]))
        .unwrap()
        .replacen(
            "<nzb>",
            "<nzb><head><meta type=\"password\">test</meta></head>",
            1,
        );
    let mut articles = packed;
    let outer = wrap("n", release.as_bytes(), &mut articles);
    let (report, history, db) = inspect_then_import_nzb(&outer, articles, Some("wrong")).await;
    assert_eq!(
        history.status,
        JobStatus::Completed,
        "{:?}",
        history.fail_message
    );
    assert!(report.manifest_complete, "{:?}", report.failure);
    assert_eq!((report.inner_nzbs, report.failure), (1, None));
    let planned: Vec<_> = report
        .files
        .iter()
        .map(|f| (f.planned_name.clone(), f.size.unwrap()))
        .collect();
    assert_eq!(planned, published(&db).await);
    let [set] = &report.archives[..] else {
        panic!("the inner NZB's archive only")
    };
    assert_eq!(set.access(), import::Access::PasswordValidated);
}
