use std::collections::VecDeque;
use std::sync::{Arc, Mutex, MutexGuard, PoisonError};

use tokio::sync::oneshot;

/// Request slots shared by every provider pool. A freed slot goes to the oldest waiting
/// playback request before any background (import, health) request, so imports never
/// make a stream wait behind them; requests already admitted run to completion.
#[derive(Debug)]
pub struct Downloads {
    lanes: Mutex<Lanes>,
}

type Waiter = (usize, oneshot::Sender<Permit>);

#[derive(Debug)]
struct Lanes {
    total: usize,
    free: usize,
    urgent: VecDeque<Waiter>,
    background: VecDeque<Waiter>,
}

/// One admitted request holding `.1` slots; dropping it hands them to the next waiters.
#[derive(Debug)]
pub(crate) struct Permit(Option<Arc<Downloads>>, usize);

impl Drop for Permit {
    fn drop(&mut self) {
        if let Some(owner) = self.0.take() {
            owner.release(self.1);
        }
    }
}

impl Downloads {
    #[must_use]
    pub fn new(permits: usize) -> Arc<Self> {
        Arc::new(Self {
            lanes: Mutex::new(Lanes {
                total: permits,
                free: permits,
                urgent: VecDeque::new(),
                background: VecDeque::new(),
            }),
        })
    }

    /// Slots nobody holds.
    #[must_use]
    pub fn available_permits(&self) -> usize {
        self.lock().free
    }

    /// Whether [`Self::acquire`] would admit this request right now. A snapshot, not a
    /// reservation.
    pub(crate) fn can_acquire(&self, urgent: bool, weight: usize) -> bool {
        let lanes = self.lock();
        let weight = weight.clamp(1, lanes.total.max(1));
        let queued = !lanes.urgent.is_empty() || (!urgent && !lanes.background.is_empty());
        !queued && lanes.free >= weight
    }

    /// Waits for `weight` slots (capped at the total); `urgent` waiters are served
    /// before every background one, each lane strictly in order.
    /// Cancel-safe: slots granted to a waiter that left are handed on.
    pub(crate) async fn acquire(self: &Arc<Self>, urgent: bool, weight: usize) -> Option<Permit> {
        let rx = {
            let mut lanes = self.lock();
            let weight = weight.clamp(1, lanes.total.max(1));
            let queued = !lanes.urgent.is_empty() || (!urgent && !lanes.background.is_empty());
            if !queued && lanes.free >= weight {
                lanes.free -= weight;
                return Some(Permit(Some(Arc::clone(self)), weight));
            }
            let (tx, rx) = oneshot::channel();
            let lane = if urgent {
                &mut lanes.urgent
            } else {
                &mut lanes.background
            };
            // Amortised sweep of waiters that gave up.
            if lane.len() >= 64 && lane.len().is_power_of_two() {
                lane.retain(|(_, w)| !w.is_closed());
            }
            lane.push_back((weight, tx));
            rx
        };
        rx.await.ok()
    }

    fn release(self: Arc<Self>, weight: usize) {
        let mut returned = weight;
        loop {
            let mut guard = self.lock();
            let Lanes {
                free,
                urgent,
                background,
                ..
            } = &mut *guard;
            *free += returned;
            for lane in [&mut *urgent, &mut *background] {
                while lane.front().is_some_and(|(_, w)| w.is_closed()) {
                    lane.pop_front();
                }
            }
            let lane = if urgent.is_empty() {
                background
            } else {
                urgent
            };
            match lane.front() {
                Some(&(next, _)) if next <= *free => *free -= next,
                _ => return,
            }
            let (next, waiter) = lane.pop_front().expect("front was just checked");
            drop(guard);
            returned = match waiter.send(Permit(Some(Arc::clone(&self)), next)) {
                Ok(()) => 0,
                // That waiter left; disarm so its slots are handed on here, not recursively.
                Err(mut permit) => {
                    permit.0 = None;
                    next
                }
            };
        }
    }

    fn lock(&self) -> MutexGuard<'_, Lanes> {
        self.lanes.lock().unwrap_or_else(PoisonError::into_inner)
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[tokio::test]
    async fn streams_overtake_waiting_background_requests() {
        let downloads = Downloads::new(1);
        let held = downloads.acquire(false, 1).await;
        let background = tokio::spawn({
            let d = Arc::clone(&downloads);
            async move { d.acquire(false, 1).await }
        });
        tokio::task::yield_now().await;
        let abandoned = {
            let d = Arc::clone(&downloads);
            tokio::spawn(async move { d.acquire(true, 1).await })
        };
        tokio::task::yield_now().await;
        abandoned.abort();
        let _ = abandoned.await;
        let stream = tokio::spawn({
            let d = Arc::clone(&downloads);
            async move { d.acquire(true, 1).await }
        });
        tokio::task::yield_now().await;

        drop(held);
        let stream = stream.await.unwrap();
        assert!(stream.is_some(), "the stream is served first");
        assert!(!background.is_finished());
        drop(stream);
        drop(background.await.unwrap());
        assert_eq!(downloads.available_permits(), 1, "no slot leaked");
    }

    #[tokio::test]
    async fn a_heavy_waiter_is_not_overtaken_by_lighter_ones() {
        let downloads = Downloads::new(4);
        let held = downloads.acquire(true, 2).await;
        assert!(downloads.can_acquire(true, 1));
        assert!(!downloads.can_acquire(true, 4));
        let heavy = tokio::spawn({
            let d = Arc::clone(&downloads);
            async move { d.acquire(true, 9).await }
        });
        tokio::task::yield_now().await;
        let light = tokio::spawn({
            let d = Arc::clone(&downloads);
            async move { d.acquire(true, 1).await }
        });
        tokio::task::yield_now().await;
        assert!(
            !light.is_finished(),
            "free slots wait for the older heavy request"
        );
        assert!(
            !downloads.can_acquire(true, 1),
            "an older heavy waiter goes first"
        );

        drop(held);
        let heavy = heavy.await.unwrap();
        assert_eq!(
            downloads.available_permits(),
            0,
            "weight is capped at the total"
        );
        drop(heavy);
        drop(light.await.unwrap());
        assert_eq!(downloads.available_permits(), 4, "no slot leaked");
        assert!(downloads.can_acquire(true, 9));
    }
}
