use std::sync::Mutex;
use std::sync::PoisonError;
use std::sync::atomic::{AtomicU64, Ordering};
use std::time::Duration;

use tokio::sync::Notify;
use tokio::time::{Instant, timeout_at};

/// Bytes a reader may get ahead of the limit, as time at the limited rate.
const BURST: Duration = Duration::from_millis(250);

/// One download speed limit shared by every provider connection it is attached to.
/// Attach with [`Endpoint::with_bandwidth`](crate::Endpoint::with_bandwidth).
#[derive(Debug)]
pub struct Bandwidth {
    /// Bytes per second; 0 for no limit.
    rate: AtomicU64,
    /// When the bytes taken so far are paid off at `rate`.
    paid_at: Mutex<Instant>,
    changed: Notify,
}

impl Bandwidth {
    #[must_use]
    pub fn new(bytes_per_s: Option<u64>) -> Self {
        Self {
            rate: AtomicU64::new(bytes_per_s.unwrap_or(0)),
            paid_at: Mutex::new(Instant::now()),
            changed: Notify::new(),
        }
    }

    /// Applies at once, also to readers already waiting.
    pub fn set_limit(&self, bytes_per_s: Option<u64>) {
        let rate = bytes_per_s.unwrap_or(0);
        if self.rate.swap(rate, Ordering::Relaxed) != rate {
            *self.paid_at.lock().unwrap_or_else(PoisonError::into_inner) = Instant::now();
            self.changed.notify_waiters();
        }
    }

    /// Waits until `bytes` already read fit under the limit. Callers queue in order.
    pub(crate) async fn take(&self, bytes: u64) {
        loop {
            let changed = self.changed.notified();
            let rate = self.rate.load(Ordering::Relaxed);
            if rate == 0 {
                return;
            }
            let nanos = u128::from(bytes) * 1_000_000_000 / u128::from(rate);
            let cost = Duration::from_nanos(u64::try_from(nanos).unwrap_or(u64::MAX));
            let now = Instant::now();
            let ready = {
                let mut paid_at = self.paid_at.lock().unwrap_or_else(PoisonError::into_inner);
                *paid_at = (*paid_at).max(now) + cost;
                paid_at.checked_sub(BURST)
            };
            let Some(ready) = ready.filter(|&ready| ready > now) else {
                return;
            };
            // A new limit forgives the debt; take again at the new rate.
            if timeout_at(ready, changed).await.is_err() {
                return;
            }
        }
    }
}
