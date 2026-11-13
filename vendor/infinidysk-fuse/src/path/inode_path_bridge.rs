use std::collections::VecDeque;
use std::collections::{HashMap, HashSet};
use std::ffi::{OsStr, OsString};
use std::fmt::{self, Debug, Formatter};
use std::path::PathBuf;
use std::pin::Pin;
use std::sync::Arc;
use std::task::{Context, Poll};

#[cfg(all(not(feature = "tokio-runtime"), feature = "async-io-runtime"))]
use async_lock::RwLock;
use bytes::Bytes;
use futures_util::stream::{self, Stream, StreamExt};
use slab::Slab;
#[cfg(all(not(feature = "async-io-runtime"), feature = "tokio-runtime"))]
use tokio::sync::RwLock;

use super::inode_generator::InodeGenerator;
use super::path_filesystem::PathFilesystem;
use crate::helper::Apply;
use crate::notify::Notify;
use crate::raw::reply::*;
use crate::raw::{Filesystem, Request};
use crate::{Errno, SetAttr};
use crate::{Inode, Result};

const ROOT_INODE: Inode = 1;

/// `RENAME_EXCHANGE` of rename2 on Linux.
const RENAME_EXCHANGE: u32 = 1 << 1;

#[derive(Debug, Eq, PartialEq, Hash, Clone)]
struct Name {
    parent: Inode,
    name: OsString,
}

impl Name {
    fn new(parent: Inode, name: OsString) -> Self {
        Self { parent, name }
    }
}

#[derive(Debug)]
struct InodeNameManager {
    inode_to_names: HashMap<Inode, HashSet<Name>>,
    name_to_inode: HashMap<Name, Inode>,
    /// How often the kernel looked up each inode and did not forget it yet.
    lookups: HashMap<Inode, u64>,
    inode_generator: InodeGenerator,
}

impl InodeNameManager {
    fn get_absolute_path(&self, inode: Inode) -> Option<PathBuf> {
        let names = self.inode_to_names.get(&inode)?;
        let name = names.iter().next().unwrap();

        if name.parent == ROOT_INODE {
            Some(PathBuf::from("/").apply(|path| path.push(&name.name)))
        } else {
            Some(
                self.get_absolute_path(name.parent)?
                    .apply(|path| path.push(&name.name)),
            )
        }
    }

    fn remove_name(&mut self, name: &Name) {
        if let Some(inode) = self.name_to_inode.remove(name)
            && let Some(names) = self.inode_to_names.get_mut(&inode)
        {
            names.remove(name);

            if names.is_empty() {
                self.inode_to_names.remove(&inode);

                // An unlinked or renamed file may still be open, so the kernel
                // can still use the inode. Its number is only reused after the
                // kernel forgot it, otherwise two files would share one inode.
                if !self.lookups.contains_key(&inode) {
                    self.inode_generator.release_inode(inode);
                }
            }
        }
    }

    fn add_name(&mut self, inode: Inode, name: Name) {
        self.name_to_inode.insert(name.clone(), inode);
        self.inode_to_names.entry(inode).or_default().insert(name);
    }

    /// Move the name `old` to `new`. The inode keeps its number, like the kernel
    /// keeps its inode for the renamed entry. An inode that had the name `new`
    /// loses it.
    fn rename_name(&mut self, old: &Name, new: Name) {
        if *old == new {
            return;
        }

        self.remove_name(&new);

        match self.get_name_inode(old) {
            Some(inode) => {
                // add the new name first, so the inode never runs out of names
                self.add_name(inode, new);
                self.remove_name(old);
            }

            None => {
                self.insert_name(new);
            }
        }
    }

    /// Swap the inodes of the names `a` and `b` (rename with `RENAME_EXCHANGE`).
    fn exchange_names(&mut self, a: &Name, b: &Name) {
        match (self.get_name_inode(a), self.get_name_inode(b)) {
            (Some(inode_a), Some(inode_b)) => {
                for (inode, old, new) in [(inode_a, a, b), (inode_b, b, a)] {
                    if let Some(names) = self.inode_to_names.get_mut(&inode) {
                        names.remove(old);
                        names.insert(new.clone());
                    }
                    self.name_to_inode.insert(new.clone(), inode);
                }
            }

            _ => {
                self.remove_name(a);
                self.remove_name(b);
            }
        }
    }

    /// Count a reply that makes the kernel look up `inode` once more.
    fn looked_up(&mut self, inode: Inode) {
        *self.lookups.entry(inode).or_default() += 1;
    }

    /// The kernel forgot `nlookup` lookups of `inode`. When no lookup is left,
    /// the inode and its names are removed and its number may be reused.
    fn forget(&mut self, inode: Inode, nlookup: u64) {
        // the root lives as long as the session; the kernel may still forget it
        // while unmounting
        if inode == ROOT_INODE {
            return;
        }

        if let Some(count) = self.lookups.get_mut(&inode) {
            *count = count.saturating_sub(nlookup);

            if *count > 0 {
                return;
            }

            self.lookups.remove(&inode);
        } else {
            return;
        }

        if let Some(names) = self.inode_to_names.remove(&inode) {
            names.iter().for_each(|name| {
                self.name_to_inode.remove(name);
            });
        }

        self.inode_generator.release_inode(inode);
    }

    fn contains_name(&self, name: &Name) -> bool {
        self.name_to_inode.contains_key(name)
    }

    fn insert_name(&mut self, name: Name) -> Inode {
        let inode = self.inode_generator.allocate_inode();

        self.name_to_inode.insert(name.clone(), inode);

        let mut names = HashSet::with_capacity(1);
        names.insert(name);

        self.inode_to_names.insert(inode, names);

        inode
    }

    fn get_name_inode(&self, name: &Name) -> Option<Inode> {
        self.name_to_inode.get(name).copied()
    }
}

/// The entries of a readdirplus reply, each holding one lookup count.
///
/// The raw layer packs entries in order and stops when the reply buffer is
/// full, so an entry was packed when the next one is asked for or the stream
/// ends. When the stream is dropped, the entries that were not packed give
/// their counts back.
struct CountedEntries {
    entries: VecDeque<(DirectoryEntryPlus, Option<Inode>)>,
    /// The count of the entry handed out last, until it is known to be packed.
    last: Option<Inode>,
    inode_name_manager: Arc<RwLock<InodeNameManager>>,
}

impl Stream for CountedEntries {
    type Item = Result<DirectoryEntryPlus>;

    fn poll_next(mut self: Pin<&mut Self>, _cx: &mut Context<'_>) -> Poll<Option<Self::Item>> {
        // asking for more means the previous entry was packed
        self.last = None;

        let next = self.entries.pop_front();

        Poll::Ready(next.map(|(entry, counted)| {
            self.last = counted;

            Ok(entry)
        }))
    }
}

impl Drop for CountedEntries {
    fn drop(&mut self) {
        let unsent = self
            .last
            .take()
            .into_iter()
            .chain(self.entries.drain(..).filter_map(|(_, counted)| counted))
            .collect::<Vec<_>>();

        if unsent.is_empty() {
            return;
        }

        let forget = move |manager: &mut InodeNameManager| {
            for inode in &unsent {
                manager.forget(*inode, 1);
            }
        };

        #[cfg(all(not(feature = "async-io-runtime"), feature = "tokio-runtime"))]
        let manager = self.inode_name_manager.try_write().ok();
        #[cfg(all(not(feature = "tokio-runtime"), feature = "async-io-runtime"))]
        let manager = self.inode_name_manager.try_write();

        if let Some(mut manager) = manager {
            forget(&mut manager);
        } else {
            let inode_name_manager = self.inode_name_manager.clone();

            crate::raw::spawn_task(async move {
                forget(&mut *inode_name_manager.write().await);
            });
        }
    }
}

pub struct InodePathBridge<FS> {
    path_filesystem: FS,
    inode_name_manager: Arc<RwLock<InodeNameManager>>,
}

impl<FS> InodePathBridge<FS> {
    pub fn new(path_filesystem: FS) -> Self {
        let mut slab = Slab::new();
        // drop 0 key
        slab.insert(());

        let mut inode_name_manager = InodeNameManager {
            inode_to_names: Default::default(),
            name_to_inode: Default::default(),
            lookups: Default::default(),
            inode_generator: InodeGenerator::new(),
        };

        let root_inode = inode_name_manager.inode_generator.allocate_inode();

        assert_eq!(root_inode, ROOT_INODE);

        // root parent is itself
        inode_name_manager.inode_to_names.insert(
            root_inode,
            HashSet::from_iter(vec![Name::new(root_inode, OsString::from("/"))]),
        );

        Self {
            path_filesystem,
            inode_name_manager: Arc::new(RwLock::new(inode_name_manager)),
        }
    }
}

impl<FS> Debug for InodePathBridge<FS> {
    fn fmt(&self, f: &mut Formatter<'_>) -> fmt::Result {
        f.debug_struct("InodePathBridge").finish_non_exhaustive()
    }
}

impl<FS> Filesystem for InodePathBridge<FS>
where
    FS: PathFilesystem + Send + Sync + 'static,
{
    async fn init(&self, req: Request) -> Result<ReplyInit> {
        let reply_init = self.path_filesystem.init(req).await?;

        Ok(ReplyInit {
            max_background: reply_init.max_background,
            congestion_threshold: reply_init.congestion_threshold,
            ..ReplyInit::new(reply_init.max_write)
        })
    }

    async fn destroy(&self, req: Request) {
        self.path_filesystem.destroy(req).await
    }

    async fn lookup(&self, req: Request, parent: u64, name: &OsStr) -> Result<ReplyEntry> {
        let mut inode_name_manager = self.inode_name_manager.write().await;

        let parent_path = inode_name_manager
            .get_absolute_path(parent)
            .ok_or_else(Errno::new_not_exist)?;

        match self
            .path_filesystem
            .lookup(req, parent_path.as_ref(), name)
            .await
        {
            Err(err) => {
                if err.is_not_exist() {
                    inode_name_manager.remove_name(&Name::new(parent, name.to_owned()));
                }

                Err(err)
            }

            Ok(entry) => {
                let name = Name::new(parent, name.to_owned());

                let inode = inode_name_manager
                    .get_name_inode(&name)
                    .unwrap_or_else(|| inode_name_manager.insert_name(name));
                inode_name_manager.looked_up(inode);

                Ok(ReplyEntry {
                    ttl: entry.ttl,
                    attr: (inode, entry.attr).into(),
                    generation: 0,
                })
            }
        }
    }

    async fn forget(&self, req: Request, inode: u64, nlookup: u64) {
        // TODO if kernel forget a dir which has children, it may break

        let mut inode_name_manager = self.inode_name_manager.write().await;

        if let Some(path) = inode_name_manager.get_absolute_path(inode) {
            self.path_filesystem
                .forget(req, path.as_ref(), nlookup)
                .await;
        }

        // Also an inode without names (unlinked while open) is released here.
        inode_name_manager.forget(inode, nlookup);
    }

    async fn getattr(
        &self,
        req: Request,
        inode: u64,
        fh: Option<u64>,
        flags: u32,
    ) -> Result<ReplyAttr> {
        let inode_name_manager = self.inode_name_manager.read().await;
        let path = inode_name_manager.get_absolute_path(inode);

        let attr = self
            .path_filesystem
            .getattr(req, path.as_ref().map(|path| path.as_ref()), fh, flags)
            .await?;

        Ok(ReplyAttr {
            ttl: attr.ttl,
            attr: (inode, attr.attr).into(),
        })
    }

    async fn setattr(
        &self,
        req: Request,
        inode: u64,
        fh: Option<u64>,
        set_attr: SetAttr,
    ) -> Result<ReplyAttr> {
        let inode_name_manager = self.inode_name_manager.read().await;
        let path = inode_name_manager.get_absolute_path(inode);

        let attr = self
            .path_filesystem
            .setattr(req, path.as_ref().map(|path| path.as_ref()), fh, set_attr)
            .await?;

        Ok(ReplyAttr {
            ttl: attr.ttl,
            attr: (inode, attr.attr).into(),
        })
    }

    async fn readlink(&self, req: Request, inode: u64) -> Result<ReplyData> {
        let inode_name_manager = self.inode_name_manager.read().await;
        let path = inode_name_manager
            .get_absolute_path(inode)
            .ok_or_else(Errno::new_not_exist)?;

        self.path_filesystem.readlink(req, path.as_ref()).await
    }

    async fn symlink(
        &self,
        req: Request,
        parent: u64,
        name: &OsStr,
        link: &OsStr,
    ) -> Result<ReplyEntry> {
        let mut inode_name_manager = self.inode_name_manager.write().await;
        let parent_path = inode_name_manager
            .get_absolute_path(parent)
            .ok_or_else(Errno::new_not_exist)?;

        match self
            .path_filesystem
            .symlink(req, parent_path.as_ref(), name, link)
            .await
        {
            Err(err) => {
                if err.is_not_exist() {
                    let name = Name::new(parent, name.to_owned());
                    inode_name_manager.remove_name(&name);
                }

                Err(err)
            }

            Ok(entry) => {
                let name = Name::new(parent, name.to_owned());

                let inode = inode_name_manager
                    .get_name_inode(&name)
                    .unwrap_or_else(|| inode_name_manager.insert_name(name));
                inode_name_manager.looked_up(inode);

                Ok(ReplyEntry {
                    ttl: entry.ttl,
                    attr: (inode, entry.attr).into(),
                    generation: 0,
                })
            }
        }
    }

    async fn mknod(
        &self,
        req: Request,
        parent: u64,
        name: &OsStr,
        mode: u32,
        rdev: u32,
    ) -> Result<ReplyEntry> {
        let mut inode_name_manager = self.inode_name_manager.write().await;
        let parent_path = inode_name_manager
            .get_absolute_path(parent)
            .ok_or_else(Errno::new_not_exist)?;

        match self
            .path_filesystem
            .mknod(req, parent_path.as_ref(), name, mode, rdev)
            .await
        {
            Err(err) => {
                if err.is_exist() {
                    let name = Name::new(parent, name.to_owned());
                    inode_name_manager.remove_name(&name);
                }

                Err(err)
            }

            Ok(entry) => {
                let name = Name::new(parent, name.to_owned());

                let inode = inode_name_manager
                    .get_name_inode(&name)
                    .unwrap_or_else(|| inode_name_manager.insert_name(name));
                inode_name_manager.looked_up(inode);

                Ok(ReplyEntry {
                    ttl: entry.ttl,
                    attr: (inode, entry.attr).into(),
                    generation: 0,
                })
            }
        }
    }

    async fn mkdir(
        &self,
        req: Request,
        parent: u64,
        name: &OsStr,
        mode: u32,
        umask: u32,
    ) -> Result<ReplyEntry> {
        let mut inode_name_manager = self.inode_name_manager.write().await;
        let parent_path = inode_name_manager
            .get_absolute_path(parent)
            .ok_or_else(Errno::new_not_exist)?;

        match self
            .path_filesystem
            .mkdir(req, parent_path.as_ref(), name, mode, umask)
            .await
        {
            Err(err) => {
                if err.is_exist() {
                    let name = Name::new(parent, name.to_owned());
                    inode_name_manager.remove_name(&name);
                }

                Err(err)
            }

            Ok(entry) => {
                let name = Name::new(parent, name.to_owned());

                let inode = inode_name_manager
                    .get_name_inode(&name)
                    .unwrap_or_else(|| inode_name_manager.insert_name(name));
                inode_name_manager.looked_up(inode);

                Ok(ReplyEntry {
                    ttl: entry.ttl,
                    attr: (inode, entry.attr).into(),
                    generation: 0,
                })
            }
        }
    }

    async fn unlink(&self, req: Request, parent: u64, name: &OsStr) -> Result<()> {
        let mut inode_name_manager = self.inode_name_manager.write().await;
        let parent_path = inode_name_manager
            .get_absolute_path(parent)
            .ok_or_else(Errno::new_not_exist)?;

        if let Err(err) = self
            .path_filesystem
            .unlink(req, parent_path.as_ref(), name)
            .await
        {
            if err.is_not_exist() {
                let name = Name::new(parent, name.to_owned());
                inode_name_manager.remove_name(&name);
            } else if err.is_dir() {
                let name = Name::new(parent, name.to_owned());

                if !inode_name_manager.contains_name(&name) {
                    inode_name_manager.insert_name(name);
                }
            }

            Err(err)
        } else {
            inode_name_manager.remove_name(&Name::new(parent, name.to_owned()));

            Ok(())
        }
    }

    async fn rmdir(&self, req: Request, parent: u64, name: &OsStr) -> Result<()> {
        let mut inode_name_manager = self.inode_name_manager.write().await;
        let parent_path = inode_name_manager
            .get_absolute_path(parent)
            .ok_or_else(Errno::new_not_exist)?;

        if let Err(err) = self
            .path_filesystem
            .rmdir(req, parent_path.as_ref(), name)
            .await
        {
            if err.is_not_exist() {
                let name = Name::new(parent, name.to_owned());
                inode_name_manager.remove_name(&name);
            } else if err.is_not_dir() {
                let name = Name::new(parent, name.to_owned());

                if !inode_name_manager.contains_name(&name) {
                    inode_name_manager.insert_name(name);
                }
            }

            Err(err)
        } else {
            inode_name_manager.remove_name(&Name::new(parent, name.to_owned()));

            Ok(())
        }
    }

    async fn rename(
        &self,
        req: Request,
        parent: u64,
        name: &OsStr,
        new_parent: u64,
        new_name: &OsStr,
    ) -> Result<()> {
        let mut inode_name_manager = self.inode_name_manager.write().await;

        let origin_parent_path = inode_name_manager
            .get_absolute_path(parent)
            .ok_or_else(Errno::new_not_exist)?;
        let new_parent_path = inode_name_manager
            .get_absolute_path(new_parent)
            .ok_or_else(Errno::new_not_exist)?;

        // here is very complex so don't modify the inode_name_manager when error
        self.path_filesystem
            .rename(
                req,
                origin_parent_path.as_ref(),
                name,
                new_parent_path.as_ref(),
                new_name,
            )
            .await?;

        inode_name_manager.rename_name(
            &Name::new(parent, name.to_owned()),
            Name::new(new_parent, new_name.to_owned()),
        );

        Ok(())
    }

    async fn link(
        &self,
        req: Request,
        inode: u64,
        new_parent: u64,
        new_name: &OsStr,
    ) -> Result<ReplyEntry> {
        let mut inode_name_manager = self.inode_name_manager.write().await;
        let parent_path = inode_name_manager
            .get_absolute_path(inode)
            .ok_or_else(Errno::new_not_exist)?;
        let new_parent_path = inode_name_manager
            .get_absolute_path(new_parent)
            .ok_or_else(Errno::new_not_exist)?;

        // here is very complex so don't modify the inode_name_manager when error
        let entry = self
            .path_filesystem
            .link(
                req,
                parent_path.as_ref(),
                new_parent_path.as_ref(),
                new_name,
            )
            .await?;

        let name = Name::new(new_parent, new_name.to_owned());

        let inode = inode_name_manager
            .get_name_inode(&name)
            .unwrap_or_else(|| inode_name_manager.insert_name(name));
        inode_name_manager.looked_up(inode);

        Ok(ReplyEntry {
            ttl: entry.ttl,
            attr: (inode, entry.attr).into(),
            generation: 0,
        })
    }

    async fn open(&self, req: Request, inode: u64, flags: u32) -> Result<ReplyOpen> {
        let inode_name_manager = self.inode_name_manager.read().await;
        let path = inode_name_manager
            .get_absolute_path(inode)
            .ok_or_else(Errno::new_not_exist)?;

        self.path_filesystem.open(req, path.as_ref(), flags).await
    }

    async fn read(
        &self,
        req: Request,
        inode: u64,
        fh: u64,
        offset: u64,
        size: u32,
    ) -> Result<ReplyData> {
        let path = self
            .inode_name_manager
            .read()
            .await
            .get_absolute_path(inode);

        self.path_filesystem
            .read(
                req,
                path.as_ref().map(|path| path.as_ref()),
                fh,
                offset,
                size,
            )
            .await
    }

    async fn write(
        &self,
        req: Request,
        inode: u64,
        fh: u64,
        offset: u64,
        data: &[u8],
        write_flags: u32,
        flags: u32,
    ) -> Result<ReplyWrite> {
        let path = self
            .inode_name_manager
            .read()
            .await
            .get_absolute_path(inode);

        self.path_filesystem
            .write(
                req,
                path.as_ref().map(|path| path.as_ref()),
                fh,
                offset,
                data,
                write_flags,
                flags,
            )
            .await
    }

    async fn statfs(&self, req: Request, inode: u64) -> Result<ReplyStatFs> {
        let inode_name_manager = self.inode_name_manager.read().await;
        let path = inode_name_manager
            .get_absolute_path(inode)
            .ok_or_else(Errno::new_not_exist)?;

        self.path_filesystem.statfs(req, path.as_ref()).await
    }

    async fn release(
        &self,
        req: Request,
        inode: u64,
        fh: u64,
        flags: u32,
        lock_owner: u64,
        flush: bool,
    ) -> Result<()> {
        let path = self
            .inode_name_manager
            .read()
            .await
            .get_absolute_path(inode);

        self.path_filesystem
            .release(
                req,
                path.as_ref().map(|path| path.as_ref()),
                fh,
                flags,
                lock_owner,
                flush,
            )
            .await
    }

    async fn fsync(&self, req: Request, inode: u64, fh: u64, datasync: bool) -> Result<()> {
        let path = self
            .inode_name_manager
            .read()
            .await
            .get_absolute_path(inode);

        self.path_filesystem
            .fsync(req, path.as_ref().map(|path| path.as_ref()), fh, datasync)
            .await
    }

    async fn setxattr(
        &self,
        req: Request,
        inode: u64,
        name: &OsStr,
        value: &[u8],
        flags: u32,
        position: u32,
    ) -> Result<()> {
        let inode_name_manager = self.inode_name_manager.read().await;
        let path = inode_name_manager
            .get_absolute_path(inode)
            .ok_or_else(Errno::new_not_exist)?;

        self.path_filesystem
            .setxattr(req, path.as_ref(), name, value, flags, position)
            .await
    }

    async fn getxattr(
        &self,
        req: Request,
        inode: u64,
        name: &OsStr,
        size: u32,
    ) -> Result<ReplyXAttr> {
        let inode_name_manager = self.inode_name_manager.read().await;
        let path = inode_name_manager
            .get_absolute_path(inode)
            .ok_or_else(Errno::new_not_exist)?;

        self.path_filesystem
            .getxattr(req, path.as_ref(), name, size)
            .await
    }

    async fn listxattr(&self, req: Request, inode: u64, size: u32) -> Result<ReplyXAttr> {
        let inode_name_manager = self.inode_name_manager.read().await;
        let path = inode_name_manager
            .get_absolute_path(inode)
            .ok_or_else(Errno::new_not_exist)?;

        self.path_filesystem
            .listxattr(req, path.as_ref(), size)
            .await
    }

    async fn removexattr(&self, req: Request, inode: u64, name: &OsStr) -> Result<()> {
        let inode_name_manager = self.inode_name_manager.read().await;
        let path = inode_name_manager
            .get_absolute_path(inode)
            .ok_or_else(Errno::new_not_exist)?;

        self.path_filesystem
            .removexattr(req, path.as_ref(), name)
            .await
    }

    async fn flush(&self, req: Request, inode: u64, fh: u64, lock_owner: u64) -> Result<()> {
        let path = self
            .inode_name_manager
            .read()
            .await
            .get_absolute_path(inode);

        self.path_filesystem
            .flush(req, path.as_ref().map(|path| path.as_ref()), fh, lock_owner)
            .await
    }

    async fn opendir(&self, req: Request, inode: u64, flags: u32) -> Result<ReplyOpen> {
        let inode_name_manager = self.inode_name_manager.read().await;
        let path = inode_name_manager
            .get_absolute_path(inode)
            .ok_or_else(Errno::new_not_exist)?;

        self.path_filesystem
            .opendir(req, path.as_ref(), flags)
            .await
    }

    async fn readdir(
        &self,
        req: Request,
        parent: u64,
        fh: u64,
        offset: i64,
    ) -> Result<ReplyDirectory<impl Stream<Item = Result<DirectoryEntry>> + Send + '_>> {
        let mut inode_name_manager = self.inode_name_manager.write().await;
        let parent_path = inode_name_manager
            .get_absolute_path(parent)
            .ok_or_else(Errno::new_not_exist)?;

        let children = self
            .path_filesystem
            .readdir(req, parent_path.as_ref(), fh, offset)
            .await?;

        let entries = children.entries;
        futures_util::pin_mut!(entries);

        let entries_size = entries.size_hint().1.unwrap_or(0);
        let mut entry_list = Vec::with_capacity(entries_size);

        while let Some(entry) = entries.next().await {
            let entry = entry?;

            let inode = if entry.name == OsStr::new(".") {
                parent
            } else if entry.name == OsStr::new("..") {
                inode_name_manager
                    .inode_to_names
                    .get(&parent)
                    .unwrap()
                    .iter()
                    .next()
                    .unwrap()
                    .parent
            } else {
                let name = Name::new(parent, entry.name.clone());

                inode_name_manager
                    .get_name_inode(&name)
                    .unwrap_or_else(|| inode_name_manager.insert_name(name))
            };

            entry_list.push(Ok(DirectoryEntry {
                inode,
                kind: entry.kind,
                name: entry.name,
                offset: entry.offset,
            }));
        }

        Ok(ReplyDirectory {
            entries: stream::iter(entry_list),
        })
    }

    async fn releasedir(&self, req: Request, inode: u64, fh: u64, flags: u32) -> Result<()> {
        let inode_name_manager = self.inode_name_manager.read().await;
        let path = inode_name_manager
            .get_absolute_path(inode)
            .ok_or_else(Errno::new_not_exist)?;

        self.path_filesystem
            .releasedir(req, path.as_ref(), fh, flags)
            .await
    }

    async fn fsyncdir(&self, req: Request, inode: u64, fh: u64, datasync: bool) -> Result<()> {
        let inode_name_manager = self.inode_name_manager.read().await;
        let path = inode_name_manager
            .get_absolute_path(inode)
            .ok_or_else(Errno::new_not_exist)?;

        self.path_filesystem
            .fsyncdir(req, path.as_ref(), fh, datasync)
            .await
    }

    #[allow(clippy::too_many_arguments)]
    #[cfg(feature = "file-lock")]
    async fn getlk(
        &self,
        req: Request,
        inode: u64,
        fh: u64,
        lock_owner: u64,
        start: u64,
        end: u64,
        r#type: u32,
        pid: u32,
    ) -> Result<ReplyLock> {
        let path = self
            .inode_name_manager
            .read()
            .await
            .get_absolute_path(inode);

        self.path_filesystem
            .getlk(
                req,
                path.as_ref().map(|path| path.as_ref()),
                fh,
                lock_owner,
                start,
                end,
                r#type,
                pid,
            )
            .await
    }

    #[allow(clippy::too_many_arguments)]
    #[cfg(feature = "file-lock")]
    async fn setlk(
        &self,
        req: Request,
        inode: u64,
        fh: u64,
        lock_owner: u64,
        start: u64,
        end: u64,
        r#type: u32,
        pid: u32,
        block: bool,
    ) -> Result<()> {
        let path = self
            .inode_name_manager
            .read()
            .await
            .get_absolute_path(inode);

        self.path_filesystem
            .setlk(
                req,
                path.as_ref().map(|path| path.as_ref()),
                fh,
                lock_owner,
                start,
                end,
                r#type,
                pid,
                block,
            )
            .await
    }

    async fn access(&self, req: Request, inode: u64, mask: u32) -> Result<()> {
        let inode_name_manager = self.inode_name_manager.read().await;
        let path = inode_name_manager
            .get_absolute_path(inode)
            .ok_or_else(Errno::new_not_exist)?;

        self.path_filesystem.access(req, path.as_ref(), mask).await
    }

    async fn create(
        &self,
        req: Request,
        parent: u64,
        name: &OsStr,
        mode: u32,
        flags: u32,
    ) -> Result<ReplyCreated> {
        let mut inode_name_manager = self.inode_name_manager.write().await;
        let parent_path = inode_name_manager
            .get_absolute_path(parent)
            .ok_or_else(Errno::new_not_exist)?;

        match self
            .path_filesystem
            .create(req, parent_path.as_ref(), name, mode, flags)
            .await
        {
            Err(err) => {
                if err.is_exist() || err.is_dir() {
                    let name = Name::new(parent, name.to_owned());

                    inode_name_manager
                        .get_name_inode(&name)
                        .unwrap_or_else(|| inode_name_manager.insert_name(name));
                }

                Err(err)
            }

            Ok(created) => {
                let name = Name::new(parent, name.to_owned());

                let inode = inode_name_manager
                    .get_name_inode(&name)
                    .unwrap_or_else(|| inode_name_manager.insert_name(name));
                inode_name_manager.looked_up(inode);

                Ok(ReplyCreated {
                    ttl: created.ttl,
                    attr: (inode, created.attr).into(),
                    generation: 0,
                    fh: created.fh,
                    flags: created.flags,
                })
            }
        }
    }

    #[inline]
    async fn interrupt(&self, req: Request, unique: u64) -> Result<()> {
        self.path_filesystem.interrupt(req, unique).await
    }

    async fn bmap(&self, req: Request, inode: u64, block_size: u32, idx: u64) -> Result<ReplyBmap> {
        let inode_name_manager = self.inode_name_manager.read().await;
        let path = inode_name_manager
            .get_absolute_path(inode)
            .ok_or_else(Errno::new_not_exist)?;

        self.path_filesystem
            .bmap(req, path.as_ref(), block_size, idx)
            .await
    }

    #[allow(clippy::too_many_arguments)]
    async fn poll(
        &self,
        req: Request,
        inode: u64,
        fh: u64,
        kh: Option<u64>,
        flags: u32,
        events: u32,
        notify: &Notify,
    ) -> Result<ReplyPoll> {
        let path = self
            .inode_name_manager
            .read()
            .await
            .get_absolute_path(inode);

        self.path_filesystem
            .poll(
                req,
                path.as_ref().map(|path| path.as_ref()),
                fh,
                kh,
                flags,
                events,
                notify,
            )
            .await
    }

    async fn notify_reply(&self, req: Request, inode: u64, offset: u64, data: Bytes) -> Result<()> {
        let inode_name_manager = self.inode_name_manager.read().await;
        let path = inode_name_manager
            .get_absolute_path(inode)
            .ok_or_else(Errno::new_not_exist)?;

        self.path_filesystem
            .notify_reply(req, path.as_ref(), offset, data)
            .await
    }

    async fn batch_forget(&self, req: Request, inodes: &[(u64, u64)]) {
        // TODO if kernel forget a dir which has children, it may break

        let mut inode_name_manager = self.inode_name_manager.write().await;

        let paths = inodes
            .iter()
            .filter_map(|(inode, _)| inode_name_manager.get_absolute_path(*inode))
            .collect::<Vec<_>>();
        let paths = paths.iter().map(|path| path.as_ref()).collect::<Vec<_>>();

        self.path_filesystem.batch_forget(req, &paths).await;

        inodes
            .iter()
            .for_each(|(inode, nlookup)| inode_name_manager.forget(*inode, *nlookup));
    }

    async fn fallocate(
        &self,
        req: Request,
        inode: u64,
        fh: u64,
        offset: u64,
        length: u64,
        mode: u32,
    ) -> Result<()> {
        let path = self
            .inode_name_manager
            .read()
            .await
            .get_absolute_path(inode);

        self.path_filesystem
            .fallocate(
                req,
                path.as_ref().map(|path| path.as_ref()),
                fh,
                offset,
                length,
                mode,
            )
            .await
    }

    async fn readdirplus(
        &self,
        req: Request,
        parent: u64,
        fh: u64,
        offset: u64,
        lock_owner: u64,
    ) -> Result<ReplyDirectoryPlus<impl Stream<Item = Result<DirectoryEntryPlus>> + Send + '_>>
    {
        // The guard owns every count from the first one on, also if this call is
        // cancelled while the listing runs. It is made before the lock is taken,
        // so it is dropped after the lock and can give counts back at once.
        let mut reply = CountedEntries {
            entries: VecDeque::new(),
            last: None,
            inode_name_manager: self.inode_name_manager.clone(),
        };
        let mut inode_name_manager = self.inode_name_manager.write().await;
        let parent_path = inode_name_manager
            .get_absolute_path(parent)
            .ok_or_else(Errno::new_not_exist)?;

        let children = self
            .path_filesystem
            .readdirplus(req, parent_path.as_ref(), fh, offset, lock_owner)
            .await?;

        let entries = children.entries;
        futures_util::pin_mut!(entries);

        reply.entries.reserve(entries.size_hint().1.unwrap_or(0));

        while let Some(entry) = entries.next().await {
            let entry = match entry {
                Ok(entry) => entry,
                Err(err) => {
                    // nothing reaches the kernel: give back the counts taken so far
                    for (_, counted) in reply.entries.drain(..) {
                        if let Some(inode) = counted {
                            inode_name_manager.forget(inode, 1);
                        }
                    }

                    return Err(err);
                }
            };
            let mut counted = None;

            let inode = if entry.name == OsStr::new(".") {
                parent
            } else if entry.name == OsStr::new("..") {
                inode_name_manager
                    .inode_to_names
                    .get(&parent)
                    .unwrap()
                    .iter()
                    .next()
                    .unwrap()
                    .parent
            } else {
                let name = Name::new(parent, entry.name.clone());

                let inode = inode_name_manager
                    .get_name_inode(&name)
                    .unwrap_or_else(|| inode_name_manager.insert_name(name));
                // The kernel counts a lookup for every entry it receives except
                // "." and "..". The count is taken now, so the number stays
                // taken while the reply is built, and given back for entries
                // that are not sent.
                inode_name_manager.looked_up(inode);
                counted = Some(inode);
                inode
            };

            reply.entries.push_back((
                DirectoryEntryPlus {
                    inode,
                    generation: 0,
                    kind: entry.kind,
                    name: entry.name,
                    offset: entry.offset,
                    attr: (inode, entry.attr).into(),
                    entry_ttl: entry.entry_ttl,
                    attr_ttl: entry.attr_ttl,
                },
                counted,
            ));
        }

        drop(inode_name_manager);

        Ok(ReplyDirectoryPlus { entries: reply })
    }

    async fn rename2(
        &self,
        req: Request,
        parent: u64,
        name: &OsStr,
        new_parent: u64,
        new_name: &OsStr,
        flags: u32,
    ) -> Result<()> {
        let mut inode_name_manager = self.inode_name_manager.write().await;

        let origin_parent_path = inode_name_manager
            .get_absolute_path(parent)
            .ok_or_else(Errno::new_not_exist)?;
        let new_parent_path = inode_name_manager
            .get_absolute_path(new_parent)
            .ok_or_else(Errno::new_not_exist)?;

        // here is very complex so don't modify the inode_name_manager when error
        self.path_filesystem
            .rename2(
                req,
                origin_parent_path.as_ref(),
                name,
                new_parent_path.as_ref(),
                new_name,
                flags,
            )
            .await?;

        let old_name = Name::new(parent, name.to_owned());
        let new_name = Name::new(new_parent, new_name.to_owned());

        if flags & RENAME_EXCHANGE != 0 {
            inode_name_manager.exchange_names(&old_name, &new_name);
        } else {
            inode_name_manager.rename_name(&old_name, new_name);
        }

        Ok(())
    }

    async fn lseek(
        &self,
        req: Request,
        inode: u64,
        fh: u64,
        offset: u64,
        whence: u32,
    ) -> Result<ReplyLSeek> {
        let path = self
            .inode_name_manager
            .read()
            .await
            .get_absolute_path(inode);

        self.path_filesystem
            .lseek(
                req,
                path.as_ref().map(|path| path.as_ref()),
                fh,
                offset,
                whence,
            )
            .await
    }

    #[allow(clippy::too_many_arguments)]
    async fn copy_file_range(
        &self,
        req: Request,
        inode: u64,
        fh_in: u64,
        off_in: u64,
        inode_out: u64,
        fh_out: u64,
        off_out: u64,
        length: u64,
        flags: u64,
    ) -> Result<ReplyCopyFileRange> {
        let inode_name_manager = self.inode_name_manager.read().await;
        let path_in = inode_name_manager.get_absolute_path(inode);
        let path_out = inode_name_manager.get_absolute_path(inode_out);

        drop(inode_name_manager);

        self.path_filesystem
            .copy_file_range(
                req,
                path_in.as_ref().map(|path| path.as_ref()),
                fh_in,
                off_in,
                path_out.as_ref().map(|path| path.as_ref()),
                fh_out,
                off_out,
                length,
                flags,
            )
            .await
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn manager() -> InodeNameManager {
        Arc::into_inner(InodePathBridge::new(()).inode_name_manager)
            .unwrap()
            .into_inner()
    }

    fn name(name: &str) -> Name {
        Name::new(ROOT_INODE, OsString::from(name))
    }

    #[test]
    fn unlinked_inode_keeps_its_number_until_forget() {
        let mut manager = manager();
        let old = manager.insert_name(name("old"));
        manager.looked_up(old);

        // unlinked while the kernel still knows it, for example an open file
        manager.remove_name(&name("old"));
        let new = manager.insert_name(name("new"));
        manager.looked_up(new);
        assert_ne!(old, new);

        manager.forget(old, 1);
        assert_eq!(manager.get_name_inode(&name("new")), Some(new));
        assert_eq!(manager.insert_name(name("later")), old);
    }

    #[test]
    fn inode_stays_until_all_lookups_are_forgotten() {
        let mut manager = manager();
        let inode = manager.insert_name(name("file"));
        manager.looked_up(inode);
        manager.looked_up(inode);

        manager.forget(inode, 1);
        assert_eq!(manager.get_name_inode(&name("file")), Some(inode));
        assert_eq!(
            manager.get_absolute_path(inode),
            Some(PathBuf::from("/file"))
        );

        manager.forget(inode, 1);
        assert_eq!(manager.get_name_inode(&name("file")), None);
        assert!(manager.get_absolute_path(inode).is_none());
        assert!(manager.name_to_inode.is_empty());
    }

    #[test]
    fn forget_without_a_lookup_keeps_the_inode() {
        let mut manager = manager();
        let inode = manager.insert_name(name("listed"));

        manager.forget(inode, 1);

        assert_eq!(manager.get_name_inode(&name("listed")), Some(inode));
        assert_ne!(manager.insert_name(name("next")), inode);
    }

    #[test]
    fn forget_after_reuse_keeps_an_uncounted_inode() {
        let mut manager = manager();
        let old = manager.insert_name(name("old"));
        manager.looked_up(old);
        manager.forget(old, 1);

        let reused = manager.insert_name(name("listed"));
        assert_eq!(reused, old);
        manager.forget(old, 1);

        assert_eq!(manager.get_name_inode(&name("listed")), Some(reused));
        assert_ne!(manager.insert_name(name("next")), reused);
    }

    #[test]
    fn renamed_inode_keeps_its_number() {
        let mut manager = manager();
        let moved = manager.insert_name(name("from"));
        manager.looked_up(moved);
        let replaced = manager.insert_name(name("to"));
        manager.looked_up(replaced);

        manager.rename_name(&name("from"), name("to"));
        assert_eq!(manager.get_name_inode(&name("to")), Some(moved));
        assert_eq!(manager.get_name_inode(&name("from")), None);
        assert_eq!(manager.get_absolute_path(moved), Some(PathBuf::from("/to")));
        // the replaced file may still be open: its number stays taken
        let new = manager.insert_name(name("new"));
        assert_ne!(new, replaced);
        assert_ne!(new, moved);
    }

    #[test]
    fn exchanged_names_swap_inodes() {
        let mut manager = manager();
        let a = manager.insert_name(name("a"));
        let b = manager.insert_name(name("b"));

        manager.exchange_names(&name("a"), &name("b"));
        assert_eq!(manager.get_name_inode(&name("a")), Some(b));
        assert_eq!(manager.get_name_inode(&name("b")), Some(a));
        assert_eq!(manager.get_absolute_path(a), Some(PathBuf::from("/b")));
        assert_eq!(manager.get_absolute_path(b), Some(PathBuf::from("/a")));
    }

    /// A directory with the files "f0".."f9", whose listing fails after
    /// `fail_after` entries if set.
    struct Listing {
        fail_after: Option<usize>,
        /// the listing waits forever after this many entries
        stall_after: Option<usize>,
    }

    impl PathFilesystem for Listing {
        async fn init(&self, _req: Request) -> Result<crate::path::reply::ReplyInit> {
            unimplemented!()
        }

        async fn destroy(&self, _req: Request) {}

        #[cfg(feature = "file-lock")]
        async fn getlk(
            &self,
            _req: Request,
            _path: Option<&OsStr>,
            _fh: u64,
            _lock_owner: u64,
            _start: u64,
            _end: u64,
            _type: u32,
            _pid: u32,
        ) -> Result<crate::path::reply::ReplyLock> {
            unimplemented!()
        }

        #[cfg(feature = "file-lock")]
        async fn setlk(
            &self,
            _req: Request,
            _path: Option<&OsStr>,
            _fh: u64,
            _lock_owner: u64,
            _start: u64,
            _end: u64,
            _type: u32,
            _pid: u32,
            _block: bool,
        ) -> Result<()> {
            unimplemented!()
        }

        async fn readdirplus<'a>(
            &'a self,
            _req: Request,
            _parent: &'a OsStr,
            _fh: u64,
            _offset: u64,
            _lock_owner: u64,
        ) -> Result<
            crate::path::reply::ReplyDirectoryPlus<
                impl Stream<Item = Result<crate::path::reply::DirectoryEntryPlus>> + Send + 'a,
            >,
        > {
            let now = std::time::SystemTime::now();
            let entries = (0..10).map(move |i| {
                if self.fail_after == Some(i) {
                    return Err(Errno::from(libc::EIO));
                }

                Ok(crate::path::reply::DirectoryEntryPlus {
                    kind: crate::FileType::RegularFile,
                    name: OsString::from(format!("f{i}")),
                    offset: i as i64 + 1,
                    attr: crate::path::reply::FileAttr {
                        size: 0,
                        blocks: 0,
                        atime: now,
                        mtime: now,
                        ctime: now,
                        #[cfg(target_os = "macos")]
                        crtime: now,
                        kind: crate::FileType::RegularFile,
                        perm: 0o644,
                        nlink: 1,
                        uid: 0,
                        gid: 0,
                        rdev: 0,
                        #[cfg(target_os = "macos")]
                        flags: 0,
                        blksize: 4096,
                    },
                    entry_ttl: std::time::Duration::from_secs(1),
                    attr_ttl: std::time::Duration::from_secs(1),
                })
            });

            let stall_after = self.stall_after;
            let entries = stream::iter(entries)
                .take(stall_after.unwrap_or(usize::MAX))
                .chain(stream::iter(stall_after).flat_map(|_| stream::pending()));

            Ok(crate::path::reply::ReplyDirectoryPlus { entries })
        }
    }

    /// Run a future that finishes without waiting.
    fn now<F: std::future::Future>(fut: F) -> F::Output {
        let mut cx = Context::from_waker(std::task::Waker::noop());
        match std::pin::pin!(fut).poll(&mut cx) {
            Poll::Ready(output) => output,
            Poll::Pending => panic!("the future had to wait"),
        }
    }

    fn request() -> Request {
        Request {
            unique: 1,
            uid: 0,
            gid: 0,
            pid: 0,
        }
    }

    fn counted(bridge: &InodePathBridge<Listing>) -> Vec<String> {
        let manager = now(bridge.inode_name_manager.read());
        let mut names = manager
            .lookups
            .keys()
            .map(|inode| {
                let path = manager.get_absolute_path(*inode).unwrap();
                path.to_string_lossy().into_owned()
            })
            .collect::<Vec<_>>();
        names.sort();
        names
    }

    #[test]
    fn readdirplus_counts_only_entries_that_were_packed() {
        let bridge = InodePathBridge::new(Listing {
            fail_after: None,
            stall_after: None,
        });
        let reply = now(bridge.readdirplus(request(), ROOT_INODE, 0, 0, 0)).unwrap();
        let mut entries = Box::pin(reply.entries);

        // like the raw layer: two entries are packed, the third does not fit
        for _ in 0..3 {
            assert!(now(entries.next()).unwrap().is_ok());
        }
        drop(entries);

        assert_eq!(counted(&bridge), vec!["/f0", "/f1"]);
    }

    #[test]
    fn readdirplus_counts_all_entries_of_a_whole_listing() {
        let bridge = InodePathBridge::new(Listing {
            fail_after: None,
            stall_after: None,
        });
        let reply = now(bridge.readdirplus(request(), ROOT_INODE, 0, 0, 0)).unwrap();
        let entries = now(reply.entries.collect::<Vec<_>>());
        assert_eq!(entries.len(), 10);

        assert_eq!(counted(&bridge).len(), 10);
    }

    #[test]
    fn readdirplus_counts_nothing_after_a_late_error() {
        let bridge = InodePathBridge::new(Listing {
            fail_after: Some(5),
            stall_after: None,
        });
        assert!(now(bridge.readdirplus(request(), ROOT_INODE, 0, 0, 0)).is_err());

        assert!(counted(&bridge).is_empty());
    }

    #[test]
    fn readdirplus_cancelled_during_the_listing_counts_nothing() {
        let bridge = InodePathBridge::new(Listing {
            fail_after: None,
            stall_after: Some(1),
        });
        let mut cx = Context::from_waker(std::task::Waker::noop());
        {
            let mut call = Box::pin(bridge.readdirplus(request(), ROOT_INODE, 0, 0, 0));
            // the first entry is counted, then the listing waits
            assert!(call.as_mut().poll(&mut cx).is_pending());
        }

        assert!(counted(&bridge).is_empty());
    }

    #[test]
    fn refunds_wait_for_a_busy_lock() {
        let bridge = InodePathBridge::new(Listing {
            fail_after: None,
            stall_after: None,
        });
        let reply = now(bridge.readdirplus(request(), ROOT_INODE, 0, 0, 0)).unwrap();
        let manager = bridge.inode_name_manager.clone();

        run(async move {
            // the lock is busy (like a forget during shutdown) when the reply
            // is dropped, so the refund runs in the background
            let busy = manager.write().await;
            drop(reply);
            drop(busy);

            for _ in 0..1000 {
                if manager.read().await.lookups.is_empty() {
                    return;
                }
                pause().await;
            }
            panic!("the refund never ran");
        });
    }

    #[cfg(all(not(feature = "async-io-runtime"), feature = "tokio-runtime"))]
    fn run(fut: impl std::future::Future<Output = ()>) {
        tokio::runtime::Builder::new_current_thread()
            .enable_all()
            .build()
            .unwrap()
            .block_on(fut)
    }

    #[cfg(all(not(feature = "async-io-runtime"), feature = "tokio-runtime"))]
    async fn pause() {
        tokio::time::sleep(std::time::Duration::from_millis(1)).await;
    }

    #[cfg(all(not(feature = "tokio-runtime"), feature = "async-io-runtime"))]
    fn run(fut: impl std::future::Future<Output = ()>) {
        async_global_executor::block_on(fut)
    }

    #[cfg(all(not(feature = "tokio-runtime"), feature = "async-io-runtime"))]
    async fn pause() {
        async_io::Timer::after(std::time::Duration::from_millis(1)).await;
    }

    #[test]
    fn root_is_never_forgotten() {
        let mut manager = manager();
        manager.forget(ROOT_INODE, 1);

        assert_eq!(
            manager.get_absolute_path(ROOT_INODE),
            Some(PathBuf::from("/"))
        );
        // the root number is not handed out again
        assert_ne!(manager.insert_name(name("file")), ROOT_INODE);
    }

    #[test]
    fn inode_unknown_to_the_kernel_is_released_on_unlink() {
        let mut manager = manager();
        // readdir gives inode numbers without a lookup count
        let inode = manager.insert_name(name("listed"));
        manager.remove_name(&name("listed"));
        assert_eq!(manager.insert_name(name("next")), inode);
    }
}
