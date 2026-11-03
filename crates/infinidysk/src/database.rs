//! Which database the app runs on, and moving it from SQLite to PostgreSQL.
//!
//! `/config/database.toml` (the boot file) selects PostgreSQL after a move. The
//! `INFINIDYSK_DATABASE_URL` environment variable, when set, overrides it.

use std::fs::{self, File, OpenOptions};
use std::io::{self, Write as _};
use std::os::unix::fs::{MetadataExt as _, OpenOptionsExt as _, PermissionsExt as _};
use std::path::Path;

use anyhow::{Context as _, bail};
use serde::{Deserialize, Serialize};
use utoipa::ToSchema;

pub const CONFIG_DIR: &str = "/config";
pub const BOOT_FILE: &str = "database.toml";
pub const DEFAULT_SQLITE: &str = "infinidysk.sqlite";
pub const PRE_POSTGRES: &str = "infinidysk.sqlite.pre-postgres";

const HEADER: &str =
    "# Written by infinidysk. INFINIDYSK_DATABASE_URL, when set, overrides this file.\n";

/// `/config/database.toml`. Holds a password: no `Debug`, never logged.
#[derive(Clone, Default, Serialize, Deserialize)]
pub struct BootFile {
    pub url: Option<String>,
    pub migration: Option<Migration>,
}

#[derive(Clone, Serialize, Deserialize)]
pub struct Migration {
    pub target: String,
    pub state: MigrationState,
    /// SQLite's migration version when the copy started; a different one forces a re-copy.
    pub schema_version: i64,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq, Serialize, Deserialize, ToSchema)]
#[serde(rename_all = "lowercase")]
pub enum MigrationState {
    Copying,
    Syncing,
    Cutover,
}

impl BootFile {
    /// A missing file is the default; an unreadable one is an error.
    ///
    /// # Errors
    ///
    /// The file exists but cannot be read or parsed.
    pub fn load(dir: &Path) -> anyhow::Result<Self> {
        let path = dir.join(BOOT_FILE);
        match fs::read_to_string(&path) {
            // The parse error's message only; its full display quotes the line, which may
            // hold the password.
            Ok(text) => toml::from_str(&text).map_err(|error| {
                anyhow::anyhow!("{} is invalid: {}", path.display(), error.message())
            }),
            Err(error) if error.kind() == io::ErrorKind::NotFound => Ok(Self::default()),
            Err(error) => Err(error).with_context(|| format!("reading {}", path.display())),
        }
    }

    /// Writes `database.toml.tmp` (mode 0o600), fsyncs it, renames it over `database.toml`,
    /// then fsyncs `dir`. `Ok`: the new file is visible and survives a host crash.
    ///
    /// # Errors
    ///
    /// [`SaveError`] says whether the new file became visible.
    pub fn save(&self, dir: &Path, owner: (u32, u32)) -> Result<(), SaveError> {
        let path = dir.join(BOOT_FILE);
        let tmp = dir.join(format!("{BOOT_FILE}.tmp"));
        let publish = || -> io::Result<()> {
            let text = toml::to_string(self).map_err(io::Error::other)?;
            let mut file = OpenOptions::new()
                .write(true)
                .create(true)
                .truncate(true)
                .mode(0o600)
                .open(&tmp)?;
            file.set_permissions(fs::Permissions::from_mode(0o600))?;
            file.write_all(HEADER.as_bytes())?;
            file.write_all(text.as_bytes())?;
            file.sync_all()?;
            fs::rename(&tmp, &path)
        };
        publish().map_err(SaveError::Unpublished)?;
        own(&path, owner);
        sync_dir(dir).map_err(SaveError::NotDurable)
    }
}

/// Why [`BootFile::save`] failed.
#[derive(Debug, thiserror::Error)]
pub enum SaveError {
    /// The temp write, its fsync or the rename failed: `database.toml` is unchanged.
    #[error("could not write /config/{BOOT_FILE}: {0}")]
    Unpublished(io::Error),
    /// Renamed, but the directory fsync failed: visible now, possibly lost on a host crash.
    #[error("could not make /config/{BOOT_FILE} durable: {0}")]
    NotDurable(io::Error),
}

/// Hands a file root created to the configured owner, as log files are.
fn own(path: &Path, (uid, gid): (u32, u32)) {
    let Ok(meta) = fs::symlink_metadata(path) else {
        return;
    };
    if meta.uid() != 0 || (uid == 0 && gid == meta.gid()) {
        return;
    }
    if let Err(error) = std::os::unix::fs::lchown(path, Some(uid), Some(gid)) {
        tracing::warn!(reason = %error, path = %path.display(), "could not change the boot file's owner");
    }
}

#[cfg(test)]
thread_local! {
    static FAIL_DIR_SYNC: std::cell::Cell<bool> = const { std::cell::Cell::new(false) };
}

/// Makes the next `sync_dir` on this thread fail (tests use the current-thread runtime).
#[cfg(test)]
fn fail_next_dir_sync() {
    FAIL_DIR_SYNC.set(true);
}

fn sync_dir(dir: &Path) -> io::Result<()> {
    #[cfg(test)]
    if FAIL_DIR_SYNC.replace(false) {
        return Err(io::Error::other("injected"));
    }
    File::open(dir)?.sync_all()
}

/// Where the database URL came from.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum Source {
    /// `INFINIDYSK_DATABASE_URL`; the app does not manage the database.
    Env,
    BootFile,
    Default,
}

/// `INFINIDYSK_DATABASE_URL`, else the boot file's URL, else the default SQLite file.
/// Refuses to start empty when a switch left only the renamed SQLite file behind.
///
/// # Errors
///
/// Only the renamed SQLite file is left.
pub fn choose(
    env_url: Option<String>,
    file: &BootFile,
    config: &Path,
) -> anyhow::Result<(String, Source)> {
    if let Some(url) = env_url {
        return Ok((url, Source::Env));
    }
    if let Some(url) = &file.url {
        return Ok((url.clone(), Source::BootFile));
    }
    let sqlite = config.join(DEFAULT_SQLITE);
    if !sqlite.exists() && config.join(PRE_POSTGRES).exists() {
        bail!(
            "This install moved to PostgreSQL, but /config/{BOOT_FILE} is missing. Restore it, \
             or rename {PRE_POSTGRES} to {DEFAULT_SQLITE} to go back to SQLite."
        );
    }
    Ok((format!("sqlite:{}", sqlite.display()), Source::Default))
}

/// Loads the boot file, makes it durable, then picks the database with [`choose`].
/// Runs before any database opens; a failed sync stops startup.
///
/// # Errors
///
/// The boot file is unreadable or cannot be made durable, or [`choose`] fails.
pub fn select(env_url: Option<String>, dir: &Path) -> anyhow::Result<(BootFile, String, Source)> {
    let file = BootFile::load(dir)?;
    let path = dir.join(BOOT_FILE);
    if path.exists() {
        File::open(&path)
            .and_then(|f| f.sync_all())
            .and_then(|()| sync_dir(dir))
            .with_context(|| {
                format!(
                    "could not make /config/{BOOT_FILE} durable; fix the /config volume and restart"
                )
            })?;
    }
    let (url, source) = choose(env_url, &file, dir)?;
    Ok((file, url, source))
}

#[cfg(test)]
mod tests {
    use std::path::PathBuf;

    use super::*;

    struct Dir(PathBuf);

    impl Dir {
        fn new() -> Self {
            let dir = std::env::temp_dir().join(format!("infinidysk-db-{}", uuid::Uuid::new_v4()));
            fs::create_dir_all(&dir).expect("temp dir");
            Self(dir)
        }
    }

    impl Drop for Dir {
        fn drop(&mut self) {
            let _ = fs::remove_dir_all(&self.0);
        }
    }

    fn owner() -> (u32, u32) {
        let meta = fs::metadata(std::env::temp_dir()).expect("temp dir");
        (meta.uid(), meta.gid())
    }

    fn cutover() -> BootFile {
        BootFile {
            url: None,
            migration: Some(Migration {
                target: "postgres://u:p@127.0.0.1:1/x".to_owned(),
                state: MigrationState::Cutover,
                schema_version: 7,
            }),
        }
    }

    fn switched() -> BootFile {
        BootFile {
            url: Some("postgres://u:p@127.0.0.1:1/x".to_owned()),
            migration: None,
        }
    }

    #[test]
    fn boot_file_round_trips_private() {
        let dir = Dir::new();
        cutover().save(&dir.0, owner()).expect("save");
        let loaded = BootFile::load(&dir.0).expect("load");
        let migration = loaded.migration.expect("migration");
        assert_eq!(migration.target, "postgres://u:p@127.0.0.1:1/x");
        assert_eq!(migration.state, MigrationState::Cutover);
        assert_eq!(migration.schema_version, 7);
        assert_eq!(loaded.url, None);
        let mode = fs::metadata(dir.0.join(BOOT_FILE)).expect("meta").mode();
        assert_eq!(mode & 0o777, 0o600);
        let text = fs::read_to_string(dir.0.join(BOOT_FILE)).expect("read");
        assert!(text.starts_with(HEADER));
    }

    #[test]
    fn missing_boot_file_is_default() {
        let dir = Dir::new();
        let file = BootFile::load(&dir.0).expect("load");
        assert!(file.url.is_none() && file.migration.is_none());
    }

    #[test]
    fn save_fails_before_rename_leaves_file() {
        let dir = Dir::new();
        cutover().save(&dir.0, owner()).expect("save");
        fs::create_dir(dir.0.join(format!("{BOOT_FILE}.tmp"))).expect("block tmp");
        assert!(matches!(
            switched().save(&dir.0, owner()),
            Err(SaveError::Unpublished(_))
        ));
        let loaded = BootFile::load(&dir.0).expect("load");
        assert_eq!(
            loaded.migration.map(|m| m.state),
            Some(MigrationState::Cutover)
        );
    }

    #[test]
    fn save_without_dir_sync_is_not_durable() {
        let dir = Dir::new();
        cutover().save(&dir.0, owner()).expect("save");
        fail_next_dir_sync();
        assert!(matches!(
            switched().save(&dir.0, owner()),
            Err(SaveError::NotDurable(_))
        ));
        let loaded = BootFile::load(&dir.0).expect("load");
        assert!(loaded.migration.is_none());
        assert_eq!(loaded.url.as_deref(), Some("postgres://u:p@127.0.0.1:1/x"));
    }

    #[test]
    fn env_url_wins_over_boot_file() {
        let dir = Dir::new();
        let (url, source) =
            choose(Some("sqlite:/x.sqlite".to_owned()), &switched(), &dir.0).expect("choose");
        assert_eq!(url, "sqlite:/x.sqlite");
        assert_eq!(source, Source::Env);
        let (url, source) = choose(None, &switched(), &dir.0).expect("choose");
        assert_eq!(url, "postgres://u:p@127.0.0.1:1/x");
        assert_eq!(source, Source::BootFile);
        let (url, source) = choose(None, &BootFile::default(), &dir.0).expect("choose");
        assert_eq!(
            url,
            format!("sqlite:{}", dir.0.join(DEFAULT_SQLITE).display())
        );
        assert_eq!(source, Source::Default);
    }

    #[test]
    fn missing_boot_file_after_switch_refuses_to_start() {
        let dir = Dir::new();
        fs::write(dir.0.join(PRE_POSTGRES), b"").expect("write");
        let error = choose(None, &BootFile::default(), &dir.0).expect_err("refuses");
        assert!(error.to_string().contains(BOOT_FILE));
    }

    #[test]
    fn select_stops_when_the_boot_file_cannot_be_made_durable() {
        let dir = Dir::new();
        switched().save(&dir.0, owner()).expect("save");
        fail_next_dir_sync();
        assert!(select(None, &dir.0).is_err());
        let (_, url, source) = select(None, &dir.0).expect("select");
        assert_eq!(url, "postgres://u:p@127.0.0.1:1/x");
        assert_eq!(source, Source::BootFile);
    }
}
