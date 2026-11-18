//! Container structure checks on verified article bytes (§5.7): the top-level box chain
//! of an MP4-family file must add up to the file's length.

use infinidysk_core::crypt::{BLOCK, Cipher};
use infinidysk_core::{FileMeta, Par2Proof, Segment, Segments};
use infinidysk_db::Defect;
use infinidysk_nntp::Articles;

/// Bumped whenever a rule changes, so evidence from older rules needs a fresh sighting.
const CLASSIFIER: u8 = 2;
/// Top-level boxes walked before giving up; a plain MP4 has a handful.
const MAX_BOXES: usize = 32;
const MP4: &[&str] = &["mp4", "m4v", "mov"];

#[derive(Debug, PartialEq, Eq)]
pub enum Structure {
    Sound,
    /// Not a container these rules cover, or a layout they cannot map.
    Unrecognized,
    /// The box header at `offset` claims `size` bytes, which cannot fit.
    Damaged {
        offset: u64,
        size: u64,
    },
    /// The bytes could not be read or walked to the end; proves nothing.
    Inconclusive(String),
}

/// What one box header says about the chain.
#[derive(Debug, PartialEq, Eq)]
enum Step {
    Next(u64),
    End,
    Damaged(u64),
    Unrecognized,
}

/// Reads the box header at `pos` from `header` (16 bytes, fewer only at the end of a
/// `len`-byte file).
fn step(header: &[u8], pos: u64, len: u64, first: bool) -> Step {
    let Some(head) = header.get(..8) else {
        return Step::Unrecognized;
    };
    let kind = &head[4..8];
    if (first && kind != b"ftyp") || !kind.iter().all(|b| (0x20..0x7f).contains(b)) {
        return Step::Unrecognized;
    }
    let (size, header_len) = match u32::from_be_bytes([head[0], head[1], head[2], head[3]]) {
        // Runs to the end of the file.
        0 => return Step::End,
        1 => match header.get(8..16) {
            Some(b) => (
                u64::from_be_bytes([b[0], b[1], b[2], b[3], b[4], b[5], b[6], b[7]]),
                16,
            ),
            None => return Step::Damaged(1),
        },
        s => (u64::from(s), 8),
    };
    if size < header_len || size > len - pos {
        return Step::Damaged(size);
    }
    if pos + size == len {
        Step::End
    } else {
        Step::Next(pos + size)
    }
}

/// Walks the top-level boxes of `meta` (file `name`), reading only box headers. `head` is
/// the first segment when the caller already has it.
pub async fn walk(
    articles: &dyn Articles,
    meta: &FileMeta,
    name: &str,
    head: Option<Vec<u8>>,
) -> Structure {
    let ext = name.rsplit_once('.').map_or("", |(_, e)| e);
    if !MP4.iter().any(|e| ext.eq_ignore_ascii_case(e)) {
        return Structure::Unrecognized;
    }
    let Some(mut bytes) = Bytes::new(articles, meta) else {
        return Structure::Unrecognized;
    };
    if let Some(head) = head
        && bytes
            .spans
            .first()
            .and_then(|s| s.segments.items.first())
            .is_some_and(|s| head.len() == s.size as usize)
    {
        bytes.held = Some((0, head));
    }
    let mut pos = 0;
    for n in 0..MAX_BOXES {
        let header = match bytes.read(pos, 16).await {
            Ok(header) => header,
            Err(reason) => return Structure::Inconclusive(reason),
        };
        match step(&header, pos, bytes.len, n == 0) {
            Step::Next(next) => pos = next,
            Step::End => return Structure::Sound,
            Step::Damaged(size) => return Structure::Damaged { offset: pos, size },
            Step::Unrecognized if n == 0 => return Structure::Unrecognized,
            Step::Unrecognized => {
                return Structure::Inconclusive(format!("unrecognized box at byte {pos}"));
            }
        }
    }
    Structure::Inconclusive(format!("more than {MAX_BOXES} top-level boxes"))
}

/// One run of the logical file, served by a run of posted segments.
struct Span<'a> {
    /// Logical offset where the run starts.
    start: u64,
    len: u64,
    segments: &'a Segments,
    /// Posted offset of the run's first byte.
    at: u64,
    /// Posted offset of the first segment.
    offset: u64,
    /// Flat index of the first segment.
    base: usize,
    proof: Option<&'a Par2Proof>,
}

/// Logical bytes of a file from CRC-verified bodies; never zero-filled.
struct Bytes<'a> {
    articles: &'a dyn Articles,
    spans: Vec<Span<'a>>,
    /// Plaintext length.
    len: u64,
    /// Posted (ciphertext) length of the spans.
    raw_len: u64,
    crypt: Option<(Cipher, [u8; BLOCK])>,
    /// The last segment read, by flat index.
    held: Option<(usize, Vec<u8>)>,
}

impl<'a> Bytes<'a> {
    /// `None` unless every segment size is trusted, so offsets map exactly.
    fn new(articles: &'a dyn Articles, meta: &'a FileMeta) -> Option<Self> {
        let mut spans = Vec::new();
        let (mut len, mut base) = (0, 0);
        let mut push = |segments: &'a Segments,
                        offset: u64,
                        start: u64,
                        end: u64,
                        proof: Option<&'a Par2Proof>| {
            if !segments.sizes_trusted
                || start < offset
                || offset
                    .checked_add(segments.size())
                    .is_none_or(|limit| end > limit)
            {
                return false;
            }
            if end > start {
                spans.push(Span {
                    start: len,
                    len: end - start,
                    segments,
                    at: start,
                    offset,
                    base,
                    proof,
                });
                len += end - start;
            }
            base += segments.items.len();
            true
        };
        let ok = match meta {
            FileMeta::Nzb(f) => push(&f.segments, 0, 0, f.segments.size(), f.proof.as_ref()),
            FileMeta::Multipart(f) => f.parts.iter().all(|p| {
                push(
                    &p.segments,
                    p.offset,
                    p.data.start,
                    p.data.end,
                    p.proof.as_ref(),
                )
            }),
        };
        let crypt = match meta {
            FileMeta::Multipart(f) => f.crypt.as_ref(),
            FileMeta::Nzb(_) => None,
        };
        if crypt.is_some_and(|c| c.size.next_multiple_of(BLOCK as u64) > len) {
            return None;
        }
        (ok && len > 0).then_some(Self {
            articles,
            spans,
            len: crypt.map_or(len, |c| c.size),
            raw_len: len,
            crypt: crypt.map(|c| (Cipher::new(&c.key), c.iv)),
            held: None,
        })
    }

    /// Up to `n` bytes at `pos`; fewer only at the end of the file.
    async fn read(&mut self, pos: u64, n: usize) -> Result<Vec<u8>, String> {
        let Some((cipher, first_iv)) = self.crypt.clone() else {
            return self.raw(pos, n).await;
        };
        let Some(n) = usize::try_from(self.len.saturating_sub(pos))
            .ok()
            .map(|left| left.min(n))
            .filter(|&n| n > 0)
        else {
            return Ok(Vec::new());
        };
        // CBC: the block before the first one wanted is its IV.
        let block = pos - pos % BLOCK as u64;
        let from = block.saturating_sub(BLOCK as u64);
        let end = (pos + n as u64).next_multiple_of(BLOCK as u64);
        let want = usize::try_from(end - from).map_err(|_| "read too large".to_owned())?;
        let mut data = self.raw(from, want).await?;
        if data.len() < want {
            return Err("ciphertext ends before the file".to_owned());
        }
        let mut iv = first_iv;
        if block > 0 {
            iv.copy_from_slice(&data[..BLOCK]);
            data.drain(..BLOCK);
        }
        cipher.decrypt(&mut iv, &mut data);
        let within = usize::try_from(pos - block).expect("less than a block");
        Ok(data[within..within + n].to_vec())
    }

    /// Up to `n` posted bytes at `pos`; fewer only at the end of the spans.
    async fn raw(&mut self, pos: u64, n: usize) -> Result<Vec<u8>, String> {
        let mut out = Vec::with_capacity(n);
        loop {
            let at = pos + out.len() as u64;
            if out.len() == n || at >= self.raw_len {
                return Ok(out);
            }
            let span = &self.spans[self.spans.partition_point(|s| s.start <= at) - 1];
            let posted = span.at + (at - span.start);
            let span_end = span.at + span.len;
            let mut start = span.offset;
            let mut found = None;
            for (k, segment) in span.segments.items.iter().enumerate() {
                let end = start + u64::from(segment.size);
                if posted < end {
                    found = Some((span.base + k, segment, start));
                    break;
                }
                start = end;
            }
            let Some((flat, segment, begin)) = found else {
                return Err("layout ends before the file".to_owned());
            };
            let segment = segment.clone();
            let proof = span.proof;
            let data = self.segment(flat, &segment, begin, proof).await?;
            let within = usize::try_from(posted - begin).unwrap_or(usize::MAX);
            let left = usize::try_from(span_end - posted).unwrap_or(usize::MAX);
            let take = (n - out.len()).min(data.len() - within).min(left);
            out.extend_from_slice(&data[within..within + take]);
        }
    }

    async fn segment(
        &mut self,
        flat: usize,
        segment: &Segment,
        begin: u64,
        proof: Option<&Par2Proof>,
    ) -> Result<&[u8], String> {
        if self.held.as_ref().is_none_or(|(held, _)| *held != flat) {
            self.held = Some((flat, fetch(self.articles, segment, begin, proof).await?));
        }
        Ok(&self.held.as_ref().expect("held set above").1)
    }
}

/// The body of `segment` exactly as posted at `begin`.
async fn fetch(
    articles: &dyn Articles,
    segment: &Segment,
    begin: u64,
    proof: Option<&Par2Proof>,
) -> Result<Vec<u8>, String> {
    let error = match articles.read(segment, Some(begin), proof).await {
        Ok(data) if data.len() == segment.size as usize => return Ok(data.into()),
        Ok(_) => "decoded size differs from the layout".to_owned(),
        Err(e) => e.to_string(),
    };
    Err(format!("a segment could not be read ({error})"))
}

/// Identity of `meta`'s article layout and of these rules, so evidence follows neither a
/// replaced file nor changed rules. FNV-1a, stable across builds.
pub fn source(meta: &FileMeta) -> i64 {
    let mut hash: u64 = 0xcbf2_9ce4_8422_2325;
    let mut write = |bytes: &[u8]| {
        for b in bytes {
            hash = (hash ^ u64::from(*b)).wrapping_mul(0x0100_0000_01b3);
        }
    };
    write(&[CLASSIFIER]);
    let mut part =
        |segments: &Segments, offset: u64, start: u64, end: u64, proof: Option<&Par2Proof>| {
            if let Some(proof) = proof
                && let Some(set) = proof.set_id
            {
                write(&[1]);
                write(&set);
                write(&proof.file_id);
                write(&proof.file_md5);
            } else {
                write(&[0]);
            }
            for v in [offset, start, end] {
                write(&v.to_le_bytes());
            }
            for s in &segments.items {
                write(&s.size.to_le_bytes());
                write(s.id.as_bytes());
                write(&[0]);
                write(&[1]);
            }
        };
    match meta {
        FileMeta::Nzb(f) => part(&f.segments, 0, 0, f.segments.size(), f.proof.as_ref()),
        FileMeta::Multipart(f) => {
            for p in &f.parts {
                part(
                    &p.segments,
                    p.offset,
                    p.data.start,
                    p.data.end,
                    p.proof.as_ref(),
                );
            }
        }
    }
    hash.cast_signed()
}

/// How a walk changes a file's stored evidence.
#[derive(Debug, PartialEq, Eq)]
pub enum Verdict {
    /// No evidence before or now.
    Nothing,
    /// The defect is gone or its evidence no longer applies.
    Clear,
    /// A new defect: first sighting.
    First(Defect),
    /// The same defect again, too early to confirm it.
    Wait(Defect),
    /// The walk proved nothing; keep the evidence.
    Keep(Defect),
    /// The same defect on the same source, long enough after the first sighting.
    Confirm(Defect),
}

/// Combines `stored` evidence with what a walk of source `source` `found` at `now`;
/// confirmation needs `apart` milliseconds between sightings.
pub fn judge(
    stored: Option<Defect>,
    found: &Structure,
    source: i64,
    now: i64,
    apart: i64,
) -> Verdict {
    match (found, stored) {
        (&Structure::Damaged { offset, size }, stored) => match stored {
            Some(d) if d.source == source && d.offset == offset && d.size == size => {
                if now - d.seen_at >= apart {
                    Verdict::Confirm(d)
                } else {
                    Verdict::Wait(d)
                }
            }
            _ => Verdict::First(Defect {
                offset,
                size,
                seen_at: now,
                source,
            }),
        },
        (_, None) => Verdict::Nothing,
        (Structure::Sound, Some(_)) => Verdict::Clear,
        (Structure::Unrecognized | Structure::Inconclusive(_), Some(d)) if d.source != source => {
            Verdict::Clear
        }
        (Structure::Unrecognized | Structure::Inconclusive(_), Some(d)) => Verdict::Keep(d),
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[tokio::test]
    async fn omitted_container_headers_use_posted_identity_and_do_not_reuse_old_evidence() {
        use infinidysk_core::{BlobId, ByteRange, FilePart, MultipartFile, Segment};
        use infinidysk_nntp::{Body, Client, RepairKey};
        let nzb = BlobId::new();
        let mut first = boxes(&[(16, b"ftyp")]);
        first.resize(16, 0);
        let mut second = boxes(&[(16, b"moov")]);
        second.resize(16, 0);
        let client = Client::new([]);
        client.set_repairs(Box::new(move |key| {
            let RepairKey::Omitted(key) = key else {
                return None;
            };
            if key.nzb != nzb
                || key.generation != 0
                || key.set != [7; 16]
                || key.range
                    != (ByteRange {
                        start: 100,
                        end: 116,
                    })
            {
                return None;
            }
            let data = if key.file == [1; 16] {
                first.clone()
            } else if key.file == [2; 16] {
                second.clone()
            } else {
                return None;
            };
            Some(Box::pin(async move {
                Ok(Some(Body {
                    data: data.into(),
                    offset: 100,
                    file_size: 200,
                    part: Some(2),
                    total: Some(3),
                }))
            }))
        }));
        let articles = crate::check::Direct {
            client: &client,
            source: Some((nzb, 0)),
            meter: None,
        };
        let part = |file: u8| FilePart {
            segments: Segments {
                items: vec![Segment {
                    id: String::new(),
                    size: 16,
                    fallbacks: vec![],
                }],
                sizes_trusted: true,
                ..Segments::default()
            },
            offset: 100,
            data: ByteRange {
                start: 100,
                end: 116,
            },
            split_after: true,
            proof: Some(Par2Proof {
                set_id: Some([7; 16]),
                file_id: [file; 16],
                file_length: 200,
                slice_size: 4,
                slice_md5: vec![],
                slice_crc32: vec![],
                file_md5: [3; 16],
                md5_16k: [4; 16],
            }),
        };
        let mut meta = FileMeta::Multipart(MultipartFile {
            parts: vec![part(1), part(2)],
            crypt: None,
        });
        assert_eq!(
            walk(&articles, &meta, "source.mp4", None).await,
            Structure::Sound
        );
        let check = crate::first_check(&articles, &meta, "source.mp4", None, false)
            .await
            .unwrap()
            .unwrap();
        assert_eq!(check.status.state, infinidysk_db::HealthState::Healthy);
        let prior = source(&meta);
        let FileMeta::Multipart(file) = &mut meta else {
            unreachable!()
        };
        file.parts[1].proof.as_mut().unwrap().file_id = [9; 16];
        assert_ne!(source(&meta), prior);
        let old = Defect {
            source: prior,
            offset: 16,
            size: 4,
            seen_at: 0,
        };
        assert_eq!(
            judge(
                Some(old),
                &Structure::Inconclusive(String::new()),
                source(&meta),
                100,
                50
            ),
            Verdict::Clear
        );
        let FileMeta::Multipart(file) = &mut meta else {
            unreachable!()
        };
        file.parts[1].proof.as_mut().unwrap().file_id = [2; 16];
        file.parts[0].offset = u64::MAX - 4;
        let opts = crate::check::Options {
            percent: 100,
            tolerant: false,
            concurrency: 2,
            proven: false,
        };
        assert!(matches!(
            crate::check::run(&articles, &mut meta, "source.mp4", &opts)
                .await
                .0,
            crate::check::Outcome::Inconclusive(infinidysk_nntp::Error::Repair(_))
        ));
    }

    fn boxes(list: &[(u32, &[u8; 4])]) -> Vec<u8> {
        list.iter()
            .flat_map(|(size, kind)| size.to_be_bytes().into_iter().chain(kind.iter().copied()))
            .collect()
    }

    #[test]
    fn box_sizes_must_fit_the_file() {
        let ftyp = boxes(&[(24, b"ftyp")]);
        assert_eq!(step(&ftyp, 0, 100, true), Step::Next(24));
        assert_eq!(step(&ftyp, 0, 24, true), Step::End);
        assert_eq!(
            step(&boxes(&[(0, b"mdat")]), 24, 100, false),
            Step::End,
            "runs to EOF"
        );
        assert_eq!(
            step(&boxes(&[(4, b"moov")]), 24, 100, false),
            Step::Damaged(4),
            "under its header"
        );
        assert_eq!(
            step(&boxes(&[(77, b"moov")]), 24, 100, false),
            Step::Damaged(77),
            "past EOF"
        );
        assert_eq!(step(&boxes(&[(76, b"moov")]), 24, 100, false), Step::End);
        assert_eq!(
            step(&boxes(&[(24, b"moov")]), 0, 100, true),
            Step::Unrecognized,
            "no ftyp"
        );
        assert_eq!(
            step(&[0, 0, 0, 9, 0, 1, 2, 3], 24, 100, false),
            Step::Unrecognized,
            "binary kind"
        );
        assert_eq!(
            step(&[0, 0], 98, 100, false),
            Step::Unrecognized,
            "trailing bytes"
        );
        let mut large = boxes(&[(1, b"mdat")]);
        large.extend_from_slice(&(1u64 << 40).to_be_bytes());
        assert_eq!(step(&large, 24, 1 << 41, false), Step::Next(24 + (1 << 40)));
        assert_eq!(step(&large, 24, 1 << 30, false), Step::Damaged(1 << 40));
        assert_eq!(
            step(&large[..12], 24, 36, false),
            Step::Damaged(1),
            "cut extended size"
        );
        large[8..].copy_from_slice(&8u64.to_be_bytes());
        assert_eq!(
            step(&large, 24, 100, false),
            Step::Damaged(8),
            "under its extended header"
        );
    }

    #[test]
    fn confirms_only_the_same_defect_after_the_interval() {
        let found = Structure::Damaged { offset: 8, size: 4 };
        let first = Defect {
            offset: 8,
            size: 4,
            seen_at: 100,
            source: 1,
        };
        assert_eq!(judge(None, &found, 1, 100, 50), Verdict::First(first));
        assert_eq!(judge(Some(first), &found, 1, 149, 50), Verdict::Wait(first));
        assert_eq!(
            judge(Some(first), &found, 1, 150, 50),
            Verdict::Confirm(first)
        );
        let moved = Structure::Damaged { offset: 9, size: 4 };
        assert!(
            matches!(judge(Some(first), &moved, 1, 500, 50), Verdict::First(_)),
            "another defect"
        );
        assert!(
            matches!(judge(Some(first), &found, 2, 500, 50), Verdict::First(_)),
            "another source"
        );
        let failed = Structure::Inconclusive(String::new());
        assert_eq!(
            judge(Some(first), &failed, 1, 500, 50),
            Verdict::Keep(first)
        );
        assert_eq!(
            judge(Some(first), &Structure::Sound, 1, 500, 50),
            Verdict::Clear
        );
        assert_eq!(
            judge(Some(first), &Structure::Unrecognized, 2, 500, 50),
            Verdict::Clear
        );
        assert_eq!(judge(None, &failed, 1, 500, 50), Verdict::Nothing);
    }
}
