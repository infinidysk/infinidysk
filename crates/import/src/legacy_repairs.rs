//! C# repair files have no payload checksum. Preserve them as candidates, never as repairs.

use std::fmt::Write as _;
use std::fs::File;
use std::io::{self, Read as _};
use std::path::Path;

use infinidysk_db::Db;
use infinidysk_nntp::{Body, MAX_DECODED_LEN};
use nix::fcntl::{OFlag, openat};
use nix::sys::stat::Mode;
use serde::Deserialize;
use sha2::{Digest as _, Sha256};

#[derive(Deserialize)]
#[serde(rename_all = "PascalCase")]
struct Header {
    file_size: u64,
    #[serde(rename = "LineLength")]
    _line_length: u32,
    part_number: u32,
    total_parts: u32,
    part_size: u64,
    part_offset: u64,
}

fn invalid() -> io::Error {
    io::Error::new(
        io::ErrorKind::InvalidData,
        "C# repair header/body is inconsistent",
    )
}

fn body(header: &[u8], bytes: Vec<u8>) -> io::Result<Body> {
    let header: Header = serde_json::from_slice(header).map_err(|_| invalid())?;
    if bytes.is_empty()
        || bytes.len() > MAX_DECODED_LEN
        || header.part_size != bytes.len() as u64
        || header
            .part_offset
            .checked_add(header.part_size)
            .is_none_or(|end| end > header.file_size)
        || header.total_parts > 0 && header.part_number > header.total_parts
    {
        return Err(invalid());
    }
    Ok(Body {
        data: bytes.into(),
        offset: header.part_offset,
        file_size: header.file_size,
        part: (header.part_number > 0).then_some(header.part_number),
        total: (header.total_parts > 0).then_some(header.total_parts),
    })
}

fn pair(root: &File, hash: &str) -> io::Result<(Vec<u8>, Vec<u8>)> {
    let dir = File::from(openat(
        root,
        &hash[..2],
        OFlag::O_RDONLY | OFlag::O_DIRECTORY | OFlag::O_NOFOLLOW | OFlag::O_CLOEXEC,
        Mode::empty(),
    )?);
    let read = |name: &str, limit: u64| -> io::Result<Vec<u8>> {
        let file = File::from(openat(
            &dir,
            name,
            OFlag::O_RDONLY | OFlag::O_NOFOLLOW | OFlag::O_NONBLOCK | OFlag::O_CLOEXEC,
            Mode::empty(),
        )?);
        if !file.metadata()?.is_file() || file.metadata()?.len() > limit {
            return Err(invalid());
        }
        let mut data = Vec::new();
        file.take(limit + 1).read_to_end(&mut data)?;
        if data.len() as u64 > limit {
            return Err(invalid());
        }
        Ok(data)
    };
    Ok((
        read(&format!("{hash}.h"), 1_048_576)?,
        read(hash, MAX_DECODED_LEN as u64)?,
    ))
}

pub async fn needed(config: &Path, db: &Db) -> Result<bool, crate::Error> {
    Ok(config
        .join("repair-segments")
        .try_exists()
        .map_err(crate::Error::LegacyRepairs)?
        && !db
            .config()
            .await?
            .iter()
            .any(|(key, value)| key == "legacy.repair.staged" && value == "1"))
}

/// Stage once while the C# install is stopped. Copies are included in database backups.
pub async fn stage(config: &Path, db: &Db) -> Result<(), crate::Error> {
    if db
        .config()
        .await?
        .iter()
        .any(|(key, value)| key == "legacy.repair.staged" && value == "1")
    {
        return Ok(());
    }
    let path = config.join("repair-segments");
    if !path.try_exists().map_err(crate::Error::LegacyRepairs)? {
        return Ok(());
    }
    db.write_config(&[("legacy.repair.enabled", "1")], &[])
        .await?;
    let db = db.clone();
    let runtime = tokio::runtime::Handle::current();
    tokio::task::spawn_blocking(move || -> Result<(), crate::Error> {
        let root = File::from(nix::fcntl::open(&path, OFlag::O_RDONLY | OFlag::O_DIRECTORY | OFlag::O_CLOEXEC, Mode::empty())
            .map_err(|error| crate::Error::LegacyRepairs(error.into()))?);
        let mut copied = 0;
        for shard in std::fs::read_dir(&path).map_err(crate::Error::LegacyRepairs)? {
            let shard = shard.map_err(crate::Error::LegacyRepairs)?;
            if !shard.file_type().map_err(crate::Error::LegacyRepairs)?.is_dir() { continue; }
            for entry in std::fs::read_dir(shard.path()).map_err(crate::Error::LegacyRepairs)? {
                let entry = entry.map_err(crate::Error::LegacyRepairs)?;
                let name = entry.file_name();
                let Some(hash) = name.to_str().filter(|name| name.len() == 64 && name.bytes().all(|b| b.is_ascii_hexdigit() && !b.is_ascii_lowercase())) else { continue };
                let (header, data) = pair(&root, hash).map_err(crate::Error::LegacyRepairs)?;
                let decoded = body(&header, data).map_err(crate::Error::LegacyRepairs)?;
                runtime.block_on(db.stage_legacy_repair(hash, &header, &decoded.data, &Sha256::digest(&decoded.data)))?;
                copied += 1;
            }
        }
        runtime.block_on(db.write_config(&[("legacy.repair.staged", "1"), ("legacy.repair.staged_at_ms", &infinidysk_db::now_ms().to_string())], &[]))?;
        tracing::warn!(articles = copied, reason = "saved C# articles need complete PAR2 verification before normal reads can use them", "C# repair bytes preserved for migration");
        Ok(())
    }).await?
}

/// Importer-flagged worker reads only. C# JSON never becomes a runtime delivery format.
pub async fn read(db: &Db, id: &str) -> Result<Option<Body>, infinidysk_nntp::Error> {
    if id.is_empty() || id.len() > 8192 {
        return Ok(None);
    }
    let hash =
        Sha256::digest(id.as_bytes())
            .iter()
            .fold(String::with_capacity(64), |mut hash, byte| {
                write!(hash, "{byte:02X}").expect("writing to a String cannot fail");
                hash
            });
    let Some((header, data, checksum)) = db
        .legacy_repair(&hash)
        .await
        .map_err(|_| infinidysk_nntp::Error::Repair("legacy repair catalog cannot be read"))?
    else {
        return Ok(None);
    };
    let result = tokio::task::spawn_blocking(move || {
        if checksum.as_slice() != Sha256::digest(&data).as_slice() {
            return Err(invalid());
        }
        body(&header, data)
    })
    .await
    .map_err(|_| infinidysk_nntp::Error::Repair("legacy repair read stopped"))?
    .map_err(|_| infinidysk_nntp::Error::Repair("legacy repair candidate is corrupt"))?;
    db.legacy_repair_used(&hash, id, infinidysk_db::now_ms())
        .await
        .map_err(|_| infinidysk_nntp::Error::Repair("legacy repair usage could not be recorded"))?;
    Ok(Some(result))
}

/// Retirement requires a full explicit rewrite pass, zero retained candidates and seven
/// days with no old-format use. Original C# files are never deleted by this migration.
pub async fn finish_rewrite(db: &Db, full_pass: bool) -> Result<(), infinidysk_db::Error> {
    if !full_pass || db.retire_legacy_repairs().await? != 0 {
        return Ok(());
    }
    let config = db.config().await?;
    let last = config
        .iter()
        .find(|(key, _)| key == "legacy.repair.last_used_ms")
        .or_else(|| {
            config
                .iter()
                .find(|(key, _)| key == "legacy.repair.staged_at_ms")
        })
        .and_then(|(_, value)| value.parse::<i64>().ok());
    if last.is_some_and(|last| infinidysk_db::now_ms().saturating_sub(last) >= 7 * 86_400_000) {
        db.write_config(
            &[(
                "legacy.repair.retired_at_ms",
                &infinidysk_db::now_ms().to_string(),
            )],
            &["legacy.repair.enabled"],
        )
        .await?;
    }
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;

    fn header() -> Vec<u8> {
        serde_json::to_vec(&serde_json::json!({ "FileSize": 100, "LineLength": 128,
            "PartNumber": 2, "TotalParts": 10, "PartSize": 4, "PartOffset": 10 }))
        .unwrap()
    }

    #[tokio::test]
    async fn staged_legacy_bytes_survive_removal_of_the_csharp_directory_without_becoming_repairs()
    {
        let root =
            std::env::temp_dir().join(format!("infinidysk-legacy-repair-{}", uuid::Uuid::new_v4()));
        let id = "legacy@loopback";
        let hash = Sha256::digest(id.as_bytes())
            .iter()
            .fold(String::new(), |mut hash, byte| {
                write!(hash, "{byte:02X}").unwrap();
                hash
            });
        let path = root.join("repair-segments").join(&hash[..2]);
        std::fs::create_dir_all(&path).unwrap();
        std::fs::write(path.join(&hash), b"data").unwrap();
        std::fs::write(path.join(format!("{hash}.h")), header()).unwrap();
        let db = Db::connect("sqlite::memory:").await.unwrap();
        stage(&root, &db).await.unwrap();
        std::fs::remove_dir_all(&root).unwrap();
        assert_eq!(read(&db, id).await.unwrap().unwrap().data.as_ref(), b"data");
        assert_eq!(db.article_repairs(id).await.unwrap(), []);
        assert!(
            db.config()
                .await
                .unwrap()
                .iter()
                .any(|(key, value)| key == "legacy.repair.hits" && value == "1")
        );
        finish_rewrite(&db, true).await.unwrap();
        assert!(
            db.config()
                .await
                .unwrap()
                .iter()
                .any(|(key, _)| key == "legacy.repair.enabled")
        );
    }

    #[test]
    fn invalid_or_incomplete_legacy_headers_never_yield_a_candidate() {
        assert!(body(&header(), b"short".to_vec()).is_err());
        let mut fields: serde_json::Value = serde_json::from_slice(&header()).unwrap();
        fields["PartOffset"] = serde_json::json!(99);
        assert!(body(&serde_json::to_vec(&fields).unwrap(), b"data".to_vec()).is_err());
        fields["PartOffset"] = serde_json::json!(-1);
        assert!(body(&serde_json::to_vec(&fields).unwrap(), b"data".to_vec()).is_err());
    }

    #[tokio::test]
    async fn retirement_needs_a_full_pass_and_seven_quiet_days() {
        let db = Db::connect("sqlite::memory:").await.unwrap();
        db.write_config(
            &[
                ("legacy.repair.enabled", "1"),
                ("legacy.repair.staged_at_ms", "0"),
            ],
            &[],
        )
        .await
        .unwrap();
        finish_rewrite(&db, false).await.unwrap();
        assert!(
            db.config()
                .await
                .unwrap()
                .iter()
                .any(|(k, _)| k == "legacy.repair.enabled")
        );
        db.write_config(
            &[(
                "legacy.repair.last_used_ms",
                &infinidysk_db::now_ms().to_string(),
            )],
            &[],
        )
        .await
        .unwrap();
        finish_rewrite(&db, true).await.unwrap();
        assert!(
            db.config()
                .await
                .unwrap()
                .iter()
                .any(|(k, _)| k == "legacy.repair.enabled")
        );
        db.write_config(&[("legacy.repair.last_used_ms", "0")], &[])
            .await
            .unwrap();
        finish_rewrite(&db, true).await.unwrap();
        assert!(
            !db.config()
                .await
                .unwrap()
                .iter()
                .any(|(k, _)| k == "legacy.repair.enabled")
        );
    }
}
