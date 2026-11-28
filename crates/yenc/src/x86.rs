//! The SSSE3 and AVX2 kernels (`x86_64`). `block.rs` holds the parts every kernel shares.
#![allow(clippy::cast_possible_truncation)] // 64-bit only: `u64` to `usize` keeps every bit.

use core::arch::x86_64::{
    __m128i, __m256i, _mm_add_epi8, _mm_alignr_epi8, _mm_and_si128, _mm_cmpeq_epi8,
    _mm_cvtsi64_si128, _mm_loadu_si128, _mm_min_epu8, _mm_movemask_epi8, _mm_or_si128,
    _mm_set_epi8, _mm_set_epi64x, _mm_set1_epi8, _mm_set1_epi64x, _mm_setzero_si128,
    _mm_shuffle_epi8, _mm_storeu_si128, _mm_sub_epi8, _mm256_add_epi8, _mm256_alignr_epi8,
    _mm256_and_si256, _mm256_broadcastsi128_si256, _mm256_castsi256_si128, _mm256_cmpeq_epi8,
    _mm256_extracti128_si256, _mm256_loadu_si256, _mm256_min_epu8, _mm256_movemask_epi8,
    _mm256_permute2x128_si256, _mm256_set1_epi8, _mm256_setzero_si256, _mm256_shuffle_epi8,
    _mm256_storeu_si256, _mm256_sub_epi8,
};

use crate::block::{BLOCK, Kernel, Lut, NEED, byte_popcounts, stuffed_dots};

pub(crate) static SSSE3: Kernel = Kernel {
    blocks: blocks_ssse3,
    tail,
};

pub(crate) static AVX2: Kernel = Kernel {
    blocks: blocks_avx2,
    tail,
};

/// rapidyenc's lookup for `=`, CR and LF: entry `min(x, 15)` equals `x` only for those.
#[inline]
#[target_feature(enable = "ssse3")]
fn special_table() -> __m128i {
    let (eq, cr, lf) = (b'='.cast_signed(), b'\r'.cast_signed(), b'\n'.cast_signed());
    _mm_set_epi8(
        eq, -1, cr, -1, -1, lf, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1,
    )
}

/// The bit mask of four byte masks, lowest lane first.
#[inline]
#[target_feature(enable = "ssse3")]
fn bitmask(lanes: [__m128i; 4]) -> u64 {
    let [a, b, c, d] = lanes.map(|lane| u64::from(_mm_movemask_epi8(lane).cast_unsigned()));
    a | b << 16 | c << 32 | d << 48
}

/// Turns a 64-bit mask into four byte masks, one byte per bit.
#[inline]
#[target_feature(enable = "ssse3")]
fn expand_mask(mask: u64) -> [__m128i; 4] {
    const ONES: i64 = 0x0101_0101_0101_0101;
    let bits = _mm_set1_epi64x(0x8040_2010_0804_0201u64.cast_signed());
    let packed = _mm_cvtsi64_si128(mask.cast_signed());
    [0, 2, 4, 6].map(|lane| {
        let spread = _mm_shuffle_epi8(packed, _mm_set_epi64x(ONES * (lane + 1), ONES * lane));
        _mm_cmpeq_epi8(_mm_and_si128(spread, bits), bits)
    })
}

/// Writes the bytes of `decoded` whose bit in `mask` is clear and returns the new end of the
/// output.
///
/// SAFETY: `dst` is writable for 16 bytes past the output position of each 16-byte group.
#[inline]
#[target_feature(enable = "ssse3")]
unsafe fn compact_block(
    mut dst: *mut u8,
    lut: Lut,
    mut mask: u64,
    decoded: [__m128i; 4],
) -> *mut u8 {
    // Bytes kept in each group of 16, in every second byte.
    let mut kept = 0x0808_0808_0808_0808u64 - byte_popcounts(mask);
    kept += kept >> 8;
    for group in decoded {
        // SAFETY: the index is below 32768, and the caller guarantees the store. A byte
        // the shuffle keeps past the new end is overwritten by the next group.
        unsafe {
            let shuffle = _mm_loadu_si128(lut.add((mask & 0x7fff) as usize).cast());
            _mm_storeu_si128(dst.cast(), _mm_shuffle_epi8(group, shuffle));
            dst = dst.add((kept & 0xff) as usize);
        }
        kept >>= 16;
        mask >>= 16;
    }
    dst
}

/// Decodes one block from its bit masks: `remove` marks the bytes to delete, `equals` the
/// `=` bytes, and `shifted` is `equals` moved up by one bit with the escape carry in bit 0.
/// Slower than the loops in `blocks_*`, but right for every block, including one where an
/// escaped byte is itself `=`, CR or LF. Returns the new end of the output and whether the
/// last byte is an unescaped `=`.
///
/// SAFETY: `src` is readable for 64 bytes, and `dst` is writable for 16 bytes past the
/// output position of each 16-byte group.
#[cold]
#[inline(never)]
#[target_feature(enable = "ssse3")]
unsafe fn block_from_masks(
    src: *const u8,
    dst: *mut u8,
    lut: Lut,
    remove: u64,
    equals: u64,
    shifted: u64,
) -> (*mut u8, bool) {
    const EVEN: u64 = 0x5555_5555_5555_5555;
    // In a run of `=`, every second one is escaped by the one before it. Adding the run
    // starts on even bits carries through each run, which marks the `=` that escape.
    let starts = equals & !shifted;
    let escapes = (equals.wrapping_add(starts & EVEN) ^ EVEN) & equals;
    let escaped = (escapes << 1) | (shifted & 1);
    let mask = remove & !escaped;
    let quoted = expand_mask(escaped);
    let (v42, v64) = (_mm_set1_epi8(42), _mm_set1_epi8(64));
    // SAFETY: the caller guarantees the 64-byte read and passes on the store guarantee.
    unsafe {
        let decoded = [0, 1, 2, 3].map(|lane| {
            let input = _mm_loadu_si128(src.add(16 * lane).cast());
            _mm_sub_epi8(input, _mm_add_epi8(v42, _mm_and_si128(quoted[lane], v64)))
        });
        (compact_block(dst, lut, mask, decoded), escapes >> 63 != 0)
    }
}

/// See [`Kernel::blocks`].
#[target_feature(enable = "ssse3")]
unsafe fn blocks_ssse3(src: *const u8, len: usize, dst: *mut u8, lut: Lut) -> (usize, usize, bool) {
    let table = special_table();
    let fifteen = _mm_set1_epi8(15);
    let (v42, v64, veq) = (
        _mm_set1_epi8(42),
        _mm_set1_epi8(64),
        _mm_set1_epi8(b'='.cast_signed()),
    );
    let mut carry_eq = _mm_setzero_si128();
    // Line-start candidates that fall on bytes 0 and 1 of the next block.
    let mut carry_starts = 0u64;
    let mut esc = false;
    let mut pos = 0;
    let mut out = dst;
    'blocks: while pos + NEED <= len {
        // SAFETY: the loop condition keeps the block and the byte after it inside `src`.
        // The output is never longer than the input, so every store ends inside the first
        // `pos + 64` bytes of `dst`. A line-start candidate has its two earlier bytes in
        // this block, or in the block before it when it is carried over.
        unsafe {
            let block = src.add(pos);
            let a = _mm_loadu_si128(block.cast());
            let b = _mm_loadu_si128(block.add(16).cast());
            let c = _mm_loadu_si128(block.add(32).cast());
            let d = _mm_loadu_si128(block.add(48).cast());
            // `=`, CR and LF.
            let sp_a = _mm_cmpeq_epi8(a, _mm_shuffle_epi8(table, _mm_min_epu8(a, fifteen)));
            let sp_b = _mm_cmpeq_epi8(b, _mm_shuffle_epi8(table, _mm_min_epu8(b, fifteen)));
            let sp_c = _mm_cmpeq_epi8(c, _mm_shuffle_epi8(table, _mm_min_epu8(c, fifteen)));
            let sp_d = _mm_cmpeq_epi8(d, _mm_shuffle_epi8(table, _mm_min_epu8(d, fifteen)));
            // The vector test stays last in the condition: computed before the two
            // scalar tests, the loop was about 7% slower on aarch64.
            if !esc
                && carry_starts == 0
                && _mm_movemask_epi8(_mm_or_si128(
                    _mm_or_si128(sp_a, sp_b),
                    _mm_or_si128(sp_c, sp_d),
                )) == 0
            {
                _mm_storeu_si128(out.cast(), _mm_sub_epi8(a, v42));
                _mm_storeu_si128(out.add(16).cast(), _mm_sub_epi8(b, v42));
                _mm_storeu_si128(out.add(32).cast(), _mm_sub_epi8(c, v42));
                _mm_storeu_si128(out.add(48).cast(), _mm_sub_epi8(d, v42));
                out = out.add(BLOCK);
                pos += BLOCK;
                continue;
            }
            let eq_a = _mm_cmpeq_epi8(a, veq);
            let eq_b = _mm_cmpeq_epi8(b, veq);
            let eq_c = _mm_cmpeq_epi8(c, veq);
            let eq_d = _mm_cmpeq_epi8(d, veq);
            let mask = bitmask([sp_a, sp_b, sp_c, sp_d]);
            let mask_eq = bitmask([eq_a, eq_b, eq_c, eq_d]);
            // The byte two after a CR or LF may start a line.
            let breaks = mask & !mask_eq;
            let mut starts = (breaks << 2) | carry_starts;
            let mut dots = 0u64;
            while starts != 0 {
                let at = starts.trailing_zeros() as usize;
                let first = *block.add(at);
                if first == b'.' || (first == b'=' && *block.add(at + 1) == b'y') {
                    match stuffed_dots(block, starts, false) {
                        Some(found) => dots = found,
                        None => break 'blocks,
                    }
                    break;
                }
                starts &= starts - 1;
            }
            carry_starts = breaks >> 62;
            let shifted = (mask_eq << 1) | u64::from(esc);
            if mask & shifted != 0 {
                (out, esc) = block_from_masks(block, out, lut, mask | dots, mask_eq, shifted);
                carry_eq = _mm_set1_epi8(-i8::from(esc));
                pos += BLOCK;
                continue;
            }
            // Take 42 off every byte, and 64 more off the byte after an `=`.
            let after_a = _mm_alignr_epi8::<15>(eq_a, carry_eq);
            let after_b = _mm_alignr_epi8::<15>(eq_b, eq_a);
            let after_c = _mm_alignr_epi8::<15>(eq_c, eq_b);
            let after_d = _mm_alignr_epi8::<15>(eq_d, eq_c);
            let out_a = _mm_sub_epi8(a, _mm_add_epi8(v42, _mm_and_si128(after_a, v64)));
            let out_b = _mm_sub_epi8(b, _mm_add_epi8(v42, _mm_and_si128(after_b, v64)));
            let out_c = _mm_sub_epi8(c, _mm_add_epi8(v42, _mm_and_si128(after_c, v64)));
            let out_d = _mm_sub_epi8(d, _mm_add_epi8(v42, _mm_and_si128(after_d, v64)));
            carry_eq = eq_d;
            esc = mask_eq >> 63 != 0;
            out = compact_block(out, lut, mask | dots, [out_a, out_b, out_c, out_d]);
            pos += BLOCK;
        }
    }
    // SAFETY: `out` started at `dst` and only moved forward inside the same buffer.
    (pos, unsafe { out.offset_from_unsigned(dst) }, esc)
}

/// See [`Kernel::blocks`]. The same loop as `blocks_ssse3` with 32-byte compares.
#[target_feature(enable = "avx2")]
unsafe fn blocks_avx2(src: *const u8, len: usize, dst: *mut u8, lut: Lut) -> (usize, usize, bool) {
    let table = _mm256_broadcastsi128_si256(special_table());
    let fifteen = _mm256_set1_epi8(15);
    let (v42, v64) = (_mm256_set1_epi8(42), _mm256_set1_epi8(64));
    let veq = _mm256_set1_epi8(b'='.cast_signed());
    let wide = |lane: __m256i| u64::from(_mm256_movemask_epi8(lane).cast_unsigned());
    let mut carry_eq = _mm256_setzero_si256();
    // Line-start candidates that fall on bytes 0 and 1 of the next block.
    let mut carry_starts = 0u64;
    let mut esc = false;
    let mut pos = 0;
    let mut out = dst;
    'blocks: while pos + NEED <= len {
        // SAFETY: the loop condition keeps the block and the byte after it inside `src`.
        // The output is never longer than the input, so every store ends inside the first
        // `pos + 64` bytes of `dst`. A line-start candidate has its two earlier bytes in
        // this block, or in the block before it when it is carried over.
        unsafe {
            let block = src.add(pos);
            let a = _mm256_loadu_si256(block.cast());
            let b = _mm256_loadu_si256(block.add(32).cast());
            // `=`, CR and LF.
            let sp_a =
                _mm256_cmpeq_epi8(a, _mm256_shuffle_epi8(table, _mm256_min_epu8(a, fifteen)));
            let sp_b =
                _mm256_cmpeq_epi8(b, _mm256_shuffle_epi8(table, _mm256_min_epu8(b, fifteen)));
            let mask = wide(sp_a) | wide(sp_b) << 32;
            if !esc && carry_starts == 0 && mask == 0 {
                _mm256_storeu_si256(out.cast(), _mm256_sub_epi8(a, v42));
                _mm256_storeu_si256(out.add(32).cast(), _mm256_sub_epi8(b, v42));
                out = out.add(BLOCK);
                pos += BLOCK;
                continue;
            }
            let eq_a = _mm256_cmpeq_epi8(a, veq);
            let eq_b = _mm256_cmpeq_epi8(b, veq);
            let mask_eq = wide(eq_a) | wide(eq_b) << 32;
            // The byte two after a CR or LF may start a line.
            let breaks = mask & !mask_eq;
            let mut starts = (breaks << 2) | carry_starts;
            let mut dots = 0u64;
            while starts != 0 {
                let at = starts.trailing_zeros() as usize;
                let first = *block.add(at);
                if first == b'.' || (first == b'=' && *block.add(at + 1) == b'y') {
                    match stuffed_dots(block, starts, false) {
                        Some(found) => dots = found,
                        None => break 'blocks,
                    }
                    break;
                }
                starts &= starts - 1;
            }
            carry_starts = breaks >> 62;
            let shifted = (mask_eq << 1) | u64::from(esc);
            if mask & shifted != 0 {
                (out, esc) = block_from_masks(block, out, lut, mask | dots, mask_eq, shifted);
                carry_eq = _mm256_set1_epi8(-i8::from(esc));
                pos += BLOCK;
                continue;
            }
            // Take 42 off every byte, and 64 more off the byte after an `=`. The byte
            // before each 16-byte half comes from the half below it.
            let below_a = _mm256_permute2x128_si256::<0x21>(carry_eq, eq_a);
            let below_b = _mm256_permute2x128_si256::<0x21>(eq_a, eq_b);
            let after_a = _mm256_alignr_epi8::<15>(eq_a, below_a);
            let after_b = _mm256_alignr_epi8::<15>(eq_b, below_b);
            let out_a = _mm256_sub_epi8(a, _mm256_add_epi8(v42, _mm256_and_si256(after_a, v64)));
            let out_b = _mm256_sub_epi8(b, _mm256_add_epi8(v42, _mm256_and_si256(after_b, v64)));
            carry_eq = eq_b;
            esc = mask_eq >> 63 != 0;
            let decoded = [
                _mm256_castsi256_si128(out_a),
                _mm256_extracti128_si256::<1>(out_a),
                _mm256_castsi256_si128(out_b),
                _mm256_extracti128_si256::<1>(out_b),
            ];
            out = compact_block(out, lut, mask | dots, decoded);
            pos += BLOCK;
        }
    }
    // SAFETY: `out` started at `dst` and only moved forward inside the same buffer.
    (pos, unsafe { out.offset_from_unsigned(dst) }, esc)
}

/// See [`Kernel::tail`]. Both kernels use it: it runs once per call.
#[inline(never)]
#[target_feature(enable = "ssse3")]
unsafe fn tail(window: *const u8, skip: usize, dst: *mut u8, lut: Lut) -> Option<(usize, bool)> {
    let table = special_table();
    let fifteen = _mm_set1_epi8(15);
    let veq = _mm_set1_epi8(b'='.cast_signed());
    // SAFETY: the caller guarantees the 64-byte read.
    let input = [0, 16, 32, 48].map(|at| unsafe { _mm_loadu_si128(window.add(at).cast()) });
    let special =
        input.map(|x| _mm_cmpeq_epi8(x, _mm_shuffle_epi8(table, _mm_min_epu8(x, fifteen))));
    let eq = input.map(|x| _mm_cmpeq_epi8(x, veq));
    // The first `skip` bytes were consumed before this call reached the tail.
    let live = !0u64 << skip;
    let mask = bitmask(special) & live;
    let mask_eq = bitmask(eq) & live;
    // SAFETY: a candidate is two bytes after a CR or LF of the tail, so it is inside the
    // window with two window bytes before it. The window ends where the call ends.
    let dots = unsafe { stuffed_dots(window, (mask & !mask_eq) << 2, true) }?;
    // Deleting the skipped bytes too leaves only the tail's output. Each group's store
    // then ends inside the tail's length, or inside 16 bytes for a shorter tail.
    let remove = mask | dots | !live;
    // SAFETY: the caller guarantees the read, the table and `max(64 - skip, 16)` writable
    // bytes, which the stores above stay inside. The end pointer comes from `dst`.
    unsafe {
        let (end, esc) = block_from_masks(window, dst, lut, remove, mask_eq, mask_eq << 1);
        Some((end.offset_from_unsigned(dst), esc))
    }
}
