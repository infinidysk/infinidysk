use std::io::Write;

use infinidysk_queue::nzb::{Error, Segment, decompress, parse};

const NZB: &str = r#"<?xml version="1.0" encoding="utf-8"?>
<!DOCTYPE nzb PUBLIC "-//newzBin//DTD NZB 1.1//EN" "http://www.newzbin.com/DTD/nzb/nzb-1.1.dtd">
<nzb xmlns="http://www.newzbin.com/DTD/2003/nzb">
  <head>
    <meta type="password">p&amp;w</meta>
    <meta type="tag">a</meta>
    <meta type="tag">b</meta>
    <meta type="empty"></meta>
  </head>
  <file poster="x" date="1700000000" subject="[1/2] - &quot;video.mkv&quot; yEnc (1/4)">
    <groups><group>alt.binaries.test</group></groups>
    <segments>
      <segment bytes="300" number="3"> &lt;c@x&gt; </segment>
      <segment bytes="100" number="1">a@x</segment>
      <segment bytes="999" number="1">dup@x</segment>
      <segment bytes="0" number="2">zero@x</segment>
      <segment bytes="9000000" number="4">huge@x</segment>
      <segment number="5">nobytes@x</segment>
      <segment bytes="10" number="six">nan@x</segment>
      <segment bytes="10" number="7"><![CDATA[<g@x>]]></segment>
      <segment bytes="10" number="8">   </segment>
    </segments>
  </file>
  <file subject="" date="bad"><segments><segment bytes="0" number="1">z@x</segment></segments></file>
  <file subject="" date="bad"><segments><segment bytes="5" number="1">y@x</segment></segments></file>
</nzb>"#;

#[test]
fn accepts_what_sabnzbd_accepts() {
    let nzb = parse(NZB.as_bytes()).unwrap();
    assert_eq!(nzb.meta("password").collect::<Vec<_>>(), ["p&w"]);
    assert_eq!(nzb.meta("tag").collect::<Vec<_>>(), ["a", "b"]);
    assert_eq!(nzb.meta("empty").count(), 0);

    let [video, unknown] = &nzb.files[..] else {
        panic!("{:?}", nzb.files)
    };
    assert_eq!(
        (video.name(), video.date),
        ("video.mkv", Some(1_700_000_000))
    );
    let seg = |id: &str, bytes, number| Segment {
        id: id.to_owned(),
        bytes,
        number,
    };
    assert_eq!(
        video.segments,
        [seg("a@x", 100, 1), seg("c@x", 300, 3), seg("g@x", 10, 7)]
    );
    assert_eq!(video.bytes(), 410);
    assert!(video.gap && !unknown.gap);
    assert_eq!(
        (unknown.subject.as_str(), unknown.date, unknown.bytes()),
        ("unknown", None, 5)
    );
}

#[test]
fn reads_gzip_and_latin1_and_rejects_empty_or_broken() {
    let mut gz = flate2::write::GzEncoder::new(Vec::new(), flate2::Compression::fast());
    gz.write_all(NZB.as_bytes()).unwrap();
    let gz = gz.finish().unwrap();
    assert_eq!(parse(&decompress(&gz).unwrap()).unwrap().files.len(), 2);

    let latin1 = b"<nzb><file subject=\"caf\xe9.mkv\"><segments><segment bytes=\"1\" number=\"1\">a@x</segment></segments></file></nzb>";
    assert_eq!(parse(latin1).unwrap().files[0].name(), "caf\u{e9}.mkv");

    assert!(matches!(
        parse(b"<nzb><file subject=\"a\"/></nzb>"),
        Err(Error::Empty)
    ));
    assert!(matches!(parse(b"<nzb><file></nzb>"), Err(Error::Xml(_))));
}

#[test]
fn extreme_part_numbers_keep_their_order_without_overflowing_gap_detection() {
    let xml = format!(
        "<nzb><file subject=\"a.bin\"><segments>\
        <segment bytes=\"1\" number=\"{}\">a@x</segment>\
        <segment bytes=\"1\" number=\"{}\">b@x</segment>\
        </segments></file></nzb>",
        i64::MIN,
        i64::MAX
    );
    let nzb = parse(xml.as_bytes()).unwrap();
    assert!(nzb.files[0].gap);
    assert_eq!(
        nzb.files[0]
            .segments
            .iter()
            .map(|s| s.number)
            .collect::<Vec<_>>(),
        [i64::MIN, i64::MAX]
    );
}

/// Local sanity check: `INFINIDYSK_SAMPLE_NZBS=scrap/samplenzb cargo test -p infinidysk-queue -- --ignored`.
#[test]
#[ignore = "needs local sample NZBs"]
fn sample_nzbs_parse() {
    let dir = std::env::var("INFINIDYSK_SAMPLE_NZBS").expect("INFINIDYSK_SAMPLE_NZBS is set");
    for (i, entry) in std::fs::read_dir(dir).unwrap().enumerate() {
        let raw = std::fs::read(entry.unwrap().path()).unwrap();
        // Prints counts only: sample names and subjects contain media titles.
        match decompress(&raw).and_then(|xml| parse(&xml)) {
            Ok(nzb) => {
                let segments: usize = nzb.files.iter().map(|f| f.segments.len()).sum();
                println!("#{i}: {} files, {segments} segments", nzb.files.len());
            }
            Err(e) => println!("#{i}: {e}"),
        }
    }
}
