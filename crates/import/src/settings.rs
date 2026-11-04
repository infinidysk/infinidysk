//! C# settings: `ConfigItems` overlaid by `NZBDAV_CONFIG__*` environment variables, plus
//! the older environment variables C# fell back to.

use std::collections::BTreeMap;
use std::path::Path;

use serde::Deserialize;
use sqlx::ConnectOptions as _;
use sqlx::sqlite::SqliteConnectOptions;
use uuid::Uuid;

use crate::{Error, legacy_hash, open};

const ENV_PREFIX: &str = "NZBDAV_CONFIG__";
/// Variables C# read directly when the matching setting was empty, and the 1.x frontend's
/// OIDC variables.
const FALLBACK_VARS: [&str; 14] = [
    "FRONTEND_BACKEND_API_KEY",
    "CATEGORIES",
    "MOUNT_DIR",
    "WEBDAV_USER",
    "WEBDAV_PASSWORD",
    "TRUSTED_PROXY_CIDRS",
    "OIDC_ISSUER",
    "OIDC_CLIENT_ID",
    "OIDC_CLIENT_SECRET",
    "OIDC_REDIRECT_URI",
    "OIDC_SCOPES",
    "OIDC_USERNAME_CLAIM",
    "OIDC_ADMIN_CLAIM",
    "OIDC_ADMIN_CLAIM_VALUE",
];

/// Effective C# settings by key (`api.categories`), and the fallback variables.
pub struct Config {
    values: BTreeMap<String, String>,
    vars: BTreeMap<&'static str, String>,
    /// The admin login as `(username, password hash in our stored form)`.
    pub admin: Option<(String, String)>,
    /// Bytes C# counted per provider since its quota reset: `(bytes, reset at)` by
    /// provider id in `N` form.
    quota_used: BTreeMap<String, (i64, i64)>,
}

impl Config {
    /// Reads the settings stored in the C# `/config` at `config`, overlaid by `vars`
    /// (normally `std::env::vars()`) as C# would.
    pub async fn read(
        config: &Path,
        vars: impl IntoIterator<Item = (String, String)>,
    ) -> Result<Self, Error> {
        let mut db = open(config).await?;
        let mut values: BTreeMap<String, String> =
            sqlx::query_as("SELECT ConfigName, ConfigValue FROM ConfigItems")
                .fetch_all(&mut db)
                .await?
                .into_iter()
                .collect();
        let admin: Option<(String, String, String)> = sqlx::query_as(
            "SELECT Username, PasswordHash, RandomSalt FROM Accounts WHERE Type = 1",
        )
        .fetch_optional(&mut db)
        .await?;
        let mut fallback = BTreeMap::new();
        for (name, value) in vars {
            if let Some(var) = FALLBACK_VARS.iter().find(|v| **v == name) {
                fallback.insert(*var, value);
            } else if let Some(key) = overlay_key(&name) {
                values.insert(key, value);
            }
        }
        Ok(Self {
            values,
            vars: fallback,
            admin: admin.map(|(user, hash, salt)| (user, legacy_hash(&hash, &salt))),
            quota_used: quota_used(config).await,
        })
    }

    /// What C# counted against `p`'s download limit: its offset plus the bytes tracked
    /// since its last reset.
    pub fn quota_used(&self, p: &Provider) -> u64 {
        let tracked = self
            .quota_used
            .get(&p.provider_id.simple().to_string())
            .filter(|(_, reset)| *reset == p.bytes_used_reset_at)
            .map_or(0, |(bytes, _)| *bytes);
        u64::try_from(tracked.max(0).saturating_add(p.bytes_used_offset.max(0))).unwrap_or(0)
    }

    /// A non-empty setting.
    pub fn get(&self, key: &str) -> Option<&str> {
        self.values
            .get(key)
            .map(|v| v.trim())
            .filter(|v| !v.is_empty())
    }

    /// A non-empty fallback variable.
    pub fn var(&self, name: &str) -> Option<&str> {
        self.vars
            .get(name)
            .map(|v| v.trim())
            .filter(|v| !v.is_empty())
    }

    pub fn keys(&self) -> impl Iterator<Item = &str> {
        self.values.keys().map(String::as_str)
    }

    pub fn providers(&self) -> Result<Vec<Provider>, serde_json::Error> {
        #[derive(Deserialize)]
        #[serde(rename_all = "PascalCase")]
        struct Providers {
            #[serde(default)]
            providers: Vec<Provider>,
        }
        self.get("usenet.providers").map_or(Ok(Vec::new()), |v| {
            Ok(serde_json::from_str::<Providers>(v)?.providers)
        })
    }

    pub fn arr_instances(&self) -> Result<Vec<(ArrKind, ArrInstance)>, serde_json::Error> {
        // Lists past Radarr and Sonarr are written by external config tools; C# ignores
        // them, but they name real instances that download through it.
        #[derive(Deserialize, Default)]
        #[serde(default)]
        struct Lists {
            #[serde(rename = "RadarrInstances")]
            radarr: Vec<ArrInstance>,
            #[serde(rename = "SonarrInstances")]
            sonarr: Vec<ArrInstance>,
            #[serde(rename = "LidarrInstances")]
            lidarr: Vec<ArrInstance>,
            #[serde(rename = "ReadarrInstances")]
            readarr: Vec<ArrInstance>,
            #[serde(rename = "WhisparrInstances")]
            whisparr: Vec<ArrInstance>,
        }
        let Some(raw) = self.get("arr.instances") else {
            return Ok(Vec::new());
        };
        let i: Lists = serde_json::from_str(raw)?;
        Ok([
            (ArrKind::Radarr, i.radarr),
            (ArrKind::Sonarr, i.sonarr),
            (ArrKind::Lidarr, i.lidarr),
            (ArrKind::Readarr, i.readarr),
            (ArrKind::Whisparr, i.whisparr),
        ]
        .into_iter()
        .flat_map(|(kind, list)| list.into_iter().map(move |a| (kind, a)))
        .collect())
    }
}

/// C#'s quota counters from `metrics.sqlite`; empty when it is missing or predates them.
async fn quota_used(config: &Path) -> BTreeMap<String, (i64, i64)> {
    let path = config.join("metrics.sqlite");
    if !path.is_file() {
        return BTreeMap::new();
    }
    let rows = async {
        let mut db = SqliteConnectOptions::new()
            .filename(&path)
            .read_only(true)
            .connect()
            .await?;
        sqlx::query_as::<_, (String, i64, i64)>(
            "SELECT Provider, BytesUsed, ResetAt FROM ProviderQuotaUsage",
        )
        .fetch_all(&mut db)
        .await
    };
    rows.await.map_or_else(
        |_| BTreeMap::new(),
        |rows| {
            rows.into_iter()
                .map(|(key, bytes, reset)| (key.to_ascii_lowercase(), (bytes, reset)))
                .collect()
        },
    )
}

/// `NZBDAV_CONFIG__USENET__MAX_DOWNLOAD_CONNECTIONS` -> `usenet.max-download-connections`.
fn overlay_key(var: &str) -> Option<String> {
    let body = var
        .get(..ENV_PREFIX.len())
        .filter(|p| p.eq_ignore_ascii_case(ENV_PREFIX))
        .map(|_| &var[ENV_PREFIX.len()..])?;
    Some(
        body.to_ascii_lowercase()
            .split("__")
            .map(|part| part.replace('_', "-"))
            .collect::<Vec<_>>()
            .join("."),
    )
}

/// A C# Usenet provider. No `Debug`: it holds the password.
#[derive(Deserialize)]
#[serde(rename_all = "PascalCase")]
pub struct Provider {
    #[serde(default)]
    pub provider_id: Uuid,
    #[serde(rename = "Type")]
    pub kind: ProviderKind,
    pub host: String,
    pub port: i64,
    pub use_ssl: bool,
    #[serde(default)]
    pub skip_tls_verification: bool,
    #[serde(default)]
    pub user: String,
    #[serde(default)]
    pub pass: String,
    pub max_connections: i64,
    #[serde(default)]
    pub priority: i64,
    pub pipelining_depth: Option<i64>,
    pub nickname: Option<String>,
    #[serde(default)]
    pub storage_group: String,
    pub byte_limit: Option<i64>,
    /// Bytes the user said were already used when the limit was set.
    #[serde(default)]
    pub bytes_used_offset: i64,
    #[serde(default)]
    pub bytes_used_reset_at: i64,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq, Deserialize)]
#[serde(from = "u8")]
pub enum ProviderKind {
    Disabled,
    /// Shares the load with the other pooled providers.
    Pooled,
    /// Asked only after the pooled providers miss.
    Backup,
}

impl From<u8> for ProviderKind {
    fn from(n: u8) -> Self {
        match n {
            1 => Self::Pooled,
            2 | 3 => Self::Backup,
            _ => Self::Disabled,
        }
    }
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum ArrKind {
    Radarr,
    Sonarr,
    Lidarr,
    Readarr,
    Whisparr,
}

/// A C# Arr instance. No `Debug`: it holds the API key.
#[derive(Deserialize)]
#[serde(rename_all = "PascalCase")]
pub struct ArrInstance {
    pub name: Option<String>,
    pub host: String,
    pub api_key: String,
    #[serde(default = "enabled")]
    pub enabled: bool,
}

const fn enabled() -> bool {
    true
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn maps_overlay_variables_to_setting_keys() {
        assert_eq!(
            overlay_key("NZBDAV_CONFIG__USENET__MAX_DOWNLOAD_CONNECTIONS").as_deref(),
            Some("usenet.max-download-connections")
        );
        assert_eq!(
            overlay_key("nzbdav_config__api__categories").as_deref(),
            Some("api.categories")
        );
        assert_eq!(overlay_key("MOUNT_DIR"), None);
    }
}
