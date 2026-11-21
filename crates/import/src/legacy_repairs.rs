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

fn parse_header(header: &[u8], length: usize) -> io::Result<Header> {
    if header.len() > 1_048_576 {
        return Err(invalid());
    }
    let header: Header = serde_json::from_slice(header).map_err(|_| invalid())?;
    if length == 0
        || length > MAX_DECODED_LEN
        || header.part_size != length as u64
        || header
            .part_offset
            .checked_add(header.part_size)
            .is_none_or(|end| end > header.file_size)
        || header.total_parts > 0 && header.part_number > header.total_parts
    {
        return Err(invalid());
    }
    Ok(header)
}

fn body(header: &[u8], bytes: Vec<u8>) -> io::Result<Body> {
    let header = parse_header(header, bytes.len())?;
    Ok(Body {
        data: bytes.into(),
        offset: header.part_offset,
        file_size: header.file_size,
        part: (header.part_number > 0).then_some(header.part_number),
        total: (header.total_parts > 0).then_some(header.total_parts),
    })
}

fn checked_body(header: &[u8], data: Vec<u8>, checksum: &[u8]) -> io::Result<Body> {
    let candidate = body(header, data)?;
    if checksum != Sha256::digest(&candidate.data).as_slice() {
        return Err(invalid());
    }
    Ok(candidate)
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
        let mut scan_error = None;
        let mut record_error = |error: io::Error| {
            tracing::warn!(reason = %error, "cannot enumerate all C# repair files; staging will need a retry");
            scan_error.get_or_insert(error);
        };
        for shard in std::fs::read_dir(&path).map_err(crate::Error::LegacyRepairs)? {
            let shard = match shard.and_then(|entry| Ok((entry.file_type()?, entry))) {
                Ok((kind, entry)) if kind.is_dir() => entry,
                Ok(_) => continue,
                Err(error) => { record_error(error); continue; }
            };
            let mut candidates = std::collections::BTreeSet::new();
            let entries = match std::fs::read_dir(shard.path()) {
                Ok(entries) => entries,
                Err(error) => { record_error(io::Error::new(error.kind(), format!("{}: {error}", shard.path().display()))); continue; }
            };
            for entry in entries {
                let entry = match entry { Ok(entry) => entry, Err(error) => { record_error(error); continue; } };
                let name = entry.file_name();
                let Some(hash) = name.to_str().map(|name| name.strip_suffix(".h").unwrap_or(name)).filter(|name| name.len() == 64 && name.bytes().all(|b| b.is_ascii_hexdigit() && !b.is_ascii_lowercase())) else { continue };
                candidates.insert(hash.to_owned());
            }
            for hash in candidates {
                let hash = hash.as_str();
                let candidate = pair(&root, hash).and_then(|(header, data)| body(&header, data).map(|decoded| (header, decoded)));
                let (header, decoded) = match candidate {
                    Ok(candidate) => candidate,
                    Err(error) if matches!(error.kind(), std::io::ErrorKind::NotFound | std::io::ErrorKind::InvalidData | std::io::ErrorKind::PermissionDenied | std::io::ErrorKind::NotADirectory | std::io::ErrorKind::IsADirectory) || error.raw_os_error() == Some(nix::libc::ELOOP) => {
                        let reason = "legacy payload is missing, inaccessible, or inconsistent; original files retained";
                        let preserved = runtime.block_on(db.legacy_repair(hash))?
                            .is_some_and(|(header, data, checksum)| checked_body(&header, data, &checksum).is_ok());
                        runtime.block_on(db.mark_legacy_repair_unusable(hash, reason, preserved))?;
                        copied += i32::from(preserved);
                        tracing::warn!(hash, preserved, reason, "C# repair source is unusable");
                        continue;
                    }
                    Err(error) => return Err(crate::Error::LegacyRepairs(error)),
                };
                runtime.block_on(db.stage_legacy_repair(hash, &header, &decoded.data, &Sha256::digest(&decoded.data), |header, length| parse_header(header, length).is_ok()))?;
                copied += 1;
            }
        }
        if let Some(error) = scan_error { return Err(crate::Error::LegacyRepairs(error)); }
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
    let result = tokio::task::spawn_blocking(move || checked_body(&header, data, &checksum))
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
    if !full_pass
        || db
            .retire_legacy_repairs(|header, length| {
                let header = parse_header(header, length).ok()?;
                Some(infinidysk_core::ByteRange {
                    start: header.part_offset,
                    end: header.part_offset.checked_add(header.part_size)?,
                })
            })
            .await?
            != 0
    {
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

    #[derive(Clone, Copy)]
    enum SavedCopy {
        InvalidHeader,
        InvalidChecksum,
        Valid,
    }

    #[tokio::test]
    async fn restaging_replaces_a_saved_candidate_with_an_invalid_header() {
        restage_candidate(SavedCopy::InvalidHeader).await;
    }

    #[tokio::test]
    async fn restaging_replaces_a_saved_candidate_with_an_invalid_checksum() {
        restage_candidate(SavedCopy::InvalidChecksum).await;
    }

    #[tokio::test]
    async fn restaging_preserves_a_valid_saved_candidate() {
        restage_candidate(SavedCopy::Valid).await;
    }

    async fn restage_candidate(saved: SavedCopy) {
        restage_candidate_in(saved, None).await;
        if let Ok(url) = std::env::var("INFINIDYSK_TEST_POSTGRES_URL") {
            let admin = sqlx::PgPool::connect(&url).await.unwrap();
            let name = format!("infinidysk_restage_{}", uuid::Uuid::new_v4().simple());
            sqlx::query(sqlx::AssertSqlSafe(format!("CREATE DATABASE {name}")))
                .execute(&admin)
                .await
                .unwrap();
            let (base, _) = url.rsplit_once('/').unwrap();
            restage_candidate_in(saved, Some(&format!("{base}/{name}"))).await;
            sqlx::query(sqlx::AssertSqlSafe(format!(
                "DROP DATABASE {name} WITH (FORCE)"
            )))
            .execute(&admin)
            .await
            .unwrap();
            admin.close().await;
        }
    }

    async fn restage_candidate_in(saved: SavedCopy, postgres_url: Option<&str>) {
        let root = std::env::temp_dir().join(format!("legacy-restage-{}", uuid::Uuid::new_v4()));
        let id = "restaged@loopback";
        let hash = Sha256::digest(id.as_bytes())
            .iter()
            .fold(String::new(), |mut hash, byte| {
                write!(hash, "{byte:02X}").unwrap();
                hash
            });
        let shard = root.join("repair-segments").join(&hash[..2]);
        std::fs::create_dir_all(&shard).unwrap();
        std::fs::write(shard.join(&hash), b"data").unwrap();
        std::fs::write(shard.join(format!("{hash}.h")), header()).unwrap();
        let url = postgres_url.map_or_else(
            || format!("sqlite:{}", root.join("candidates.sqlite").display()),
            str::to_owned,
        );
        let db = Db::connect(&url).await.unwrap();
        let saved_bytes = if matches!(saved, SavedCopy::Valid) {
            &b"kept"[..]
        } else {
            &b"data"[..]
        };
        db.stage_legacy_repair(
            &hash,
            &header(),
            saved_bytes,
            &Sha256::digest(saved_bytes),
            |header, length| parse_header(header, length).is_ok(),
        )
        .await
        .unwrap();
        let corruption = match saved {
            SavedCopy::InvalidHeader => Some((
                "UPDATE legacy_repair_articles SET header = $2 WHERE hash = $1",
                b"{}".to_vec(),
            )),
            SavedCopy::InvalidChecksum => Some((
                "UPDATE legacy_repair_articles SET sha256 = $2 WHERE hash = $1",
                vec![0u8; 32],
            )),
            SavedCopy::Valid => None,
        };
        if let Some((query, corrupt_bytes)) = corruption {
            if postgres_url.is_some() {
                let pool = sqlx::PgPool::connect(&url).await.unwrap();
                sqlx::query(query)
                    .bind(&hash)
                    .bind(corrupt_bytes)
                    .execute(&pool)
                    .await
                    .unwrap();
                pool.close().await;
            } else {
                let pool = sqlx::SqlitePool::connect(&url).await.unwrap();
                sqlx::query(query)
                    .bind(&hash)
                    .bind(corrupt_bytes)
                    .execute(&pool)
                    .await
                    .unwrap();
                pool.close().await;
            }
        }
        let before = db.legacy_repair(&hash).await.unwrap().unwrap();
        stage(&root, &db).await.unwrap();
        db.close().await;
        let db = Db::connect(&url).await.unwrap();
        assert_eq!(
            read(&db, id).await.unwrap().unwrap().data.as_ref(),
            saved_bytes
        );
        if matches!(saved, SavedCopy::Valid) {
            assert_eq!(db.legacy_repair(&hash).await.unwrap().unwrap(), before);
        }
        assert_eq!(db.article_repairs(id).await.unwrap(), []);
        assert_eq!(std::fs::read(shard.join(&hash)).unwrap(), b"data");
        db.close().await;
        std::fs::remove_dir_all(root).unwrap();
    }

    fn header() -> Vec<u8> {
        serde_json::to_vec(&serde_json::json!({ "FileSize": 100, "LineLength": 128,
            "PartNumber": 2, "TotalParts": 10, "PartSize": 4, "PartOffset": 10 }))
        .unwrap()
    }

    #[tokio::test]
    async fn unusable_candidates_do_not_block_valid_staging_or_become_repairs() {
        let root = std::env::temp_dir().join(format!("legacy-mixed-{}", uuid::Uuid::new_v4()));
        for (hash, payload, with_header) in [
            ("A".repeat(64), &b"data"[..], true),
            ("B".repeat(64), &b"data"[..], false),
            ("C".repeat(64), &b"short"[..], true),
        ] {
            let shard = root.join("repair-segments").join(&hash[..2]);
            std::fs::create_dir_all(&shard).unwrap();
            std::fs::write(shard.join(&hash), payload).unwrap();
            if with_header {
                std::fs::write(shard.join(format!("{hash}.h")), header()).unwrap();
            }
        }
        let missing = "D".repeat(64);
        let shard = root.join("repair-segments").join(&missing[..2]);
        std::fs::create_dir_all(&shard).unwrap();
        std::fs::write(shard.join(format!("{missing}.h")), header()).unwrap();
        let url = format!("sqlite:{}", root.join("candidates.sqlite").display());
        let db = Db::connect(&url).await.unwrap();
        stage(&root, &db).await.unwrap();
        db.close().await;
        let db = Db::connect(&url).await.unwrap();
        assert!(db.legacy_repair(&"A".repeat(64)).await.unwrap().is_some());
        for hash in ["B".repeat(64), "C".repeat(64), missing.clone()] {
            assert!(db.legacy_repair(&hash).await.unwrap().is_none());
            assert!(
                db.config()
                    .await
                    .unwrap()
                    .iter()
                    .any(|(key, _)| key == &format!("legacy.repair.unusable.{hash}"))
            );
            let shard = root.join("repair-segments").join(&hash[..2]);
            if hash == missing {
                assert_eq!(
                    std::fs::read(shard.join(format!("{hash}.h"))).unwrap(),
                    header()
                );
            } else {
                let expected = if hash.starts_with('B') {
                    &b"data"[..]
                } else {
                    &b"short"[..]
                };
                assert_eq!(std::fs::read(shard.join(&hash)).unwrap(), expected);
            }
        }
        stage(&root, &db).await.unwrap();
        db.close().await;
        std::fs::remove_dir_all(root).unwrap();
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

    #[tokio::test]
    async fn retry_preserves_checked_staged_bytes_when_original_files_are_unusable() {
        let root = std::env::temp_dir().join(format!("legacy-retry-{}", uuid::Uuid::new_v4()));
        let url = format!("sqlite:{}", root.join("candidates.sqlite").display());
        for hash in ["A".repeat(64), "B".repeat(64)] {
            let shard = root.join("repair-segments").join(&hash[..2]);
            std::fs::create_dir_all(&shard).unwrap();
            // Only a header remains; the source payload became unavailable after staging.
            std::fs::write(shard.join(format!("{hash}.h")), header()).unwrap();
        }
        let db = Db::connect(&url).await.unwrap();
        for (hash, saved_header) in [("A".repeat(64), header()), ("B".repeat(64), b"{}".to_vec())] {
            db.stage_legacy_repair(
                &hash,
                &saved_header,
                b"data",
                &Sha256::digest(b"data"),
                |_, _| true,
            )
            .await
            .unwrap();
        }
        let saved = db.legacy_repair(&"A".repeat(64)).await.unwrap();
        db.close().await;
        let db = Db::connect(&url).await.unwrap();
        stage(&root, &db).await.unwrap();
        assert_eq!(db.legacy_repair(&"A".repeat(64)).await.unwrap(), saved);
        assert!(db.legacy_repair(&"B".repeat(64)).await.unwrap().is_none());
        assert_eq!(db.article_repairs("staged@loopback").await.unwrap(), []);
        for hash in ["A".repeat(64), "B".repeat(64)] {
            assert!(
                db.config()
                    .await
                    .unwrap()
                    .iter()
                    .any(|(key, _)| key == &format!("legacy.repair.unusable.{hash}"))
            );
            assert_eq!(
                std::fs::read(
                    root.join("repair-segments")
                        .join(&hash[..2])
                        .join(format!("{hash}.h"))
                )
                .unwrap(),
                header()
            );
        }
        db.close().await;
        std::fs::remove_dir_all(root).unwrap();
    }

    #[test]
    fn invalid_or_incomplete_legacy_headers_never_yield_a_candidate() {
        assert!(body(&header(), b"short".to_vec()).is_err());
        assert!(checked_body(&header(), b"data".to_vec(), &[0; 32]).is_err());
        assert!(checked_body(&header(), b"data".to_vec(), &Sha256::digest(b"data")).is_ok());
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
