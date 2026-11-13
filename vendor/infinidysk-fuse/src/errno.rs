use std::error::Error;
use std::fmt::{self, Display, Formatter};
use std::io::Error as IoError;
use std::os::raw::c_int;

use tracing::error;

#[derive(Debug, Copy, Clone, Eq, PartialEq)]
/// linux errno wrap.
pub struct Errno(c_int);

impl From<Errno> for c_int {
    fn from(errno: Errno) -> Self {
        -errno.0
    }
}

impl From<c_int> for Errno {
    fn from(errno: c_int) -> Self {
        Self(errno)
    }
}

/// When raw os error is undefined, will return Errno(libc::EIO)
impl From<IoError> for Errno {
    fn from(err: IoError) -> Self {
        if let Some(errno) = err.raw_os_error() {
            Self(errno)
        } else {
            Self(libc::EIO)
        }
    }
}

impl From<Errno> for IoError {
    fn from(errno: Errno) -> Self {
        IoError::from_raw_os_error(errno.0)
    }
}

impl Display for Errno {
    fn fmt(&self, f: &mut Formatter<'_>) -> fmt::Result {
        write!(f, "errno is {}", self.0)
    }
}

impl Errno {
    pub fn new_not_exist() -> Self {
        Self(libc::ENOENT)
    }

    pub fn new_exist() -> Self {
        Self(libc::EEXIST)
    }

    pub fn new_is_dir() -> Self {
        Self(libc::EISDIR)
    }

    pub fn new_is_not_dir() -> Self {
        Self(libc::ENOTDIR)
    }

    pub fn is_not_exist(&self) -> bool {
        self.0 == libc::ENOENT
    }

    pub fn is_exist(&self) -> bool {
        self.0 == libc::EEXIST
    }

    pub fn is_dir(&self) -> bool {
        self.0 == libc::EISDIR
    }

    pub fn is_not_dir(&self) -> bool {
        self.0 == libc::ENOTDIR
    }

    /// The `fuse_out_header.error` for this error. A value the kernel would reject becomes `-EIO`.
    pub(crate) fn reply_code(self) -> c_int {
        // Linux v6.12 fuse_dev_do_write rejects `error <= -ERESTARTSYS` (512) before matching the request.
        #[cfg(target_os = "linux")]
        const MAX_ERRNO: c_int = 511;
        // FreeBSD 15 fuse_device_write rejects `error > ELAST` with EINVAL.
        #[cfg(target_os = "freebsd")]
        const MAX_ERRNO: c_int = libc::ELAST;
        #[cfg(not(any(target_os = "linux", target_os = "freebsd")))]
        const MAX_ERRNO: c_int = c_int::MAX;

        if (1..=MAX_ERRNO).contains(&self.0) {
            -self.0
        } else {
            error!(
                errno = self.0,
                "filesystem returned an invalid errno, replying EIO"
            );

            -libc::EIO
        }
    }
}

impl Error for Errno {}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn reply_code_negates_valid_errno() {
        assert_eq!(Errno::from(libc::ENOENT).reply_code(), -libc::ENOENT);
        assert_eq!(Errno::from(1).reply_code(), -1);
    }

    #[test]
    fn invalid_errno_becomes_eio() {
        for errno in [0, -1, -libc::ENOENT, c_int::MIN] {
            assert_eq!(Errno::from(errno).reply_code(), -libc::EIO, "errno {errno}");
        }
    }

    #[cfg(target_os = "linux")]
    #[test]
    fn reply_code_respects_linux_range() {
        assert_eq!(Errno::from(511).reply_code(), -511);
        assert_eq!(Errno::from(512).reply_code(), -libc::EIO);
        assert_eq!(Errno::from(c_int::MAX).reply_code(), -libc::EIO);
    }

    #[cfg(target_os = "freebsd")]
    #[test]
    fn reply_code_respects_freebsd_range() {
        assert_eq!(Errno::from(libc::ELAST).reply_code(), -libc::ELAST);
        assert_eq!(Errno::from(libc::ELAST + 1).reply_code(), -libc::EIO);
        assert_eq!(Errno::from(1000).reply_code(), -libc::EIO);
    }
}
