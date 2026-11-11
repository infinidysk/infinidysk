use infinidysk_nntp::Body;
use infinidysk_par2::{File, Slice, hash_16k};

use super::{Budget, Error, PREFIX};

pub(super) struct Identity<'a> {
    sources: &'a [File],
    size: usize,
    length: Option<u64>,
    prefix: Vec<u8>,
    prefix_done: bool,
    proven: Option<usize>,
    matched: Vec<bool>,
    slice: Vec<u8>,
    next: u64,
    start: u64,
}

impl<'a> Identity<'a> {
    pub fn new(sources: &'a [File], size: usize) -> Self {
        Self {
            sources,
            size,
            length: None,
            prefix: Vec::new(),
            prefix_done: false,
            proven: None,
            matched: vec![false; sources.len()],
            slice: Vec::new(),
            next: 0,
            start: 0,
        }
    }

    pub fn feed(&mut self, body: &Body, budget: &mut Budget) -> Result<(), Error> {
        if self.proven.is_some() {
            return Ok(());
        }
        let length = *self.length.get_or_insert(body.file_size);
        let prefix_len =
            usize::try_from(length.min(PREFIX as u64)).expect("prefix bounded by 16384");
        if !self.prefix_done && body.offset == self.prefix.len() as u64 {
            let n = (prefix_len - self.prefix.len()).min(body.data.len());
            self.prefix
                .try_reserve_exact(n)
                .map_err(|_| Error::Allocation)?;
            self.prefix.extend_from_slice(&body.data[..n]);
            if self.prefix.len() == prefix_len {
                self.prefix_done = true;
                let hash = hash_16k(&self.prefix);
                for (i, source) in self.sources.iter().enumerate() {
                    budget.comparison()?;
                    if source.length == length && source.hash_16k == hash {
                        if self.proven.is_some() {
                            return Err(Error::Ambiguous(
                                "multiple sources share the observed prefix",
                            ));
                        }
                        self.proven = Some(i);
                    }
                }
                if self.proven.is_some() {
                    return Ok(());
                }
            } else {
                return Ok(());
            }
        }
        self.slices(body, budget)
    }

    fn slices(&mut self, body: &Body, budget: &mut Budget) -> Result<(), Error> {
        let length = body.file_size;
        let size = self.size as u64;
        let mut offset = body.offset;
        let mut bytes = body.data.as_ref();
        if self.next != offset {
            self.slice.clear();
        }
        while !bytes.is_empty() {
            budget.check()?;
            if self.slice.is_empty() {
                let skip = usize::try_from((size - offset % size) % size)
                    .map_err(|_| Error::Limit("slice size"))?
                    .min(bytes.len());
                bytes = &bytes[skip..];
                offset += skip as u64;
                if bytes.is_empty() {
                    break;
                }
                self.start = offset;
                if self.slice.capacity() < self.size {
                    budget.scratch(self.size)?;
                    self.slice
                        .try_reserve_exact(self.size)
                        .map_err(|_| Error::Allocation)?;
                }
            }
            let want = usize::try_from((length - self.start).min(size))
                .expect("slice bounded by usize size");
            let n = (want - self.slice.len()).min(bytes.len());
            self.slice.extend_from_slice(&bytes[..n]);
            offset += n as u64;
            bytes = &bytes[n..];
            self.next = offset;
            if self.slice.len() == want {
                let checksum = Slice::checksum(size, &self.slice).expect("buffer fits slice size");
                let index =
                    usize::try_from(self.start / size).map_err(|_| Error::Limit("slice index"))?;
                for (i, source) in self.sources.iter().enumerate() {
                    budget.comparison()?;
                    if source.length == length && source.slices.get(index) == Some(&checksum) {
                        self.matched[i] = true;
                    }
                }
                self.slice.clear();
            }
        }
        Ok(())
    }

    pub fn finish(self) -> Result<Option<usize>, Error> {
        if self.proven.is_some() {
            return Ok(self.proven);
        }
        let mut matches = self
            .matched
            .iter()
            .enumerate()
            .filter(|(_, matched)| **matched)
            .map(|(i, _)| i);
        let first = matches.next();
        if matches.next().is_some() {
            return Err(Error::Ambiguous(
                "slice evidence matches multiple protected sources",
            ));
        }
        Ok(first)
    }
}
