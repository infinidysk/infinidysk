//! Which files health checks look at, and which of their segments they probe.

const VIDEO: &[&str] = &[
    "mkv", "mk3d", "mp4", "m4v", "avi", "ts", "m2ts", "mts", "wmv", "mov", "mpg", "mpeg", "webm",
    "vob", "iso", "flv", "ogm",
];
const AUDIO: &[&str] = &[
    "flac", "mp3", "m4a", "m4b", "aac", "ogg", "opus", "wav", "wma", "alac", "ape", "dsf", "dff",
    "mka", "mp2", "m4p", "mp4a", "oga", "vorbis", "wavpack", "wv", "aif", "aiff", "aifc",
];
/// Video containers that also hold audio-only media, such as `.mp4` audiobooks.
const AUDIO_TOO: &[&str] = &["mp4"];
/// Ebook formats the book Arr imports.
const BOOK: &[&str] = &["epub", "kepub", "mobi", "azw", "azw3", "pdf"];
const ARCHIVE: &[&str] = &["rar", "7z", "zip"];

/// What an Arr category imports.
#[derive(Clone, Copy, Debug, PartialEq, Eq, Hash, PartialOrd, Ord)]
pub enum Kind {
    Video,
    Audio,
    Book,
}

fn ext(name: &str) -> Option<&str> {
    name.rsplit_once('.').map(|(_, e)| e)
}

fn listed(name: &str, list: &[&str]) -> bool {
    ext(name).is_some_and(|e| list.iter().any(|v| e.eq_ignore_ascii_case(v)))
}

#[must_use]
pub fn is_video(name: &str) -> bool {
    listed(name, VIDEO)
}

/// What kind of media a file name says it is; `None` for sidecars and everything else.
#[must_use]
pub fn kind(name: &str) -> Option<Kind> {
    [
        (VIDEO, Kind::Video),
        (AUDIO, Kind::Audio),
        (BOOK, Kind::Book),
    ]
    .into_iter()
    .find_map(|(list, kind)| listed(name, list).then_some(kind))
}

/// Whether a file of this name can be media of kind `wanted`.
#[must_use]
pub fn fits(name: &str, wanted: Kind) -> bool {
    kind(name) == Some(wanted) || (wanted == Kind::Audio && listed(name, AUDIO_TOO))
}

/// Media worth health checks: video, audio, ebooks, archives and their volumes (`.r00`,
/// `.001`). Subtitles, NFOs, images and other sidecars are not.
#[must_use]
pub fn is_checkable(name: &str) -> bool {
    let volume = ext(name).is_some_and(|e| {
        let digits = e.trim_start_matches(['r', 'R']);
        e.len() == 3 && !digits.is_empty() && digits.bytes().all(|b| b.is_ascii_digit())
    });
    volume
        || listed(name, VIDEO)
        || listed(name, AUDIO)
        || listed(name, BOOK)
        || listed(name, ARCHIVE)
}

/// Audio or video whose container media analysis reads: not disc images, archives or
/// their volumes.
#[must_use]
pub fn is_analyzable(name: &str) -> bool {
    (listed(name, VIDEO) || listed(name, AUDIO)) && !listed(name, &["iso"])
}

/// Indices to probe out of `n`: `head` first, `tail` last, the rest evenly spaced; all of
/// them when `count >= n`.
#[must_use]
pub fn spread(n: usize, head: usize, tail: usize, count: usize) -> Vec<usize> {
    if count >= n {
        return (0..n).collect();
    }
    let mut picks: Vec<usize> = (0..head.min(n)).chain(n.saturating_sub(tail)..n).collect();
    let middle = count.saturating_sub(picks.len());
    let span = n.saturating_sub(head + tail);
    picks.extend((0..middle).map(|k| head + (2 * k + 1) * span / (2 * middle)));
    picks.sort_unstable();
    picks.dedup();
    picks
}

/// The default share of segments a health sample probes.
pub const SAMPLE_PERCENT: u8 = 5;

/// Containers that play past a gap of filler bytes.
const GAP_TOLERANT: &[&str] = &["mkv", "webm", "mp4", "m4v", "mov", "ts", "m2ts", "mts"];
/// Damage caps for a file that stays playable; equality is allowed.
pub const MAX_RUN: usize = 4;
const MAX_DAMAGED: usize = 64;
/// Damaged bytes may be at most 1/50 = 2 % of the file.
const MAX_BYTES_DIVISOR: u64 = 50;

/// Whether a file of this name is in a container that plays past filled gaps.
#[must_use]
pub fn is_gap_tolerant(name: &str) -> bool {
    listed(name, GAP_TOLERANT)
}

/// Which damage cap a file breaks.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum Exceeded {
    /// The first segment, which holds the container's header.
    First,
    /// This many damaged segments in a row.
    Run(usize),
    Count,
    Bytes,
}

/// Whether damage at the sorted, distinct segment indices `damaged` stays playable in a
/// gap-tolerant file whose segments have `sizes`.
///
/// # Errors
/// The first cap the damage breaks.
pub fn within_caps(damaged: &[usize], sizes: &[u64]) -> Result<(), Exceeded> {
    if damaged.first() == Some(&0) {
        return Err(Exceeded::First);
    }
    let run = longest_run(damaged);
    if run > MAX_RUN {
        return Err(Exceeded::Run(run));
    }
    if damaged.len() > MAX_DAMAGED {
        return Err(Exceeded::Count);
    }
    let lost: u64 = damaged.iter().filter_map(|&i| sizes.get(i)).sum();
    if lost * MAX_BYTES_DIVISOR > sizes.iter().sum() {
        return Err(Exceeded::Bytes);
    }
    Ok(())
}

fn longest_run(sorted: &[usize]) -> usize {
    let (mut best, mut run) = (0, 0);
    for (k, &i) in sorted.iter().enumerate() {
        run = if k > 0 && sorted[k - 1] + 1 == i {
            run + 1
        } else {
            1
        };
        best = best.max(run);
    }
    best
}

/// A health sample of `n` segments: `percent` %, at least 5, first 3 and last 2.
#[must_use]
pub fn sample(n: usize, percent: u8) -> Vec<usize> {
    spread(n, 3, 2, (n * usize::from(percent) / 100).max(5))
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn samples_five_percent_with_edges() {
        assert_eq!(sample(4, 5), [0, 1, 2, 3]);
        assert_eq!(sample(50, 5), [0, 1, 2, 48, 49]);
        assert_eq!(sample(50, 100).len(), 50);
        assert_eq!(sample(1_000, 25).len(), 250);
        let s = sample(1_000, 5);
        assert_eq!(
            (s.len(), &s[..3], &s[48..]),
            (50, &[0, 1, 2][..], &[998, 999][..])
        );
        assert!(s.windows(2).all(|w| w[0] < w[1]));
    }

    #[test]
    fn checks_media_only() {
        for name in [
            "a.mkv",
            "b.FLAC",
            "c.rar",
            "c.r07",
            "d.7z.001",
            "e.mkv.002",
            "f.epub",
            "g.AZW3",
        ] {
            assert!(is_checkable(name), "{name}");
        }
        for name in ["a.srt", "b.nfo", "c.jpg", "readme", "x.r0a"] {
            assert!(!is_checkable(name), "{name}");
        }
    }

    #[test]
    fn audio_categories_take_mp4_audiobooks() {
        for name in [
            "a.mp4",
            "b.m4b",
            "c.oga",
            "d.MP2",
            "e.wavpack",
            "f.wv",
            "g.aif",
            "h.AIFF",
            "i.aifc",
        ] {
            assert!(fits(name, Kind::Audio), "{name}");
        }
        assert!(fits("a.mp4", Kind::Video));
        assert!(!fits("a.mkv", Kind::Audio));
        assert!(!fits("a.mp3", Kind::Book));
    }

    #[test]
    fn caps_damage_at_equality() {
        let sizes = vec![10; 1_000];
        assert_eq!(within_caps(&[0], &sizes), Err(Exceeded::First));
        assert_eq!(within_caps(&[5, 6, 7, 8], &sizes), Ok(()));
        assert_eq!(within_caps(&[5, 6, 7, 8, 9], &sizes), Err(Exceeded::Run(5)));
        let spread: Vec<usize> = (1..=65).map(|i| i * 10).collect();
        assert_eq!(within_caps(&spread[..20], &sizes), Ok(()));
        assert_eq!(within_caps(&spread, &sizes), Err(Exceeded::Count));
        assert_eq!(
            within_caps(&[1, 3], &[10, 1, 10, 1, 10]),
            Err(Exceeded::Bytes)
        );
    }

    #[test]
    fn analyzes_logical_media_only() {
        for name in ["a.mkv", "b.FLAC", "c.mp4"] {
            assert!(is_analyzable(name), "{name}");
        }
        for name in ["a.iso", "b.rar", "c.r07", "d.mkv.001", "e.srt"] {
            assert!(!is_analyzable(name), "{name}");
        }
    }
}
