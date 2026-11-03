//! 7z header reader.
//!
//! A 7z archive keeps its header at the end, pointed to by a 32-byte start
//! header. Split sets (`.7z.001`, `.002`, …) are raw byte splits, so every
//! offset here is into the volumes laid end to end.

use std::io::Read;
use std::ops::Range;

use infinidysk_yenc::crc32;
use lzma_rs::decompress::{Options, UnpackedSize};

use crate::Error;
use crate::crypt::{BLOCK, Cipher, Derived, Encryption, Kdf, MAX_LG2};

pub const SIGNATURE: &[u8] = b"7z\xbc\xaf\x27\x1c";

const START_HEADER: u64 = 32;
/// Upper bound for the (decoded) header; real headers are kilobytes.
const MAX_HEADER: u64 = 256 << 20;
/// Memory the parsed header may take; the same ceiling as the header bytes.
const MAX_INDEX: usize = 256 << 20;

const COPY: &[u8] = &[0x00];
const LZMA: &[u8] = &[0x03, 0x01, 0x01];
const LZMA2: &[u8] = &[0x21];
const AES: &[u8] = &[0x06, 0xf1, 0x07, 0x01];

/// One file in the archive.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Entry {
    pub name: String,
    pub size: u64,
    pub crc32: Option<u32>,
    /// Byte range of the content when it is stored verbatim, or stored and encrypted
    /// (then the range holds whole cipher blocks); `None` when compressed.
    pub data: Option<Range<u64>>,
    pub encrypted: bool,
    /// How to decrypt `data`.
    pub crypt: Option<Encryption>,
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub enum Step {
    /// Call [`Reader::next`] again with bytes covering `offset..offset + len`.
    Need { offset: u64, len: u64 },
    /// Headers are encrypted: derive this key and pass it to [`Reader::add_key`], then
    /// call [`Reader::next`] again.
    Key(Kdf),
    /// All entries; later calls return an empty list.
    Done(Vec<Entry>),
}

#[derive(Debug, Default)]
pub struct Reader {
    state: State,
    keys: Vec<(Kdf, Derived)>,
}

#[derive(Debug, Default)]
enum State {
    #[default]
    Start,
    Header {
        offset: u64,
        len: u64,
        crc: u32,
    },
    Packed {
        offset: u64,
        len: u64,
        folder: Folder,
    },
    Done,
}

impl Reader {
    pub fn new() -> Self {
        Self::default()
    }

    /// Supplies the key a [`Step::Key`] asked for.
    pub fn add_key(&mut self, kdf: Kdf, derived: Derived) {
        self.keys.push((kdf, derived));
    }

    fn key(&self, kdf: &Kdf) -> Option<Derived> {
        self.keys
            .iter()
            .find(|(k, _)| k == kdf)
            .map(|(_, d)| d.clone())
    }

    /// Continues reading using `buf`, which holds archive bytes starting at `at`.
    pub fn next(&mut self, at: u64, buf: &[u8]) -> Result<Step, Error> {
        loop {
            let (offset, len) = match &self.state {
                State::Start => (0, START_HEADER),
                State::Header { offset, len, .. } | State::Packed { offset, len, .. } => {
                    (*offset, *len)
                }
                State::Done => return Ok(Step::Done(Vec::new())),
            };
            let Some(bytes) = window(at, buf, offset, len) else {
                return Ok(Step::Need { offset, len });
            };
            let (header, packed) = match std::mem::take(&mut self.state) {
                State::Start => {
                    self.state = start_header(bytes)?;
                    continue;
                }
                State::Header { crc, .. } => {
                    if crc32(0, bytes) != crc {
                        return Err(Error::HeaderCrc(offset));
                    }
                    (parse(bytes, offset, MAX_INDEX)?, false)
                }
                State::Packed { folder, .. } => {
                    let key = match aes(&folder, offset)? {
                        None => None,
                        Some(crypt) => {
                            let Some(derived) = self.key(&crypt.kdf) else {
                                let kdf = crypt.kdf;
                                self.state = State::Packed {
                                    offset,
                                    len,
                                    folder,
                                };
                                return Ok(Step::Key(kdf));
                            };
                            Some((crypt, derived))
                        }
                    };
                    let parsed = decode(&folder, bytes, offset, key.as_ref())
                        .and_then(|decoded| parse(&decoded, offset, MAX_INDEX));
                    let header = match parsed {
                        // A wrong key decrypts to garbage.
                        Err(Error::Corrupt { .. } | Error::HeaderCrc(_)) if key.is_some() => {
                            return Err(Error::WrongPassword);
                        }
                        parsed => parsed?,
                    };
                    (header, true)
                }
                State::Done => unreachable!("handled above"),
            };
            match header {
                Header::Entries(entries) => {
                    self.state = State::Done;
                    return Ok(Step::Done(entries));
                }
                // 7-Zip reads one encoded header, never a chain of them.
                Header::Encoded { offset, .. } if packed => {
                    return Err(Error::Corrupt {
                        offset,
                        reason: "nested encoded header",
                    });
                }
                Header::Encoded {
                    offset,
                    len,
                    folder,
                } => {
                    self.state = State::Packed {
                        offset,
                        len,
                        folder,
                    }
                }
            }
        }
    }
}

fn window(at: u64, buf: &[u8], offset: u64, len: u64) -> Option<&[u8]> {
    let start = usize::try_from(offset.checked_sub(at)?).ok()?;
    buf.get(start..start.checked_add(usize::try_from(len).ok()?)?)
}

fn start_header(b: &[u8]) -> Result<State, Error> {
    if !b.starts_with(SIGNATURE) {
        return Err(Error::Signature);
    }
    let u64_at = |i: usize| u64::from_le_bytes(b[i..i + 8].try_into().expect("8-byte slice"));
    if crc32(0, &b[12..32]) != u32::from_le_bytes(b[8..12].try_into().expect("4-byte slice")) {
        return Err(Error::HeaderCrc(0));
    }
    let (offset, len) = (u64_at(12), u64_at(20));
    if len == 0 {
        return Ok(State::Done);
    }
    if len > MAX_HEADER {
        return Err(Error::Corrupt {
            offset: 0,
            reason: "header size",
        });
    }
    let offset = START_HEADER.checked_add(offset).ok_or(Error::Corrupt {
        offset: 0,
        reason: "header offset",
    })?;
    let crc = u32::from_le_bytes(b[28..32].try_into().expect("4-byte slice"));
    Ok(State::Header { offset, len, crc })
}

/// The folder's AES coder parameters, when it has one.
fn aes(folder: &Folder, offset: u64) -> Result<Option<Encryption>, Error> {
    let corrupt = |reason| Error::Corrupt { offset, reason };
    let Some(coder) = folder.coders.iter().find(|c| c.id == AES) else {
        return Ok(None);
    };
    let p = &coder.props;
    let b0 = *p.first().ok_or_else(|| corrupt("AES properties"))?;
    let cycles = b0 & 0x3f;
    if cycles > MAX_LG2 && cycles != 0x3f {
        return Err(corrupt("key derivation cost"));
    }
    let (mut salt, mut iv) = (Vec::new(), [0u8; BLOCK]);
    if b0 & 0xc0 != 0 {
        let b1 = *p.get(1).ok_or_else(|| corrupt("AES properties"))?;
        let salt_len = usize::from((b0 >> 7) & 1) + usize::from(b1 >> 4);
        let iv_len = usize::from((b0 >> 6) & 1) + usize::from(b1 & 0x0f);
        let rest = p
            .get(2..2 + salt_len + iv_len)
            .ok_or_else(|| corrupt("AES properties"))?;
        salt = rest[..salt_len].to_vec();
        iv[..iv_len].copy_from_slice(&rest[salt_len..]);
    }
    Ok(Some(Encryption {
        kdf: Kdf::SevenZip { salt, cycles },
        iv: Some(iv),
    }))
}

fn decode(
    folder: &Folder,
    packed: &[u8],
    offset: u64,
    key: Option<&(Encryption, Derived)>,
) -> Result<Vec<u8>, Error> {
    let corrupt = |reason| Error::Corrupt { offset, reason };
    let mut packed = packed.to_vec();
    let mut coders: Vec<&Coder> = folder.coders.iter().collect();
    if let Some(i) = coders.iter().position(|c| c.id == AES) {
        let (crypt, derived) = key.ok_or_else(|| corrupt("missing header key"))?;
        if !packed.len().is_multiple_of(BLOCK) {
            return Err(corrupt("encrypted header size"));
        }
        Cipher::new(&derived.key).decrypt(&mut crypt.iv(derived), &mut packed);
        let size = folder.unpack_sizes.get(i).copied().unwrap_or(u64::MAX);
        packed.truncate(usize::try_from(size).unwrap_or(usize::MAX));
        coders.remove(i);
    }
    let size = folder.unpack_size();
    if size > MAX_HEADER {
        return Err(corrupt("header size"));
    }
    let mut out = Vec::with_capacity(usize::try_from(size).unwrap_or_default());
    let decoded = match coders.as_slice() {
        [] => {
            out = packed;
            Ok(())
        }
        [coder] => match coder.id.as_slice() {
            COPY => {
                out = packed;
                Ok(())
            }
            LZMA => {
                let options = Options {
                    unpacked_size: UnpackedSize::UseProvided(Some(size)),
                    ..Options::default()
                };
                lzma_rs::lzma_decompress_with_options(
                    &mut coder.props.as_slice().chain(&packed[..]),
                    &mut out,
                    &options,
                )
            }
            LZMA2 => lzma_rs::lzma2_decompress(&mut &packed[..], &mut out),
            _ => return Err(corrupt("unsupported header coder")),
        },
        _ => return Err(corrupt("unsupported header coders")),
    };
    decoded.map_err(|_| corrupt("header does not decode"))?;
    if out.len() as u64 != size || folder.crc.is_some_and(|crc| crc != crc32(0, &out)) {
        return Err(corrupt("decoded header mismatch"));
    }
    Ok(out)
}

enum Header {
    Entries(Vec<Entry>),
    Encoded {
        offset: u64,
        len: u64,
        folder: Folder,
    },
}

#[derive(Debug, Default)]
struct Coder {
    id: Vec<u8>,
    props: Vec<u8>,
}

#[derive(Debug, Default)]
struct Folder {
    coders: Vec<Coder>,
    packed_streams: usize,
    /// Output stream index that leaves the folder (not bound to another coder).
    main_out: usize,
    unpack_sizes: Vec<u64>,
    crc: Option<u32>,
}

impl Folder {
    fn unpack_size(&self) -> u64 {
        self.unpack_sizes
            .get(self.main_out)
            .copied()
            .unwrap_or_default()
    }

    fn stored(&self) -> bool {
        matches!(self.coders.as_slice(), [c] if c.id == COPY) && self.packed_streams == 1
    }
}

/// Size and CRC of one file packed into a folder.
type Substream = (u64, Option<u32>);
/// One row of the flattened stream table `entries` builds.
type Stream = (
    u64,
    Option<u32>,
    Option<Range<u64>>,
    bool,
    Option<Encryption>,
);

/// Index bytes charged per folder, including its default single substream.
const FOLDER_INDEX: usize = size_of::<Folder>()
    + size_of::<Vec<Substream>>()
    + size_of::<Substream>()
    + size_of::<usize>()
    + size_of::<bool>()
    + size_of::<Option<u32>>();

#[derive(Debug, Default)]
struct Streams {
    pack_pos: u64,
    pack_sizes: Vec<u64>,
    folders: Vec<Folder>,
    substreams: Vec<Vec<Substream>>,
}

mod id {
    pub const END: u64 = 0x00;
    pub const HEADER: u64 = 0x01;
    pub const ARCHIVE_PROPERTIES: u64 = 0x02;
    pub const ADDITIONAL_STREAMS: u64 = 0x03;
    pub const MAIN_STREAMS: u64 = 0x04;
    pub const FILES: u64 = 0x05;
    pub const PACK_INFO: u64 = 0x06;
    pub const UNPACK_INFO: u64 = 0x07;
    pub const SUBSTREAMS: u64 = 0x08;
    pub const SIZE: u64 = 0x09;
    pub const CRC: u64 = 0x0a;
    pub const FOLDER: u64 = 0x0b;
    pub const UNPACK_SIZE: u64 = 0x0c;
    pub const NUM_UNPACK_STREAM: u64 = 0x0d;
    pub const EMPTY_STREAM: u64 = 0x0e;
    pub const EMPTY_FILE: u64 = 0x0f;
    pub const NAME: u64 = 0x11;
    pub const ATTRIBUTES: u64 = 0x15;
    pub const ENCODED_HEADER: u64 = 0x17;
}

fn parse(b: &[u8], offset: u64, budget: usize) -> Result<Header, Error> {
    let mut c = Cur { b, offset, budget };
    match c.number()? {
        id::HEADER => {}
        id::ENCODED_HEADER => {
            let streams = streams(&mut c)?;
            let (Some(&len), Some(folder)) = (
                streams.pack_sizes.first(),
                streams.folders.into_iter().next(),
            ) else {
                return Err(c.corrupt("encoded header without streams"));
            };
            let offset = START_HEADER
                .checked_add(streams.pack_pos)
                .ok_or_else(|| c.corrupt("encoded header offset"))?;
            if len > MAX_HEADER {
                return Err(c.corrupt("encoded header size"));
            }
            return Ok(Header::Encoded {
                offset,
                len,
                folder,
            });
        }
        _ => return Err(c.corrupt("not a header")),
    }
    let mut main = Streams::default();
    let mut files = Vec::new();
    loop {
        match c.number()? {
            id::END => break,
            id::ARCHIVE_PROPERTIES => loop {
                if c.number()? == id::END {
                    break;
                }
                let len = c.count()?;
                c.take(len)?;
            },
            id::ADDITIONAL_STREAMS => {
                streams(&mut c)?;
            }
            id::MAIN_STREAMS => main = streams(&mut c)?,
            id::FILES => files = files_info(&mut c)?,
            _ => return Err(c.corrupt("unknown header property")),
        }
    }
    entries(&main, files, &c).map(Header::Entries)
}

fn streams(c: &mut Cur) -> Result<Streams, Error> {
    let mut s = Streams::default();
    let mut substreams = None;
    loop {
        match c.number()? {
            id::END => break,
            id::PACK_INFO => {
                s.pack_pos = c.number()?;
                let n = c.count()?;
                c.charge(
                    n,
                    size_of::<u64>() + size_of::<bool>() + size_of::<Option<u32>>(),
                )?;
                loop {
                    match c.number()? {
                        id::END => break,
                        id::SIZE => {
                            s.pack_sizes = (0..n).map(|_| c.number()).collect::<Result<_, _>>()?;
                        }
                        id::CRC => {
                            digests(c, n)?;
                        }
                        _ => return Err(c.corrupt("unknown pack property")),
                    }
                }
            }
            id::UNPACK_INFO => unpack_info(c, &mut s.folders)?,
            id::SUBSTREAMS => substreams = Some(substreams_info(c, &s.folders)?),
            _ => return Err(c.corrupt("unknown streams property")),
        }
    }
    s.substreams = if let Some(substreams) = substreams {
        substreams
    } else {
        c.charge(s.folders.len(), size_of::<Stream>())?;
        s.folders
            .iter()
            .map(|f| vec![(f.unpack_size(), f.crc)])
            .collect()
    };
    Ok(s)
}

fn unpack_info(c: &mut Cur, folders: &mut Vec<Folder>) -> Result<(), Error> {
    if c.number()? != id::FOLDER {
        return Err(c.corrupt("expected folders"));
    }
    let n = c.count()?;
    c.charge(n, FOLDER_INDEX)?;
    if c.byte()? != 0 {
        return Err(c.corrupt("external folders"));
    }
    *folders = (0..n).map(|_| folder(c)).collect::<Result<_, _>>()?;
    if c.number()? != id::UNPACK_SIZE {
        return Err(c.corrupt("expected unpack sizes"));
    }
    for f in folders.iter_mut() {
        let outs = f.unpack_sizes.len();
        f.unpack_sizes = (0..outs).map(|_| c.number()).collect::<Result<_, _>>()?;
    }
    loop {
        match c.number()? {
            id::END => return Ok(()),
            id::CRC => {
                for (f, crc) in folders.iter_mut().zip(digests(c, n)?) {
                    f.crc = crc;
                }
            }
            _ => return Err(c.corrupt("unknown unpack property")),
        }
    }
}

fn folder(c: &mut Cur) -> Result<Folder, Error> {
    let coders = c.small(64)?;
    let mut f = Folder::default();
    let (mut ins, mut outs) = (0, 0);
    for _ in 0..coders {
        let flags = c.byte()?;
        if flags & 0x80 != 0 {
            return Err(c.corrupt("alternative coder methods"));
        }
        let id = c.take(usize::from(flags & 0x0f))?.to_vec();
        let (i, o) = if flags & 0x10 != 0 {
            (c.small(64)?, c.small(64)?)
        } else {
            (1, 1)
        };
        let props = if flags & 0x20 != 0 {
            let len = c.count()?;
            c.take(len)?.to_vec()
        } else {
            Vec::new()
        };
        ins += i;
        outs += o;
        c.charge(1, size_of::<Coder>())?;
        f.coders.push(Coder { id, props });
    }
    c.charge(outs, size_of::<bool>() + 2 * size_of::<u64>())?;
    let mut bound_outs = vec![false; outs];
    for _ in 1..outs {
        c.small(ins)?; // in index
        let out = c.small(outs.saturating_sub(1))?;
        bound_outs[out] = true;
    }
    f.packed_streams = ins
        .checked_sub(outs.saturating_sub(1))
        .ok_or_else(|| c.corrupt("bind pairs"))?;
    if f.packed_streams > 1 {
        for _ in 0..f.packed_streams {
            c.small(ins)?;
        }
    }
    f.main_out = bound_outs
        .iter()
        .position(|bound| !bound)
        .unwrap_or_default();
    f.unpack_sizes = vec![0; outs];
    Ok(f)
}

fn substreams_info(c: &mut Cur, folders: &[Folder]) -> Result<Vec<Vec<Substream>>, Error> {
    let mut counts = vec![1; folders.len()];
    let mut prop = c.number()?;
    if prop == id::NUM_UNPACK_STREAM {
        for n in &mut counts {
            *n = c.count()?;
            c.charge(
                *n,
                size_of::<Substream>()
                    + size_of::<bool>()
                    + size_of::<Option<u32>>()
                    + size_of::<Stream>(),
            )?;
        }
        prop = c.number()?;
    }
    let mut sizes: Vec<Vec<Substream>> = Vec::with_capacity(folders.len());
    let explicit = prop == id::SIZE;
    for (f, &n) in folders.iter().zip(&counts) {
        let mut v = Vec::with_capacity(n);
        let mut sum = 0u64;
        if explicit {
            for _ in 1..n {
                let size = c.number()?;
                sum = sum
                    .checked_add(size)
                    .ok_or_else(|| c.corrupt("substream sizes"))?;
                v.push((size, None));
            }
        }
        if n > 0 {
            let last = f
                .unpack_size()
                .checked_sub(sum)
                .ok_or_else(|| c.corrupt("substream sizes"))?;
            v.push((last, if n == 1 { f.crc } else { None }));
        }
        sizes.push(v);
    }
    if explicit {
        prop = c.number()?;
    }
    loop {
        match prop {
            id::END => return Ok(sizes),
            id::CRC => {
                let missing = sizes
                    .iter()
                    .flatten()
                    .filter(|(_, crc)| crc.is_none())
                    .count();
                let mut crcs = digests(c, missing)?.into_iter();
                for slot in sizes.iter_mut().flatten().filter(|(_, crc)| crc.is_none()) {
                    slot.1 = crcs.next().flatten();
                }
            }
            _ => return Err(c.corrupt("unknown substreams property")),
        }
        prop = c.number()?;
    }
}

fn digests(c: &mut Cur, n: usize) -> Result<Vec<Option<u32>>, Error> {
    let defined = bits_or_all(c, n)?;
    defined
        .into_iter()
        .map(|d| d.then(|| c.u32()).transpose())
        .collect()
}

fn bits_or_all(c: &mut Cur, n: usize) -> Result<Vec<bool>, Error> {
    if c.byte()? != 0 {
        Ok(vec![true; n])
    } else {
        bits(c, n)
    }
}

fn bits(c: &mut Cur, n: usize) -> Result<Vec<bool>, Error> {
    let bytes = c.take(n.div_ceil(8))?;
    Ok((0..n)
        .map(|i| bytes[i / 8] & (0x80 >> (i % 8)) != 0)
        .collect())
}

struct FileInfo {
    name: String,
    has_stream: bool,
    dir: bool,
}

fn files_info(c: &mut Cur) -> Result<Vec<FileInfo>, Error> {
    let n = c.count()?;
    c.charge(
        n,
        3 * size_of::<bool>() + size_of::<String>() + size_of::<FileInfo>() + size_of::<Entry>(),
    )?;
    let mut empty_stream = vec![false; n];
    let mut empty_file = Vec::new();
    let mut names = vec![String::new(); n];
    let mut dir_attr = vec![false; n];
    loop {
        let prop = c.number()?;
        if prop == id::END {
            break;
        }
        let len = c.count()?;
        let mut p = Cur {
            b: c.take(len)?,
            offset: c.offset,
            budget: 0,
        };
        match prop {
            id::EMPTY_STREAM => empty_stream = bits(&mut p, n)?,
            id::EMPTY_FILE => {
                empty_file = bits(&mut p, empty_stream.iter().filter(|&&e| e).count())?;
            }
            id::NAME => {
                if p.byte()? != 0 {
                    return Err(p.corrupt("external names"));
                }
                let units: Vec<u16> =
                    p.b.as_chunks::<2>()
                        .0
                        .iter()
                        .map(|&u| u16::from_le_bytes(u))
                        .collect();
                for (slot, name) in names.iter_mut().zip(units.split(|&u| u == 0)) {
                    *slot = String::from_utf16_lossy(name);
                }
            }
            id::ATTRIBUTES => {
                let defined = bits_or_all(&mut p, n)?;
                if p.byte()? != 0 {
                    return Err(p.corrupt("external attributes"));
                }
                for (slot, defined) in dir_attr.iter_mut().zip(defined) {
                    *slot = defined && p.u32()? & 0x10 != 0;
                }
            }
            _ => {}
        }
    }
    let mut empty_files = empty_file.into_iter();
    Ok(names
        .into_iter()
        .zip(empty_stream)
        .zip(dir_attr)
        .map(|((name, empty), dir_attr)| {
            let dir = empty && (!empty_files.next().unwrap_or(false) || dir_attr);
            FileInfo {
                name: name.replace('\\', "/"),
                has_stream: !empty,
                dir,
            }
        })
        .collect())
}

fn entries(s: &Streams, files: Vec<FileInfo>, c: &Cur) -> Result<Vec<Entry>, Error> {
    // (folder, substream) for each stream in file order, plus where it starts.
    let overflow = || c.corrupt("pack offsets");
    let base = START_HEADER.checked_add(s.pack_pos).ok_or_else(overflow)?;
    let mut streams: Vec<Stream> = Vec::with_capacity(s.substreams.iter().map(Vec::len).sum());
    let mut pack_index = 0;
    for (f, subs) in s.folders.iter().zip(&s.substreams) {
        let mut start = s
            .pack_sizes
            .get(..pack_index)
            .ok_or_else(|| c.corrupt("pack streams"))?
            .iter()
            .try_fold(base, |acc, &n| acc.checked_add(n))
            .ok_or_else(overflow)?;
        let first_pack = pack_index;
        pack_index += f.packed_streams;
        let encrypted = f.coders.iter().any(|c| c.id == AES);
        // Only a folder holding one file, encrypted but not compressed, is readable in
        // place: CBC needs the chain from the folder's start.
        let sealed = encrypted
            && f.packed_streams == 1
            && f.coders.iter().all(|c| c.id == AES || c.id == COPY)
            && f.coders.len() <= 2;
        if let ([(size, crc)], true) = (subs.as_slice(), sealed) {
            let packed = *s
                .pack_sizes
                .get(first_pack)
                .ok_or_else(|| c.corrupt("pack streams"))?;
            let crypt = aes(f, c.offset)?;
            let fits = packed >= *size && packed % BLOCK as u64 == 0;
            let end = start.checked_add(packed).ok_or_else(overflow)?;
            streams.push((*size, *crc, fits.then_some(start..end), true, crypt));
            continue;
        }
        for &(size, crc) in subs {
            let end = start.checked_add(size).ok_or_else(overflow)?;
            streams.push((size, crc, f.stored().then_some(start..end), encrypted, None));
            start = end;
        }
    }
    let mut streams = streams.into_iter();
    let mut out = Vec::with_capacity(files.len());
    for file in files {
        if file.dir {
            continue;
        }
        let (size, crc32, data, encrypted, crypt) = if file.has_stream {
            streams
                .next()
                .ok_or_else(|| c.corrupt("more files than streams"))?
        } else {
            (0, None, Some(0..0), false, None)
        };
        out.push(Entry {
            name: file.name,
            size,
            crc32,
            data,
            encrypted,
            crypt,
        });
    }
    Ok(out)
}

struct Cur<'a> {
    b: &'a [u8],
    offset: u64,
    budget: usize,
}

impl<'a> Cur<'a> {
    const fn corrupt(&self, reason: &'static str) -> Error {
        Error::Corrupt {
            offset: self.offset,
            reason,
        }
    }

    fn take(&mut self, n: usize) -> Result<&'a [u8], Error> {
        let (head, tail) = self
            .b
            .split_at_checked(n)
            .ok_or_else(|| self.corrupt("header too short"))?;
        self.b = tail;
        Ok(head)
    }

    fn byte(&mut self) -> Result<u8, Error> {
        Ok(self.take(1)?[0])
    }

    /// Takes `n * each` bytes from the parse budget, before they are allocated.
    fn charge(&mut self, n: usize, each: usize) -> Result<(), Error> {
        self.budget = n
            .checked_mul(each)
            .and_then(|bytes| self.budget.checked_sub(bytes))
            .ok_or_else(|| self.corrupt("header too large to index"))?;
        Ok(())
    }

    /// A count of at most `max`.
    fn small(&mut self, max: usize) -> Result<usize, Error> {
        let n = self.number()?;
        usize::try_from(n)
            .ok()
            .filter(|&n| n <= max)
            .ok_or_else(|| self.corrupt("count"))
    }

    /// A count bounded by what the remaining bytes could describe, so corrupt
    /// input cannot trigger huge allocations.
    fn count(&mut self) -> Result<usize, Error> {
        let max = self.b.len().saturating_mul(8);
        self.small(max)
    }

    fn u32(&mut self) -> Result<u32, Error> {
        Ok(u32::from_le_bytes(
            self.take(4)?.try_into().expect("4-byte slice"),
        ))
    }

    /// 7z NUMBER: leading one bits in the first byte count the extra bytes.
    fn number(&mut self) -> Result<u64, Error> {
        let first = self.byte()?;
        let extra = first.leading_ones() as usize;
        let bytes = self.take(extra)?;
        let mut value = 0u64;
        for (i, &b) in bytes.iter().enumerate() {
            value |= u64::from(b) << (8 * i);
        }
        if extra < 8 {
            let high = u64::from(first & (0xff >> (extra + 1)));
            value |= high << (8 * extra);
        }
        Ok(value)
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn too_large(parsed: &Result<Header, Error>) -> bool {
        matches!(
            parsed,
            Err(Error::Corrupt {
                reason: "header too large to index",
                ..
            })
        )
    }

    #[test]
    fn empty_folders_are_charged_before_they_are_collected() {
        let mut bytes = vec![0x01, 0x04, 0x07, 0x0b, 0x64, 0x00];
        bytes.extend([0x00; 100]);
        bytes.extend([0x0c, 0x00, 0x00, 0x00]);
        assert!(too_large(&parse(&bytes, 0, 1_000)));
        assert!(matches!(
            parse(&bytes, 0, MAX_INDEX),
            Ok(Header::Entries(e)) if e.is_empty()
        ));
    }

    #[test]
    fn implicit_substreams_are_charged_for_the_stream_table() {
        let mut bytes = vec![0x01, 0x04, 0x07, 0x0b, 0x64, 0x00];
        bytes.extend([0x00; 100]);
        bytes.extend([0x0c, 0x00, 0x00, 0x00]);
        assert!(too_large(&parse(&bytes, 0, 100 * FOLDER_INDEX)));
        assert!(matches!(
            parse(&bytes, 0, 100 * (FOLDER_INDEX + size_of::<Stream>())),
            Ok(Header::Entries(e)) if e.is_empty()
        ));
    }

    #[test]
    fn files_are_charged_before_they_are_allocated() {
        let bytes = [0x01, 0x05, 0x08, 0x00, 0x00];
        assert!(too_large(&parse(&bytes, 0, 100)));
        assert!(matches!(
            parse(&bytes, 0, MAX_INDEX),
            Err(Error::Corrupt {
                reason: "more files than streams",
                ..
            })
        ));
    }
}
