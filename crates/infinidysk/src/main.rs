use std::os::unix::process::CommandExt as _;
use std::process::Command;
use std::sync::Arc;
use std::sync::atomic::{AtomicBool, Ordering};

use anyhow::Context as _;
use infinidysk::app::{self, Boot, Exit};
use infinidysk::logs::{self, Capture};
use tokio::net::TcpListener;
use tokio::signal::unix::{SignalKind, signal};
use tokio_util::sync::CancellationToken;
use tracing_subscriber::layer::SubscriberExt as _;
use tracing_subscriber::util::SubscriberInitExt as _;

#[tokio::main]
async fn main() -> anyhow::Result<()> {
    if std::env::args().nth(1).as_deref() == Some("openapi") {
        println!("{}", infinidysk::admin::openapi_json()?);
        return Ok(());
    }
    if std::env::args().nth(1).as_deref() == Some("import-report") {
        let config = std::env::args()
            .nth(2)
            .unwrap_or_else(|| "/config".to_owned());
        print!("{}", infinidysk::upgrade::report(config.as_ref()).await?);
        return Ok(());
    }
    if std::env::args().nth(1).as_deref() == Some("healthcheck") {
        let listen = infinidysk::app::listen_from_env()?;
        match infinidysk::healthcheck::probe(listen).await {
            Ok(body) => println!("{body}"),
            // An expected answer, not a crash: one line, no backtrace.
            Err(error) => {
                eprintln!("unhealthy: {error:#}");
                std::process::exit(1);
            }
        }
        return Ok(());
    }
    let boot = Boot::from_env()?;
    let (capture, unkept) = match Capture::persisted(&boot.log_dir, (boot.uid, boot.gid)) {
        Ok(capture) => (capture, None),
        Err(error) => (Capture::MEMORY, Some(error)),
    };
    let filter = logs::filter(std::env::var("RUST_LOG").ok().as_deref());
    tracing_subscriber::registry()
        .with(filter)
        .with(tracing_subscriber::fmt::layer())
        .with(capture)
        .init();
    if let Some(error) = unkept {
        tracing::warn!(
            dir = %boot.log_dir.display(),
            reason = %error,
            "logs are not kept across restarts: directory is not writable"
        );
    }
    let listener = TcpListener::bind(boot.listen)
        .await
        .with_context(|| format!("listening on {}", boot.listen))?;
    let cancel = CancellationToken::new();
    let mut term = signal(SignalKind::terminate()).context("installing the SIGTERM handler")?;
    let stop = cancel.clone();
    let signalled = Arc::new(AtomicBool::new(false));
    let signal_seen = Arc::clone(&signalled);
    tokio::spawn(async move {
        tokio::select! {
            _ = tokio::signal::ctrl_c() => {}
            _ = term.recv() => {}
        }
        tracing::info!("shutting down");
        signal_seen.store(true, Ordering::Relaxed);
        stop.cancel();
    });
    let exit = app::serve(listener, &boot, std::env::vars(), cancel).await?;
    // A stop signal during a restart wins: the container is being stopped.
    if exit == Exit::Restart && !signalled.load(Ordering::Relaxed) {
        let exe = std::env::current_exe().context("finding the binary to restart")?;
        tracing::info!("starting again");
        let error = Command::new(exe).args(std::env::args_os().skip(1)).exec();
        return Err(error).context("restarting");
    }
    Ok(())
}
