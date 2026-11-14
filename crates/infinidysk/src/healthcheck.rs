//! Container health: `GET /healthz` judges the server's own state, and
//! `infinidysk healthcheck` asks it for Docker's `HEALTHCHECK`. The probe never touches
//! the mount itself, so a hung FUSE request cannot hang it.

use std::collections::BTreeMap;
use std::net::{IpAddr, Ipv4Addr, Ipv6Addr, SocketAddr};
use std::path::PathBuf;
use std::sync::Arc;
use std::sync::atomic::AtomicBool;
#[cfg(any(target_os = "linux", test))]
use std::sync::atomic::Ordering;
use std::time::{Duration, Instant};

use axum::extract::State;
use axum::http::{StatusCode, header};
use axum::response::{IntoResponse, Response};
use axum::routing::get;
use axum::{Json, Router};
use infinidysk_db::Db;
use infinidysk_vfs::Kernel;
use serde::Serialize;
use tokio::sync::{Mutex, watch};
use tokio::task::AbortHandle;
use tracing::{info, warn};

pub const PATH: &str = "/healthz";
/// The mount state until the first mount attempt ends.
pub const MOUNTING: &str = "mounting";
/// Per check; the database and the mount are asked at the same time.
const CHECK_TIMEOUT: Duration = Duration::from_secs(3);
/// How long a result answers later probes, so the open endpoint cannot load the server.
const FRESH: Duration = Duration::from_secs(5);
/// Below Docker's `--timeout=10s`, so a slow answer still prints why.
const PROBE_TIMEOUT: Duration = Duration::from_secs(5);

#[derive(Clone, Copy, Debug, PartialEq, Eq, Serialize)]
#[serde(rename_all = "lowercase")]
pub enum Check {
    Pass,
    Fail,
    /// Not ready yet, and expected to be soon.
    Pending,
    /// Does not apply to this install.
    Off,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq, Serialize)]
#[serde(rename_all = "lowercase")]
pub enum Status {
    Healthy,
    Starting,
    Unhealthy,
}

/// What `/healthz` answers: no reasons, those go to the log.
#[derive(Clone, Debug, Serialize)]
pub struct Report {
    pub status: Status,
    pub checks: BTreeMap<&'static str, Check>,
}

impl Report {
    pub fn new(checks: impl IntoIterator<Item = (&'static str, Check)>) -> Self {
        let checks: BTreeMap<_, _> = checks.into_iter().collect();
        let status = if checks.values().any(|c| *c == Check::Fail) {
            Status::Unhealthy
        } else if checks.values().any(|c| *c == Check::Pending) {
            Status::Starting
        } else {
            Status::Healthy
        };
        Self { status, checks }
    }
}

impl IntoResponse for Report {
    fn into_response(self) -> Response {
        let code = if self.status == Status::Healthy {
            StatusCode::OK
        } else {
            StatusCode::SERVICE_UNAVAILABLE
        };
        (code, [(header::CACHE_CONTROL, "no-store")], Json(self)).into_response()
    }
}

type Outcome = (Check, Option<String>);

const fn pass() -> Outcome {
    (Check::Pass, None)
}

/// Checks the running server: the database answers, the FUSE mount answers through the
/// kernel, and the import worker is alive.
#[cfg_attr(not(target_os = "linux"), allow(dead_code))] // Only Linux mounts.
pub struct Healthcheck {
    db: Db,
    mount: watch::Receiver<Result<Kernel, String>>,
    /// `None` when this process does not serve the built-in FUSE mount.
    mount_dir: Option<PathBuf>,
    queue: AbortHandle,
    /// A mount check still waits on the kernel.
    asking: Arc<AtomicBool>,
    /// Held while checking, so concurrent probes share one check.
    last: Mutex<Option<(Instant, Report)>>,
}

impl Healthcheck {
    pub fn new(
        db: Db,
        mount: watch::Receiver<Result<Kernel, String>>,
        mount_dir: Option<PathBuf>,
        queue: AbortHandle,
    ) -> Self {
        Self {
            db,
            mount,
            mount_dir,
            queue,
            asking: Arc::default(),
            last: Mutex::new(None),
        }
    }

    pub async fn report(&self) -> Report {
        let mut last = self.last.lock().await;
        if let Some((at, report)) = &*last
            && at.elapsed() < FRESH
        {
            return report.clone();
        }
        #[cfg(target_os = "linux")]
        let mount = self.mount();
        // Only Linux mounts.
        #[cfg(not(target_os = "linux"))]
        let mount = std::future::ready((Check::Off, None));
        let (database, mount) = tokio::join!(self.database(), mount);
        let outcomes = [
            ("database", database),
            ("mount", mount),
            ("queue", self.queue()),
        ];
        let before = last.as_ref().map(|(_, r)| &r.checks);
        for (name, (check, reason)) in &outcomes {
            let was = before.and_then(|b| b.get(name)).copied();
            match (was, check) {
                (Some(Check::Fail), Check::Fail) | (_, Check::Pending | Check::Off) => {}
                (_, Check::Fail) => warn!(
                    check = *name,
                    reason = reason.as_deref().unwrap_or("unknown"),
                    "health check failing"
                ),
                (Some(Check::Fail), Check::Pass) => {
                    info!(check = *name, "health check passes again");
                }
                (_, Check::Pass) => {}
            }
        }
        let report = Report::new(outcomes.map(|(name, (check, _))| (name, check)));
        *last = Some((Instant::now(), report.clone()));
        drop(last);
        report
    }

    async fn database(&self) -> Outcome {
        match tokio::time::timeout(CHECK_TIMEOUT, self.db.ping()).await {
            Ok(Ok(())) => pass(),
            Ok(Err(error)) => (Check::Fail, Some(error.to_string())),
            Err(_) => (
                Check::Fail,
                Some(format!("no answer within {} s", CHECK_TIMEOUT.as_secs())),
            ),
        }
    }

    fn queue(&self) -> Outcome {
        if self.queue.is_finished() {
            (Check::Fail, Some("the import worker stopped".to_owned()))
        } else {
            pass()
        }
    }

    #[cfg(target_os = "linux")]
    async fn mount(&self) -> Outcome {
        // Turned off: never touch `/dev/fuse` or whatever is mounted at the folder.
        let Some(dir) = self.mount_dir.clone() else {
            return (Check::Off, None);
        };
        let device = std::path::Path::new("/dev/fuse").exists();
        if let Some(outcome) = mount_state(device, &self.mount.borrow()) {
            return outcome;
        }
        match bounded(&self.asking, CHECK_TIMEOUT, move || {
            infinidysk_mount::answers(&dir)
        })
        .await
        {
            Ok(()) => pass(),
            Err(reason) => (Check::Fail, Some(reason)),
        }
    }
}

/// What the mount state alone tells; `None` when the mount itself must answer.
#[cfg(any(target_os = "linux", test))]
fn mount_state(device: bool, state: &Result<Kernel, String>) -> Option<Outcome> {
    match state {
        // WebDAV-only: the server warned once at startup.
        _ if !device => Some((Check::Off, None)),
        Ok(_) => None,
        Err(reason) if reason == MOUNTING => Some((Check::Pending, None)),
        Err(reason) => Some((Check::Fail, Some(reason.clone()))),
    }
}

/// Clears the in-flight flag when the blocking call returns, even by panic.
#[cfg(any(target_os = "linux", test))]
struct Asked(Arc<AtomicBool>);

#[cfg(any(target_os = "linux", test))]
impl Drop for Asked {
    fn drop(&mut self) {
        self.0.store(false, Ordering::Release);
    }
}

/// Runs `call` on its own thread for up to `limit`. A call that overruns keeps its
/// thread; until it returns, later calls fail at once instead of piling up threads.
/// Not the blocking pool: the runtime waits for that on shutdown, and a hung mount
/// would then hold up the exit.
#[cfg(any(target_os = "linux", test))]
async fn bounded<F>(asking: &Arc<AtomicBool>, limit: Duration, call: F) -> Result<(), String>
where
    F: FnOnce() -> std::io::Result<()> + Send + 'static,
{
    if asking.swap(true, Ordering::AcqRel) {
        return Err("an earlier check still waits for an answer".to_owned());
    }
    let asked = Asked(Arc::clone(asking));
    let (done, answer) = tokio::sync::oneshot::channel();
    std::thread::Builder::new()
        .name("healthcheck".to_owned())
        .spawn(move || {
            // Clear the flag before answering, so the next check never sees it set.
            let answer = {
                let _asked = asked;
                call()
            };
            let _ = done.send(answer);
        })
        .map_err(|error| format!("could not start the check: {error}"))?;
    match tokio::time::timeout(limit, answer).await {
        Ok(Ok(Ok(()))) => Ok(()),
        Ok(Ok(Err(error))) => Err(error.to_string()),
        Ok(Err(_)) => Err("the check panicked".to_owned()),
        Err(_) => Err(format!("no answer within {} s", limit.as_secs())),
    }
}

pub fn router(check: Arc<Healthcheck>) -> Router {
    Router::new()
        .route(
            PATH,
            get(|State(check): State<Arc<Healthcheck>>| async move { check.report().await }),
        )
        .with_state(check)
}

/// Where a client in the same network namespace reaches a server listening on `listen`.
pub fn local(listen: SocketAddr) -> SocketAddr {
    let ip = match listen.ip() {
        IpAddr::V4(ip) if ip.is_unspecified() => IpAddr::V4(Ipv4Addr::LOCALHOST),
        IpAddr::V6(ip) if ip.is_unspecified() => IpAddr::V6(Ipv6Addr::LOCALHOST),
        ip => ip,
    };
    SocketAddr::new(ip, listen.port())
}

/// Asks the server listening on `listen` for its health; the body when healthy.
pub async fn probe(listen: SocketAddr) -> anyhow::Result<String> {
    anyhow::ensure!(
        listen.port() != 0,
        "INFINIDYSK_LISTEN uses port 0, so the port is not known"
    );
    let url = format!("http://{}{PATH}", local(listen));
    let http = reqwest::Client::builder()
        .no_proxy()
        .timeout(PROBE_TIMEOUT)
        .build()?;
    let response = http
        .get(&url)
        .send()
        .await
        .map_err(|error| anyhow::anyhow!("no answer from {url}: {error}"))?;
    let code = response.status();
    let body = response
        .text()
        .await
        .map_err(|error| anyhow::anyhow!("{code} without a full answer from {url}: {error}"))?;
    anyhow::ensure!(code == reqwest::StatusCode::OK, "{code}: {body}");
    Ok(body)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn any_failure_is_unhealthy_and_any_pending_is_starting() {
        use Check::{Fail, Off, Pass, Pending};
        for (checks, status) in [
            (vec![Pass, Off], Status::Healthy),
            (vec![Pass, Pending, Off], Status::Starting),
            (vec![Pending, Fail], Status::Unhealthy),
        ] {
            let names = ["a", "b", "c"];
            let report = Report::new(names.into_iter().zip(checks));
            assert_eq!(report.status, status);
            let code = report.into_response().status();
            assert_eq!(code == StatusCode::OK, status == Status::Healthy);
        }
    }

    #[test]
    fn the_mount_is_asked_only_once_mounted() {
        let mounted = Ok(Kernel::default());
        let mounting = Err(MOUNTING.to_owned());
        let failed = Err("permission denied".to_owned());
        assert_eq!(mount_state(true, &mounted), None);
        assert_eq!(mount_state(true, &mounting), Some((Check::Pending, None)));
        assert_eq!(
            mount_state(true, &failed),
            Some((Check::Fail, Some("permission denied".to_owned())))
        );
        for state in [mounted, mounting, failed] {
            assert_eq!(mount_state(false, &state), Some((Check::Off, None)));
        }
    }

    #[test]
    fn unspecified_listen_addresses_are_probed_on_loopback() {
        for (listen, probed) in [
            ("0.0.0.0:3000", "127.0.0.1:3000"),
            ("[::]:3000", "[::1]:3000"),
            ("192.0.2.7:8080", "192.0.2.7:8080"),
            ("127.0.0.1:4000", "127.0.0.1:4000"),
        ] {
            assert_eq!(local(listen.parse().unwrap()).to_string(), probed);
        }
    }

    #[tokio::test]
    async fn a_hung_check_holds_one_thread_and_fails_the_next_at_once() {
        let asking = Arc::new(AtomicBool::new(false));
        let (release, hung) = std::sync::mpsc::channel::<()>();
        let limit = Duration::from_millis(50);
        let first = bounded(&asking, limit, move || {
            let _ = hung.recv();
            Ok(())
        })
        .await;
        assert!(first.unwrap_err().starts_with("no answer"));
        let started = Instant::now();
        let second = bounded(&asking, Duration::from_secs(60), || {
            panic!("a second call must not start while the first hangs")
        })
        .await;
        assert!(second.unwrap_err().contains("still waits"));
        assert!(started.elapsed() < Duration::from_secs(1));

        release.send(()).unwrap();
        while asking.load(Ordering::Acquire) {
            tokio::task::yield_now().await;
        }
        assert_eq!(bounded(&asking, limit, || Ok(())).await, Ok(()));
        let error = bounded(&asking, limit, || Err(std::io::Error::other("gone"))).await;
        assert_eq!(error, Err("gone".to_owned()));
    }

    #[test]
    fn a_hung_check_does_not_hold_up_shutdown() {
        let (release, hung) = std::sync::mpsc::channel::<()>();
        let (stopped, stop) = std::sync::mpsc::channel();
        std::thread::spawn(move || {
            let runtime = tokio::runtime::Builder::new_current_thread()
                .enable_time()
                .build()
                .unwrap();
            let asking = Arc::new(AtomicBool::new(false));
            let result = runtime.block_on(bounded(&asking, Duration::from_millis(50), move || {
                let _ = hung.recv();
                Ok(())
            }));
            drop(runtime);
            stopped.send(result).unwrap();
        });
        let result = stop.recv_timeout(Duration::from_secs(5));
        let _ = release.send(());
        assert!(result.unwrap().unwrap_err().starts_with("no answer"));
    }

    #[tokio::test]
    async fn a_200_without_its_full_body_is_not_healthy() {
        use tokio::io::{AsyncReadExt as _, AsyncWriteExt as _};
        let listener = tokio::net::TcpListener::bind("127.0.0.1:0").await.unwrap();
        let addr = listener.local_addr().unwrap();
        tokio::spawn(async move {
            let (mut socket, _) = listener.accept().await.unwrap();
            let _ = socket.read(&mut [0; 1024]).await;
            let head = "HTTP/1.1 200 OK\r\nContent-Length: 100\r\nConnection: close\r\n\r\n{";
            socket.write_all(head.as_bytes()).await.unwrap();
        });
        let error = probe(addr).await.unwrap_err().to_string();
        assert!(error.contains("without a full answer"), "{error}");
    }

    async fn healthcheck(queue: AbortHandle) -> Healthcheck {
        healthcheck_with(queue, Some(PathBuf::from("/nonexistent"))).await
    }

    async fn healthcheck_with(queue: AbortHandle, mount_dir: Option<PathBuf>) -> Healthcheck {
        let path = std::env::temp_dir().join(format!(
            "infinidysk-healthcheck-{}.sqlite",
            infinidysk_core::ItemId::new()
        ));
        let db = Db::connect(&format!("sqlite:{}", path.display()))
            .await
            .unwrap();
        let (_, mount) = watch::channel(Err(MOUNTING.to_owned()));
        Healthcheck::new(db, mount, mount_dir, queue)
    }

    #[tokio::test]
    async fn a_turned_off_mount_is_off_and_healthy() {
        let worker = tokio::spawn(std::future::pending::<()>());
        let check = healthcheck_with(worker.abort_handle(), None).await;
        let report = check.report().await;
        assert_eq!(report.checks["mount"], Check::Off);
        assert_eq!(report.status, Status::Healthy);
        worker.abort();
    }

    #[tokio::test]
    async fn a_stopped_import_worker_is_unhealthy_after_the_cached_answer() {
        let worker = tokio::spawn(std::future::pending::<()>());
        let check = healthcheck(worker.abort_handle()).await;
        let report = check.report().await;
        assert_eq!(report.checks["database"], Check::Pass);
        assert_eq!(report.checks["queue"], Check::Pass);
        worker.abort();
        let _ = worker.await;
        // Cached: an open endpoint cannot make the server check more often.
        assert_eq!(check.report().await.checks["queue"], Check::Pass);
        *check.last.lock().await = None;
        let report = check.report().await;
        assert_eq!(report.checks["queue"], Check::Fail);
        assert_eq!(report.status, Status::Unhealthy);
    }
}
