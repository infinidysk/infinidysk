//! Compares the Rust codec with rapidyenc (issue 1966).
//!
//! Every decode call of every kernel is checked against rapidyenc and folded into a digest.
//! The digests in `GOLDEN` were recorded while rapidyenc was linked, so they keep its
//! results as the reference after it is removed.

use crate::block::{self, Kernel};
use crate::{DecoderState, rust};

/// `SplitMix64`: deterministic test data without a dependency.
struct Rng(u64);

impl Rng {
    fn next(&mut self) -> u64 {
        self.0 = self.0.wrapping_add(0x9E37_79B9_7F4A_7C15);
        let mut z = self.0;
        z = (z ^ (z >> 30)).wrapping_mul(0xBF58_476D_1CE4_E5B9);
        z = (z ^ (z >> 27)).wrapping_mul(0x94D0_49BB_1331_11EB);
        z ^ (z >> 31)
    }

    fn below(&mut self, n: usize) -> usize {
        usize::try_from(self.next() % u64::try_from(n).unwrap()).unwrap()
    }

    fn byte(&mut self) -> u8 {
        self.next().to_le_bytes()[0]
    }

    fn bytes(&mut self, len: usize) -> Vec<u8> {
        (0..len).map(|_| self.byte()).collect()
    }
}

/// FNV-1a.
fn fold(digest: &mut u64, bytes: &[u8]) {
    for &byte in bytes {
        *digest = (*digest ^ u64::from(byte)).wrapping_mul(0x0100_0000_01b3);
    }
}

/// yEnc lines from an encoder that does not escape a leading `.`, as many posters do.
fn encode_keeping_dots(src: &[u8], width: usize) -> Vec<u8> {
    let mut out = Vec::new();
    let mut column = 0;
    for (index, &byte) in src.iter().enumerate() {
        if column >= width {
            out.extend_from_slice(b"\r\n");
            column = 0;
        }
        let wire = byte.wrapping_add(42);
        let edge = column == 0 || column + 1 == width || index + 1 == src.len();
        if matches!(wire, 0 | b'\r' | b'\n' | b'=') || (matches!(wire, b' ' | b'\t') && edge) {
            out.extend_from_slice(&[b'=', wire.wrapping_add(64)]);
            column += 2;
        } else {
            out.push(wire);
            column += 1;
        }
    }
    out
}

/// The NNTP wire form: dot-stuffed lines, then `=yend` and the terminator.
fn wire_body(encoded: &[u8]) -> Vec<u8> {
    let mut wire = Vec::new();
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

/// Malformed but decodable: every byte sent as `==`, 128 characters per line.
fn doubled_wire(lines: usize) -> Vec<u8> {
    wire_body(&vec![b"==".repeat(64); lines].join(&b"\r\n"[..]))
}

/// Sequences the kernels must hand to scalar code or decode with care.
const INSERTS: &[&[u8]] = &[
    b"\r\n.",
    b"\r\n..",
    b"\r\n.\r\n",
    b"\r\n=y",
    b"=y",
    b"==",
    b"===",
    b"=\r",
    b"=\n",
    b"=\r\n",
    b"=\r\n.",
    b"=\r\n=y",
    b"\r",
    b"\n",
    b"\r\r\n",
    b".",
    b"\n.",
    b"\r.",
    b"\r\n=",
    b"\r\n=\r\n",
    b"\r\n.=y",
    b"\r\n.=",
    b"\r\n.\r",
    b"\r\n.\rx",
    b"\r\n=\r",
    b"\ry",
    b"\r\ny",
    b"=",
    b"y",
];

/// The bytes the decoder treats specially, as node-yencode's decoder test picks them.
const CHARSET: &[u8] = b"=\r\n.ay";

/// Mostly special bytes, with random bytes between them.
fn special_wire(rng: &mut Rng, len: usize) -> Vec<u8> {
    const ALPHA: &[u8] = b"=\r\n.y\r\n=.";
    (0..len)
        .map(|_| {
            if rng.below(10) < 4 {
                rng.byte()
            } else {
                ALPHA[rng.below(ALPHA.len())]
            }
        })
        .collect()
}

/// A slice of valid wire data with hostile sequences spliced in or written over it.
fn spliced(rng: &mut Rng, wire: &[u8]) -> Vec<u8> {
    let len = 2_000 + rng.below(12_000);
    let start = rng.below(wire.len() - len);
    let mut out = wire[start..start + len].to_vec();
    for _ in 0..rng.below(40) {
        let at = rng.below(out.len() + 1);
        let insert = INSERTS[rng.below(INSERTS.len())];
        if rng.below(3) == 0 {
            let end = (at + insert.len()).min(out.len());
            out[at..end].copy_from_slice(&insert[..end - at]);
        } else {
            out.splice(at..at, insert.iter().copied());
        }
    }
    out
}

/// Special bytes thinned with filler, so whole blocks reach the kernels.
fn charset_wire(rng: &mut Rng, len: usize, one_in: usize) -> Vec<u8> {
    (0..len)
        .map(|_| {
            if rng.below(one_in) == 0 {
                CHARSET[rng.below(CHARSET.len())]
            } else {
                1
            }
        })
        .collect()
}

/// Ordinary bytes broken by long runs of one special byte or by a hostile sequence.
fn runs_wire(rng: &mut Rng) -> Vec<u8> {
    let mut out = Vec::new();
    for _ in 0..rng.below(24) {
        let filler = [b'X', 1, b'y', 0xff][rng.below(4)];
        let ordinary = rng.below(200);
        out.resize(out.len() + ordinary, filler);
        if rng.below(2) == 0 {
            let run = 1 + rng.below(130);
            out.resize(out.len() + run, CHARSET[rng.below(CHARSET.len())]);
        } else {
            out.extend_from_slice(INSERTS[rng.below(INSERTS.len())]);
        }
    }
    out
}

/// The inputs of node-yencode's `test/testdec.js`, the decoder rapidyenc comes from.
fn upstream_cases() -> Vec<Vec<u8>> {
    // Its regression input for a past NEON decoder bug.
    const EXTRA_NULL: &str = "2e900a4fb6054c9126171cdc196dc41237bb1b76da9191aa5e85c1d2a2a5c638fe39054a210e8c799473cd510541fd118f3904b242a9938558c879238aae1d3bdab32e287cedb820b494f54ffae6dd0b13f73a4a9499df486a7845c612182bcef72a6e50a8e98351c35765d26c605115dc8c5c56a5e3f20ae6da8dcd78536e6d1601eb1fc3ddc774";
    let named: &[&[u8]] = &[
        &[],
        &[0, 1, 2, 10, 3, 61, 64],
        &[10],
        &[61],
        &[61, 13],
        &[61, 61],
        &[61, 61, 13],
        &[10, 61],
        &[13, 10, 46],
        &[46],
        &[13, 10, 46, 13, 10, 46],
        &[61, 13, 10, 46],
        b"\r\n.\r\n",
        b".\r\n",
        b"\r\n=y",
        b"=y",
        b"\r\n=y\r\n.\r\n",
        b"\r\n.\r\n=y",
        b"\r\n=abc",
        b"\r\n.=y",
        b"\r\n.=.",
        b"\r\n.\ra\n",
        b"\r\n..\r\n",
        b"\r\n.a=y",
        b"=\r\n.\r\n",
        b"=\r\n=y",
    ];
    let mut cases: Vec<Vec<u8>> = named.iter().map(|case| case.to_vec()).collect();
    cases.push(vec![0; 256]);
    cases.push(vec![b'='; 256]);
    cases.push(b"=@".repeat(128));
    cases.push(b"@=".repeat(128));
    cases.push(vec![10; 256]);
    cases.push(vec![223; 256]);
    cases.push(
        (0..EXTRA_NULL.len() / 2)
            .map(|i| u8::from_str_radix(&EXTRA_NULL[2 * i..2 * i + 2], 16).unwrap())
            .collect(),
    );
    cases
}

/// `front`, then `pre` filler bytes, the case, and `post` filler bytes.
fn padded(front: &[u8], pre: usize, case: &[u8], post: usize) -> Vec<u8> {
    let mut wire = front.to_vec();
    wire.resize(wire.len() + pre, 1);
    wire.extend_from_slice(case);
    wire.resize(wire.len() + post, 1);
    wire
}

/// One kernel under test (`None` is the scalar decoder) and the digest of its results.
struct Run {
    name: &'static str,
    kernel: Option<&'static Kernel>,
    digest: u64,
}

impl Run {
    /// Decodes `wire` in calls of the (input length, spare capacity) pairs `schedule`
    /// returns, and compares every call with rapidyenc.
    fn stream(&mut self, wire: &[u8], schedule: &mut dyn FnMut() -> (usize, usize)) {
        /// Bytes already in the output buffer, which the decoder must not touch.
        const PREFIX: [u8; 3] = [0xa5; 3];
        let name = self.name;
        let mut state = DecoderState::default();
        let mut reference_state = DecoderState::default();
        let mut pos = 0;
        let mut stalled = 0;
        while pos < wire.len() {
            let (width, spare) = schedule();
            // A schedule may return zeros; move on after a few calls without progress.
            let grow = usize::from(stalled > 2);
            let (width, spare) = (width.max(grow), spare.max(grow));
            let input = &wire[pos..(pos + width).min(wire.len())];
            let buffer = || {
                let mut buffer = Vec::with_capacity(PREFIX.len() + spare);
                buffer.extend_from_slice(&PREFIX);
                buffer
            };
            let (mut out, mut reference) = (buffer(), buffer());
            let capacity = out.capacity();
            assert_eq!(reference.capacity(), capacity);

            let (used, end) = block::decode_incremental(self.kernel, input, &mut out, &mut state);
            assert_eq!(out.capacity(), capacity, "{name} grew the output buffer");
            let expected = crate::decode_incremental(input, &mut reference, &mut reference_state);
            assert!(
                (used, end) == expected && out == reference && state == reference_state,
                "{name} differs from rapidyenc at input offset {pos}: {} bytes, {spare} spare",
                input.len(),
            );

            fold(
                &mut self.digest,
                &u64::try_from(used).unwrap().to_le_bytes(),
            );
            fold(&mut self.digest, &[end as u8]);
            fold(
                &mut self.digest,
                &u64::try_from(out.len()).unwrap().to_le_bytes(),
            );
            fold(&mut self.digest, &out);
            fold(&mut self.digest, &state.0.to_le_bytes());
            stalled = if used == 0 { stalled + 1 } else { 0 };
            pos += used;
        }
    }

    /// The whole input in one call.
    fn whole(&mut self, wire: &[u8]) {
        self.stream(wire, &mut || (wire.len(), wire.len()));
    }

    /// Random call lengths and spare capacities below `limit`.
    fn chunks(&mut self, wire: &[u8], rng: &mut Rng, limit: usize) {
        self.stream(wire, &mut || (rng.below(limit), rng.below(limit)));
    }

    /// `first` bytes in the first call, then the rest.
    fn split(&mut self, wire: &[u8], first: usize, spare: usize) {
        let mut widths = [first].into_iter();
        self.stream(wire, &mut || (widths.next().unwrap_or(wire.len()), spare));
    }
}

/// The digest of every section for one kernel.
fn sections(name: &'static str, kernel: Option<&'static Kernel>) -> Vec<(&'static str, u64)> {
    /// Carried states in front of a case; node-yencode's test leaves these as a TODO.
    const STATES: &[&[u8]] = &[
        b"=", b"\r", b"\r\n", b"\r\n.", b"\r\n=", b"\r\n.\r", b"x", b"x=", b"x\r", b"x\r\n",
        b"x\r\n.",
    ];
    const ENDINGS: &[&[u8]] = &[
        b"", b"=", b"==", b"\r", b"\r\n", b"\r\n.", b"\r\n..", b"\r\n=", b"\r\n.\r", b"\r\n.=",
        b"=\r", b"=\r\n", b"=\r\n.", b"\n.", b".",
    ];
    const NEXTS: &[&[u8]] = &[b"", b"\n", b"\r\n", b"y", b".", b"=y", b"\r\n.\r\n"];

    let mut rng = Rng(0x1966);
    let mut done = Vec::new();
    let mut run = Run {
        name,
        kernel,
        digest: 0,
    };
    let mut finish = |name, run: &mut Run| done.push((name, std::mem::take(&mut run.digest)));

    // Seed 1025, as the benchmark corpus uses.
    let corpus = Rng(1025).bytes(180_000);
    let article = wire_body(&crate::encode(&corpus, 128));
    let dotted = wire_body(&encode_keeping_dots(&corpus, 128));
    let all_dots = wire_body(&encode_keeping_dots(&vec![4; 40_000], 128));
    let third_dots: Vec<u8> = (0..60_000)
        .map(|_| if rng.below(10) < 3 { 4 } else { rng.byte() })
        .collect();
    let third_dots = wire_body(&encode_keeping_dots(&third_dots, 128));
    let doubled = doubled_wire(300);

    // Whole articles under every call shape, and every block alignment.
    for wire in [&article, &dotted, &all_dots, &third_dots, &doubled] {
        run.whole(wire);
        run.stream(wire, &mut || (128 * 1024, wire.len()));
        for limit in [3000, 3000, 3000, 200] {
            run.chunks(wire, &mut rng, limit);
        }
        for offset in 0..130 {
            run.whole(&wire[offset..offset + 10_000]);
        }
        for width in [65, 66, 127, 128, 129, 191, 192, 193, 256, 1024, 1460] {
            run.stream(wire, &mut || (width, 1 << 16));
        }
        run.stream(wire, &mut || (65 + rng.below(66), 65 + rng.below(66)));
        run.stream(wire, &mut || (1 << 17, 65 + rng.below(200)));
    }
    finish("articles", &mut run);

    // node-yencode pads each case with 0..48 bytes on both sides for 16-byte vectors. A
    // 64-byte block needs more.
    for case in &upstream_cases() {
        for pre in 0..145 {
            for post in 0..81 {
                run.whole(&padded(&[], pre, case, post));
            }
        }
        let wire = padded(&[], 70, case, 70);
        for first in 0..=wire.len() {
            run.split(&wire, first, 4096);
        }
        for state in STATES {
            for pre in 0..70 {
                for post in [0, 1, 2, 3, 62, 63, 64, 65, 66, 130] {
                    run.split(&padded(state, pre, case, post), state.len(), 4096);
                }
            }
        }
    }
    finish("upstream cases", &mut run);

    for _ in 0..800 {
        let base = if rng.below(2) == 0 { &article } else { &dotted };
        let wire = spliced(&mut rng, base);
        run.whole(&wire);
        run.chunks(&wire, &mut rng, 3000);
    }
    finish("spliced", &mut run);

    for _ in 0..1000 {
        let len = rng.below(3000);
        let wire = special_wire(&mut rng, len);
        run.whole(&wire);
        run.chunks(&wire, &mut rng, 700);
    }
    for one_in in [1, 2, 4, 8, 16, 32, 64] {
        for _ in 0..100 {
            let wire = charset_wire(&mut rng, 4096, one_in);
            run.whole(&wire);
            run.chunks(&wire, &mut rng, 700);
        }
    }
    for _ in 0..16 {
        let wire = rng.bytes(32 * 1024);
        run.whole(&wire);
        run.chunks(&wire, &mut rng, 6000);
        run.stream(&wire, &mut || (65 + rng.below(136), 65 + rng.below(136)));
    }
    finish("special bytes", &mut run);

    // Every split point of short hostile inputs around a stretch of valid data.
    for _ in 0..100 {
        let mut wire = special_wire(&mut rng, 24);
        wire.extend_from_slice(&article[1000..1200]);
        let tail = rng.below(24);
        wire.extend(special_wire(&mut rng, tail));
        for first in 0..=wire.len() {
            run.split(&wire, first, 4096);
        }
    }
    finish("split points", &mut run);

    // Every way a call can end, at every call length from one to three blocks, with the
    // spare capacity equal to the call or a little above it.
    for len in 64..=200 {
        for ending in ENDINGS {
            for next in NEXTS {
                let mut wire = article[1000..1000 + len - ending.len()].to_vec();
                wire.extend_from_slice(ending);
                wire.extend_from_slice(next);
                wire.extend_from_slice(&article[5000..5100]);
                for extra in [0, 1, 15, 16, 64] {
                    run.split(&wire, len, len + extra);
                }
            }
        }
    }
    finish("call endings", &mut run);

    // Random streams under random call shapes.
    for _ in 0..6000 {
        let wire = match rng.below(4) {
            0 => spliced(&mut rng, &dotted),
            1 => {
                let (len, one_in) = (rng.below(6000), 1 << rng.below(7));
                charset_wire(&mut rng, len, one_in)
            }
            2 => {
                let len = rng.below(5000);
                special_wire(&mut rng, len)
            }
            _ => runs_wire(&mut rng),
        };
        match rng.below(5) {
            0 => run.whole(&wire),
            1 => run.chunks(&wire, &mut rng, 3000),
            2 => run.stream(&wire, &mut || (65 + rng.below(136), 65 + rng.below(136))),
            3 => run.stream(&wire, &mut || (rng.below(140), 4096)),
            _ => run.stream(&wire, &mut || (1 << 15, rng.below(400))),
        }
    }
    finish("random streams", &mut run);
    done
}

/// Recorded with rapidyenc `d223ecc` linked. Its NEON, SSSE3 and AVX2 kernels gave the
/// same digests.
const GOLDEN: [(&str, u64); 7] = [
    ("articles", 0x8aaf_6efb_7cf2_d8e9),
    ("upstream cases", 0x8fa5_0203_3b48_f45f),
    ("spliced", 0xdc03_5c74_fc26_fa1d),
    ("special bytes", 0x8585_37bc_a33f_9a24),
    ("split points", 0x3bc4_c0eb_4fa7_f3d0),
    ("call endings", 0x1f45_285d_f26a_a141),
    ("random streams", 0x5376_5ca6_51a0_f926),
];
const ENCODE_GOLDEN: u64 = 0x2987_e8f3_537f_c4a5;
const DECODE_GOLDEN: u64 = 0x7fd1_a7bc_73f1_4dd5;
const CRC_GOLDEN: u32 = 0x9afb_632c;

#[test]
fn every_kernel_matches_rapidyenc() {
    let kernels = block::available().into_iter();
    let kernels = kernels.map(|(name, kernel)| (name, Some(kernel)));
    for (name, kernel) in kernels.chain([("scalar", None)]) {
        assert_eq!(sections(name, kernel), GOLDEN, "kernel {name}");
    }
}

#[test]
fn encode_matches_rapidyenc() {
    // Half the bytes need an escape always, or at the edge of a line.
    const WIRE: &[u8] = b"\0\r\n=. \t";
    let mut rng = Rng(0x1966);
    let mut digest = 0;
    for width in [1, 2, 3, 4, 63, 64, 127, 128, 129, 255, 256, 1000, u16::MAX] {
        for len in [0, 1, 2, 3, 127, 128, 129, 300, 5000] {
            let data: Vec<u8> = (0..len)
                .map(|_| {
                    if rng.below(2) == 0 {
                        WIRE[rng.below(WIRE.len())].wrapping_sub(42)
                    } else {
                        rng.byte()
                    }
                })
                .collect();
            let encoded = rust::encode(&data, width);
            assert!(
                encoded == crate::encode(&data, width),
                "{len} bytes at width {width}"
            );
            fold(&mut digest, &encoded);
        }
    }
    assert_eq!(digest, ENCODE_GOLDEN);
}

#[test]
fn plain_decode_matches_rapidyenc() {
    let mut rng = Rng(0x1966);
    let mut digest = 0;
    for raw in [false, true] {
        for _ in 0..4000 {
            let (len, one_in) = (rng.below(200), 1 + rng.below(8));
            let wire = if rng.below(2) == 0 {
                special_wire(&mut rng, len)
            } else {
                charset_wire(&mut rng, len, one_in)
            };
            let (first, second) = wire.split_at(rng.below(wire.len() + 1));
            let mut state = DecoderState::default();
            let mut reference_state = DecoderState::default();
            for chunk in [first, second] {
                let (mut out, mut reference) = (vec![0; chunk.len()], vec![0; chunk.len()]);
                let written = rust::decode(raw, chunk, &mut out, &mut state);
                let expected = crate::decode(raw, chunk, &mut reference, &mut reference_state);
                assert!(
                    written == expected
                        && out[..written] == reference[..written]
                        && state == reference_state,
                    "raw {raw}: {chunk:?} of {wire:?}"
                );
                fold(&mut digest, &out[..written]);
                fold(&mut digest, &state.0.to_le_bytes());
            }
        }
    }
    assert_eq!(digest, DECODE_GOLDEN);
}

#[test]
fn crc32_matches_rapidyenc() {
    let mut rng = Rng(0x1966);
    let data = rng.bytes(200_000);
    let (mut crc, mut reference, mut pos) = (0, 0, 0);
    while pos < data.len() {
        let chunk = &data[pos..(pos + rng.below(9000)).min(data.len())];
        crc = rust::crc32(crc, chunk);
        reference = crate::crc32(reference, chunk);
        assert_eq!(crc, reference, "after {pos} bytes");
        pos += chunk.len();
    }
    assert_eq!(crc, CRC_GOLDEN);
}

proptest::proptest! {
    #![proptest_config(proptest::test_runner::Config {
        cases: 128,
        rng_seed: proptest::test_runner::RngSeed::Fixed(1966),
        ..proptest::test_runner::Config::default()
    })]
    #[test]
    fn generated_calls_match_reference(
        wire in proptest::collection::vec(proptest::prop_oneof![
            3 => proptest::arbitrary::any::<u8>(),
            1 => proptest::sample::select(CHARSET.to_vec()),
        ], 0..2049),
        schedule in proptest::collection::vec((0usize..257, 0usize..257), 1..17),
    ) {
        let kernels = block::available().into_iter().map(|(name, kernel)| (name, Some(kernel)));
        for (name, kernel) in kernels.chain([("scalar", None)]) {
            let mut calls = schedule.iter().copied().cycle();
            let mut run = Run { name, kernel, digest: 0 };
            run.stream(&wire, &mut || calls.next().expect("a nonempty schedule repeats"));
        }
    }
}
