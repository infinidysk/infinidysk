//! Scalar yEnc: the reference decoder, the fallback for CPUs without a kernel, and the encoder.

use crate::{DecodeEnd, DecoderState};

// Line states. The numbers are rapidyenc's, so a carried `DecoderState` keeps its meaning.

/// At the start of a line: after CRLF, or at the start of the body.
pub(crate) const LINE: i32 = 0;
/// After an `=`: the next byte is escaped.
pub(crate) const ESC: i32 = 1;
/// After a CR.
pub(crate) const CR: i32 = 2;
/// Inside a line.
pub(crate) const DATA: i32 = 3;
/// After a `.` at the start of a line.
pub(crate) const DOT: i32 = 4;
/// After `.\r` at the start of a line.
pub(crate) const DOT_CR: i32 = 5;
/// After an `=` at the start of a line: a `y` makes it a control line.
pub(crate) const LINE_EQ: i32 = 6;

/// Decodes one byte of a dot-stuffed body. `out` must have spare capacity for one byte.
#[inline]
pub(crate) fn step(byte: u8, out: &mut Vec<u8>, state: &mut DecoderState) -> DecodeEnd {
    let prev = state.0;
    if prev == LINE_EQ && byte == b'y' {
        state.0 = DATA;
        return DecodeEnd::Control;
    }
    if matches!(prev, ESC | LINE_EQ) {
        out.push(byte.wrapping_sub(106));
        state.0 = if byte == b'\r' { CR } else { DATA };
        return DecodeEnd::None;
    }
    if prev == DOT_CR && byte == b'\n' {
        state.0 = LINE;
        return DecodeEnd::Article;
    }
    state.0 = match (prev, byte) {
        (CR, b'\n') => LINE,
        (LINE, b'.') => DOT,
        (DOT, b'\r') => DOT_CR,
        (LINE | DOT, b'=') => LINE_EQ,
        (_, b'=') => ESC,
        (_, b'\r') => CR,
        (_, b'\n') => DATA,
        _ => {
            out.push(byte.wrapping_sub(42));
            DATA
        }
    };
    DecodeEnd::None
}

/// Decodes `src` byte by byte and stops after an end marker. The caller caps `src` to the
/// spare capacity of `out`.
pub(crate) fn decode_incremental(
    src: &[u8],
    out: &mut Vec<u8>,
    state: &mut DecoderState,
) -> (usize, DecodeEnd) {
    for (index, &byte) in src.iter().enumerate() {
        let end = step(byte, out, state);
        if end != DecodeEnd::None {
            return (index + 1, end);
        }
    }
    (src.len(), DecodeEnd::None)
}

/// Decodes `src` without looking for an end marker. `dst` must be at least as long as `src`.
pub(crate) fn decode(raw: bool, src: &[u8], dst: &mut [u8], state: &mut DecoderState) -> usize {
    if src.is_empty() {
        return 0;
    }
    // rapidyenc reports no output for a raw chunk that is only the CR, or CRLF, after a
    // carried escape. Kept so results stay identical.
    let carried_cr_only = raw && state.0 == ESC && (src == b"\r" || src == b"\r\n");
    let mut phase = match state.0 {
        ESC => ESC,
        LINE | CR if raw => state.0,
        _ => DATA,
    };
    let mut written = 0;
    for &byte in src {
        phase = match (phase, byte) {
            (ESC, _) => {
                dst[written] = byte.wrapping_sub(106);
                written += 1;
                if raw && byte == b'\r' { CR } else { DATA }
            }
            (CR, b'\n') if raw => LINE,
            (LINE, b'.') if raw => DATA,
            (_, b'=') => ESC,
            (_, b'\r') if raw => CR,
            (_, b'\r' | b'\n') => DATA,
            _ => {
                dst[written] = byte.wrapping_sub(42);
                written += 1;
                DATA
            }
        };
    }
    state.0 = phase;
    if carried_cr_only { 0 } else { written }
}

/// Encodes `src` as yEnc lines of `line_size` characters separated by CRLF.
pub(crate) fn encode(src: &[u8], line_size: u16) -> Vec<u8> {
    let width = usize::from(line_size);
    let mut out = Vec::with_capacity(src.len() + src.len() / 32 + 2);
    let mut column = 0;
    for (index, &byte) in src.iter().enumerate() {
        if column != 0 && column >= width {
            out.extend_from_slice(b"\r\n");
            column = 0;
        }
        let wire = byte.wrapping_add(42);
        let whitespace = matches!(wire, b' ' | b'\t');
        let escape = matches!(wire, 0 | b'\r' | b'\n' | b'=')
            || (column == 0 && wire == b'.')
            || (whitespace && (column == 0 || column + 1 == width || index + 1 == src.len()));
        if escape {
            out.extend_from_slice(&[b'=', wire.wrapping_add(64)]);
            column += 2;
        } else {
            out.push(wire);
            column += 1;
        }
    }
    out
}
