//! NZB parsing that accepts whatever SABnzbd accepts.

use std::borrow::Cow;
use std::collections::BTreeMap;
use std::io::Read;
use std::sync::LazyLock;

use quick_xml::Reader;
use quick_xml::events::{BytesStart, Event};
use regex::Regex;

/// Largest NZB we accept, raw or decompressed: 256 MB.
pub const MAX_NZB: u64 = 256_000_000;
/// Request body limit for uploads carrying one NZB, with room for multipart framing.
pub const MAX_UPLOAD: usize = 257_000_000;
/// Segments of this size or larger are dropped, like SABnzbd does.
const MAX_SEGMENT: u32 = 1 << 23;

#[derive(Debug, thiserror::Error)]
pub enum Error {
    #[error("NZB is not valid XML: {0}")]
    Xml(#[from] quick_xml::Error),
    #[error("NZB contains no file with a valid segment")]
    Empty,
    #[error("NZB is larger than 256 MB")]
    TooLarge,
    #[error("NZB could not be decompressed: {0}")]
    Decompress(#[from] std::io::Error),
}

#[derive(Debug, Default)]
pub struct Nzb {
    /// `<head><meta type="…">` pairs in document order; a type can repeat.
    pub meta: Vec<(String, String)>,
    pub files: Vec<File>,
}

#[derive(Debug, Clone)]
pub struct File {
    pub subject: String,
    /// Posting time in Unix seconds, when the NZB carries a valid one.
    pub date: Option<i64>,
    /// Segments in part-number order; gaps are allowed.
    pub segments: Vec<Segment>,
    /// The part numbers skip some, so the NZB lacks those segments.
    pub gap: bool,
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Segment {
    /// Message-id without angle brackets.
    pub id: String,
    pub bytes: u32,
    /// Original NZB part number; repair checks it against decoded yEnc evidence.
    pub number: i64,
}

impl Nzb {
    /// Values of every `<meta>` with this type.
    pub fn meta<'a>(&'a self, kind: &'a str) -> impl Iterator<Item = &'a str> {
        self.meta
            .iter()
            .filter(move |(k, _)| k == kind)
            .map(|(_, v)| v.as_str())
    }

    /// Hex SHA-256 over the message-ids, deduplicated and sorted, each length-prefixed so
    /// no two id sets hash the same input. Names and grouping do not count.
    pub fn content_hash(&self) -> String {
        use sha2::Digest as _;
        let ids: std::collections::BTreeSet<&str> = self
            .files
            .iter()
            .flat_map(|f| &f.segments)
            .map(|s| s.id.as_str())
            .collect();
        let mut hash = sha2::Sha256::new();
        for id in ids {
            hash.update((id.len() as u64).to_le_bytes());
            hash.update(id);
        }
        hash.finalize()
            .iter()
            .fold(String::with_capacity(64), |mut s, b| {
                use std::fmt::Write as _;
                let _ = write!(s, "{b:02x}");
                s
            })
    }
}

impl File {
    pub fn bytes(&self) -> u64 {
        self.segments.iter().map(|s| u64::from(s.bytes)).sum()
    }

    /// The filename named in the subject, or the whole subject.
    pub fn name(&self) -> &str {
        subject_name(&self.subject)
    }
}

/// Undoes gzip compression (detected by magic, like SABnzbd), capped at [`MAX_NZB`].
pub fn decompress(raw: &[u8]) -> Result<Cow<'_, [u8]>, Error> {
    if !raw.starts_with(&[0x1f, 0x8b]) {
        // ponytail: bzip2 NZBs are not handled; add a bz2 decoder here if indexers serve them.
        return if raw.len() as u64 > MAX_NZB {
            Err(Error::TooLarge)
        } else {
            Ok(Cow::Borrowed(raw))
        };
    }
    let mut out = Vec::new();
    flate2::read::MultiGzDecoder::new(raw)
        .take(MAX_NZB + 1)
        .read_to_end(&mut out)?;
    if out.len() as u64 > MAX_NZB {
        return Err(Error::TooLarge);
    }
    Ok(Cow::Owned(out))
}

/// Parses NZB XML.
///
/// Like SABnzbd: namespaces are ignored, segments with a missing or bad `number` or `bytes`
/// (or `bytes` outside `1..8 MiB`) are dropped, a repeated part number keeps the first
/// segment, files without segments are dropped, and only an NZB with no files left fails.
pub fn parse(xml: &[u8]) -> Result<Nzb, Error> {
    // ponytail: non-UTF-8 NZBs are read as Latin-1 instead of honouring the declared
    // encoding; enable quick-xml's `encoding` feature if other encodings show up.
    let xml = std::str::from_utf8(xml).map_or_else(
        |_| Cow::Owned(xml.iter().map(|&b| char::from(b)).collect()),
        Cow::Borrowed,
    );
    let mut reader = Reader::from_str(&xml);
    let mut nzb = Nzb::default();
    let mut file: Option<(File, BTreeMap<i64, Segment>)> = None;
    // Raw (still escaped) text of the open `<segment>` or `<meta>`.
    let mut text: Option<String> = None;
    let mut segment: Option<(Option<i64>, Option<u32>)> = None;
    let mut meta: Option<String> = None;
    loop {
        match reader.read_event()? {
            Event::Start(e) => match e.local_name().as_ref() {
                "file" => file = Some((file_start(&e)?, BTreeMap::new())),
                "segment" => {
                    let number = attr(&e, "number")?.and_then(|v| v.trim().parse().ok());
                    let bytes = attr(&e, "bytes")?
                        .and_then(|v| v.trim().parse::<u32>().ok())
                        .filter(|b| (1..MAX_SEGMENT).contains(b));
                    segment = Some((number, bytes));
                    text = Some(String::new());
                }
                "meta" => {
                    meta = attr(&e, "type")?.map(Cow::into_owned);
                    text = Some(String::new());
                }
                _ => {}
            },
            Event::Text(t) => {
                if let Some(s) = &mut text {
                    s.push_str(&t.xml10_content());
                }
            }
            Event::CData(t) => {
                if let Some(s) = &mut text {
                    s.push_str(&quick_xml::escape::escape(t.xml10_content()));
                }
            }
            Event::GeneralRef(r) => {
                if let Some(s) = &mut text {
                    s.push('&');
                    s.push_str(&r.xml10_content());
                    s.push(';');
                }
            }
            Event::End(e) => match e.local_name().as_ref() {
                "segment" => {
                    let id = unescape(text.take().unwrap_or_default());
                    let id = id
                        .trim()
                        .trim_start_matches('<')
                        .trim_end_matches('>')
                        .trim();
                    if let (Some((Some(number), Some(bytes))), Some((_, segments))) =
                        (segment.take(), &mut file)
                        && !id.is_empty()
                    {
                        segments.entry(number).or_insert_with(|| Segment {
                            id: id.to_owned(),
                            bytes,
                            number,
                        });
                    }
                }
                "meta" => {
                    if let (Some(kind), Some(value)) = (meta.take(), text.take())
                        && !value.is_empty()
                    {
                        nzb.meta.push((kind, unescape(value)));
                    }
                }
                "file" => {
                    if let Some((mut f, segments)) = file.take()
                        && let (Some(&first), Some(&last)) =
                            (segments.keys().next(), segments.keys().next_back())
                    {
                        f.gap = first > 1
                            || last.checked_sub(first).is_none_or(|span| {
                                span >= i64::try_from(segments.len()).unwrap_or(i64::MAX)
                            });
                        f.segments = segments.into_values().collect();
                        nzb.files.push(f);
                    }
                }
                _ => {}
            },
            Event::Eof => break,
            _ => {}
        }
    }
    if nzb.files.is_empty() {
        return Err(Error::Empty);
    }
    Ok(nzb)
}

fn file_start(e: &BytesStart) -> Result<File, Error> {
    let subject = attr(e, "subject")?
        .filter(|s| !s.is_empty())
        .map_or_else(|| "unknown".to_owned(), Cow::into_owned);
    let date = attr(e, "date")?.and_then(|d| d.trim().parse().ok());
    Ok(File {
        subject,
        date,
        segments: Vec::new(),
        gap: false,
    })
}

fn attr<'a>(e: &'a BytesStart, name: &str) -> Result<Option<Cow<'a, str>>, Error> {
    for a in e.attributes() {
        let a = a.map_err(quick_xml::Error::from)?;
        if a.key.local_name().as_ref() == name {
            return Ok(Some(
                a.normalized_value(quick_xml::XmlVersion::Implicit1_0)?,
            ));
        }
    }
    Ok(None)
}

/// Resolves entity references; unknown ones are kept verbatim.
fn unescape(raw: String) -> String {
    match quick_xml::escape::unescape(&raw) {
        Ok(Cow::Owned(s)) => s,
        Ok(Cow::Borrowed(_)) | Err(_) => raw,
    }
}

/// SABnzbd's subject-to-filename rules: the last non-empty quoted string, else the last
/// filename-like token, else the whole subject.
fn subject_name(subject: &str) -> &str {
    static QUOTED: LazyLock<Regex> =
        LazyLock::new(|| Regex::new(r#""([^"]*)""#).expect("valid regex literal"));
    static BASIC: LazyLock<Regex> = LazyLock::new(|| {
        Regex::new(r"\b([\w\-+()' .,]+(?:\[[\w\-/+()' .,]*][\w\-+()' .,]*)*\.[A-Za-z0-9]{2,4})\b")
            .expect("valid regex literal")
    });
    let last = |re: &Regex, trim: &[char]| {
        re.captures_iter(subject)
            .filter_map(|c| c.get(1).map(|m| m.as_str().trim_matches(trim)))
            .filter(|n| !n.is_empty())
            .last()
    };
    last(&QUOTED, &[' ', '"'])
        .or_else(|| last(&BASIC, &[' ']))
        .unwrap_or(subject)
        .trim()
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn subject_names() {
        assert_eq!(
            subject_name(r#"[1/9] - "a.part01.rar" yEnc (1/50)"#),
            "a.part01.rar"
        );
        assert_eq!(subject_name("a.b.mkv yEnc (1/50)"), "a.b.mkv");
        assert_eq!(subject_name("0123abcdef"), "0123abcdef");
    }

    #[test]
    fn content_hash_ignores_order_and_repeats_but_not_boundaries() {
        let nzb = |files: &[&[&str]]| Nzb {
            meta: Vec::new(),
            files: files
                .iter()
                .map(|ids| File {
                    subject: "s".into(),
                    date: None,
                    gap: false,
                    segments: ids
                        .iter()
                        .enumerate()
                        .map(|(i, id)| Segment {
                            id: (*id).into(),
                            bytes: 1,
                            number: i64::try_from(i + 1).expect("small test NZB"),
                        })
                        .collect(),
                })
                .collect(),
        };
        let hash = nzb(&[&["a@b", "c@d"]]).content_hash();
        assert_eq!(nzb(&[&["c@d"], &["a@b", "c@d"]]).content_hash(), hash);
        assert_ne!(nzb(&[&["a@bc@d"]]).content_hash(), hash);
        assert_ne!(nzb(&[&["A@b", "c@d"]]).content_hash(), hash);
    }
}
