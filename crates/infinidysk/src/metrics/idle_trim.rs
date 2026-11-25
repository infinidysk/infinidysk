use std::io;
use std::time::{Duration, Instant};

use tokio_util::sync::CancellationToken;

use super::SAMPLE_EVERY;

const IDLE_FOR: Duration = Duration::from_mins(2);

pub(super) async fn release(sampled_at: Instant, cancel: CancellationToken) -> bool {
    tokio::task::spawn_blocking(move || {
        let before = rss_bytes();
        if cancel.is_cancelled() || sampled_at.elapsed() >= SAMPLE_EVERY {
            return false;
        }
        let start = Instant::now();
        let released = infinidysk_allocator::trim();
        let elapsed_ms = super::ms(start.elapsed());
        log_trim(released, elapsed_ms, before, rss_bytes());
        true
    })
    .await
    .unwrap_or_else(|error| {
        tracing::warn!(reason = %error, "idle memory trim did not finish");
        false
    })
}

fn rss_bytes() -> io::Result<u64> {
    let status = std::fs::read_to_string("/proc/self/status")?;
    super::kb_field(&status, "VmRSS")
        .ok_or_else(|| io::Error::new(io::ErrorKind::InvalidData, "missing or invalid VmRSS"))
}

fn log_trim(released: bool, elapsed_ms: u64, before: io::Result<u64>, after: io::Result<u64>) {
    #[allow(clippy::cast_precision_loss)]
    // A process's RSS is below the exact integer limit of f64.
    let mb = |bytes: u64| bytes as f64 / 1_000_000.0;
    match (before, after) {
        (Ok(before), Ok(after)) => tracing::info!(
            released,
            rss_drop_mb = mb(before.saturating_sub(after)),
            rss_before_mb = mb(before),
            rss_after_mb = mb(after),
            elapsed_ms,
            "idle memory trim"
        ),
        (Err(error), _) | (_, Err(error)) => {
            tracing::info!(released, elapsed_ms, rss_error = %error, "idle memory trim");
        }
    }
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

    #[test]
    fn idle_trim_event_reports_observed_rss_drop_or_read_error() {
        use crate::logs::{Capture, LOGS, Level};
        use tracing_subscriber::layer::SubscriberExt as _;

        let subscriber = tracing_subscriber::registry().with(Capture::MEMORY);
        tracing::subscriber::with_default(subscriber, || {
            let _span = tracing::info_span!("test", task = "trim-rss-test").entered();
            log_trim(true, 21, Ok(12_000_000), Ok(9_500_000));
            log_trim(true, 14, Ok(9_500_000), Ok(10_000_000));
            log_trim(
                false,
                11,
                Ok(10_000_000),
                Err(io::Error::other("unavailable")),
            );
        });
        let entries = LOGS.snapshot(Level::Info, Some("trim-rss-test"));
        assert_eq!(entries.len(), 3);
        assert_eq!(entries[0].message, "idle memory trim");
        assert_eq!(
            entries[0].fields,
            "released=true rss_drop_mb=2.5 rss_before_mb=12.0 rss_after_mb=9.5 elapsed_ms=21"
        );
        assert!(entries[1].fields.contains("rss_drop_mb=0.0 "));
        assert_eq!(
            entries[2].fields,
            "released=false elapsed_ms=11 rss_error=unavailable"
        );
    }

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
