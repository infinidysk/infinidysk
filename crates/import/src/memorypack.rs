//! Reader for the `MemoryPack` wire format, limited to what C# metadata blobs use:
//! version-tolerant objects, strings, arrays and unmanaged values.

use uuid::Uuid;

#[derive(Debug, thiserror::Error)]
pub enum Error {
    #[error("truncated")]
    Truncated,
    #[error("{0} trailing bytes")]
    Trailing(usize),
    #[error("{0}")]
    Invalid(&'static str),
}

pub struct Reader<'a> {
    buf: &'a [u8],
}

impl<'a> Reader<'a> {
    pub const fn new(buf: &'a [u8]) -> Self {
        Self { buf }
    }

    fn take(&mut self, n: usize) -> Result<&'a [u8], Error> {
        let (head, rest) = self.buf.split_at_checked(n).ok_or(Error::Truncated)?;
        self.buf = rest;
        Ok(head)
    }

    fn fixed<const N: usize>(&mut self) -> Result<[u8; N], Error> {
        let mut out = [0; N];
        out.copy_from_slice(self.take(N)?);
        Ok(out)
    }

    pub const fn finish(&self) -> Result<(), Error> {
        match self.buf.len() {
            0 => Ok(()),
            n => Err(Error::Trailing(n)),
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
            let raw = self.take(units.checked_mul(2).ok_or(Error::Truncated)?)?;
            let utf16: Vec<u16> = raw
                .as_chunks::<2>()
                .0
                .iter()
                .map(|c| u16::from_le_bytes(*c))
                .collect();
            return String::from_utf16(&utf16)
                .map(Some)
                .map_err(|_| Error::Invalid("bad UTF-16 string"));
        }
        // UTF-8: the complement of the byte count, then the UTF-16 length.
        let bytes = usize::try_from(!header).map_err(|_| Error::Invalid("length"))?;
        self.i32()?;
        let raw = self.take(bytes)?;
        std::str::from_utf8(raw)
            .map(|s| Some(s.to_owned()))
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
        // Bounded by the input: every element takes at least one byte.
        let mut out = Vec::with_capacity(n.min(self.buf.len()));
        for _ in 0..n {
            out.push(item(self)?);
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
        let raw = self.take(n.checked_mul(N).ok_or(Error::Truncated)?)?;
        Ok(Some(
            raw.as_chunks::<N>().0.iter().map(|c| from(*c)).collect(),
        ))
    }

    pub fn bytes(&mut self) -> Result<Option<Vec<u8>>, Error> {
        self.unmanaged(|[b]: [u8; 1]| b)
    }

    pub fn i32s(&mut self) -> Result<Option<Vec<i32>>, Error> {
        self.unmanaged(i32::from_le_bytes)
    }

    pub fn u32s(&mut self) -> Result<Option<Vec<u32>>, Error> {
        self.unmanaged(u32::from_le_bytes)
    }

    /// A version-tolerant object: member count, each member's byte length, the members.
    /// `None` for null.
    pub fn object(&mut self) -> Result<Option<Object<'a>>, Error> {
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
        let fields = lengths
            .into_iter()
            .map(|len| self.take(len))
            .collect::<Result<_, _>>()?;
        Ok(Some(Object { fields }))
    }
}

pub struct Object<'a> {
    fields: Vec<&'a [u8]>,
}

impl<'a> Object<'a> {
    /// Member `i` read by `read`, which must consume it exactly; `None` when the writer
    /// predates the member.
    pub fn field<T>(
        &self,
        i: usize,
        read: impl FnOnce(&mut Reader<'a>) -> Result<T, Error>,
    ) -> Result<Option<T>, Error> {
        let Some(bytes) = self.fields.get(i) else {
            return Ok(None);
        };
        let mut r = Reader::new(bytes);
        let value = read(&mut r)?;
        r.finish()?;
        Ok(Some(value))
    }
}
