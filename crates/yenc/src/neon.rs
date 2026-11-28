//! The NEON kernel (aarch64). `block.rs` holds the parts every kernel shares.
#![allow(clippy::cast_possible_truncation)] // 64-bit only: `u64` to `usize` keeps every bit.

use core::arch::aarch64::{
    uint8x16_t, vaddq_u8, vandq_u8, vbslq_u8, vceqq_u8, vcnt_u8, vdup_n_u64, vdupq_n_u8,
    vdupq_n_u64, vextq_u8, vget_lane_u64, vget_low_u8, vgetq_lane_u64, vld1q_u8, vld1q_u8_x4,
    vmaxvq_u8, vorrq_u8, vpaddq_u8, vqtbl1q_u8, vqtbx1q_u8, vreinterpret_u8_u64,
    vreinterpret_u64_u8, vreinterpretq_u8_u64, vreinterpretq_u64_u8, vst1q_u8, vsubq_u8, vtstq_u8,
};

use crate::block::{BLOCK, Kernel, Lut, NEED, byte_popcounts, stuffed_dots};

pub(crate) static NEON: Kernel = Kernel { blocks, tail };

/// One bit per byte lane, to turn compare results into bit masks.
const BITS: [u8; 16] = [1, 2, 4, 8, 16, 32, 64, 128, 1, 2, 4, 8, 16, 32, 64, 128];
/// 0xff at the indexes of LF and CR, for a table lookup that leaves other bytes alone.
const CRLF: [u8; 16] = [0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 255, 0, 0, 255, 0, 0];

/// Turns a 64-bit mask into four byte masks, one byte per bit.
#[inline]
#[target_feature(enable = "neon")]
fn expand_mask(mask: u64) -> [uint8x16_t; 4] {
    let bits = vreinterpretq_u8_u64(vdupq_n_u64(0x8040_2010_0804_0201));
    let packed = vreinterpretq_u8_u64(vdupq_n_u64(mask));
    let indices = vextq_u8::<8>(vdupq_n_u8(0), vdupq_n_u8(1));
    [0, 2, 4, 6].map(|lane| {
        vtstq_u8(
            vqtbl1q_u8(packed, vaddq_u8(indices, vdupq_n_u8(lane))),
            bits,
        )
    })
}

/// Writes the bytes of `decoded` whose bit in `mask` is clear and returns the new end of the
/// output. `deleted` holds the population count of each byte of `mask`.
///
/// SAFETY: `dst` is writable for 16 bytes past the output position of each 16-byte group.
#[inline]
#[target_feature(enable = "neon")]
unsafe fn compact_block(
    mut dst: *mut u8,
    lut: Lut,
    mut mask: u64,
    deleted: u64,
    decoded: [uint8x16_t; 4],
) -> *mut u8 {
    // Bytes kept in each group of 16, in every second byte.
    let mut kept = 0x0808_0808_0808_0808u64 - deleted;
    kept += kept >> 8;
    for group in decoded {
        // SAFETY: the index is below 32768, and the caller guarantees the store. A byte
        // the shuffle keeps past the new end is overwritten by the next group.
        unsafe {
            let shuffle = vld1q_u8(lut.add((mask & 0x7fff) as usize).cast());
            vst1q_u8(dst, vqtbl1q_u8(group, shuffle));
            dst = dst.add((kept & 0xff) as usize);
        }
        kept >>= 16;
        mask >>= 16;
    }
    dst
}

/// Decodes one block from its bit masks: `remove` marks the bytes to delete, `equals` the
/// `=` bytes, and `shifted` is `equals` moved up by one bit with the escape carry in bit 0.
/// Slower than the loop in `blocks`, but right for every block, including one where an
/// escaped byte is itself `=`, CR or LF. Returns the new end of the output and whether the
/// last byte is an unescaped `=`.
///
/// SAFETY: `src` is readable for 64 bytes, and `dst` is writable for 16 bytes past the
/// output position of each 16-byte group.
#[cold]
#[inline(never)]
#[target_feature(enable = "neon")]
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
    let deleted = vget_lane_u64::<0>(vreinterpret_u64_u8(vcnt_u8(vreinterpret_u8_u64(
        vdup_n_u64(mask),
    ))));
    // SAFETY: the caller guarantees the 64-byte read and passes on the store guarantee.
    unsafe {
        let input = vld1q_u8_x4(src);
        let (plain, escape) = (vdupq_n_u8(42), vdupq_n_u8(106));
        let decoded = [
            vsubq_u8(input.0, vbslq_u8(quoted[0], escape, plain)),
            vsubq_u8(input.1, vbslq_u8(quoted[1], escape, plain)),
            vsubq_u8(input.2, vbslq_u8(quoted[2], escape, plain)),
            vsubq_u8(input.3, vbslq_u8(quoted[3], escape, plain)),
        ];
        (
            compact_block(dst, lut, mask, deleted, decoded),
            escapes >> 63 != 0,
        )
    }
}

/// See [`Kernel::blocks`].
#[target_feature(enable = "neon")]
unsafe fn blocks(src: *const u8, len: usize, dst: *mut u8, lut: Lut) -> (usize, usize, bool) {
    let bits = load16(&BITS);
    let crlf = load16(&CRLF);
    let (v42, v106, veq) = (vdupq_n_u8(42), vdupq_n_u8(106), vdupq_n_u8(b'='));
    let mut carry_eq = vdupq_n_u8(0);
    // Line-start candidates that fall on bytes 0 and 1 of the next block.
    let mut carry_starts = 0u64;
    let mut esc = false;
    let mut pos = 0;
    let mut out = dst;
    macro_rules! bitmask {
        ($a:expr, $b:expr, $c:expr, $d:expr) => {
            vpaddq_u8(
                vpaddq_u8(vandq_u8($a, bits), vandq_u8($b, bits)),
                vpaddq_u8(vandq_u8($c, bits), vandq_u8($d, bits)),
            )
        };
    }
    'blocks: while pos + NEED <= len {
        // SAFETY: the loop condition keeps the block and the byte after it inside `src`.
        // The output is never longer than the input, so every store ends inside the first
        // `pos + 64` bytes of `dst`. A line-start candidate has its two earlier bytes in
        // this block, or in the block before it when it is carried over.
        unsafe {
            let block = src.add(pos);
            let a = vld1q_u8(block);
            let b = vld1q_u8(block.add(16));
            let c = vld1q_u8(block.add(32));
            let d = vld1q_u8(block.add(48));
            let eq_a = vceqq_u8(a, veq);
            let eq_b = vceqq_u8(b, veq);
            let eq_c = vceqq_u8(c, veq);
            let eq_d = vceqq_u8(d, veq);
            // `=`, CR and LF.
            let sp_a = vqtbx1q_u8(eq_a, crlf, a);
            let sp_b = vqtbx1q_u8(eq_b, crlf, b);
            let sp_c = vqtbx1q_u8(eq_c, crlf, c);
            let sp_d = vqtbx1q_u8(eq_d, crlf, d);
            // The vector test stays last in the condition: computed before the two
            // scalar tests, the loop was about 7% slower.
            if !esc
                && carry_starts == 0
                && vmaxvq_u8(vorrq_u8(vorrq_u8(sp_a, sp_b), vorrq_u8(sp_c, sp_d))) == 0
            {
                vst1q_u8(out, vsubq_u8(a, v42));
                vst1q_u8(out.add(16), vsubq_u8(b, v42));
                vst1q_u8(out.add(32), vsubq_u8(c, v42));
                vst1q_u8(out.add(48), vsubq_u8(d, v42));
                out = out.add(BLOCK);
                pos += BLOCK;
                continue;
            }
            let combined = vpaddq_u8(
                bitmask!(sp_a, sp_b, sp_c, sp_d),
                bitmask!(eq_a, eq_b, eq_c, eq_d),
            );
            let lanes = vreinterpretq_u64_u8(combined);
            let mut mask = vgetq_lane_u64::<0>(lanes);
            let mask_eq = vgetq_lane_u64::<1>(lanes);
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
                carry_eq = vdupq_n_u8(u8::from(esc).wrapping_neg());
                pos += BLOCK;
                continue;
            }
            // Take 42 off every byte, and 106 off the byte after an `=`.
            let out_a = vsubq_u8(a, vbslq_u8(vextq_u8::<15>(carry_eq, eq_a), v106, v42));
            let out_b = vsubq_u8(b, vbslq_u8(vextq_u8::<15>(eq_a, eq_b), v106, v42));
            let out_c = vsubq_u8(c, vbslq_u8(vextq_u8::<15>(eq_b, eq_c), v106, v42));
            let out_d = vsubq_u8(d, vbslq_u8(vextq_u8::<15>(eq_c, eq_d), v106, v42));
            carry_eq = eq_d;
            esc = mask_eq >> 63 != 0;
            let mut deleted =
                vget_lane_u64::<0>(vreinterpret_u64_u8(vcnt_u8(vget_low_u8(combined))));
            if dots != 0 {
                mask |= dots;
                deleted = byte_popcounts(mask);
            }
            out = compact_block(out, lut, mask, deleted, [out_a, out_b, out_c, out_d]);
            pos += BLOCK;
        }
    }
    // SAFETY: `out` started at `dst` and only moved forward inside the same buffer.
    (pos, unsafe { out.offset_from_unsigned(dst) }, esc)
}

/// See [`Kernel::tail`].
#[inline(never)]
#[target_feature(enable = "neon")]
unsafe fn tail(window: *const u8, skip: usize, dst: *mut u8, lut: Lut) -> Option<(usize, bool)> {
    let bits = load16(&BITS);
    let crlf = load16(&CRLF);
    let veq = vdupq_n_u8(b'=');
    // SAFETY: the caller guarantees the 64-byte read.
    let input = unsafe { vld1q_u8_x4(window) };
    let input = [input.0, input.1, input.2, input.3];
    let eq = input.map(|lane| vceqq_u8(lane, veq));
    let special = [0, 1, 2, 3].map(|lane| vqtbx1q_u8(eq[lane], crlf, input[lane]));
    let [special, eq] = [special, eq].map(|m| {
        vpaddq_u8(
            vpaddq_u8(vandq_u8(m[0], bits), vandq_u8(m[1], bits)),
            vpaddq_u8(vandq_u8(m[2], bits), vandq_u8(m[3], bits)),
        )
    });
    let lanes = vreinterpretq_u64_u8(vpaddq_u8(special, eq));
    // The first `skip` bytes were consumed before this call reached the tail.
    let live = !0u64 << skip;
    let mask = vgetq_lane_u64::<0>(lanes) & live;
    let mask_eq = vgetq_lane_u64::<1>(lanes) & live;
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

/// Loads a 16-byte constant.
#[inline]
#[target_feature(enable = "neon")]
fn load16(bytes: &[u8; 16]) -> uint8x16_t {
    // SAFETY: the reference is valid for 16 bytes.
    unsafe { vld1q_u8(bytes.as_ptr()) }
}
