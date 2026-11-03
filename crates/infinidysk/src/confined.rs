//! Files below a folder, reached without following a symlink below it.
//!
//! The folder itself is opened normally, so it may be a symlink. Each folder below it is
//! opened with `O_NOFOLLOW`, so a symlinked subfolder cannot lead a write or a removal out.

use std::ffi::{OsStr, OsString};
use std::fs::File;
use std::io::{self, Read as _, Write as _};
use std::os::fd::{AsFd, OwnedFd};
use std::path::{Component, Path, PathBuf};

use infinidysk_core::ItemId;
use nix::errno::Errno;
use nix::fcntl::{AT_FDCWD, AtFlags, OFlag, openat, readlinkat, renameat};
use nix::sys::stat::{FileStat, Mode, SFlag, fstat, fstatat, mkdirat};
use nix::unistd::{Gid, Uid, UnlinkatFlags, fchown, fchownat, fsync, symlinkat, unlinkat};

const DIR: OFlag = OFlag::O_RDONLY
    .union(OFlag::O_DIRECTORY)
    .union(OFlag::O_CLOEXEC);

/// What is at a path, as found without following a symlink there.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum Entry {
    /// A regular file of this many bytes.
    File(u64),
    Link,
    Other,
}

pub struct Root {
    path: PathBuf,
    fd: OwnedFd,
}

impl Root {
    pub fn open(path: &Path) -> io::Result<Self> {
        Ok(Self {
            path: path.to_path_buf(),
            fd: openat(AT_FDCWD, path, DIR, Mode::empty())?,
        })
    }

    /// Opens the deepest of `roots` that `path` lies below.
    pub fn below(roots: &[PathBuf], path: &Path) -> io::Result<Self> {
        let root = roots
            .iter()
            .filter(|r| path.starts_with(r))
            .max_by_key(|r| r.components().count())
            .ok_or_else(|| {
                io::Error::other(format!(
                    "{} is outside the library folders and the completed STRM directory",
                    path.display()
                ))
            })?;
        Self::open(root)
    }

    /// Writes `bytes` to `path` through a private temporary file, durably, creating its
    /// folders.
    pub fn write(&self, path: &Path, bytes: &[u8], owner: (u32, u32)) -> io::Result<()> {
        let (dirs, name) = self.split(path)?;
        let chain = self.dirs(&dirs, Some(owner))?;
        let dir = last(&chain);
        let tmp = temporary(name);
        let fd = openat(
            dir,
            tmp.as_os_str(),
            OFlag::O_WRONLY | OFlag::O_CREAT | OFlag::O_EXCL | OFlag::O_NOFOLLOW | OFlag::O_CLOEXEC,
            Mode::from_bits_truncate(0o644),
        )?;
        let written = (|| {
            let mut file = File::from(fd);
            file.write_all(bytes)?;
            file.sync_all()?;
            own(&file, owner)?;
            renameat(dir, tmp.as_os_str(), dir, name)?;
            io::Result::Ok(())
        })();
        if written.is_err() {
            let _ = unlinkat(dir, tmp.as_os_str(), UnlinkatFlags::NoRemoveDir);
        }
        written?;
        Ok(fsync(dir)?)
    }

    /// Points the symlink `path` at `target` through a temporary link, durably.
    pub fn link(&self, path: &Path, target: &str, (uid, gid): (u32, u32)) -> io::Result<()> {
        let (dirs, name) = self.split(path)?;
        let chain = self.dirs(&dirs, None)?;
        let dir = last(&chain);
        let tmp = temporary(name);
        symlinkat(target, dir, tmp.as_os_str())?;
        let linked = (|| {
            let at = fstatat(dir, tmp.as_os_str(), AtFlags::AT_SYMLINK_NOFOLLOW)?;
            if at.st_uid == 0 && uid != 0 {
                fchownat(
                    dir,
                    tmp.as_os_str(),
                    Some(Uid::from_raw(uid)),
                    Some(Gid::from_raw(gid)),
                    AtFlags::AT_SYMLINK_NOFOLLOW,
                )?;
            }
            renameat(dir, tmp.as_os_str(), dir, name)?;
            io::Result::Ok(())
        })();
        if linked.is_err() {
            let _ = unlinkat(dir, tmp.as_os_str(), UnlinkatFlags::NoRemoveDir);
        }
        linked?;
        Ok(fsync(dir)?)
    }

    /// Removes the file at `path` if there, then the folders this empties, keeping the
    /// first `keep` folders below the root.
    pub fn remove(&self, path: &Path, keep: usize) -> io::Result<()> {
        let (dirs, name) = self.split(path)?;
        let chain = self.dirs(&dirs, None)?;
        match unlinkat(last(&chain), name, UnlinkatFlags::NoRemoveDir) {
            Ok(()) | Err(Errno::ENOENT) => {}
            Err(error) => return Err(error.into()),
        }
        for k in (keep..dirs.len()).rev() {
            if unlinkat(&chain[k], dirs[k], UnlinkatFlags::RemoveDir).is_err() {
                break;
            }
        }
        Ok(())
    }

    /// What is at `path`; `None` when nothing is.
    pub fn entry(&self, path: &Path) -> io::Result<Option<Entry>> {
        let (dirs, name) = self.split(path)?;
        let at = match self
            .dirs(&dirs, None)
            .and_then(|chain| Ok(fstatat(last(&chain), name, AtFlags::AT_SYMLINK_NOFOLLOW)?))
        {
            Ok(at) => at,
            Err(e) if e.kind() == io::ErrorKind::NotFound => return Ok(None),
            Err(e) => return Err(e),
        };
        Ok(Some(match kind(&at) {
            SFlag::S_IFREG => Entry::File(u64::try_from(at.st_size).unwrap_or_default()),
            SFlag::S_IFLNK => Entry::Link,
            _ => Entry::Other,
        }))
    }

    /// The target of the symlink at `path`.
    pub fn read_link(&self, path: &Path) -> io::Result<OsString> {
        let (dirs, name) = self.split(path)?;
        let chain = self.dirs(&dirs, None)?;
        Ok(readlinkat(last(&chain), name)?)
    }

    /// The text of the file at `path`, refusing a symlink there.
    /// The file at `path`, refused as `InvalidData` when longer than `max` bytes.
    pub fn read_to_string(&self, path: &Path, max: u64) -> io::Result<String> {
        let (dirs, name) = self.split(path)?;
        let chain = self.dirs(&dirs, None)?;
        let fd = openat(
            last(&chain),
            name,
            OFlag::O_RDONLY | OFlag::O_NOFOLLOW | OFlag::O_CLOEXEC,
            Mode::empty(),
        )?;
        let mut text = String::new();
        File::from(fd).take(max + 1).read_to_string(&mut text)?;
        if text.len() as u64 > max {
            return Err(io::Error::new(
                io::ErrorKind::InvalidData,
                format!("{} is longer than {max} bytes", path.display()),
            ));
        }
        Ok(text)
    }

    /// The folders and the name of `path` below the root; plain names only.
    fn split<'a>(&self, path: &'a Path) -> io::Result<(Vec<&'a OsStr>, &'a OsStr)> {
        let outside = || {
            io::Error::new(
                io::ErrorKind::InvalidInput,
                format!("{} is not below {}", path.display(), self.path.display()),
            )
        };
        let mut names = Vec::new();
        for c in path
            .strip_prefix(&self.path)
            .map_err(|_| outside())?
            .components()
        {
            let Component::Normal(name) = c else {
                return Err(outside());
            };
            names.push(name);
        }
        let name = names.pop().ok_or_else(outside)?;
        Ok((names, name))
    }

    /// The root and each of `dirs` in turn, opened without following a symlink. With
    /// `create`, missing folders are made and given to that owner.
    fn dirs(&self, dirs: &[&OsStr], create: Option<(u32, u32)>) -> io::Result<Vec<OwnedFd>> {
        let mut chain = vec![self.fd.try_clone()?];
        let mut at = self.path.clone();
        for &name in dirs {
            at.push(name);
            let parent = last(&chain);
            let flags = DIR | OFlag::O_NOFOLLOW;
            let fd = match (openat(parent, name, flags, Mode::empty()), create) {
                (Ok(fd), _) => fd,
                (Err(Errno::ENOENT), Some(owner)) => {
                    let made = match mkdirat(parent, name, Mode::from_bits_truncate(0o755)) {
                        Ok(()) => true,
                        Err(Errno::EEXIST) => false,
                        Err(error) => return Err(error.into()),
                    };
                    let fd = openat(parent, name, flags, Mode::empty())
                        .map_err(|e| self.refused(e, parent, name, &at))?;
                    if made && let Err(error) = own(&fd, owner) {
                        // Made again, and owned, by the next attempt.
                        let _ = unlinkat(parent, name, UnlinkatFlags::RemoveDir);
                        return Err(error);
                    }
                    fd
                }
                (Err(error), _) => return Err(self.refused(error, parent, name, &at)),
            };
            chain.push(fd);
        }
        Ok(chain)
    }

    /// `error` from opening the folder `name` at `at`, named plainly when it is a symlink.
    fn refused(&self, error: Errno, parent: &OwnedFd, name: &OsStr, at: &Path) -> io::Error {
        let link = matches!(error, Errno::ELOOP | Errno::ENOTDIR)
            && fstatat(parent, name, AtFlags::AT_SYMLINK_NOFOLLOW)
                .is_ok_and(|s| kind(&s) == SFlag::S_IFLNK);
        if link {
            io::Error::other(format!(
                "{} is a symlink; folders below {} must be real folders",
                at.display(),
                self.path.display()
            ))
        } else {
            error.into()
        }
    }
}

fn last(chain: &[OwnedFd]) -> &OwnedFd {
    chain.last().expect("a chain starts with the root")
}

fn kind(at: &FileStat) -> SFlag {
    SFlag::from_bits_truncate(at.st_mode & SFlag::S_IFMT.bits())
}

/// A name for a temporary file next to `name`, unique per call.
fn temporary(name: &OsStr) -> OsString {
    format!(".{}.{}.partial", name.to_string_lossy(), ItemId::new()).into()
}

/// Gives what root created to `PUID`/`PGID`; otherwise it is ours already.
fn own(fd: impl AsFd, (uid, gid): (u32, u32)) -> io::Result<()> {
    if uid != 0 && fstat(&fd)?.st_uid == 0 {
        fchown(fd, Some(Uid::from_raw(uid)), Some(Gid::from_raw(gid)))?;
    }
    Ok(())
}

#[cfg(test)]
mod tests {
    use std::fs;
    use std::os::unix::fs::{MetadataExt as _, symlink};

    use super::*;

    fn scratch() -> PathBuf {
        let tmp = std::env::temp_dir().join(format!("infinidysk-confined-{}", ItemId::new()));
        fs::create_dir_all(tmp.join("root")).unwrap();
        fs::create_dir_all(tmp.join("outside")).unwrap();
        tmp
    }

    #[test]
    fn refuses_a_symlinked_folder_below_the_root() {
        let tmp = scratch();
        let owner = {
            let m = fs::metadata(&tmp).unwrap();
            (m.uid(), m.gid())
        };
        symlink(tmp.join("outside"), tmp.join("root/sub")).unwrap();
        fs::write(tmp.join("outside/kept.strm"), b"x").unwrap();
        let root = Root::open(&tmp.join("root")).unwrap();

        let error = root
            .write(&tmp.join("root/sub/a.strm"), b"x", owner)
            .unwrap_err();
        assert!(error.to_string().contains("is a symlink"), "{error}");
        assert!(root.link(&tmp.join("root/sub/a"), "t", owner).is_err());
        assert!(root.remove(&tmp.join("root/sub/kept.strm"), 0).is_err());
        assert!(root.entry(&tmp.join("root/sub/kept.strm")).is_err());
        assert_eq!(
            fs::read_dir(tmp.join("outside")).unwrap().count(),
            1,
            "nothing written or removed outside"
        );
        fs::remove_dir_all(&tmp).unwrap();
    }

    #[test]
    fn follows_a_symlinked_root() {
        let tmp = scratch();
        let owner = {
            let m = fs::metadata(&tmp).unwrap();
            (m.uid(), m.gid())
        };
        symlink(tmp.join("outside"), tmp.join("link")).unwrap();
        let root = Root::open(&tmp.join("link")).unwrap();
        root.write(&tmp.join("link/a/b.strm"), b"x", owner).unwrap();
        assert_eq!(fs::read(tmp.join("outside/a/b.strm")).unwrap(), b"x");
        fs::remove_dir_all(&tmp).unwrap();
    }

    #[test]
    fn reads_refuse_files_past_the_limit() {
        let tmp = scratch();
        fs::write(tmp.join("root/a.strm"), b"12345").unwrap();
        let root = Root::open(&tmp.join("root")).unwrap();
        assert_eq!(
            root.read_to_string(&tmp.join("root/a.strm"), 5).unwrap(),
            "12345"
        );
        let error = root
            .read_to_string(&tmp.join("root/a.strm"), 4)
            .unwrap_err();
        assert_eq!(error.kind(), io::ErrorKind::InvalidData);
        fs::remove_dir_all(&tmp).unwrap();
    }

    #[test]
    fn write_leaves_a_symlink_at_a_fixed_partial_name_alone() {
        let tmp = scratch();
        let owner = {
            let m = fs::metadata(&tmp).unwrap();
            (m.uid(), m.gid())
        };
        let victim = tmp.join("outside/victim");
        fs::write(&victim, b"keep").unwrap();
        symlink(&victim, tmp.join("root/.a.strm.partial")).unwrap();
        let root = Root::open(&tmp.join("root")).unwrap();
        root.write(&tmp.join("root/a.strm"), b"new", owner).unwrap();
        assert_eq!(fs::read(&victim).unwrap(), b"keep");
        assert_eq!(fs::read(tmp.join("root/a.strm")).unwrap(), b"new");
        fs::remove_dir_all(&tmp).unwrap();
    }

    #[test]
    fn removes_the_folders_a_removal_empties_below_the_kept_ones() {
        let tmp = scratch();
        fs::create_dir_all(tmp.join("root/c/d/e")).unwrap();
        fs::write(tmp.join("root/c/d/e/a.strm"), b"x").unwrap();
        let root = Root::open(&tmp.join("root")).unwrap();
        root.remove(&tmp.join("root/c/d/e/a.strm"), 1).unwrap();
        assert!(tmp.join("root/c").is_dir());
        assert!(!tmp.join("root/c/d").exists());
        fs::remove_dir_all(&tmp).unwrap();
    }
}
