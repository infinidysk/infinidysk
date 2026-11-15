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
const MAX_FILES: usize = 50_000;
const MAX_SEGMENTS: usize = 1_000_000;
const MAX_ELEMENTS: usize = 2_000_000;
const MAX_DEPTH: usize = 64;
const MAX_TEXT: usize = 65_536;
const MAX_META: usize = 20_000;

#[derive(Debug, thiserror::Error)]
pub enum Error {
    #[error("NZB is not valid XML: {0}")]
    Xml(#[from] quick_xml::Error),
    #[error("NZB contains no file with a valid segment")]
    Empty,
    #[error("NZB is larger than 256 MB")]
    TooLarge,
    #[error("NZB has invalid document structure")]
    Structure,
    #[error("NZB exceeds parser resource limits")]
    Resource,
    #[error("NZB could not be decompressed: {0}")]
    Decompress(#[from] std::io::Error),
}

#[derive(Debug, Default)]
pub struct Nzb {
    /// `<head><meta type="…">` pairs in document order; a type can repeat.
    pub meta: Vec<(String, String)>,
    pub files: Vec<File>,
}

#[derive(Debug)]
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
    decompress_limited(raw, MAX_NZB)
}

fn decompress_limited(raw: &[u8], limit: u64) -> Result<Cow<'_, [u8]>, Error> {
    if raw.len() as u64 > limit {
        return Err(Error::TooLarge);
    }
    if !raw.starts_with(&[0x1f, 0x8b]) {
        // ponytail: bzip2 NZBs are not handled; add a bz2 decoder here if indexers serve them.
        return Ok(Cow::Borrowed(raw));
    }
    let mut out = Vec::new();
    flate2::read::MultiGzDecoder::new(raw)
        .take(limit.saturating_add(1))
        .read_to_end(&mut out)?;
    if out.len() as u64 > limit {
        return Err(Error::TooLarge);
    }
    Ok(Cow::Owned(out))
}

#[derive(Clone, Copy, PartialEq, Eq)]
enum Element {
    Root,
    Head,
    Meta,
    File,
    Segments,
    Segment,
    Extension,
}

/// Which elements with a meaning are open, one bit each. Each one is open at most once,
/// because [`element`] never opens one inside itself.
#[derive(Clone, Copy, Default)]
struct Open(u8);

impl Open {
    fn set(&mut self, node: Element, open: bool) {
        if matches!(node, Element::Root | Element::Extension) {
            return;
        }
        let bit = 1 << node as u8;
        if open {
            self.0 |= bit;
        } else {
            self.0 &= !bit;
        }
    }

    const fn has(self, node: Element) -> bool {
        self.0 & (1 << node as u8) != 0
    }
}

/// Classifies an element the way SABnzbd's `ElementTree` walk finds it: any root name, a
/// `file` or `head` at any depth (any case), `segments` directly in a file, a `segment`
/// anywhere below it and a `meta` anywhere in a head. Anything else is ignored. Only a
/// second root is an error, as in `ElementTree`.
fn element(
    name: &str,
    parent: Option<Element>,
    open: Open,
    root_seen: bool,
) -> Result<Element, Error> {
    let Some(parent) = parent else {
        return if root_seen {
            Err(Error::Structure)
        } else {
            Ok(Element::Root)
        };
    };
    Ok(
        if name.eq_ignore_ascii_case("file") && !open.has(Element::File) {
            Element::File
        } else if name.eq_ignore_ascii_case("head")
            && !open.has(Element::Head)
            && !open.has(Element::File)
        {
            Element::Head
        } else if name == "meta" && open.has(Element::Head) && !open.has(Element::Meta) {
            Element::Meta
        } else if name == "segments" && parent == Element::File {
            Element::Segments
        } else if name == "segment" && open.has(Element::Segments) && !open.has(Element::Segment) {
            Element::Segment
        } else {
            Element::Extension
        },
    )
}

fn count_element(
    node: Element,
    elements: &mut usize,
    files: &mut usize,
    segments: &mut usize,
) -> Result<(), Error> {
    *elements += 1;
    if node == Element::File {
        *files += 1;
    }
    if node == Element::Segment {
        *segments += 1;
    }
    if *elements > MAX_ELEMENTS || *files > MAX_FILES || *segments > MAX_SEGMENTS {
        return Err(Error::Resource);
    }
    Ok(())
}

fn append_text(text: &mut Option<String>, value: &str) -> Result<(), Error> {
    if let Some(text) = text {
        if text.len().saturating_add(value.len()) > MAX_TEXT {
            return Err(Error::Resource);
        }
        text.push_str(value);
    }
    Ok(())
}

/// Parses NZB XML within the resource limits.
///
/// Like SABnzbd: namespaces are ignored, elements are found where [`element`] says,
/// segments with a missing or bad `number` or `bytes` (or `bytes` outside `1..8 MiB`) are
/// dropped, a repeated part number keeps the first segment, a segment or meta value is
/// its text before any child element, files without segments are dropped, and only an
/// NZB with no files left fails. Malformed XML, text outside the root and a second root
/// fail, as they do in `ElementTree`.
pub fn parse(xml: &[u8]) -> Result<Nzb, Error> {
    if xml.len() as u64 > MAX_NZB {
        return Err(Error::TooLarge);
    }
    // ponytail: non-UTF-8 NZBs are read as Latin-1 instead of honouring the declared
    // encoding; enable quick-xml's `encoding` feature if other encodings show up.
    let xml = std::str::from_utf8(xml).map_or_else(
        |_| Cow::Owned(xml.iter().map(|&b| char::from(b)).collect()),
        Cow::Borrowed,
    );
    let mut reader = Reader::from_str(&xml);
    reader.config_mut().check_end_names = true;
    let mut nzb = Nzb::default();
    let mut stack = Vec::new();
    let mut open = Open::default();
    let mut root_seen = false;
    let mut finished = false;
    let (mut elements, mut file_count, mut segment_count) = (0, 0, 0);
    let mut file: Option<(File, BTreeMap<i64, Segment>)> = None;
    // Raw (still escaped) text of the open `<segment>` or `<meta>`; `text_open` turns off at
    // its first child element, like `ElementTree`'s `.text`.
    let mut text: Option<String> = None;
    let mut text_open = false;
    let mut segment: Option<(Option<i64>, Option<u32>)> = None;
    let mut meta: Option<String> = None;
    loop {
        match reader.read_event()? {
            Event::Start(e) => {
                if stack.len() >= MAX_DEPTH {
                    return Err(Error::Resource);
                }
                let node = element(
                    e.local_name().as_ref(),
                    stack.last().copied(),
                    open,
                    root_seen,
                )?;
                count_element(node, &mut elements, &mut file_count, &mut segment_count)?;
                text_open = false;
                match node {
                    Element::Root => root_seen = true,
                    Element::File => file = Some((file_start(&e)?, BTreeMap::new())),
                    Element::Segment => {
                        // Any integer, like SABnzbd's `int()`.
                        let number = attr(&e, "number")?.and_then(|v| v.trim().parse().ok());
                        let bytes = attr(&e, "bytes")?
                            .and_then(|v| v.trim().parse::<u32>().ok())
                            .filter(|b| (1..MAX_SEGMENT).contains(b));
                        segment = Some((number, bytes));
                        text = Some(String::new());
                        text_open = true;
                    }
                    Element::Meta => {
                        meta = attr(&e, "type")?.map(Cow::into_owned);
                        if meta.as_ref().is_some_and(|value| value.len() > MAX_TEXT) {
                            return Err(Error::Resource);
                        }
                        text = Some(String::new());
                        text_open = true;
                    }
                    _ => {}
                }
                open.set(node, true);
                stack.push(node);
            }
            Event::Empty(e) => {
                let node = element(
                    e.local_name().as_ref(),
                    stack.last().copied(),
                    open,
                    root_seen,
                )?;
                count_element(node, &mut elements, &mut file_count, &mut segment_count)?;
                text_open = false;
                if node == Element::Root {
                    root_seen = true;
                    finished = true;
                }
            }
            Event::Text(t) => {
                let value = t.xml10_content();
                if stack.is_empty() && !value.trim().is_empty() {
                    return Err(Error::Structure);
                }
                if text_open {
                    append_text(&mut text, &value)?;
                }
            }
            Event::CData(t) => {
                let value = t.xml10_content();
                if stack.is_empty() && !value.trim().is_empty() {
                    return Err(Error::Structure);
                }
                if text_open {
                    if value.len() > MAX_TEXT {
                        return Err(Error::Resource);
                    }
                    append_text(&mut text, &quick_xml::escape::escape(value))?;
                }
            }
            Event::GeneralRef(r) => {
                if stack.is_empty() {
                    return Err(Error::Structure);
                }
                if text_open {
                    append_text(&mut text, "&")?;
                    append_text(&mut text, &r.xml10_content())?;
                    append_text(&mut text, ";")?;
                }
            }
            Event::End(_) => {
                let node = stack.pop().ok_or(Error::Structure)?;
                open.set(node, false);
                text_open = false;
                match node {
                    Element::Segment => {
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
                            });
                        }
                    }
                    Element::Meta => {
                        if let (Some(kind), Some(value)) = (meta.take(), text.take())
                            && !value.is_empty()
                        {
                            if nzb.meta.len() >= MAX_META {
                                return Err(Error::Resource);
                            }
                            nzb.meta.push((kind, unescape(value)));
                        }
                    }
                    Element::File => {
                        if let Some((mut f, segments)) = file.take()
                            && let (Some(&first), Some(&last)) =
                                (segments.keys().next(), segments.keys().next_back())
                        {
                            f.gap = first > 1 || last.abs_diff(first) >= segments.len() as u64;
                            f.segments = segments.into_values().collect();
                            nzb.files.push(f);
                        }
                    }
                    Element::Root => finished = true,
                    _ => {}
                }
            }
            Event::Decl(_) | Event::DocType(_) if root_seen => return Err(Error::Structure),
            Event::Eof => {
                if !root_seen || !finished || !stack.is_empty() {
                    return Err(Error::Structure);
                }
                break;
            }
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
    if subject.len() > MAX_TEXT {
        return Err(Error::Resource);
    }
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

    fn one_file(segment: &str) -> String {
        format!(
            "<file subject=\"sample\" date=\"0\"><groups><group>a.b</group></groups><segments>{segment}</segments></file>"
        )
    }

    fn one_segment(number: &str) -> String {
        format!("<segment bytes=\"100\" number=\"{number}\">a@b</segment>")
    }

    #[test]
    fn second_roots_incomplete_documents_and_segments_outside_segments_are_rejected() {
        let file = one_file(&one_segment("1"));
        for xml in [
            format!("<nzb>{file}</nzb><extra/>"),
            format!("<nzb>{file}"),
            format!("<nzb>{file}</nzb>text"),
            // SABnzbd reads segments only below a `<segments>` child of the file.
            "<nzb><file subject=\"sample\"><segment bytes=\"100\" number=\"1\">a@b</segment></file></nzb>".to_owned(),
        ] {
            assert!(parse(xml.as_bytes()).is_err(), "{xml}");
        }
    }

    #[test]
    fn layouts_sabnzbd_accepts_are_accepted() {
        let file = one_file(&one_segment("1"));
        for xml in [
            format!("<other>{file}</other>"),
            format!("<nzb><other>{file}</other></nzb>"),
            "<nzb><FILE subject=\"sample\"><group>a.b</group><segments><x><segment bytes=\"100\" number=\"1\">a@b</segment></x></segments></FILE></nzb>".to_owned(),
        ] {
            let parsed = parse(xml.as_bytes()).unwrap_or_else(|e| panic!("{xml}: {e}"));
            assert_eq!(parsed.files.len(), 1, "{xml}");
            assert_eq!(parsed.files[0].segments[0].id, "a@b", "{xml}");
        }

        // A value is the text before the first child element, like `ElementTree`'s `.text`.
        let xml = format!(
            "<nzb><x><head><meta type=\"password\">value<i>child</i>tail</meta></head></x>{}</nzb>",
            one_file("<segment bytes=\"100\" number=\"0\">a@b<i>child</i>tail</segment>")
        );
        let parsed = parse(xml.as_bytes()).unwrap();
        assert_eq!(parsed.meta("password").collect::<Vec<_>>(), ["value"]);
        assert_eq!(parsed.files[0].segments[0].id, "a@b");
    }

    #[test]
    fn extreme_segment_numbers_do_not_overflow() {
        let segments = format!(
            "{}{}{}",
            one_segment("-9223372036854775808"),
            one_segment("1"),
            one_segment("9223372036854775807")
        );
        let xml = format!("<nzb>{}</nzb>", one_file(&segments));
        let parsed = parse(xml.as_bytes()).unwrap();
        // SABnzbd's `int()` keeps zero and negative parts too.
        assert_eq!(parsed.files[0].segments.len(), 3);
        assert!(parsed.files[0].gap);
    }

    #[test]
    fn file_segment_depth_and_text_budgets_reject_excessive_documents() {
        let segment = one_segment("1");
        let file = one_file(&segment);
        let too_many_files = format!("<nzb>{}</nzb>", file.repeat(50_001));
        assert!(parse(too_many_files.as_bytes()).is_err());
        let many_segments = segment.repeat(1_000_001);
        let too_many_segments = format!("<nzb>{}</nzb>", one_file(&many_segments));
        assert!(parse(too_many_segments.as_bytes()).is_err());
        let deep = format!(
            "<nzb>{}{file}{}</nzb>",
            "<extension>".repeat(65),
            "</extension>".repeat(65)
        );
        assert!(parse(deep.as_bytes()).is_err());
        let huge_id = format!(
            "<segment bytes=\"100\" number=\"1\">{}</segment>",
            "x".repeat(65_537)
        );
        let huge_text = format!("<nzb>{}</nzb>", one_file(&huge_id));
        assert!(parse(huge_text.as_bytes()).is_err());
    }

    #[test]
    fn namespaced_nzbs_and_safely_sparse_segments_are_accepted() {
        let xml = "<?xml version=\"1.0\"?><n:nzb xmlns:n=\"urn:example\"><n:head><n:meta type=\"password\">value</n:meta></n:head><n:file subject=\"sample\"><n:segments><n:segment bytes=\"100\" number=\"1\">a@b</n:segment><n:segment bytes=\"100\" number=\"4294967295\">c@d</n:segment></n:segments></n:file></n:nzb>";
        let parsed = parse(xml.as_bytes()).unwrap();
        assert_eq!(parsed.files[0].segments.len(), 2);
        assert!(parsed.files[0].gap);
        assert_eq!(parsed.meta("password").collect::<Vec<_>>(), ["value"]);
    }

    #[test]
    fn configured_file_and_segment_limits_accept_the_exact_boundary() {
        let segment = one_segment("1");
        let file = one_file(&segment);
        let many_files = format!("<nzb>{}</nzb>", file.repeat(MAX_FILES));
        assert_eq!(parse(many_files.as_bytes()).unwrap().files.len(), MAX_FILES);
        let many_segments = format!("<nzb>{}</nzb>", one_file(&segment.repeat(MAX_SEGMENTS)));
        assert_eq!(
            parse(many_segments.as_bytes()).unwrap().files[0]
                .segments
                .len(),
            1
        );
    }

    #[test]
    fn standard_doctype_and_cdata_metadata_remain_valid() {
        let xml = format!(
            "<?xml version=\"1.0\"?><!DOCTYPE nzb PUBLIC \"-//newzBin//DTD NZB 1.1//EN\" \"http://www.newzbin.com/DTD/nzb/nzb-1.1.dtd\"><nzb><head><meta type=\"password\"><![CDATA[a&b]]></meta></head>{}</nzb>",
            one_file(&one_segment("1"))
        );
        let parsed = parse(xml.as_bytes()).unwrap();
        assert_eq!(parsed.meta("password").collect::<Vec<_>>(), ["a&b"]);
        assert_eq!(parsed.files.len(), 1);
    }

    #[test]
    fn gzip_limits_corruption_and_valid_compressed_nzbs() {
        use flate2::Compression;
        use flate2::write::GzEncoder;
        use std::io::Write as _;

        let xml = format!("<nzb>{}</nzb>", one_file(&one_segment("1")));
        let mut encoder = GzEncoder::new(Vec::new(), Compression::default());
        encoder.write_all(xml.as_bytes()).unwrap();
        let valid = encoder.finish().unwrap();
        let decoded = decompress(&valid).unwrap();
        assert_eq!(parse(&decoded).unwrap().files.len(), 1);

        let mut truncated = valid.clone();
        truncated.truncate(truncated.len() - 4);
        assert!(decompress(&truncated).is_err());

        let mut encoder = GzEncoder::new(Vec::new(), Compression::default());
        encoder.write_all(&vec![b'x'; 8192]).unwrap();
        let bomb = encoder.finish().unwrap();
        assert!(matches!(
            decompress_limited(&bomb, 1024),
            Err(Error::TooLarge)
        ));
        assert!(matches!(
            decompress_limited(&valid, 8),
            Err(Error::TooLarge)
        ));

        let mut concatenated = valid.clone();
        concatenated.extend_from_slice(&valid);
        let decoded = decompress(&concatenated).unwrap();
        assert!(matches!(parse(&decoded), Err(Error::Structure)));
    }

    #[test]
    fn valid_segment_size_boundaries_and_duplicate_parts_remain_compatible() {
        let segments = format!(
            "<segment bytes=\"{}\" number=\"1\">a@b</segment><segment bytes=\"{}\" number=\"1\">b@c</segment><segment bytes=\"{}\" number=\"2\">c@d</segment>",
            MAX_SEGMENT - 1,
            MAX_SEGMENT - 1,
            MAX_SEGMENT
        );
        let xml = format!("<nzb>{}</nzb>", one_file(&segments));
        let parsed = parse(xml.as_bytes()).unwrap();
        assert_eq!(parsed.files[0].segments.len(), 1);
        assert_eq!(parsed.files[0].segments[0].id, "a@b");
        assert_eq!(parsed.files[0].bytes(), u64::from(MAX_SEGMENT - 1));
        assert!(!parsed.files[0].gap);
    }

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
                        .map(|id| Segment {
                            id: (*id).into(),
                            bytes: 1,
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
