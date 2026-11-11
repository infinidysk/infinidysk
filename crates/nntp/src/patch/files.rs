use std::fs::{self, File};
use std::io::{self, Read as _, Write as _};
use std::os::unix::fs::{MetadataExt as _, PermissionsExt as _};
use std::path::Path;

use infinidysk_core::{
    PatchId,
    repair::{PREFIX_LEN, Patch, header_length},
};
use nix::dir::Dir;
use nix::errno::Errno;
use nix::fcntl::{AtFlags, OFlag, openat};
use nix::sys::stat::{Mode, SFlag, fstatat, mkdirat};
use nix::unistd::{UnlinkatFlags, linkat, unlinkat};
use sha2::{Digest as _, Sha256};

use super::{CHUNK, Error, check, steps::Steps};

pub(super) fn open_file(dir: &File, name: &str, flags: OFlag) -> io::Result<File> {
    openat(
        dir,
        name,
        flags | OFlag::O_CLOEXEC | OFlag::O_NOFOLLOW | OFlag::O_NONBLOCK,
        Mode::S_IRUSR | Mode::S_IWUSR,
    )
    .map(File::from)
    .map_err(Into::into)
}

pub(super) fn directory(path: &Path) -> Result<(File, File), Error> {
    let parent = path
        .parent()
        .filter(|p| !p.as_os_str().is_empty())
        .unwrap_or_else(|| Path::new("."));
    let name = path
        .file_name()
        .ok_or(Error::Integrity("store directory must be named"))?;
    let parent = File::open(parent)?;
    match mkdirat(&parent, name, Mode::S_IRWXU) {
        Ok(()) | Err(Errno::EEXIST) => {}
        Err(error) => return Err(error.into()),
    }
    let dir = File::from(openat(
        &parent,
        name,
        OFlag::O_RDONLY | OFlag::O_DIRECTORY | OFlag::O_NOFOLLOW | OFlag::O_CLOEXEC,
        Mode::empty(),
    )?);
    if dir.metadata()?.permissions().mode() & 0o022 != 0 {
        return Err(Error::Integrity(
            "store directory is writable by another user",
        ));
    }
    let lease = open_file(&dir, ".lock", OFlag::O_RDWR | OFlag::O_CREAT)?;
    if !lease.metadata()?.is_file() || lease.metadata()?.nlink() != 1 {
        return Err(Error::Integrity("invalid store lock file"));
    }
    lease.try_lock().map_err(|error| match error {
        fs::TryLockError::WouldBlock => Error::Busy,
        fs::TryLockError::Error(error) => Error::Io(error),
    })?;
    parent.sync_all()?;
    Ok((dir, lease))
}

pub(super) fn scan(dir: &File, recover: bool) -> Result<u64, Error> {
    let mut entries = Dir::openat(
        dir,
        ".",
        OFlag::O_RDONLY | OFlag::O_DIRECTORY | OFlag::O_CLOEXEC,
        Mode::empty(),
    )?;
    let mut used = 0u64;
    for entry in entries.iter() {
        let entry = entry?;
        let name = entry
            .file_name()
            .to_str()
            .map_err(|_| Error::Integrity("unexpected store entry"))?;
        if matches!(name, "." | ".." | ".lock") {
            continue;
        }
        let suffix = if Path::new(name).extension() == Some(std::ffi::OsStr::new("part")) {
            ".part"
        } else {
            ".patch"
        };
        let id = name
            .strip_suffix(suffix)
            .and_then(|id| id.parse::<PatchId>().ok())
            .filter(|id| format!("{id}{suffix}") == name)
            .ok_or(Error::Integrity("unexpected store entry"))?;
        let metadata = fstatat(dir, name, AtFlags::AT_SYMLINK_NOFOLLOW)?;
        if SFlag::from_bits_truncate(metadata.st_mode) != SFlag::S_IFREG {
            return Err(Error::Integrity("store entry is not a regular file"));
        }
        if recover && suffix == ".part" {
            unlinkat(
                dir,
                format!("{id}.part").as_str(),
                UnlinkatFlags::NoRemoveDir,
            )?;
        } else {
            used = u64::try_from(metadata.st_size)
                .ok()
                .and_then(|n| used.checked_add(n))
                .ok_or(Error::Integrity("store size overflow"))?;
        }
    }
    Ok(used)
}

struct Part<'a> {
    dir: &'a File,
    name: String,
    present: bool,
}

pub(super) fn prune<G>(
    dir: &File,
    remove: impl Fn(PatchId) -> Result<Option<G>, Error>,
    cancelled: &impl Fn() -> bool,
) -> Result<u64, Error> {
    let mut entries = Dir::openat(
        dir,
        ".",
        OFlag::O_RDONLY | OFlag::O_DIRECTORY | OFlag::O_CLOEXEC,
        Mode::empty(),
    )?;
    let mut removed = 0;
    for entry in entries.iter() {
        check(cancelled)?;
        let entry = entry?;
        let name = entry
            .file_name()
            .to_str()
            .map_err(|_| Error::Integrity("unexpected store entry"))?;
        let Some(id) = name
            .strip_suffix(".patch")
            .and_then(|id| id.parse::<PatchId>().ok())
        else {
            continue;
        };
        if format!("{id}.patch") != name {
            return Err(Error::Integrity("unexpected store entry"));
        }
        if let Some(_guard) = remove(id)? {
            unlinkat(dir, name, UnlinkatFlags::NoRemoveDir)?;
            removed += 1;
        }
    }
    dir.sync_all()?;
    Ok(removed)
}

impl Drop for Part<'_> {
    fn drop(&mut self) {
        if self.present {
            let _ = unlinkat(self.dir, self.name.as_str(), UnlinkatFlags::NoRemoveDir);
        }
    }
}

pub(super) fn write(
    dir: &File,
    patch: &Patch,
    header: &[u8],
    data: &[u8],
    cancelled: &impl Fn() -> bool,
    steps: &mut Steps,
) -> Result<(), Error> {
    check(cancelled)?;
    let mut part = Part {
        dir,
        name: format!("{}.part", patch.id),
        present: false,
    };
    let mut file = steps.run(|| {
        open_file(
            dir,
            &part.name,
            OFlag::O_WRONLY | OFlag::O_CREAT | OFlag::O_EXCL,
        )
    })?;
    part.present = true;
    steps.run(|| file.write_all(header))?;
    for chunk in data.chunks(CHUNK) {
        check(cancelled)?;
        steps.run(|| file.write_all(chunk))?;
    }
    steps.run(|| file.sync_all())?;
    check(cancelled)?;
    // Unlike rename, linkat refuses to replace an existing immutable repair file.
    steps.run(|| {
        linkat(
            dir,
            part.name.as_str(),
            dir,
            format!("{}.patch", patch.id).as_str(),
            AtFlags::empty(),
        )
        .map_err(Into::into)
    })?;
    steps.run(|| {
        unlinkat(dir, part.name.as_str(), UnlinkatFlags::NoRemoveDir).map_err(Into::into)
    })?;
    part.present = false;
    steps.run(|| dir.sync_all())?;
    Ok(())
}

fn exact(file: &mut File, bytes: &mut [u8]) -> Result<(), Error> {
    file.read_exact(bytes).map_err(|error| {
        if error.kind() == io::ErrorKind::UnexpectedEof {
            Error::Integrity("truncated repair file")
        } else {
            Error::Io(error)
        }
    })
}

pub(super) fn validate_file(
    file: &mut File,
    expected: &Patch,
    mut output: Option<&mut Vec<u8>>,
) -> Result<(), Error> {
    let metadata = file.metadata()?;
    if !metadata.is_file() {
        return Err(Error::Integrity("repair is not a regular file"));
    }
    let mut prefix = [0; PREFIX_LEN];
    exact(file, &mut prefix)?;
    let length = header_length(&prefix)?;
    if metadata.len() != length as u64 + expected.source.range.len() {
        return Err(Error::Integrity("repair file length"));
    }
    let mut header = vec![0; length];
    header[..PREFIX_LEN].copy_from_slice(&prefix);
    exact(file, &mut header[PREFIX_LEN..])?;
    if Patch::decode(&header)? != *expected {
        return Err(Error::Integrity("repair identity"));
    }
    let mut remaining = expected.source.range.len();
    let mut hash = Sha256::new();
    let mut buffer = [0; 16 * 1024];
    while remaining > 0 {
        let count = usize::try_from(remaining.min(buffer.len() as u64)).expect("bounded chunk");
        exact(file, &mut buffer[..count])?;
        hash.update(&buffer[..count]);
        if let Some(output) = &mut output {
            output.extend_from_slice(&buffer[..count]);
        }
        remaining -= count as u64;
    }
    if <[u8; 32]>::from(hash.finalize()) != expected.sha256 {
        return Err(Error::Integrity("repair checksum"));
    }
    if file.read(&mut [0; 1])? != 0 {
        return Err(Error::Integrity("repair file grew while reading"));
    }
    Ok(())
}
