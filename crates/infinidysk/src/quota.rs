//! Bytes downloaded per provider this period, the quota cap, and period resets. Usage
//! counts every provider while running; caps come from the settings.

use std::collections::HashMap;
use std::sync::{Arc, Mutex, PoisonError};
use std::time::{Duration, SystemTime};

use axum::Json;
use axum::extract::{Path, State};
use infinidysk_db::Db;
use infinidysk_nntp::Usage;
use serde::{Deserialize, Serialize};
use tokio_util::sync::CancellationToken;
use utoipa::ToSchema;
use uuid::Uuid;

use crate::admin::{Admin, ApiError, Fail};
use crate::config::{self, QuotaPeriod};
use crate::metrics::unix_ms;

/// Config key of the stored usage; not part of [`config::Settings`], so writing it never
/// rebuilds the provider pools.
const KEY: &str = "provider_usage";
const FLUSH_EVERY: Duration = Duration::from_secs(60);
const GB: u64 = 1_000_000_000;
const DAY_MS: i64 = 86_400_000;

#[derive(Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
struct Stored {
    used: u64,
    /// Unix ms the period started.
    since: i64,
}

struct Tracked {
    usage: Arc<Usage>,
    since: i64,
    /// The provider's display name and quota, from the settings.
    name: String,
    quota: Option<config::Quota>,
    /// Logged once per period.
    warned: bool,
}

impl Tracked {
    fn new(used: u64, since: i64) -> Self {
        let usage = Arc::new(Usage::default());
        usage.set_used(used);
        Self {
            usage,
            since,
            name: String::new(),
            quota: None,
            warned: false,
        }
    }

    /// Unix ms of the latest automatic reset at or before `now`; `None` for blocks.
    /// Providers without a quota still show usage per calendar month.
    fn period_start(&self, now: i64) -> Option<i64> {
        match self.quota {
            Some(config::Quota {
                period: QuotaPeriod::Block,
                ..
            }) => None,
            quota => Some(last_reset(now, quota.map_or(1, |q| q.reset_day))),
        }
    }
}

pub struct Quotas {
    providers: Mutex<HashMap<Uuid, Tracked>>,
    /// Last written, to skip unchanged writes.
    stored: tokio::sync::Mutex<String>,
    db: Db,
}

#[derive(Serialize, ToSchema)]
pub struct UsageStatus {
    /// By provider id.
    providers: HashMap<Uuid, ProviderUsage>,
}

#[derive(Serialize, ToSchema)]
pub struct ProviderUsage {
    /// Bytes read from the provider this period.
    used: u64,
    /// The quota in bytes; absent without one.
    cap: Option<u64>,
    /// Unix ms the period started.
    since: i64,
    /// Unix ms of the next automatic reset; absent for block quotas.
    resets_at: Option<i64>,
    /// Requests skip the provider until the quota resets.
    exhausted: bool,
}

#[derive(Deserialize, ToSchema)]
pub struct ResetUsage {
    /// Usage to start from, for data already used elsewhere this period.
    #[serde(default)]
    used_bytes: u64,
    /// Monthly quotas: make today (UTC) the reset day.
    #[serde(default)]
    reset_day_today: bool,
}

impl Quotas {
    pub async fn load(db: Db) -> anyhow::Result<Self> {
        let stored: HashMap<Uuid, Stored> = db
            .config()
            .await?
            .into_iter()
            .find(|(key, _)| key == KEY)
            .and_then(|(_, value)| serde_json::from_str(&value).ok())
            .unwrap_or_default();
        let now = now();
        let providers = stored
            .into_iter()
            .map(|(id, s)| (id, Tracked::new(s.used, s.since.min(now))))
            .collect();
        Ok(Self {
            providers: Mutex::new(providers),
            stored: tokio::sync::Mutex::default(),
            db,
        })
    }

    fn lock(&self) -> std::sync::MutexGuard<'_, HashMap<Uuid, Tracked>> {
        self.providers
            .lock()
            .unwrap_or_else(PoisonError::into_inner)
    }

    /// The counter connections to provider `id` add to; it outlives pool rebuilds.
    pub fn usage(&self, id: Uuid) -> Arc<Usage> {
        Arc::clone(
            &self
                .lock()
                .entry(id)
                .or_insert_with(|| Tracked::new(0, now()))
                .usage,
        )
    }

    /// Applies caps and reset days from the settings; forgets removed providers.
    pub fn configure(&self, providers: &[config::Provider]) {
        let mut tracked = self.lock();
        tracked.retain(|id, _| providers.iter().any(|p| p.id == *id));
        for p in providers {
            let t = tracked
                .entry(p.id)
                .or_insert_with(|| Tracked::new(0, now()));
            t.usage.set_cap(p.quota.map(|q| u64::from(q.gb) * GB));
            t.name.clone_from(&p.name);
            t.quota = p.quota;
        }
        drop(tracked);
        self.roll(now());
    }

    /// Starts each listed provider's period now at the bytes already used, and stores it.
    pub async fn seed(&self, used: &[(Uuid, u64)]) {
        let now = now();
        for (id, bytes) in used {
            if let Some(t) = self.lock().get_mut(id) {
                t.usage.set_used(*bytes);
                t.since = now;
            }
        }
        self.flush().await;
    }

    /// Starts a new period for providers whose reset passed, and logs newly used-up ones.
    fn roll(&self, now: i64) {
        for t in self.lock().values_mut() {
            if let Some(start) = t.period_start(now)
                && t.since < start
            {
                t.usage.set_used(0);
                t.since = start;
                t.warned = false;
            }
            if t.usage.exhausted() && !t.warned {
                t.warned = true;
                tracing::warn!(
                    provider = %t.name,
                    reason = "download quota used up",
                    "skipping provider until its quota resets"
                );
            }
        }
    }

    fn view(&self) -> HashMap<Uuid, ProviderUsage> {
        let now = now();
        self.lock()
            .iter()
            .map(|(&id, t)| {
                let usage = ProviderUsage {
                    used: t.usage.used(),
                    cap: t.usage.cap(),
                    since: t.since,
                    resets_at: t
                        .period_start(now)
                        .map(|_| next_reset(now, t.quota.map_or(1, |q| q.reset_day))),
                    exhausted: t.usage.exhausted(),
                };
                (id, usage)
            })
            .collect()
    }

    #[allow(clippy::significant_drop_tightening)] // Held across the write so stores land in order.
    async fn flush(&self) {
        let json = {
            let stored: HashMap<Uuid, Stored> = self
                .lock()
                .iter()
                .map(|(&id, t)| {
                    let stored = Stored {
                        used: t.usage.used(),
                        since: t.since,
                    };
                    (id, stored)
                })
                .collect();
            serde_json::to_string(&stored)
        };
        let json = match json {
            Ok(json) => json,
            Err(error) => {
                tracing::warn!(reason = %error, "could not store provider usage");
                return;
            }
        };
        let mut last = self.stored.lock().await;
        if *last == json {
            return;
        }
        let _writing = self.db.write_permit().await;
        match self.db.write_config(&[(KEY, &json)], &[]).await {
            Ok(()) => *last = json,
            Err(error) => tracing::warn!(reason = %error, "could not store provider usage"),
        }
    }

    /// Rolls periods and stores usage every minute until `cancel`, then stores it once more.
    pub async fn run(&self, cancel: CancellationToken) {
        let mut tick = tokio::time::interval(FLUSH_EVERY);
        loop {
            tokio::select! {
                () = cancel.cancelled() => break,
                _ = tick.tick() => {}
            }
            self.roll(now());
            self.flush().await;
        }
        self.flush().await;
    }
}

fn now() -> i64 {
    unix_ms(SystemTime::now())
}

/// Download usage and quota of each provider.
#[utoipa::path(get, path = "/api/v1/provider-usage", tag = "settings",
    responses((status = 200, body = UsageStatus)))]
pub async fn get_provider_usage(State(admin): State<Arc<Admin>>) -> Json<UsageStatus> {
    Json(UsageStatus {
        providers: admin.live.quotas().view(),
    })
}

/// Starts a new quota period now; a used-up provider is used again at once.
#[utoipa::path(post, path = "/api/v1/providers/{id}/usage/reset", tag = "settings",
    params(("id" = Uuid, Path)), request_body = ResetUsage,
    responses((status = 200, body = ProviderUsage), (status = 404, body = ApiError)))]
#[allow(clippy::significant_drop_tightening)] // The settings lock spans save and apply.
pub async fn post_reset_usage(
    State(admin): State<Arc<Admin>>,
    Path(id): Path<Uuid>,
    Json(reset): Json<ResetUsage>,
) -> Result<Json<ProviderUsage>, Fail> {
    let now = now();
    {
        let mut store = admin.config.write().await;
        let mut next = store.settings().clone();
        let provider = next
            .providers
            .iter_mut()
            .find(|p| p.id == id)
            .ok_or(Fail::NotFound)?;
        if reset.reset_day_today
            && let Some(quota) = &mut provider.quota
            && quota.period == QuotaPeriod::Monthly
        {
            let today = u8::try_from(civil(now.div_euclid(DAY_MS)).2).unwrap_or(1);
            if quota.reset_day != today {
                quota.reset_day = today;
                if let Err(error) = store.save(next).await {
                    tracing::warn!(reason = %error, "could not save the quota reset day");
                    return Err(Fail::Internal);
                }
                admin
                    .live
                    .apply(store.settings())
                    .await
                    .map_err(|e| Fail::Bad(format!("saved, but could not apply: {e:#}")))?;
            }
        }
    }
    let quotas = admin.live.quotas();
    if let Some(t) = quotas.lock().get_mut(&id) {
        t.usage.set_used(reset.used_bytes);
        t.since = now;
        t.warned = false;
    }
    quotas.flush().await;
    quotas.view().remove(&id).map(Json).ok_or(Fail::NotFound)
}

/// Days since 1970-01-01 of a proleptic Gregorian date.
fn days(year: i64, month: i64, day: i64) -> i64 {
    let y = if month <= 2 { year - 1 } else { year };
    let era = y.div_euclid(400);
    let yoe = y - era * 400;
    let doy = (153 * ((month + 9) % 12) + 2) / 5 + day - 1;
    era * 146_097 + yoe * 365 + yoe / 4 - yoe / 100 + doy - 719_468
}

/// `(year, month, day)` of days since 1970-01-01.
pub(crate) fn civil(days: i64) -> (i64, i64, i64) {
    let z = days + 719_468;
    let era = z.div_euclid(146_097);
    let doe = z - era * 146_097;
    let yoe = (doe - doe / 1460 + doe / 36_524 - doe / 146_096) / 365;
    let doy = doe - (365 * yoe + yoe / 4 - yoe / 100);
    let mp = (5 * doy + 2) / 153;
    let month = if mp < 10 { mp + 3 } else { mp - 9 };
    let year = yoe + era * 400 + i64::from(month <= 2);
    (year, month, doy - (153 * mp + 2) / 5 + 1)
}

/// The reset day of a month, clamped to its length, as days since 1970-01-01.
fn reset_in(year: i64, month: i64, day: u8) -> i64 {
    let first = days(year, month, 1);
    let next = if month == 12 {
        days(year + 1, 1, 1)
    } else {
        days(year, month + 1, 1)
    };
    first + i64::from(day).min(next - first) - 1
}

const fn shift(year: i64, month: i64, by: i64) -> (i64, i64) {
    let index = year * 12 + month - 1 + by;
    (index.div_euclid(12), index.rem_euclid(12) + 1)
}

/// Unix ms of the latest reset (00:00 UTC on `day`) at or before `now`.
fn last_reset(now: i64, day: u8) -> i64 {
    let today = now.div_euclid(DAY_MS);
    let (year, month, _) = civil(today);
    let this = reset_in(year, month, day);
    let start = if this <= today {
        this
    } else {
        let (y, m) = shift(year, month, -1);
        reset_in(y, m, day)
    };
    start * DAY_MS
}

/// Unix ms of the first reset after `now`.
fn next_reset(now: i64, day: u8) -> i64 {
    let today = now.div_euclid(DAY_MS);
    let (year, month, _) = civil(today);
    let this = reset_in(year, month, day);
    let next = if this > today {
        this
    } else {
        let (y, m) = shift(year, month, 1);
        reset_in(y, m, day)
    };
    next * DAY_MS
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn resets_fall_on_the_day_or_the_last_day_of_short_months() {
        let at = |y, m, d| days(y, m, d) * DAY_MS;
        assert_eq!(civil(days(2024, 2, 29)), (2024, 2, 29));
        // Mid-month, day 15: last reset this month, next one the following month.
        let now = at(2026, 10, 20) + 5;
        assert_eq!(last_reset(now, 15), at(2026, 10, 15));
        assert_eq!(next_reset(now, 15), at(2026, 11, 15));
        // On the day itself, the reset has just happened.
        assert_eq!(last_reset(at(2026, 10, 15), 15), at(2026, 10, 15));
        assert_eq!(next_reset(at(2026, 10, 15), 15), at(2026, 11, 15));
        // Day 31 resets on February's last day, and across the year end.
        assert_eq!(next_reset(at(2026, 2, 10), 31), at(2026, 2, 28));
        assert_eq!(next_reset(at(2028, 2, 10), 31), at(2028, 2, 29));
        assert_eq!(last_reset(at(2027, 1, 5), 31), at(2026, 12, 31));
        assert_eq!(next_reset(at(2026, 12, 31), 31), at(2027, 1, 31));
    }
}
