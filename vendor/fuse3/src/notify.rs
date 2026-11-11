//! notify kernel.

use std::ffi::OsString;
use std::sync::Arc;

use bytes::{Buf, Bytes};
use zerocopy::IntoBytes;

use crate::raw::abi::{
    FUSE_NOTIFY_DELETE_OUT_SIZE, FUSE_NOTIFY_INVAL_ENTRY_OUT_SIZE,
    FUSE_NOTIFY_INVAL_INODE_OUT_SIZE, FUSE_NOTIFY_POLL_WAKEUP_OUT_SIZE,
    FUSE_NOTIFY_RETRIEVE_OUT_SIZE, FUSE_NOTIFY_STORE_OUT_SIZE, FUSE_OUT_HEADER_SIZE,
    fuse_notify_code, fuse_notify_delete_out, fuse_notify_inval_entry_out,
    fuse_notify_inval_inode_out, fuse_notify_poll_wakeup_out, fuse_notify_retrieve_out,
    fuse_notify_store_out, fuse_out_header,
};
use crate::raw::session::ResponseSender;

#[derive(Debug, Clone)]
/// notify kernel there are something need to handle.
pub struct Notify {
    sender: Arc<ResponseSender>,
}

impl Notify {
    /// Invalidate cached inode attributes and data, reporting device write errors.
    /// Success means the whole notification was accepted, not merely queued locally.
    pub async fn try_invalid_inode(self, inode: u64, offset: i64, len: i64) -> std::io::Result<()> {
        let header = fuse_out_header {
            len: (FUSE_OUT_HEADER_SIZE + FUSE_NOTIFY_INVAL_INODE_OUT_SIZE) as u32,
            error: fuse_notify_code::FUSE_NOTIFY_INVAL_INODE as i32,
            unique: 0,
        };
        let body = fuse_notify_inval_inode_out {
            ino: inode,
            off: offset,
            len,
        };
        self.sender.try_send2(&header, body.as_bytes()).await
    }

    pub(crate) fn new(sender: Arc<ResponseSender>) -> Self {
        Self { sender }
    }

    /// notify kernel there are something need to handle. If notify failed, the `kind` will be
    /// return in `Err`.
    async fn notify(&mut self, kind: NotifyKind) {
        match &kind {
            NotifyKind::Wakeup { kh } => {
                let out_header = fuse_out_header {
                    len: (FUSE_OUT_HEADER_SIZE + FUSE_NOTIFY_POLL_WAKEUP_OUT_SIZE) as u32,
                    error: fuse_notify_code::FUSE_POLL as i32,
                    unique: 0,
                };

                let wakeup_out = fuse_notify_poll_wakeup_out { kh: *kh };

                self.sender.send2(&out_header, wakeup_out.as_bytes()).await;
            }

            NotifyKind::InvalidInode { inode, offset, len } => {
                let out_header = fuse_out_header {
                    len: (FUSE_OUT_HEADER_SIZE + FUSE_NOTIFY_INVAL_INODE_OUT_SIZE) as u32,
                    error: fuse_notify_code::FUSE_NOTIFY_INVAL_INODE as i32,
                    unique: 0,
                };

                let invalid_inode_out = fuse_notify_inval_inode_out {
                    ino: *inode,
                    off: *offset,
                    len: *len,
                };

                self.sender
                    .send2(&out_header, invalid_inode_out.as_bytes())
                    .await;
            }

            NotifyKind::InvalidEntry { parent, name } => {
                let out_header = fuse_out_header {
                    len: (FUSE_OUT_HEADER_SIZE + FUSE_NOTIFY_INVAL_ENTRY_OUT_SIZE) as u32,
                    error: fuse_notify_code::FUSE_NOTIFY_INVAL_ENTRY as i32,
                    unique: 0,
                };

                let invalid_entry_out = fuse_notify_inval_entry_out {
                    parent: *parent,
                    namelen: name.len() as _,
                    _padding: 0,
                };

                // TODO should I add null at the end?
                self.sender
                    .send2(&out_header, invalid_entry_out.as_bytes())
                    .await;
            }

            NotifyKind::Delete {
                parent,
                child,
                name,
            } => {
                let out_header = fuse_out_header {
                    len: (FUSE_OUT_HEADER_SIZE + FUSE_NOTIFY_DELETE_OUT_SIZE) as u32,
                    error: fuse_notify_code::FUSE_NOTIFY_DELETE as i32,
                    unique: 0,
                };

                let delete_out = fuse_notify_delete_out {
                    parent: *parent,
                    child: *child,
                    namelen: name.len() as _,
                    _padding: 0,
                };

                // TODO should I add null at the end?
                self.sender.send2(&out_header, delete_out.as_bytes()).await;
            }

            NotifyKind::Store {
                inode,
                offset,
                data,
            } => {
                let out_header = fuse_out_header {
                    len: (FUSE_OUT_HEADER_SIZE + FUSE_NOTIFY_STORE_OUT_SIZE) as u32,
                    error: fuse_notify_code::FUSE_NOTIFY_STORE as i32,
                    unique: 0,
                };

                let store_out = fuse_notify_store_out {
                    nodeid: *inode,
                    offset: *offset,
                    size: data.len() as _,
                    _padding: 0,
                };

                self.sender.send2(&out_header, store_out.as_bytes()).await;
            }

            NotifyKind::Retrieve {
                notify_unique,
                inode,
                offset,
                size,
            } => {
                let out_header = fuse_out_header {
                    len: (FUSE_OUT_HEADER_SIZE + FUSE_NOTIFY_RETRIEVE_OUT_SIZE) as u32,
                    error: fuse_notify_code::FUSE_NOTIFY_RETRIEVE as i32,
                    unique: 0,
                };

                let retrieve_out = fuse_notify_retrieve_out {
                    notify_unique: *notify_unique,
                    nodeid: *inode,
                    offset: *offset,
                    size: *size,
                    _padding: 0,
                };

                self.sender
                    .send2(&out_header, retrieve_out.as_bytes())
                    .await;
            }
        };
    }

    /// try to notify kernel the IO is ready, kernel can wakeup the waiting program.
    pub async fn wakeup(mut self, kh: u64) {
        self.notify(NotifyKind::Wakeup { kh }).await;
    }

    /// try to notify the cache invalidation about an inode.
    pub async fn invalid_inode(mut self, inode: u64, offset: i64, len: i64) {
        self.notify(NotifyKind::InvalidInode { inode, offset, len })
            .await;
    }

    /// try to notify the invalidation about a directory entry.
    pub async fn invalid_entry(mut self, parent: u64, name: OsString) {
        self.notify(NotifyKind::InvalidEntry { parent, name }).await;
    }

    /// try to notify a directory entry has been deleted.
    pub async fn delete(mut self, parent: u64, child: u64, name: OsString) {
        self.notify(NotifyKind::Delete {
            parent,
            child,
            name,
        })
        .await;
    }

    /// try to push the data in an inode for updating the kernel cache.
    pub async fn store(mut self, inode: u64, offset: u64, mut data: impl Buf) {
        self.notify(NotifyKind::Store {
            inode,
            offset,
            data: data.copy_to_bytes(data.remaining()),
        })
        .await;
    }

    /// try to retrieve data in an inode from the kernel cache.
    pub async fn retrieve(mut self, notify_unique: u64, inode: u64, offset: u64, size: u32) {
        self.notify(NotifyKind::Retrieve {
            notify_unique,
            inode,
            offset,
            size,
        })
        .await;
    }
}

#[derive(Debug)]
/// the kind of notify.
enum NotifyKind {
    /// notify the IO is ready.
    Wakeup { kh: u64 },

    // TODO need check is right or not
    /// notify the cache invalidation about an inode.
    InvalidInode { inode: u64, offset: i64, len: i64 },

    /// notify the invalidation about a directory entry.
    InvalidEntry { parent: u64, name: OsString },

    /// notify a directory entry has been deleted.
    Delete {
        parent: u64,
        child: u64,
        name: OsString,
    },

    /// push the data in an inode for updating the kernel cache.
    Store {
        inode: u64,
        offset: u64,
        data: Bytes,
    },

    /// retrieve data in an inode from the kernel cache.
    Retrieve {
        notify_unique: u64,
        inode: u64,
        offset: u64,
        size: u32,
    },
}

#[cfg(all(test, feature = "tokio-runtime", target_os = "linux"))]
mod checked_tests {
    use super::*;
    use std::fs::File;
    use std::io::Read;
    use std::os::fd::OwnedFd;
    use std::os::unix::net::UnixStream;

    #[tokio::test]
    async fn checked_invalidation_reports_the_actual_write_result() {
        for nonblocking in [false, true] {
            let (socket, mut peer) = UnixStream::pair().unwrap();
            peer.set_read_timeout(Some(std::time::Duration::from_secs(1)))
                .unwrap();
            socket.set_nonblocking(nonblocking).unwrap();
            let fd: OwnedFd = socket.into();
            let sender = ResponseSender::for_test(File::from(fd), nonblocking).unwrap();
            Notify::new(sender)
                .try_invalid_inode(77, 0, 0)
                .await
                .unwrap();
            let mut packet = [0; 40];
            peer.read_exact(&mut packet).unwrap();
            assert_eq!(u32::from_ne_bytes(packet[..4].try_into().unwrap()), 40);
            assert_eq!(
                i32::from_ne_bytes(packet[4..8].try_into().unwrap()),
                fuse_notify_code::FUSE_NOTIFY_INVAL_INODE as i32
            );
            assert_eq!(u64::from_ne_bytes(packet[8..16].try_into().unwrap()), 0);
            assert_eq!(u64::from_ne_bytes(packet[16..24].try_into().unwrap()), 77);
            assert_eq!(&packet[24..], &[0; 16]);

            let (socket, _peer) = UnixStream::pair().unwrap();
            socket.shutdown(std::net::Shutdown::Write).unwrap();
            socket.set_nonblocking(nonblocking).unwrap();
            let fd: OwnedFd = socket.into();
            let sender = ResponseSender::for_test(File::from(fd), nonblocking).unwrap();
            assert!(
                Notify::new(sender)
                    .try_invalid_inode(77, 0, 0)
                    .await
                    .is_err()
            );
        }
    }
}
