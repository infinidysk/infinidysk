use std::sync::atomic::{AtomicU64, Ordering};

/// Bytes read from one provider's sockets, and the cap past which requests skip it.
/// Attach with [`Endpoint::with_usage`](crate::Endpoint::with_usage).
#[derive(Debug, Default)]
pub struct Usage {
    used: AtomicU64,
    /// 0 for no cap.
    cap: AtomicU64,
}

impl Usage {
    pub(crate) fn add(&self, bytes: u64) {
        self.used.fetch_add(bytes, Ordering::Relaxed);
    }

    #[must_use]
    pub fn used(&self) -> u64 {
        self.used.load(Ordering::Relaxed)
    }

    pub fn set_used(&self, bytes: u64) {
        self.used.store(bytes, Ordering::Relaxed);
    }

    #[must_use]
    pub fn cap(&self) -> Option<u64> {
        Some(self.cap.load(Ordering::Relaxed)).filter(|&cap| cap > 0)
    }

    pub fn set_cap(&self, cap: Option<u64>) {
        self.cap.store(cap.unwrap_or(0), Ordering::Relaxed);
    }

    /// Checked before each request, so requests in flight may overshoot the cap a little.
    #[must_use]
    pub fn exhausted(&self) -> bool {
        self.cap().is_some_and(|cap| self.used() >= cap)
    }
}
