//! What a file name says about a file: archive volume, split part, or plain file.

/// How a file takes part in an import.
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum Kind {
    Par2,
    /// RAR volume; `volume` is the zero-based position its name implies.
    Rar {
        set: String,
        volume: u32,
    },
    /// `.7z` or one of `.7z.001`, `.7z.002`, …
    SevenZip {
        set: String,
        volume: u32,
    },
    /// Raw byte split of a video, `video.mkv.001`, …
    Split {
        set: String,
        volume: u32,
    },
    Plain,
}

const BLOCKED: &[&str] = &[
    "par2", "nfo", "sfv", "srr", "srs", "exe", "scr", "bat", "cmd", "com", "lnk", "url", "vbs",
    "ps1",
];

fn ext(name: &str) -> Option<(&str, &str)> {
    name.rsplit_once('.')
}

fn digits(s: &str) -> Option<u32> {
    if s.is_empty() || !s.bytes().all(|b| b.is_ascii_digit()) {
        return None;
    }
    s.parse().ok()
}

pub fn classify(name: &str) -> Kind {
    let lower = name.to_ascii_lowercase();
    let Some((stem, ext)) = ext(&lower) else {
        return Kind::Plain;
    };
    if ext == "par2" {
        return Kind::Par2;
    }
    if ext == "rar" {
        if let Some((set, part)) = stem.rsplit_once(".part")
            && let Some(n) = digits(part)
        {
            return Kind::Rar {
                set: set.to_owned(),
                volume: n.saturating_sub(1),
            };
        }
        return Kind::Rar {
            set: stem.to_owned(),
            volume: 0,
        };
    }
    if let Some(letter @ b'r'..=b'z') = ext.bytes().next()
        && ext.len() == 3
        && let Some(n) = digits(&ext[1..])
    {
        return Kind::Rar {
            set: stem.to_owned(),
            volume: u32::from(letter - b'r') * 100 + n + 1,
        };
    }
    if ext == "7z" {
        return Kind::SevenZip {
            set: lower,
            volume: 0,
        };
    }
    if let Some(n) = digits(ext) {
        if stem.rsplit_once('.').is_some_and(|(_, e)| e == "7z") {
            return Kind::SevenZip {
                set: stem.to_owned(),
                volume: n.saturating_sub(1),
            };
        }
        if ext.len() == 3 && is_video(stem) {
            return Kind::Split {
                set: stem.to_owned(),
                volume: n,
            };
        }
    }
    Kind::Plain
}

pub use infinidysk_core::media::is_video;

/// Files never worth importing: PAR2 and release info, unpack leftovers, and programs.
const BLOCKED_GLOBS: &[&str] = &["*.unpack.*", "*unpack.mkv"];

/// Files never worth importing, plus those matching an `extra` glob.
pub fn is_blocked(name: &str, extra: &[String]) -> bool {
    ext(name).is_some_and(|(_, e)| BLOCKED.iter().any(|b| e.eq_ignore_ascii_case(b)))
        || BLOCKED_GLOBS.iter().any(|g| glob(g, name))
        || extra.iter().any(|g| glob(g, name))
}

/// Case-insensitive match of `name` against `pattern`, where `*` is any run of characters
/// and `?` one character.
pub fn glob(pattern: &str, name: &str) -> bool {
    let (p, n): (Vec<char>, Vec<char>) = (
        pattern.to_lowercase().chars().collect(),
        name.to_lowercase().chars().collect(),
    );
    let (mut pi, mut ni) = (0, 0);
    // Where the last `*` was and how much of `name` it has swallowed.
    let mut star: Option<(usize, usize)> = None;
    while ni < n.len() {
        if pi < p.len() && (p[pi] == '?' || p[pi] == n[ni]) {
            pi += 1;
            ni += 1;
        } else if pi < p.len() && p[pi] == '*' {
            star = Some((pi, ni));
            pi += 1;
        } else if let Some((sp, sn)) = star {
            pi = sp + 1;
            ni = sn + 1;
            star = Some((sp, sn + 1));
        } else {
            return false;
        }
    }
    p[pi..].iter().all(|&c| c == '*')
}

/// Whether a file stem has `sample` or `proof` as a word of its own.
pub fn is_sample(stem: &str) -> bool {
    stem.split(|c: char| !c.is_alphanumeric())
        .any(|w| w.eq_ignore_ascii_case("sample") || w.eq_ignore_ascii_case("proof"))
}

/// Whether a file stem (no extension) looks like random characters rather than a title,
/// using the same signals as `SABnzbd`'s deobfuscation.
pub fn is_obfuscated(stem: &str) -> bool {
    let hex = |c: char| c.is_ascii_digit() || ('a'..='f').contains(&c);
    if stem.len() == 32 && stem.chars().all(hex) {
        return true;
    }
    if stem.len() >= 40 && stem.chars().all(|c| hex(c) || c == '.') {
        return true;
    }
    if stem.starts_with("abc.xyz") {
        return true;
    }
    let count = |f: fn(&char) -> bool| stem.chars().filter(f).count();
    let digits = count(char::is_ascii_digit);
    let upper = count(|c| c.is_uppercase());
    let lower = count(|c| c.is_lowercase());
    let gaps = count(|c| matches!(c, ' ' | '.' | '_'));
    let titled =
        stem.chars().next().is_some_and(char::is_uppercase) && lower > 2 && upper * 4 <= lower;
    !((upper >= 2 && lower >= 2 && gaps >= 1)
        || gaps >= 3
        || (upper + lower >= 4 && digits >= 4 && gaps >= 1)
        || titled)
}

/// Bytes in a path component: 255 less room for ` (n)` and `.rclonelink`.
pub const MAX_NAME: usize = 230;

const RESERVED: [&str; 22] = [
    "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8",
    "COM9", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
];

/// One path component that is safe on Linux and Windows, or `None` when nothing usable
/// is left.
pub fn sanitize(component: &str) -> Option<String> {
    let clean: String = component
        .chars()
        .map(|c| match c {
            '/' | '\\' | '<' | '>' | ':' | '"' | '|' | '?' | '*' | '\u{FFFE}' | '\u{FFFF}' => '_',
            c if c.is_control() => '_',
            c => c,
        })
        .collect();
    let mut clean = trim(&clean).to_owned();
    let stem = clean.split('.').next().unwrap_or_default();
    if RESERVED.iter().any(|r| r.eq_ignore_ascii_case(stem)) {
        clean.insert(0, '_');
    }
    if clean.len() > MAX_NAME {
        let ext = clean
            .rfind('.')
            .map(|at| &clean[at..])
            .filter(|ext| ext.len() <= 16)
            .unwrap_or_default();
        let mut cut = MAX_NAME - ext.len();
        while !clean.is_char_boundary(cut) {
            cut -= 1;
        }
        clean = format!("{}{ext}", trim(&clean[..cut]));
    }
    (!clean.is_empty()).then_some(clean)
}

/// Windows drops trailing dots and spaces, so names never end in one.
fn trim(name: &str) -> &str {
    name.trim_start()
        .trim_end_matches(|c: char| c == '.' || c.is_whitespace())
}

/// The job name SABnzbd derives from an NZB file name: no `.nzb`, no password.
pub fn job_name(file_name: &str) -> String {
    sanitize(split_password(file_name).0).unwrap_or_else(|| "job".to_owned())
}

/// The archive password an NZB file name carries, as `name{{pw}}.nzb` or
/// `name password=pw.nzb`.
pub fn password(file_name: &str) -> Option<&str> {
    split_password(file_name).1
}

/// `file_name` without directories and password.
pub fn without_password(file_name: &str) -> String {
    let mut base = split_password(file_name).0;
    // A name can carry both markers; strip until none parses.
    loop {
        let next = split_password(base).0;
        if next == base {
            return format!("{base}.nzb");
        }
        base = next;
    }
}

/// The file name without directories, `.nzb` and password, and the password.
fn split_password(file_name: &str) -> (&str, Option<&str>) {
    let base = file_name.rsplit(['/', '\\']).next().unwrap_or(file_name);
    let base = match base.len().checked_sub(4) {
        Some(cut)
            if base
                .get(cut..)
                .is_some_and(|e| e.eq_ignore_ascii_case(".nzb")) =>
        {
            &base[..cut]
        }
        _ => base,
    };
    let found = match base.find("{{") {
        Some(at) if base.ends_with("}}") && base.len() >= at + 4 => {
            Some((at, &base[at + 2..base.len() - 2]))
        }
        _ => base
            .to_ascii_lowercase()
            .find("password=")
            .map(|at| (at, &base[at + 9..])),
    };
    match found {
        Some((at, pw)) => (
            base[..at].trim_end_matches(|c: char| c == '-' || c.is_whitespace()),
            Some(pw).filter(|pw| !pw.is_empty()),
        ),
        None => (base, None),
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn rar(set: &str, volume: u32) -> Kind {
        Kind::Rar {
            set: set.into(),
            volume,
        }
    }

    #[test]
    fn classifies_volume_schemes() {
        assert_eq!(classify("a.part01.rar"), rar("a", 0));
        assert_eq!(classify("A.Part10.RAR"), rar("a", 9));
        assert_eq!(classify("a.rar"), rar("a", 0));
        assert_eq!(classify("a.r00"), rar("a", 1));
        assert_eq!(classify("a.r99"), rar("a", 100));
        assert_eq!(classify("A.S00"), rar("a", 101));
        assert_eq!(classify("a.t05"), rar("a", 206));
        assert_eq!(classify("a.z99"), rar("a", 900));
        assert_eq!(classify("a.s0"), Kind::Plain);
        assert_eq!(classify("a.s000"), Kind::Plain);
        assert_eq!(classify("a.sa0"), Kind::Plain);
        assert_eq!(
            classify("a.7z.002"),
            Kind::SevenZip {
                set: "a.7z".into(),
                volume: 1
            }
        );
        assert_eq!(
            classify("a.mkv.003"),
            Kind::Split {
                set: "a.mkv".into(),
                volume: 3
            }
        );
        assert_eq!(classify("a.bin.001"), Kind::Plain);
        assert_eq!(classify("a.vol00+01.par2"), Kind::Par2);
        assert_eq!(job_name("dir/a.b{{pw}}.NZB"), "a.b");
        assert_eq!(password("dir/a.b{{p w}}.NZB"), Some("p w"));
        assert_eq!(job_name("a.b - Password=s3cret.nzb"), "a.b");
        assert_eq!(password("a.b - Password=s3cret.nzb"), Some("s3cret"));
        assert_eq!(password("a.b{{}}.nzb"), None);
        assert_eq!(without_password("dir/a.b{{pw}}.NZB"), "a.b.nzb");
        assert_eq!(without_password("a{{2024}} - password=x.nzb"), "a.nzb");
        assert_eq!(
            password(&without_password("a{{2024}} - password=x.nzb")),
            None
        );
        assert_eq!(sanitize(".."), None);
    }

    #[test]
    fn sanitizes_for_windows() {
        for (raw, safe) in [
            ("a:b?<c>|\"d\"*.mkv", "a_b__c___d__.mkv"),
            ("tab\there", "tab_here"),
            ("  x. . ", "x"),
            ("con.foo.txt", "_con.foo.txt"),
            ("Lpt1", "_Lpt1"),
            ("CONSOLE.txt", "CONSOLE.txt"),
        ] {
            assert_eq!(sanitize(raw).as_deref(), Some(safe), "{raw}");
        }
        let long = sanitize(&format!("a{}.mkv", "é".repeat(200))).unwrap();
        assert!(long.len() <= MAX_NAME && long.ends_with("é.mkv"), "{long}");
        assert_eq!(sanitize(". ."), None);
    }

    #[test]
    fn blocks_junk_and_extra_globs() {
        let extra = ["*.TXT".to_owned(), "cover?.jpg".to_owned()];
        for name in [
            "a.nfo",
            "setup.EXE",
            "x.unpack.tmp",
            "aunpack.mkv",
            "read.txt",
            "cover1.jpg",
        ] {
            assert!(is_blocked(name, &extra), "{name}");
        }
        for name in ["a.mkv", "cover12.jpg", "a.txt.srt"] {
            assert!(!is_blocked(name, &extra), "{name}");
        }
        assert!(glob("a*b*c", "aXbYbZc") && !glob("a*b", "ab c"));
        assert!(is_sample("show-SAMPLE") && is_sample("x.proof.y"));
        assert!(!is_sample("samples") && !is_sample("example"));
    }

    #[test]
    fn spots_obfuscated_stems() {
        for stem in [
            "b082fa0beaa644d3aa01045d5b8d0b36",
            "0675e29e9abfd2.f7d069dab0b853283cc1b069a25f82.6547",
            "abc.xyz.a4c567edbcbf27.BLA",
            "kj2Hq9xLp0Zr",
        ] {
            assert!(is_obfuscated(stem), "{stem}");
        }
        for stem in [
            "Great Distro",
            "this is a download",
            "Holiday 2020",
            "Testfile",
            "Some.Show.S01E02.1080p",
        ] {
            assert!(!is_obfuscated(stem), "{stem}");
        }
    }
}
