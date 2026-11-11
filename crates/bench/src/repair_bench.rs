//! `--scenario par2-` compares playback through a shared pool while repair runs.
//! `par2cmdline` generates the fixture before measurement and is never used by repair.

use std::error::Error;
use std::fmt::Write as _;
use std::path::{Path, PathBuf};
use std::process::Command;
use std::sync::Arc;
use std::time::{Duration, Instant};

use infinidysk_core::ItemId;
use infinidysk_nntp::{Client, Workload, patch::Store};
use infinidysk_queue::{nzb, repair};
use infinidysk_testkit::{Article, Corpus, Impairment, LoopbackServer, post};
use serde_json::{Map, Value, json};
use tokio::runtime::{Handle, Runtime};
use tokio::time;
use tokio_util::sync::CancellationToken;

use super::{KIB, REPETITIONS, SEED, Trace, client, generate, plain, read, seek_read, usage};

type Result<T> = std::result::Result<T, Box<dyn Error>>;

const ARTICLE: usize = 512 * KIB;
const LOST: usize = 16;
const MODES: [&str; 5] = [
    "par2-idle",
    "par2-active",
    "par2-cancelled",
    "par2-failing",
    "par2-verify",
];
const LIMITS: repair::Limits = repair::Limits {
    requests: 10_000,
    decoded_bytes: 1_000_000_000,
    memory_bytes: 160_000_000,
    comparisons: 10_000_000,
    retained_bytes: 1_000_000,
};

struct Scratch(PathBuf);

impl Drop for Scratch {
    fn drop(&mut self) {
        let _ = std::fs::remove_dir_all(&self.0);
    }
}

struct Fixture {
    bytes: Vec<u8>,
    index: Vec<u8>,
    parity: Vec<u8>,
}

impl Fixture {
    fn generate(root: &Path) -> Result<Self> {
        let bytes = Corpus::generate(SEED, 64, ARTICLE).data;
        std::fs::write(root.join("source.bin"), &bytes)?;
        let output = Command::new("par2")
            .current_dir(root)
            .args([
                "create",
                "-q",
                "-s524288",
                "-c1",
                "-n1",
                "repair.par2",
                "source.bin",
            ])
            .output()?;
        if !output.status.success() {
            return Err(format!(
                "par2 fixture generation failed: {}",
                String::from_utf8_lossy(&output.stderr)
            )
            .into());
        }
        let index = std::fs::read(root.join("repair.par2"))?;
        let parity = std::fs::read(root.join("repair.vol0+1.par2"))?;
        for name in ["source.bin", "repair.par2", "repair.vol0+1.par2"] {
            std::fs::remove_file(root.join(name))?;
        }
        Ok(Self {
            bytes,
            index,
            parity,
        })
    }

    fn posting(&self, mode: &str) -> Result<(Vec<Article>, Arc<nzb::Nzb>)> {
        let mut xml = String::from("<nzb>");
        let mut posted = Vec::new();
        for (name, bytes) in [
            ("source.bin", self.bytes.as_slice()),
            ("repair.par2", self.index.as_slice()),
            ("repair.vol0+1.par2", self.parity.as_slice()),
        ] {
            if mode == "par2-failing" && name.ends_with("+1.par2") {
                continue;
            }
            write!(xml, "<file subject=\"{name}\"><segments>")?;
            for (i, article) in post(name, name, bytes, ARTICLE).into_iter().enumerate() {
                let len = (bytes.len() - i * ARTICLE).min(ARTICLE);
                write!(
                    xml,
                    "<segment bytes=\"{len}\" number=\"{}\">{}</segment>",
                    i + 1,
                    article.message_id
                )?;
                if mode == "par2-verify" || name != "source.bin" || i != LOST {
                    posted.push(article);
                }
            }
            xml.push_str("</segments></file>");
        }
        xml.push_str("</nzb>");
        Ok((posted, Arc::new(nzb::parse(xml.as_bytes())?)))
    }
}

pub(super) fn run(
    filter: &str,
    json_path: Option<&str>,
    server_rt: &Runtime,
    client_rt: &Runtime,
) -> Result<()> {
    let root =
        Scratch(std::env::temp_dir().join(format!("infinidysk-par2-bench-{}", ItemId::new())));
    std::fs::create_dir(&root.0)?;
    let fixture = Fixture::generate(&root.0)?;
    let scenario = super::Scenario {
        impairment: Impairment {
            response_delay: Duration::from_millis(if filter == "par2-verify" { 20 } else { 3 }),
            ..super::PLAIN
        },
        ..plain("par2", 128, ARTICLE, 8, 4, 32, None)
    };
    let (playback, layout) = generate(&scenario);
    let mut report = Map::new();
    for mode in MODES.into_iter().filter(|mode| mode.contains(filter)) {
        let (articles, nzb) = fixture.posting(mode)?;
        let mut runs = Vec::new();
        for _ in 0..REPETITIONS {
            let server = server_rt.block_on(LoopbackServer::start_impaired(
                playback
                    .articles
                    .iter()
                    .cloned()
                    .chain(articles.iter().cloned()),
                scenario.impairment,
            ))?;
            let metrics = client_rt.block_on(async {
                let client = client(&scenario, server.addr().port())?;
                let permits = client.free_permits();
                let cpu = usage::cpu();
                let stop = CancellationToken::new();
                if mode == "par2-verify" {
                    let started = Instant::now();
                    let result = execute(Arc::clone(&client), Arc::clone(&nzb), &stop, true).await?;
                    assert_eq!(result.verified_bytes, fixture.bytes.len() as u64);
                    assert_eq!(result.patches.len(), 0);
                    let seconds = started.elapsed().as_secs_f64();
                    let provider_bytes = client.stats()[0].workloads[Workload::Health as usize].bytes;
                    return Ok(json!({
                        "verificationMs": seconds * 1000.0,
                        "verifiedMbPerS": result.verified_bytes as f64 / seconds / 1e6,
                        "providerBytes": provider_bytes,
                        "sourcePassEquivalent": provider_bytes as f64 / fixture.bytes.len() as f64,
                        "verifiedBytes": result.verified_bytes,
                        "cpuMs": usage::cpu_ms_since(cpu), "maxRssBytes": usage::max_rss_bytes(),
                        "permitsRestored": client.free_permits() == permits,
                    }));
                }
                let store = root.0.join("repairs");
                let repair = (mode != "par2-idle").then(|| {
                    let client = Arc::clone(&client);
                    let nzb = Arc::clone(&nzb);
                    let stop = stop.clone();
                    let store = store.clone();
                    tokio::spawn(async move {
                        let started = Instant::now();
                        let outcome = execute(client, nzb, &stop, false).await;
                        (outcome, started.elapsed(), store)
                    })
                });
                if repair.is_some() {
                    time::timeout(Duration::from_secs(5), async {
                        while server.body_commands() == 0 { time::sleep(Duration::from_millis(1)).await; }
                    }).await?;
                }
                let cancel = (mode == "par2-cancelled").then(|| tokio::spawn(async move {
                    time::sleep(Duration::from_millis(50)).await;
                    stop.cancel();
                }));
                let trace = Arc::new(Trace::new(scenario.articles * 16 + 64));
                let (bytes, timing) = read(&scenario, Arc::clone(&client), Arc::clone(&layout), true, true, Some(Arc::clone(&trace))).await?;
                assert!(bytes == playback.data, "playback bytes differ from the fixture");
                let overlap = repair.as_ref().is_some_and(|job| !job.is_finished());
                let (seek_first, seek_full) = seek_read(&client, &layout, scenario.prefetch(), layout.len()/2, true).await?;
                if let Some(cancel) = cancel { cancel.await?; }
                let (verified, stored, repair_ms) = if let Some(task) = repair {
                    let (result, elapsed, store) = task.await?;
                    match (mode, result) {
                        ("par2-active", Ok(result)) => {
                            assert_eq!(result.patches.len(), 1);
                            assert!(result.patches[0].1 == fixture.bytes[LOST * ARTICLE..(LOST + 1) * ARTICLE], "recovered bytes differ from the fixture");
                            let bytes = persist(&store, &result.patches)?;
                            (result.verified_bytes, bytes, elapsed.as_secs_f64() * 1000.0)
                        }
                        ("par2-cancelled", Err(repair::Error::Cancelled)) | ("par2-failing", Err(repair::Error::Content(_))) => (0, 0, elapsed.as_secs_f64() * 1000.0),
                        (_, Err(error)) => return Err(error.into()),
                        _ => return Err("unexpected repair success".into()),
                    }
                } else { (0, 0, 0.0) };
                let recovering = Instant::now();
                time::timeout(Duration::from_secs(5), async {
                    while client.free_permits() != permits { time::sleep(Duration::from_millis(1)).await; }
                }).await?;
                let stats = &client.stats()[0].workloads;
                let repair_bytes = stats[Workload::Health as usize].bytes;
                assert_eq!(stats[Workload::Queue as usize].bytes, 0);
                let trace = trace.summary();
                Ok::<_, Box<dyn Error>>(json!({
                    "throughputMbps": timing.throughput_mbps, "timeTo8MbMs": timing.time_to_8mb_ms,
                    "timeTo64MbMs": timing.time_to_64mb_ms, "longestReadGapMs": timing.longest_read_gap_ms,
                    "firstByteMs": trace.first_byte_ns.map(|ns| ns as f64 / 1e6),
                    "seekFirstByteMs": seek_first, "seekTo8MbMs": seek_full,
                    "prefetchStallMs": trace.stall_idle_ns as f64 / 1e6,
                    "repairActiveAtPlaybackEnd": overlap, "repairMs": repair_ms,
                    "repairProviderBytes": repair_bytes, "sourcePassEquivalent": repair_bytes as f64 / fixture.bytes.len() as f64,
                    "verifiedBytes": verified, "retainedPatchBytes": stored,
                    "cpuMs": usage::cpu_ms_since(cpu), "maxRssBytes": usage::max_rss_bytes(),
                    "permitsRestored": true, "permitDrainMs": recovering.elapsed().as_secs_f64() * 1000.0,
                }))
            })?;
            println!("{mode} {metrics}");
            runs.push(metrics);
        }
        report.insert(mode.to_owned(), Value::Array(runs));
    }
    if report.is_empty() {
        return Err("no PAR2 scenarios match the filter".into());
    }
    if let Some(path) = json_path {
        std::fs::write(
            path,
            serde_json::to_vec_pretty(&json!({"seed": SEED, "scenarios": report}))?,
        )?;
    }
    Ok(())
}

async fn execute(
    client: Arc<Client>,
    nzb: Arc<nzb::Nzb>,
    cancel: &CancellationToken,
    verify_only: bool,
) -> std::result::Result<repair::Execution, repair::Error> {
    let runtime = Handle::current();
    let (reader, limits, _) = tokio::task::spawn_blocking({
        let client = Arc::clone(&client);
        let nzb = Arc::clone(&nzb);
        let cancel = cancel.clone();
        move || runtime.block_on(repair::discover(&client, &nzb, LIMITS, &cancel))
    })
    .await??;
    let set = reader.sets()[0];
    repair::execute(
        &client,
        nzb,
        Arc::new(reader),
        set,
        limits,
        verify_only,
        repair::Context::default(),
        cancel,
    )
    .await
}

fn persist(path: &Path, patches: &[(infinidysk_core::repair::Patch, Vec<u8>)]) -> Result<u64> {
    let store = Store::open(path, LIMITS.retained_bytes)?;
    for (patch, bytes) in patches {
        store.write(patch, bytes, || false)?;
    }
    let retained = store.used_bytes()?;
    drop(store);
    std::fs::remove_dir_all(path)?;
    Ok(retained)
}
