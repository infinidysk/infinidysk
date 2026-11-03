//! Fixtures: `par2 create -s4096 -r10 -n1 set.par2 a.bin b.bin` over [`data`] files.

use infinidysk_par2::{Reader, hash_16k, read};
use md5::{Digest, Md5};

fn fixture(name: &str) -> Vec<u8> {
    std::fs::read(format!(
        "{}/tests/fixtures/{name}",
        env!("CARGO_MANIFEST_DIR")
    ))
    .expect("fixture exists")
}

/// Same bytes as `(i * 31 + i // 251) % 256` in the generator.
fn data(len: usize) -> Vec<u8> {
    (0..len)
        .map(|i| ((i * 31 + i / 251) % 256).to_le_bytes()[0])
        .collect()
}

#[test]
fn index_describes_and_verifies_every_slice() {
    let index = read(&fixture("set.par2"));
    assert!(!index.truncated && !index.reached_recovery);
    assert_eq!(index.slice_size, 4096);
    let mut names: Vec<_> = index
        .files
        .iter()
        .map(|f| (f.name.as_str(), f.length))
        .collect();
    names.sort_unstable();
    assert_eq!(names, [("a.bin", 100_000), ("b.bin", 8192)]);
    for f in &index.files {
        let bytes = data(usize::try_from(f.length).unwrap());
        assert_eq!(f.hash, <[u8; 16]>::from(Md5::digest(&bytes)));
        assert_eq!(f.hash_16k, hash_16k(&bytes));
        let chunks: Vec<_> = bytes.chunks(4096).collect();
        assert_eq!(f.slices.len(), chunks.len());
        assert!(
            f.slices
                .iter()
                .zip(&chunks)
                .all(|(s, c)| s.matches(4096, c))
        );
        let mut bad = chunks[0].to_vec();
        bad[7] ^= 1;
        assert!(!f.slices[0].matches(4096, &bad));
    }
}

#[test]
fn prefixes_damage_and_recovery_volumes() {
    let full = fixture("set.par2");
    let whole = read(&full);
    let cut = read(&full[..full.len() - 10]);
    assert!(cut.truncated && cut.files.len() <= whole.files.len());

    let mut damaged = full;
    damaged[100] ^= 0xff;
    let damaged = read(&damaged);
    assert!(!damaged.truncated);
    assert_eq!(damaged.set_id, whole.set_id);

    let vol = read(&fixture("set.vol0+3.par2"));
    assert!(vol.reached_recovery);
    assert_eq!(vol.set_id, whole.set_id);
    assert_eq!(vol.files, whole.files);
}

fn packet(set: &[u8; 16], kind: &[u8; 16], body: &[u8]) -> Vec<u8> {
    let mut rest = [set.as_slice(), kind, body].concat();
    rest.resize(rest.len().next_multiple_of(4), 0);
    let len = 32 + rest.len() as u64;
    [
        b"PAR2\0PKT".as_slice(),
        &len.to_le_bytes(),
        &Md5::digest(&rest),
        &rest,
    ]
    .concat()
}

fn file_desc(set: &[u8; 16], fid: &[u8; 16], name: &str) -> Vec<u8> {
    let body = [
        fid.as_slice(),
        &[1; 16],
        &[2; 16],
        &7u64.to_le_bytes(),
        name.as_bytes(),
    ]
    .concat();
    packet(set, b"PAR 2.0\0FileDesc", &body)
}

#[test]
fn indexes_many_descriptors_in_order_and_first_wins() {
    const N: u128 = 20_000;
    let set = [9; 16];
    let ids: Vec<[u8; 16]> = (0..N).map(|i| (i + 1).to_le_bytes()).collect();
    let main_body = [
        4096u64.to_le_bytes().as_slice(),
        &u32::try_from(N).unwrap().to_le_bytes(),
        &ids.iter().rev().flatten().copied().collect::<Vec<u8>>(),
    ]
    .concat();
    let mut reader = Reader::default();
    reader.push(&packet(&set, b"PAR 2.0\0Main\0\0\0\0", &main_body));
    for (i, fid) in ids.iter().enumerate() {
        reader.push(&file_desc(&set, fid, &format!("f{i}.bin")));
    }
    reader.push(&file_desc(&set, &ids[5], "dup.bin"));

    let index = reader.index();
    assert_eq!(index.set_id, Some(set));
    assert_eq!(index.slice_size, 4096);
    assert_eq!(index.files.len(), ids.len());
    assert!(
        index
            .files
            .iter()
            .map(|f| f.id)
            .eq(ids.iter().rev().copied())
    );
    let five = index.files.iter().find(|f| f.id == ids[5]).unwrap();
    assert_eq!(five.name, "f5.bin");
    assert_eq!(five.length, 7);
}

#[test]
fn reader_matches_a_full_parse_after_every_piece() {
    let mut damaged = fixture("set.par2");
    damaged[100] ^= 0xff;
    let inputs = [
        [fixture("set.par2"), fixture("set.vol0+3.par2")].concat(),
        [damaged, fixture("set.par2")].concat(),
    ];
    for input in &inputs {
        for piece in [1, 7, 8, 63, 64, 65, 100, 1000] {
            let mut reader = Reader::default();
            for chunk in input.chunks(piece) {
                reader.push(chunk);
                let whole = read(&input[..reader.buffered()]);
                assert_eq!(format!("{:?}", reader.index()), format!("{whole:?}"));
                assert_eq!(reader.reached_recovery(), whole.reached_recovery);
            }
        }
    }
}
