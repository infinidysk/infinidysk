//! yEnc decoding, encoding and CRC32 backed by the rapidyenc SIMD kernels.
#![allow(unsafe_code)] // FFI to the vendored rapidyenc C library.

use std::ffi::{c_int, c_void};
use std::sync::Once;

mod ffi {
    use std::ffi::{c_int, c_void};

    unsafe extern "C" {
        pub fn rapidyenc_encode_init();
        pub fn rapidyenc_encode_max_length(length: usize, line_size: c_int) -> usize;
        pub fn rapidyenc_encode_ex(
            line_size: c_int,
            column: *mut c_int,
            src: *const c_void,
            dest: *mut c_void,
            src_length: usize,
            is_end: c_int,
        ) -> usize;
        pub fn rapidyenc_decode_init();
        pub fn rapidyenc_decode_ex(
            is_raw: c_int,
            src: *const c_void,
            dest: *mut c_void,
            src_length: usize,
            state: *mut c_int,
        ) -> usize;
        pub fn rapidyenc_decode_incremental(
            src: *mut *const c_void,
            dest: *mut *mut c_void,
            src_length: usize,
            state: *mut c_int,
        ) -> c_int;
        pub fn rapidyenc_crc_init();
        pub fn rapidyenc_crc(src: *const c_void, src_length: usize, init_crc: u32) -> u32;
    }
}

/// Selects the CPU-specific kernels once; every entry point calls this first.
fn init() {
    static INIT: Once = Once::new();
    INIT.call_once(|| {
        // SAFETY: the init functions take no arguments and only fill rapidyenc's
        // internal dispatch tables; `Once` guarantees they run exactly once.
        unsafe {
            ffi::rapidyenc_encode_init();
            ffi::rapidyenc_decode_init();
            ffi::rapidyenc_crc_init();
        }
    });
}

/// Decoder line state carried across chunks of one article body.
///
/// Start each article with [`DecoderState::default`].
#[derive(Debug, Default, Clone, Copy, PartialEq, Eq)]
pub struct DecoderState(c_int);

/// Decodes yEnc data from `src` into `dst` and returns the number of bytes written.
///
/// With `raw`, NNTP dot-stuffing is undone too. `state` carries escape and
/// line-start context so a body can be decoded in arbitrary chunks.
///
/// # Panics
///
/// If `dst` is shorter than `src` (decoded output never exceeds the input).
pub fn decode(raw: bool, src: &[u8], dst: &mut [u8], state: &mut DecoderState) -> usize {
    assert!(dst.len() >= src.len(), "yEnc decode buffer too small");
    init();
    // SAFETY: `src` is valid for `src.len()` reads, `dst` for at least that many
    // writes (asserted above), they cannot overlap (`&` vs `&mut`), and `state`
    // points to a live `c_int` holding a value rapidyenc produced or 0 (CRLF).
    unsafe {
        ffi::rapidyenc_decode_ex(
            c_int::from(raw),
            src.as_ptr().cast::<c_void>(),
            dst.as_mut_ptr().cast::<c_void>(),
            src.len(),
            &raw mut state.0,
        )
    }
}

/// Where [`decode_incremental`] stopped.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum DecodeEnd {
    /// Input exhausted (or `dst` spare capacity filled) without an end sequence.
    None,
    /// `\r\n=y` found; input was consumed through the `y`.
    Control,
    /// `\r\n.\r\n` found; input was consumed through the final `\n`.
    Article,
}

/// Decodes dot-stuffed yEnc from `src` into the spare capacity of `dst`, stopping at
/// a `=y` control line or the NNTP terminator.
///
/// Consumes at most `dst.capacity() - dst.len()` bytes, so reserve before calling.
/// Returns the number of `src` bytes consumed.
pub fn decode_incremental(
    src: &[u8],
    dst: &mut Vec<u8>,
    state: &mut DecoderState,
) -> (usize, DecodeEnd) {
    init();
    let len = src.len().min(dst.capacity() - dst.len());
    let src_start = src.as_ptr();
    let dst_start = dst.spare_capacity_mut().as_mut_ptr().cast::<u8>();
    let mut src_ptr = src_start.cast::<c_void>();
    let mut dst_ptr = dst_start.cast::<c_void>();
    // SAFETY: `src` is valid for `len` reads and the spare capacity for `len` writes
    // (decoded output never exceeds input); the buffers cannot overlap; `state` is
    // a live `c_int` rapidyenc produced or 0.
    let end = unsafe {
        ffi::rapidyenc_decode_incremental(&raw mut src_ptr, &raw mut dst_ptr, len, &raw mut state.0)
    };
    // SAFETY: rapidyenc only advances both pointers within the ranges given above,
    // and every byte between `dst_start` and `dst_ptr` was written by it.
    let (consumed, written) = unsafe {
        let consumed = src_ptr.cast::<u8>().offset_from_unsigned(src_start);
        let written = dst_ptr.cast::<u8>().offset_from_unsigned(dst_start);
        dst.set_len(dst.len() + written);
        (consumed, written)
    };
    debug_assert!(written <= consumed);
    let end = match end {
        1 => DecodeEnd::Control,
        2 => DecodeEnd::Article,
        _ => DecodeEnd::None,
    };
    (consumed, end)
}

/// Encodes `src` as yEnc body lines of `line_size` characters separated by CRLF.
///
/// Output has no `=ybegin`/`=yend` headers and no trailing CRLF.
pub fn encode(src: &[u8], line_size: u16) -> Vec<u8> {
    init();
    let line_size = c_int::from(line_size);
    // SAFETY: pure arithmetic on its arguments.
    let max = unsafe { ffi::rapidyenc_encode_max_length(src.len(), line_size) };
    let mut out = vec![0u8; max];
    let mut column: c_int = 0;
    // SAFETY: `src` is valid for `src.len()` reads, `out` holds the maximum
    // encoded length rapidyenc reported for this input, and `column` is live.
    let written = unsafe {
        ffi::rapidyenc_encode_ex(
            line_size,
            &raw mut column,
            src.as_ptr().cast::<c_void>(),
            out.as_mut_ptr().cast::<c_void>(),
            src.len(),
            1,
        )
    };
    out.truncate(written);
    out
}

/// Continues a standard CRC-32 (IEEE, as used by yEnc and PAR2) over `data`.
///
/// Pass `0` as `crc` for the first chunk.
pub fn crc32(crc: u32, data: &[u8]) -> u32 {
    init();
    // SAFETY: `data` is valid for `data.len()` reads.
    unsafe { ffi::rapidyenc_crc(data.as_ptr().cast::<c_void>(), data.len(), crc) }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn crc32_matches_check_value() {
        assert_eq!(crc32(0, b"123456789"), 0xCBF4_3926);
        assert_eq!(crc32(crc32(0, b"1234"), b"56789"), 0xCBF4_3926);
    }

    #[test]
    fn decode_carries_state_across_chunk_splits() {
        let data: Vec<u8> = (0..=255u8).cycle().take(2048).collect();
        let encoded = encode(&data, 128);
        for split in 0..=encoded.len() {
            let (a, b) = encoded.split_at(split);
            let mut out = vec![0; encoded.len()];
            let mut state = DecoderState::default();
            let n = decode(false, a, &mut out, &mut state);
            let n = n + decode(false, b, &mut out[n..], &mut state);
            assert_eq!(&out[..n], data, "split at {split}");
        }
    }

    #[test]
    fn incremental_decode_stops_at_control_and_terminator() {
        let data: Vec<u8> = (0..=255u8).cycle().take(1000).collect();
        let wire = [encode(&data, 128).as_slice(), b"\r\n=yend\r\n"].concat();
        let mut out = Vec::with_capacity(wire.len());
        let mut state = DecoderState::default();
        let mut pos = 0;
        let end = loop {
            let chunk = &wire[pos..(pos + 7).min(wire.len())];
            let (used, end) = decode_incremental(chunk, &mut out, &mut state);
            pos += used;
            if end != DecodeEnd::None {
                break end;
            }
        };
        assert_eq!((end, &wire[pos..]), (DecodeEnd::Control, &b"end\r\n"[..]));
        assert_eq!(out, data);

        let mut state = DecoderState::default();
        let (used, end) = decode_incremental(b".\r\nrest", &mut out, &mut state);
        assert_eq!((used, end), (3, DecodeEnd::Article));
    }

    #[test]
    fn raw_decode_undoes_dot_stuffing() {
        let mut out = [0; 3];
        let n = decode(true, b"..*", &mut out, &mut DecoderState::default());
        assert_eq!(&out[..n], [b'.'.wrapping_sub(42), 0]);
    }
}
