use std::mem;

use nix::sys::stat::mode_t;

use crate::FileType;

pub trait Apply: Sized {
    fn apply<F>(mut self, f: F) -> Self
    where
        F: FnOnce(&mut Self),
    {
        f(&mut self);
        self
    }
}

impl<T> Apply for T {}

#[inline]
pub fn get_first_null_position(data: impl AsRef<[u8]>) -> Option<usize> {
    data.as_ref().iter().position(|char| *char == 0)
}

// Some platforms like Linux x86_64 have mode_t = u32, and lint warns of a trivial_numeric_casts.
// But others like macOS x86_64 have mode_t = u16, requiring a typecast. So, just silence lint.
#[cfg(target_os = "linux")]
#[allow(trivial_numeric_casts)]
/// returns the mode for a given file kind and permission
pub const fn mode_from_kind_and_perm(kind: FileType, perm: u16) -> u32 {
    kind.const_into_mode_t() | perm as mode_t
}

// Some platforms like Linux x86_64 have mode_t = u32, and lint warns of a trivial_numeric_casts.
// But others like macOS x86_64 have mode_t = u16, requiring a typecast. So, just silence lint.
#[cfg(all(
    not(target_os = "linux"),
    any(target_os = "freebsd", target_os = "macos")
))]
#[allow(trivial_numeric_casts)]
/// returns the mode for a given file kind and permission
pub const fn mode_from_kind_and_perm(kind: FileType, perm: u16) -> u32 {
    (kind.const_into_mode_t() | perm as mode_t) as u32
}

/// returns the permission for a given file kind and mode
#[allow(clippy::unnecessary_cast)] // Not unnecessary on all platforms.
pub const fn perm_from_mode_and_kind(kind: FileType, mode: mode_t) -> u16 {
    (mode ^ kind.const_into_mode_t()) as u16
}

#[inline]
pub const fn get_padding_size(dir_entry_size: usize) -> usize {
    // 64bit align
    let entry_size = (dir_entry_size + mem::size_of::<u64>() - 1) & !(mem::size_of::<u64>() - 1);

    entry_size - dir_entry_size
}

/// whether a directory entry of `dir_entry_size` bytes, with its alignment
/// padding, still fits into a reply of `max_size` bytes that holds `used` bytes
#[inline]
pub const fn dir_entry_fits(used: usize, dir_entry_size: usize, max_size: usize) -> bool {
    used + dir_entry_size + get_padding_size(dir_entry_size) <= max_size
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::raw::abi::{FUSE_DIRENT_SIZE, FUSE_DIRENTPLUS_SIZE};

    /// how many entries with 3-byte names one reply of `max_size` bytes takes,
    /// and how many bytes they use
    fn pack(header_size: usize, max_size: usize) -> (usize, usize) {
        let dir_entry_size = header_size + 3;
        let (mut count, mut used) = (0, 0);

        while dir_entry_fits(used, dir_entry_size, max_size) {
            used += dir_entry_size + get_padding_size(dir_entry_size);
            count += 1;
        }

        (count, used)
    }

    #[test]
    fn padded_entries_never_pass_the_reply_size() {
        for header_size in [FUSE_DIRENT_SIZE, FUSE_DIRENTPLUS_SIZE] {
            let padded = header_size + 3 + get_padding_size(header_size + 3);

            // exactly 26 padded entries fit
            assert_eq!(pack(header_size, 26 * padded), (26, 26 * padded));
            // one byte less, or only room for the last entry without its padding
            let short = 26 * padded - get_padding_size(header_size + 3);
            assert_eq!(pack(header_size, short), (25, 25 * padded));
            assert_eq!(pack(header_size, 26 * padded - 1), (25, 25 * padded));
        }

        // the reported case: direntplus entries with 3-byte names and 4155 bytes
        assert_eq!(pack(FUSE_DIRENTPLUS_SIZE, 4155), (25, 4000));
    }
}
