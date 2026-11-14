//! Database scale benchmark: seeds a library and a metrics store with deterministic
//! generated data (seed 1025), then times the queries the explorer, SAB API, health
//! worker and overview run.
//!
//! `cargo run -p infinidysk-bench --bin db-scale --release -- [--items N] [--checks N]
//!  [--metrics-days N] [--providers N] [--runs N] [--postgres <admin-url>] [--dir <path>]
//!  [--keep] [--json <path>]`
//!
//! The library goes to SQLite unless `--postgres` names a server whose user may create
//! databases. The metrics store is always SQLite.

use std::error::Error;
use std::path::{Path, PathBuf};
use std::time::{Duration, Instant};

use infinidysk_core::{BlobId, ItemId, ItemKind, JobId};
use infinidysk_db::metrics::{BreakerEvent, Grain, Metrics, SessionRecord};
use infinidysk_db::{
    Db, FileQuery, FileSort, HealthCheck, HealthResult, HealthState, HealthStatus, HistoryEntry,
    HistoryQuery, Item, JobStatus, Recheck, Repair, now_ms,
};
use serde_json::{Value, json};

const MINUTE: i64 = 60_000;
const HOUR: i64 = 60 * MINUTE;
const DAY: i64 = 24 * HOUR;
const FILES_PER_RELEASE: usize = 4;

struct Args {
    items: usize,
    checks: usize,
    metrics_days: i64,
    providers: usize,
    runs: usize,
    postgres: Option<String>,
    dir: PathBuf,
    keep: bool,
    json: Option<String>,
}

impl Args {
    fn parse() -> Result<Self, Box<dyn Error>> {
        let mut a = Self {
            items: 40_000,
            checks: 3,
            metrics_days: 30,
            providers: 4,
            runs: 7,
            postgres: None,
            dir: std::env::temp_dir(),
            keep: false,
            json: None,
        };
        let mut args = std::env::args().skip(1);
        while let Some(flag) = args.next() {
            if flag == "--keep" {
                a.keep = true;
                continue;
            }
            let value = args.next().ok_or_else(|| format!("{flag} needs a value"))?;
            match flag.as_str() {
                "--items" => a.items = value.parse()?,
                "--checks" => a.checks = value.parse()?,
                "--metrics-days" => a.metrics_days = value.parse()?,
                "--providers" => a.providers = value.parse()?,
                "--runs" => a.runs = value.parse::<usize>()?.max(1),
                "--postgres" => a.postgres = Some(value),
                "--dir" => a.dir = value.into(),
                "--json" => a.json = Some(value),
                _ => return Err(format!("unknown flag {flag}").into()),
            }
        }
        Ok(a)
    }
}

/// splitmix64: deterministic and dependency-free.
struct Rng(u64);

impl Rng {
    const fn next(&mut self) -> u64 {
        self.0 = self.0.wrapping_add(0x9e37_79b9_7f4a_7c15);
        let mut z = self.0;
        z = (z ^ (z >> 30)).wrapping_mul(0xbf58_476d_1ce4_e5b9);
        z = (z ^ (z >> 27)).wrapping_mul(0x94d0_49bb_1331_11eb);
        z ^ (z >> 31)
    }

    const fn below(&mut self, n: u64) -> u64 {
        self.next() % n
    }

    fn range(&mut self, from: i64, to: i64) -> i64 {
        from + i64::try_from(self.below((to - from).max(1).cast_unsigned())).unwrap_or(0)
    }

    /// True `percent`% of the time.
    const fn chance(&mut self, percent: u64) -> bool {
        self.below(100) < percent
    }
}

#[derive(Default)]
struct Report {
    rows: Vec<(String, usize, Vec<Duration>)>,
}

impl Report {
    fn add(&mut self, name: &str, rows: usize, mut times: Vec<Duration>) {
        times.sort();
        let ms = |d: Duration| d.as_secs_f64() * 1000.0;
        let (min, mid, max) = (times[0], times[times.len() / 2], times[times.len() - 1]);
        println!(
            "{name:<34} rows={rows:<7} min={:>9.3}ms p50={:>9.3}ms max={:>9.3}ms",
            ms(min),
            ms(mid),
            ms(max)
        );
        self.rows.push((name.to_owned(), rows, times));
    }

    fn json(&self) -> Value {
        let ms = |d: Duration| (d.as_secs_f64() * 1e6).round() / 1000.0;
        self.rows
            .iter()
            .map(|(name, rows, t)| {
                let v = json!({
                    "deterministic": { "rowsReturned": rows },
                    "timing": { "minMs": ms(t[0]), "p50Ms": ms(t[t.len() / 2]),
                        "maxMs": ms(t[t.len() - 1]) },
                });
                (name.clone(), v)
            })
            .collect::<serde_json::Map<_, _>>()
            .into()
    }
}

/// Runs `$body` (yielding a row count) `$runs` times and records the timings.
macro_rules! bench {
    ($report:expr, $runs:expr, $name:expr, $body:expr) => {{
        let mut times = Vec::with_capacity($runs);
        let mut rows = 0;
        for _ in 0..$runs {
            let started = Instant::now();
            rows = $body;
            times.push(started.elapsed());
        }
        $report.add($name, rows, times);
    }};
}

fn main() -> Result<(), Box<dyn Error>> {
    let args = Args::parse()?;
    let rt = tokio::runtime::Builder::new_multi_thread()
        .enable_all()
        .build()?;
    rt.block_on(run(&args))
}

async fn run(args: &Args) -> Result<(), Box<dyn Error>> {
    let tag = ItemId::new().0.simple().to_string();
    std::fs::create_dir_all(&args.dir)?;
    let library = args.dir.join(format!("infinidysk-db-scale-{tag}.sqlite"));
    let metrics_path = args
        .dir
        .join(format!("infinidysk-db-scale-{tag}-metrics.sqlite"));
    let pg_name = format!("infinidysk_scale_{tag}");
    let url = match &args.postgres {
        Some(admin) => {
            let pool = sqlx::PgPool::connect(admin).await?;
            sqlx::query(sqlx::AssertSqlSafe(format!("CREATE DATABASE {pg_name}")))
                .execute(&pool)
                .await?;
            pool.close().await;
            let (base, _) = admin.rsplit_once('/').ok_or("the url needs a database")?;
            format!("{base}/{pg_name}")
        }
        None => format!("sqlite:{}", library.display()),
    };
    let mut report = Report::default();
    let mut rng = Rng(1025);
    let now = now_ms();

    let db = Db::connect(&url).await?;
    let started = Instant::now();
    let lib = seed_library(&db, args, &mut rng, now).await?;
    let seed_library_s = started.elapsed().as_secs_f64();
    println!(
        "seeded library in {seed_library_s:.1}s: {} files",
        lib.files.len()
    );
    library_scenarios(&db, &lib, args, now, &mut report).await?;
    db.close().await;

    let started = Instant::now();
    let metrics = Metrics::open(&metrics_path).await?;
    seed_metrics(&metrics, args, &mut rng, now).await?;
    metrics.close().await;
    let seed_metrics_s = started.elapsed().as_secs_f64();
    println!("seeded metrics in {seed_metrics_s:.1}s");
    metrics_scenarios(&metrics_path, args, now, &mut report).await?;

    let sizes = json!({
        "libraryBytes": args.postgres.is_none().then(|| file_bytes(&library)),
        "metricsBytes": file_bytes(&metrics_path),
    });
    println!("sizes: {sizes}");
    if let Some(path) = &args.json {
        let out = json!({
            "schemaVersion": 1,
            "report": "db-scale",
            "params": {
                "backend": if args.postgres.is_some() { "postgres" } else { "sqlite" },
                "items": args.items, "checksPerFile": args.checks,
                "metricsDays": args.metrics_days, "providers": args.providers,
                "runs": args.runs,
            },
            "seedSeconds": { "library": seed_library_s, "metrics": seed_metrics_s },
            "sizes": sizes,
            "scenarios": report.json(),
        });
        std::fs::write(path, serde_json::to_string_pretty(&out)? + "\n")?;
    }

    if args.keep {
        println!("kept {url} and {}", metrics_path.display());
    } else {
        remove(&library);
        remove(&metrics_path);
        if let Some(admin) = &args.postgres {
            let pool = sqlx::PgPool::connect(admin).await?;
            sqlx::query(sqlx::AssertSqlSafe(format!("DROP DATABASE {pg_name}")))
                .execute(&pool)
                .await?;
            pool.close().await;
        }
    }
    Ok(())
}

struct Library {
    tv: ItemId,
    jobs: Vec<JobId>,
    files: Vec<(ItemId, String)>,
}

/// Releases of one folder and [`FILES_PER_RELEASE`] files each, spread over two years,
/// 60% `tv`; 5% failed without files. Files carry mixed health states, schedules and
/// `--checks` check results each.
async fn seed_library(
    db: &Db,
    args: &Args,
    rng: &mut Rng,
    now: i64,
) -> Result<Library, Box<dyn Error>> {
    let content = db
        .item(ItemId::CONTENT)
        .await?
        .ok_or("/content is missing")?;
    let tv = Item::child(&content, "tv", ItemKind::Directory)?;
    let movies = Item::child(&content, "movies", ItemKind::Directory)?;
    db.insert(&[tv.clone(), movies.clone()], &[]).await?;
    let releases = args.items / (FILES_PER_RELEASE + 1);
    let spacing = (730 * DAY) / i64::try_from(releases.max(1))?;
    let mut lib = Library {
        tv: tv.id,
        jobs: Vec::with_capacity(releases),
        files: Vec::with_capacity(releases * FILES_PER_RELEASE),
    };
    let mut scheduled = Vec::new();
    for i in 0..releases {
        let at = now - i64::try_from(releases - i)? * spacing;
        let (parent, category) = if rng.chance(60) {
            (&tv, "tv")
        } else {
            (&movies, "movies")
        };
        let name = format!("release-{i:07}");
        let nzb = BlobId::new();
        let mut history = HistoryEntry {
            id: JobId::new(),
            created_at: at,
            file_name: format!("{name}.nzb"),
            job_name: name.clone(),
            category: category.to_owned(),
            status: JobStatus::Completed,
            total_bytes: 0,
            download_seconds: 2,
            fail_message: None,
            download_dir: None,
            nzb_blob: Some(nzb),
            storage: None,
        };
        if rng.chance(5) {
            history.status = JobStatus::Failed;
            history.fail_message = Some("Articles are missing.".to_owned());
            db.add_history(&history, &[], &[(nzb, b"nzb")]).await?;
            lib.jobs.push(history.id);
            continue;
        }
        let mut dir = Item::child(parent, &name, ItemKind::Directory)?;
        dir.created_at = at;
        dir.nzb_blob = Some(nzb);
        history.download_dir = Some(dir.id);
        let mut items = vec![dir.clone()];
        let mut blobs: Vec<(BlobId, &[u8])> = vec![(nzb, b"nzb")];
        for j in 0..FILES_PER_RELEASE {
            let mut file = Item::child(&dir, &format!("file-{j}.mkv"), ItemKind::NzbFile)?;
            let meta = BlobId::new();
            file.size = Some(rng.below(8_000_000_000) + 100_000_000);
            file.meta_blob = Some(meta);
            file.created_at = at;
            file.release_date = Some(at - rng.range(0, 30 * DAY));
            file.health_state = match rng.below(100) {
                0..75 => HealthState::Healthy,
                75..90 => HealthState::Unchecked,
                90..93 => HealthState::Degraded,
                93..95 => HealthState::Suspect,
                95 => HealthState::Repairing,
                96 => HealthState::Hidden,
                97 => HealthState::ActionNeeded,
                _ => HealthState::Ignored,
            };
            history.total_bytes += file.size.unwrap_or_default();
            if !matches!(
                file.health_state,
                HealthState::Unchecked | HealthState::Ignored
            ) {
                scheduled.push((file.id, file.health_state));
            }
            lib.files.push((file.id, file.path.clone()));
            blobs.push((meta, b"meta"));
            items.push(file);
        }
        db.add_history(&history, &items, &blobs).await?;
        lib.jobs.push(history.id);
    }
    for (id, state) in scheduled {
        let status = HealthStatus {
            state,
            last_check: Some(now - rng.range(0, 30 * DAY)),
            next_check: Some(now + rng.range(-7 * DAY, 30 * DAY)),
            ..HealthStatus::default()
        };
        db.set_health(id, &status, Recheck::None).await?;
        if rng.chance(1) {
            db.request_recheck(id, Recheck::User).await?;
        }
    }
    for (id, path) in &lib.files {
        for _ in 0..args.checks {
            let result = match rng.below(100) {
                0..90 => HealthResult::Healthy,
                90..95 => HealthResult::Inconclusive,
                95..98 => HealthResult::Degraded,
                _ => HealthResult::Missing,
            };
            db.record_health_check(&HealthCheck {
                created_at: now - rng.range(0, 90 * DAY),
                item_id: *id,
                path: path.clone(),
                result,
                repair: Repair::None,
                message: None,
            })
            .await?;
        }
    }
    Ok(lib)
}

async fn library_scenarios(
    db: &Db,
    lib: &Library,
    args: &Args,
    now: i64,
    report: &mut Report,
) -> Result<(), Box<dyn Error>> {
    let runs = args.runs;
    let page = FileQuery {
        limit: 100,
        ..FileQuery::default()
    };
    let explorer = [
        ("files-all-by-name", page.clone()),
        (
            "files-all-added-desc",
            FileQuery {
                sort: FileSort::Added,
                descending: true,
                ..page.clone()
            },
        ),
        (
            "files-only-by-size",
            FileQuery {
                files_only: true,
                sort: FileSort::Size,
                descending: true,
                ..page.clone()
            },
        ),
        (
            "files-parent-tv",
            FileQuery {
                parent: Some(lib.tv),
                ..page.clone()
            },
        ),
        (
            "files-category-movies",
            FileQuery {
                category: Some("movies"),
                ..page.clone()
            },
        ),
        (
            "files-search",
            FileQuery {
                search: Some("release-00012"),
                ..page.clone()
            },
        ),
        (
            "files-result-missing",
            FileQuery {
                result: Some(HealthResult::Missing),
                ..page.clone()
            },
        ),
        (
            "files-result-healthy",
            FileQuery {
                result: Some(HealthResult::Healthy),
                ..page.clone()
            },
        ),
        (
            "files-state-hidden",
            FileQuery {
                state: Some(HealthState::Hidden),
                ..page.clone()
            },
        ),
        (
            "files-deep-page",
            FileQuery {
                offset: i64::try_from(args.items / 2)?,
                ..page.clone()
            },
        ),
    ];
    for (name, q) in &explorer {
        bench!(report, runs, name, db.files(q).await?.0.len());
    }

    bench!(
        report,
        runs,
        "health-due-routine",
        db.due_health_work(now, true, 32).await?.len()
    );
    bench!(
        report,
        runs,
        "health-due-rechecks-only",
        db.due_health_work(now, false, 32).await?.len()
    );
    bench!(
        report,
        runs,
        "health-summary",
        db.health_summary().await?.len()
    );
    bench!(
        report,
        runs,
        "health-checks-feed",
        db.health_checks(infinidysk_db::HealthFilter::default(), 100)
            .await?
            .len()
    );
    let (item, _) = &lib.files[lib.files.len() / 2];
    bench!(
        report,
        runs,
        "item-checks",
        db.item_checks(*item, 20).await?.len()
    );
    let job = lib.jobs[lib.jobs.len() / 2];
    bench!(
        report,
        runs,
        "job-dir",
        usize::from(db.job_dir(job).await?.is_some())
    );
    bench!(
        report,
        runs,
        "catalogue",
        usize::try_from(db.catalogue(now - 7 * DAY).await?.files)?
    );

    let history = [
        (
            "history-sab-default",
            HistoryQuery {
                limit: Some(10_000),
                ..HistoryQuery::default()
            },
        ),
        (
            "history-page10",
            HistoryQuery {
                limit: Some(10),
                ..HistoryQuery::default()
            },
        ),
        (
            "history-category-tv",
            HistoryQuery {
                category: Some("tv"),
                limit: Some(10),
                ..HistoryQuery::default()
            },
        ),
        (
            "history-failed",
            HistoryQuery {
                status: Some(JobStatus::Failed),
                limit: Some(10),
                ..HistoryQuery::default()
            },
        ),
        (
            "history-search",
            HistoryQuery {
                search: Some("release-00012"),
                limit: Some(10),
                ..HistoryQuery::default()
            },
        ),
    ];
    for (name, q) in &history {
        bench!(report, runs, name, db.history_page(q).await?.0.len());
    }
    let ids = &lib.jobs[..lib.jobs.len().min(50)];
    let by_id = HistoryQuery {
        ids: Some(ids),
        ..HistoryQuery::default()
    };
    bench!(
        report,
        runs,
        "history-50-ids",
        db.history_page(&by_id).await?.0.len()
    );
    bench!(
        report,
        runs,
        "nzb-expiry-scan",
        usize::try_from(db.delete_expired_nzbs().await?)?
    );
    Ok(())
}

/// `--metrics-days` of hour rows (one write per hour) and the last 7 days minute by
/// minute, from `--providers` providers; 200 reads a day for up to 90 days.
async fn seed_metrics(
    metrics: &Metrics,
    args: &Args,
    rng: &mut Rng,
    now: i64,
) -> Result<(), Box<dyn Error>> {
    let providers: Vec<String> = (0..args.providers)
        .map(|_| ItemId::new().0.to_string())
        .collect();
    let minutes_from = now - 7 * DAY;
    let mut at = (now - args.metrics_days * DAY) / HOUR * HOUR;
    while at < now {
        let (step, scale) = if at < minutes_from {
            (HOUR, 60)
        } else {
            (MINUTE, 1)
        };
        let counters = minute_counters(&providers, rng, scale);
        // About 200 reads a day either way.
        let reads = if step == HOUR {
            usize::from(rng.chance(80)) * 10
        } else {
            usize::from(rng.chance(14))
        };
        let sessions: Vec<SessionRecord> = (0..reads).map(|_| session(rng, at)).collect();
        let events: Vec<BreakerEvent> = if rng.chance(1) {
            vec![BreakerEvent {
                at,
                provider_key: Some(providers[0].clone()),
                provider: providers[0].clone(),
                open_ms: 30_000,
            }]
        } else {
            Vec::new()
        };
        metrics.write(at, &counters, &sessions, &events).await?;
        at += step;
    }
    Ok(())
}

fn session(rng: &mut Rng, at: i64) -> SessionRecord {
    let size = rng.range(100_000_000, 8_000_000_000);
    SessionRecord {
        item: Some(ItemId::new()),
        opened_at: at,
        closed_at: at + rng.range(1000, 2 * HOUR),
        path: format!("/content/tv/release-{:07}/file-0.mkv", rng.below(10_000)),
        delivery: "fuse".to_owned(),
        client: "player".to_owned(),
        size,
        served: rng.range(0, size),
        first_byte_ms: Some(rng.range(50, 2000)),
        stalls: rng.range(0, 3),
        stall_ms: rng.range(0, 500),
        seeks: rng.range(0, 10),
        end_reason: "closed".to_owned(),
    }
}

/// One minute of non-zero counters, keyed like the app writes them, times `scale`.
fn minute_counters(providers: &[String], rng: &mut Rng, scale: u64) -> Vec<(String, u64)> {
    let mut c = Vec::new();
    let mut peaks = Vec::new();
    let mut add = |key: String, value: u64| c.push((key, value * scale));
    for (i, p) in providers.iter().enumerate() {
        // Lower-priority providers see less traffic.
        let busy = 90 / (u64::try_from(i).unwrap_or(0) + 1);
        let mut any = false;
        for workload in ["stream", "queue", "health"] {
            if !rng.chance(busy) {
                continue;
            }
            any = true;
            let base = format!("p.{p}.{workload}");
            let ok = rng.below(5000) + 1;
            add(format!("{base}.bytes"), ok * 750_000);
            add(format!("{base}.retrieved"), ok);
            if rng.chance(30) {
                add(format!("{base}.not_found"), rng.below(20) + 1);
            }
            if rng.chance(10) {
                add(format!("{base}.cancelled"), rng.below(50) + 1);
            }
            for bucket in 9..15 {
                add(format!("{base}.lat.{bucket}"), rng.below(ok) + 1);
            }
        }
        if any {
            add(format!("p.{p}.active_ms"), 60_000);
            peaks.push((format!("peak.p.{p}.bps"), rng.below(200_000_000)));
        }
        if rng.chance(2) {
            add(format!("p.{p}.connect_failures"), 1);
        }
    }
    if providers.len() > 1 && rng.chance(20) {
        add(
            format!("rescue.{}.{}.missing", providers[0], providers[1]),
            rng.below(20) + 1,
        );
    }
    for key in [
        "served.fuse",
        "served.webdav",
        "cache.memory_hits",
        "cache.misses",
        "cache.bytes_served",
        "queue.bytes",
        "health.healthy",
    ] {
        if rng.chance(50) {
            add(key.to_owned(), rng.below(1_000_000) + 1);
        }
    }
    c.extend(peaks);
    c
}

async fn metrics_scenarios(
    path: &Path,
    args: &Args,
    now: i64,
    report: &mut Report,
) -> Result<(), Box<dyn Error>> {
    let runs = args.runs;
    let mut times = Vec::with_capacity(runs);
    for _ in 1..runs {
        let started = Instant::now();
        let m = Metrics::open(path).await?;
        times.push(started.elapsed());
        m.close().await;
    }
    let started = Instant::now();
    let m = Metrics::open(path).await?;
    times.push(started.elapsed());
    report.add("metrics-open", 1, times);
    bench!(report, runs, "metrics-records", {
        let r = m.records().await?;
        usize::from(r.busiest_hour.is_some())
    });
    bench!(
        report,
        runs,
        "metrics-series-minute-1h",
        m.series(Grain::Minute, now - HOUR, MINUTE).await?.len()
    );
    bench!(
        report,
        runs,
        "metrics-series-minute-24h",
        m.series(Grain::Minute, now - DAY, 5 * MINUTE).await?.len()
    );
    bench!(
        report,
        runs,
        "metrics-series-hour-30d",
        m.series(Grain::Hour, now - 30 * DAY, HOUR).await?.len()
    );
    bench!(
        report,
        runs,
        "metrics-series-hour-365d",
        m.series(Grain::Hour, now - 365 * DAY, DAY).await?.len()
    );
    bench!(
        report,
        runs,
        "metrics-sum-rescues-30d",
        usize::from(m.sum(Grain::Hour, now - 30 * DAY, now, "rescue.").await? > 0)
    );
    bench!(report, runs, "metrics-lifetime", m.lifetime().await?.len());
    bench!(
        report,
        runs,
        "metrics-sessions-100",
        m.sessions(100).await?.len()
    );
    bench!(
        report,
        runs,
        "metrics-breaker-events-7d",
        m.breaker_events(now - 7 * DAY).await?.len()
    );
    // A day of expired minute rows, as after a day offline.
    bench!(report, 1, "metrics-prune-backlog", {
        m.prune(now + DAY).await?;
        1
    });
    bench!(report, runs, "metrics-prune-steady", {
        m.prune(now + DAY).await?;
        1
    });
    m.close().await;
    Ok(())
}

fn file_bytes(path: &Path) -> u64 {
    ["", "-wal"]
        .iter()
        .filter_map(|s| std::fs::metadata(format!("{}{s}", path.display())).ok())
        .map(|m| m.len())
        .sum()
}

fn remove(path: &Path) {
    for suffix in ["", "-wal", "-shm"] {
        let _ = std::fs::remove_file(format!("{}{suffix}", path.display()));
    }
}
