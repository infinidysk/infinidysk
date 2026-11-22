//! Incremental repair over verified slices. All I/O belongs to the caller.

use md5::{Digest, Md5};

use crate::field::FIELD;
use crate::{HEADER, Id, MAGIC, RECOVERY, Slice};

// ponytail: cap implicit zero hashing at 256 MiB until callers admit a padding-work budget.
pub(super) const MAX_PADDING_BYTES: u64 = 256 * 1024 * 1024;

#[derive(Debug, thiserror::Error, PartialEq, Eq)]
pub enum Error {
    #[error("invalid PAR2 input: {0}")]
    Invalid(&'static str),
    #[error("repair needs {required} workspace bytes, limit is {limit}")]
    Memory { required: usize, limit: usize },
    #[error("repair allocation failed")]
    Allocation,
    #[error("repair cancelled")]
    Cancelled,
    #[error("slice {index}: {reason}")]
    Slice { index: usize, reason: &'static str },
    #[error("not enough independent recovery slices")]
    InsufficientRecovery,
    #[error("{remaining} present slices have not been verified")]
    Incomplete { remaining: usize },
    #[error("file length mismatch: expected {expected} bytes, received {actual} bytes")]
    Length { expected: u64, actual: u64 },
    #[error("file checksum mismatch")]
    FileChecksum,
}

#[derive(Clone, Copy, Debug)]
pub struct Input {
    /// Original bytes in this slice, excluding final zero padding.
    pub length: usize,
    pub checksum: Slice,
}

/// Borrowed recovery data whose packet identity, length, and MD5 have been checked.
#[derive(Clone, Copy, Debug)]
pub struct Recovery<'a> {
    set: Id,
    pub(super) exponent: u32,
    pub(super) data: &'a [u8],
}

impl<'a> Recovery<'a> {
    /// Verify one complete recovery packet without copying its data.
    pub fn read(packet: &'a [u8], set: Id, slice_size: usize) -> Result<Self, Error> {
        let row = Self::scanned(packet, set, slice_size)?;
        if <[u8; 16]>::from(Md5::digest(&packet[32..])) != packet[16..32] {
            return Err(Error::Invalid("recovery packet length or checksum"));
        }
        Ok(row)
    }

    /// [`Self::read`] for a packet whose MD5 the scanner already checked.
    pub(crate) fn scanned(packet: &'a [u8], set: Id, slice_size: usize) -> Result<Self, Error> {
        if slice_size == 0 || !slice_size.is_multiple_of(4) {
            return Err(Error::Invalid(
                "slice size must be a positive multiple of four",
            ));
        }
        if slice_size.checked_add(HEADER + 4) != Some(packet.len())
            || &packet[..8] != MAGIC
            || packet[32..48] != set
            || &packet[48..64] != RECOVERY
        {
            return Err(Error::Invalid("recovery packet identity or length"));
        }
        let length = u64::from_le_bytes(packet[8..16].try_into().expect("checked packet header"));
        if usize::try_from(length) != Ok(packet.len()) {
            return Err(Error::Invalid("recovery packet length or checksum"));
        }
        Ok(Self {
            set,
            exponent: u32::from_le_bytes(
                packet[64..68].try_into().expect("checked exponent length"),
            ),
            data: &packet[68..],
        })
    }
}

#[derive(Debug)]
pub struct Recovered {
    pub index: usize,
    /// Original data only; final slice padding is removed.
    pub data: Vec<u8>,
}

/// A single recovery set, fed in any order without retaining source data.
///
/// `missing` must be sorted and unique. The caller supplies inputs in main-packet
/// order and admits their metadata and borrowed packet buffers separately.
/// `workspace_bytes` bounds requested solver heap storage, including output and
/// matrix workspace, but excludes shared field tables and allocator overhead.
pub struct Repair<'a> {
    size: usize,
    inputs: &'a [Input],
    missing: &'a [usize],
    exponents: Vec<u32>,
    rows: Vec<Vec<u8>>,
    inverse: Vec<u16>,
    seen: Vec<bool>,
    remaining: usize,
    cancelled: bool,
    workspace_bytes: usize,
}

impl<'a> Repair<'a> {
    pub fn new(
        slice_size: usize,
        inputs: &'a [Input],
        missing: &'a [usize],
        recovery: &[Recovery<'_>],
        workspace_bytes: usize,
        cancelled: impl Fn() -> bool,
    ) -> Result<Self, Error> {
        check_cancel(&cancelled)?;
        if slice_size == 0 || !slice_size.is_multiple_of(4) || inputs.len() > 32_768 {
            return Err(Error::Invalid("slice size or input count"));
        }
        if inputs
            .iter()
            .any(|i| i.length == 0 || i.length > slice_size)
            || missing.windows(2).any(|w| w[0] >= w[1])
            || missing.last().is_some_and(|&i| i >= inputs.len())
        {
            return Err(Error::Invalid("input lengths or missing indices"));
        }
        let mut padding_left = MAX_PADDING_BYTES;
        for input in inputs {
            padding_left = padding_left
                .checked_sub((slice_size - input.length) as u64)
                .ok_or(Error::Invalid("checksum padding exceeds limit"))?;
        }
        if recovery
            .iter()
            .any(|r| r.data.len() != slice_size || r.set != recovery[0].set)
        {
            return Err(Error::Invalid("mixed recovery sets or slice sizes"));
        }
        let count = missing.len();
        if recovery.len() < count {
            return Err(Error::InsufficientRecovery);
        }
        // Conservatively includes simultaneous buffers across selection, inversion, and output.
        let required = count
            .checked_mul(slice_size)
            .and_then(|n| n.checked_mul(2))
            .and_then(|n| {
                count
                    .checked_mul(count)
                    .and_then(|m| m.checked_mul(8))
                    .and_then(|m| n.checked_add(m))
            })
            .and_then(|n| {
                count
                    .checked_mul(
                        2 * size_of::<usize>()
                            + size_of::<u32>()
                            + size_of::<Vec<u8>>()
                            + size_of::<Recovered>()
                            + size_of::<u16>(),
                    )
                    .and_then(|m| n.checked_add(m))
            })
            .and_then(|n| n.checked_add(inputs.len()))
            .ok_or(Error::Invalid("workspace size overflow"))?;
        if required > workspace_bytes {
            return Err(Error::Memory {
                required,
                limit: workspace_bytes,
            });
        }
        let selected = select(missing, recovery, &cancelled)?;
        let inverse = invert(missing, recovery, &selected, &cancelled)?;
        let mut rows = reserved(count)?;
        let mut exponents = reserved(count)?;
        for index in selected {
            check_cancel(&cancelled)?;
            let mut row = reserved(slice_size)?;
            for chunk in recovery[index].data.chunks(64 * 1024) {
                check_cancel(&cancelled)?;
                row.extend_from_slice(chunk);
            }
            rows.push(row);
            exponents.push(recovery[index].exponent);
        }
        let seen = filled(inputs.len(), false)?;
        check_cancel(&cancelled)?;
        Ok(Self {
            size: slice_size,
            inputs,
            missing,
            exponents,
            rows,
            inverse,
            seen,
            remaining: inputs.len() - count,
            cancelled: false,
            workspace_bytes: required,
        })
    }

    /// Conservative admitted storage, including simultaneous recovery output.
    pub const fn workspace_bytes(&self) -> usize {
        self.workspace_bytes
    }

    /// Verify one present slice before folding it. Cancellation makes this repair unusable.
    pub fn feed(
        &mut self,
        index: usize,
        data: &[u8],
        cancelled: impl Fn() -> bool,
    ) -> Result<(), Error> {
        if self.cancelled || cancelled() {
            self.cancelled = true;
            return Err(Error::Cancelled);
        }
        let invalid = |reason| Error::Slice { index, reason };
        let input = self
            .inputs
            .get(index)
            .ok_or_else(|| invalid("index outside recovery set"))?;
        if self.missing.binary_search(&index).is_ok() || self.seen[index] {
            return Err(invalid("missing or already verified"));
        }
        if data.len() != input.length {
            return Err(invalid("length or checksum mismatch"));
        }
        // A cancelled operation or panicking callback must never leave resumable partial rows.
        self.cancelled = true;
        if !input
            .checksum
            .matches_cancellable(self.size as u64, data, &cancelled)?
        {
            self.cancelled = false;
            return Err(invalid("length or checksum mismatch"));
        }
        for (row, &exponent) in self.rows.iter_mut().zip(&self.exponents) {
            let coefficient = FIELD.pow(FIELD.constants[index], exponent);
            add_scaled(row, data, coefficient, &cancelled)?;
        }
        self.seen[index] = true;
        self.remaining -= 1;
        self.cancelled = false;
        Ok(())
    }

    /// Return only fully reconstructed and checksum-verified slices, in missing-index order.
    pub fn finish(self, cancelled: impl Fn() -> bool) -> Result<Vec<Recovered>, Error> {
        if self.cancelled {
            return Err(Error::Cancelled);
        }
        check_cancel(&cancelled)?;
        if self.remaining != 0 {
            return Err(Error::Incomplete {
                remaining: self.remaining,
            });
        }
        let count = self.missing.len();
        let mut output = reserved(count)?;
        for (position, &index) in self.missing.iter().enumerate() {
            check_cancel(&cancelled)?;
            let mut data = filled(self.size, 0)?;
            for (row, &coefficient) in self
                .rows
                .iter()
                .zip(&self.inverse[position * count..(position + 1) * count])
            {
                add_scaled(&mut data, row, coefficient, &cancelled)?;
            }
            let length = self.inputs[index].length;
            // PAR2 zero-pads a short final slice; anything else there is not this input.
            if data[length..].iter().any(|&b| b != 0)
                || !self.inputs[index].checksum.matches_cancellable(
                    self.size as u64,
                    &data[..length],
                    &cancelled,
                )?
            {
                return Err(Error::Slice {
                    index,
                    reason: "reconstructed checksum mismatch",
                });
            }
            data.truncate(length);
            output.push(Recovered { index, data });
        }
        Ok(output)
    }
}

pub(super) fn reserved<T>(count: usize) -> Result<Vec<T>, Error> {
    let mut output = Vec::new();
    output
        .try_reserve_exact(count)
        .map_err(|_| Error::Allocation)?;
    Ok(output)
}

pub(super) fn filled<T: Clone>(count: usize, value: T) -> Result<Vec<T>, Error> {
    let mut output = reserved(count)?;
    output.resize(count, value);
    Ok(output)
}

fn check_cancel(cancelled: &impl Fn() -> bool) -> Result<(), Error> {
    if cancelled() {
        Err(Error::Cancelled)
    } else {
        Ok(())
    }
}

fn add_scaled(
    destination: &mut [u8],
    source: &[u8],
    coefficient: u16,
    cancelled: &impl Fn() -> bool,
) -> Result<(), Error> {
    check_cancel(cancelled)?;
    if coefficient == 0 {
        return Ok(());
    }
    for (out, input) in destination.chunks_mut(4096).zip(source.chunks(4096)) {
        check_cancel(cancelled)?;
        if coefficient == 1 {
            for (out, &byte) in out.iter_mut().zip(input) {
                *out ^= byte;
            }
        } else {
            for (out, input) in out.as_chunks_mut::<2>().0.iter_mut().zip(input.chunks(2)) {
                let word = u16::from_le_bytes([input[0], input.get(1).copied().unwrap_or(0)]);
                let product = FIELD.mul(word, coefficient).to_le_bytes();
                out[0] ^= product[0];
                out[1] ^= product[1];
            }
        }
    }
    Ok(())
}

/// Choose linearly independent rows, including spares after a dependent row.
fn select(
    missing: &[usize],
    recovery: &[Recovery<'_>],
    cancelled: &impl Fn() -> bool,
) -> Result<Vec<usize>, Error> {
    let mut selected = reserved(missing.len())?;
    if missing.is_empty() {
        return Ok(selected);
    }
    let mut rank = Rank::new(missing)?;
    for (index, recovery) in recovery.iter().enumerate() {
        check_cancel(cancelled)?;
        if rank.add_exponent(recovery.exponent, cancelled)? {
            selected.push(index);
            if rank.full() {
                return Ok(selected);
            }
        }
    }
    Err(Error::InsufficientRecovery)
}

/// The independent recovery rows seen so far, over the `missing` inputs.
pub struct Rank<'a> {
    missing: &'a [usize],
    basis: Vec<u16>,
    scratch: Vec<u16>,
    pivots: Vec<usize>,
}

impl<'a> Rank<'a> {
    /// `missing` must be sorted, unique input indices.
    pub fn new(missing: &'a [usize]) -> Result<Self, Error> {
        let count = missing.len();
        if missing.windows(2).any(|w| w[0] >= w[1])
            || missing.last().is_some_and(|&i| i >= FIELD.constants.len())
        {
            return Err(Error::Invalid("missing indices"));
        }
        Ok(Self {
            missing,
            basis: filled(count.checked_mul(count).ok_or(Error::Allocation)?, 0)?,
            scratch: filled(count, 0)?,
            pivots: reserved(count)?,
        })
    }

    /// Heap bytes [`Self::new`] allocates for `count` missing inputs.
    pub fn bytes(count: usize) -> Option<usize> {
        count
            .checked_mul(count)?
            .checked_add(count)?
            .checked_mul(size_of::<u16>())?
            .checked_add(count.checked_mul(size_of::<usize>())?)
    }

    /// Whether the rows seen so far can rebuild every missing input.
    pub fn full(&self) -> bool {
        self.pivots.len() == self.missing.len()
    }

    /// `true` when `row` is independent of the rows seen so far.
    pub fn add(&mut self, row: &Recovery<'_>) -> bool {
        self.add_exponent(row.exponent, &|| false)
            .expect("an uncancellable rank update cannot fail")
    }

    pub(crate) fn add_exponent(
        &mut self,
        exponent: u32,
        cancelled: &impl Fn() -> bool,
    ) -> Result<bool, Error> {
        let count = self.missing.len();
        if self.full() {
            return Ok(false);
        }
        for (entry, &input) in self.scratch.iter_mut().zip(self.missing) {
            *entry = FIELD.pow(FIELD.constants[input], exponent);
        }
        for (row, &pivot) in self.pivots.iter().enumerate() {
            check_cancel(cancelled)?;
            let coefficient = self.scratch[pivot];
            for column in pivot..count {
                self.scratch[column] ^= FIELD.mul(coefficient, self.basis[row * count + column]);
            }
        }
        let Some(pivot) = self.scratch.iter().position(|&value| value != 0) else {
            return Ok(false);
        };
        let inverse = FIELD.inverse(self.scratch[pivot]);
        for entry in &mut self.scratch {
            *entry = FIELD.mul(*entry, inverse);
        }
        let start = self.pivots.len() * count;
        self.basis[start..start + count].copy_from_slice(&self.scratch);
        self.pivots.push(pivot);
        Ok(true)
    }
}

fn invert(
    missing: &[usize],
    recovery: &[Recovery<'_>],
    selected: &[usize],
    cancelled: &impl Fn() -> bool,
) -> Result<Vec<u16>, Error> {
    let count = missing.len();
    let width = count * 2;
    let mut matrix = filled(count * width, 0)?;
    for (row, &index) in selected.iter().enumerate() {
        check_cancel(cancelled)?;
        for (column, &input) in missing.iter().enumerate() {
            matrix[row * width + column] =
                FIELD.pow(FIELD.constants[input], recovery[index].exponent);
        }
        matrix[row * width + count + row] = 1;
    }
    for column in 0..count {
        check_cancel(cancelled)?;
        let pivot = (column..count)
            .find(|&row| matrix[row * width + column] != 0)
            .ok_or(Error::InsufficientRecovery)?;
        for entry in 0..width {
            matrix.swap(column * width + entry, pivot * width + entry);
        }
        let factor = FIELD.inverse(matrix[column * width + column]);
        for entry in 0..width {
            matrix[column * width + entry] = FIELD.mul(matrix[column * width + entry], factor);
        }
        for row in 0..count {
            check_cancel(cancelled)?;
            if row == column {
                continue;
            }
            let factor = matrix[row * width + column];
            for entry in 0..width {
                matrix[row * width + entry] ^= FIELD.mul(factor, matrix[column * width + entry]);
            }
        }
    }
    let mut inverse = filled(count * count, 0)?;
    for row in 0..count {
        inverse[row * count..(row + 1) * count]
            .copy_from_slice(&matrix[row * width + count..(row + 1) * width]);
    }
    Ok(inverse)
}

#[cfg(test)]
mod tests {
    use super::Rank;

    #[test]
    fn rank_ignores_distinct_but_dependent_exponents() {
        // Constants 0 and 5 are 2^1 and 2^11; multiples of 13107 raise both to one value.
        let missing = [0, 5];
        let mut rank = Rank::new(&missing).expect("missing indices are sorted and in range");
        let never = || false;
        let added: Vec<bool> = [0, 13_107, 26_214, 39_321]
            .into_iter()
            .map(|exponent| {
                rank.add_exponent(exponent, &never)
                    .expect("never cancelled")
            })
            .collect();
        assert_eq!(added, [true, false, false, false]);
        assert!(!rank.full());
        assert!(rank.add_exponent(1, &never).expect("never cancelled"));
        assert!(rank.full());
    }
}
