use infinidysk_par2::{Reader, Repair, hash_16k, read};
use md5::{Digest, Md5};

const INDEX: &[u8] = include_bytes!("fixtures/set.par2");
const VOLUME: &[u8] = include_bytes!("fixtures/set.vol0+3.par2");

fn reader() -> Reader {
    let mut reader = Reader::default();
    reader.push(INDEX);
    reader.push(VOLUME);
    reader
}

#[test]
fn plans_and_reconstructs_a_complete_independent_set() {
    let reader = reader();
    let set = reader.repair_set(read(INDEX).set_id.unwrap()).unwrap();
    assert_eq!(set.sources().len(), 2);
    assert_eq!(set.inputs().len(), 27);
    assert_eq!(set.recovery().len(), 3);
    let mut repair = Repair::new(
        set.slice_size(),
        set.inputs(),
        &[0, 26],
        set.recovery(),
        2_000_000,
        || false,
    )
    .unwrap();
    for (i, input) in set.inputs().iter().enumerate() {
        let (source, range) = set.source(i).unwrap();
        assert_eq!(range.end - range.start, input.length as u64);
        let data: Vec<u8> = (range.start..range.end)
            .map(|i| ((i * 31 + i / 251) % 256).to_le_bytes()[0])
            .collect();
        assert!(range.end <= set.sources()[source].file.length);
        if i != 0 && i != 26 {
            repair.feed(i, &data, || false).unwrap();
        }
    }
    for recovered in repair.finish(|| false).unwrap() {
        let (_, range) = set.source(recovered.index).unwrap();
        let expected: Vec<u8> = (range.start..range.end)
            .map(|i| ((i * 31 + i / 251) % 256).to_le_bytes()[0])
            .collect();
        assert_eq!(recovered.data, expected);
    }
    assert!(set.source(27).is_none());
}

#[test]
fn maps_slice_crossings_to_whole_source_article_ranges() {
    let reader = reader();
    let set = reader.repair_set(read(INDEX).set_id.unwrap()).unwrap();
    let source = &set.sources()[0];
    let mut lengths = vec![3000; usize::try_from(source.file.length / 3000).unwrap()];
    let tail = u32::try_from(source.file.length % 3000).unwrap();
    if tail != 0 {
        lengths.push(tail);
    }
    let parts = set.articles(source.slices.start + 1, &lengths).unwrap();
    assert_eq!(parts, [(1, 1096..3000), (2, 0..2192)]);
    assert!(set.articles(source.slices.start, &[1]).is_err());
}

#[test]
fn source_identity_uses_the_par2_description() {
    let reader = reader();
    let set = reader.repair_set(read(INDEX).set_id.unwrap()).unwrap();
    for source in set.sources() {
        let data: Vec<u8> = (0..source.file.length.min(16_384))
            .map(|i| ((i * 31 + i / 251) % 256).to_le_bytes()[0])
            .collect();
        assert_eq!(source.file.hash_16k, hash_16k(&data));
    }
}

fn packets(mut data: &[u8]) -> Vec<&[u8]> {
    let mut result = Vec::new();
    while !data.is_empty() {
        let size = usize::try_from(u64::from_le_bytes(
            data[8..16].try_into().expect("complete fixture header"),
        ))
        .expect("small fixture packet");
        result.push(&data[..size]);
        data = &data[size..];
    }
    result
}

fn packet(set: [u8; 16], kind: &[u8; 16], body: &[u8]) -> Vec<u8> {
    let mut rest = [set.as_slice(), kind, body].concat();
    rest.resize(rest.len().next_multiple_of(4), 0);
    [
        b"PAR2\0PKT".as_slice(),
        &(32 + rest.len() as u64).to_le_bytes(),
        &Md5::digest(&rest),
        &rest,
    ]
    .concat()
}

fn rehash(packet: &mut [u8]) {
    let hash = Md5::digest(&packet[32..]);
    packet[16..32].copy_from_slice(&hash);
}

fn main_packet() -> &'static [u8] {
    packets(INDEX)
        .into_iter()
        .find(|p| &p[48..64] == b"PAR 2.0\0Main\0\0\0\0")
        .expect("fixture Main packet")
}

#[test]
fn recovery_packets_do_not_imply_that_metadata_is_complete() {
    let set = read(INDEX).set_id.unwrap();
    let mut reader = Reader::default();
    for p in packets(VOLUME).into_iter().rev() {
        reader.push(p);
    }
    assert!(reader.reached_recovery());
    // Omit descriptors first; recover them from a different volume afterwards.
    let mut partial = Reader::default();
    for p in packets(VOLUME) {
        if &p[48..64] != b"PAR 2.0\0FileDesc" {
            partial.push(p);
        }
    }
    assert!(partial.reached_recovery());
    assert!(partial.repair_set(set).is_err());
    for p in packets(INDEX).into_iter().rev() {
        for bytes in p.chunks(7) {
            partial.push(bytes);
        }
    }
    let complete = partial.repair_set(set).unwrap();
    assert_eq!(complete.inputs().len(), 27);
    assert_eq!(complete.recovery().len(), 3);
    assert_eq!(reader.repair_set(set).unwrap().inputs().len(), 27);
}

#[test]
fn non_recoverable_files_do_not_shift_global_slice_indices() {
    let set = read(INDEX).set_id.unwrap();
    let mut main = main_packet().to_vec();
    main[72..76].copy_from_slice(&1u32.to_le_bytes());
    rehash(&mut main);
    let mut reader = Reader::default();
    reader.push(&main);
    for p in packets(INDEX) {
        if &p[48..64] != b"PAR 2.0\0Main\0\0\0\0" {
            reader.push(p);
        }
    }
    let plan = reader.repair_set(set).unwrap();
    assert_eq!(reader.index().files.len(), 2);
    assert_eq!(plan.sources().len(), 1);
    assert_eq!(plan.sources()[0].file.id.as_slice(), &main[76..92]);
    assert_eq!(plan.sources()[0].slices, 0..plan.inputs().len());
    assert!(plan.recovery().is_empty());
}

#[test]
fn empty_source_files_need_no_ifsc_or_global_slice() {
    let set = read(INDEX).set_id.unwrap();
    let empty_id = [42; 16];
    let original = main_packet();
    let body = [
        &original[64..72],
        &3u32.to_le_bytes(),
        &empty_id,
        &original[76..],
    ]
    .concat();
    let empty = [
        empty_id.as_slice(),
        Md5::digest(b"").as_slice(),
        Md5::digest(b"").as_slice(),
        &0u64.to_le_bytes(),
        b"empty.bin",
    ]
    .concat();
    let mut reader = Reader::default();
    reader.push(&packet(set, b"PAR 2.0\0Main\0\0\0\0", &body));
    reader.push(&packet(set, b"PAR 2.0\0FileDesc", &empty));
    for p in packets(INDEX) {
        if &p[48..64] != b"PAR 2.0\0Main\0\0\0\0" {
            reader.push(p);
        }
    }
    let plan = reader.repair_set(set).unwrap();
    assert_eq!(plan.sources()[0].slices, 0..0);
    assert_eq!(plan.source(0).unwrap().0, 1);
    assert_eq!(plan.inputs().len(), 27);
}

#[test]
fn conflicting_descriptions_and_slice_checksums_are_rejected() {
    let set = read(INDEX).set_id.unwrap();
    for kind in [b"PAR 2.0\0FileDesc", b"PAR 2.0\0IFSC\0\0\0\0"] {
        let mut conflicting = packets(INDEX)
            .into_iter()
            .find(|p| &p[48..64] == kind)
            .unwrap()
            .to_vec();
        conflicting[80] ^= 1;
        rehash(&mut conflicting);
        let mut reader = reader();
        reader.push(&conflicting);
        assert!(reader.repair_set(set).is_err());
    }
}

#[test]
fn missing_or_malformed_metadata_cannot_be_used_for_repair() {
    let set = read(INDEX).set_id.unwrap();
    for omitted in [
        b"PAR 2.0\0Main\0\0\0\0",
        b"PAR 2.0\0FileDesc",
        b"PAR 2.0\0IFSC\0\0\0\0",
    ] {
        let mut reader = Reader::default();
        for p in packets(INDEX) {
            if &p[48..64] != omitted {
                reader.push(p);
            }
        }
        assert!(reader.repair_set(set).is_err());
    }
    let original = main_packet();
    for body in [
        original[64..].iter().copied().chain([0; 4]).collect(),
        [0u64.to_le_bytes().as_slice(), &original[72..]].concat(),
        [5u64.to_le_bytes().as_slice(), &original[72..]].concat(),
        [&original[64..72], &3u32.to_le_bytes(), &original[76..]].concat(),
        [&original[64..92], &original[76..92]].concat(),
        [
            &original[64..72],
            &1u32.to_le_bytes(),
            &original[76..92],
            &original[76..92],
        ]
        .concat(),
    ] {
        let mut reader = Reader::default();
        reader.push(&packet(set, b"PAR 2.0\0Main\0\0\0\0", &body));
        for p in packets(INDEX) {
            if &p[48..64] != b"PAR 2.0\0Main\0\0\0\0" {
                reader.push(p);
            }
        }
        assert!(reader.repair_set(set).is_err());
    }
}

#[test]
fn identical_and_equivalent_recovery_rows_are_deduplicated() {
    let set = read(INDEX).set_id.unwrap();
    let zero = packets(VOLUME)
        .into_iter()
        .find(|p| &p[48..64] == b"PAR 2.0\0RecvSlic" && p[64..68] == [0; 4])
        .unwrap();
    let mut equivalent = zero.to_vec();
    equivalent[64..68].copy_from_slice(&65_535u32.to_le_bytes());
    rehash(&mut equivalent);
    let mut reader = reader();
    reader.push(VOLUME);
    reader.push(&equivalent);
    assert_eq!(reader.repair_set(set).unwrap().recovery().len(), 3);
    equivalent[68] ^= 1;
    rehash(&mut equivalent);
    reader.push(&equivalent);
    assert!(reader.repair_set(set).is_err());
}

#[test]
fn mixed_sets_are_selected_by_identity_and_never_combined() {
    let mut reader = Reader::default();
    let odd = include_bytes!("fixtures/odd.par2");
    reader.push(odd);
    reader.push(include_bytes!("fixtures/odd.vol5+3.par2"));
    reader.push(INDEX);
    reader.push(VOLUME);
    let small = reader.repair_set(read(odd).set_id.unwrap()).unwrap();
    let large = reader.repair_set(read(INDEX).set_id.unwrap()).unwrap();
    assert_eq!(small.slice_size(), 128);
    assert_eq!(large.slice_size(), 4096);
    assert_eq!(small.recovery().len(), 3);
    assert_eq!(large.recovery().len(), 3);
    assert!(reader.repair_set([0; 16]).is_err());
}

#[test]
fn all_slice_ranges_cover_exact_source_bytes_with_zero_length_articles() {
    let reader = reader();
    let set = reader.repair_set(read(INDEX).set_id.unwrap()).unwrap();
    for source in set.sources() {
        let mut lengths: Vec<u32> = (0..source.file.length)
            .step_by(7000)
            .flat_map(|offset| {
                [
                    0,
                    u32::try_from((source.file.length - offset).min(7000)).unwrap(),
                ]
            })
            .collect();
        lengths.push(0);
        for index in source.slices.clone() {
            let (_, range) = set.source(index).unwrap();
            let parts = set.articles(index, &lengths).unwrap();
            let mut next = range.start;
            for (i, data) in parts {
                let begin: u64 = lengths[..i].iter().map(|&n| u64::from(n)).sum();
                assert_eq!(begin + data.start as u64, next);
                assert!(data.end as u64 <= u64::from(lengths[i]));
                next = begin + data.end as u64;
            }
            assert_eq!(next, range.end);
        }
    }
}

#[test]
fn short_odd_final_slices_map_only_real_source_bytes() {
    let index = include_bytes!("fixtures/odd.par2");
    let mut reader = Reader::default();
    reader.push(index);
    let set = reader.repair_set(read(index).set_id.unwrap()).unwrap();
    for source in set.sources() {
        let last = source.slices.end - 1;
        let (_, range) = set.source(last).unwrap();
        assert_eq!(range.end, source.file.length);
        assert_eq!(range.end - range.start, source.file.length % 128);
        let length = u32::try_from(source.file.length).unwrap();
        assert_eq!(
            set.articles(last, &[length]).unwrap(),
            [(
                0,
                usize::try_from(range.start).unwrap()..usize::try_from(range.end).unwrap()
            )]
        );
    }
}

#[test]
fn accepts_the_input_limit_and_rejects_larger_descriptions_before_building_inputs() {
    let set = [11; 16];
    let file_id = [22; 16];
    let main = [4u64.to_le_bytes().as_slice(), &1u32.to_le_bytes(), &file_id].concat();
    let checksums = [Md5::digest([0; 4]).as_slice(), &[0; 4]].concat();
    let ifsc = [file_id.as_slice(), checksums.repeat(32_768).as_slice()].concat();
    for length in [131_072u64, 131_073, u64::MAX] {
        let desc = [
            file_id.as_slice(),
            &[0; 16],
            &[0; 16],
            &length.to_le_bytes(),
            b"a.bin",
        ]
        .concat();
        let mut reader = Reader::default();
        reader.push(&packet(set, b"PAR 2.0\0Main\0\0\0\0", &main));
        reader.push(&packet(set, b"PAR 2.0\0FileDesc", &desc));
        reader.push(&packet(set, b"PAR 2.0\0IFSC\0\0\0\0", &ifsc));
        let plan = reader.repair_set(set);
        if length == 131_072 {
            let plan = plan.unwrap();
            assert_eq!(plan.inputs().len(), 32_768);
            assert_eq!(plan.source(32_767), Some((0, 131_068..131_072)));
        } else {
            assert!(plan.is_err());
        }
    }
}

#[test]
fn conflicting_main_packets_are_not_resolved_by_input_order() {
    let set = read(INDEX).set_id.unwrap();
    let mut different = main_packet().to_vec();
    different[64..72].copy_from_slice(&8192u64.to_le_bytes());
    rehash(&mut different);
    for reverse in [false, true] {
        let mut reader = Reader::default();
        if reverse {
            reader.push(&different);
        }
        reader.push(INDEX);
        if !reverse {
            reader.push(&different);
        }
        assert!(reader.repair_set(set).is_err());
    }
}

#[test]
fn unicode_names_and_duplicate_display_names_do_not_change_source_identity() {
    let mut reader = reader();
    let set = read(INDEX).set_id.unwrap();
    let files = read(INDEX).files;
    for file in &files {
        let name: Vec<_> = "source.bin"
            .encode_utf16()
            .flat_map(u16::to_le_bytes)
            .collect();
        let body = [file.id.as_slice(), &name].concat();
        reader.push(&packet(set, b"PAR 2.0\0UniFileN", &body));
    }
    let plan = reader.repair_set(set).unwrap();
    assert_eq!(plan.sources().len(), 2);
    for (source, original) in plan.sources().iter().zip(&files) {
        assert_eq!(source.file.name, "source.bin");
        assert_eq!(source.file.id, original.id);
    }
}

#[test]
fn incomplete_and_corrupt_recovery_packets_are_never_counted() {
    let set = read(INDEX).set_id.unwrap();
    let row = packets(VOLUME)
        .into_iter()
        .find(|p| &p[48..64] == b"PAR 2.0\0RecvSlic")
        .unwrap();
    let mut reader = Reader::default();
    reader.push(INDEX);
    reader.push(&row[..68]);
    assert!(reader.reached_recovery());
    assert!(reader.repair_set(set).unwrap().recovery().is_empty());
    reader.push(&row[68..]);
    assert_eq!(reader.repair_set(set).unwrap().recovery().len(), 1);
    let mut wrong = row.to_vec();
    wrong[68] ^= 1;
    reader.push(&wrong);
    assert_eq!(reader.repair_set(set).unwrap().recovery().len(), 1);
}

#[test]
fn tolerant_indexing_keeps_available_descriptors_when_repair_metadata_is_incomplete() {
    let set = read(INDEX).set_id.unwrap();
    let first_id = &main_packet()[76..92];
    let mut reader = Reader::default();
    for p in packets(INDEX) {
        if &p[48..64] != b"PAR 2.0\0FileDesc" || &p[64..80] != first_id {
            reader.push(p);
        }
    }
    let index = reader.index();
    assert_eq!(index.files.len(), 1);
    assert_ne!(index.files[0].id.as_slice(), first_id);
    assert!(reader.repair_set(set).is_err());
}
