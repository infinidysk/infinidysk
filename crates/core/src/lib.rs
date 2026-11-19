//! Pure data shared by every layer: ids, byte ranges, per-file streaming metadata and the
//! blob format that stores it.

pub mod blob;
pub mod crypt;
pub mod media;
mod meta;
pub mod repair;
pub mod trace;
pub mod wait;

use std::fmt;
use std::str::FromStr;

use serde::{Deserialize, Serialize};
use uuid::Uuid;

pub use meta::{FileMeta, FilePart, MultipartFile, NzbFile, Par2Proof, Segment, Segments};

// ponytail: temporary A/B switches so one image runs every benchmark arm; each is
// deleted once its winner becomes the default.
static EXPERIMENTS: std::sync::OnceLock<Vec<String>> = std::sync::OnceLock::new();

/// Experiments switched on by `INFINIDYSK_EXPERIMENTS` (comma list).
#[must_use]
pub fn env_experiments() -> Vec<String> {
    std::env::var("INFINIDYSK_EXPERIMENTS")
        .unwrap_or_default()
        .split(',')
        .map(|s| s.trim().to_owned())
        .filter(|s| !s.is_empty())
        .collect()
}

/// Switches on `enabled` beside [`env_experiments`] for the life of the process. Only
/// the first call, before any [`experiment`] check, has an effect.
pub fn enable_experiments(enabled: &[String]) {
    EXPERIMENTS.get_or_init(|| {
        let mut on = env_experiments();
        on.extend(
            enabled
                .iter()
                .filter(|e| !on.contains(e))
                .cloned()
                .collect::<Vec<_>>(),
        );
        on
    });
}

/// Experiments on in this process.
pub fn experiments() -> &'static [String] {
    EXPERIMENTS.get_or_init(env_experiments)
}

#[must_use]
pub fn experiment(name: &str) -> bool {
    experiments().iter().any(|on| on == name)
}

macro_rules! id {
    ($(#[$doc:meta])* $name:ident) => {
        $(#[$doc])*
        #[derive(Clone, Copy, Debug, PartialEq, Eq, Hash, PartialOrd, Ord, Serialize, Deserialize)]
        #[serde(transparent)]
        pub struct $name(pub Uuid);

        impl $name {
            /// Time-ordered (UUID v7), so new rows land at the end of the primary key index.
            #[must_use]
            pub fn new() -> Self {
                Self(Uuid::now_v7())
            }
        }

        impl Default for $name {
            fn default() -> Self {
                Self::new()
            }
        }

        impl fmt::Display for $name {
            fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
                self.0.hyphenated().fmt(f)
            }
        }

        impl FromStr for $name {
            type Err = uuid::Error;

            fn from_str(s: &str) -> Result<Self, Self::Err> {
                Uuid::try_parse(s).map(Self)
            }
        }
    };
}

id!(
    /// A file or directory in the virtual tree; also its `/.ids/<id>` name.
    ItemId
);
id!(
    /// A stored metadata or NZB blob.
    BlobId
);
id!(
    /// A queued or finished download; the same id moves from queue to history.
    JobId
);
id!(
    /// One immutable repaired article, distinct from metadata/NZB blobs.
    PatchId
);

impl ItemId {
    /// `/`, seeded by the first migration.
    pub const ROOT: Self = Self(Uuid::from_u128(0));
    /// `/content`, seeded by the first migration.
    pub const CONTENT: Self = Self(Uuid::from_u128(1));

    /// `.ids/a/b/c/d/e/<id>`: the file by id below the tree's root, sharded by the id's
    /// first five hex digits.
    #[must_use]
    pub fn ids_path(self) -> String {
        let id = self.to_string();
        let mut path = ".ids/".to_owned();
        for c in id.chars().take(5) {
            path.push(c);
            path.push('/');
        }
        path.push_str(&id);
        path
    }

    /// The id of an [`ItemId::ids_path`], spelled exactly as that writes it.
    #[must_use]
    pub fn from_ids_path(path: &str) -> Option<Self> {
        let item: Self = path.rsplit_once('/')?.1.parse().ok()?;
        (item.ids_path() == path).then_some(item)
    }
}

/// What an item is. Stored as `SMALLINT`; values must never change.
#[derive(Clone, Copy, Debug, PartialEq, Eq, Hash)]
#[repr(i16)]
pub enum ItemKind {
    Directory = 1,
    /// One file made of the segments of one NZB file entry.
    NzbFile = 2,
    /// One file stitched from parts of several NZB file entries (archive members, split files).
    Multipart = 3,
}

impl TryFrom<i16> for ItemKind {
    type Error = i16;

    fn try_from(v: i16) -> Result<Self, i16> {
        match v {
            1 => Ok(Self::Directory),
            2 => Ok(Self::NzbFile),
            3 => Ok(Self::Multipart),
            other => Err(other),
        }
    }
}

/// Half-open byte range `start..end`.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq, Hash, Serialize, Deserialize)]
pub struct ByteRange {
    pub start: u64,
    pub end: u64,
}

impl ByteRange {
    #[must_use]
    pub const fn len(self) -> u64 {
        self.end.saturating_sub(self.start)
    }

    #[must_use]
    pub const fn is_empty(self) -> bool {
        self.end <= self.start
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn ids_paths_round_trip_only_in_their_own_spelling() {
        let id = ItemId(Uuid::from_u128(0xabcd_ef01_2345_6789_abcd_ef01_2345_6789));
        let path = id.ids_path();
        assert_eq!(ItemId::from_ids_path(&path), Some(id));
        assert_eq!(ItemId::from_ids_path(&path.to_uppercase()), None);
        assert_eq!(ItemId::from_ids_path(&format!(".ids/0/0/0/0/0/{id}")), None);
        assert_eq!(ItemId::from_ids_path(&format!(".ids/{id}")), None);
    }
}
