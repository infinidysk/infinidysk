use std::slice;

use infinidysk_core::{
    BlobId, ByteRange, ItemId, ItemKind, JobId, PatchId,
    repair::{Patch, Source as Posted},
};
use infinidysk_db::{
    Db, Error, Item, QueueEntry,
    repair::{MAX_PATCHES, Owner, Source},
};
use sqlx::{AssertSqlSafe, Connection as _, PgConnection, PgPool, SqliteConnection};

enum Cleanup {
    Sqlite(std::path::PathBuf),
    Postgres(PgPool, String),
}

struct Fixture {
    db: Db,
    url: String,
    file: Item,
    nzb: BlobId,
    meta: BlobId,
    cleanup: Cleanup,
}

macro_rules! connection {
    ($fixture:expr, $conn:ident => $body:block) => {
        match &$fixture.cleanup {
            Cleanup::Sqlite(_) => {
                let mut $conn = SqliteConnection::connect(&$fixture.url)
                    .await
                    .expect("raw connection");
                let result = async { $body }.await;
                $conn.close().await.expect("close raw connection");
                result
            }
            Cleanup::Postgres(_, _) => {
                let mut $conn = PgConnection::connect(&$fixture.url)
                    .await
                    .expect("raw connection");
                let result = async { $body }.await;
                $conn.close().await.expect("close raw connection");
                result
            }
        }
    };
}

impl Fixture {
    async fn execute(&self, sqlite: &str, postgres: &str) {
        let sql = match &self.cleanup {
            Cleanup::Sqlite(_) => sqlite,
            Cleanup::Postgres(_, _) => postgres,
        };
        connection!(self, conn => {
            sqlx::raw_sql(AssertSqlSafe(sql.to_owned())).execute(&mut conn).await.expect("test SQL");
        });
    }

    async fn open(url: String, cleanup: Cleanup) -> Self {
        let db = Db::connect(&url).await.expect("test database");
        let root = db
            .item(ItemId::CONTENT)
            .await
            .expect("query")
            .expect("content");
        let mut file = Item::child(&root, "source.bin", ItemKind::NzbFile).expect("path");
        let (nzb, meta) = (BlobId::new(), BlobId::new());
        file.nzb_blob = Some(nzb);
        file.meta_blob = Some(meta);
        db.insert(&[file.clone()], &[(nzb, b"nzb"), (meta, b"meta")])
            .await
            .expect("seed");
        Self {
            db,
            url,
            file,
            nzb,
            meta,
            cleanup,
        }
    }

    async fn source(&self) -> Source {
        self.db
            .repair_source(Owner::Item(self.file.id))
            .await
            .expect("source")
            .expect("exists")
    }

    async fn close(self) {
        self.db.close().await;
        match self.cleanup {
            Cleanup::Sqlite(path) => {
                for suffix in ["", "-wal", "-shm"] {
                    let _ = std::fs::remove_file(format!("{}{suffix}", path.display()));
                }
            }
            Cleanup::Postgres(admin, name) => {
                sqlx::query(AssertSqlSafe(format!("DROP DATABASE {name} WITH (FORCE)")))
                    .execute(&admin)
                    .await
                    .expect("drop test database");
                admin.close().await;
            }
        }
    }
}

async fn fixtures() -> Vec<Fixture> {
    let path = std::env::temp_dir().join(format!("infinidysk-patch-db-{}.sqlite", ItemId::new()));
    let mut fixtures =
        vec![Fixture::open(format!("sqlite:{}", path.display()), Cleanup::Sqlite(path)).await];
    if let Ok(url) = std::env::var("INFINIDYSK_TEST_POSTGRES_URL") {
        let admin = PgPool::connect(&url).await.expect("test admin database");
        let name = format!("infinidysk_patch_{}", ItemId::new().0.simple());
        sqlx::query(AssertSqlSafe(format!("CREATE DATABASE {name}")))
            .execute(&admin)
            .await
            .expect("create test database");
        let (base, _) = url.rsplit_once('/').expect("test URL has database");
        fixtures.push(
            Fixture::open(
                format!("{base}/{name}?options=-c%20synchronous_commit%3Doff"),
                Cleanup::Postgres(admin, name),
            )
            .await,
        );
    } else {
        eprintln!("PostgreSQL cases need INFINIDYSK_TEST_POSTGRES_URL");
    }
    fixtures
}

fn patch() -> Patch {
    Patch {
        id: PatchId::new(),
        source: Posted {
            set: [1; 16],
            file: [2; 16],
            range: ByteRange { start: 0, end: 3 },
            file_length: 3,
            message_id: Some("patch@loopback".to_owned()),
            part: None,
            total: None,
        },
        sha256: [3; 32],
    }
}

#[tokio::test]
async fn omitted_reads_require_the_exact_owned_nzb_generation_set_file_and_range() {
    use infinidysk_core::repair::Omitted;
    for fixture in fixtures().await {
        let source = fixture.source().await;
        let mut patch = patch();
        patch.source.message_id = None;
        fixture
            .db
            .publish_patches(&source, slice::from_ref(&patch))
            .await
            .unwrap();
        let key = Omitted {
            nzb: fixture.nzb,
            generation: 0,
            set: patch.source.set,
            file: patch.source.file,
            range: patch.source.range,
            file_length: 3,
        };
        assert_eq!(
            fixture.db.omitted_patch(&key).await.unwrap(),
            Some(patch.clone())
        );
        for wrong in [
            Omitted {
                nzb: BlobId::new(),
                ..key
            },
            Omitted {
                generation: 1,
                ..key
            },
            Omitted {
                set: [9; 16],
                ..key
            },
            Omitted {
                file: [9; 16],
                ..key
            },
            Omitted {
                range: ByteRange { start: 1, end: 3 },
                ..key
            },
        ] {
            assert!(fixture.db.omitted_patch(&wrong).await.unwrap().is_none());
        }
        assert!(
            fixture
                .db
                .omitted_patch(&Omitted {
                    file_length: 4,
                    ..key
                })
                .await
                .is_err()
        );
        assert_eq!(
            fixture
                .db
                .repair_metadata(fixture.file.id, fixture.meta, fixture.nzb)
                .await
                .unwrap(),
            Some((0, b"meta".to_vec()))
        );
        assert!(
            fixture
                .db
                .repair_metadata(fixture.file.id, BlobId::new(), fixture.nzb)
                .await
                .unwrap()
                .is_none()
        );
        fixture
            .db
            .update_blob(fixture.nzb, b"changed NZB")
            .await
            .unwrap();
        assert!(fixture.db.omitted_patch(&key).await.unwrap().is_none());
        fixture.db.delete_item(fixture.file.id).await.unwrap();
        assert!(
            fixture
                .db
                .repair_metadata(fixture.file.id, fixture.meta, fixture.nzb)
                .await
                .unwrap()
                .is_none()
        );
        assert!(
            fixture
                .db
                .omitted_patch(&Omitted {
                    generation: 1,
                    ..key
                })
                .await
                .unwrap()
                .is_none()
        );
        fixture.close().await;
    }
}

#[tokio::test]
async fn publishes_references_only_for_the_unchanged_source_and_survives_reopening() {
    for mut fixture in fixtures().await {
        let source = fixture.source().await;
        let db = &fixture.db;
        let patch = patch();
        assert!(!db.may_have_repairs());
        assert!(
            db.article_patch(&source, "patch@loopback")
                .await
                .unwrap()
                .is_none()
        );
        assert!(
            db.publish_patches(&source, slice::from_ref(&patch))
                .await
                .unwrap()
        );
        assert!(db.may_have_repairs());
        assert_eq!(
            db.article_repairs("patch@loopback").await.unwrap(),
            std::slice::from_ref(&patch)
        );
        assert_eq!(
            db.article_patch(&source, "patch@loopback").await.unwrap(),
            Some(patch.clone())
        );
        db.close().await;
        // Upgrade a committed version-10 repair catalog without rewriting its references.
        let previous_catalog = "DROP INDEX repair_patches_message;
            DELETE FROM _sqlx_migrations WHERE version = 11;";
        fixture.execute(previous_catalog, previous_catalog).await;
        fixture.db = Db::connect(&fixture.url).await.unwrap();
        let db = &fixture.db;
        assert!(db.may_have_repairs());
        assert_eq!(
            db.article_patch(&source, "patch@loopback").await.unwrap(),
            Some(patch.clone())
        );
        db.update_blob(fixture.nzb, b"replacement NZB")
            .await
            .unwrap();
        assert!(!db.publish_patches(&source, &[patch]).await.unwrap());
        assert_eq!(
            db.article_repairs("patch@loopback").await.unwrap(),
            [] as [Patch; 0]
        );
        assert!(
            db.article_patch(&source, "patch@loopback")
                .await
                .unwrap()
                .is_none()
        );
        fixture.close().await;
    }
}

#[tokio::test]
async fn metadata_and_nzb_aba_changes_reject_stale_publication() {
    for fixture in fixtures().await {
        let source = fixture.source().await;
        let first = patch();
        assert!(
            fixture
                .db
                .publish_patches(&source, slice::from_ref(&first))
                .await
                .unwrap()
        );
        fixture
            .db
            .update_blob(fixture.meta, b"changed metadata")
            .await
            .unwrap();
        fixture.db.update_blob(fixture.meta, b"meta").await.unwrap();
        assert!(
            !fixture
                .db
                .publish_patches(&source, &[patch()])
                .await
                .unwrap()
        );
        assert_eq!(
            fixture
                .db
                .article_patch(&source, "patch@loopback")
                .await
                .unwrap(),
            Some(first)
        );
        let refreshed = fixture.source().await;
        assert_ne!(source, refreshed);
        fixture
            .db
            .update_blob(fixture.nzb, b"changed NZB")
            .await
            .unwrap();
        fixture.db.update_blob(fixture.nzb, b"nzb").await.unwrap();
        assert!(
            !fixture
                .db
                .publish_patches(&refreshed, &[patch()])
                .await
                .unwrap()
        );
        assert!(
            fixture
                .db
                .article_patch(&source, "patch@loopback")
                .await
                .unwrap()
                .is_none()
        );
        let refreshed = fixture.source().await;
        let fresh = patch();
        assert!(
            fixture
                .db
                .publish_patches(&refreshed, slice::from_ref(&fresh))
                .await
                .unwrap()
        );
        assert_eq!(
            fixture
                .db
                .article_patch(&refreshed, "patch@loopback")
                .await
                .unwrap(),
            Some(fresh)
        );
        fixture.close().await;
    }
}

fn range_patch(start: u64, end: u64) -> Patch {
    let mut patch = patch();
    patch.source.range = ByteRange { start, end };
    patch.source.file_length = 100;
    patch.source.message_id = None;
    patch
}

#[tokio::test]
async fn idempotent_batches_reject_conflicting_ids_articles_and_overlapping_ranges_atomically() {
    for fixture in fixtures().await {
        let source = fixture.source().await;
        let first = range_patch(0, 10);
        assert!(
            fixture
                .db
                .publish_patches(&source, slice::from_ref(&first))
                .await
                .unwrap()
        );
        assert!(
            fixture
                .db
                .publish_patches(&source, slice::from_ref(&first))
                .await
                .unwrap()
        );
        let second = range_patch(10, 20);
        let conflict = range_patch(5, 15);
        assert!(matches!(
            fixture
                .db
                .publish_patches(&source, &[second, conflict])
                .await,
            Err(Error::RepairConflict)
        ));
        let range = ByteRange { start: 0, end: 100 };
        assert_eq!(
            fixture
                .db
                .range_patches(&source, [1; 16], [2; 16], range)
                .await
                .unwrap(),
            slice::from_ref(&first)
        );
        let mut conflict = first.clone();
        conflict.sha256[0] ^= 1;
        assert!(matches!(
            fixture.db.publish_patches(&source, &[conflict]).await,
            Err(Error::RepairConflict)
        ));
        let mut identified = range_patch(10, 20);
        identified.source.message_id = Some("identified@loopback".into());
        identified.source.part = Some(2);
        assert!(
            fixture
                .db
                .publish_patches(&source, slice::from_ref(&identified))
                .await
                .unwrap()
        );
        let mut duplicate = identified.clone();
        duplicate.id = PatchId::new();
        duplicate.source.file = [4; 16];
        assert!(matches!(
            fixture.db.publish_patches(&source, &[duplicate]).await,
            Err(Error::RepairConflict)
        ));
        assert_eq!(
            fixture
                .db
                .article_patch(&source, "identified@loopback")
                .await
                .unwrap(),
            Some(identified)
        );
        assert!(
            fixture
                .db
                .article_patch(&source, "")
                .await
                .unwrap()
                .is_none()
        );
        assert_eq!(
            fixture
                .db
                .range_patches(&source, [1; 16], [2; 16], ByteRange { start: 9, end: 10 })
                .await
                .unwrap(),
            [first]
        );
        assert_eq!(
            fixture
                .db
                .range_patches(&source, [9; 16], [2; 16], range)
                .await
                .unwrap(),
            [] as [Patch; 0]
        );
        fixture.close().await;
    }
}

#[tokio::test]
async fn shared_references_survive_one_owner_but_disappear_with_the_last_owner() {
    for fixture in fixtures().await {
        let source = fixture.source().await;
        let patch = patch();
        assert!(
            fixture
                .db
                .publish_patches(&source, slice::from_ref(&patch))
                .await
                .unwrap()
        );
        let root = fixture.db.item(ItemId::CONTENT).await.unwrap().unwrap();
        let mut other = Item::child(&root, "other.bin", ItemKind::NzbFile).unwrap();
        let meta = BlobId::new();
        other.nzb_blob = Some(fixture.nzb);
        other.meta_blob = Some(meta);
        fixture
            .db
            .insert(&[other.clone()], &[(meta, b"metadata")])
            .await
            .unwrap();
        fixture.db.delete_item(fixture.file.id).await.unwrap();
        assert!(
            !fixture
                .db
                .publish_patches(&source, slice::from_ref(&patch))
                .await
                .unwrap()
        );
        let shared = fixture
            .db
            .repair_source(Owner::Item(other.id))
            .await
            .unwrap()
            .unwrap();
        assert_eq!(
            fixture
                .db
                .article_patch(&shared, "patch@loopback")
                .await
                .unwrap(),
            Some(patch)
        );
        fixture.db.delete_item(other.id).await.unwrap();
        assert!(
            fixture.db.blob(fixture.nzb).await.unwrap().is_some(),
            "NZB retention keeps bytes"
        );
        assert!(
            fixture
                .db
                .article_patch(&shared, "patch@loopback")
                .await
                .unwrap()
                .is_none()
        );
        fixture.close().await;
    }
}

#[tokio::test]
async fn source_and_lookup_bounds_reject_invalid_work_without_publication() {
    for fixture in fixtures().await {
        let source = fixture.source().await;
        assert!(
            fixture
                .db
                .repair_source(Owner::Item(ItemId::CONTENT))
                .await
                .unwrap()
                .is_none()
        );
        assert!(
            fixture
                .db
                .repair_source(Owner::Item(ItemId::new()))
                .await
                .unwrap()
                .is_none()
        );
        assert!(matches!(
            fixture.db.publish_patches(&source, &[]).await,
            Err(Error::RepairBounds(_))
        ));
        assert!(matches!(
            fixture
                .db
                .publish_patches(&source, &vec![patch(); MAX_PATCHES + 1])
                .await,
            Err(Error::RepairBounds(_))
        ));
        let mut invalid = patch();
        invalid.source.range = ByteRange {
            start: i64::MAX as u64,
            end: i64::MAX as u64 + 1,
        };
        invalid.source.file_length = invalid.source.range.end;
        assert!(matches!(
            fixture.db.publish_patches(&source, &[invalid]).await,
            Err(Error::RepairBounds(_))
        ));
        invalid = patch();
        invalid.source.message_id = Some(String::new());
        assert!(matches!(
            fixture.db.publish_patches(&source, &[invalid]).await,
            Err(Error::RepairDescriptor(_))
        ));
        assert!(
            fixture
                .db
                .range_patches(&source, [1; 16], [2; 16], ByteRange { start: 10, end: 5 })
                .await
                .is_err()
        );
        assert!(
            fixture
                .db
                .article_patch(&source, "patch@loopback")
                .await
                .unwrap()
                .is_none()
        );
        fixture.close().await;
    }
}

#[tokio::test]
async fn concurrent_publications_choose_one_immutable_article() {
    for fixture in fixtures().await {
        let source = fixture.source().await;
        let (first, second) = (patch(), patch());
        let a = [first.clone()];
        let b = [second.clone()];
        let (a, b) = tokio::join!(
            fixture.db.publish_patches(&source, &a),
            fixture.db.publish_patches(&source, &b)
        );
        let expected = match (a, b) {
            (Ok(true), Err(Error::RepairConflict)) => first,
            (Err(Error::RepairConflict), Ok(true)) => second,
            results => panic!("one publication must win: {results:?}"),
        };
        assert_eq!(
            fixture
                .db
                .article_patch(&source, "patch@loopback")
                .await
                .unwrap(),
            Some(expected)
        );
        fixture.close().await;
    }
}

#[tokio::test]
async fn queued_sources_publish_before_import_and_stop_when_the_job_disappears() {
    for fixture in fixtures().await {
        let job = QueueEntry {
            id: JobId::new(),
            created_at: 1,
            file_name: "input.nzb".into(),
            job_name: "input".into(),
            category: String::new(),
            priority: 0,
            post_processing: 3,
            nzb_blob: BlobId::new(),
            nzb_size: 3,
            total_bytes: 100,
            paused: false,
            retry_at: None,
            attempts: 0,
            stuck_count: 0,
            last_error: None,
            content_hash: None,
        };
        fixture.db.enqueue(&job, b"nzb").await.unwrap();
        let source = fixture
            .db
            .repair_source(Owner::Queue(job.id))
            .await
            .unwrap()
            .unwrap();
        assert_eq!(source.nzb(), job.nzb_blob);
        assert_eq!(
            fixture
                .db
                .queued_repair_nzb(job.id, job.nzb_blob)
                .await
                .unwrap(),
            Some((source.clone(), b"nzb".to_vec()))
        );
        assert!(
            fixture
                .db
                .queued_repair_nzb(job.id, BlobId::new())
                .await
                .unwrap()
                .is_none()
        );
        assert!(
            fixture
                .db
                .queued_repair_nzb(JobId::new(), job.nzb_blob)
                .await
                .unwrap()
                .is_none()
        );
        let patch = patch();
        assert!(
            fixture
                .db
                .publish_patches(&source, slice::from_ref(&patch))
                .await
                .unwrap()
        );
        assert_eq!(
            fixture
                .db
                .article_patch(&source, "patch@loopback")
                .await
                .unwrap(),
            Some(patch)
        );
        fixture
            .db
            .update_blob(job.nzb_blob, b"new NZB")
            .await
            .unwrap();
        let (changed, raw) = fixture
            .db
            .queued_repair_nzb(job.id, job.nzb_blob)
            .await
            .unwrap()
            .unwrap();
        assert_eq!(changed.generation(), 1);
        assert_eq!(raw, b"new NZB");
        assert_ne!(changed, source);
        fixture
            .execute("DELETE FROM queue", "DELETE FROM queue")
            .await;
        assert!(
            fixture
                .db
                .queued_repair_nzb(job.id, job.nzb_blob)
                .await
                .unwrap()
                .is_none()
        );
        assert!(
            !fixture
                .db
                .publish_patches(&source, &[range_patch(10, 20)])
                .await
                .unwrap()
        );
        assert!(
            fixture
                .db
                .article_patch(&source, "patch@loopback")
                .await
                .unwrap()
                .is_none()
        );
        fixture.close().await;
    }
}

#[tokio::test]
async fn commit_failure_rolls_back_every_reference_and_retry_stays_safe() {
    for fixture in fixtures().await {
        fixture.execute(
            "CREATE TABLE repair_commit_guard (id BLOB REFERENCES blobs(id) DEFERRABLE INITIALLY DEFERRED);
             CREATE TRIGGER repair_commit_failure AFTER INSERT ON repair_patches BEGIN
             INSERT INTO repair_commit_guard VALUES (X'ffffffffffffffffffffffffffffffff'); END;",
            "CREATE TABLE repair_commit_guard (id UUID REFERENCES blobs(id) DEFERRABLE INITIALLY DEFERRED);
             CREATE FUNCTION repair_commit_fail() RETURNS trigger LANGUAGE plpgsql AS $$
             BEGIN INSERT INTO repair_commit_guard VALUES ('ffffffff-ffff-ffff-ffff-ffffffffffff');
             RETURN NEW; END $$;
             CREATE TRIGGER repair_commit_failure AFTER INSERT ON repair_patches FOR EACH ROW EXECUTE FUNCTION repair_commit_fail();",
        ).await;
        let source = fixture.source().await;
        let patches = [range_patch(0, 10), range_patch(10, 20)];
        assert!(fixture.db.publish_patches(&source, &patches).await.is_err());
        let range = ByteRange { start: 0, end: 100 };
        assert_eq!(
            fixture
                .db
                .range_patches(&source, [1; 16], [2; 16], range)
                .await
                .unwrap(),
            [] as [Patch; 0]
        );
        fixture
            .execute(
                "DROP TRIGGER repair_commit_failure",
                "DROP TRIGGER repair_commit_failure ON repair_patches",
            )
            .await;
        assert!(fixture.db.publish_patches(&source, &patches).await.unwrap());
        assert_eq!(
            fixture
                .db
                .range_patches(&source, [1; 16], [2; 16], range)
                .await
                .unwrap(),
            patches
        );
        fixture.close().await;
    }
}

#[tokio::test]
async fn publication_uses_durable_commit_even_when_regular_connections_do_not() {
    for fixture in fixtures().await {
        fixture.execute(
            "CREATE TRIGGER repair_require_sync BEFORE INSERT ON repair_patches BEGIN
             SELECT CASE WHEN (SELECT synchronous FROM pragma_synchronous) <> 2
             THEN RAISE(ABORT, 'repair requires FULL durability') END; END;",
            "CREATE FUNCTION repair_require_sync() RETURNS trigger LANGUAGE plpgsql AS $$
             BEGIN IF current_setting('synchronous_commit') <> 'on' THEN
             RAISE EXCEPTION 'repair requires synchronous commit'; END IF; RETURN NEW; END $$;
             CREATE TRIGGER repair_require_sync BEFORE INSERT ON repair_patches FOR EACH ROW EXECUTE FUNCTION repair_require_sync();",
        ).await;
        assert!(
            fixture
                .db
                .publish_patches(&fixture.source().await, &[patch()])
                .await
                .unwrap()
        );
        fixture.close().await;
    }
}

#[tokio::test]
async fn waiting_publications_recheck_changes_and_cancellation_releases_locks() {
    for fixture in fixtures().await {
        for cancel in [false, true] {
            let source = fixture.source().await;
            connection!(fixture, conn => {
                let mut tx = conn.begin().await.unwrap();
                sqlx::query("UPDATE blobs SET data = $2, generation = generation + 1 WHERE id = $1")
                    .bind(fixture.meta.0).bind(b"changed metadata".as_slice()).execute(&mut *tx).await.unwrap();
                let patches = [patch()];
                let mut running = Box::pin(fixture.db.publish_patches(&source, &patches));
                assert!(tokio::time::timeout(std::time::Duration::from_millis(50), &mut running).await.is_err());
                if cancel {
                    drop(running);
                    tx.commit().await.unwrap();
                    let source = fixture.source().await;
                    assert!(tokio::time::timeout(std::time::Duration::from_secs(3), fixture.db.publish_patches(&source, &patches)).await.unwrap().unwrap());
                } else {
                    tx.commit().await.unwrap();
                    assert!(!tokio::time::timeout(std::time::Duration::from_secs(3), running).await.unwrap().unwrap());
                    assert!(fixture.db.article_patch(&source, "patch@loopback").await.unwrap().is_none());
                }
            });
        }
        fixture.close().await;
    }
}

#[tokio::test]
async fn deleting_the_nzb_cascades_references_and_corrupt_indexes_never_supply_descriptors() {
    for fixture in fixtures().await {
        fixture.db.set_nzb_retention_days(0);
        let source = fixture.source().await;
        assert!(
            fixture
                .db
                .publish_patches(&source, &[patch()])
                .await
                .unwrap()
        );
        fixture
            .execute(
                "UPDATE repair_patches SET message_id = 'wrong@loopback'",
                "UPDATE repair_patches SET message_id = 'wrong@loopback'",
            )
            .await;
        assert!(
            fixture
                .db
                .article_patch(&source, "wrong@loopback")
                .await
                .is_err()
        );
        fixture.db.delete_item(fixture.file.id).await.unwrap();
        assert!(fixture.db.blob(fixture.nzb).await.unwrap().is_none());
        connection!(fixture, conn => {
            let count: i64 = sqlx::query_scalar("SELECT COUNT(*) FROM repair_patches").fetch_one(&mut conn).await.unwrap();
            assert_eq!(count, 0);
        });
        fixture.close().await;
    }
}

#[tokio::test]
async fn exhausted_generations_fail_without_reusing_a_source_version() {
    for fixture in fixtures().await {
        fixture
            .execute(
                "UPDATE blobs SET generation = 9223372036854775807",
                "UPDATE blobs SET generation = 9223372036854775807",
            )
            .await;
        let source = fixture.source().await;
        assert!(
            fixture
                .db
                .update_blob(fixture.meta, b"must not replace metadata")
                .await
                .is_err()
        );
        assert_eq!(
            fixture.db.blob(fixture.meta).await.unwrap().unwrap(),
            b"meta"
        );
        assert_eq!(source, fixture.source().await);
        fixture.close().await;
    }
}

#[tokio::test]
async fn additive_migration_keeps_existing_files_and_starts_their_generation_at_zero() {
    for mut fixture in fixtures().await {
        fixture.db.close().await;
        let old_schema = "DROP TABLE repair_jobs;
            DROP TABLE legacy_repair_articles;
            ALTER TABLE items DROP COLUMN arr_replacement_requested;
            DROP TABLE repair_patches;
            ALTER TABLE blobs DROP COLUMN generation;
            DELETE FROM _sqlx_migrations WHERE version >= 10;";
        fixture.execute(old_schema, old_schema).await;
        fixture.db = Db::connect(&fixture.url).await.unwrap();
        assert_eq!(
            fixture.db.item(fixture.file.id).await.unwrap(),
            Some(fixture.file.clone())
        );
        assert_eq!(fixture.db.blob(fixture.nzb).await.unwrap().unwrap(), b"nzb");
        assert_eq!(
            fixture.db.blob(fixture.meta).await.unwrap().unwrap(),
            b"meta"
        );
        connection!(fixture, conn => {
            let version: i64 = sqlx::query_scalar("SELECT generation FROM blobs WHERE id = $1")
                .bind(fixture.nzb.0).fetch_one(&mut conn).await.unwrap();
            assert_eq!(version, 0);
        });
        assert!(
            fixture
                .db
                .publish_patches(&fixture.source().await, &[patch()])
                .await
                .unwrap()
        );
        fixture.close().await;
    }
}
