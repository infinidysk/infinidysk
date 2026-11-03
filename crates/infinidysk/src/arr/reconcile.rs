//! Daily reconciliation (import policy §10.4): deletes items whose Arr library file is
//! gone, catching webhook deliveries we missed. A run that would delete more than 5 % of
//! an Arr's items deletes nothing and waits for the user to confirm.

use std::collections::{BTreeSet, HashMap, HashSet};
use std::path::PathBuf;

use infinidysk_arr::Arr;
use infinidysk_arr::inventory::{self, LibraryFile};
use infinidysk_core::{ItemId, ItemKind, JobId};
use infinidysk_db::{ArrOwner, Db, now_ms};
use serde::{Deserialize, Serialize};
use utoipa::ToSchema;
use uuid::Uuid;

use super::{ArrBinding, Discovery, item_at};
use crate::library;

/// Config key of the runs waiting for the user.
const HELD_KEY: &str = "reconcile.held";

/// A reconciliation held back for being too large.
#[derive(Clone, Debug, Serialize, Deserialize, ToSchema)]
pub struct HeldReconcile {
    pub instance: Uuid,
    pub name: String,
    pub at_ms: i64,
    /// Items the Arr owned when the run started.
    pub owned: u64,
    /// The files the Arr no longer has, as reviewed; confirming deletes only these.
    pub files: Vec<HeldFile>,
}

#[derive(Clone, Debug, Serialize, Deserialize, ToSchema)]
pub struct HeldFile {
    pub item: Uuid,
    pub file_id: Option<i64>,
    pub path: String,
}

impl From<&HeldFile> for ArrOwner {
    fn from(f: &HeldFile) -> Self {
        Self {
            item: ItemId(f.item),
            file_id: f.file_id,
            path: f.path.clone(),
        }
    }
}

#[derive(Debug, PartialEq, Eq)]
enum Verdict {
    Delete(Vec<ArrOwner>),
    Hold(Vec<ArrOwner>),
}

/// Owned files absent from the library: neither their file id nor their path is in it.
fn missing(owners: &[ArrOwner], files: &[LibraryFile]) -> Vec<ArrOwner> {
    let ids: HashSet<i64> = files.iter().map(|f| f.id).collect();
    let paths: HashSet<&str> = files.iter().map(|f| f.path.as_str()).collect();
    owners
        .iter()
        .filter(|o| {
            !o.file_id.is_some_and(|id| ids.contains(&id)) && !paths.contains(o.path.as_str())
        })
        .cloned()
        .collect()
}

/// Owners whose file id is gone while their path holds a library file, with that file's
/// id: an Arr rescan re-imports a returning or converted file under a new id.
fn moved(owners: &[ArrOwner], files: &[LibraryFile]) -> Vec<(usize, i64)> {
    let ids: HashSet<i64> = files.iter().map(|f| f.id).collect();
    let paths: HashMap<&str, i64> = files.iter().map(|f| (f.path.as_str(), f.id)).collect();
    owners
        .iter()
        .enumerate()
        .filter(|(_, o)| o.file_id.is_some_and(|id| !ids.contains(&id)))
        .filter_map(|(i, o)| Some((i, *paths.get(o.path.as_str())?)))
        .collect()
}

/// Holds when more than 5 % of the owned items would go.
fn judge(owners: &[ArrOwner], files: &[LibraryFile]) -> Verdict {
    let gone = missing(owners, files);
    let owned = owners.iter().map(|o| o.item).collect::<BTreeSet<_>>().len();
    let items = gone.iter().map(|o| o.item).collect::<BTreeSet<_>>().len();
    if items.saturating_mul(100) > owned.saturating_mul(5) {
        Verdict::Hold(gone)
    } else {
        Verdict::Delete(gone)
    }
}

impl Discovery {
    /// Reconciles each of `arrs` once, after the first discovery found their clients;
    /// returns those that failed.
    pub async fn reconcile(&self, arrs: Vec<Arr>) -> Vec<Uuid> {
        // Cannot fail: `self` holds the sender.
        let _ = self.discovered.subscribe().wait_for(|d| *d).await;
        let mut failed = Vec::new();
        let any = !arrs.is_empty();
        for arr in arrs {
            if let Err(error) = self.reconcile_one(&arr).await {
                tracing::warn!(arr = %arr.name, reason = %error, "skipped reconciling with the Arr library");
                failed.push(arr.id);
            }
        }
        if any && failed.is_empty() {
            let now = now_ms().to_string();
            let key = infinidysk_health::BACKFILLED_KEY;
            if let Err(error) = self.queue.db().write_config(&[(key, &now)], &[]).await {
                tracing::warn!(reason = %error, "could not record the Arr reconciliation");
            }
        }
        failed
    }

    async fn reconcile_one(&self, arr: &Arr) -> Result<(), String> {
        let discovery = self.lock().get(&arr.id).and_then(|k| k.error.clone());
        if let Some(error) = discovery {
            return Err(format!("its discovery failed: {error}"));
        }
        let db = self.queue.db();
        if db
            .remap_pending(Some(arr.id))
            .await
            .map_err(|e| e.to_string())?
            > 0
        {
            return Err("a library conversion waits for its rescans".to_owned());
        }
        // Read before the library, so files imported meanwhile are not taken for gone.
        let mut owners = db.owners(arr.id).await.map_err(|e| e.to_string())?;
        let categories = self.verified_categories(arr.id);
        if owners.is_empty() && categories.is_empty() {
            return Ok(());
        }
        let Some(files) = inventory::files(&self.http, arr)
            .await
            .map_err(|e| e.to_string())?
        else {
            return Ok(());
        };
        let moves = moved(&owners, &files);
        for &(i, id) in &moves {
            db.refile_owner(arr.id, &owners[i], id)
                .await
                .map_err(|e| e.to_string())?;
            owners[i].file_id = Some(id);
        }
        if !moves.is_empty() {
            tracing::info!(arr = %arr.name, files = moves.len(), "followed re-imported files to their new ids");
        }
        if !files.is_empty() && !categories.is_empty() {
            let adopted = self
                .backfill_owners(arr, &categories, &owners, &files)
                .await?;
            if adopted > 0 {
                tracing::info!(arr = %arr.name, files = adopted, "recorded the owner of earlier imports");
            }
        }
        if !files.is_empty() {
            let adopted = self.adopt_linked(arr, &files).await?;
            if adopted > 0 {
                tracing::info!(arr = %arr.name, files = adopted, "recorded the owner of linked files");
            }
        }
        if owners.is_empty() {
            return Ok(());
        }
        if files.is_empty() {
            return Err("it listed no files".to_owned());
        }
        match judge(&owners, &files) {
            Verdict::Delete(gone) => {
                forget(db, arr, &gone).await.map_err(|e| e.to_string())?;
                self.set_held(arr.id, None).await
            }
            Verdict::Hold(gone) => {
                tracing::warn!(
                    arr = %arr.name,
                    files = gone.len(),
                    "the Arr library lacks many files we hold; waiting for confirmation on the Health page"
                );
                let held = HeldReconcile {
                    instance: arr.id,
                    name: arr.name.clone(),
                    at_ms: now_ms(),
                    owned: owners.iter().map(|o| o.item).collect::<BTreeSet<_>>().len() as u64,
                    files: gone
                        .into_iter()
                        .map(|o| HeldFile {
                            item: o.item.0,
                            file_id: o.file_id,
                            path: o.path,
                        })
                        .collect(),
                };
                self.set_held(arr.id, Some(held)).await
            }
        }
    }

    /// Records `arr` as the owner of library files it imported from us before owners were
    /// recorded, or whose webhook we missed, from its import history; returns how many.
    /// The item's job must be the download imported, unless the item predates job
    /// records. ponytail: files renamed since import are skipped; following the
    /// history's rename events would adopt them too.
    async fn backfill_owners(
        &self,
        arr: &Arr,
        categories: &[String],
        owners: &[ArrOwner],
        files: &[LibraryFile],
    ) -> Result<usize, String> {
        let db = self.queue.db();
        let mut owned_items: HashSet<ItemId> = owners.iter().map(|o| o.item).collect();
        let mut owned_files: HashSet<i64> = owners.iter().filter_map(|o| o.file_id).collect();
        let library: HashMap<&str, i64> = files.iter().map(|f| (f.path.as_str(), f.id)).collect();
        let imports = infinidysk_arr::all_imports(&self.http, arr)
            .await
            .map_err(|e| e.to_string())?;
        let mut adopted = 0;
        for import in imports {
            let Some(&file_id) = library.get(import.imported.as_str()) else {
                continue;
            };
            if owned_files.contains(&file_id) {
                continue;
            }
            let job = Uuid::parse_str(&import.download_id).ok().map(JobId);
            for (dir, path) in content_paths(&import.dropped, categories) {
                let Some(item) = item_at(db, &path).await.map_err(|e| e.to_string())? else {
                    continue;
                };
                let made_by = db.job_at(&dir).await.map_err(|e| e.to_string())?;
                if owned_items.contains(&item.id) || made_by.is_some_and(|(id, _)| Some(id) != job)
                {
                    break;
                }
                db.record_owner(arr.id, item.id, Some(file_id), &import.imported, job)
                    .await
                    .map_err(|e| e.to_string())?;
                owned_items.insert(item.id);
                owned_files.insert(file_id);
                adopted += 1;
                break;
            }
        }
        Ok(adopted)
    }

    /// Records `arr` as the owner of files with no download record that a library link or
    /// STRM it lists points at, such as files carried over from another install; returns
    /// how many. An unreadable or empty library folder fails, so a missing library never
    /// leaves files looking unowned.
    async fn adopt_linked(&self, arr: &Arr, files: &[LibraryFile]) -> Result<usize, String> {
        let settings = self.config.read().await.settings().clone();
        if settings.imports.library_dirs.is_empty() {
            return Ok(0);
        }
        let db = self.queue.db();
        let mount_dirs = library::mount_dirs(db, &settings).await;
        let dirs = settings.imports.library_dirs;
        let owners = db.owners(arr.id).await.map_err(|e| e.to_string())?;
        let owned_files: HashSet<i64> = owners.iter().filter_map(|o| o.file_id).collect();
        let owned_paths: HashSet<&str> = owners.iter().map(|o| o.path.as_str()).collect();
        let mut owned_items: HashSet<ItemId> = owners.iter().map(|o| o.item).collect();
        let candidates: Vec<(usize, PathBuf)> = files
            .iter()
            .enumerate()
            .filter(|(_, f)| !owned_files.contains(&f.id) && !owned_paths.contains(f.path.as_str()))
            .filter_map(|(i, f)| Some((i, library::local(&dirs, &f.path)?)))
            .collect();
        if candidates.is_empty() {
            return Ok(0);
        }
        let linked = tokio::task::spawn_blocking(move || {
            for d in &dirs {
                let mut entries = std::fs::read_dir(&d.path)
                    .map_err(|e| format!("library folder {}: {e}", d.path))?;
                if entries.next().is_none() {
                    return Err(format!("library folder {} is empty", d.path));
                }
            }
            Ok(candidates
                .into_iter()
                .filter_map(|(i, local)| Some((i, library::pointed(&local, &mount_dirs)?)))
                .collect::<Vec<_>>())
        })
        .await
        .map_err(|e| e.to_string())??;
        let mut adopted = 0;
        for (i, id) in linked {
            if owned_items.contains(&id) {
                continue;
            }
            let Some(item) = db.item(id).await.map_err(|e| e.to_string())? else {
                continue;
            };
            if item.kind == ItemKind::Directory || !item.path.starts_with("/content/") {
                continue;
            }
            // Files with a download record are adopted through import history.
            let job_dir: String = item.path.split_inclusive('/').take(4).collect();
            let job_dir = job_dir.trim_end_matches('/');
            if db
                .job_at(job_dir)
                .await
                .map_err(|e| e.to_string())?
                .is_some()
                || !db
                    .owner_jobs(id)
                    .await
                    .map_err(|e| e.to_string())?
                    .is_empty()
            {
                continue;
            }
            let file = &files[i];
            db.record_owner(arr.id, id, Some(file.id), &file.path, None)
                .await
                .map_err(|e| e.to_string())?;
            owned_items.insert(id);
            adopted += 1;
        }
        Ok(adopted)
    }

    /// Lowercased categories of `instance`'s SAB clients that are us.
    fn verified_categories(&self, instance: Uuid) -> Vec<String> {
        self.lock().get(&instance).map_or_else(Vec::new, |k| {
            k.clients
                .iter()
                .filter(|(_, b)| *b == ArrBinding::Verified)
                .filter_map(|(c, _)| c.category.as_deref().map(str::to_lowercase))
                .collect()
        })
    }

    /// The whole library; `None` when there is none to compare against.
    async fn library(&self, arr: &Arr) -> Result<Option<Vec<LibraryFile>>, String> {
        match inventory::files(&self.http, arr).await {
            Ok(Some(files)) if files.is_empty() => Err("it listed no files".to_owned()),
            Ok(files) => Ok(files),
            Err(error) => Err(error.to_string()),
        }
    }

    /// Runs held for the user, oldest first.
    pub async fn held(&self) -> Vec<HeldReconcile> {
        match self.queue.db().config().await {
            Ok(config) => config
                .into_iter()
                .find(|(k, _)| k == HELD_KEY)
                .and_then(|(_, v)| serde_json::from_str(&v).ok())
                .unwrap_or_default(),
            Err(error) => {
                tracing::warn!(reason = %error, "reading held reconciliations failed");
                Vec::new()
            }
        }
    }

    /// Deletes the reviewed files of a held run that the Arr still lacks and whose
    /// ownership is unchanged. `Ok(None)` when nothing is held for `instance` at `at_ms`.
    pub async fn confirm_held(&self, instance: Uuid, at_ms: i64) -> Result<Option<usize>, String> {
        let Some(held) = self
            .held()
            .await
            .into_iter()
            .find(|h| h.instance == instance && h.at_ms == at_ms)
        else {
            return Ok(None);
        };
        let arr = self
            .lock()
            .get(&instance)
            .map(|k| k.arr.clone())
            .ok_or("the instance is not enabled")?;
        let Some(files) = self.library(&arr).await? else {
            return Err("its profile cannot list library files".to_owned());
        };
        let reviewed: Vec<ArrOwner> = held.files.iter().map(ArrOwner::from).collect();
        let gone = missing(&reviewed, &files);
        let deleted = forget(self.queue.db(), &arr, &gone)
            .await
            .map_err(|e| e.to_string())?;
        self.drop_held(instance, at_ms).await?;
        Ok(Some(deleted))
    }

    /// Forgets a held run without deleting anything.
    pub async fn drop_held(&self, instance: Uuid, at_ms: i64) -> Result<(), String> {
        let _guard = self.held_lock.lock().await;
        let mut held = self.held().await;
        held.retain(|h| !(h.instance == instance && h.at_ms == at_ms));
        self.write_held(&held).await
    }

    async fn set_held(&self, instance: Uuid, run: Option<HeldReconcile>) -> Result<(), String> {
        let _guard = self.held_lock.lock().await;
        let mut held = self.held().await;
        held.retain(|h| h.instance != instance);
        held.extend(run);
        self.write_held(&held).await
    }

    async fn write_held(&self, held: &[HeldReconcile]) -> Result<(), String> {
        let db = self.queue.db();
        let result = if held.is_empty() {
            db.write_config(&[], &[HELD_KEY]).await
        } else {
            let json = serde_json::to_string(held).map_err(|e| e.to_string())?;
            db.write_config(&[(HELD_KEY, &json)], &[]).await
        };
        result.map_err(|e| e.to_string())
    }
}

/// Where `dropped`, a file under the Arr's completed-downloads folder wherever it
/// mounted us, may be in our tree, as `(job dir, file)` paths: one guess per segment
/// naming one of `categories` with a job and file below it, nearest the file first.
fn content_paths(dropped: &str, categories: &[String]) -> Vec<(String, String)> {
    let parts: Vec<&str> = dropped
        .split(['/', '\\'])
        .filter(|s| !s.is_empty())
        .collect();
    (0..parts.len().saturating_sub(2))
        .rev()
        .filter(|&i| categories.iter().any(|c| c.eq_ignore_ascii_case(parts[i])))
        .map(|i| {
            (
                format!("/content/{}/{}", parts[i], parts[i + 1]),
                format!("/content/{}", parts[i..].join("/")),
            )
        })
        .collect()
}

/// Forgets `arr`'s `gone` files and deletes the items nobody owns anymore; returns how
/// many items went.
async fn forget(db: &Db, arr: &Arr, gone: &[ArrOwner]) -> Result<usize, infinidysk_db::Error> {
    let mut deleted = 0;
    for owner in gone {
        if let Some(item) = db.forget_owner(arr.id, owner).await?
            && db.delete_unowned(item).await?
        {
            tracing::info!(arr = %arr.name, item = %item, "deleted a file its Arr no longer has");
            deleted += 1;
        }
    }
    Ok(deleted)
}

#[cfg(test)]
mod tests {
    use super::*;

    fn owner(n: i64) -> ArrOwner {
        ArrOwner {
            item: ItemId::new(),
            file_id: Some(n),
            path: format!("/lib/{n}.mkv"),
        }
    }

    fn file(n: i64) -> LibraryFile {
        LibraryFile {
            id: n,
            path: format!("/lib/{n}.mkv"),
        }
    }

    #[test]
    fn holds_runs_over_five_percent() {
        let owners: Vec<ArrOwner> = (0..20).map(owner).collect();
        let present: Vec<LibraryFile> = (1..20).map(file).collect();
        assert_eq!(
            judge(&owners, &present),
            Verdict::Delete(vec![owners[0].clone()]),
            "one in twenty is allowed"
        );
        assert!(matches!(
            judge(&owners[..19], &present[..18]),
            Verdict::Hold(_)
        ));
        let by_path = ArrOwner {
            file_id: None,
            ..owner(5)
        };
        assert_eq!(missing(&[by_path], &present), [], "matched by path");
    }

    #[test]
    fn follows_a_reimported_file_to_its_new_id() {
        let owners = [owner(1), owner(2)];
        let library = [file(1), LibraryFile { id: 9, ..file(2) }];
        assert_eq!(missing(&owners, &library), []);
        assert_eq!(moved(&owners, &library), [(1, 9)]);
    }

    #[test]
    fn maps_dropped_paths_into_content_nearest_the_file_first() {
        let cats = ["movies".to_owned()];
        assert_eq!(
            content_paths("/movies/completed-symlinks/Movies/Job/a/b.mkv", &cats),
            [
                (
                    "/content/Movies/Job".to_owned(),
                    "/content/Movies/Job/a/b.mkv".to_owned()
                ),
                (
                    "/content/movies/completed-symlinks".to_owned(),
                    "/content/movies/completed-symlinks/Movies/Job/a/b.mkv".to_owned()
                ),
            ]
        );
        assert_eq!(content_paths("Z:\\movies\\Job\\b.mkv", &cats).len(), 1);
        assert_eq!(
            content_paths("/data/movies/b.mkv", &cats),
            [],
            "no job folder"
        );
    }
}
