use std::str::FromStr;

use bytes::Bytes;

/// Upper bound on one decoded article; real articles are around 1 MiB.
pub const MAX_DECODED_LEN: usize = 32 * 1024 * 1024;

/// A decoded yEnc article body.
#[derive(Debug, Clone)]
pub struct Body {
    pub data: Bytes,
    /// Zero-based offset of `data` within the file.
    pub offset: u64,
    /// Size of the whole file from `=ybegin size=`.
    pub file_size: u64,
    /// One-based part number, for multipart posts.
    pub part: Option<u32>,
    /// Number of parts the file was posted in, from `=ybegin total=`.
    pub total: Option<u32>,
}

/// Value of `key=` in a `=y…` header line. Stops at `name=`, which is always last and
/// may contain spaces.
fn raw_field<'a>(line: &'a [u8], key: &[u8]) -> Option<&'a str> {
    let value = line
        .trim_ascii_end()
        .split(|&b| b == b' ')
        .take_while(|token| !token.starts_with(b"name="))
        .find_map(|token| token.strip_prefix(key)?.strip_prefix(b"="))?;
    std::str::from_utf8(value).ok()
}

pub fn field<T: FromStr>(line: &[u8], key: &[u8]) -> Option<T> {
    raw_field(line, key)?.parse().ok()
}

pub fn crc_field(line: &[u8], key: &[u8]) -> Option<u32> {
    u32::from_str_radix(raw_field(line, key)?, 16).ok()
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn fields_stop_at_name() {
        let line = b"=ybegin part=2 line=128 size=15 name=a size=9 b.bin\r\n";
        assert_eq!(field::<u64>(line, b"size"), Some(15));
        assert_eq!(field::<u32>(line, b"part"), Some(2));
        assert_eq!(
            crc_field(b"end size=5 pcrc32=0aBc\r\n", b"pcrc32"),
            Some(0xabc)
        );
        assert_eq!(crc_field(b"end size=5 pcrc32=0aBc\r\n", b"crc32"), None);
    }
}
