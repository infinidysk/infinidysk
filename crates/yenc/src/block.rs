//! The 64-byte block decoder that the SIMD kernels share.
//!
//! A kernel removes `=`, CR and LF inside the vector and compacts what is left, as
//! rapidyenc does. This file holds what is the same on every CPU: the compaction table, the
//! line-start checks and the loop that hands blocks to a kernel.

use std::sync::LazyLock;

use crate::scalar::{self, CR, DATA, DOT, ESC, LINE, LINE_EQ};
use crate::{DecodeEnd, DecoderState};

pub(crate) const BLOCK: usize = 64;
/// One block plus the byte after it, which a line-start `=` or `.` at the end of the block
/// needs.
pub(crate) const NEED: usize = BLOCK + 1;
/// Shortest tail decoded as a block instead of by scalar steps.
const MIN_TAIL: usize = 8;

/// rapidyenc's compaction table, 32768 entries: for a 15-bit delete mask, the shuffle that
/// moves the bytes to keep to the front.
pub(crate) type Lut = *const [u8; 16];
type BlocksFn = unsafe fn(*const u8, usize, *mut u8, Lut) -> (usize, usize, bool);
type TailFn = unsafe fn(*const u8, usize, *mut u8, Lut) -> Option<(usize, bool)>;

/// The block routines of one CPU feature level.
pub(crate) struct Kernel {
    /// Decodes whole blocks starting in the DATA state, while a block and its look-ahead
    /// byte remain. Returns the input consumed (a multiple of 64), the output written, and
    /// whether the last consumed byte was an unescaped `=`. Stops early at a block that
    /// scalar code must decide.
    ///
    /// SAFETY: `src` is readable for `len` bytes, `dst` is writable for `len` bytes, and
    /// the CPU supports the kernel.
    pub(crate) blocks: BlocksFn,
    /// Decodes the last `64 - skip` bytes of a call, which start in the DATA state, from
    /// the 64-byte window that ends where the call ends. Returns the output written and
    /// the escape carry, or `None` when scalar code must decide.
    ///
    /// SAFETY: `window` is readable for 64 bytes, `dst` is writable for `max(64 - skip, 16)`
    /// bytes, `skip` is below 64, and the CPU supports the kernel.
    pub(crate) tail: TailFn,
}

/// The kernels this CPU supports with their names, fastest first.
pub(crate) fn available() -> Vec<(&'static str, &'static Kernel)> {
    #[cfg(target_arch = "x86_64")]
    let found = {
        use std::arch::is_x86_feature_detected as has;
        let kernels = [
            ("avx2", has!("avx2"), &crate::x86::AVX2),
            ("ssse3", has!("ssse3"), &crate::x86::SSSE3),
        ];
        let supported = kernels.into_iter().filter(|kernel| kernel.1);
        supported.map(|(name, _, kernel)| (name, kernel)).collect()
    };
    #[cfg(target_arch = "aarch64")]
    let found = vec![("neon", &crate::neon::NEON)];
    #[cfg(not(any(target_arch = "x86_64", target_arch = "aarch64")))]
    let found = Vec::new();
    found
}

/// The kernel `decode_incremental` uses, or `None` for the scalar decoder.
pub(crate) fn best() -> Option<&'static Kernel> {
    static BEST: LazyLock<Option<&'static Kernel>> =
        LazyLock::new(|| available().first().map(|&(_, kernel)| kernel));
    *BEST
}

/// The table behind [`Lut`], built on first use.
static LUT: LazyLock<Box<[[u8; 16]]>> = LazyLock::new(|| {
    let mut table = vec![[0x80u8; 16]; 1 << 15].into_boxed_slice();
    for (mask, entry) in table.iter_mut().enumerate() {
        let mut kept = 0;
        for bit in 0..16u8 {
            if mask & (1 << bit) == 0 {
                entry[kept] = bit;
                kept += 1;
            }
        }
    }
    table
});

/// The population count of each byte of `mask`, in place.
pub(crate) const fn byte_popcounts(mask: u64) -> u64 {
    let pairs = mask - ((mask >> 1) & 0x5555_5555_5555_5555);
    let nibbles = (pairs & 0x3333_3333_3333_3333) + ((pairs >> 2) & 0x3333_3333_3333_3333);
    (nibbles + (nibbles >> 4)) & 0x0f0f_0f0f_0f0f_0f0f
}

/// Finds the stuffed dots among the line-start candidates in `starts`: bit `n` is set when
/// byte `n - 2` of the block is CR or LF. A stuffed dot is a `.` directly after CRLF.
/// Returns the dots to delete, or `None` when scalar code must decide: a possible `=y`
/// control line, or a `.` that may start the article terminator.
///
/// With `last_in_call`, the block ends where the call ends. Its last byte then has no
/// byte after it, and the state after the call lets the next call decide.
///
/// SAFETY: every candidate is inside the block and has two readable bytes before it. A
/// candidate also has one readable byte after it, unless `last_in_call` is set.
#[cold]
#[inline(never)]
pub(crate) unsafe fn stuffed_dots(
    block: *const u8,
    mut starts: u64,
    last_in_call: bool,
) -> Option<u64> {
    let mut dots = 0u64;
    while starts != 0 {
        let at = starts.trailing_zeros() as usize;
        let has_next = !(last_in_call && at + 1 == BLOCK);
        // SAFETY: the caller guarantees the bytes at `at - 2`, `at - 1` and `at`, and the
        // byte at `at + 1` when `has_next` is set.
        let (before, first, next) = unsafe {
            let here = block.add(at);
            let next = if has_next { *here.add(1) } else { 0 };
            ([*here.sub(2), *here.sub(1)], *here, next)
        };
        if first == b'=' && next == b'y' {
            return None;
        }
        if first == b'.' && before == *b"\r\n" {
            if matches!(next, b'\r' | b'=') {
                return None;
            }
            dots |= 1 << at;
        }
        starts &= starts - 1;
    }
    Some(dots)
}

/// The state after a kernel run, from the escape carry and the last three bytes consumed.
fn exit_state(esc: bool, last: &[u8]) -> i32 {
    match (esc, last) {
        (true, [b'\r', b'\n', _]) => LINE_EQ,
        (true, _) => ESC,
        (false, [_, b'\r', b'\n']) => LINE,
        (false, [_, _, b'\r']) => CR,
        (false, [b'\r', b'\n', b'.']) => DOT,
        (false, _) => DATA,
    }
}

/// Decodes dot-stuffed yEnc from `src` into the spare capacity of `dst` with `kernel`, or
/// with the scalar decoder when there is none.
pub(crate) fn decode_incremental(
    kernel: Option<&Kernel>,
    src: &[u8],
    dst: &mut Vec<u8>,
    state: &mut DecoderState,
) -> (usize, DecodeEnd) {
    // Like rapidyenc, consume no more input than the spare output capacity.
    let src = &src[..src.len().min(dst.capacity() - dst.len())];
    let Some(kernel) = kernel else {
        return scalar::decode_incremental(src, dst, state);
    };
    let lut = LUT.as_ptr();
    let mut used = 0;
    loop {
        // The kernels start in the DATA state.
        while used < src.len() && state.0 != DATA {
            let end = scalar::step(src[used], dst, state);
            used += 1;
            if end != DecodeEnd::None {
                return (used, end);
            }
        }
        let rest = src.len() - used;
        if rest < NEED {
            // The last bytes of the call have no look-ahead byte, so `blocks` cannot take
            // them. Decode them from the window that ends where the call ends.
            let spare = dst.capacity() - dst.len();
            if rest >= MIN_TAIL && src.len() >= BLOCK && spare >= rest.max(16) {
                // SAFETY: the window is the last 64 bytes of `src`, `rest` is 1..=64 so
                // the skip is below 64, the spare capacity was checked above, and the
                // kernel came from `available`.
                let done = unsafe {
                    let window = src.as_ptr().add(src.len() - BLOCK);
                    (kernel.tail)(window, BLOCK - rest, dst.as_mut_ptr().add(dst.len()), lut)
                };
                if let Some((written, esc)) = done {
                    // SAFETY: the kernel initialised `written` bytes of spare capacity.
                    unsafe { dst.set_len(dst.len() + written) };
                    used = src.len();
                    state.0 = exit_state(esc, &src[used - 3..]);
                }
            }
            break;
        }
        // SAFETY: `src[used..]` is readable for `rest` bytes. The cap on `src` leaves at
        // least `rest` bytes of spare capacity, because the output so far is no longer
        // than the input so far. The kernel came from `available`.
        let (consumed, written, esc) = unsafe {
            (kernel.blocks)(
                src.as_ptr().add(used),
                rest,
                dst.as_mut_ptr().add(dst.len()),
                lut,
            )
        };
        // SAFETY: the kernel initialised `written` bytes of spare capacity.
        unsafe { dst.set_len(dst.len() + written) };
        if consumed > 0 {
            used += consumed;
            state.0 = exit_state(esc, &src[used - 3..used]);
        }
        // With a block and its look-ahead byte still unread, the kernel stopped at a
        // block it would not take. Scalar code decodes that block.
        if src.len() - used >= NEED {
            let (more, end) = scalar::decode_incremental(&src[used..used + BLOCK], dst, state);
            used += more;
            if end != DecodeEnd::None {
                return (used, end);
            }
        }
    }
    let (more, end) = scalar::decode_incremental(&src[used..], dst, state);
    (used + more, end)
}
