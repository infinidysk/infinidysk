//! Archive header parsers. They never do I/O: callers feed bytes and the
//! parser answers with what it found or which bytes it needs next.

pub mod crypt;
pub mod rar;
pub mod sevenzip;

/// Archive formats recognised from their leading bytes.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum Format {
    Rar4,
    Rar5,
    SevenZip,
}

/// Identifies an archive from its first 8 bytes.
pub fn detect(prefix: &[u8]) -> Option<Format> {
    if prefix.starts_with(rar::RAR5_SIGNATURE) {
        Some(Format::Rar5)
    } else if prefix.starts_with(rar::RAR4_SIGNATURE) {
        Some(Format::Rar4)
    } else if prefix.starts_with(sevenzip::SIGNATURE) {
        Some(Format::SevenZip)
    } else {
        None
    }
}

#[derive(Debug, thiserror::Error, PartialEq, Eq)]
pub enum Error {
    #[error("not a recognised archive")]
    Signature,
    #[error("wrong archive password")]
    WrongPassword,
    #[error("header checksum mismatch at offset {0}")]
    HeaderCrc(u64),
    #[error("corrupt header at offset {offset}: {reason}")]
    Corrupt { offset: u64, reason: &'static str },
    #[error("key derivation was stopped")]
    Cancelled,
}
