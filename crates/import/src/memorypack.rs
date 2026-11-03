//! Reader for the `MemoryPack` wire format, limited to what C# metadata blobs use:
//! version-tolerant objects, strings, arrays and unmanaged values.

use std::cell::{Cell, RefCell};
use std::io::{self, Read};

use uuid::Uuid;

#[derive(Debug, thiserror::Error)]
pub enum Error {
    #[error("{0}")]
    Io(io::Error),
    #[error("metadata allocation failed: {0}")]
    Allocation(#[from] std::collections::TryReserveError),
    #[error("truncated")]
    Truncated,
    #[error("trailing data")]
    Trailing,
    #[error("{0}")]
    Invalid(&'static str),
}

pub struct Reader<'a> {
    input: &'a mut dyn Read,
    remaining: Option<u64>,
}

impl From<io::Error> for Error {
    fn from(error: io::Error) -> Self {
        if error.kind() == io::ErrorKind::UnexpectedEof {
            Self::Truncated
        } else {
            Self::Io(error)
        }
    }
}

impl Read for Reader<'_> {
    fn read(&mut self, buf: &mut [u8]) -> io::Result<usize> {
        let length = self.remaining.map_or(buf.len(), |remaining| {
            usize::try_from(remaining)
                .unwrap_or(usize::MAX)
                .min(buf.len())
        });
        let read = self.input.read(&mut buf[..length])?;
        if let Some(remaining) = &mut self.remaining {
            *remaining -= read as u64;
        }
        Ok(read)
    }
}

impl<'a> Reader<'a> {
    pub fn new(input: &'a mut dyn Read) -> Self {
        Self {
            input,
            remaining: None,
        }
    }

    fn read_bytes(&mut self, length: usize) -> Result<Vec<u8>, Error> {
        if self
            .remaining
            .is_some_and(|remaining| length as u64 > remaining)
        {
            return Err(Error::Truncated);
        }
        let mut bytes = Vec::new();
        let mut chunk = [0; 8192];
        while bytes.len() < length {
            let count = (length - bytes.len()).min(chunk.len());
            self.read_exact(&mut chunk[..count])?;
            bytes.try_reserve(count)?;
            bytes.extend_from_slice(&chunk[..count]);
        }
        Ok(bytes)
    }

    fn fixed<const N: usize>(&mut self) -> Result<[u8; N], Error> {
        let mut out = [0; N];
        self.read_exact(&mut out)?;
        Ok(out)
    }

    pub fn finish(&mut self) -> Result<(), Error> {
        if self.read(&mut [0])? == 0 {
            if self.remaining.is_some_and(|remaining| remaining != 0) {
                return Err(Error::Truncated);
            }
            Ok(())
        } else {
            Err(Error::Trailing)
        }
    }

    pub fn bool(&mut self) -> Result<bool, Error> {
        Ok(self.fixed::<1>()?[0] != 0)
    }

    pub fn i32(&mut self) -> Result<i32, Error> {
        self.fixed().map(i32::from_le_bytes)
    }

    pub fn i64(&mut self) -> Result<i64, Error> {
        self.fixed().map(i64::from_le_bytes)
    }

    /// A .NET `Guid` in its in-memory layout.
    pub fn guid(&mut self) -> Result<Uuid, Error> {
        self.fixed().map(Uuid::from_bytes_le)
    }

    /// `Nullable<bool>`: the raw struct, `hasValue` then the value.
    pub fn opt_bool(&mut self) -> Result<Option<bool>, Error> {
        let [has, value] = self.fixed()?;
        Ok((has != 0).then_some(value != 0))
    }

    /// `Nullable<byte>`.
    pub fn opt_u8(&mut self) -> Result<Option<u8>, Error> {
        let [has, value] = self.fixed()?;
        Ok((has != 0).then_some(value))
    }

    /// `Nullable<long>`: `hasValue`, 7 bytes of padding, the value.
    pub fn opt_i64(&mut self) -> Result<Option<i64>, Error> {
        let raw: [u8; 16] = self.fixed()?;
        let mut value = [0; 8];
        value.copy_from_slice(&raw[8..]);
        Ok((raw[0] != 0).then_some(i64::from_le_bytes(value)))
    }

    /// Compact integer: one signed byte, or a type code then the little-endian value.
    fn varint(&mut self) -> Result<i64, Error> {
        let code = i8::from_le_bytes(self.fixed()?);
        Ok(match code {
            -120..=127 => i64::from(code),
            -121 => i64::from(self.fixed::<1>()?[0]),
            -122 => i64::from(i8::from_le_bytes(self.fixed()?)),
            -123 => i64::from(u16::from_le_bytes(self.fixed()?)),
            -124 => i64::from(i16::from_le_bytes(self.fixed()?)),
            -125 => i64::from(u32::from_le_bytes(self.fixed()?)),
            -126 => i64::from(i32::from_le_bytes(self.fixed()?)),
            -127 => i64::try_from(u64::from_le_bytes(self.fixed()?))
                .map_err(|_| Error::Invalid("varint overflow"))?,
            _ => i64::from_le_bytes(self.fixed()?),
        })
    }

    /// Collection header: element count, `None` for null.
    fn count(&mut self) -> Result<Option<usize>, Error> {
        match self.i32()? {
            -1 => Ok(None),
            n => usize::try_from(n)
                .map(Some)
                .map_err(|_| Error::Invalid("negative length")),
        }
    }

    pub fn string(&mut self) -> Result<Option<String>, Error> {
        let header = self.i32()?;
        if header == -1 {
            return Ok(None);
        }
        if header >= 0 {
            let units = usize::try_from(header).map_err(|_| Error::Invalid("length"))?;
            let raw = self.read_bytes(units.checked_mul(2).ok_or(Error::Truncated)?)?;
            let utf16 = raw
                .as_chunks::<2>()
                .0
                .iter()
                .map(|chunk| u16::from_le_bytes(*chunk));
            let mut text = String::new();
            for character in char::decode_utf16(utf16) {
                let character = character.map_err(|_| Error::Invalid("bad UTF-16 string"))?;
                text.try_reserve(character.len_utf8())?;
                text.push(character);
            }
            return Ok(Some(text));
        }
        // UTF-8: the complement of the byte count, then the UTF-16 length.
        let bytes = usize::try_from(!header).map_err(|_| Error::Invalid("length"))?;
        self.i32()?;
        let raw = self.read_bytes(bytes)?;
        String::from_utf8(raw)
            .map(Some)
            .map_err(|_| Error::Invalid("bad UTF-8 string"))
    }

    /// `string[]` whose elements are never null.
    pub fn strings(&mut self) -> Result<Option<Vec<String>>, Error> {
        self.list(|r| r.string()?.ok_or(Error::Invalid("null string element")))
    }

    pub fn list<T>(
        &mut self,
        mut item: impl FnMut(&mut Self) -> Result<T, Error>,
    ) -> Result<Option<Vec<T>>, Error> {
        let Some(n) = self.count()? else {
            return Ok(None);
        };
        let mut out = Vec::new();
        for _ in 0..n {
            let value = item(self)?;
            out.try_reserve(1)?;
            out.push(value);
        }
        Ok(Some(out))
    }

    fn unmanaged<const N: usize, T>(
        &mut self,
        from: impl Fn([u8; N]) -> T,
    ) -> Result<Option<Vec<T>>, Error> {
        let Some(n) = self.count()? else {
            return Ok(None);
        };
        let mut values = Vec::new();
        for _ in 0..n {
            let value = from(self.fixed()?);
            values.try_reserve(1)?;
            values.push(value);
        }
        Ok(Some(values))
    }

    pub fn bytes(&mut self) -> Result<Option<Vec<u8>>, Error> {
        self.count()?
            .map(|length| self.read_bytes(length))
            .transpose()
    }

    pub fn i32s(&mut self) -> Result<Option<Vec<i32>>, Error> {
        self.unmanaged(i32::from_le_bytes)
    }

    pub fn u32s(&mut self) -> Result<Option<Vec<u32>>, Error> {
        self.unmanaged(u32::from_le_bytes)
    }

    /// A version-tolerant object: member count, each member's byte length, the members.
    /// `None` for null.
    pub fn object(&mut self) -> Result<Option<Object<'_>>, Error> {
        let members = self.fixed::<1>()?[0];
        if members == 255 {
            return Ok(None);
        }
        let mut lengths = Vec::with_capacity(usize::from(members));
        for _ in 0..members {
            let len =
                usize::try_from(self.varint()?).map_err(|_| Error::Invalid("member length"))?;
            lengths.push(len);
        }
        let length = lengths.iter().try_fold(0_u64, |total, length| {
            total
                .checked_add(*length as u64)
                .ok_or(Error::Invalid("object length overflow"))
        })?;
        if self.remaining.is_some_and(|remaining| length > remaining) {
            return Err(Error::Truncated);
        }
        Ok(Some(Object {
            input: RefCell::new(Reader {
                input: self,
                remaining: Some(length),
            }),
            lengths,
            next: Cell::new(0),
        }))
    }
}

pub struct Object<'a> {
    input: RefCell<Reader<'a>>,
    lengths: Vec<usize>,
    next: Cell<usize>,
}

impl Object<'_> {
    /// Member `i` read by `read`, which must consume it exactly; `None` when the writer
    /// predates the member.
    pub fn field<T>(
        &self,
        i: usize,
        read: impl FnOnce(&mut Reader<'_>) -> Result<T, Error>,
    ) -> Result<Option<T>, Error> {
        let Some(&length) = self.lengths.get(i) else {
            return Ok(None);
        };
        if i < self.next.get() {
            return Err(Error::Invalid("fields must be read in order"));
        }
        let mut input = self.input.borrow_mut();
        for &skipped in &self.lengths[self.next.get()..i] {
            let copied = io::copy(&mut input.by_ref().take(skipped as u64), &mut io::sink())?;
            if copied != skipped as u64 {
                return Err(Error::Truncated);
            }
        }
        let mut field = Reader {
            input: &mut *input,
            remaining: Some(length as u64),
        };
        let value = read(&mut field)?;
        field.finish()?;
        self.next.set(i + 1);
        Ok(Some(value))
    }

    pub fn finish(&self) -> Result<(), Error> {
        let mut input = self.input.borrow_mut();
        io::copy(&mut *input, &mut io::sink())?;
        input.finish()
    }
}
