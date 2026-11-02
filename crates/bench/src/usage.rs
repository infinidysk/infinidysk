//! Process CPU time and peak RSS. Both cover the whole process, loopback server
//! included, so they compare runs of one scenario rather than give absolute costs.

use std::time::Duration;

use nix::sys::resource::{UsageWho, getrusage};
use nix::sys::time::TimeValLike as _;

/// User plus system CPU time used by this process so far.
pub fn cpu() -> Duration {
    getrusage(UsageWho::RUSAGE_SELF).map_or(Duration::ZERO, |u| {
        let micros = u.user_time().num_microseconds() + u.system_time().num_microseconds();
        Duration::from_micros(micros.unsigned_abs())
    })
}

/// Milliseconds of CPU used since [`cpu`] returned `start`.
pub fn cpu_ms_since(start: Duration) -> f64 {
    cpu().saturating_sub(start).as_secs_f64() * 1000.0
}

/// Peak resident set size of this process, in bytes.
pub fn max_rss_bytes() -> u64 {
    let max = getrusage(UsageWho::RUSAGE_SELF).map_or(0, |u| u.max_rss().unsigned_abs());
    // Linux reports kilobytes (1024 bytes); macOS reports bytes.
    if cfg!(target_os = "macos") {
        max
    } else {
        max * 1024
    }
}
