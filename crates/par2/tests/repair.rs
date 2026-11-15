use infinidysk_par2::{Error, Input, Recovery, Repair, read};
use md5::{Digest, Md5};

const INDEX: &[u8] = include_bytes!("fixtures/set.par2");
const VOLUME: &[u8] = include_bytes!("fixtures/set.vol0+3.par2");
const LIMIT: usize = 2_000_000;

fn packets() -> Vec<&'static [u8]> {
    packets_from(VOLUME)
}

fn packets_from(mut bytes: &'static [u8]) -> Vec<&'static [u8]> {
    let mut packets = Vec::new();
    while !bytes.is_empty() {
        let len = usize::try_from(u64::from_le_bytes(
            bytes[8..16]
                .try_into()
                .expect("fixture has a complete packet header"),
        ))
        .expect("fixture packet length fits usize");
        packets.push(&bytes[..len]);
        bytes = &bytes[len..];
    }
    packets
}

fn fixture() -> (usize, Vec<Input>, Vec<Vec<u8>>, Vec<Recovery<'static>>) {
    fixture_from(INDEX, VOLUME)
}

fn fixture_from(
    index_bytes: &[u8],
    volume: &'static [u8],
) -> (usize, Vec<Input>, Vec<Vec<u8>>, Vec<Recovery<'static>>) {
    let index = read(index_bytes);
    let size = usize::try_from(index.slice_size).expect("fixture slice size fits usize");
    let mut inputs = Vec::new();
    let mut slices = Vec::new();
    for file in &index.files {
        let bytes: Vec<u8> = (0..usize::try_from(file.length).expect("fixture length fits usize"))
            .map(|i| ((i * 31 + i / 251) % 256).to_le_bytes()[0])
            .collect();
        for (data, checksum) in bytes.chunks(size).zip(&file.slices) {
            inputs.push(Input {
                length: data.len(),
                checksum: *checksum,
            });
            slices.push(data.to_vec());
        }
    }
    let rows = packets_from(volume)
        .into_iter()
        .filter(|p| &p[48..64] == b"PAR 2.0\0RecvSlic")
        .map(|p| {
            Recovery::read(p, index.set_id.expect("fixture has a recovery set"), size)
                .expect("fixture recovery packet is valid")
        })
        .collect();
    (size, inputs, slices, rows)
}

fn recover(missing: &[usize]) {
    let (size, inputs, slices, rows) = fixture();
    let mut repair = Repair::new(size, &inputs, missing, &rows, LIMIT, || false)
        .expect("fixture has sufficient recovery data and workspace");
    for (index, bytes) in slices.iter().enumerate().rev() {
        if !missing.contains(&index) {
            repair
                .feed(index, bytes, || false)
                .expect("fixture source matches checksums");
        }
    }
    let output = repair
        .finish(|| false)
        .expect("fixture reconstructs with valid checksums");
    assert_eq!(output.len(), missing.len());
    for (actual, &index) in output.iter().zip(missing) {
        assert_eq!(actual.index, index);
        assert_eq!(actual.data, slices[index]);
    }
}

#[test]
fn reconstructs_independent_par2cmdline_fixture() {
    let (_, inputs, _, _) = fixture();
    for first in 0..inputs.len() {
        recover(&[first]);
        for second in first + 1..inputs.len() {
            recover(&[first, second]);
        }
    }
    recover(&[0, 13, 26]);
}

#[test]
fn rejects_invalid_dimensions_and_insufficient_workspace() {
    let (size, inputs, _, rows) = fixture();
    assert!(matches!(
        Repair::new(size, &inputs, &[0], &rows, 0, || false),
        Err(Error::Memory { .. })
    ));
    assert!(Repair::new(0, &inputs, &[0], &rows, LIMIT, || false).is_err());
    assert!(Repair::new(3, &inputs, &[0], &rows, LIMIT, || false).is_err());
    assert!(Repair::new(size, &inputs, &[1, 0], &rows, LIMIT, || false).is_err());
    assert!(Repair::new(size, &inputs, &[0, 0], &rows, LIMIT, || false).is_err());
    assert!(Repair::new(size, &inputs, &[inputs.len()], &rows, LIMIT, || false).is_err());
    let too_many = vec![inputs[0]; 32_769];
    assert!(Repair::new(size, &too_many, &[0], &rows, LIMIT, || false).is_err());
    assert!(matches!(
        Repair::new(size, &inputs, &[0], &[], LIMIT, || false),
        Err(Error::InsufficientRecovery)
    ));
}

#[test]
fn rejects_damage_before_changing_reconstruction_state() {
    let (size, inputs, slices, rows) = fixture();
    let mut repair = Repair::new(size, &inputs, &[0], &rows, LIMIT, || false).unwrap();
    assert!(repair.feed(inputs.len(), &slices[1], || false).is_err());
    assert!(repair.feed(0, &slices[0], || false).is_err());
    assert!(repair.feed(1, &slices[1][..size - 1], || false).is_err());
    let mut corrupt = slices[1].clone();
    corrupt[0] ^= 1;
    assert!(repair.feed(1, &corrupt, || false).is_err());
    for (index, bytes) in slices.iter().enumerate().skip(1) {
        repair.feed(index, bytes, || false).unwrap();
    }
    assert!(repair.feed(1, &slices[1], || false).is_err());
    assert_eq!(repair.finish(|| false).unwrap()[0].data, slices[0]);
}

#[test]
fn cancellation_never_returns_partial_reconstruction() {
    let (size, inputs, slices, rows) = fixture();
    assert!(matches!(
        Repair::new(size, &inputs, &[0], &rows, LIMIT, || true),
        Err(Error::Cancelled)
    ));
    let mut repair = Repair::new(size, &inputs, &[0], &rows, LIMIT, || false).unwrap();
    assert!(matches!(
        repair.feed(1, &slices[1], || true),
        Err(Error::Cancelled)
    ));
    assert!(matches!(repair.finish(|| false), Err(Error::Cancelled)));
    let repair = Repair::new(size, &inputs, &[0], &rows, LIMIT, || false).unwrap();
    assert!(matches!(repair.finish(|| true), Err(Error::Cancelled)));
}

#[test]
fn incomplete_reconstruction_cannot_finish() {
    let (size, inputs, _, rows) = fixture();
    let repair = Repair::new(size, &inputs, &[0], &rows, LIMIT, || false).unwrap();
    assert!(matches!(
        repair.finish(|| false),
        Err(Error::Incomplete { .. })
    ));
}

#[test]
fn recovery_packets_require_matching_identity_size_and_checksum() {
    let index = read(INDEX);
    let size = usize::try_from(index.slice_size).unwrap();
    let packet = packets()
        .into_iter()
        .find(|p| &p[48..64] == b"PAR 2.0\0RecvSlic")
        .unwrap();
    for length in 0..packet.len() {
        assert!(Recovery::read(&packet[..length], index.set_id.unwrap(), size).is_err());
    }
    assert!(Recovery::read(packet, [0; 16], size).is_err());
    assert!(Recovery::read(packet, index.set_id.unwrap(), size + 4).is_err());
    let mut damaged = packet.to_vec();
    damaged[68] ^= 1;
    assert!(Recovery::read(&damaged, index.set_id.unwrap(), size).is_err());
}

#[test]
fn reconstructs_odd_final_slices_with_nonzero_exponents() {
    // Same data generator as the original fixture; par2 create -s128 -c3 -n1 -f5.
    let (size, inputs, slices, rows) = fixture_from(
        include_bytes!("fixtures/odd.par2"),
        include_bytes!("fixtures/odd.vol5+3.par2"),
    );
    assert!(inputs.iter().any(|input| !input.length.is_multiple_of(2)));
    for first in 0..inputs.len() {
        for last in first..inputs.len() {
            let missing: Vec<_> = if first == last {
                vec![first]
            } else {
                vec![first, last]
            };
            let mut repair = Repair::new(size, &inputs, &missing, &rows, LIMIT, || false).unwrap();
            for (i, bytes) in slices.iter().enumerate() {
                if !missing.contains(&i) {
                    repair.feed(i, bytes, || false).unwrap();
                }
            }
            for output in repair.finish(|| false).unwrap() {
                assert_eq!(output.data, slices[output.index]);
            }
        }
    }
}

fn rehash(packet: &mut [u8]) {
    let hash = Md5::digest(&packet[32..]);
    packet[16..32].copy_from_slice(&hash);
}

#[test]
fn selects_independent_spares_after_a_dependent_row() {
    let (size, inputs, slices, rows) = fixture();
    let index = read(INDEX);
    let original = packets()
        .into_iter()
        .find(|p| &p[48..64] == b"PAR 2.0\0RecvSlic")
        .unwrap();
    assert_eq!(&original[64..68], &0u32.to_le_bytes());
    let mut equivalent = original.to_vec();
    equivalent[64..68].copy_from_slice(&65_535u32.to_le_bytes());
    rehash(&mut equivalent);
    let dependent = Recovery::read(&equivalent, index.set_id.unwrap(), size).unwrap();
    let rows = [rows[0], dependent, rows[1]];
    let mut repair = Repair::new(size, &inputs, &[0, 1], &rows, LIMIT, || false).unwrap();
    for (i, data) in slices.iter().enumerate().skip(2) {
        repair.feed(i, data, || false).unwrap();
    }
    for output in repair.finish(|| false).unwrap() {
        assert_eq!(output.data, slices[output.index]);
    }
    assert!(matches!(
        Repair::new(size, &inputs, &[0, 1], &rows[..2], LIMIT, || false),
        Err(Error::InsufficientRecovery)
    ));
}

#[test]
fn verifies_reconstructed_bytes_even_when_packet_md5_is_valid() {
    let (size, inputs, slices, _) = fixture();
    let index = read(INDEX);
    let mut packet = packets()
        .into_iter()
        .find(|p| &p[48..64] == b"PAR 2.0\0RecvSlic")
        .unwrap()
        .to_vec();
    packet[68] ^= 1;
    rehash(&mut packet);
    let rows = [Recovery::read(&packet, index.set_id.unwrap(), size).unwrap()];
    let mut repair = Repair::new(size, &inputs, &[0], &rows, LIMIT, || false).unwrap();
    for (i, data) in slices.iter().enumerate().skip(1) {
        repair.feed(i, data, || false).unwrap();
    }
    assert!(matches!(
        repair.finish(|| false),
        Err(Error::Slice { index: 0, .. })
    ));
}

#[test]
fn cancellation_after_a_partial_feed_prevents_reuse() {
    let (size, inputs, slices, rows) = fixture();
    let mut repair = Repair::new(size, &inputs, &[0, 1], &rows, LIMIT, || false).unwrap();
    let calls = std::cell::Cell::new(0);
    let result = repair.feed(2, &slices[2], || {
        let count = calls.get();
        calls.set(count + 1);
        count >= 3
    });
    assert!(matches!(result, Err(Error::Cancelled)));
    assert!(matches!(
        repair.feed(2, &slices[2], || false),
        Err(Error::Cancelled)
    ));
    assert!(matches!(repair.finish(|| false), Err(Error::Cancelled)));
}

#[test]
fn workspace_limit_is_checked_before_reconstruction() {
    let (size, inputs, _, rows) = fixture();
    let Err(Error::Memory { required, .. }) =
        Repair::new(size, &inputs, &[0, 1], &rows, 0, || false)
    else {
        panic!("expected workspace limit");
    };
    assert!(Repair::new(size, &inputs, &[0, 1], &rows, required, || false).is_ok());
    assert!(matches!(
        Repair::new(size, &inputs, &[0, 1], &rows, required - 1, || false),
        Err(Error::Memory { .. })
    ));
    assert!(
        Repair::new(4, &[], &[], &[], 0, || false)
            .unwrap()
            .finish(|| false)
            .unwrap()
            .is_empty()
    );
}

#[test]
fn a_panicking_callback_cannot_leave_partial_rows_reusable() {
    let (size, inputs, slices, rows) = fixture();
    let mut repair = Repair::new(size, &inputs, &[0, 1], &rows, LIMIT, || false).unwrap();
    let calls = std::cell::Cell::new(0);
    let result = std::panic::catch_unwind(std::panic::AssertUnwindSafe(|| {
        let _ = repair.feed(2, &slices[2], || {
            let count = calls.get();
            calls.set(count + 1);
            assert!(count < 3, "callback failure after the first row");
            false
        });
    }));
    assert!(result.is_err());
    assert!(matches!(repair.finish(|| false), Err(Error::Cancelled)));
}

#[test]
fn packets_from_different_recovery_sets_cannot_be_combined() {
    let (size, inputs, _, rows) = fixture();
    let mut packet = packets()
        .into_iter()
        .find(|p| &p[48..64] == b"PAR 2.0\0RecvSlic")
        .unwrap()
        .to_vec();
    packet[32..48].copy_from_slice(&[9; 16]);
    rehash(&mut packet);
    let foreign = Recovery::read(&packet, [9; 16], size).unwrap();
    assert!(matches!(
        Repair::new(size, &inputs, &[0], &[rows[0], foreign], LIMIT, || false),
        Err(Error::Invalid(_))
    ));
}
