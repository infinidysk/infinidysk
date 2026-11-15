use infinidysk_par2::{Error, Verifier, read};
use md5::{Digest, Md5};

const INDEX: &[u8] = include_bytes!("fixtures/set.par2");

#[test]
fn verifies_existing_content_with_only_its_stored_proof() {
    let index = read(INDEX);
    for file in &index.files {
        let data: Vec<u8> = (0..usize::try_from(file.length).unwrap())
            .map(|i| ((i * 31 + i / 251) % 256).to_le_bytes()[0])
            .collect();
        for chunk in [1, 3, 4096, 7000] {
            let mut verifier =
                Verifier::new(file.length, file.hash, index.slice_size, &file.slices).unwrap();
            for bytes in data.chunks(chunk) {
                verifier.push(bytes, || false).unwrap();
            }
            let result = verifier.finish(|| false).unwrap();
            assert_eq!(result.bytes, file.length);
            assert_eq!(result.slices, file.slices.len());
        }
    }
}

#[test]
fn whole_file_hash_does_not_require_slice_checksums() {
    let data = b"existing bytes without retained parity";
    let mut verifier = Verifier::new(data.len() as u64, Md5::digest(data).into(), 0, &[]).unwrap();
    verifier.push(data, || false).unwrap();
    let result = verifier.finish(|| false).unwrap();
    assert_eq!(result.bytes, data.len() as u64);
    assert_eq!(result.slices, 0);
}

#[test]
fn truncated_and_overlong_data_cannot_be_verified() {
    let hash = Md5::digest(b"abc").into();
    let mut truncated = Verifier::new(3, hash, 0, &[]).unwrap();
    truncated.push(b"ab", || false).unwrap();
    assert!(matches!(
        truncated.finish(|| false),
        Err(Error::Length {
            expected: 3,
            actual: 2
        })
    ));
    let mut overlong = Verifier::new(3, hash, 0, &[]).unwrap();
    assert!(matches!(
        overlong.push(b"abcd", || false),
        Err(Error::Length { .. })
    ));
    assert!(overlong.finish(|| false).is_err());
}

#[test]
fn detects_content_mismatch_and_contradictory_hashes() {
    let index = read(INDEX);
    let file = &index.files[0];
    let mut verifier =
        Verifier::new(file.length, file.hash, index.slice_size, &file.slices).unwrap();
    assert!(matches!(
        verifier.push(&[9; 4096], || false),
        Err(Error::Slice { index: 0, .. })
    ));
    assert!(verifier.finish(|| false).is_err());
    let mut verifier = Verifier::new(3, Md5::digest(b"abc").into(), 0, &[]).unwrap();
    verifier.push(b"def", || false).unwrap();
    assert!(matches!(
        verifier.finish(|| false),
        Err(Error::FileChecksum)
    ));
}

#[test]
fn cancellation_and_bad_proof_never_produce_success() {
    let index = read(INDEX);
    let file = &index.files[0];
    assert!(Verifier::new(file.length, file.hash, 0, &file.slices).is_err());
    assert!(Verifier::new(file.length, file.hash, 3, &file.slices).is_err());
    assert!(Verifier::new(file.length, file.hash, index.slice_size, &file.slices[..1]).is_err());
    let mut verifier = Verifier::new(3, Md5::digest(b"abc").into(), 0, &[]).unwrap();
    assert!(matches!(
        verifier.push(b"abc", || true),
        Err(Error::Cancelled)
    ));
    assert!(verifier.finish(|| false).is_err());
}

#[test]
fn short_odd_tails_use_padded_slice_hashes_and_unpadded_file_hashes() {
    let index = read(include_bytes!("fixtures/odd.par2"));
    for file in &index.files {
        assert!(!file.length.is_multiple_of(2));
        let data: Vec<u8> = (0..usize::try_from(file.length).unwrap())
            .map(|i| ((i * 31 + i / 251) % 256).to_le_bytes()[0])
            .collect();
        let mut verifier =
            Verifier::new(file.length, file.hash, index.slice_size, &file.slices).unwrap();
        for chunk in data.chunks(3) {
            verifier.push(chunk, || false).unwrap();
        }
        assert_eq!(verifier.finish(|| false).unwrap().bytes, file.length);
    }
}

#[test]
fn empty_files_need_no_source_or_recovery_data() {
    let verifier = Verifier::new(0, Md5::digest(b"").into(), 0, &[]).unwrap();
    assert_eq!(verifier.finish(|| false).unwrap().bytes, 0);
}

#[test]
fn panicking_cancellation_callback_cannot_leave_reusable_state() {
    let mut verifier = Verifier::new(3, Md5::digest(b"abc").into(), 0, &[]).unwrap();
    assert!(
        std::panic::catch_unwind(std::panic::AssertUnwindSafe(|| {
            verifier.push(b"abc", || panic!("callback failed"))
        }))
        .is_err()
    );
    assert!(verifier.push(b"abc", || false).is_err());
    assert!(verifier.finish(|| false).is_err());
}
