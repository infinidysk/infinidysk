//! PAR2 parsing, verification, and incremental repair (sans-I/O).
//!
//! [`read`] takes whatever prefix of a PAR2 file has been fetched and returns the file
//! descriptions and slice checksums of its recovery set. Damaged packets are skipped by
//! rescanning for the next packet magic, like other PAR2 clients do.

use std::collections::HashMap;
use std::ops::Range;

use md5::{Digest, Md5};

mod field;
mod plan;
mod repair;
mod verify;

pub use plan::{RepairSet, Source};
pub use repair::{Error, Input, Recovered, Recovery, Repair};
pub use verify::{Verified, Verifier};

const MAGIC: &[u8; 8] = b"PAR2\0PKT";
const HEADER: usize = 64;
/// Non-recovery packets larger than this are treated as damage.
const MAX_PACKET: usize = 16 << 20;
const MAIN: &[u8; 16] = b"PAR 2.0\0Main\0\0\0\0";
const FILE_DESC: &[u8; 16] = b"PAR 2.0\0FileDesc";
const IFSC: &[u8; 16] = b"PAR 2.0\0IFSC\0\0\0\0";
const RECOVERY: &[u8; 16] = b"PAR 2.0\0RecvSlic";
const UNICODE_NAME: &[u8; 16] = b"PAR 2.0\0UniFileN";

pub type Id = [u8; 16];

#[derive(Debug, Default)]
pub struct Index {
    pub set_id: Option<Id>,
    /// Zero when no main packet was found.
    pub slice_size: u64,
    /// Files in main-packet order (or packet order without a main packet).
    pub files: Vec<File>,
    /// A recovery-slice header was seen; metadata may still be incomplete.
    pub reached_recovery: bool,
    /// The buffer ended inside a packet; more bytes may yield more packets.
    pub truncated: bool,
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct File {
    pub id: Id,
    /// MD5 of the whole file.
    pub hash: Id,
    /// MD5 of the first 16 KiB, used to match obfuscated files.
    pub hash_16k: Id,
    pub length: u64,
    pub name: String,
    /// Per-slice checksums; empty when the IFSC packet was not seen.
    pub slices: Vec<Slice>,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct Slice {
    pub md5: Id,
    pub crc32: u32,
}

/// MD5 of the first 16 KiB of a file, as stored in [`File::hash_16k`].
pub fn hash_16k(start: &[u8]) -> Id {
    Md5::digest(&start[..start.len().min(16 << 10)]).into()
}

impl Slice {
    /// Checks one slice of file data; the last slice may be short and is zero-padded.
    pub fn matches(&self, slice_size: u64, data: &[u8]) -> bool {
        Self::checksum(slice_size, data).as_ref() == Some(self)
    }

    /// Hash one unpadded input slice, including PAR2's final zero padding.
    pub fn checksum(slice_size: u64, data: &[u8]) -> Option<Self> {
        static ZEROS: [u8; 4096] = [0; 4096];
        let mut pad = slice_size.checked_sub(data.len() as u64)?;
        let mut md5 = Md5::new();
        md5.update(data);
        let mut crc = infinidysk_yenc::crc32(0, data);
        while pad > 0 {
            let n = usize::try_from(pad).map_or(ZEROS.len(), |p| p.min(ZEROS.len()));
            md5.update(&ZEROS[..n]);
            crc = infinidysk_yenc::crc32(crc, &ZEROS[..n]);
            pad -= n as u64;
        }
        Some(Self {
            md5: md5.finalize().into(),
            crc32: crc,
        })
    }
}

struct Packet<'a> {
    set: Id,
    kind: &'a [u8],
    body: &'a [u8],
}

/// A verified packet of the scanned buffer.
struct Found {
    set: Id,
    kind: [u8; 16],
    body: Range<usize>,
}

/// Where scanning a growing buffer stopped, and what it accepted so far.
#[derive(Default)]
struct Scan {
    pos: usize,
    found: Vec<Found>,
    reached_recovery: bool,
    truncated: bool,
}

impl Scan {
    /// Scans `buf` from where the previous call stopped; `buf` extends that call's buffer.
    fn run(&mut self, buf: &[u8]) {
        self.truncated = false;
        while let Some(p) = buf[self.pos..]
            .windows(MAGIC.len())
            .position(|w| w == MAGIC)
            .map(|p| p + self.pos)
        {
            let Some(header) = buf.get(p..p + HEADER) else {
                (self.pos, self.truncated) = (p, true);
                return;
            };
            let len = usize::try_from(u64::from_le_bytes(
                header[8..16].try_into().expect("8-byte slice"),
            ))
            .unwrap_or(usize::MAX);
            let kind = &header[48..64];
            let recovery = kind == RECOVERY;
            if len < HEADER || len % 4 != 0 || (!recovery && len > MAX_PACKET) {
                self.pos = p + 1;
                continue;
            }
            if recovery {
                self.reached_recovery = true;
            }
            let Some(packet) = p.checked_add(len).and_then(|end| buf.get(p..end)) else {
                (self.pos, self.truncated) = (p, !recovery);
                return;
            };
            if <[u8; 16]>::from(Md5::digest(&packet[32..])) != header[16..32] {
                self.pos = p + 1;
                continue;
            }
            self.pos = p + len;
            self.found.push(Found {
                set: header[32..48].try_into().expect("16-byte slice"),
                kind: kind.try_into().expect("16-byte slice"),
                body: p + HEADER..p + len,
            });
        }
        // Magic may straddle the end of `buf`.
        self.pos = self.pos.max(buf.len().saturating_sub(MAGIC.len() - 1));
    }

    fn index(&self, buf: &[u8]) -> Index {
        let mut index = Index {
            reached_recovery: self.reached_recovery,
            truncated: self.truncated,
            ..Index::default()
        };
        let packets: Vec<Packet> = self
            .found
            .iter()
            .filter(|f| &f.kind != RECOVERY)
            .map(|f| Packet {
                set: f.set,
                kind: &f.kind,
                body: &buf[f.body.clone()],
            })
            .collect();
        build(&mut index, &packets);
        index
    }
}

/// Parses the PAR2 packets in `buf`, which may be any prefix of a PAR2 file.
pub fn read(buf: &[u8]) -> Index {
    let mut scan = Scan::default();
    scan.run(buf);
    scan.index(buf)
}

/// [`read`] over a PAR2 file fetched piece by piece, scanning each byte once.
#[derive(Default)]
pub struct Reader {
    buf: Vec<u8>,
    scan: Scan,
}

impl Reader {
    pub fn push(&mut self, data: &[u8]) {
        self.buf.extend_from_slice(data);
        self.scan.run(&self.buf);
    }

    /// A skipped article or a new volume cannot continue an incomplete packet.
    pub fn end_volume(&mut self) {
        self.scan.pos = self.buf.len();
        self.scan.reached_recovery = false;
        self.scan.truncated = false;
    }

    /// Bytes pushed so far.
    pub const fn buffered(&self) -> usize {
        self.buf.len()
    }

    /// Recovery sets with at least one checksum-validated packet.
    pub fn sets(&self) -> Vec<Id> {
        self.scan
            .found
            .iter()
            .map(|packet| packet.set)
            .collect::<std::collections::BTreeSet<_>>()
            .into_iter()
            .collect()
    }

    pub const fn reached_recovery(&self) -> bool {
        self.scan.reached_recovery
    }

    /// What [`read`] returns for everything pushed so far.
    pub fn index(&self) -> Index {
        self.scan.index(&self.buf)
    }

    /// Build a complete repair set from the buffered packets of one or more volumes.
    /// The caller admits buffered packet storage separately from solver workspace.
    pub fn repair_set(&self, set: Id) -> Result<RepairSet<'_>, Error> {
        plan::read(self, set, false)
    }

    /// File descriptions can verify complete hashes even without IFSC packets.
    pub fn verification_set(&self, set: Id) -> Result<RepairSet<'_>, Error> {
        plan::read(self, set, true)
    }

    pub fn recovery_slices(&self, set: Id, slice_size: usize) -> Result<Vec<Recovery<'_>>, Error> {
        plan::recovery(self, set, slice_size)
    }
}

fn build(index: &mut Index, packets: &[Packet]) {
    let main = packets
        .iter()
        .find(|p| p.kind == MAIN && p.body.len() >= 12);
    let Some(set) = main
        .or_else(|| packets.iter().find(|p| p.kind == FILE_DESC))
        .map(|p| p.set)
    else {
        return;
    };
    index.set_id = Some(set);
    let packets = || packets.iter().filter(move |p| p.set == set);
    let id = |b: &[u8]| -> Option<Id> { b.get(..16)?.try_into().ok() };

    let mut at: HashMap<Id, usize> = HashMap::new();
    for p in packets().filter(|p| p.kind == FILE_DESC) {
        let b = p.body;
        let (Some(fid), Some(hash), Some(hash_16k), Some(length)) = (
            id(b),
            b.get(16..).and_then(id),
            b.get(32..).and_then(id),
            b.get(48..56),
        ) else {
            continue;
        };
        if at.contains_key(&fid) {
            continue;
        }
        let name = String::from_utf8_lossy(b.get(56..).unwrap_or_default())
            .trim_end_matches('\0')
            .to_owned();
        let length = u64::from_le_bytes(length.try_into().expect("8-byte slice"));
        at.insert(fid, index.files.len());
        index.files.push(File {
            id: fid,
            hash,
            hash_16k,
            length,
            name,
            slices: Vec::new(),
        });
    }
    for p in packets() {
        let Some(file) = id(p.body)
            .and_then(|fid| at.get(&fid).copied())
            .map(|i| &mut index.files[i])
        else {
            continue;
        };
        if p.kind == IFSC && file.slices.is_empty() {
            file.slices = p.body[16..]
                .as_chunks::<20>()
                .0
                .iter()
                .map(|c| Slice {
                    md5: c[..16].try_into().expect("16-byte slice"),
                    crc32: u32::from_le_bytes(c[16..].try_into().expect("4-byte slice")),
                })
                .collect();
        } else if p.kind == UNICODE_NAME {
            let units: Vec<u16> = p.body[16..]
                .as_chunks::<2>()
                .0
                .iter()
                .map(|&u| u16::from_le_bytes(u))
                .collect();
            String::from_utf16_lossy(&units)
                .trim_end_matches('\0')
                .clone_into(&mut file.name);
        }
    }
    if let Some(main) = main {
        index.slice_size = u64::from_le_bytes(main.body[..8].try_into().expect("8-byte slice"));
        let order = main.body[12..].as_chunks::<16>().0;
        // Reversed so a repeated ID keeps its first position.
        let rank: HashMap<Id, usize> = order
            .iter()
            .enumerate()
            .rev()
            .filter(|(_, id)| at.contains_key(*id))
            .map(|(i, o)| (*o, i))
            .collect();
        index
            .files
            .sort_by_key(|f| rank.get(&f.id).copied().unwrap_or(usize::MAX));
    }
}
