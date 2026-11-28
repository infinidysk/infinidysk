//! Paired throughput of rapidyenc and the Rust codec on one machine (issue 1966).
//!
//! `cargo run --release -p infinidysk-yenc --example paired`
//!
//! Each row times rapidyenc, rapidyenc again, and the Rust codec, in turn, seven times,
//! and prints the median of each. The second rapidyenc column shows the noise. A row
//! passes when the Rust median is not below the lower rapidyenc median.
#![allow(unsafe_code)] // Asks rapidyenc which kernels it chose.

use std::ffi::c_int;
use std::hint::black_box;
use std::time::Instant;

use infinidysk_yenc::{self as rapidyenc, DecodeEnd, DecoderState, rust};

unsafe extern "C" {
    fn rapidyenc_decode_kernel() -> c_int;
    fn rapidyenc_crc_kernel() -> c_int;
}

const ROUNDS: usize = 7;
/// About 700 KB, the usual article size.
const ARTICLE: usize = 716_800;

type Decode = fn(&[u8], &mut Vec<u8>, &mut DecoderState) -> (usize, DecodeEnd);
type Crc = fn(u32, &[u8]) -> u32;

/// `SplitMix64` with the benchmark corpus seed.
fn payload(len: usize) -> Vec<u8> {
    let mut state = 1025u64;
    let mut data = Vec::with_capacity(len + 8);
    while data.len() < len {
        state = state.wrapping_add(0x9E37_79B9_7F4A_7C15);
        let mut z = state;
        z = (z ^ (z >> 30)).wrapping_mul(0xBF58_476D_1CE4_E5B9);
        z = (z ^ (z >> 27)).wrapping_mul(0x94D0_49BB_1331_11EB);
        data.extend_from_slice(&(z ^ (z >> 31)).to_le_bytes());
    }
    data.truncate(len);
    data
}

/// The NNTP wire form: dot-stuffed lines, then `=yend` and the terminator.
fn wire_body(encoded: &[u8]) -> Vec<u8> {
    let mut wire = Vec::with_capacity(encoded.len() + 64);
    let mut line_start = true;
    for &byte in encoded {
        if line_start && byte == b'.' {
            wire.push(b'.');
        }
        wire.push(byte);
        line_start = byte == b'\n';
    }
    wire.extend_from_slice(b"\r\n=yend size=1 part=1 pcrc32=00000000\r\n.\r\n");
    wire
}

/// The BODY loop of the NNTP client: decode each read, then continue the CRC over the
/// new bytes when a CRC function is given.
fn body(decode: Decode, crc: Option<Crc>, wire: &[u8], read: usize, out: &mut Vec<u8>) -> u32 {
    out.clear();
    let mut state = DecoderState::default();
    let mut sum = 0;
    for chunk in wire.chunks(read) {
        let before = out.len();
        let (_, end) = decode(chunk, out, &mut state);
        if let Some(crc) = crc {
            sum = crc(sum, &out[before..]);
        }
        if end != DecodeEnd::None {
            break;
        }
    }
    sum
}

/// Times the three columns in turn and prints their medians in MB/s of `bytes`.
/// Returns whether the row passes.
fn row(
    title: &str,
    bytes: usize,
    iterations: usize,
    gated: bool,
    mut columns: [&mut dyn FnMut(); 3],
) -> bool {
    let megabytes = f64::from(u32::try_from(bytes * iterations).expect("below 4 GB")) / 1e6;
    let mut samples: [Vec<f64>; 3] = Default::default();
    for _ in 0..ROUNDS {
        for (column, run) in samples.iter_mut().zip(columns.iter_mut()) {
            let started = Instant::now();
            for _ in 0..iterations {
                run();
            }
            column.push(megabytes / started.elapsed().as_secs_f64());
        }
    }
    let [first, second, rust] = samples.map(|mut column| {
        column.sort_by(f64::total_cmp);
        column[ROUNDS / 2]
    });
    let pass = rust >= first.min(second);
    println!(
        "| {title} | {first:.0} | {second:.0} | {rust:.0} | {:.1}% | {} |",
        100.0 * rust / first,
        match (gated, pass) {
            (false, _) => "not gated",
            (true, true) => "pass",
            (true, false) => "FAIL",
        },
    );
    pass
}

/// One decode row: the payload must come back, then the three columns are timed.
fn decode_row(title: &str, payload: &[u8], wire: &[u8], read: usize, with_crc: bool) -> bool {
    let (native_crc, rust_crc): (Option<Crc>, Option<Crc>) = if with_crc {
        (Some(rapidyenc::crc32), Some(rust::crc32))
    } else {
        (None, None)
    };
    let mut out = Vec::with_capacity(wire.len());
    for (decode, crc) in [
        (rapidyenc::decode_incremental as Decode, native_crc),
        (rust::decode_incremental as Decode, rust_crc),
    ] {
        let sum = body(decode, crc, wire, read, &mut out);
        assert!(out == payload, "{title}: wrong output");
        assert!(
            crc.is_none() || sum == rapidyenc::crc32(0, payload),
            "{title}: wrong CRC"
        );
    }
    let (mut a, mut b, mut c) = (
        Vec::with_capacity(wire.len()),
        Vec::with_capacity(wire.len()),
        out,
    );
    let iterations = (ARTICLE * 300 / payload.len()).max(1);
    row(
        title,
        payload.len(),
        iterations,
        true,
        [
            &mut || {
                black_box(body(
                    rapidyenc::decode_incremental,
                    native_crc,
                    black_box(wire),
                    read,
                    &mut a,
                ));
            },
            &mut || {
                black_box(body(
                    rapidyenc::decode_incremental,
                    native_crc,
                    black_box(wire),
                    read,
                    &mut b,
                ));
            },
            &mut || {
                black_box(body(
                    rust::decode_incremental,
                    rust_crc,
                    black_box(wire),
                    read,
                    &mut c,
                ));
            },
        ],
    )
}

/// One CRC row over `data` in continuation chunks.
fn crc_row(title: &str, data: &[u8], chunk: usize, gated: bool) -> bool {
    let sum = |crc: Crc| data.chunks(chunk).fold(0, crc);
    assert_eq!(
        sum(rust::crc32),
        sum(rapidyenc::crc32),
        "{title}: wrong CRC"
    );
    row(
        title,
        data.len(),
        2000,
        gated,
        [
            &mut || {
                black_box(sum(rapidyenc::crc32));
            },
            &mut || {
                black_box(sum(rapidyenc::crc32));
            },
            &mut || {
                black_box(sum(rust::crc32));
            },
        ],
    )
}

fn main() {
    let article = payload(ARTICLE);
    let wire = wire_body(&rapidyenc::encode(&article, 128));
    // SAFETY: plain getters, called after `encode` above initialised rapidyenc.
    let (decode_kernel, crc_kernel) =
        unsafe { (rapidyenc_decode_kernel(), rapidyenc_crc_kernel()) };
    println!(
        "{} {}: rapidyenc decode kernel {decode_kernel:#x}, CRC kernel {crc_kernel:#x}; Rust decode kernel {}",
        std::env::consts::OS,
        std::env::consts::ARCH,
        rust::kernel(),
    );
    println!();
    println!("| Row | rapidyenc MB/s | rapidyenc again | Rust MB/s | Rust / rapidyenc | Gate |");
    println!("|---|---:|---:|---:|---:|---|");

    let mut failed = Vec::new();
    let mut gate = |title: &str, pass: bool| {
        if !pass {
            failed.push(title.to_owned());
        }
    };

    for (title, read) in [
        ("article, 128 KiB reads", 128 * 1024),
        ("article, 16 KiB reads", 16 * 1024),
        ("article, 4 KiB reads", 4096),
        ("article, 1 KiB reads", 1024),
        ("article, 1460-byte reads", 1460),
    ] {
        gate(title, decode_row(title, &article, &wire, read, false));
    }
    let title = "article, decode then CRC, 128 KiB reads";
    gate(title, decode_row(title, &article, &wire, 128 * 1024, true));

    // The speed-test inputs of node-yencode, and a stuffed dot on every line.
    let plain = vec![0u8; ARTICLE];
    let escaped = vec![214u8; ARTICLE];
    let dots = vec![4u8; ARTICLE];
    let mut twice = article.clone();
    for byte in &mut twice {
        if (64..192).contains(byte) {
            *byte = byte.wrapping_add(128);
        }
    }
    let small = &article[..4096];
    for (title, data) in [
        ("no escapes, line breaks only", &plain[..]),
        ("every byte escaped", &escaped[..]),
        ("twice the escape rate", &twice[..]),
        ("4 KiB article", small),
    ] {
        let wire = wire_body(&rapidyenc::encode(data, 128));
        gate(title, decode_row(title, data, &wire, 128 * 1024, false));
    }
    // rapidyenc's encoder escapes a leading dot; many posters do not, and NNTP stuffs it.
    let title = "every line starts with a stuffed dot";
    let stuffed = [&b".."[..], &[b'.'; 127], b"\r\n"]
        .concat()
        .repeat(ARTICLE / 128);
    let stuffed = [&stuffed[..], b"=yend\r\n.\r\n"].concat();
    gate(
        title,
        decode_row(
            title,
            &dots[..ARTICLE / 128 * 128],
            &stuffed,
            128 * 1024,
            false,
        ),
    );

    for (title, chunk) in [
        ("CRC-32, 128 KiB chunks", 128 * 1024),
        ("CRC-32, 64 KiB chunks", 65_536),
    ] {
        gate(title, crc_row(title, &article, chunk, true));
    }
    // The per-call cost on short buffers, as archive headers are. Speed does not matter there.
    crc_row("CRC-32, 32-byte chunks", &article, 32, false);

    println!();
    if failed.is_empty() {
        println!("GATE: pass");
    } else {
        println!("GATE: FAIL in {}", failed.join("; "));
        std::process::exit(1);
    }
}
