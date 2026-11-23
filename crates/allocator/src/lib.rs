//! Optional maintenance for the system allocator. Does not replace the allocator.

/// Whether this target can return unused glibc heap pages to the OS.
pub const SUPPORTED: bool = cfg!(all(target_os = "linux", target_env = "gnu"));

/// Asks glibc to return free pages. True means some memory was released, not how much.
#[cfg(all(target_os = "linux", target_env = "gnu"))]
#[allow(unsafe_code, reason = "FFI boundary for glibc allocator maintenance")]
#[must_use]
pub fn trim() -> bool {
    unsafe extern "C" {
        fn malloc_trim(pad: usize) -> std::ffi::c_int;
    }
    // SAFETY: malloc_trim is thread-safe, takes a size_t, and has no caller preconditions.
    unsafe { malloc_trim(0) != 0 }
}

/// No allocator operation is available on this target.
#[cfg(not(all(target_os = "linux", target_env = "gnu")))]
#[must_use]
pub const fn trim() -> bool {
    false
}
