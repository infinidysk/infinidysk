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
    checks: Vec<(usize, Slice)>,
}

impl<'a> Identity<'a> {
    pub fn proven(&self) -> Option<usize> {
        self.proven
    }

    pub fn new(sources: &'a [File], size: usize, budget: &mut Budget) -> Result<Self, Error> {
        let count = sources
            .iter()
            .map(|source| source.slices.len())
            .max()
            .unwrap_or(0);
        budget.metadata(
            count
                .checked_mul(size_of::<(usize, Slice)>())
                .ok_or(Error::Limit("slice observations"))?,
        )?;
        let mut checks = Vec::new();
        checks
            .try_reserve_exact(count)
            .map_err(|_| Error::Allocation)?;
        Ok(Self {
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
            checks,
        })
    }

    pub fn feed(&mut self, body: &Body, budget: &mut Budget) -> Result<(), Error> {
        if self.proven.is_some() {
            return self.slices(body, budget);
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
            }
        }
        self.slices(body, budget)
    }

    fn slices(&mut self, body: &Body, budget: &mut Budget) -> Result<(), Error> {
        if self.checks.capacity() == 0 {
            return Ok(());
        }
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
                if self.checks.len() == self.checks.capacity() {
                    return Err(Error::Limit("slice observations"));
                }
                self.checks.push((index, checksum));
                for (i, source) in self
                    .sources
                    .iter()
                    .enumerate()
                    .filter(|_| self.proven.is_none())
                {
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

    pub fn finish(self) -> Result<Option<(usize, Vec<bool>)>, Error> {
        let mut matches = self
            .matched
            .iter()
            .enumerate()
            .filter(|(_, matched)| **matched)
            .map(|(i, _)| i);
        let first = self.proven.or_else(|| matches.next());
        if self.proven.is_none() && matches.next().is_some() {
            return Err(Error::Ambiguous(
                "slice evidence matches multiple protected sources",
            ));
        }
        Ok(first.map(|source| {
            let mut checked = vec![false; self.sources[source].slices.len()];
            for (index, checksum) in self.checks {
                if self.sources[source].slices.get(index) == Some(&checksum) {
                    checked[index] = true;
                }
            }
            (source, checked)
        }))
    }
}
