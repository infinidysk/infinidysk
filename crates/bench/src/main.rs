//! Streaming benchmarks against an impaired loopback provider. Scenario and metric
//! names match the original suite so results compare against its baselines.
//!
//! `cargo run -p infinidysk-bench --release -- [--scenario <substring>] [--json <path>] [--trace]`
//!
//! `--trace` traces each timed read and prints where the last one stalled.

#![allow(clippy::cast_precision_loss)] // Byte counts stay far below 2^52.

mod repair_bench;
mod usage;

use std::error::Error;
use std::fmt::Write as _;
use std::io::SeekFrom;
use std::sync::Arc;
use std::time::{Duration, Instant};

use infinidysk_core::trace::{Summary, Trace};
use infinidysk_nntp::{
    Client, Credentials, Downloads, Endpoint, Pool, PoolConfig, Provider, TlsMode,
};
use infinidysk_stream::{Layout, Segment, SegmentReader};
use infinidysk_testkit::{Corpus, Impairment, LoopbackServer};
use tokio::io::{AsyncReadExt, AsyncSeekExt};
use tokio::runtime::{Builder, Runtime};

const SEED: u64 = 1025;
const READ_SIZE: usize = 64 * 1024;
const WINDOW: Duration = Duration::from_millis(250);
const REPETITIONS: u32 = 3;
const KIB: usize = 1024;
const MIB: usize = 1024 * KIB;

/// A remote provider: slow handshakes, per-response latency, per-connection bandwidth.
const REMOTE: Impairment = Impairment {
    handshake_delay: Duration::from_millis(150),
    response_delay: Duration::from_millis(40),
    bandwidth: Some(6_000_000),
};
/// Like [`REMOTE`] with handshakes already done by the warmup.
const PACED: Impairment = Impairment {
    handshake_delay: Duration::ZERO,
    ..REMOTE
};
const PLAIN: Impairment = Impairment {
    handshake_delay: Duration::ZERO,
    response_delay: Duration::ZERO,
    bandwidth: None,
};

struct Scenario {
    name: &'static str,
    articles: usize,
    article_size: usize,
    connections: usize,
    width: usize,
    /// Reader window in segments; 0 derives it from read-ahead like the app does.
    prefetch: usize,
    impairment: Impairment,
    /// Timed runs reuse the warmup's open connections.
    warm: bool,
    /// The reader opens connections for its window alongside the first read.
    prewarm: bool,
    /// Original suite's throughput baseline in MB/s.
    baseline_mbps: Option<f64>,
}

const fn cold(name: &'static str, prewarm: bool, baseline_mbps: f64) -> Scenario {
    Scenario {
        name,
        articles: 342,
        article_size: 768 * KIB,
        connections: 20,
        width: 4,
        prefetch: 40,
        impairment: REMOTE,
        warm: false,
        prewarm,
        baseline_mbps: Some(baseline_mbps),
    }
}

const fn paced(name: &'static str, width: usize, connections: usize) -> Scenario {
    Scenario {
        impairment: PACED,
        warm: true,
        prewarm: true,
        connections,
        width,
        baseline_mbps: None,
        ..cold(name, true, 0.0)
    }
}

/// Unimpaired loopback: measures client CPU cost rather than provider pacing.
const fn plain(
    name: &'static str,
    articles: usize,
    article_size: usize,
    connections: usize,
    width: usize,
    prefetch: usize,
    baseline_mbps: Option<f64>,
) -> Scenario {
    Scenario {
        name,
        articles,
        article_size,
        connections,
        width,
        prefetch,
        impairment: PLAIN,
        warm: true,
        prewarm: true,
        baseline_mbps,
    }
}

/// Paced provider with the app's window: `BENCH_READ_AHEAD_MB` (default 60) / article.
const fn prod(name: &'static str, articles: usize, article_size: usize) -> Scenario {
    Scenario {
        articles,
        article_size,
        prefetch: 0,
        ..paced(name, 4, 20)
    }
}

impl Scenario {
    fn prefetch(&self) -> usize {
        if self.prefetch > 0 {
            return self.prefetch;
        }
        let mb: usize = std::env::var("BENCH_READ_AHEAD_MB")
            .ok()
            .and_then(|v| v.parse().ok())
            .unwrap_or(60);
        (mb * 1_000_000 / self.article_size).max(2)
    }
}

// The original suite's sustained baselines are unrunnable placeholders, so only the
// quick scenario carries one.
const SCENARIOS: &[Scenario] = &[
    prod("prod-4mib", 64, 4 * MIB),
    prod("prod-722kib", 363, 722 * KIB),
    cold("cold-ramp-256mib-w4", false, 36.263),
    cold("cold-ramp-256mib-w4-prewarm", true, 60.231),
    paced("paced-256mib-w1", 1, 20),
    paced("paced-256mib-w4", 4, 20),
    paced("paced-256mib-w8", 8, 20),
    paced("paced-256mib-w4-4conn", 4, 4),
    paced("paced-256mib-w4-40conn", 4, 40),
    plain("plain-http-like-w4", 8, 256 * KIB, 4, 4, 32, Some(359.673)),
    plain("profile-plain-http-like-w4", 64, 4 * MIB, 20, 4, 32, None),
    plain("sustained-plain-buffered-w1", 256, 4 * MIB, 20, 1, 16, None),
    plain("sustained-plain-buffered-w2", 256, 4 * MIB, 20, 2, 32, None),
    plain("sustained-plain-buffered-w4", 256, 4 * MIB, 20, 4, 64, None),
    plain(
        "sustained-plain-buffered-w8",
        256,
        4 * MIB,
        20,
        8,
        128,
        None,
    ),
];

fn main() -> Result<(), Box<dyn Error>> {
    let (mut filter, mut json_path, mut trace) = (String::new(), None, false);
    let mut args = std::env::args().skip(1);
    while let Some(arg) = args.next() {
        if arg == "--trace" {
            trace = true;
            continue;
        }
        match (arg.as_str(), args.next()) {
            ("--scenario", Some(value)) => filter = value,
            ("--json", Some(value)) => json_path = Some(value),
            _ => return Err("usage: [--scenario <substring>] [--json <path>] [--trace]".into()),
        }
    }

    // Separate runtimes so server pacing never competes with client tasks for workers.
    let server_rt = Builder::new_multi_thread()
        .worker_threads(4)
        .enable_all()
        .build()?;
    let client_rt = Builder::new_multi_thread().enable_all().build()?;
    if filter.starts_with("par2-") {
        return repair_bench::run(&filter, json_path.as_deref(), &server_rt, &client_rt);
    }

    let mut json = String::new();
    let mut shape = None;
    for scenario in SCENARIOS.iter().filter(|s| s.name.contains(&filter)) {
        // Scenarios sharing a shape share one corpus; they are listed together.
        let key = (scenario.articles, scenario.article_size);
        let (corpus, layout) = match &shape {
            Some((cached, corpus, layout)) if *cached == key => (corpus, layout),
            _ => {
                let (corpus, layout) = generate(scenario);
                let (_, corpus, layout) = shape.insert((key, corpus, layout));
                (&*corpus, &*layout)
            }
        };
        let result = run(scenario, &server_rt, &client_rt, corpus, layout, trace)?;
        print_line(scenario, &result);
        if let Some(summary) = &result.trace {
            print_trace(summary);
        }
        if !json.is_empty() {
            json.push(',');
        }
        result.write_json(scenario.name, &mut json);
    }
    if let Some(path) = json_path {
        let report =
            format!(r#"{{"schemaVersion":1,"report":"nntp-whole-path","scenarios":{{{json}}}}}"#);
        std::fs::write(path, report + "\n")?;
    }
    if SEEK_SCENARIO.contains(filter.as_str()) {
        seeks(&server_rt, &client_rt)?;
    }
    Ok(())
}

const SEEK_SCENARIO: &str = "seek-8mb-w4";
/// The streaming harness's seek points, in permille of the file, each read on a fresh
/// reader that is dropped with its window still in flight.
const SEEK_POINTS: [u64; 6] = [10, 250, 500, 750, 950, 100];
const SEEK_BYTES: u64 = 8_000_000;

/// Prints median and worst time to first byte and to [`SEEK_BYTES`] across seeks, for
/// open-ended reads and for reads bounded to [`SEEK_BYTES`].
fn seeks(server_rt: &Runtime, client_rt: &Runtime) -> Result<(), Box<dyn Error>> {
    // 32 MiB read-ahead over 768 KiB segments, as the default preset gives.
    let scenario = Scenario {
        prefetch: 42,
        ..paced(SEEK_SCENARIO, 4, 20)
    };
    let (corpus, layout) = generate(&scenario);
    let server = server_rt.block_on(LoopbackServer::start_impaired(
        corpus.articles,
        scenario.impairment,
    ))?;
    let client = client(&scenario, server.addr().port())?;
    for bounded in [false, true] {
        let (mut first, mut full) = (Vec::new(), Vec::new());
        client_rt.block_on(async {
            seek_read(&client, &layout, scenario.prefetch(), 0, bounded).await?;
            for _ in 0..REPETITIONS {
                for permille in SEEK_POINTS {
                    let offset = layout.len() * permille / 1000;
                    let (a, b) =
                        seek_read(&client, &layout, scenario.prefetch(), offset, bounded).await?;
                    first.push(a);
                    full.push(b);
                }
            }
            Ok::<_, std::io::Error>(())
        })?;
        let stat = |v: &mut Vec<f64>| {
            v.sort_by(f64::total_cmp);
            (v[v.len() / 2], v[v.len() - 1])
        };
        let ((first_p50, first_max), (full_p50, full_max)) = (stat(&mut first), stat(&mut full));
        let name = format!("{SEEK_SCENARIO}{}", if bounded { "-bounded" } else { "" });
        println!(
            "{name:<24} ttfb p50={first_p50:.0}ms max={first_max:.0}ms  8MB p50={full_p50:.0}ms max={full_max:.0}ms"
        );
    }
    Ok(())
}

/// Milliseconds to the first byte and to [`SEEK_BYTES`] read from `offset`.
async fn seek_read(
    client: &Arc<Client>,
    layout: &Arc<Layout>,
    prefetch: usize,
    offset: u64,
    bounded: bool,
) -> std::io::Result<(f64, f64)> {
    let mut reader = SegmentReader::new(Arc::clone(client), Arc::clone(layout), prefetch);
    if bounded {
        reader.set_end(offset + SEEK_BYTES);
    }
    reader.seek(SeekFrom::Start(offset)).await?;
    let start = Instant::now();
    let (mut buf, mut total, mut first) = (vec![0; READ_SIZE], 0, None);
    while total < SEEK_BYTES {
        let n = reader.read(&mut buf).await?;
        if n == 0 {
            break;
        }
        first.get_or_insert_with(|| start.elapsed());
        total += n as u64;
    }
    drop(reader);
    let ms = |d: Duration| d.as_secs_f64() * 1000.0;
    Ok((ms(first.unwrap_or_default()), ms(start.elapsed())))
}

struct Outcome {
    expected_bytes: usize,
    actual_bytes: usize,
    data_match: bool,
    body_commands: usize,
    timing: Timing,
    /// Process peak so far: compare it only across single-scenario runs.
    max_rss_bytes: u64,
    /// The last timed read's, with `--trace`.
    trace: Option<Summary>,
}

fn generate(scenario: &Scenario) -> (Corpus, Arc<Layout>) {
    let corpus = Corpus::generate(SEED, scenario.articles, scenario.article_size);
    let layout = Layout::new(
        corpus
            .articles
            .iter()
            .map(|a| Segment {
                message_id: a.message_id.clone(),
                size: scenario.article_size as u64,
                begin: None,
            })
            .collect(),
    );
    (corpus, Arc::new(layout))
}

#[allow(clippy::significant_drop_tightening)] // False positive: the client Arcs move into `read`.
fn run(
    scenario: &Scenario,
    server_rt: &Runtime,
    client_rt: &Runtime,
    corpus: &Corpus,
    layout: &Arc<Layout>,
    trace: bool,
) -> Result<Outcome, Box<dyn Error>> {
    let server = server_rt.block_on(LoopbackServer::start_impaired(
        corpus.articles.clone(),
        scenario.impairment,
    ))?;
    let port = server.addr().port();
    client_rt.block_on(async {
        // Warmup doubles as the correctness run.
        let warm = client(scenario, port)?;
        let (data, _) = read(
            scenario,
            Arc::clone(&warm),
            Arc::clone(layout),
            true,
            true,
            None,
        )
        .await?;
        let warm = scenario.warm.then_some(warm);
        let bodies_before = server.body_commands();
        let mut total = Timing::default();
        let mut summary = None;
        for _ in 0..REPETITIONS {
            let client = warm
                .as_ref()
                .map_or_else(|| client(scenario, port), |c| Ok(Arc::clone(c)))?;
            // Room for every event, so the summary covers the whole read.
            let traced = trace.then(|| Arc::new(Trace::new(scenario.articles * 16 + 64)));
            let cpu = usage::cpu();
            let (_, mut timing) = read(
                scenario,
                client,
                Arc::clone(layout),
                scenario.prewarm,
                false,
                traced.clone(),
            )
            .await?;
            timing.cpu_ms_per_mb = usage::cpu_ms_since(cpu) / (corpus.data.len() as f64 / 1e6);
            total.add(&timing);
            summary = traced.map(|t| t.summary());
        }
        total.divide(REPETITIONS);
        Ok::<_, Box<dyn Error>>(Outcome {
            expected_bytes: corpus.data.len(),
            actual_bytes: data.len(),
            data_match: data == corpus.data,
            body_commands: (server.body_commands() - bodies_before) / REPETITIONS as usize,
            timing: total,
            max_rss_bytes: usage::max_rss_bytes(),
            trace: summary,
        })
    })
}

fn client(scenario: &Scenario, port: u16) -> Result<Arc<Client>, infinidysk_nntp::Error> {
    let credentials = Credentials {
        username: "benchmark".to_owned(),
        password: "benchmark".to_owned(),
    };
    let endpoint = Endpoint::new(
        "127.0.0.1",
        port,
        TlsMode::Plain,
        Some(credentials),
        Duration::from_secs(30),
    )?;
    let config = PoolConfig {
        max_connections: scenario.connections,
        pipeline_depth: scenario.width,
        idle_timeout: Duration::from_secs(60),
        min_connections: 0,
    };
    let downloads = Downloads::new(scenario.connections * scenario.width);
    let pool = Pool::new(endpoint, config, downloads)?;
    let provider = Provider {
        pool,
        key: String::new(),
        name: String::new(),
        priority: 0,
        backbone: None,
    };
    let client = Client::new([provider]);
    // Clones every body like the app's cache-warming sink does for queue and drained bodies.
    if std::env::var_os("BENCH_SINK").is_some() {
        client.set_sink(Box::new(|_, _, body, _| {
            std::hint::black_box(body.data.clone());
        }));
    }
    Ok(Arc::new(client))
}

/// Reads the whole file in player-sized chunks, recording when each chunk arrived.
#[allow(clippy::significant_drop_tightening)] // False positive: the reader is used until EOF.
async fn read(
    scenario: &Scenario,
    client: Arc<Client>,
    layout: Arc<Layout>,
    prewarm: bool,
    keep: bool,
    trace: Option<Arc<Trace>>,
) -> std::io::Result<(Vec<u8>, Timing)> {
    let start = Instant::now();
    let reader = SegmentReader::new(client, layout, scenario.prefetch()).with_prewarm(prewarm);
    let mut reader = match trace {
        Some(trace) => reader.with_trace(trace),
        None => reader,
    };
    let mut buf = vec![0; READ_SIZE];
    let (mut kept, mut points, mut total) = (Vec::new(), Vec::new(), 0_u64);
    loop {
        let n = reader.read(&mut buf).await?;
        if n == 0 {
            break;
        }
        total += n as u64;
        points.push((start.elapsed(), total));
        if keep {
            kept.extend_from_slice(&buf[..n]);
        }
    }
    Ok((kept, Timing::from_points(&points, start.elapsed())))
}

#[derive(Default, Clone, Copy)]
struct Timing {
    wall_seconds: f64,
    throughput_mbps: f64,
    time_to_8mb_ms: f64,
    time_to_64mb_ms: f64,
    longest_read_gap_ms: f64,
    p05_window_mbps: f64,
    p50_window_mbps: f64,
    /// Process CPU (user + system) per decimal MB delivered.
    cpu_ms_per_mb: f64,
}

impl Timing {
    /// `points` are `(since request, cumulative bytes)` per completed read.
    fn from_points(points: &[(Duration, u64)], wall: Duration) -> Self {
        let Some(&(end, bytes)) = points.last() else {
            return Self::default();
        };
        let ms = |d: Duration| d.as_secs_f64() * 1000.0;
        let time_to = |target| {
            points
                .iter()
                .find(|&&(_, cumulative)| cumulative >= target)
                .map_or(0.0, |&(at, _)| ms(at))
        };
        let longest_gap = points
            .windows(2)
            .map(|pair| pair[1].0.saturating_sub(pair[0].0))
            .max()
            .unwrap_or_default();

        // Windows start at the request, so time before the first byte counts as zero
        // rate; a trailing window under half length is dropped unless it is the only one.
        let mut rates = Vec::new();
        let (mut index, mut previous, mut window_end) = (0, 0, WINDOW);
        loop {
            let last = window_end >= end;
            let boundary = if last { end } else { window_end };
            let mut at = previous;
            while let Some(&(time, cumulative)) = points.get(index)
                && time <= boundary
            {
                at = cumulative;
                index += 1;
            }
            let span = boundary.saturating_sub(window_end.saturating_sub(WINDOW));
            if !last || span * 2 >= WINDOW || rates.is_empty() {
                let seconds = span.as_secs_f64().max(1e-9);
                rates.push((at - previous) as f64 / seconds / 1e6);
            }
            previous = at;
            if last {
                break;
            }
            window_end += WINDOW;
        }
        rates.sort_by(f64::total_cmp);
        let last = rates.len() - 1;

        Self {
            wall_seconds: wall.as_secs_f64(),
            throughput_mbps: bytes as f64 / wall.as_secs_f64() / 1e6,
            time_to_8mb_ms: time_to(8_000_000),
            time_to_64mb_ms: time_to(64_000_000),
            longest_read_gap_ms: ms(longest_gap),
            p05_window_mbps: rates[last * 5 / 100],
            p50_window_mbps: rates[last / 2],
            cpu_ms_per_mb: 0.0,
        }
    }

    fn fields(&mut self) -> [&mut f64; 8] {
        [
            &mut self.wall_seconds,
            &mut self.throughput_mbps,
            &mut self.time_to_8mb_ms,
            &mut self.time_to_64mb_ms,
            &mut self.longest_read_gap_ms,
            &mut self.p05_window_mbps,
            &mut self.p50_window_mbps,
            &mut self.cpu_ms_per_mb,
        ]
    }

    fn add(&mut self, other: &Self) {
        let mut other = *other;
        for (sum, value) in self.fields().into_iter().zip(other.fields()) {
            *sum += *value;
        }
    }

    fn divide(&mut self, count: u32) {
        for value in self.fields() {
            *value /= f64::from(count);
        }
    }
}

impl Outcome {
    fn write_json(&self, name: &str, out: &mut String) {
        let t = &self.timing;
        // `sha256Match` keeps the original key; the bytes are compared directly.
        let _ = write!(
            out,
            r#""{name}":{{"deterministic":{{"expectedBytes":{},"actualBytes":{},"sha256Match":{},"bodyCommands":{}}},"timing":{{"wallSeconds":{:.3},"throughputMbps":{:.3},"timeTo8MbMs":{:.1},"timeTo64MbMs":{:.1},"longestReadGapMs":{:.1},"p05WindowThroughputMbps":{:.3},"p50WindowThroughputMbps":{:.3},"cpuMsPerMb":{:.3},"maxRssBytes":{}}}}}"#,
            self.expected_bytes,
            self.actual_bytes,
            self.data_match,
            self.body_commands,
            t.wall_seconds,
            t.throughput_mbps,
            t.time_to_8mb_ms,
            t.time_to_64mb_ms,
            t.longest_read_gap_ms,
            t.p05_window_mbps,
            t.p50_window_mbps,
            t.cpu_ms_per_mb,
            self.max_rss_bytes,
        );
    }
}

fn print_line(scenario: &Scenario, result: &Outcome) {
    let t = &result.timing;
    let baseline = scenario
        .baseline_mbps
        .map_or_else(String::new, |b| format!(" (baseline {b:.1})"));
    println!(
        "{:<24} {:>6.2} MB/s{baseline} wall={:.3}s to8MB={:.0}ms to64MB={:.0}ms gap={:.0}ms p05={:.1} p50={:.1} cpu={:.2}ms/MB rss={:.0}MB bodies={} match={}",
        scenario.name,
        t.throughput_mbps,
        t.wall_seconds,
        t.time_to_8mb_ms,
        t.time_to_64mb_ms,
        t.longest_read_gap_ms,
        t.p05_window_mbps,
        t.p50_window_mbps,
        t.cpu_ms_per_mb,
        result.max_rss_bytes as f64 / 1e6,
        result.body_commands,
        result.data_match,
    );
}

fn print_trace(s: &Summary) {
    let ms = |ns: u64| ns as f64 / 1e6;
    let share = |ns: u64| ns as f64 * 100.0 / s.stall_ns.max(1) as f64;
    let c = &s.causes;
    println!(
        "{:<24} first-byte={:.1}ms stalls={} stall={:.0}ms longest={:.1}ms budget={:.0}% permit={:.0}% connection={:.0}% pipeline={:.0}% latency={:.0}% transfer={:.0}% decode={:.0}% other={:.0}% client={:.0}ms attempts={} failed={} hedges={} dropped={}",
        "  trace",
        ms(s.first_byte_ns.unwrap_or_default()),
        s.stalls,
        ms(s.stall_ns),
        ms(s.longest_stall_ns),
        share(c.budget),
        share(c.permit),
        share(c.connection),
        share(c.pipeline),
        share(c.latency),
        share(c.transfer),
        share(c.decode),
        share(c.other),
        ms(s.client_ns),
        s.attempts,
        s.failed,
        s.hedges,
        s.dropped,
    );
}
