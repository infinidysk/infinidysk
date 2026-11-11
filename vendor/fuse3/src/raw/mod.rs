//! inode based
//!
//! it is not recommend to use this inode based [`Filesystem`] first, you need to handle inode
//! allocate, recycle and sometimes map to the path, [`PathFilesystem`][crate::path::PathFilesystem]
//! helps you do those jobs so you can pay more attention to your filesystem design. However if you
//! want to control the inode or do the path<->inode map on yourself, [`Filesystem`] is the only one
//! choose.

pub use filesystem::Filesystem;
pub use request::Request;
#[cfg(any(feature = "async-io-runtime", feature = "tokio-runtime"))]
pub use session::{MountHandle, Session};

pub(crate) mod abi;
mod connection;
mod filesystem;
pub mod flags;
pub mod reply;
mod request;
pub(crate) mod session;

pub mod prelude {
    pub use super::Filesystem;
    pub use super::Request;
    pub use super::Session;
    pub use super::reply::FileAttr;
    pub use super::reply::*;
    pub use crate::FileType;
    pub use crate::SetAttr;
    pub use crate::notify::Notify;
}
