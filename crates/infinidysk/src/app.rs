//! Process wiring: the SAB API and `WebDAV` on one port, the queue worker and the FUSE mount.

use std::collections::HashMap;
use std::future::{Future, IntoFuture as _};
use std::net::SocketAddr;
use std::pin::Pin;
use std::str::FromStr;
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::{Arc, Mutex, PoisonError};
use std::time::{Duration, Instant};

use anyhow::Context as _;
use axum::extract::Request;
use infinidysk_core::ItemId;
use infinidysk_db::Db;
use infinidysk_health::{
    Arr, Health, Profile, Settings as HealthSettings, UnownedAction, Unverified,
};
use infinidysk_nntp::{
    Articles, Bandwidth, Client, Credentials, Downloads, Endpoint, Pool, PoolConfig, Provider,
    TlsMode, Workload,
};
use infinidysk_queue::Queue;
use infinidysk_queue::import::{self, Output, Verifier};
use infinidysk_stream::{Budget, Cache, CacheConfig, DiskConfig, Fault};
use infinidysk_vfs::{ReadVersion, Tracing, Vfs};
use infinidysk_webdav::{Access, Auth as DavAuth, WebDav};
use tokio::net::TcpListener;
use tokio::sync::{Notify, RwLock, mpsc, watch};
use tokio_util::sync::CancellationToken;
use tower::Layer as _;
use tracing::{info, warn};

use crate::admin::{self, Admin};
use crate::arr::{Discovery, Probe};
use crate::auth::{self, Auth};
use crate::config::{self, ConfigStore, Settings, Tuning};
use crate::metrics::{Metrics, Sources};
use crate::quota::Quotas;
use crate::sab::{self, Sab};
use crate::ui;

const MB: u64 = 1_000_000;
const PROVIDER_TIMEOUT: Duration = Duration::from_secs(30);
const IDLE_TIMEOUT: Duration = Duration::from_secs(60);
/// Idle connections kept open to each primary-level provider, so a play after a
/// pause skips the handshake.
const WARM_FLOOR: usize = 2;
/// How long open responses (streams) get to finish once shutdown starts.
const DRAIN: Duration = Duration::from_secs(5);
/// How often stale query planner statistics are refreshed, starting at startup.
const OPTIMIZE_EVERY: Duration = Duration::from_hours(6);

/// Process settings needed before the database is open.
pub struct Boot {
    pub listen: SocketAddr,
    pub database_url: String,
    /// Where `database_url` came from.
    pub database_source: crate::database::Source,
    /// Owner of every mounted file.
    pub uid: u32,
    pub gid: u32,
    /// Trace reads opened in the first this many minutes; 0 for none.
    pub trace_minutes: u32,
    /// The metrics store, always its own SQLite file.
    pub metrics_path: std::path::PathBuf,
    /// Where log files are kept across restarts.
    pub log_dir: std::path::PathBuf,
    /// Where a C# install kept its data, imported on first boot.
    pub config_dir: std::path::PathBuf,
    /// The ffprobe binary for media analysis.
    pub ffprobe: String,
}

impl Boot {
    /// `INFINIDYSK_LISTEN`, `INFINIDYSK_DATABASE_URL` (default: the URL in
    /// `/config/database.toml`, else `/config/infinidysk.sqlite`), `PUID`, `PGID`,
    /// `INFINIDYSK_TRACE_MINUTES`, `INFINIDYSK_METRICS_PATH` (default: `metrics.sqlite`
    /// beside a SQLite database, else in `/config`), `INFINIDYSK_LOG_DIR` (default: `logs`
    /// in that same directory) and `INFINIDYSK_FFPROBE` (default:
    /// `ffprobe` on `PATH`).
    pub fn from_env() -> anyhow::Result<Self> {
        let (_, database_url, database_source) = crate::database::select(
            std::env::var("INFINIDYSK_DATABASE_URL").ok(),
            std::path::Path::new(crate::database::CONFIG_DIR),
        )?;
        let beside = sqlite_path(&database_url)
            .as_deref()
            .and_then(std::path::Path::parent)
            .filter(|dir| !dir.as_os_str().is_empty())
            .map_or_else(|| "/config".into(), std::path::Path::to_path_buf);
        Ok(Self {
            listen: listen_from_env()?,
            metrics_path: var("INFINIDYSK_METRICS_PATH", beside.join("metrics.sqlite"))?,
            log_dir: var("INFINIDYSK_LOG_DIR", beside.join("logs"))?,
            config_dir: beside,
            database_url,
            database_source,
            uid: var("PUID", 1000)?,
            gid: var("PGID", 1000)?,
            trace_minutes: var("INFINIDYSK_TRACE_MINUTES", 0)?,
            ffprobe: var("INFINIDYSK_FFPROBE", "ffprobe".to_owned())?,
        })
    }
}

/// The file of a `sqlite:` URL; `None` for any other database.
pub(crate) fn sqlite_path(url: &str) -> Option<std::path::PathBuf> {
    url.strip_prefix("sqlite:").map(|p| {
        p.trim_start_matches("//")
            .split('?')
            .next()
            .unwrap_or_default()
            .into()
    })
}

/// `INFINIDYSK_LISTEN`, default `0.0.0.0:3000`.
pub fn listen_from_env() -> anyhow::Result<SocketAddr> {
    var("INFINIDYSK_LISTEN", SocketAddr::from(([0, 0, 0, 0], 3000)))
}

fn var<T>(key: &str, default: T) -> anyhow::Result<T>
where
    T: FromStr,
    T::Err: std::error::Error + Send + Sync + 'static,
{
    std::env::var(key).map_or(Ok(default), |v| {
        v.parse().with_context(|| format!("{key} is invalid"))
    })
}

/// How [`serve`] ended.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum Exit {
    Stopped,
    /// The admin API asked for a restart, to apply settings read only at startup or a
    /// restore, or a restored database did not open and boot rolls it back.
    Restart,
}

/// Serves until `cancel` fires, then unmounts and closes the database.
#[allow(clippy::significant_drop_tightening)] // False positive: `vfs` is moved into `WebDav`.
pub async fn serve(
    listener: TcpListener,
    boot: &Boot,
    vars: impl IntoIterator<Item = (String, String)>,
    cancel: CancellationToken,
) -> anyhow::Result<Exit> {
    let pending = crate::backup::boot::apply_pending(boot)?;
    let vars: Vec<(String, String)> = vars.into_iter().collect();
    let db = match Db::connect(&boot.database_url).await {
        Ok(db) => db,
        Err(error) if pending.swapped() => {
            let reason = format!("{:#}", anyhow::Error::from(error));
            warn!(%reason, "the restored database did not open; putting the previous one back");
            crate::backup::boot::begin_roll_back(boot, pending, &reason)?;
            return Ok(Exit::Restart);
        }
        Err(error) => return Err(anyhow::Error::from(error).context("opening the database")),
    };
    // Past `finish` a restore no longer rolls back, so its settings must load first.
    if pending.swapped()
        && let Err(error) = ConfigStore::load(db.clone(), vars.iter().cloned()).await
    {
        db.close().await;
        let reason = format!(
            "{:#}",
            anyhow::Error::from(error).context("loading settings")
        );
        warn!(%reason, "the restored database did not open; putting the previous one back");
        crate::backup::boot::begin_roll_back(boot, pending, &reason)?;
        return Ok(Exit::Restart);
    }
    crate::backup::boot::finish(&db, boot, pending).await?;
    let booted = crate::database::serve_cutover(
        listener,
        db,
        boot,
        std::path::Path::new(crate::database::CONFIG_DIR),
        crate::database::LIMITS,
        &cancel,
    )
    .await
    .context("switching to PostgreSQL")?;
    let (listener, db) = (booted.listener, booted.db);
    if cancel.is_cancelled() {
        db.close().await;
        return Ok(Exit::Stopped);
    }
    let listener = crate::upgrade::serve_import(listener, &db, &boot.config_dir, &cancel)
        .await
        .context("serving the C# import")?;
    if cancel.is_cancelled() {
        db.close().await;
        return Ok(Exit::Stopped);
    }
    crate::upgrade::stamp_migrated(&db, &boot.config_dir).await;
    let mut store = ConfigStore::load(db.clone(), vars)
        .await
        .context("loading settings")?;
    store
        .generate_missing_secrets()
        .await
        .context("storing generated secrets")?;
    crate::upgrade::keep_unowned_migrated(&db, &mut store).await;
    crate::maintenance::schedule_dead_links(&db, &mut store).await;
    let settings = store.settings().clone();
    infinidysk_core::enable_experiments(&settings.experiments);
    let tuning = settings.performance.effective();

    let quotas = Arc::new(
        Quotas::load(db.clone())
            .await
            .context("loading provider usage")?,
    );
    quotas.configure(&settings.providers);
    let quota_task = {
        let quotas = Arc::clone(&quotas);
        let cancel = cancel.clone();
        tokio::spawn(async move { quotas.run(cancel).await })
    };
    let bandwidth = Arc::new(Bandwidth::new(download_limit(&settings)));
    let client = Arc::new(Client::new(providers(
        &settings.providers,
        &quotas,
        &bandwidth,
    )?));
    let repairs = crate::repair::install(&client, db.clone(), boot.config_dir.join("repairs"));
    repairs
        .restore_embedded()
        .await
        .context("restoring saved repair articles")?;
    let warm_task = {
        let client = Arc::clone(&client);
        let cancel = cancel.clone();
        tokio::spawn(async move {
            let mut tick = tokio::time::interval(IDLE_TIMEOUT / 2);
            tick.set_missed_tick_behavior(tokio::time::MissedTickBehavior::Delay);
            loop {
                tokio::select! {
                    () = cancel.cancelled() => break,
                    _ = tick.tick() => client.warm().await,
                }
            }
        })
    };
    let optimize_task = {
        let db = db.clone();
        let cancel = cancel.clone();
        tokio::spawn(async move {
            let mut tick = tokio::time::interval(OPTIMIZE_EVERY);
            tick.set_missed_tick_behavior(tokio::time::MissedTickBehavior::Delay);
            loop {
                tokio::select! {
                    () = cancel.cancelled() => break,
                    _ = tick.tick() => {
                        let _writing = db.write_permit().await;
                        if let Err(error) = db.optimize().await {
                            warn!(reason = %error, "could not refresh database statistics");
                        }
                    }
                }
            }
        })
    };
    let cache = Arc::new(Cache::new(
        tuning.memory_cache_enabled,
        u64::from(tuning.memory_cache_mb) * MB,
    ));
    cache.configure(&cache_config(&tuning)).await;
    {
        // Imports read a release's first segments; playback usually starts there. A
        // stream's abandoned read-ahead arrives anyway, and seeks often come back to it.
        let cache = Arc::clone(&cache);
        client.set_sink(Box::new(move |id, workload, body, drained| {
            if workload == Workload::Queue || (drained && workload == Workload::Stream) {
                cache.insert(id, body.data.clone());
            }
        }));
    }
    let queue = Arc::new(Queue::new(db.clone(), Arc::clone(&client)));
    queue.restore().await.context("loading the queue pause")?;
    queue.set_concurrency(settings.queue.concurrency.into());
    db.set_nzb_retention_days(settings.queue.nzb_retention_days);
    let cleanup = Arc::new(Notify::new());
    set_rules(&queue, &settings);
    add_categories(&queue, &settings).await;

    let (arr_tx, arr_rx) = watch::channel(arr_instances(&settings.arr_instances));
    let (health_tx, health_rx) = watch::channel(health_settings(&settings.health));
    let (fault_tx, mut fault_rx) = mpsc::unbounded_channel::<(ItemId, ReadVersion, usize, Fault)>();
    let vfs = Arc::new(
        Vfs::new(
            db.clone(),
            Arc::clone(&client),
            Budget::new(
                usize::try_from(u64::from(tuning.read_ahead_memory_mb) * MB).unwrap_or(usize::MAX),
            ),
            u64::from(tuning.read_ahead_mb) * MB,
            &settings.mount.dir,
        )
        .with_cache(Arc::clone(&cache))
        .with_report(Arc::new(move |id, version, index, fault| {
            // Fails only at shutdown, when nobody records damage anymore.
            let _ = fault_tx.send((id, version, index, fault));
        })),
    );
    vfs.set_gap_tolerance(settings.health.degraded_tolerance);
    let handoff = Arc::new(crate::handoff::Writer::new(
        Arc::clone(&vfs),
        (boot.uid, boot.gid),
        &settings,
    ));
    queue.set_handoff(Arc::clone(&handoff) as _);
    let analyzes = crate::probe::available(&boot.ffprobe).await;
    queue.set_verifier(Arc::new(FirstCheck {
        settings: health_rx.clone(),
        analyzes,
    }));
    if infinidysk_core::experiment("par2-repair") {
        queue.enable_par2();
    }
    let worker = {
        let queue = Arc::clone(&queue);
        tokio::spawn(async move { queue.run().await })
    };
    let importing = {
        let queue = Arc::clone(&queue);
        move || !queue.active().is_empty()
    };
    let streaming = {
        let vfs = Arc::clone(&vfs);
        move || !vfs.sessions().open().is_empty()
    };
    let in_use = {
        let vfs = Arc::clone(&vfs);
        move |id| vfs.sessions().open().iter().any(|s| s.item == id)
    };
    let mut health = Health::new(db.clone(), Arc::clone(&client), arr_rx, health_rx)
        .context("setting up health checks")?
        .with_gates(importing.clone(), streaming.clone())
        .with_in_use(in_use);
    let busy = {
        let (importing, streaming) = (importing.clone(), streaming.clone());
        move || importing() || streaming()
    };
    if analyzes {
        health = health.with_analyzer(crate::probe::analyzer(
            Arc::clone(&vfs),
            boot.ffprobe.clone(),
            move || importing() || streaming(),
        ));
    }
    if infinidysk_core::experiment("par2-repair") {
        health = health.with_par2();
    }
    let health = Arc::new(health);
    let health_task = {
        let health = Arc::clone(&health);
        tokio::spawn(async move { health.run().await })
    };
    tokio::spawn(crate::upgrade::retry_skipped(
        db.clone(),
        boot.config_dir.clone(),
        Arc::clone(&client),
        settings.clone(),
        cancel.clone(),
    ));
    // Playback damage, batched per file so a broken stretch costs one blob rewrite.
    tokio::spawn({
        let health = Arc::clone(&health);
        let db = db.clone();
        async move {
            while let Some(first) = fault_rx.recv().await {
                let mut files: HashMap<ItemId, (ReadVersion, Vec<usize>, Vec<usize>)> =
                    HashMap::new();
                let mut next = Some(first);
                while let Some((id, version, index, fault)) = next {
                    if version.is_current() {
                        let entry = files
                            .entry(id)
                            .or_insert_with(|| (version.clone(), Vec::new(), Vec::new()));
                        if !entry.0.is_current() {
                            *entry = (version, Vec::new(), Vec::new());
                        }
                        match fault {
                            Fault::Missing => entry.1.push(index),
                            Fault::Corrupt => entry.2.push(index),
                        }
                    }
                    next = fault_rx.try_recv().ok();
                }
                for (id, (version, missing, corrupt)) in files {
                    // Publication pauses background work before taking item writers.
                    let _writing = db.write_permit().await;
                    let Some(_lease) = version.read().await else {
                        continue;
                    };
                    if let Err(error) = health.report(id, &missing, &corrupt).await {
                        warn!(item = %id, reason = %error, "could not record playback damage");
                    }
                }
            }
        }
    });
    if !infinidysk_core::experiments().is_empty() {
        tracing::warn!(experiments = ?infinidysk_core::experiments(), "experiments on");
    }
    if boot.trace_minutes > 0 {
        info!(minutes = boot.trace_minutes, "tracing new reads");
        vfs.sessions().set_tracing(Some(Tracing {
            until: Instant::now() + Duration::from_secs(u64::from(boot.trace_minutes) * 60),
            capacity: admin::TRACE_CAPACITY,
        }));
    }
    let fuse = fuse_enabled(settings.mount.fuse);
    let (mount_tx, mount_rx) = watch::channel(Err(if fuse {
        crate::healthcheck::MOUNTING.to_owned()
    } else {
        fuse_off_reason(settings.mount.fuse).to_owned()
    }));
    #[cfg(not(target_os = "linux"))]
    drop(mount_tx);
    #[cfg(target_os = "linux")]
    let invalidator = Arc::new(infinidysk_fuse::Invalidator::default());
    #[cfg(target_os = "linux")]
    let mount = fuse.then(|| {
        tokio::spawn(infinidysk_fuse::run(
            Arc::clone(&vfs),
            Arc::clone(&invalidator),
            infinidysk_fuse::Config {
                mount_dir: settings.mount.dir.clone().into(),
                uid: boot.uid,
                gid: boot.gid,
                max_read: 1 << 20,
                max_readahead: tuning.fuse_max_readahead_kb * 1000,
                max_background: tuning.fuse_max_background,
                epoch_times: settings.mount.epoch_times,
            },
            mount_tx,
            cancel.clone(),
        ))
    });
    let maintenance_live = Arc::new(crate::maintenance::Tracker::default());
    let healthcheck = Arc::new(crate::healthcheck::Healthcheck::new(
        db.clone(),
        mount_rx.clone(),
        fuse.then(|| settings.mount.dir.clone().into()),
        worker.abort_handle(),
    ));
    let metrics = Arc::new(Metrics::new(
        &boot.metrics_path,
        Sources {
            client: Arc::clone(&client),
            vfs: Arc::clone(&vfs),
            queue: Arc::clone(&queue),
            health: Arc::clone(&health),
            cache: Arc::clone(&cache),
            maintenance: Arc::clone(&maintenance_live),
            mount: mount_rx.clone(),
        },
    ));
    let metrics_task = {
        let metrics = Arc::clone(&metrics);
        let cancel = cancel.clone();
        tokio::spawn(async move { metrics.run(cancel).await })
    };

    let legacy_strm = crate::upgrade::legacy_strm(&db).await;
    let dav = Arc::new(WebDav {
        vfs: Arc::clone(&vfs),
        queue: Arc::clone(&queue),
        access: std::sync::RwLock::new(Access {
            legacy_strm,
            ..access(&settings)
        }),
    });
    let live = Live {
        client: Arc::clone(&client),
        dav: Arc::clone(&dav),
        cache: Arc::clone(&cache),
        arr: arr_tx.clone(),
        health: health_tx,
        providers: Mutex::new(settings.providers.clone()),
        quotas,
        bandwidth,
        cleanup,
        mount_dir: settings.mount.dir.clone(),
        fuse: settings.mount.fuse,
        epoch_times: settings.mount.epoch_times,
        tuning,
        experiments: settings.experiments.clone(),
        handoff: Arc::clone(&handoff),
    };
    let config = Arc::new(RwLock::new(store));
    let auth = Arc::new(
        Auth::load(db.clone(), Arc::clone(&config))
            .await
            .context("loading the admin account")?,
    );
    let sab =
        Sab::new(Arc::clone(&queue), Arc::clone(&config)).context("building the NZB fetcher")?;
    let discovery = Arc::new(
        Discovery::new(
            Arc::clone(&config),
            Arc::clone(&sab.probes),
            Arc::clone(&queue),
        )
        .context("building the Arr client")?,
    );
    let cleanup_task = tokio::spawn(crate::cleanup::run(
        db.clone(),
        Arc::clone(&live.cleanup),
        Arc::clone(&discovery),
        arr_tx.subscribe(),
        Arc::clone(&handoff),
    ));
    let library = Arc::new(
        crate::library::Library::load(crate::library::Parts {
            db: db.clone(),
            vfs: Arc::clone(&vfs),
            handoff: Arc::clone(&handoff),
            dav: Arc::clone(&dav),
            config: Arc::clone(&config),
            discovery: Arc::clone(&discovery),
            mount_dir: settings.mount.dir.clone(),
            busy: Box::new(busy),
        })
        .await,
    );
    let library_task = tokio::spawn(Arc::clone(&library).run());
    let repair_task = if infinidysk_core::experiment("par2-repair") {
        let publisher = crate::repair::Publisher::new(Arc::clone(&repairs), Arc::clone(&vfs))?;
        #[cfg(target_os = "linux")]
        let publisher = if fuse {
            publisher.with_mount(invalidator)
        } else {
            publisher
        };
        Some(tokio::spawn(Arc::clone(&repairs).run(
            Arc::clone(&client),
            publisher,
            Arc::clone(&health),
            cancel.clone(),
        )))
    } else {
        None
    };
    let restart = Arc::new(AtomicBool::new(false));
    let backups = Arc::new(
        crate::backup::Backups::new(crate::backup::Parts {
            db: db.clone(),
            repairs: Arc::clone(&repairs),
            config: Arc::clone(&config),
            // After a cutover the database is PostgreSQL, whatever the boot URL says.
            live: if db.is_postgres() {
                None
            } else {
                sqlite_path(&boot.database_url)
            },
            config_dir: boot.config_dir.clone(),
            owner: (boot.uid, boot.gid),
            tracker: Arc::clone(&maintenance_live),
        })
        .await,
    );
    let maintenance = crate::maintenance::Maintenance::load(crate::maintenance::Parts {
        db: db.clone(),
        vfs: Arc::clone(&vfs),
        handoff,
        config: Arc::clone(&config),
        library: Arc::clone(&library),
        discovery: Arc::clone(&discovery),
        queue: Arc::clone(&queue),
        metrics: Arc::clone(&metrics),
        mount_dir: settings.mount.dir.clone(),
        live: maintenance_live,
        backups: Arc::clone(&backups),
    })
    .await;
    let maintenance_task = tokio::spawn(Arc::clone(&maintenance).run());
    let discovery_task = {
        let discovery = Arc::clone(&discovery);
        let instances = arr_tx.subscribe();
        let events = arr_tx.subscribe();
        tokio::spawn(async move {
            tokio::join!(
                discovery.run(instances),
                discovery.notify(),
                discovery.apply_events(events),
                discovery.manage_queues()
            )
        })
    };
    let updates = Arc::new(crate::update::Updates::default());
    let update_task = {
        let updates = Arc::clone(&updates);
        let cancel = cancel.clone();
        tokio::spawn(async move { updates.run(cancel).await })
    };
    let database = crate::database::Migrator::new(
        db.clone(),
        boot,
        std::path::Path::new(crate::database::CONFIG_DIR),
        booted.file,
        crate::database::LIMITS,
        booted.error,
    );
    database.resume().await;
    let admin = Arc::new(Admin {
        config: Arc::clone(&config),
        queue: Arc::clone(&queue),
        client,
        vfs,
        health,
        live,
        metrics,
        arr: discovery,
        tests: crate::provider_test::Tests::load(db.clone())
            .await
            .context("loading provider speeds")?,
        mount: mount_rx,
        library,
        maintenance,
        backups,
        updates,
        database: Arc::clone(&database),
        cancel: cancel.clone(),
        restart: Arc::clone(&restart),
    });
    let hooks = crate::arr::router(Arc::clone(&admin.arr));
    let adoption = Arc::new(crate::upgrade::WebDavAdoption::load(Arc::clone(&admin)).await);
    let app = sab::router(Arc::new(sab))
        .merge(hooks)
        .merge(crate::healthcheck::router(healthcheck))
        .merge(admin::router(admin, Arc::clone(&auth)))
        .merge(auth::router(auth))
        .merge(
            infinidysk_webdav::router(dav).layer(axum::middleware::from_fn_with_state(
                adoption,
                crate::upgrade::adopt_webdav,
            )),
        )
        .layer(axum::middleware::from_fn(ui::serve));
    // Before routing, so probe calls reach the SAB route.
    let app = axum::middleware::map_request(probe_path).layer(app);

    info!(addr = %listener.local_addr()?, "listening");
    let server = axum::serve(
        listener,
        axum::ServiceExt::<Request>::into_make_service_with_connect_info::<SocketAddr>(app),
    )
    .with_graceful_shutdown(cancel.clone().cancelled_owned());
    let served = tokio::select! {
        served = server.into_future() => served.context("serving HTTP"),
        () = async {
            cancel.cancelled().await;
            tokio::time::sleep(DRAIN).await;
        } => {
            warn!(reason = "open responses did not finish in time", "closing open connections");
            Ok(())
        }
    };
    cancel.cancel();
    if let Some(mut task) = repair_task
        && tokio::time::timeout(DRAIN, &mut task).await.is_err()
    {
        task.abort();
        warn!(
            reason = "repair finalization is still pending",
            "repair will reconcile after restart"
        );
    }
    worker.abort();
    health_task.abort();
    discovery_task.abort();
    cleanup_task.abort();
    library_task.abort();
    maintenance_task.abort();
    update_task.abort();
    warm_task.abort();
    optimize_task.abort();
    database.stop().await;
    let _ = metrics_task.await;
    let _ = quota_task.await;
    #[cfg(target_os = "linux")]
    if let Some(mount) = mount {
        let _ = mount.await;
    }
    db.close().await;
    info!("stopped");
    served.map(|()| {
        if restart.load(Ordering::Relaxed) {
            Exit::Restart
        } else {
            Exit::Stopped
        }
    })
}

/// Serves an Arr's probe calls, `…/infinidysk-probe-<nonce>/api`, as the SAB API.
async fn probe_path(mut req: Request) -> Request {
    if let Some(nonce) = crate::arr::nonce(req.uri().path()).map(str::to_owned) {
        let uri = req
            .uri()
            .query()
            .map_or_else(|| "/api".to_owned(), |q| format!("/api?{q}"));
        if let Ok(uri) = uri.parse() {
            *req.uri_mut() = uri;
            req.extensions_mut().insert(Probe(nonce));
        }
    }
    req
}

fn set_rules(queue: &Queue, settings: &Settings) {
    queue.update_rules(|r| {
        r.check_percent = settings.health.check_percent;
        r.blocklist.clone_from(&settings.queue.extra_blocklist);
    });
}

/// Arr apps flag the SAB category folders as missing until something lands in them.
async fn add_categories(queue: &Queue, settings: &Settings) {
    let categories: Vec<&str> = settings.sab.categories().filter(|c| *c != "*").collect();
    if let Err(error) = queue.add_categories(&categories).await {
        warn!(reason = %error, "could not create the category folders");
    }
}

/// Enabled instances; health repairs through those whose profile supports it.
pub(crate) fn arr_instances(instances: &[config::ArrInstance]) -> Vec<Arr> {
    instances.iter().filter(|a| a.enabled).map(to_arr).collect()
}

pub(crate) fn to_arr(a: &config::ArrInstance) -> Arr {
    Arr {
        id: a.id,
        name: a.name.clone(),
        profile: match a.profile {
            config::ArrProfile::Sonarr => Profile::Sonarr,
            config::ArrProfile::Radarr => Profile::Radarr,
            config::ArrProfile::Lidarr => Profile::Lidarr,
            config::ArrProfile::Chaptarr => Profile::Chaptarr,
            config::ArrProfile::Whisparr => Profile::Whisparr,
            config::ArrProfile::Sportarr => Profile::Sportarr,
        },
        url: a.url.clone(),
        api_key: a.api_key.0.clone(),
    }
}

fn health_settings(h: &config::Health) -> HealthSettings {
    HealthSettings {
        enabled: h.enabled,
        sample_percent: h.check_percent,
        auto_repair: h.auto_repair,
        degraded_tolerance: h.degraded_tolerance,
        unowned_action: match h.unowned_action {
            config::UnownedAction::Delete => UnownedAction::Delete,
            config::UnownedAction::ActionNeeded => UnownedAction::ActionNeeded,
        },
        media_probe: h.media_probe,
    }
}

/// Counts an import at a 100 % sample as its files' first health check.
struct FirstCheck {
    settings: watch::Receiver<HealthSettings>,
    /// ffprobe is there to analyze good files.
    analyzes: bool,
}

impl Verifier for FirstCheck {
    fn verify<'a>(
        &'a self,
        articles: &'a dyn Articles,
        output: &'a Output,
        posted: Option<i64>,
    ) -> Pin<
        Box<
            dyn Future<Output = Result<Option<infinidysk_db::FirstCheck>, import::Error>>
                + Send
                + 'a,
        >,
    > {
        let analyze = self.analyzes && self.settings.borrow().media_probe;
        Box::pin(async move {
            let name = output.path.last().map_or("", String::as_str);
            infinidysk_health::first_check(articles, &output.meta, name, posted, analyze)
                .await
                .map_err(|error| match error {
                    Unverified::Damaged(message) => import::Error::Missing(message),
                    Unverified::Inconclusive(error) => import::Error::Retry(error),
                })
        })
    }
}

/// Settings a save applies without a restart.
pub struct Live {
    client: Arc<Client>,
    dav: Arc<WebDav>,
    cache: Arc<Cache>,
    arr: watch::Sender<Vec<Arr>>,
    health: watch::Sender<HealthSettings>,
    /// Last applied, so saves that leave providers alone keep warm connections.
    providers: Mutex<Vec<config::Provider>>,
    quotas: Arc<Quotas>,
    bandwidth: Arc<Bandwidth>,
    /// Wakes the cleanup after a retention change.
    cleanup: Arc<Notify>,
    /// What FUSE mounted; changing it needs a restart.
    mount_dir: String,
    /// `mount.fuse` as saved at startup.
    fuse: bool,
    epoch_times: bool,
    /// Tuning at startup, for the parts that need a restart.
    tuning: Tuning,
    experiments: Vec<String>,
    handoff: Arc<crate::handoff::Writer>,
}

impl Live {
    pub async fn apply(&self, settings: &Settings) -> anyhow::Result<()> {
        self.quotas.configure(&settings.providers);
        self.bandwidth.set_limit(download_limit(settings));
        {
            // Quota edits apply through the shared usage counters, without new pools.
            let pools = |providers: &[config::Provider]| -> Vec<config::Provider> {
                providers
                    .iter()
                    .map(|p| config::Provider {
                        quota: None,
                        ..p.clone()
                    })
                    .collect()
            };
            let mut current = self
                .providers
                .lock()
                .unwrap_or_else(PoisonError::into_inner);
            if pools(&current) != pools(&settings.providers) {
                self.client.replace(providers(
                    &settings.providers,
                    &self.quotas,
                    &self.bandwidth,
                )?);
                current.clone_from(&settings.providers);
                drop(current);
                let client = Arc::clone(&self.client);
                tokio::spawn(async move { client.warm().await });
            }
        }
        {
            // Replaced STRM keys and C#'s earlier form are the library converter's.
            let mut current = self
                .dav
                .access
                .write()
                .unwrap_or_else(PoisonError::into_inner);
            *current = Access {
                previous_strm: std::mem::take(&mut current.previous_strm),
                legacy_strm: current.legacy_strm.take(),
                ..access(settings)
            };
        }
        self.arr
            .send_replace(arr_instances(&settings.arr_instances));
        self.health.send_replace(health_settings(&settings.health));
        self.dav
            .queue
            .set_concurrency(settings.queue.concurrency.into());
        self.dav
            .queue
            .db()
            .set_nzb_retention_days(settings.queue.nzb_retention_days);
        self.cleanup.notify_one();
        set_rules(&self.dav.queue, settings);
        self.handoff.configure(settings);
        add_categories(&self.dav.queue, settings).await;
        let tuning = settings.performance.effective();
        self.dav
            .vfs
            .set_read_ahead(u64::from(tuning.read_ahead_mb) * MB);
        self.dav
            .vfs
            .set_gap_tolerance(settings.health.degraded_tolerance);
        self.cache.configure(&cache_config(&tuning)).await;
        Ok(())
    }

    // ponytail: the shared read-ahead budget and FUSE knobs are fixed at startup;
    // resize them live if restarts for tuning become a nuisance.
    pub fn restart_required(&self, settings: &Settings) -> bool {
        let tuning = settings.performance.effective();
        settings.mount.dir != self.mount_dir
            || settings.mount.fuse != self.fuse
            || settings.mount.epoch_times != self.epoch_times
            || tuning.read_ahead_memory_mb != self.tuning.read_ahead_memory_mb
            || tuning.fuse_max_readahead_kb != self.tuning.fuse_max_readahead_kb
            || tuning.fuse_max_background != self.tuning.fuse_max_background
            || settings.experiments != self.experiments
    }

    pub const fn cache(&self) -> &Arc<Cache> {
        &self.cache
    }

    pub fn quotas(&self) -> &Quotas {
        &self.quotas
    }

    pub fn mount_dir(&self) -> &str {
        &self.mount_dir
    }

    /// Whether this process serves the built-in FUSE mount; not whether it is mounted.
    pub const fn fuse_enabled(&self) -> bool {
        fuse_enabled(self.fuse)
    }

    /// Checks that STRM files can be written in `dir`.
    pub async fn probe_strm_dir(&self, dir: &str) -> Result<(), String> {
        self.handoff.probe(dir).await
    }
}

const fn fuse_enabled(setting: bool) -> bool {
    setting && cfg!(target_os = "linux")
}

/// Why the built-in mount is not served.
const fn fuse_off_reason(setting: bool) -> &'static str {
    if setting {
        "FUSE mounts need Linux; use WebDAV"
    } else {
        "turned off in Settings → Mount"
    }
}

fn cache_config(tuning: &Tuning) -> CacheConfig {
    CacheConfig {
        memory_enabled: tuning.memory_cache_enabled,
        memory_bytes: u64::from(tuning.memory_cache_mb) * MB,
        disk: tuning.disk_cache_enabled.then(|| DiskConfig {
            dir: tuning.disk_cache_dir.clone().into(),
            max_bytes: u64::from(tuning.disk_cache_gb) * 1000 * MB,
            max_age: Duration::from_secs(u64::from(tuning.disk_cache_max_age_hours) * 3600),
        }),
    }
}

fn access(settings: &Settings) -> Access {
    let webdav = &settings.webdav;
    Access {
        auth: DavAuth::Basic {
            user: webdav.user.clone(),
            password: webdav.password.0.clone(),
        },
        download_key: Some(webdav.download_key.0.clone()),
        link_key: Some(settings.auth.session_key.0.clone()).filter(|k| !k.is_empty()),
        strm_key: Some(settings.imports.strm_key.0.clone()).filter(|k| !k.is_empty()),
        previous_strm: Vec::new(),
        legacy_strm: None,
        trusted_proxies: settings
            .network
            .trusted_proxies
            .iter()
            .filter_map(|p| infinidysk_webdav::parse_proxy(p))
            .collect(),
    }
}

/// Bytes per second.
pub(crate) fn download_limit(settings: &Settings) -> Option<u64> {
    settings
        .network
        .download_limit_mb_per_s
        .map(|mb| u64::from(mb) * MB)
}

pub(crate) fn providers(
    providers: &[config::Provider],
    quotas: &Quotas,
    bandwidth: &Arc<Bandwidth>,
) -> anyhow::Result<Vec<Provider>> {
    let enabled: Vec<_> = providers.iter().filter(|p| p.enabled).collect();
    let primary = enabled.iter().map(|p| p.priority).min();
    if enabled.is_empty() {
        warn!(
            reason = "no Usenet provider is enabled",
            "imports and playback stay unavailable until a provider is added"
        );
    }
    let downloads = Downloads::new(
        enabled
            .iter()
            .map(|p| usize::from(p.max_connections) * pipeline_depth(p))
            .sum(),
    );
    let weight_large = infinidysk_core::experiment("weight-large");
    enabled
        .into_iter()
        .map(|p| {
            let config = PoolConfig {
                max_connections: p.max_connections.into(),
                pipeline_depth: pipeline_depth(p),
                idle_timeout: IDLE_TIMEOUT,
                min_connections: if Some(p.priority) == primary {
                    WARM_FLOOR.min(p.max_connections.into())
                } else {
                    0
                },
            };
            Ok(Provider {
                pool: Pool::new(
                    endpoint(p)?
                        .with_usage(quotas.usage(p.id))
                        .with_bandwidth(Arc::clone(bandwidth)),
                    config,
                    Arc::clone(&downloads),
                )?
                .with_weight_large(weight_large),
                key: p.id.to_string(),
                name: if p.name.is_empty() {
                    p.hostname.clone()
                } else {
                    p.name.clone()
                },
                priority: p.priority,
                backbone: p.backbone.clone(),
            })
        })
        .collect::<Result<Vec<_>, infinidysk_nntp::Error>>()
        .context("configuring Usenet providers")
}

/// BODY requests in flight per connection; a `pipe<N>` experiment overrides it.
fn pipeline_depth(p: &config::Provider) -> usize {
    infinidysk_core::experiments()
        .iter()
        .find_map(|e| e.strip_prefix("pipe")?.parse().ok())
        .unwrap_or_else(|| p.pipeline_depth.into())
}

pub(crate) fn endpoint(p: &config::Provider) -> Result<Endpoint, infinidysk_nntp::Error> {
    let credentials = (!p.username.is_empty()).then(|| Credentials {
        username: p.username.clone(),
        password: p.password.0.clone(),
    });
    let tls = if p.tls {
        TlsMode::Verified
    } else {
        TlsMode::Plain
    };
    Endpoint::new(
        p.hostname.clone(),
        p.port,
        tls,
        credentials,
        PROVIDER_TIMEOUT,
    )
}
