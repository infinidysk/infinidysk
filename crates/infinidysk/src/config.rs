//! Settings: defaults, overlaid by values stored in the database, overlaid by
//! `INFINIDYSK__<SECTION>__<KEY>` environment variables. The environment layer is
//! authoritative and never written back; only differences from the defaults are stored.

use std::collections::HashSet;
use std::fmt;
use std::ops::RangeInclusive;

use infinidysk_db::Db;
use infinidysk_nntp::catalog;
use serde::{Deserialize, Serialize};
use serde_json::{Map, Value};
use utoipa::ToSchema;
use uuid::Uuid;

/// What every non-empty secret looks like outside the process. Saving it back keeps the
/// stored secret.
pub const MASK: &str = "********";
const ENV_PREFIX: &str = "INFINIDYSK__";
/// The disk cache folder when none is saved, for callers that know no config folder.
pub const DEFAULT_CACHE_DIR: &str = "/config/cache";

#[derive(Debug, thiserror::Error)]
pub enum Error {
    #[error("config storage failed")]
    Db(#[from] infinidysk_db::Error),
    #[error("config is malformed")]
    Json(#[from] serde_json::Error),
    #[error("{message}")]
    Invalid {
        /// The dotted setting path, like `mount.dir`.
        path: &'static str,
        message: String,
    },
    #[error("{0} is set by an environment variable and cannot be changed here")]
    Locked(String),
}

impl Error {
    /// The dotted setting path the error is about.
    #[must_use]
    pub fn path(&self) -> Option<&str> {
        match self {
            Self::Invalid { path, .. } => Some(path),
            Self::Locked(path) => Some(path),
            Self::Db(_) | Self::Json(_) => None,
        }
    }
}

fn invalid(path: &'static str, message: String) -> Error {
    Error::Invalid { path, message }
}

const MAX_INDEXERS: usize = 32;
const MAX_INDEXER_NAME_BYTES: usize = 128;
const MAX_INDEXER_URL_BYTES: usize = 2048;
const MAX_INDEXER_KEY_BYTES: usize = 512;

const CHECK_PERCENT: RangeInclusive<u8> = 5..=100;
const QUEUE_CONCURRENCY: RangeInclusive<u8> = 1..=10;
const NZB_RETENTION_DAYS: RangeInclusive<u32> = 0..=3650;
const READ_AHEAD_MB: RangeInclusive<u32> = 1..=1250;
const MEMORY_CACHE_MB: RangeInclusive<u32> = 16..=65_536;
const DISK_CACHE_GB: RangeInclusive<u32> = 1..=100_000;
const DISK_CACHE_MAX_AGE_HOURS: RangeInclusive<u32> = 1..=8760;
const READ_AHEAD_MEMORY_MB: RangeInclusive<u32> = 64..=65_536;
const FUSE_MAX_READAHEAD_KB: RangeInclusive<u32> = 128..=65_536;
const FUSE_MAX_BACKGROUND: RangeInclusive<u16> = 1..=1024;
const MAX_CONNECTIONS: RangeInclusive<u16> = 1..=u16::MAX;
const PIPELINE_DEPTH: RangeInclusive<u8> = 1..=16;
const PORT: RangeInclusive<u16> = 1..=u16::MAX;
const QUOTA_GB: RangeInclusive<u32> = 1..=u32::MAX;
const RESET_DAY: RangeInclusive<u8> = 1..=31;
/// Up to 100 Gbit/s.
const DOWNLOAD_LIMIT_MB_PER_S: RangeInclusive<u32> = 1..=12_500;

fn check<T: PartialOrd + fmt::Display + Copy>(
    path: &'static str,
    what: &str,
    value: T,
    range: &RangeInclusive<T>,
) -> Result<(), Error> {
    if range.contains(&value) {
        Ok(())
    } else {
        Err(invalid(
            path,
            format!("{what} must be {}-{}", range.start(), range.end()),
        ))
    }
}

fn reset<T: PartialOrd + fmt::Display>(
    field: &str,
    value: &mut T,
    range: &RangeInclusive<T>,
    default: T,
) {
    if !range.contains(value) {
        reset_warning(field, &*value, &default);
        *value = default;
    }
}

/// An override outside `range` falls back to the preset's value.
fn reset_override<T: PartialOrd + fmt::Display + Copy>(
    field: &str,
    value: &mut Option<T>,
    range: &RangeInclusive<T>,
    preset: T,
) {
    if let Some(v) = *value
        && !range.contains(&v)
    {
        reset_warning(field, &v, &preset);
        *value = None;
    }
}

fn reset_warning(field: &str, value: &dyn fmt::Display, default: &dyn fmt::Display) {
    tracing::warn!(
        field,
        %value,
        %default,
        "stored setting is out of range; using the default"
    );
}

/// A password or key. `Debug` never prints it.
#[derive(Clone, Default, PartialEq, Eq, Serialize, Deserialize, ToSchema)]
#[serde(transparent)]
pub struct Secret(pub String);

impl Secret {
    fn masked(&self) -> Self {
        if self.0.is_empty() {
            Self::default()
        } else {
            Self(MASK.to_owned())
        }
    }

    pub(crate) fn restore(&mut self, current: &Self) {
        if self.0 == MASK {
            self.0.clone_from(&current.0);
        }
    }
}

impl fmt::Debug for Secret {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        fmt::Debug::fmt(&self.masked().0, f)
    }
}

#[derive(Clone, Debug, Default, PartialEq, Eq, Serialize, Deserialize, ToSchema)]
#[serde(default)]
pub struct Settings {
    pub sab: Sab,
    pub auth: Auth,
    pub webdav: WebDav,
    pub mount: Mount,
    pub performance: Performance,
    pub providers: Vec<Provider>,
    pub arr_instances: Vec<ArrInstance>,
    pub arr: ArrOptions,
    pub indexers: Vec<Indexer>,
    pub health: Health,
    pub queue: Queue,
    pub imports: Imports,
    pub metrics: Metrics,
    pub network: Network,
    pub maintenance: Maintenance,
    /// Experiments switched on, by name; they apply after a restart.
    #[serde(deserialize_with = "known_experiments")]
    pub experiments: Vec<String>,
}

#[derive(Clone, PartialEq, Eq, Serialize, Deserialize, ToSchema)]
#[serde(default)]
pub struct Indexer {
    pub id: Uuid,
    pub name: String,
    pub base_url: String,
    pub api_key: Secret,
    #[serde(default = "enabled")]
    #[schema(default = true)]
    pub enabled: bool,
}

impl Default for Indexer {
    fn default() -> Self {
        Self {
            id: Uuid::nil(),
            name: String::new(),
            base_url: String::new(),
            api_key: Secret::default(),
            enabled: enabled(),
        }
    }
}

impl fmt::Debug for Indexer {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        f.debug_struct("Indexer")
            .field("id", &self.id)
            .field("name", &self.name)
            .field("base_url", &self.public_base_url())
            .field("api_key", &self.api_key)
            .field("enabled", &self.enabled)
            .finish()
    }
}

impl Indexer {
    pub(crate) fn public_base_url(&self) -> &str {
        if crate::newznab::base_url(self).is_ok() {
            &self.base_url
        } else {
            MASK
        }
    }

    pub(crate) fn validate(&self) -> Result<(), Error> {
        if self.id.is_nil() || self.name.trim().is_empty() {
            return Err(invalid(
                "indexers",
                "An indexer needs an ID and name".to_owned(),
            ));
        }
        if self.name.len() > MAX_INDEXER_NAME_BYTES {
            return Err(invalid(
                "indexers",
                "Indexer name exceeds 128 bytes".to_owned(),
            ));
        }
        if self.base_url.len() > MAX_INDEXER_URL_BYTES {
            return Err(invalid(
                "indexers",
                "Indexer URL exceeds 2048 bytes".to_owned(),
            ));
        }
        if self.api_key.0.len() > MAX_INDEXER_KEY_BYTES {
            return Err(invalid(
                "indexers",
                "Indexer API key exceeds 512 bytes".to_owned(),
            ));
        }
        crate::newznab::endpoint(self)
            .map(|_| ())
            .map_err(|reason| invalid("indexers", reason.to_owned()))
    }
}

/// An unfinished change users may switch on while it is measured.
#[derive(Debug, Serialize, ToSchema)]
pub struct Experiment {
    pub name: &'static str,
    pub title: &'static str,
    pub description: &'static str,
}

pub const EXPERIMENTS: [Experiment; 2] = [
    Experiment {
        name: "win-floor",
        title: "Deeper read-ahead for large articles",
        description: "Files posted in articles of 2.1 MB or more may keep two articles per connection in flight, so one stream can use every connection. Faster sequential reads in testing; jumping backwards may start slower.",
    },
    Experiment {
        name: "weight-large",
        title: "Large articles use a whole connection",
        description: "Articles of 2.1 MB or more count as a full connection's pipeline, so a deep pipeline cannot stack them on one connection. Matters most with a high pipeline depth.",
    },
];

/// Keeps names from [`EXPERIMENTS`] once each; others (retired, or a typo) are dropped.
fn known_experiments<'de, D: serde::Deserializer<'de>>(d: D) -> Result<Vec<String>, D::Error> {
    let names = Vec::<String>::deserialize(d)?;
    Ok(EXPERIMENTS
        .iter()
        .filter(|e| names.iter().any(|n| n == e.name))
        .map(|e| e.name.to_owned())
        .collect())
}

/// Maintenance jobs and the daily window they run in (`infinidysk-lab:docs/maintenance-policy.md`).
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize, ToSchema)]
#[serde(default)]
pub struct Maintenance {
    pub window: MaintenanceWindow,
    /// Jobs that run in the window, once each per window.
    #[serde(deserialize_with = "known_jobs")]
    pub scheduled: Vec<MaintenanceJob>,
    pub backup: BackupSettings,
}

impl Default for Maintenance {
    fn default() -> Self {
        Self {
            window: MaintenanceWindow::default(),
            scheduled: vec![MaintenanceJob::RemoveDeadLinks],
            backup: BackupSettings::default(),
        }
    }
}

/// Where backups go and how many the backup job keeps.
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize, ToSchema)]
#[serde(default)]
pub struct BackupSettings {
    /// Absolute folder; empty means `backups` beside the database.
    pub dir: String,
    /// Scheduled and manual backups kept; pinned, uploaded and added ones are extra.
    pub keep: u16,
}

impl Default for BackupSettings {
    fn default() -> Self {
        Self {
            dir: String::new(),
            keep: 3,
        }
    }
}

pub const BACKUP_KEEP: RangeInclusive<u16> = 1..=100;

/// Daily, in server time, as `HH:MM`. An end at or before the start ends the next day.
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize, ToSchema)]
#[serde(default)]
pub struct MaintenanceWindow {
    pub start: String,
    pub end: String,
}

impl Default for MaintenanceWindow {
    fn default() -> Self {
        Self {
            start: "03:00".to_owned(),
            end: "06:00".to_owned(),
        }
    }
}

impl MaintenanceWindow {
    /// `(start, end)` in minutes after midnight; `None` when either is not `HH:MM` or
    /// they are equal.
    #[must_use]
    pub fn minutes(&self) -> Option<(u16, u16)> {
        let parse = |s: &str| {
            let (h, m) = s.split_once(':')?;
            if h.len() != 2 || m.len() != 2 {
                return None;
            }
            let (h, m): (u16, u16) = (h.parse().ok()?, m.parse().ok()?);
            (h < 24 && m < 60).then_some(h * 60 + m)
        };
        let (start, end) = (parse(&self.start)?, parse(&self.end)?);
        (start != end).then_some((start, end))
    }
}

/// A maintenance job, in the order the window runs them.
#[derive(
    Clone, Copy, Debug, PartialEq, Eq, PartialOrd, Ord, Hash, Serialize, Deserialize, ToSchema,
)]
#[serde(rename_all = "kebab-case")]
pub enum MaintenanceJob {
    /// Writes a checked copy of the SQLite library database and prunes old copies.
    BackupDatabase,
    /// Deletes downloads nothing in the library folders points to.
    RemoveUnlinked,
    /// Removes library links and STRM files whose download is gone, and has the Arr
    /// search for it again.
    RemoveDeadLinks,
    /// Rewrites the SQLite databases without their unused space; always scheduled.
    CompactDatabase,
}

impl MaintenanceJob {
    pub const ALL: [Self; 4] = [
        Self::BackupDatabase,
        Self::RemoveUnlinked,
        Self::RemoveDeadLinks,
        Self::CompactDatabase,
    ];

    #[must_use]
    pub const fn id(self) -> &'static str {
        match self {
            Self::BackupDatabase => "backup-database",
            Self::RemoveUnlinked => "remove-unlinked",
            Self::RemoveDeadLinks => "remove-dead-links",
            Self::CompactDatabase => "compact-database",
        }
    }

    /// Runs in every window; not part of the `scheduled` setting.
    #[must_use]
    pub const fn always_scheduled(self) -> bool {
        matches!(self, Self::CompactDatabase)
    }

    #[must_use]
    pub const fn has_dry_run(self) -> bool {
        matches!(self, Self::RemoveUnlinked | Self::RemoveDeadLinks)
    }
}

/// Drops job ids this build does not know (stored by a newer one) and jobs that are
/// always scheduled.
fn known_jobs<'de, D: serde::Deserializer<'de>>(d: D) -> Result<Vec<MaintenanceJob>, D::Error> {
    let ids = Vec::<Value>::deserialize(d)?;
    Ok(ids
        .into_iter()
        .filter_map(|id| serde_json::from_value::<MaintenanceJob>(id).ok())
        .filter(|job| !job.always_scheduled())
        .collect())
}

/// How finished jobs reach the Arrs (`infinidysk-lab:docs/strm-support-proposal.md` §4).
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize, ToSchema)]
#[serde(default)]
pub struct Imports {
    pub strategy: ImportStrategy,
    /// STRM mode: where each job's STRM files are written for the Arrs to import.
    pub strm_dir: String,
    /// Signs STRM playback URLs, one key per item.
    pub strm_key: Secret,
    /// Folders holding the Arrs' libraries, walked when pointers change form.
    pub library_dirs: Vec<LibraryDir>,
}

/// A folder of an Arr's library.
#[derive(Clone, Debug, Default, PartialEq, Eq, Serialize, Deserialize, ToSchema)]
#[serde(from = "StoredLibraryDir")]
pub struct LibraryDir {
    /// As seen from here.
    pub path: String,
    /// As the Arrs see it; empty when that is `path`.
    pub arr_path: String,
}

/// Builds before `arr_path` stored a bare path string.
#[derive(Deserialize)]
#[serde(untagged)]
enum StoredLibraryDir {
    Path(String),
    Dir {
        #[serde(default)]
        path: String,
        #[serde(default)]
        arr_path: String,
    },
}

impl From<StoredLibraryDir> for LibraryDir {
    fn from(stored: StoredLibraryDir) -> Self {
        match stored {
            StoredLibraryDir::Path(path) => Self {
                path,
                arr_path: String::new(),
            },
            StoredLibraryDir::Dir { path, arr_path } => Self { path, arr_path },
        }
    }
}

impl LibraryDir {
    #[must_use]
    pub fn arr_path(&self) -> &str {
        if self.arr_path.is_empty() {
            &self.path
        } else {
            &self.arr_path
        }
    }
}

impl Default for Imports {
    fn default() -> Self {
        Self {
            strategy: ImportStrategy::default(),
            strm_dir: "/data/completed-downloads".to_owned(),
            strm_key: Secret::default(),
            library_dirs: Vec::new(),
        }
    }
}

#[derive(Clone, Copy, Debug, Default, PartialEq, Eq, Serialize, Deserialize, ToSchema)]
#[serde(rename_all = "lowercase")]
pub enum ImportStrategy {
    /// Symlinks into the mount, for Plex and anything else on the mount host.
    #[default]
    Symlinks,
    /// STRM files with playback URLs, for Emby, Jellyfin and Kodi without the mount.
    Strm,
}

/// Import queue choices; everything else is fixed by `infinidysk-lab:docs/import-policy.md`.
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize, ToSchema)]
#[serde(default)]
pub struct Queue {
    /// Jobs importing at once, 1 to 10.
    pub concurrency: u8,
    /// Filename globs (`*`, `?`) dropped from imports on top of the built-in list.
    pub extra_blocklist: Vec<String>,
    /// Days an NZB is kept after nothing uses it anymore; 0 deletes it at once.
    pub nzb_retention_days: u32,
}

impl Default for Queue {
    fn default() -> Self {
        Self {
            concurrency: 2,
            extra_blocklist: Vec::new(),
            nzb_retention_days: 30,
        }
    }
}

#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize, ToSchema)]
#[serde(default)]
pub struct Network {
    /// Reverse proxies (addresses or CIDR ranges) whose `X-Forwarded-For` names the
    /// client; every local range by default.
    pub trusted_proxies: Vec<String>,
    /// URL Arrs and browsers use to reach this app, such as the reverse-proxy address;
    /// empty when not set.
    pub public_url: String,
    /// Cap on the total download speed from all providers together; `None` for no
    /// limit. Serving files to players is not limited.
    pub download_limit_mb_per_s: Option<u32>,
}

impl Default for Network {
    fn default() -> Self {
        Self {
            trusted_proxies: [
                "127.0.0.0/8",
                "10.0.0.0/8",
                "172.16.0.0/12",
                "192.168.0.0/16",
                "169.254.0.0/16",
                "::1/128",
                "fc00::/7",
                "fe80::/10",
            ]
            .map(str::to_owned)
            .into(),
            public_url: String::new(),
            download_limit_mb_per_s: None,
        }
    }
}

/// Health monitoring choices; everything else is fixed by `infinidysk-lab:docs/health-policy.md`.
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize, ToSchema)]
#[serde(default)]
#[allow(clippy::struct_excessive_bools, reason = "independent user toggles")]
pub struct Health {
    /// Run routine checks. Rechecks, playback reports and repairs run regardless.
    pub enabled: bool,
    /// Share of segments each health check probes, 5 to 100; at least 5 segments.
    pub check_percent: u8,
    /// Share of segments an import probes, 5 to 100; at least 5 segments.
    pub import_check_percent: u8,
    /// Ask the Arr that imported a broken file for a replacement.
    pub auto_repair: bool,
    /// Keep serving files with a few small gaps instead of replacing them.
    pub degraded_tolerance: bool,
    /// What happens to a broken file not imported by an Arr.
    pub unowned_action: UnownedAction,
    /// Analyze each good media file once with ffprobe, when it is installed.
    pub media_probe: bool,
}

impl Default for Health {
    fn default() -> Self {
        Self {
            enabled: true,
            check_percent: infinidysk_core::media::SAMPLE_PERCENT,
            import_check_percent: infinidysk_core::media::SAMPLE_PERCENT,
            auto_repair: true,
            degraded_tolerance: true,
            unowned_action: UnownedAction::Delete,
            media_probe: true,
        }
    }
}

#[derive(Clone, Copy, Debug, Default, PartialEq, Eq, Serialize, Deserialize, ToSchema)]
#[serde(rename_all = "snake_case")]
pub enum UnownedAction {
    #[default]
    Delete,
    ActionNeeded,
}

/// How we work with the Arr instances; the rules are fixed by `infinidysk-lab:docs/import-policy.md` §10.
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize, ToSchema)]
#[serde(default)]
pub struct ArrOptions {
    /// Clear downloads whose import is stuck for a known reason from the Arr queues.
    pub queue_management: bool,
}

impl Default for ArrOptions {
    fn default() -> Self {
        Self {
            queue_management: true,
        }
    }
}

#[derive(Clone, Debug, Default, PartialEq, Eq, Serialize, Deserialize, ToSchema)]
#[serde(default)]
pub struct Metrics {
    /// Serve Prometheus metrics at `/metrics`, behind the admin API key
    /// (`Authorization: Bearer <key>`).
    pub prometheus: bool,
}

#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize, ToSchema)]
#[serde(default)]
pub struct Sab {
    pub api_key: Secret,
    pub categories: Vec<String>,
    /// Category for NZBs submitted without one, or with `*`.
    pub manual_category: String,
}

impl Default for Sab {
    fn default() -> Self {
        Self {
            api_key: Secret::default(),
            categories: ["audio", "software", "tv", "movies"]
                .map(str::to_owned)
                .into(),
            manual_category: "uncategorized".to_owned(),
        }
    }
}

impl Sab {
    /// Categories offered to download clients: the manual category first.
    pub fn categories(&self) -> impl Iterator<Item = &str> {
        std::iter::once(self.manual_category.as_str())
            .chain(self.categories.iter().map(String::as_str))
            .map(str::trim)
            .filter(|c| !c.is_empty())
    }
}

/// Sign-in choices and admin API secrets; the admin login itself lives in the accounts table.
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize, ToSchema)]
#[serde(default)]
pub struct Auth {
    /// `X-Api-Key` for scripts calling the admin API; separate from the SAB key.
    pub api_key: Secret,
    /// Signs UI session cookies; changing it logs everyone out.
    pub session_key: Secret,
    /// Who must sign in to the web UI and admin API.
    pub mode: AuthMode,
    /// Sign in with the admin username and password. Turning it off takes effect only
    /// while SSO is set up.
    pub local_login: bool,
    /// Single sign-on through one OIDC provider.
    pub oidc: Oidc,
}

impl Default for Auth {
    fn default() -> Self {
        Self {
            api_key: Secret::default(),
            session_key: Secret::default(),
            mode: AuthMode::default(),
            local_login: true,
            oidc: Oidc::default(),
        }
    }
}

impl Auth {
    /// Username and password sign-in is accepted; always while SSO is off.
    #[must_use]
    pub fn password_login(&self) -> bool {
        self.local_login || !self.oidc.enabled()
    }
}

/// SSO through one OIDC provider; on while `issuer` is set.
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize, ToSchema)]
#[serde(default)]
pub struct Oidc {
    /// Issuer URL exactly as the provider publishes it; empty turns SSO off.
    pub issuer: String,
    pub client_id: String,
    pub client_secret: Secret,
    /// Callback registered at the provider. Empty means `network.public_url`, else the
    /// address the browser used, plus `/auth/oidc/callback`.
    pub redirect_uri: String,
    /// Space-separated; `openid` is always requested.
    pub scopes: String,
    /// ID-token claim shown as the username; then `preferred_username`, `email`, `sub`.
    pub username_claim: String,
    /// Shown on the sign-in page as "Sign in with <name>"; empty shows `SSO`.
    pub display_name: String,
}

impl Default for Oidc {
    fn default() -> Self {
        Self {
            issuer: String::new(),
            client_id: String::new(),
            client_secret: Secret::default(),
            redirect_uri: String::new(),
            scopes: "openid profile email".to_owned(),
            username_claim: "preferred_username".to_owned(),
            display_name: String::new(),
        }
    }
}

impl Oidc {
    #[must_use]
    pub fn enabled(&self) -> bool {
        !self.issuer.is_empty()
    }

    /// The provider name the sign-in page shows.
    #[must_use]
    pub fn display_name(&self) -> &str {
        if self.display_name.is_empty() {
            "SSO"
        } else {
            &self.display_name
        }
    }

    /// Trims the fields; checks them only while SSO is on, so clearing the issuer alone
    /// always turns SSO off.
    pub(crate) fn validate(&mut self) -> Result<(), Error> {
        self.issuer = self.issuer.trim().to_owned();
        self.client_id = self.client_id.trim().to_owned();
        self.redirect_uri = self.redirect_uri.trim().to_owned();
        self.username_claim = self.username_claim.trim().to_owned();
        self.display_name = self
            .display_name
            .split_whitespace()
            .collect::<Vec<_>>()
            .join(" ");
        self.scopes = self.scopes.split_whitespace().collect::<Vec<_>>().join(" ");
        if self.display_name.chars().count() > MAX_DISPLAY_NAME
            || self.display_name.chars().any(char::is_control)
        {
            return Err(invalid(
                "auth.oidc.display_name",
                format!("Display name must be plain text of at most {MAX_DISPLAY_NAME} characters"),
            ));
        }
        if !self.enabled() {
            return Ok(());
        }
        if !issuer_ok(&self.issuer) {
            return Err(invalid(
                "auth.oidc.issuer",
                format!(
                    "Issuer {:?} must be an https:// address without a query (http:// only on this machine)",
                    self.issuer
                ),
            ));
        }
        if self.client_id.is_empty() {
            return Err(invalid(
                "auth.oidc.client_id",
                "SSO needs the client ID from your identity provider".to_owned(),
            ));
        }
        if self.client_secret.0.is_empty() {
            return Err(invalid(
                "auth.oidc.client_secret",
                "SSO needs the client secret from your identity provider".to_owned(),
            ));
        }
        if !self.redirect_uri.is_empty() && !redirect_ok(&self.redirect_uri) {
            return Err(invalid(
                "auth.oidc.redirect_uri",
                format!(
                    "Redirect URI {:?} must be an http:// or https:// address without a fragment",
                    self.redirect_uri
                ),
            ));
        }
        Ok(())
    }
}

/// Fits the sign-in button on a phone screen.
const MAX_DISPLAY_NAME: usize = 40;

/// `https://`, or `http://` to this machine ([`crate::oidc::secure`]), with no query or
/// fragment. The trailing slash stays: discovery compares the issuer exactly.
fn issuer_ok(issuer: &str) -> bool {
    reqwest::Url::parse(issuer).is_ok_and(|u| {
        crate::oidc::secure(u.scheme(), u.host_str().unwrap_or_default())
            && u.query().is_none()
            && u.fragment().is_none()
    })
}

fn redirect_ok(uri: &str) -> bool {
    reqwest::Url::parse(uri).is_ok_and(|u| {
        matches!(u.scheme(), "http" | "https") && u.host_str().is_some() && u.fragment().is_none()
    })
}

#[derive(Clone, Copy, Debug, Default, PartialEq, Eq, Serialize, Deserialize, ToSchema)]
#[serde(rename_all = "snake_case")]
pub enum AuthMode {
    #[default]
    Required,
    /// Clients on loopback, private or link-local addresses skip sign-in.
    Local,
    Disabled,
}

/// `WebDAV` login, separate from the UI login.
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize, ToSchema)]
#[serde(default)]
pub struct WebDav {
    pub user: String,
    pub password: Secret,
    /// `downloadKey` that `/view` links must carry.
    pub download_key: Secret,
}

impl Default for WebDav {
    fn default() -> Self {
        Self {
            user: "admin".to_owned(),
            password: Secret::default(),
            download_key: Secret::default(),
        }
    }
}

#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize, ToSchema)]
#[serde(default)]
pub struct Mount {
    /// Where the tree is mounted, as Sonarr/Radarr see it; completed downloads are reported
    /// under `<dir>/completed-symlinks`.
    pub dir: String,
    /// Serve the built-in FUSE mount at `dir`. Off leaves `dir` to an external mount.
    pub fuse: bool,
    /// Media files on the mount report 1970 dates, matching an rclone mount that used
    /// `--no-modtime`, so media servers that stored those dates do not rescan.
    pub epoch_times: bool,
}

impl Default for Mount {
    fn default() -> Self {
        Self {
            dir: "/mnt/infinidysk".to_owned(),
            fuse: true,
            epoch_times: false,
        }
    }
}

/// Streaming and cache tuning: a preset, plus overrides (`None` takes the preset's value).
#[derive(Clone, Debug, Default, PartialEq, Eq, Serialize, Deserialize, ToSchema)]
#[serde(default)]
pub struct Performance {
    pub preset: Preset,
    /// Bytes each open file fetches ahead of the reader.
    pub read_ahead_mb: Option<u32>,
    pub memory_cache_enabled: Option<bool>,
    pub memory_cache_mb: Option<u32>,
    pub disk_cache_enabled: Option<bool>,
    pub disk_cache_dir: Option<String>,
    pub disk_cache_gb: Option<u32>,
    pub disk_cache_max_age_hours: Option<u32>,
    /// Decoded bytes all open files together may hold in flight.
    pub read_ahead_memory_mb: Option<u32>,
    pub fuse_max_readahead_kb: Option<u32>,
    pub fuse_max_background: Option<u16>,
}

#[derive(Clone, Copy, Debug, Default, PartialEq, Eq, Serialize, Deserialize, ToSchema)]
#[serde(rename_all = "snake_case")]
pub enum Preset {
    #[default]
    Balanced,
    MaxPlayback,
    LowResources,
}

/// Concrete performance values: a preset with its overrides applied.
#[derive(Clone, Debug, PartialEq, Eq, Serialize, ToSchema)]
pub struct Tuning {
    pub read_ahead_mb: u32,
    pub memory_cache_enabled: bool,
    pub memory_cache_mb: u32,
    pub disk_cache_enabled: bool,
    pub disk_cache_dir: String,
    pub disk_cache_gb: u32,
    pub disk_cache_max_age_hours: u32,
    pub read_ahead_memory_mb: u32,
    pub fuse_max_readahead_kb: u32,
    pub fuse_max_background: u16,
}

impl Preset {
    pub const ALL: [Self; 3] = [Self::Balanced, Self::MaxPlayback, Self::LowResources];

    #[must_use]
    pub fn tuning(self) -> Tuning {
        // Read-ahead memory = read-ahead x streams at full depth (10, ~13, 5).
        let (read_ahead_mb, memory_cache_mb, read_ahead_memory_mb, fuse_kb, fuse_background) =
            match self {
                Self::Balanced => (60, 600, 600, 5000, 64),
                Self::MaxPlayback => (150, 1250, 2000, 10_000, 128),
                Self::LowResources => (20, 150, 100, 1250, 32),
            };
        Tuning {
            read_ahead_mb,
            memory_cache_enabled: true,
            memory_cache_mb,
            disk_cache_enabled: false,
            disk_cache_dir: DEFAULT_CACHE_DIR.to_owned(),
            disk_cache_gb: 25,
            disk_cache_max_age_hours: 168,
            read_ahead_memory_mb,
            fuse_max_readahead_kb: fuse_kb,
            fuse_max_background: fuse_background,
        }
    }
}

impl Performance {
    #[must_use]
    pub fn effective(&self) -> Tuning {
        let base = self.preset.tuning();
        Tuning {
            read_ahead_mb: self.read_ahead_mb.unwrap_or(base.read_ahead_mb),
            memory_cache_enabled: self
                .memory_cache_enabled
                .unwrap_or(base.memory_cache_enabled),
            memory_cache_mb: self.memory_cache_mb.unwrap_or(base.memory_cache_mb),
            disk_cache_enabled: self.disk_cache_enabled.unwrap_or(base.disk_cache_enabled),
            disk_cache_dir: self.disk_cache_dir.clone().unwrap_or(base.disk_cache_dir),
            disk_cache_gb: self.disk_cache_gb.unwrap_or(base.disk_cache_gb),
            disk_cache_max_age_hours: self
                .disk_cache_max_age_hours
                .unwrap_or(base.disk_cache_max_age_hours),
            read_ahead_memory_mb: self
                .read_ahead_memory_mb
                .unwrap_or(base.read_ahead_memory_mb),
            fuse_max_readahead_kb: self
                .fuse_max_readahead_kb
                .unwrap_or(base.fuse_max_readahead_kb),
            fuse_max_background: self.fuse_max_background.unwrap_or(base.fuse_max_background),
        }
    }

    fn reset_invalid(&mut self) {
        let p = self.preset.tuning();
        reset_override(
            "performance.read_ahead_mb",
            &mut self.read_ahead_mb,
            &READ_AHEAD_MB,
            p.read_ahead_mb,
        );
        reset_override(
            "performance.memory_cache_mb",
            &mut self.memory_cache_mb,
            &MEMORY_CACHE_MB,
            p.memory_cache_mb,
        );
        reset_override(
            "performance.disk_cache_gb",
            &mut self.disk_cache_gb,
            &DISK_CACHE_GB,
            p.disk_cache_gb,
        );
        reset_override(
            "performance.disk_cache_max_age_hours",
            &mut self.disk_cache_max_age_hours,
            &DISK_CACHE_MAX_AGE_HOURS,
            p.disk_cache_max_age_hours,
        );
        reset_override(
            "performance.read_ahead_memory_mb",
            &mut self.read_ahead_memory_mb,
            &READ_AHEAD_MEMORY_MB,
            p.read_ahead_memory_mb,
        );
        reset_override(
            "performance.fuse_max_readahead_kb",
            &mut self.fuse_max_readahead_kb,
            &FUSE_MAX_READAHEAD_KB,
            p.fuse_max_readahead_kb,
        );
        reset_override(
            "performance.fuse_max_background",
            &mut self.fuse_max_background,
            &FUSE_MAX_BACKGROUND,
            p.fuse_max_background,
        );
    }

    fn validate(&self, mount_dir: &str, default_cache_dir: &str) -> Result<(), Error> {
        let t = self.effective();
        check(
            "performance.read_ahead_mb",
            "Read-ahead",
            t.read_ahead_mb,
            &READ_AHEAD_MB,
        )?;
        check(
            "performance.memory_cache_mb",
            "Memory cache size",
            t.memory_cache_mb,
            &MEMORY_CACHE_MB,
        )?;
        check(
            "performance.disk_cache_gb",
            "Disk cache size",
            t.disk_cache_gb,
            &DISK_CACHE_GB,
        )?;
        check(
            "performance.disk_cache_max_age_hours",
            "Disk cache max age",
            t.disk_cache_max_age_hours,
            &DISK_CACHE_MAX_AGE_HOURS,
        )?;
        check(
            "performance.read_ahead_memory_mb",
            "Read-ahead memory",
            t.read_ahead_memory_mb,
            &READ_AHEAD_MEMORY_MB,
        )?;
        check(
            "performance.fuse_max_readahead_kb",
            "FUSE max readahead",
            t.fuse_max_readahead_kb,
            &FUSE_MAX_READAHEAD_KB,
        )?;
        check(
            "performance.fuse_max_background",
            "FUSE max background",
            t.fuse_max_background,
            &FUSE_MAX_BACKGROUND,
        )?;
        let cache_dir = self.disk_cache_dir.as_deref().unwrap_or(default_cache_dir);
        let dir = std::path::Path::new(cache_dir);
        if !dir.is_absolute() {
            return Err(invalid(
                "performance.disk_cache_dir",
                format!("Disk cache dir {cache_dir:?} must be an absolute path"),
            ));
        }
        if dir.starts_with(mount_dir) {
            return Err(invalid(
                "performance.disk_cache_dir",
                format!("Disk cache dir {cache_dir:?} must be outside the mount dir"),
            ));
        }
        Ok(())
    }
}

#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize, ToSchema)]
#[serde(default)]
pub struct Provider {
    /// Stable across edits, so masked secrets can be matched back.
    pub id: Uuid,
    pub name: String,
    /// Catalog profile id. When set, `port` and `backbone` come from the profile server
    /// named by `hostname` (its first server when empty).
    pub profile: Option<String>,
    pub hostname: String,
    pub port: u16,
    pub tls: bool,
    pub username: String,
    pub password: Secret,
    pub max_connections: u16,
    /// BODY requests in flight per connection, `1..=16`; Auto-Tune Pipeline sets it.
    pub pipeline_depth: u8,
    /// Same number = one pool; higher numbers are asked only after lower ones miss.
    pub priority: u32,
    /// Catalog backbone id; a custom provider without one is its own group.
    pub backbone: Option<String>,
    /// Download cap; once used up, requests go to other providers until it resets.
    pub quota: Option<Quota>,
    pub enabled: bool,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq, Serialize, Deserialize, ToSchema)]
pub struct Quota {
    /// Decimal gigabytes (10^9 bytes), as providers sell them.
    pub gb: u32,
    pub period: QuotaPeriod,
    /// Monthly: the day of the month usage resets, `1..=31`, at 00:00 UTC; shorter
    /// months reset on their last day.
    #[serde(default = "first_day")]
    pub reset_day: u8,
}

const fn first_day() -> u8 {
    1
}

#[derive(Clone, Copy, Debug, PartialEq, Eq, Serialize, Deserialize, ToSchema)]
#[serde(rename_all = "lowercase")]
pub enum QuotaPeriod {
    Monthly,
    /// A prepaid block: resets only by hand.
    Block,
}

impl Default for Provider {
    fn default() -> Self {
        Self {
            id: Uuid::now_v7(),
            name: String::new(),
            profile: None,
            hostname: String::new(),
            port: 563,
            tls: true,
            username: String::new(),
            password: Secret::default(),
            max_connections: 10,
            pipeline_depth: 4,
            priority: 0,
            backbone: None,
            quota: None,
            enabled: true,
        }
    }
}

#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize, ToSchema)]
pub struct ArrInstance {
    #[serde(default = "Uuid::now_v7")]
    pub id: Uuid,
    pub name: String,
    pub profile: ArrProfile,
    pub url: String,
    pub api_key: Secret,
    #[serde(default = "enabled")]
    pub enabled: bool,
}

const fn enabled() -> bool {
    true
}

/// Arr-specific behavior hangs off this, so new Arr types are additive.
#[derive(Clone, Copy, Debug, PartialEq, Eq, Serialize, Deserialize, ToSchema)]
#[serde(rename_all = "lowercase")]
pub enum ArrProfile {
    Sonarr,
    Radarr,
    Lidarr,
    Chaptarr,
    Whisparr,
    Sportarr,
}

impl Settings {
    /// A copy safe to return from the API or log: every non-empty secret is [`MASK`].
    #[must_use]
    pub fn masked(&self) -> Self {
        let mut out = self.clone();
        for indexer in &mut out.indexers {
            if indexer.public_base_url() == MASK {
                MASK.clone_into(&mut indexer.base_url);
            }
            indexer.api_key = indexer.api_key.masked();
        }
        out.sab.api_key = out.sab.api_key.masked();
        out.auth.api_key = out.auth.api_key.masked();
        out.auth.session_key = out.auth.session_key.masked();
        out.auth.oidc.client_secret = out.auth.oidc.client_secret.masked();
        out.webdav.password = out.webdav.password.masked();
        out.webdav.download_key = out.webdav.download_key.masked();
        out.imports.strm_key = out.imports.strm_key.masked();
        for p in &mut out.providers {
            p.password = p.password.masked();
        }
        for a in &mut out.arr_instances {
            a.api_key = a.api_key.masked();
        }
        out
    }

    /// Replaces [`MASK`] placeholders with the secrets in `current`.
    fn restore_secrets(&mut self, current: &Self) {
        for indexer in &mut self.indexers {
            if let Some(old) = current.indexers.iter().find(|old| old.id == indexer.id) {
                indexer.api_key.restore(&old.api_key);
            }
        }
        self.sab.api_key.restore(&current.sab.api_key);
        self.auth.api_key.restore(&current.auth.api_key);
        self.auth.session_key.restore(&current.auth.session_key);
        self.auth
            .oidc
            .client_secret
            .restore(&current.auth.oidc.client_secret);
        self.webdav.password.restore(&current.webdav.password);
        self.webdav
            .download_key
            .restore(&current.webdav.download_key);
        self.imports.strm_key.restore(&current.imports.strm_key);
        for p in &mut self.providers {
            if let Some(c) = current.providers.iter().find(|c| c.id == p.id) {
                p.password.restore(&c.password);
            }
        }
        for a in &mut self.arr_instances {
            if let Some(c) = current.arr_instances.iter().find(|c| c.id == a.id) {
                a.api_key.restore(&c.api_key);
            }
        }
    }

    /// On load: resets out-of-range numbers and a blank manual category to their defaults,
    /// so a value an older build or a hand edit stored does not stop startup.
    fn reset_invalid(&mut self) {
        let d = Self::default();
        reset(
            "health.check_percent",
            &mut self.health.check_percent,
            &CHECK_PERCENT,
            d.health.check_percent,
        );
        reset(
            "health.import_check_percent",
            &mut self.health.import_check_percent,
            &CHECK_PERCENT,
            d.health.import_check_percent,
        );
        reset(
            "queue.concurrency",
            &mut self.queue.concurrency,
            &QUEUE_CONCURRENCY,
            d.queue.concurrency,
        );
        reset(
            "queue.nzb_retention_days",
            &mut self.queue.nzb_retention_days,
            &NZB_RETENTION_DAYS,
            d.queue.nzb_retention_days,
        );
        if self.sab.manual_category.trim().is_empty() {
            reset_warning(
                "sab.manual_category",
                &format!("{:?}", self.sab.manual_category),
                &d.sab.manual_category,
            );
            self.sab.manual_category = d.sab.manual_category;
        }
        if self.maintenance.window.minutes().is_none() {
            let w = &self.maintenance.window;
            reset_warning(
                "maintenance.window",
                &format!("{}-{}", w.start, w.end),
                &format!(
                    "{}-{}",
                    d.maintenance.window.start, d.maintenance.window.end
                ),
            );
            self.maintenance.window = d.maintenance.window;
        }
        reset(
            "maintenance.backup.keep",
            &mut self.maintenance.backup.keep,
            &BACKUP_KEEP,
            d.maintenance.backup.keep,
        );
        self.performance.reset_invalid();
        if let Some(limit) = self.network.download_limit_mb_per_s
            && !DOWNLOAD_LIMIT_MB_PER_S.contains(&limit)
        {
            reset_warning("network.download_limit_mb_per_s", &limit, &"no limit");
            self.network.download_limit_mb_per_s = None;
        }
        let pd = Provider::default();
        for p in &mut self.providers {
            let f = format!("providers[{}].", p.label());
            reset(
                &format!("{f}max_connections"),
                &mut p.max_connections,
                &MAX_CONNECTIONS,
                pd.max_connections,
            );
            reset(
                &format!("{f}pipeline_depth"),
                &mut p.pipeline_depth,
                &PIPELINE_DEPTH,
                pd.pipeline_depth,
            );
            reset(&format!("{f}port"), &mut p.port, &PORT, pd.port);
            if let Some(q) = p.quota
                && !QUOTA_GB.contains(&q.gb)
            {
                reset_warning(&format!("{f}quota.gb"), &q.gb, &"no quota");
                p.quota = None;
            }
            if let Some(q) = &mut p.quota {
                reset(
                    &format!("{f}quota.reset_day"),
                    &mut q.reset_day,
                    &RESET_DAY,
                    first_day(),
                );
            }
        }
    }

    /// Checks every field and fills the provider fields that come from the catalog.
    pub fn validate(&mut self) -> Result<(), Error> {
        self.validate_in(DEFAULT_CACHE_DIR)
    }

    /// [`Self::validate`], with the disk cache in `default_cache_dir` when none is saved.
    pub(crate) fn validate_in(&mut self, default_cache_dir: &str) -> Result<(), Error> {
        if !self.mount.dir.starts_with('/') {
            return Err(invalid(
                "mount.dir",
                format!("Mount dir {:?} must be an absolute path", self.mount.dir),
            ));
        }
        // A mount over the config folder would hide its files once it mounts.
        if let Some(config_dir) = std::path::Path::new(default_cache_dir).parent()
            && config_dir.starts_with(&self.mount.dir)
        {
            return Err(invalid(
                "mount.dir",
                format!(
                    "Mount dir {:?} must not contain the config folder {}",
                    self.mount.dir,
                    config_dir.display()
                ),
            ));
        }
        if self.webdav.user.trim().is_empty() {
            return Err(invalid(
                "webdav.user",
                "WebDAV user must not be empty".to_owned(),
            ));
        }
        if self.sab.manual_category.trim().is_empty() {
            return Err(invalid(
                "sab.manual_category",
                "Default category must not be empty".to_owned(),
            ));
        }
        check(
            "health.check_percent",
            "Check percent",
            self.health.check_percent,
            &CHECK_PERCENT,
        )?;
        check(
            "health.import_check_percent",
            "Import check percent",
            self.health.import_check_percent,
            &CHECK_PERCENT,
        )?;
        check(
            "queue.concurrency",
            "Concurrency",
            self.queue.concurrency,
            &QUEUE_CONCURRENCY,
        )?;
        check(
            "queue.nzb_retention_days",
            "NZB retention days",
            self.queue.nzb_retention_days,
            &NZB_RETENTION_DAYS,
        )?;
        self.queue.extra_blocklist = self
            .queue
            .extra_blocklist
            .iter()
            .map(|g| g.trim().to_owned())
            .filter(|g| !g.is_empty())
            .collect();
        self.performance
            .validate(&self.mount.dir, default_cache_dir)?;
        if self.maintenance.window.minutes().is_none() {
            return Err(invalid(
                "maintenance.window",
                "The window needs a start and a different end, each HH:MM".to_owned(),
            ));
        }
        self.maintenance.scheduled.sort_unstable();
        self.maintenance.scheduled.dedup();
        check(
            "maintenance.backup.keep",
            "Copies to keep",
            self.maintenance.backup.keep,
            &BACKUP_KEEP,
        )?;
        let backup_dir = self.maintenance.backup.dir.trim();
        if !backup_dir.is_empty() {
            if !backup_dir.starts_with('/') {
                return Err(invalid(
                    "maintenance.backup.dir",
                    format!("Backup folder {backup_dir:?} must be an absolute path"),
                ));
            }
            if std::path::Path::new(backup_dir).starts_with(&self.mount.dir) {
                return Err(invalid(
                    "maintenance.backup.dir",
                    format!("Backup folder {backup_dir:?} must be outside the mount"),
                ));
            }
        }
        self.maintenance.backup.dir = backup_dir.to_owned();
        let strm_dir = self.imports.strm_dir.trim();
        if !strm_dir.starts_with('/') {
            return Err(invalid(
                "imports.strm_dir",
                format!("STRM directory {strm_dir:?} must be an absolute path"),
            ));
        }
        self.imports.strm_dir = strm_dir.to_owned();
        self.imports
            .library_dirs
            .retain(|d| !d.path.trim().is_empty());
        for dir in &mut self.imports.library_dirs {
            for path in [&mut dir.path, &mut dir.arr_path] {
                let trimmed = path.trim();
                if !trimmed.is_empty() && !trimmed.starts_with('/') {
                    return Err(invalid(
                        "imports.library_dirs",
                        format!("Library folder {trimmed:?} must be an absolute path"),
                    ));
                }
                *path = if trimmed == "/" {
                    trimmed.to_owned()
                } else {
                    trimmed.trim_end_matches('/').to_owned()
                };
            }
        }
        let inside = |a: &str, b: &str| std::path::Path::new(a).starts_with(b);
        // The mount and the STRM directory may sit inside a library folder (one `/data`
        // for everything); library walks skip them.
        for (i, dir) in self.imports.library_dirs.iter().enumerate() {
            let path = dir.path.as_str();
            let clash = [
                (self.mount.dir.as_str(), "is inside the mount"),
                (
                    self.imports.strm_dir.as_str(),
                    "is inside the completed STRM directory",
                ),
            ]
            .into_iter()
            .find(|(other, _)| inside(path, other))
            .map(|(_, what)| what)
            .or_else(|| {
                self.imports
                    .library_dirs
                    .iter()
                    .enumerate()
                    .any(|(j, o)| i != j && (inside(path, &o.path) || inside(&o.path, path)))
                    .then_some("overlaps another library folder")
            });
            if let Some(what) = clash {
                return Err(invalid(
                    "imports.library_dirs",
                    format!("Library folder {path:?} {what}"),
                ));
            }
        }
        self.network
            .trusted_proxies
            .retain(|p| !p.trim().is_empty());
        for proxy in &mut self.network.trusted_proxies {
            if infinidysk_webdav::parse_proxy(proxy).is_none() {
                return Err(invalid(
                    "network.trusted_proxies",
                    format!("Trusted proxy {proxy:?} is not an IP address or CIDR range"),
                ));
            }
            *proxy = proxy.trim().to_owned();
        }
        let url = self.network.public_url.trim().trim_end_matches('/');
        let host = url
            .strip_prefix("https://")
            .or_else(|| url.strip_prefix("http://"));
        if !url.is_empty()
            && host.is_none_or(|h| {
                h.is_empty()
                    || h.starts_with('/')
                    || h.contains(|c: char| c.is_whitespace() || c == '?' || c == '#')
            })
        {
            return Err(invalid(
                "network.public_url",
                format!(
                    "Public URL {url:?} must be an http:// or https:// address without a query"
                ),
            ));
        }
        self.network.public_url = url.to_owned();
        self.auth.oidc.validate()?;
        if let Some(limit) = self.network.download_limit_mb_per_s {
            check(
                "network.download_limit_mb_per_s",
                "Download limit MB/s",
                limit,
                &DOWNLOAD_LIMIT_MB_PER_S,
            )?;
        }
        if self.imports.strategy == ImportStrategy::Strm && self.network.public_url.is_empty() {
            return Err(invalid(
                "imports.strategy",
                "STRM files need a public URL, the address media servers play them from".to_owned(),
            ));
        }
        let mut ids = HashSet::new();
        for p in &mut self.providers {
            if !ids.insert(p.id) {
                return Err(invalid(
                    "providers",
                    format!("Duplicate provider id {}", p.id),
                ));
            }
            p.resolve()?;
        }
        ids.clear();
        if self.indexers.len() > MAX_INDEXERS {
            return Err(invalid(
                "indexers",
                format!("At most {MAX_INDEXERS} indexers can be configured"),
            ));
        }
        for indexer in &self.indexers {
            if !ids.insert(indexer.id) {
                return Err(invalid("indexers", "Duplicate indexer ID".to_owned()));
            }
            indexer.validate()?;
        }
        ids.clear();
        for a in &self.arr_instances {
            if !ids.insert(a.id) {
                return Err(invalid(
                    "arr_instances",
                    format!("Duplicate Arr instance id {}", a.id),
                ));
            }
            a.validate()?;
        }
        Ok(())
    }
}

impl Provider {
    fn label(&self) -> &str {
        [&self.name, &self.hostname]
            .into_iter()
            .find(|s| !s.is_empty())
            .or(self.profile.as_ref())
            .map_or("", String::as_str)
    }

    pub(crate) fn resolve(&mut self) -> Result<(), Error> {
        const P: &str = "providers";
        let label = self.label().to_owned();
        check(
            P,
            &format!("Provider {label}: max connections"),
            self.max_connections,
            &MAX_CONNECTIONS,
        )?;
        check(
            P,
            &format!("Provider {label}: pipeline depth"),
            self.pipeline_depth,
            &PIPELINE_DEPTH,
        )?;
        if let Some(quota) = &self.quota {
            check(
                P,
                &format!("Provider {label}: quota GB"),
                quota.gb,
                &QUOTA_GB,
            )?;
            check(
                P,
                &format!("Provider {label}: quota reset day"),
                quota.reset_day,
                &RESET_DAY,
            )?;
        }
        if let Some(id) = &self.profile {
            let profile = catalog::profile(id).ok_or_else(|| {
                invalid(
                    P,
                    format!("Provider {label}: unknown catalog profile {id:?}"),
                )
            })?;
            let server = if self.hostname.is_empty() {
                profile.servers.first()
            } else {
                profile
                    .servers
                    .iter()
                    .find(|s| s.hostname.eq_ignore_ascii_case(&self.hostname))
            }
            .ok_or_else(|| {
                invalid(
                    P,
                    format!(
                        "Provider {label}: {} is not a {} server",
                        self.hostname, profile.name
                    ),
                )
            })?;
            server.hostname.clone_into(&mut self.hostname);
            self.port = server.port;
            self.backbone = Some(server.backbone.to_owned());
            if self.name.is_empty() {
                profile.name.clone_into(&mut self.name);
            }
        } else {
            if self.hostname.is_empty()
                || self
                    .hostname
                    .contains(|c: char| c.is_whitespace() || c == '/')
            {
                return Err(invalid(
                    P,
                    format!("Provider {label}: invalid hostname {:?}", self.hostname),
                ));
            }
            check(P, &format!("Provider {label}: port"), self.port, &PORT)?;
            if let Some(b) = &self.backbone
                && catalog::backbone(b).is_none()
            {
                return Err(invalid(
                    P,
                    format!(
                        "Provider {label}: unknown backbone {b:?}; pick one from the catalog or none"
                    ),
                ));
            }
            if self.name.is_empty() {
                self.name.clone_from(&self.hostname);
            }
        }
        Ok(())
    }
}

impl ArrInstance {
    pub(crate) fn validate(&self) -> Result<(), Error> {
        const A: &str = "arr_instances";
        let label = &self.name;
        if label.trim().is_empty() {
            return Err(invalid(A, "Arr instance name must not be empty".to_owned()));
        }
        let rest = self
            .url
            .strip_prefix("https://")
            .or_else(|| self.url.strip_prefix("http://"));
        if rest.is_none_or(str::is_empty) {
            return Err(invalid(
                A,
                format!("Arr instance {label}: URL must start with http:// or https://"),
            ));
        }
        if self.api_key.0.is_empty() {
            return Err(invalid(
                A,
                format!("Arr instance {label}: API key is required"),
            ));
        }
        Ok(())
    }
}

struct Override {
    path: Vec<String>,
    value: Value,
}

/// Effective settings plus the layers they came from.
pub struct ConfigStore {
    db: Db,
    stored: Map<String, Value>,
    env: Vec<Override>,
    settings: Settings,
    indexers_revision: Uuid,
    /// The disk cache folder when none is saved; validation checks it against the mount.
    default_cache_dir: String,
}

impl ConfigStore {
    /// Loads stored settings and overlays `vars` (normally `std::env::vars()`).
    pub async fn load(
        db: Db,
        vars: impl IntoIterator<Item = (String, String)>,
    ) -> Result<Self, Error> {
        Self::load_in(db, vars, DEFAULT_CACHE_DIR.to_owned()).await
    }

    /// [`Self::load`], checking the disk cache in `default_cache_dir` when none is saved.
    pub(crate) async fn load_in(
        db: Db,
        vars: impl IntoIterator<Item = (String, String)>,
        default_cache_dir: String,
    ) -> Result<Self, Error> {
        let defaults = serde_json::to_value(Settings::default())?;
        let mut env: Vec<Override> = vars
            .into_iter()
            .filter_map(|(key, raw)| {
                let path: Vec<String> = key
                    .strip_prefix(ENV_PREFIX)?
                    .split("__")
                    .map(str::to_ascii_lowercase)
                    .collect();
                let Some(default) = lookup(&defaults, &path) else {
                    tracing::warn!(key, "ignoring unknown config environment variable");
                    return None;
                };
                // Strings stay verbatim so a numeric password is not parsed as a number.
                let value = if default.is_string() {
                    Value::String(raw)
                } else {
                    serde_json::from_str(&raw).unwrap_or(Value::String(raw))
                };
                Some(Override { path, value })
            })
            .collect();

        let mut stored = Map::new();
        for (key, value) in db.config().await? {
            stored.insert(key, serde_json::from_str(&value)?);
        }
        let settings = effective(&stored, &env, &default_cache_dir)?;
        // Freeze generated ids and catalog fields so env-defined lists round-trip through saves.
        let resolved = serde_json::to_value(&settings)?;
        for o in &mut env {
            if let Some(v) = lookup(&resolved, &o.path) {
                o.value = v.clone();
            }
        }
        Ok(Self {
            db,
            stored,
            env,
            settings,
            indexers_revision: Uuid::new_v4(),
            default_cache_dir,
        })
    }

    pub const fn settings(&self) -> &Settings {
        &self.settings
    }

    pub const fn indexers_revision(&self) -> Uuid {
        self.indexers_revision
    }

    /// Fills every empty server secret (API keys, session key, `WebDAV` password and
    /// download key, STRM key) with a random one and stores it.
    pub async fn generate_missing_secrets(&mut self) -> Result<(), Error> {
        let mut next = self.settings.clone();
        let mut changed = false;
        for secret in [
            &mut next.sab.api_key,
            &mut next.auth.api_key,
            &mut next.auth.session_key,
            &mut next.webdav.password,
            &mut next.webdav.download_key,
            &mut next.imports.strm_key,
        ] {
            if secret.0.is_empty() {
                secret.0 = Uuid::new_v4().simple().to_string();
                changed = true;
            }
        }
        if changed {
            self.save(next).await?;
        }
        Ok(())
    }

    /// Dotted paths fixed by environment variables.
    pub fn locked(&self) -> impl Iterator<Item = String> + '_ {
        self.env.iter().map(|o| o.path.join("."))
    }

    /// Validates and persists `new`. [`MASK`] secrets keep their current value.
    pub async fn save(&mut self, mut new: Settings) -> Result<(), Error> {
        for indexer in &new.indexers {
            let Some(old) = self
                .settings
                .indexers
                .iter()
                .find(|old| old.id == indexer.id)
            else {
                continue;
            };
            let after = reqwest::Url::parse(&indexer.base_url).ok();
            if indexer.api_key.0 == MASK {
                let before = reqwest::Url::parse(&old.base_url)
                    .ok()
                    .map(|url| url.origin());
                if before != after.as_ref().map(reqwest::Url::origin) {
                    return Err(invalid(
                        "indexers",
                        "Changing an indexer origin requires its API key to be provided again."
                            .to_owned(),
                    ));
                }
            } else if indexer.api_key != old.api_key
                && after.as_ref().is_some_and(|url| {
                    crate::newznab::base_url_contains_key(&indexer.base_url, url, &old.api_key.0)
                })
            {
                return Err(invalid("indexers", crate::newznab::BASE_URL_KEY.to_owned()));
            }
        }
        new.restore_secrets(&self.settings);
        let indexers_changed = new.indexers != self.settings.indexers;
        new.validate_in(&self.default_cache_dir)?;
        let mut tree = serde_json::to_value(&new)?;
        let current = serde_json::to_value(&self.settings)?;
        let stored = Value::Object(self.stored.clone());
        for o in &self.env {
            if lookup(&tree, &o.path) != lookup(&current, &o.path) {
                return Err(Error::Locked(o.path.join(".")));
            }
            match lookup(&stored, &o.path) {
                Some(v) => set(&mut tree, &o.path, v.clone()),
                None => remove(&mut tree, &o.path),
            }
        }

        let defaults = serde_json::to_value(Settings::default())?;
        let mut next = Map::new();
        if let Value::Object(sections) = tree {
            for (key, value) in sections {
                if let Some(v) = diff(value, defaults.get(&key)) {
                    next.insert(key, v);
                }
            }
        }
        let set: Vec<(&str, String)> = next
            .iter()
            .filter(|(k, v)| self.stored.get(*k) != Some(v))
            .map(|(k, v)| (k.as_str(), v.to_string()))
            .collect();
        let set: Vec<(&str, &str)> = set.iter().map(|(k, v)| (*k, v.as_str())).collect();
        // Keys the current build does not know stay, so a downgrade does not lose them.
        let remove: Vec<&str> = self
            .stored
            .keys()
            .filter(|k| defaults.get(k).is_some() && !next.contains_key(*k))
            .map(String::as_str)
            .collect();
        self.db.write_config(&set, &remove).await?;

        for (k, v) in &self.stored {
            if defaults.get(k).is_none() {
                next.insert(k.clone(), v.clone());
            }
        }
        self.settings = effective(&next, &self.env, &self.default_cache_dir)?;
        if indexers_changed {
            self.indexers_revision = Uuid::new_v4();
        }
        self.stored = next;
        Ok(())
    }
}

fn effective(
    stored: &Map<String, Value>,
    env: &[Override],
    default_cache_dir: &str,
) -> Result<Settings, Error> {
    let mut tree = serde_json::to_value(Settings::default())?;
    merge(&mut tree, Value::Object(stored.clone()));
    for o in env {
        set(&mut tree, &o.path, o.value.clone());
    }
    reject_readarr(&tree)?;
    let mut settings: Settings = serde_json::from_value(tree)?;
    settings.reset_invalid();
    settings.validate_in(default_cache_dir)?;
    Ok(settings)
}

/// Readarr support was dropped; an instance left from before stops startup with what
/// to do, not a parse error.
fn reject_readarr(tree: &Value) -> Result<(), Error> {
    let Some(arr) = tree["arr_instances"]
        .as_array()
        .and_then(|arrs| arrs.iter().find(|a| a["profile"] == "readarr"))
    else {
        return Ok(());
    };
    Err(invalid(
        "arr_instances",
        format!(
            "Arr instance \"{}\" is a Readarr, which infinidysk no longer supports. Remove \
             it first: in the previous version under Settings > Automation > Arr \
             instances, or from INFINIDYSK__ARR_INSTANCES. Then upgrade and add Chaptarr.",
            arr["name"].as_str().unwrap_or_default()
        ),
    ))
}

fn lookup<'a>(tree: &'a Value, path: &[String]) -> Option<&'a Value> {
    path.iter().try_fold(tree, |node, key| node.get(key))
}

fn set(tree: &mut Value, path: &[String], value: Value) {
    let mut node = tree;
    for key in path {
        if !node.is_object() {
            *node = Value::Object(Map::new());
        }
        let Value::Object(map) = node else { return };
        node = map.entry(key.clone()).or_insert(Value::Null);
    }
    *node = value;
}

fn remove(tree: &mut Value, path: &[String]) {
    if let Some((last, parents)) = path.split_last()
        && let Some(Value::Object(map)) =
            parents.iter().try_fold(tree, |node, key| node.get_mut(key))
    {
        map.remove(last);
    }
}

/// Objects merge key by key; anything else replaces.
fn merge(base: &mut Value, over: Value) {
    match (base, over) {
        (Value::Object(base), Value::Object(over)) => {
            for (key, value) in over {
                match base.get_mut(&key) {
                    Some(slot) => merge(slot, value),
                    None => {
                        base.insert(key, value);
                    }
                }
            }
        }
        (slot, value) => *slot = value,
    }
}

/// The parts of `value` that differ from `default`; lists compare whole.
fn diff(value: Value, default: Option<&Value>) -> Option<Value> {
    match (value, default) {
        (Value::Object(map), Some(Value::Object(defaults))) => {
            let out: Map<String, Value> = map
                .into_iter()
                .filter_map(|(k, v)| {
                    let d = defaults.get(&k);
                    diff(v, d).map(|v| (k, v))
                })
                .collect();
            (!out.is_empty()).then_some(Value::Object(out))
        }
        (value, Some(default)) if &value == default => None,
        (value, _) => Some(value),
    }
}
