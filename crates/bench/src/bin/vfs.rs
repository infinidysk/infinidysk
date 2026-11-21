//! Delivery benchmarks over a loopback provider: metadata opens of a large file, plain,
//! multipart and encrypted reads through the VFS, `WebDAV` GET bodies, and playback
//! while a log burst meets a slow log destination.
//!
//! `cargo run -p infinidysk-bench --release --bin vfs -- [--scenario <substring>] [--json <path>]`
//!
//! Readers share a memory cache that the first read fills, so timed reads measure the
//! delivery path rather than yEnc decoding. Run `log-burst` on its own: it installs the
//! process-wide log subscriber.

#![allow(clippy::cast_precision_loss)] // Byte counts and durations stay far below 2^52.

#[path = "../usage.rs"]
mod usage;

use std::error::Error;
use std::fmt::Write as _;
use std::io;
use std::sync::atomic::{AtomicBool, AtomicU64, Ordering};
use std::sync::{Arc, Mutex, PoisonError, RwLock};
use std::time::{Duration, Instant};

use axum::Router;
use axum::body::Body;
use axum::http::{Request, StatusCode};
use bytes::{BufMut as _, BytesMut};
use http_body_util::BodyExt as _;
use infinidysk::logs::Capture;
use infinidysk_core::crypt::{Cipher, Crypt, Key};
use infinidysk_core::{
    BlobId, ByteRange, FileMeta, FilePart, ItemId, ItemKind, JobId, MultipartFile, NzbFile,
    Par2Proof, Segment, Segments, blob,
};
use infinidysk_db::{Db, HistoryEntry, Item, JobStatus};
use infinidysk_nntp::{Client, Downloads, Endpoint, Pool, PoolConfig, Provider, TlsMode};
use infinidysk_queue::Queue;
use infinidysk_stream::{Budget, Cache};
use infinidysk_testkit::{Article, Corpus, LoopbackServer, post};
use infinidysk_vfs::{Delivery, Node, Origin, Vfs};
use infinidysk_webdav::{Access, Auth, WebDav, router};
use tokio::io::AsyncReadExt as _;
use tokio::runtime::{Builder, Runtime};
use tower::ServiceExt as _;
use tracing_subscriber::fmt::MakeWriter;
use tracing_subscriber::layer::SubscriberExt as _;
use tracing_subscriber::util::SubscriberInitExt as _;

type Result<T> = std::result::Result<T, Box<dyn Error + Send + Sync>>;

const SEED: u64 = 1025;
const ARTICLE: usize = 768 * 1024;
/// Articles per file: about 66 MB.
const ARTICLES: usize = 84;
const VOLUMES: usize = 4;
/// Segments of the large file; only the first [`HEAD`] exist on the provider.
const BIG_SEGMENTS: usize = 100_000;
const HEAD: usize = 8;
const REPETITIONS: u32 = 3;
/// Whole-file passes per timed read or GET; one pass takes about a millisecond.
const PASSES: u32 = 40;
/// The player's pause between [`PLAYER_READ`] chunks: about 65 MB/s.
const PLAYER_PAUSE: Duration = Duration::from_millis(1);
const OPENS: usize = 20;
const CONCURRENT: usize = 32;
/// FUSE's largest read.
const FUSE_READ: usize = 1 << 20;
const PLAYER_READ: usize = 64 * 1024;
/// A log destination that takes this long per line, one line at a time.
const SLOW_SINK: Duration = Duration::from_millis(2);

const DIR: &str = "/content/bench/job";

type Metrics = Vec<(&'static str, f64)>;

const SCENARIOS: &[&str] = &[
    "open-100k-serial",
    "open-100k-concurrent32",
    "plain-read-fresh",
    "plain-read-reused",
    "parts-read-fresh",
    "parts-read-reused",
    "crypt-read-fresh",
    "crypt-read-reused",
    "webdav-get-plain",
    "webdav-get-parts",
    "webdav-get-crypt",
    "webdav-get-slow",
    "log-burst",
];

fn main() -> Result<()> {
    let (mut filter, mut json_path) = (String::new(), None);
    let mut args = std::env::args().skip(1);
    while let Some(arg) = args.next() {
        match (arg.as_str(), args.next()) {
            ("--scenario", Some(value)) => filter = value,
            ("--json", Some(value)) => json_path = Some(value),
            _ => return Err("usage: [--scenario <substring>] [--json <path>]".into()),
        }
    }
    let server_rt = Builder::new_multi_thread()
        .worker_threads(4)
        .enable_all()
        .build()?;
    let client_rt = Builder::new_multi_thread().enable_all().build()?;
    let bench = setup(&server_rt, &client_rt)?;

    let mut json = String::new();
    for name in SCENARIOS.iter().filter(|s| s.contains(filter.as_str())) {
        let metrics = client_rt.block_on(run(name, &bench))?;
        let mut line = format!("{name:<24}");
        for (key, value) in &metrics {
            let _ = write!(line, " {key}={value:.3}");
        }
        println!("{line}");
        if !json.is_empty() {
            json.push(',');
        }
        let _ = write!(json, r#""{name}":{{"#);
        for (i, (key, value)) in metrics.iter().enumerate() {
            let comma = if i == 0 { "" } else { "," };
            let _ = write!(json, r#"{comma}"{key}":{value:.3}"#);
        }
        let _ = write!(json, r#","maxRssBytes":{}}}"#, usage::max_rss_bytes());
    }
    if let Some(path) = json_path {
        let report =
            format!(r#"{{"schemaVersion":1,"report":"vfs-delivery","scenarios":{{{json}}}}}"#);
        std::fs::write(path, report + "\n")?;
    }
    Ok(())
}

async fn run(name: &str, bench: &Bench) -> Result<Metrics> {
    let path = |file: &str| format!("{DIR}/{file}");
    match name {
        "open-100k-serial" => open_serial(bench).await,
        "open-100k-concurrent32" => open_concurrent(bench).await,
        "plain-read-fresh" => read(bench, &path("plain.mkv"), &bench.plain, true).await,
        "plain-read-reused" => read(bench, &path("plain.mkv"), &bench.plain, false).await,
        "parts-read-fresh" => read(bench, &path("parts.mkv"), &bench.parts, true).await,
        "parts-read-reused" => read(bench, &path("parts.mkv"), &bench.parts, false).await,
        "crypt-read-fresh" => read(bench, &path("crypt.mkv"), &bench.crypt, true).await,
        "crypt-read-reused" => read(bench, &path("crypt.mkv"), &bench.crypt, false).await,
        "webdav-get-plain" => get(bench, &path("plain.mkv"), &bench.plain, None).await,
        "webdav-get-parts" => get(bench, &path("parts.mkv"), &bench.parts, None).await,
        "webdav-get-crypt" => get(bench, &path("crypt.mkv"), &bench.crypt, None).await,
        "webdav-get-slow" => {
            let pause = Some(Duration::from_millis(1));
            get(bench, &path("parts.mkv"), &bench.parts, pause).await
        }
        "log-burst" => log_burst(bench).await,
        _ => Err(format!("unknown scenario {name}").into()),
    }
}

struct Bench {
    _server: LoopbackServer,
    vfs: Arc<Vfs>,
    app: Router,
    plain: Vec<u8>,
    parts: Vec<u8>,
    crypt: Vec<u8>,
}

fn setup(server_rt: &Runtime, client_rt: &Runtime) -> Result<Bench> {
    let plain = Corpus::generate(SEED, ARTICLES, ARTICLE);
    let a_plain = plain.articles;
    let member = Corpus::generate(SEED + 1, ARTICLES, ARTICLE).data;
    let volume_len = member.len() / VOLUMES;
    let volumes: Vec<Vec<Article>> = member
        .chunks(volume_len)
        .enumerate()
        .map(|(i, data)| post(&format!("vol{i}"), &format!("b.part{i}.rar"), data, ARTICLE))
        .collect();
    let parts: Vec<FilePart> = member
        .chunks(volume_len)
        .zip(&volumes)
        .enumerate()
        .map(|(i, (data, articles))| FilePart {
            segments: segments(articles, data.len()),
            offset: 0,
            data: ByteRange {
                start: 0,
                end: data.len() as u64,
            },
            split_after: i + 1 < VOLUMES,
            proof: None,
        })
        .collect();
    // The member's bytes read as AES ciphertext, trimmed short of a whole block.
    let crypt = Crypt {
        key: Key::Aes256([9; 32]),
        iv: [3; 16],
        size: member.len() as u64 - 7,
    };
    let mut decrypted = member.clone();
    Cipher::new(&crypt.key).decrypt(&mut crypt.iv.clone(), &mut decrypted);
    decrypted.truncate(member.len() - 7);

    let server = server_rt.block_on(LoopbackServer::start(
        a_plain.iter().cloned().chain(volumes.concat()),
    ))?;
    let port = server.addr().port();
    let metas = [
        (
            "plain.mkv",
            FileMeta::Nzb(NzbFile {
                segments: segments(&a_plain, plain.data.len()),
                proof: None,
            }),
        ),
        (
            "parts.mkv",
            FileMeta::Multipart(MultipartFile {
                parts: parts.clone(),
                crypt: None,
            }),
        ),
        (
            "crypt.mkv",
            FileMeta::Multipart(MultipartFile {
                parts,
                crypt: Some(crypt),
            }),
        ),
        ("big.mkv", big(&a_plain[..HEAD])),
    ];
    let (vfs, app) = client_rt.block_on(seed(port, &metas))?;
    Ok(Bench {
        _server: server,
        vfs,
        app,
        plain: plain.data,
        parts: member,
        crypt: decrypted,
    })
}

fn segments(articles: &[Article], len: usize) -> Segments {
    Segments {
        items: articles
            .iter()
            .enumerate()
            .map(|(i, a)| Segment {
                id: a.message_id.clone(),
                size: u32::try_from(ARTICLE.min(len - i * ARTICLE)).unwrap_or(u32::MAX),
                fallbacks: Vec::new(),
            })
            .collect(),
        sizes_trusted: true,
        ..Segments::default()
    }
}

/// A [`BIG_SEGMENTS`]-segment file with message-ids and a PAR2 proof shaped like a
/// real release's; its first segments are `head`.
fn big(head: &[Article]) -> FileMeta {
    let mut state = SEED;
    let mut next = || {
        state = state.wrapping_add(0x9E37_79B9_7F4A_7C15);
        let z = (state ^ (state >> 30)).wrapping_mul(0xBF58_476D_1CE4_E5B9);
        (z ^ (z >> 27)).wrapping_mul(0x94D0_49BB_1331_11EB)
    };
    let items: Vec<Segment> = (0..BIG_SEGMENTS)
        .map(|i| Segment {
            id: head.get(i).map_or_else(
                || format!("{i:08x}{:016x}{:08x}@ngpost.example", next(), next() >> 32),
                |a| a.message_id.clone(),
            ),
            size: u32::try_from(ARTICLE).unwrap_or(u32::MAX),
            fallbacks: Vec::new(),
        })
        .collect();
    let len = (BIG_SEGMENTS * ARTICLE) as u64;
    let slice_size = 10_000_000_u32;
    let slices = len.div_ceil(u64::from(slice_size));
    let mut md5 = || {
        let mut out = [0; 16];
        out[..8].copy_from_slice(&next().to_le_bytes());
        out[8..].copy_from_slice(&next().to_le_bytes());
        out
    };
    let proof = Par2Proof {
        set_id: None,
        file_length: len,
        slice_size,
        slice_md5: (0..slices).map(|_| md5()).collect(),
        slice_crc32: (0..slices)
            .map(|i| u32::try_from(i).unwrap_or(0).wrapping_mul(2_654_435_761))
            .collect(),
        file_id: md5(),
        file_md5: md5(),
        md5_16k: md5(),
    };
    FileMeta::Nzb(NzbFile {
        segments: Segments {
            items,
            sizes_trusted: true,
            ..Segments::default()
        },
        proof: Some(proof),
    })
}

async fn seed(port: u16, metas: &[(&str, FileMeta)]) -> Result<(Arc<Vfs>, Router)> {
    let endpoint = Endpoint::new(
        "127.0.0.1",
        port,
        TlsMode::Plain,
        None,
        Duration::from_secs(30),
    )?;
    let config = PoolConfig {
        max_connections: 20,
        pipeline_depth: 4,
        idle_timeout: Duration::from_secs(60),
        min_connections: 0,
    };
    let pool = Pool::new(endpoint, config, Downloads::new(80))?;
    let client = Arc::new(Client::new([Provider {
        pool,
        key: String::new(),
        name: String::new(),
        priority: 0,
        backbone: None,
    }]));

    let path = std::env::temp_dir().join(format!("infinidysk-bench-vfs-{}.sqlite", ItemId::new()));
    let db = Db::connect(&format!("sqlite:{}", path.display())).await?;
    let content = db
        .item(ItemId::CONTENT)
        .await?
        .ok_or("/content is missing")?;
    let category = Item::child(&content, "bench", ItemKind::Directory)?;
    let job = Item::child(&category, "job", ItemKind::Directory)?;
    let mut items = vec![job.clone()];
    let mut blobs = Vec::new();
    for (name, meta) in metas {
        let kind = match meta {
            FileMeta::Nzb(_) => ItemKind::NzbFile,
            FileMeta::Multipart(_) => ItemKind::Multipart,
        };
        let mut item = Item::child(&job, name, kind)?;
        let id = BlobId::new();
        item.size = Some(meta.size());
        item.meta_blob = Some(id);
        blobs.push((id, blob::encode_meta(meta)?));
        items.push(item);
    }
    let blob_refs: Vec<(BlobId, &[u8])> = blobs.iter().map(|(id, b)| (*id, b.as_slice())).collect();
    db.insert(std::slice::from_ref(&category), &[]).await?;
    let history = HistoryEntry {
        id: JobId::new(),
        created_at: 1,
        file_name: "job.nzb".into(),
        job_name: "job".into(),
        category: "bench".into(),
        status: JobStatus::Completed,
        total_bytes: 0,
        download_seconds: 0,
        fail_message: None,
        download_dir: Some(job.id),
        nzb_blob: None,
        storage: None,
    };
    db.add_history(&history, &items, &blob_refs).await?;

    // Read-ahead and cache as the app's defaults would size them for this corpus.
    let cache = Arc::new(Cache::new(true, 1_000_000_000));
    let vfs = Arc::new(
        Vfs::new(
            db.clone(),
            Arc::clone(&client),
            Budget::new(300_000_000),
            60_000_000,
            "/mnt",
        )
        .with_cache(cache),
    );
    let dav = WebDav {
        vfs: Arc::clone(&vfs),
        queue: Arc::new(Queue::new(db, client)),
        access: RwLock::new(Access {
            auth: Auth::Disabled,
            download_key: None,
            link_key: None,
            strm_key: None,
            previous_strm: Vec::new(),
            legacy_strm: None,
            trusted_proxies: Vec::new(),
        }),
    };
    Ok((vfs, router(Arc::new(dav))))
}

fn origin() -> Origin {
    Origin {
        delivery: Delivery::Fuse,
        client: "bench".into(),
        player: None,
    }
}

fn ms(since: Instant) -> f64 {
    since.elapsed().as_secs_f64() * 1000.0
}

/// Median and 95th percentile.
fn percentiles(mut values: Vec<f64>) -> (f64, f64) {
    values.sort_by(f64::total_cmp);
    let last = values.len().saturating_sub(1);
    (values[last / 2], values[last * 95 / 100])
}

async fn lookup(vfs: &Vfs, path: &str) -> Result<Node> {
    Ok(vfs.lookup(path).await?.ok_or("file is missing")?)
}

/// Each open fetches, decodes and lays out the metadata, then reads one player chunk.
async fn open_serial(bench: &Bench) -> Result<Metrics> {
    let node = lookup(&bench.vfs, &format!("{DIR}/big.mkv")).await?;
    let Node::Item(item) = &node else {
        return Err("big.mkv is not an item".into());
    };
    let id = item.meta_blob.ok_or("big.mkv has no metadata")?;
    let mut buf = vec![0; PLAYER_READ];
    let (mut fetch, mut decode, mut open, mut first) = (vec![], vec![], vec![], vec![]);
    let mut blob_bytes = 0;
    for _ in 0..=OPENS {
        let start = Instant::now();
        let raw = bench.vfs.db().blob(id).await?.ok_or("blob is missing")?;
        fetch.push(ms(start));
        blob_bytes = raw.len();
        let start = Instant::now();
        drop(blob::decode_meta(&raw)?);
        decode.push(ms(start));
        let start = Instant::now();
        let mut handle = bench.vfs.open(&node, origin()).await?.ok_or("not a file")?;
        open.push(ms(start));
        handle.read_exact(&mut buf).await?;
        first.push(ms(start));
    }
    // The first round fetched the head from the provider; later ones hit the cache.
    let warm = |mut v: Vec<f64>| {
        v.remove(0);
        percentiles(v)
    };
    let (fetch, decode, open, first) = (warm(fetch), warm(decode), warm(open), warm(first));
    Ok(vec![
        ("blobBytes", blob_bytes as f64),
        ("blobFetchP50Ms", fetch.0),
        ("decodeP50Ms", decode.0),
        ("openP50Ms", open.0),
        ("openP95Ms", open.1),
        ("firstChunkP50Ms", first.0),
        ("firstChunkP95Ms", first.1),
    ])
}

/// [`CONCURRENT`] readers open the file at once, as a library scan would.
async fn open_concurrent(bench: &Bench) -> Result<Metrics> {
    let node = lookup(&bench.vfs, &format!("{DIR}/big.mkv")).await?;
    let mut rounds = Vec::new();
    let mut opens = Vec::new();
    let cpu = usage::cpu();
    for _ in 0..REPETITIONS {
        let start = Instant::now();
        let mut tasks = tokio::task::JoinSet::new();
        for _ in 0..CONCURRENT {
            let (vfs, node) = (Arc::clone(&bench.vfs), node.clone());
            tasks.spawn(async move {
                let start = Instant::now();
                let mut handle = vfs.open(&node, origin()).await?.ok_or("not a file")?;
                let open = ms(start);
                let mut buf = vec![0; PLAYER_READ];
                handle.read_exact(&mut buf).await?;
                Ok::<_, Box<dyn Error + Send + Sync>>((open, ms(start)))
            });
        }
        while let Some(result) = tasks.join_next().await {
            opens.push(result??);
        }
        rounds.push(ms(start));
    }
    let cpu_ms = usage::cpu_ms_since(cpu);
    let (open, first) = (
        percentiles(opens.iter().map(|o| o.0).collect()),
        percentiles(opens.iter().map(|o| o.1).collect()),
    );
    Ok(vec![
        (
            "wallMs",
            rounds.iter().sum::<f64>() / f64::from(REPETITIONS),
        ),
        ("openP50Ms", open.0),
        ("openP95Ms", open.1),
        ("firstChunkP50Ms", first.0),
        ("firstChunkP95Ms", first.1),
        ("cpuMsPerOpen", cpu_ms / opens.len() as f64),
    ])
}

/// Reads the whole file: `fresh` like FUSE, a new [`FUSE_READ`] buffer per request;
/// otherwise one reused buffer.
async fn read(bench: &Bench, path: &str, expected: &[u8], fresh: bool) -> Result<Metrics> {
    let node = lookup(&bench.vfs, path).await?;
    let mut data = Vec::new();
    bench
        .vfs
        .open(&node, origin())
        .await?
        .ok_or("not a file")?
        .read_to_end(&mut data)
        .await?;
    if data != expected {
        return Err(format!("{path} read back different bytes").into());
    }
    let (mut wall, mut cpu_ms) = (0.0, 0.0);
    let mut reused = vec![0; FUSE_READ];
    for _ in 0..REPETITIONS * PASSES {
        let mut handle = bench.vfs.open(&node, origin()).await?.ok_or("not a file")?;
        let (start, cpu) = (Instant::now(), usage::cpu());
        let mut total = 0;
        loop {
            let n = if fresh {
                let mut buf = BytesMut::with_capacity(FUSE_READ);
                while buf.len() < FUSE_READ {
                    let want = FUSE_READ - buf.len();
                    if handle.read_buf(&mut (&mut buf).limit(want)).await? == 0 {
                        break;
                    }
                }
                buf.len()
            } else {
                handle.read(&mut reused).await?
            };
            if n == 0 {
                break;
            }
            total += n;
        }
        if total != expected.len() {
            return Err(format!("{path} read {total} of {} bytes", expected.len()).into());
        }
        wall += start.elapsed().as_secs_f64();
        cpu_ms += usage::cpu_ms_since(cpu);
    }
    let mb = expected.len() as f64 / 1e6 * f64::from(REPETITIONS * PASSES);
    Ok(vec![
        ("throughputMbps", mb / wall),
        ("cpuMsPerMb", cpu_ms / mb),
    ])
}

/// GETs the whole file, pausing `pause` after each body chunk like a slow client.
async fn get(
    bench: &Bench,
    uri: &str,
    expected: &[u8],
    pause: Option<Duration>,
) -> Result<Metrics> {
    let (mut wall, mut cpu_ms, mut frames, mut smallest) = (0.0, 0.0, 0_usize, usize::MAX);
    // A slow client takes long enough per GET already.
    let rounds = if pause.is_some() {
        REPETITIONS
    } else {
        REPETITIONS * PASSES
    };
    for round in 0..=rounds {
        let (start, cpu) = (Instant::now(), usage::cpu());
        let request = Request::get(uri).body(Body::empty())?;
        let response = bench.app.clone().oneshot(request).await?;
        if response.status() != StatusCode::OK {
            return Err(format!("GET {uri}: {}", response.status()).into());
        }
        let mut body = response.into_body();
        let (mut got, mut received, mut count, mut least) = (Vec::new(), 0, 0, usize::MAX);
        while let Some(frame) = body.frame().await {
            if let Ok(data) = frame?.into_data() {
                count += 1;
                least = least.min(data.len());
                received += data.len();
                // Only the correctness round keeps the bytes.
                if round == 0 {
                    got.extend_from_slice(&data);
                }
                if let Some(pause) = pause {
                    tokio::time::sleep(pause).await;
                }
            }
        }
        if round == 0 {
            if got != expected {
                return Err(format!("GET {uri} returned different bytes").into());
            }
            continue;
        }
        if received != expected.len() {
            return Err(format!("GET {uri} returned {received} bytes").into());
        }
        wall += start.elapsed().as_secs_f64();
        cpu_ms += usage::cpu_ms_since(cpu);
        frames += count;
        smallest = smallest.min(least);
    }
    let mb = expected.len() as f64 / 1e6 * f64::from(rounds);
    Ok(vec![
        ("throughputMbps", mb / wall),
        ("cpuMsPerMb", cpu_ms / mb),
        ("framesPerGet", frames as f64 / f64::from(rounds)),
        (
            "meanFrameBytes",
            expected.len() as f64 * f64::from(rounds) / frames as f64,
        ),
        ("smallestFrameBytes", smallest as f64),
    ])
}

/// One line at a time, [`SLOW_SINK`] each: a log pipe its reader drains slowly.
#[derive(Clone, Default)]
struct SlowSink(Arc<Mutex<()>>);

impl io::Write for &SlowSink {
    fn write(&mut self, buf: &[u8]) -> io::Result<usize> {
        let held = self.0.lock().unwrap_or_else(PoisonError::into_inner);
        std::thread::sleep(SLOW_SINK);
        drop(held);
        Ok(buf.len())
    }

    fn flush(&mut self) -> io::Result<()> {
        Ok(())
    }
}

impl<'a> MakeWriter<'a> for SlowSink {
    type Writer = &'a Self;

    fn make_writer(&'a self) -> Self::Writer {
        self
    }
}

/// Reads the plain file quietly, then again while every worker's worth of tasks logs
/// warnings to stdout through [`SlowSink`] and to the log file, as during an outage.
async fn log_burst(bench: &Bench) -> Result<Metrics> {
    let dir = std::env::temp_dir().join(format!("infinidysk-bench-logs-{}", ItemId::new()));
    tracing_subscriber::registry()
        .with(infinidysk::logs::filter(Some("info")))
        .with(tracing_subscriber::fmt::layer().with_writer(SlowSink::default()))
        .with(Capture::persisted(&dir, (0, 0))?)
        .try_init()?;
    let node = lookup(&bench.vfs, &format!("{DIR}/plain.mkv")).await?;
    // Fills the cache so both timed reads are served alike.
    paced_read(bench, &node).await?;
    let quiet = paced_read(bench, &node).await?;

    let stop = Arc::new(AtomicBool::new(false));
    let events = Arc::new(AtomicU64::new(0));
    let workers = std::thread::available_parallelism().map_or(4, usize::from);
    let mut tasks = tokio::task::JoinSet::new();
    for _ in 0..workers {
        let (stop, events) = (Arc::clone(&stop), Arc::clone(&events));
        tasks.spawn(async move {
            while !stop.load(Ordering::Relaxed) {
                tracing::warn!(
                    reason = "connection reset",
                    provider = "bench",
                    "provider request failed"
                );
                events.fetch_add(1, Ordering::Relaxed);
                tokio::time::sleep(Duration::from_millis(1)).await;
            }
        });
    }
    let burst = paced_read(bench, &node).await;
    stop.store(true, Ordering::Relaxed);
    while tasks.join_next().await.is_some() {}
    let burst = burst?;
    let _ = std::fs::remove_dir_all(&dir);
    Ok(vec![
        ("quietThroughputMbps", quiet.0),
        ("quietLongestGapMs", quiet.1),
        ("burstThroughputMbps", burst.0),
        ("burstLongestGapMs", burst.1),
        ("events", events.load(Ordering::Relaxed) as f64),
    ])
}

/// MB/s and the longest wait for one [`PLAYER_READ`] chunk, pausing [`PLAYER_PAUSE`]
/// between chunks like a player.
async fn paced_read(bench: &Bench, node: &Node) -> Result<(f64, f64)> {
    let mut handle = bench.vfs.open(node, origin()).await?.ok_or("not a file")?;
    let mut buf = vec![0; PLAYER_READ];
    let (start, mut total, mut longest) = (Instant::now(), 0, 0.0_f64);
    loop {
        let at = Instant::now();
        let n = handle.read(&mut buf).await?;
        if n == 0 {
            break;
        }
        longest = longest.max(ms(at));
        total += n;
        tokio::time::sleep(PLAYER_PAUSE).await;
    }
    Ok((total as f64 / 1e6 / start.elapsed().as_secs_f64(), longest))
}
