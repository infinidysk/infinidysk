//! Articles a backbone (or a provider without one) recently answered as missing, so
//! repeat requests skip it without touching the network.

use std::collections::{HashMap, VecDeque};
use std::sync::atomic::{AtomicU64, Ordering};
use std::sync::{Arc, Mutex, PoisonError};
use std::time::{Duration, Instant};

const TTL: Duration = Duration::from_secs(300);
const MAX_ENTRIES: usize = 10_000;

#[derive(Default)]
pub(crate) struct Missing {
    inner: Mutex<Inner>,
    hits: AtomicU64,
}

#[derive(Default)]
struct Inner {
    expires: HashMap<Arc<str>, Instant>,
    /// Insertion order; every entry lives [`TTL`], so the front expires first.
    order: VecDeque<(Arc<str>, Instant)>,
}

/// `group` is a backbone or provider key; the newline cannot occur in a message-id.
fn key(group: &str, message_id: &str) -> String {
    format!("{group}\n{message_id}")
}

impl Missing {
    pub(crate) fn contains(&self, group: &str, message_id: &str) -> bool {
        let now = Instant::now();
        let hit = self
            .lock()
            .expires
            .get(key(group, message_id).as_str())
            .is_some_and(|&at| at > now);
        if hit {
            self.hits.fetch_add(1, Ordering::Relaxed);
        }
        hit
    }

    pub(crate) fn insert(&self, group: &str, message_id: &str) {
        let now = Instant::now();
        let key: Arc<str> = key(group, message_id).into();
        let mut inner = self.lock();
        while let Some((front, at)) = inner.order.front()
            && (*at <= now || inner.order.len() >= MAX_ENTRIES)
        {
            let (front, at) = (Arc::clone(front), *at);
            inner.order.pop_front();
            // A re-inserted key has a later expiry and stays.
            if inner.expires.get(&front) == Some(&at) {
                inner.expires.remove(&front);
            }
        }
        let at = now + TTL;
        inner.expires.insert(Arc::clone(&key), at);
        inner.order.push_back((key, at));
    }

    /// Requests answered from here since start.
    pub(crate) fn hits(&self) -> u64 {
        self.hits.load(Ordering::Relaxed)
    }

    fn lock(&self) -> std::sync::MutexGuard<'_, Inner> {
        self.inner.lock().unwrap_or_else(PoisonError::into_inner)
    }
}
