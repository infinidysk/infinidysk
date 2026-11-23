use std::time::{Duration, Instant};

use tokio_util::sync::CancellationToken;

use super::SAMPLE_EVERY;

const IDLE_FOR: Duration = Duration::from_mins(2);

pub(super) async fn release(sampled_at: Instant, cancel: CancellationToken) -> bool {
    tokio::task::spawn_blocking(move || {
        if cancel.is_cancelled() || sampled_at.elapsed() >= SAMPLE_EVERY {
            return false;
        }
        let start = Instant::now();
        let released = infinidysk_allocator::trim();
        tracing::info!(
            released,
            elapsed_ms = super::ms(start.elapsed()),
            "idle memory trim"
        );
        true
    })
    .await
    .unwrap_or_else(|error| {
        tracing::warn!(reason = %error, "idle memory trim did not finish");
        false
    })
}

#[derive(Default)]
pub(super) enum IdleTrim {
    #[default]
    Disarmed,
    AfterWork,
    Quiet {
        since: Instant,
        last: Instant,
    },
}

impl IdleTrim {
    pub(super) fn sample(&mut self, now: Instant, busy: bool) -> bool {
        *self = match *self {
            _ if busy => Self::AfterWork,
            Self::Disarmed => Self::Disarmed,
            Self::Quiet { since, last } if now - last <= SAMPLE_EVERY * 2 => {
                if now - since >= IDLE_FOR {
                    *self = Self::Disarmed;
                    return true;
                }
                Self::Quiet { since, last: now }
            }
            Self::AfterWork | Self::Quiet { .. } => Self::Quiet {
                since: now,
                last: now,
            },
        };
        false
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[cfg(all(target_os = "linux", target_env = "gnu"))]
    #[tokio::test]
    async fn idle_trim_calls_glibc_only_for_a_fresh_non_cancelled_sample() {
        let cancel = CancellationToken::new();
        let stale = Instant::now()
            .checked_sub(SAMPLE_EVERY)
            .expect("past sample");
        assert!(!release(stale, cancel.clone()).await);
        assert!(release(Instant::now(), cancel.clone()).await);
        cancel.cancel();
        assert!(!release(Instant::now(), cancel).await);
    }

    fn quiet(timer: &mut IdleTrim, start: Instant, seconds: u32) -> Vec<u32> {
        (0..=seconds)
            .step_by(2)
            .filter(|&offset| timer.sample(start + Duration::from_secs(u64::from(offset)), false))
            .collect()
    }

    #[test]
    fn idle_trim_only_once_after_each_busy_period() {
        let mut timer = IdleTrim::default();
        let start = Instant::now();
        assert_eq!(quiet(&mut timer, start, 240), [0_u32; 0]);
        for offset in [242, 486] {
            let busy = start + Duration::from_secs(offset);
            assert!(!timer.sample(busy, true));
            assert_eq!(quiet(&mut timer, busy + SAMPLE_EVERY, 240), [120]);
        }
    }

    #[test]
    fn idle_trim_restarts_after_activity_or_missing_samples() {
        let mut timer = IdleTrim::default();
        let start = Instant::now();
        assert!(!timer.sample(start, true));
        assert_eq!(quiet(&mut timer, start + SAMPLE_EVERY, 118), [0_u32; 0]);
        assert!(!timer.sample(start + Duration::from_secs(122), true));
        assert_eq!(
            quiet(&mut timer, start + Duration::from_secs(124), 118),
            [0_u32; 0]
        );
        assert_eq!(
            quiet(&mut timer, start + Duration::from_secs(250), 126),
            [120]
        );
    }
}
