//! Combines lookups of one host name that overlap in time. Nothing is kept after a
//! lookup ends, so the system resolver still decides how long an answer lives.

use std::collections::HashMap;
use std::io;
use std::net::{IpAddr, SocketAddr};
use std::sync::{Arc, LazyLock, Mutex, PoisonError};
use std::time::Instant;

use tokio::sync::watch;

// Whole addresses, not IPs: IPv6 link-local answers carry a scope id.
type Outcome = Result<Arc<[SocketAddr]>, (io::ErrorKind, String)>;
type Slot = watch::Receiver<Option<Outcome>>;
type Map = Arc<Mutex<HashMap<String, Slot>>>;

/// Lookups for provider connects, shared by the whole process.
pub(crate) static PROVIDERS: LazyLock<Lookups> = LazyLock::new(Lookups::default);

/// The system resolver, as [`Lookups::resolve`] calls it.
pub(crate) async fn system(host: String) -> io::Result<Vec<SocketAddr>> {
    Ok(tokio::net::lookup_host((host.as_str(), 0)).await?.collect())
}

/// Lookups in flight, by host name.
// ponytail: a lookup has no timeout of its own, so a resolver call that never returns
// blocks new connects to that host until it does. Upgrade path: a maximum age after
// which a new caller replaces the entry (`Remove` then needs an identity check; the
// stuck blocking thread is still lost).
#[derive(Default)]
pub(crate) struct Lookups {
    map: Map,
}

/// Removes a lookup's entry when its task ends, even if it never ran.
struct Remove {
    map: Map,
    host: String,
}

impl Drop for Remove {
    fn drop(&mut self) {
        lock(&self.map).remove(&self.host);
    }
}

fn lock(map: &Map) -> std::sync::MutexGuard<'_, HashMap<String, Slot>> {
    map.lock().unwrap_or_else(PoisonError::into_inner)
}

impl Lookups {
    /// Resolves `host` with `lookup`, sharing the answer with every caller whose call
    /// overlaps. Callers set their own port; each one waits only as long as it is polled.
    pub(crate) async fn resolve<L, F>(
        &self,
        host: &str,
        port: u16,
        lookup: L,
    ) -> io::Result<Vec<SocketAddr>>
    where
        L: Fn(String) -> F,
        F: Future<Output = io::Result<Vec<SocketAddr>>> + Send + 'static,
    {
        if let Ok(ip) = host.parse::<IpAddr>() {
            return Ok(vec![SocketAddr::new(ip, port)]);
        }
        let slot = self.join(host, &lookup);
        wait(slot, host, port, lookup).await
    }

    /// Subscribes to `host`'s lookup in flight, or starts one.
    fn join<L, F>(&self, host: &str, lookup: &L) -> Slot
    where
        L: Fn(String) -> F,
        F: Future<Output = io::Result<Vec<SocketAddr>>> + Send + 'static,
    {
        let mut map = lock(&self.map);
        if let Some(slot) = map.get(host) {
            return slot.clone();
        }
        let (tx, slot) = watch::channel(None);
        map.insert(host.to_owned(), slot.clone());
        drop(map);
        // Built before the spawn, so the entry goes even if the task is dropped unpolled.
        let remove = Remove {
            map: Arc::clone(&self.map),
            host: host.to_owned(),
        };
        let resolving = lookup(host.to_owned());
        tokio::spawn(async move {
            let started = Instant::now();
            let outcome = resolving
                .await
                .map(Arc::from)
                .map_err(|error| (error.kind(), error.to_string()));
            tracing::debug!(
                host = remove.host.as_str(),
                ms = started.elapsed().as_millis(),
                ok = outcome.is_ok(),
                "provider address lookup"
            );
            tx.send_replace(Some(outcome));
        });
        slot
    }
}

/// Waits for `slot`'s answer; looks up directly if its task ended without one.
async fn wait<L, F>(mut slot: Slot, host: &str, port: u16, lookup: L) -> io::Result<Vec<SocketAddr>>
where
    L: Fn(String) -> F,
    F: Future<Output = io::Result<Vec<SocketAddr>>>,
{
    // `wait_for` checks the stored value before it reports a closed channel.
    let answer = slot
        .wait_for(Option::is_some)
        .await
        .ok()
        .and_then(|v| v.clone());
    let addrs = match answer {
        Some(Ok(addrs)) => addrs,
        Some(Err((kind, message))) => return Err(io::Error::new(kind, message)),
        None => lookup(host.to_owned()).await?.into(),
    };
    Ok(addrs
        .iter()
        .map(|&(mut addr)| {
            addr.set_port(port);
            addr
        })
        .collect())
}

#[cfg(test)]
mod tests {
    use std::sync::atomic::{AtomicUsize, Ordering};
    use std::time::Duration;

    use tokio::sync::Notify;

    use super::*;

    const IP: IpAddr = IpAddr::V4(std::net::Ipv4Addr::new(192, 0, 2, 1));

    /// Counts lookups that run; each one waits for `gate` when given.
    fn counting(
        runs: &Arc<AtomicUsize>,
        gate: Option<&Arc<Notify>>,
    ) -> impl Fn(String) -> std::pin::Pin<Box<dyn Future<Output = io::Result<Vec<SocketAddr>>> + Send>>
    {
        let runs = Arc::clone(runs);
        let gate = gate.cloned();
        move |_host| {
            let runs = Arc::clone(&runs);
            let gate = gate.clone();
            Box::pin(async move {
                runs.fetch_add(1, Ordering::SeqCst);
                if let Some(gate) = gate {
                    gate.notified().await;
                }
                Ok(vec![SocketAddr::new(IP, 0)])
            })
        }
    }

    async fn until_empty(lookups: &Lookups) {
        for _ in 0..100 {
            if lock(&lookups.map).is_empty() {
                return;
            }
            tokio::task::yield_now().await;
        }
        panic!("lookup entry was not removed");
    }

    #[tokio::test]
    async fn overlapping_callers_share_one_lookup() {
        let lookups = Lookups::default();
        let runs = Arc::new(AtomicUsize::new(0));
        let gate = Arc::new(Notify::new());
        let lookup = counting(&runs, Some(&gate));
        let (a, b, c, ()) = tokio::join!(
            lookups.resolve("news.example", 563, &lookup),
            lookups.resolve("news.example", 563, &lookup),
            lookups.resolve("news.example", 119, &lookup),
            async {
                tokio::task::yield_now().await;
                gate.notify_one();
            }
        );
        assert_eq!(a.unwrap(), vec![SocketAddr::new(IP, 563)]);
        assert_eq!(b.unwrap(), vec![SocketAddr::new(IP, 563)]);
        assert_eq!(c.unwrap(), vec![SocketAddr::new(IP, 119)]);
        assert_eq!(runs.load(Ordering::SeqCst), 1);
        until_empty(&lookups).await;
    }

    #[tokio::test]
    async fn finished_lookup_is_not_kept() {
        let lookups = Lookups::default();
        let runs = Arc::new(AtomicUsize::new(0));
        let lookup = counting(&runs, None);
        lookups.resolve("news.example", 563, &lookup).await.unwrap();
        until_empty(&lookups).await;
        lookups.resolve("news.example", 563, &lookup).await.unwrap();
        assert_eq!(runs.load(Ordering::SeqCst), 2);
    }

    #[tokio::test]
    async fn failure_reaches_every_waiter_and_is_not_kept() {
        let lookups = Lookups::default();
        let runs = Arc::new(AtomicUsize::new(0));
        let gate = Arc::new(Notify::new());
        let failing = |_host: String| {
            let runs = Arc::clone(&runs);
            let gate = Arc::clone(&gate);
            async move {
                runs.fetch_add(1, Ordering::SeqCst);
                gate.notified().await;
                Err::<Vec<SocketAddr>, _>(io::Error::new(io::ErrorKind::NotFound, "no such host"))
            }
        };
        let (a, b, ()) = tokio::join!(
            lookups.resolve("news.example", 563, &failing),
            lookups.resolve("news.example", 563, &failing),
            async {
                tokio::task::yield_now().await;
                gate.notify_one();
            }
        );
        for error in [a.unwrap_err(), b.unwrap_err()] {
            assert_eq!(error.kind(), io::ErrorKind::NotFound);
            assert_eq!(error.to_string(), "no such host");
        }
        until_empty(&lookups).await;
        gate.notify_one();
        lookups
            .resolve("news.example", 563, &failing)
            .await
            .unwrap_err();
        assert_eq!(runs.load(Ordering::SeqCst), 2);
    }

    #[tokio::test]
    async fn short_deadline_gives_up_alone() {
        let lookups = Lookups::default();
        let runs = Arc::new(AtomicUsize::new(0));
        let gate = Arc::new(Notify::new());
        let lookup = counting(&runs, Some(&gate));
        let (short, long, ()) = tokio::join!(
            tokio::time::timeout(
                Duration::from_millis(10),
                lookups.resolve("news.example", 563, &lookup)
            ),
            lookups.resolve("news.example", 563, &lookup),
            async {
                tokio::time::sleep(Duration::from_millis(50)).await;
                gate.notify_one();
            }
        );
        short.unwrap_err();
        assert_eq!(long.unwrap(), vec![SocketAddr::new(IP, 563)]);
        assert_eq!(runs.load(Ordering::SeqCst), 1);
    }

    #[tokio::test]
    async fn lookup_finishes_after_every_waiter_left() {
        let lookups = Lookups::default();
        let runs = Arc::new(AtomicUsize::new(0));
        let gate = Arc::new(Notify::new());
        let lookup = counting(&runs, Some(&gate));
        tokio::time::timeout(
            Duration::from_millis(10),
            lookups.resolve("news.example", 563, &lookup),
        )
        .await
        .unwrap_err();
        assert_eq!(lock(&lookups.map).len(), 1);
        gate.notify_one();
        until_empty(&lookups).await;
        assert_eq!(runs.load(Ordering::SeqCst), 1);
    }

    #[test]
    fn lookup_dropped_unpolled_falls_back() {
        let lookups = Lookups::default();
        let runs = Arc::new(AtomicUsize::new(0));
        let lookup = counting(&runs, None);
        let runtime = || {
            tokio::runtime::Builder::new_current_thread()
                .build()
                .unwrap()
        };
        let first = runtime();
        let slot = first.block_on(async { lookups.join("news.example", &lookup) });
        drop(first);
        assert!(lock(&lookups.map).is_empty());
        assert_eq!(runs.load(Ordering::SeqCst), 0);
        let addrs = runtime()
            .block_on(wait(slot, "news.example", 563, &lookup))
            .unwrap();
        assert_eq!(addrs, vec![SocketAddr::new(IP, 563)]);
        assert_eq!(runs.load(Ordering::SeqCst), 1);
    }

    #[tokio::test]
    async fn answer_survives_closed_channel() {
        let lookups = Lookups::default();
        let runs = Arc::new(AtomicUsize::new(0));
        let lookup = counting(&runs, None);
        let slot = lookups.join("news.example", &lookup);
        until_empty(&lookups).await;
        assert!(slot.has_changed().is_err(), "sender should be closed");
        let addrs = wait(slot, "news.example", 563, &lookup).await.unwrap();
        assert_eq!(addrs, vec![SocketAddr::new(IP, 563)]);
        assert_eq!(runs.load(Ordering::SeqCst), 1);
    }

    #[tokio::test]
    async fn slow_host_does_not_hold_up_another() {
        let lookups = Lookups::default();
        let runs = Arc::new(AtomicUsize::new(0));
        let gate = Arc::new(Notify::new());
        let slow = counting(&runs, Some(&gate));
        let fast = counting(&runs, None);
        let (a, ()) = tokio::join!(lookups.resolve("slow.example", 563, &slow), async {
            let b = lookups.resolve("fast.example", 563, &fast).await;
            assert_eq!(b.unwrap(), vec![SocketAddr::new(IP, 563)]);
            gate.notify_one();
        });
        a.unwrap();
    }

    #[tokio::test]
    async fn ipv6_scope_id_is_kept() {
        let lookups = Lookups::default();
        let scoped = std::net::SocketAddrV6::new(
            std::net::Ipv6Addr::new(0xfe80, 0, 0, 0, 0, 0, 0, 1),
            0,
            0,
            3,
        );
        let lookup = move |_host: String| async move { Ok(vec![SocketAddr::V6(scoped)]) };
        let addrs = lookups.resolve("fe80::1%lo0", 119, lookup).await.unwrap();
        let mut expected = scoped;
        expected.set_port(119);
        assert_eq!(addrs, vec![SocketAddr::V6(expected)]);
    }

    #[tokio::test]
    async fn ip_literal_skips_lookup() {
        let lookups = Lookups::default();
        let runs = Arc::new(AtomicUsize::new(0));
        let lookup = counting(&runs, None);
        for (host, ip) in [
            ("192.0.2.1", IP),
            ("::1", IpAddr::from([0, 0, 0, 0, 0, 0, 0, 1])),
        ] {
            let addrs = lookups.resolve(host, 119, &lookup).await.unwrap();
            assert_eq!(addrs, vec![SocketAddr::new(ip, 119)]);
        }
        assert_eq!(runs.load(Ordering::SeqCst), 0);
        assert!(lock(&lookups.map).is_empty());
    }
}
