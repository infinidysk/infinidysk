use infinidysk_yenc::{crc32, encode};

/// One logical file split into yEnc articles, as a provider would serve it.
#[derive(Debug, Clone)]
pub struct Corpus {
    /// The decoded file.
    pub data: Vec<u8>,
    pub articles: Vec<Article>,
}

#[derive(Debug, Clone)]
pub struct Article {
    /// Message-id without angle brackets, e.g. `bench-0000@loopback`.
    pub message_id: String,
    /// BODY response payload: dot-stuffed yEnc lines plus the `.\r\n` terminator.
    pub wire: Vec<u8>,
}

impl Corpus {
    /// Generates `article_count` articles of `article_size` bytes from `seed`.
    ///
    /// Layout, message-ids and yEnc headers match the benchmark corpus; use seed 1025
    /// for benchmarks.
    // ponytail: bytes come from SplitMix64, not .NET `System.Random`, so the payload
    // differs from the C# corpus. Sizes and framing match, which is what the metrics
    // depend on. Port .NET's generator only if byte-identical data is ever needed.
    #[must_use]
    pub fn generate(seed: u64, article_count: usize, article_size: usize) -> Self {
        let mut data = vec![0u8; article_count * article_size];
        let mut state = seed;
        for chunk in data.chunks_mut(8) {
            state = state.wrapping_add(0x9E37_79B9_7F4A_7C15);
            let mut z = state;
            z = (z ^ (z >> 30)).wrapping_mul(0xBF58_476D_1CE4_E5B9);
            z = (z ^ (z >> 27)).wrapping_mul(0x94D0_49BB_1331_11EB);
            z ^= z >> 31;
            chunk.copy_from_slice(&z.to_le_bytes()[..chunk.len()]);
        }

        let articles = data
            .chunks(article_size)
            .enumerate()
            .map(|(index, payload)| Article {
                message_id: format!("bench-{index:04}@loopback"),
                wire: wire_body(
                    "loopback.bin",
                    index,
                    article_count,
                    data.len(),
                    article_size,
                    payload,
                ),
            })
            .collect();

        Self { data, articles }
    }
}

/// Posts `data` as `name` in articles of `article_size` bytes, with message-ids
/// `<prefix>-<index>@loopback`.
#[must_use]
pub fn post(prefix: &str, name: &str, data: &[u8], article_size: usize) -> Vec<Article> {
    let total = data.len().div_ceil(article_size);
    data.chunks(article_size)
        .enumerate()
        .map(|(index, payload)| Article {
            message_id: format!("{prefix}-{index}@loopback"),
            wire: wire_body(name, index, total, data.len(), article_size, payload),
        })
        .collect()
}

fn wire_body(
    name: &str,
    index: usize,
    total: usize,
    file_size: usize,
    article_size: usize,
    payload: &[u8],
) -> Vec<u8> {
    let part = index + 1;
    let begin = index * article_size + 1;
    let end = begin + payload.len() - 1;
    let mut wire = format!(
        "=ybegin part={part} total={total} line=128 size={file_size} name={name}\r\n\
         =ypart begin={begin} end={end}\r\n"
    )
    .into_bytes();

    let encoded = encode(payload, 128);
    wire.reserve(encoded.len() + 128);
    let mut line_start = true;
    for &byte in &encoded {
        if line_start && byte == b'.' {
            wire.push(b'.');
        }
        wire.push(byte);
        line_start = byte == b'\n';
    }

    wire.extend_from_slice(
        format!(
            "\r\n=yend size={} part={part} pcrc32={:08x}\r\n.\r\n",
            payload.len(),
            crc32(0, payload)
        )
        .as_bytes(),
    );
    wire
}
