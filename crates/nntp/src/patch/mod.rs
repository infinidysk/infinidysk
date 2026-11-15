//! Blocking immutable repair files. Call from a blocking worker, not an async executor.
//! A durability receipt is not SQL publication; callers serve only committed references.

mod files;
mod steps;
#[cfg(test)]
mod tests;

use std::collections::HashMap;
use std::fs::File;
use std::io;
use std::path::Path;
use std::sync::{Arc, Mutex};

use infinidysk_core::{PatchId, repair::Patch};
use sha2::{Digest as _, Sha256};

use crate::{Body, MAX_DECODED_LEN};
use files::{open_file, scan, validate_file};
use steps::Steps;

pub(super) const CHUNK: usize = 64 * 1024;

#[derive(Debug, thiserror::Error)]
pub enum Error {
    #[error("repair storage failed")]
    Io(#[from] io::Error),
    #[error("repair descriptor failed validation")]
    Descriptor(#[from] infinidysk_core::repair::Error),
    #[error("repair store is already open by another owner")]
    Busy,
    #[error("repair store capacity is exhausted")]
    Capacity,
    #[error("repair store must be reopened before writing again")]
    Recovery,
    #[error("repair storage operation cancelled")]
    Cancelled,
    #[error("invalid repair storage: {0}")]
    Integrity(&'static str),
    #[error("repair allocation failed")]
    Allocation,
}

impl From<nix::errno::Errno> for Error {
    fn from(error: nix::errno::Errno) -> Self {
        Self::Io(error.into())
    }
}

/// Issued only after both file data and directory entries have been synced.
/// SQL visibility and ownership still belong to the publishing transaction.
#[derive(Debug)]
pub struct Durable(Patch, Arc<Inner>);

impl Durable {
    pub const fn patch(&self) -> &Patch {
        &self.0
    }
}

impl Drop for Durable {
    fn drop(&mut self) {
        let mut pins = self
            .1
            .pins
            .lock()
            .unwrap_or_else(std::sync::PoisonError::into_inner);
        if let Some(count) = pins.get_mut(&self.0.id) {
            *count -= 1;
            if *count == 0 {
                pins.remove(&self.0.id);
            }
        }
    }
}

#[derive(Clone)]
pub struct Store(Arc<Inner>, u64);

#[derive(Debug)]
struct Inner {
    dir: File,
    // The OS lease survives until every reader/writer clone is gone.
    lease: File,
    used: Mutex<Option<u64>>,
    pins: Mutex<HashMap<PatchId, usize>>,
}

impl Drop for Inner {
    fn drop(&mut self) {
        // A fork before exec can retain a duplicate after this owner closes its descriptor.
        let _ = self.lease.unlock();
    }
}

impl Store {
    /// A checked backup can heal a checksum failure after the stored header matched.
    /// Preserve other identity conflicts so a restore cannot destroy an undo's data.
    pub fn restore(
        &self,
        patch: &Patch,
        data: &[u8],
        cancelled: impl Fn() -> bool,
    ) -> Result<Durable, Error> {
        match self.write(patch, data, &cancelled) {
            Err(Error::Integrity("repair checksum")) => {}
            result => return result,
        }
        {
            let mut state = self.0.used.lock().map_err(|_| Error::Recovery)?;
            let name = format!("{}.patch", patch.id);
            let mut file = open_file(&self.0.dir, &name, nix::fcntl::OFlag::O_RDONLY)?;
            match validate_file(&mut file, patch, None) {
                Ok(()) => {}
                Err(Error::Integrity("repair checksum")) => {
                    check(&cancelled)?;
                    nix::unistd::unlinkat(
                        &self.0.dir,
                        name.as_str(),
                        nix::unistd::UnlinkatFlags::NoRemoveDir,
                    )?;
                    let synced = self.0.dir.sync_all();
                    *state = scan(&self.0.dir, false).ok();
                    drop(state);
                    synced?;
                }
                Err(error) => return Err(error),
            }
        }
        self.write(patch, data, cancelled)
    }
    /// Caller excludes publication and returns a guard only for unreferenced files.
    /// The guard lives through the unlink, so in-flight reads can drain per file.
    pub fn prune<G>(
        &self,
        remove: impl Fn(PatchId) -> Result<Option<G>, Error>,
        cancelled: impl Fn() -> bool,
    ) -> Result<u64, Error> {
        let mut state = self.0.used.lock().map_err(|_| Error::Recovery)?;
        let result = files::prune(
            &self.0.dir,
            |id| {
                // The usage lock prevents new receipts; drops may release existing pins.
                let pinned = self
                    .0
                    .pins
                    .lock()
                    .map_err(|_| Error::Recovery)?
                    .contains_key(&id);
                if pinned { Ok(None) } else { remove(id) }
            },
            &cancelled,
        );
        *state = scan(&self.0.dir, false).ok();
        drop(state);
        result
    }
    /// Open one private dedicated directory. Its parent must exist. The process lease
    /// excludes other writers/recovery. Remove only abandoned temporary files; retain
    /// every durable file until SQL reconciliation can prove it is unreferenced.
    pub fn open(path: &Path, capacity: u64) -> Result<Self, Error> {
        if capacity == 0 {
            return Err(Error::Capacity);
        }
        let (dir, lease) = files::directory(path)?;
        let used = scan(&dir, true)?;
        dir.sync_all()?;
        Ok(Self(
            Arc::new(Inner {
                dir,
                lease,
                used: Mutex::new(Some(used)),
                pins: Mutex::new(HashMap::new()),
            }),
            capacity,
        ))
    }

    /// Share the same directory lease and usage accounting with a tighter write quota.
    pub fn limited(&self, capacity: u64) -> Result<Self, Error> {
        if capacity == 0 {
            return Err(Error::Capacity);
        }
        Ok(Self(Arc::clone(&self.0), self.1.min(capacity)))
    }

    pub fn used_bytes(&self) -> Result<u64, Error> {
        self.0
            .used
            .lock()
            .map_err(|_| Error::Recovery)?
            .ok_or(Error::Recovery)
    }

    fn receipt(&self, patch: &Patch) -> Result<Durable, Error> {
        let mut pins = self.0.pins.lock().map_err(|_| Error::Recovery)?;
        let count = pins.entry(patch.id).or_default();
        *count = count.checked_add(1).ok_or(Error::Capacity)?;
        drop(pins);
        Ok(Durable(patch.clone(), Arc::clone(&self.0)))
    }

    /// Store already verified original posted bytes. Retries of the same immutable
    /// descriptor are idempotent, including after a previous uncertain directory sync.
    pub fn write(
        &self,
        patch: &Patch,
        data: &[u8],
        cancelled: impl Fn() -> bool,
    ) -> Result<Durable, Error> {
        self.write_with(patch, data, &cancelled, &mut Steps::default())
    }

    fn write_with(
        &self,
        patch: &Patch,
        data: &[u8],
        cancelled: &impl Fn() -> bool,
        steps: &mut Steps,
    ) -> Result<Durable, Error> {
        let header = descriptor(patch)?;
        if patch.source.range.len() != data.len() as u64 {
            return Err(Error::Integrity("payload length"));
        }
        let mut hash = Sha256::new();
        check(cancelled)?;
        for chunk in data.chunks(CHUNK) {
            check(cancelled)?;
            hash.update(chunk);
        }
        if <[u8; 32]>::from(hash.finalize()) != patch.sha256 {
            return Err(Error::Integrity("payload checksum"));
        }
        let mut state = self.0.used.lock().map_err(|_| Error::Recovery)?;
        let used = state.ok_or(Error::Recovery)?;
        let name = format!("{}.patch", patch.id);
        match open_file(&self.0.dir, &name, nix::fcntl::OFlag::O_RDONLY) {
            Ok(mut existing) => {
                validate_file(&mut existing, patch, None)?;
                steps.run(|| existing.sync_all())?;
                steps.run(|| self.0.dir.sync_all())?;
                let receipt = self.receipt(patch);
                drop(state);
                return receipt;
            }
            Err(error) if error.kind() == io::ErrorKind::NotFound => {}
            Err(error) => return Err(error.into()),
        }
        let length = header.len() as u64 + data.len() as u64;
        if length > self.1.saturating_sub(used) {
            return Err(Error::Capacity);
        }
        let result = files::write(&self.0.dir, patch, &header, data, cancelled, steps);
        match result {
            Ok(()) => {
                *state = Some(used + length);
                let receipt = self.receipt(patch);
                drop(state);
                receipt
            }
            Err(error) => {
                // Failed writes may leave a durable but uncommitted file. Count it;
                // never delete it based on an uncertain publication result.
                *state = scan(&self.0.dir, false).ok();
                drop(state);
                Err(error)
            }
        }
    }

    /// Verify identity, exact size and SHA-256 before returning any bytes. The expected
    /// descriptor must come from a trusted reference, not directory enumeration.
    pub fn read(&self, patch: &Patch) -> Result<Body, Error> {
        descriptor(patch)?;
        let mut file = open_file(
            &self.0.dir,
            &format!("{}.patch", patch.id),
            nix::fcntl::OFlag::O_RDONLY,
        )?;
        let mut data = Vec::new();
        data.try_reserve_exact(usize::try_from(patch.source.range.len()).expect("bounded article"))
            .map_err(|_| Error::Allocation)?;
        validate_file(&mut file, patch, Some(&mut data))?;
        Ok(Body {
            data: data.into(),
            offset: patch.source.range.start,
            file_size: patch.source.file_length,
            part: patch.source.part,
            total: patch.source.total,
        })
    }
}

fn descriptor(patch: &Patch) -> Result<Vec<u8>, Error> {
    let encoded = patch.encode()?;
    if patch.source.range.len() > MAX_DECODED_LEN as u64 {
        return Err(Error::Integrity("article size limit"));
    }
    Ok(encoded)
}

fn check(cancelled: &impl Fn() -> bool) -> Result<(), Error> {
    if cancelled() {
        Err(Error::Cancelled)
    } else {
        Ok(())
    }
}
