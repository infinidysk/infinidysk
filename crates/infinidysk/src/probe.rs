//! Media analysis with ffprobe (`infinidysk-lab:docs/health-policy.md` §5.7): one file is
//! served to a local ffprobe over loopback HTTP from verified bytes, and a bounded summary of what it
//! found is kept.

use std::future::IntoFuture as _;
use std::io;
use std::pin::Pin;
use std::process::{ExitStatus, Stdio};
use std::sync::Arc;
use std::sync::atomic::{AtomicBool, AtomicU32, AtomicU64, Ordering};
use std::task::{Context, Poll};
use std::time::Duration;

use axum::body::Body;
use axum::extract::State;
use axum::http::{HeaderMap, HeaderValue, Method, StatusCode, header};
use axum::response::{IntoResponse as _, Response};
use infinidysk_db::Item;
use infinidysk_health::{Analysis, Analyzer};
use infinidysk_vfs::Vfs;
use serde::{Deserialize, Serialize};
use tokio::io::{AsyncRead, AsyncReadExt as _, AsyncSeekExt as _, ReadBuf};
use tokio::net::TcpListener;
use tokio::process::Command;
use tokio_util::io::ReaderStream;
use utoipa::ToSchema;
use uuid::Uuid;

/// Schema version of [`MediaInfo`]; bump on any change to its fields.
const VERSION: u32 = 1;
const TIMEOUT: Duration = Duration::from_secs(60);
const PREEMPT_POLL: Duration = Duration::from_millis(250);
const MAX_REQUESTS: u32 = 32;
const MAX_BYTES: u64 = 32_000_000;
const MAX_STDOUT: usize = 1_000_000;
const MAX_STDERR: usize = 64_000;
const MAX_STREAMS: usize = 64;
const MAX_TEXT: usize = 128;
const CHUNK: usize = 64 * 1024;
/// Demuxers for the extensions `media::is_analyzable` accepts.
const FORMATS: &str =
    "matroska,webm,mov,avi,mpegts,asf,mpeg,flv,ogg,flac,mp3,aac,wav,ape,dsf,iff,wv,aiff";
const ENTRIES: &str = "format=format_name,duration,bit_rate:\
    stream=codec_type,codec_name,profile,width,height,avg_frame_rate,bit_rate,channels,\
    channel_layout,sample_rate,color_transfer:\
    stream_tags=language,title:stream_disposition=default,forced";

/// What ffprobe found in a file, as stored and served.
#[derive(Clone, Debug, PartialEq, Serialize, Deserialize, ToSchema)]
pub struct MediaInfo {
    /// Schema version of this record.
    pub v: u32,
    pub container: String,
    pub duration_ms: Option<u64>,
    /// Bytes per second over the whole file.
    pub bps: Option<u64>,
    pub streams: Vec<MediaStream>,
}

#[derive(Clone, Debug, PartialEq, Serialize, Deserialize, ToSchema)]
pub struct MediaStream {
    /// `video`, `audio`, `subtitle`, or another ffprobe codec type.
    pub kind: String,
    pub codec: String,
    pub profile: Option<String>,
    pub width: Option<u32>,
    pub height: Option<u32>,
    pub frame_rate: Option<f64>,
    /// `pq` (HDR10, Dolby Vision) or `hlg` when the video is HDR.
    pub hdr: Option<String>,
    pub channels: Option<u32>,
    pub channel_layout: Option<String>,
    pub sample_rate: Option<u32>,
    /// Bytes per second.
    pub bps: Option<u64>,
    pub language: Option<String>,
    pub title: Option<String>,
    pub default: bool,
    pub forced: bool,
}

impl MediaInfo {
    /// Stored metadata of a version this build reads.
    pub fn parse(json: &str) -> Option<Self> {
        serde_json::from_str::<Self>(json)
            .ok()
            .filter(|i| i.v == VERSION)
    }
}

/// Whether `ffprobe` runs; logs one warning when it does not.
pub async fn available(ffprobe: &str) -> bool {
    let run = Command::new(ffprobe)
        .arg("-version")
        .env_clear()
        .stdin(Stdio::null())
        .stdout(Stdio::null())
        .stderr(Stdio::null())
        .kill_on_drop(true)
        .status();
    let reason = match tokio::time::timeout(Duration::from_secs(10), run).await {
        Ok(Ok(status)) if status.success() => return true,
        Ok(Ok(status)) => format!("ffprobe -version exited with {status}"),
        Ok(Err(error)) => error.to_string(),
        Err(_) => "ffprobe -version timed out".to_owned(),
    };
    tracing::warn!(ffprobe, reason = %reason, "ffprobe unavailable; media analysis is off");
    false
}

/// Analyzes files with `ffprobe`, giving way whenever `busy` turns true.
pub fn analyzer(
    vfs: Arc<Vfs>,
    ffprobe: String,
    busy: impl Fn() -> bool + Send + Sync + 'static,
) -> Analyzer {
    let (ffprobe, busy) = (Arc::<str>::from(ffprobe), Arc::new(busy));
    Arc::new(move |item| {
        let (vfs, ffprobe, busy) = (Arc::clone(&vfs), Arc::clone(&ffprobe), Arc::clone(&busy));
        Box::pin(async move { analyze(vfs, &ffprobe, &*busy, item).await })
    })
}

/// Requests and bytes served to one ffprobe run.
#[derive(Default)]
struct Served {
    requests: AtomicU32,
    bytes: AtomicU64,
    limited: AtomicBool,
    fault: AtomicBool,
}

struct File {
    vfs: Arc<Vfs>,
    item: Item,
    size: u64,
    served: Arc<Served>,
}

enum End {
    Exited(Vec<u8>, Vec<u8>, ExitStatus),
    Failed(io::Error),
    TimedOut,
    Preempted,
}

#[allow(clippy::significant_drop_tightening)] // False positive: `file` moves into the router.
async fn analyze(
    vfs: Arc<Vfs>,
    ffprobe: &str,
    busy: &(dyn Fn() -> bool + Send + Sync),
    item: Item,
) -> Analysis {
    let Some(size) = item.size else {
        return Analysis::Inconclusive("the file size is unknown".to_owned());
    };
    let listener = match TcpListener::bind(("127.0.0.1", 0)).await {
        Ok(listener) => listener,
        Err(error) => return Analysis::Inconclusive(format!("loopback listen failed: {error}")),
    };
    let port = match listener.local_addr() {
        Ok(addr) => addr.port(),
        Err(error) => return Analysis::Inconclusive(format!("loopback listen failed: {error}")),
    };
    // The extension hints the demuxer; the token keeps other local processes out.
    let ext: String = item
        .name
        .rsplit_once('.')
        .map_or("", |(_, e)| e)
        .chars()
        .filter(char::is_ascii_alphanumeric)
        .take(8)
        .collect();
    let route = format!("/{}/media.{ext}", Uuid::new_v4().simple());
    let url = format!("http://127.0.0.1:{port}{route}");
    let served = Arc::new(Served::default());
    let file = Arc::new(File {
        vfs,
        item,
        size,
        served: Arc::clone(&served),
    });
    let app = axum::Router::new()
        .route(&route, axum::routing::get(serve).head(serve))
        .with_state(file);
    let server = tokio::spawn(axum::serve(listener, app).into_future());

    let end = run(ffprobe, &url, busy).await;
    server.abort();
    match end {
        End::Preempted => Analysis::Preempted,
        End::TimedOut => Analysis::Inconclusive("ffprobe timed out".to_owned()),
        End::Failed(error) => Analysis::Inconclusive(format!("ffprobe did not run: {error}")),
        _ if served.limited.load(Ordering::Relaxed) => {
            Analysis::Inconclusive("ffprobe read more than analysis allows".to_owned())
        }
        _ if served.fault.load(Ordering::Relaxed) => {
            Analysis::Inconclusive("a segment could not be read".to_owned())
        }
        End::Exited(stdout, stderr, status) => classify(&stdout, &stderr, status),
    }
}

async fn run(ffprobe: &str, url: &str, busy: &(dyn Fn() -> bool + Send + Sync)) -> End {
    let child = Command::new(ffprobe)
        .env_clear()
        .args(["-v", "error", "-hide_banner"])
        .args([
            "-protocol_whitelist",
            "http,tcp",
            "-format_whitelist",
            FORMATS,
        ])
        .args(["-probesize", "5000000", "-analyzeduration", "5000000"])
        .args(["-of", "json", "-show_entries", ENTRIES, url])
        .stdin(Stdio::null())
        .stdout(Stdio::piped())
        .stderr(Stdio::piped())
        .kill_on_drop(true)
        .spawn();
    let mut child = match child {
        Ok(child) => child,
        Err(error) => return End::Failed(error),
    };
    let (stdout, stderr) = (child.stdout.take(), child.stderr.take());
    let end = tokio::select! {
        (stdout, stderr, status) = async {
            tokio::join!(capped(stdout, MAX_STDOUT), capped(stderr, MAX_STDERR), child.wait())
        } => match status {
            Ok(status) => End::Exited(stdout, stderr, status),
            Err(error) => End::Failed(error),
        },
        () = tokio::time::sleep(TIMEOUT) => End::TimedOut,
        () = async {
            while !busy() {
                tokio::time::sleep(PREEMPT_POLL).await;
            }
        } => End::Preempted,
    };
    // Already exited is fine; otherwise this kills and reaps it.
    let _ = child.kill().await;
    end
}

/// Up to `cap + 1` bytes of `pipe`, so overflow shows.
async fn capped(pipe: Option<impl AsyncRead + Unpin>, cap: usize) -> Vec<u8> {
    let mut out = Vec::new();
    if let Some(pipe) = pipe {
        let _ = pipe.take(cap as u64 + 1).read_to_end(&mut out).await;
    }
    out
}

fn classify(stdout: &[u8], stderr: &[u8], status: ExitStatus) -> Analysis {
    if stdout.len() > MAX_STDOUT {
        return Analysis::Inconclusive("ffprobe output is too large".to_owned());
    }
    let error = String::from_utf8_lossy(&stderr[..stderr.len().min(MAX_STDERR)]);
    let reason = error
        .lines()
        .rev()
        .find(|l| !l.trim().is_empty())
        .map(|l| text(l.rsplit_once(": ").map_or(l, |(_, r)| r)).unwrap_or_default())
        .unwrap_or_default();
    if !status.success() {
        return if error.contains("not on whitelist") {
            Analysis::Unsupported("the container is not one analysis reads".to_owned())
        } else if error.contains("Invalid data found") {
            Analysis::Unsupported("ffprobe does not recognize the container".to_owned())
        } else {
            Analysis::Inconclusive(format!("ffprobe failed: {reason}"))
        };
    }
    match parse(stdout) {
        Some(info)
            if !info
                .streams
                .iter()
                .any(|s| s.kind == "video" || s.kind == "audio") =>
        {
            Analysis::NoStreams
        }
        Some(info) => serde_json::to_string(&info).map_or_else(
            |_| Analysis::Inconclusive("metadata could not be stored".to_owned()),
            Analysis::Discovered,
        ),
        None => Analysis::Inconclusive("ffprobe output is unreadable".to_owned()),
    }
}

#[derive(Deserialize)]
struct Raw {
    format: Option<RawFormat>,
    #[serde(default)]
    streams: Vec<RawStream>,
}

#[derive(Deserialize)]
struct RawFormat {
    format_name: Option<String>,
    duration: Option<String>,
    bit_rate: Option<String>,
}

#[derive(Deserialize)]
struct RawStream {
    codec_type: Option<String>,
    codec_name: Option<String>,
    profile: Option<String>,
    width: Option<u32>,
    height: Option<u32>,
    avg_frame_rate: Option<String>,
    bit_rate: Option<String>,
    channels: Option<u32>,
    channel_layout: Option<String>,
    sample_rate: Option<String>,
    color_transfer: Option<String>,
    #[serde(default)]
    tags: Tags,
    #[serde(default)]
    disposition: Disposition,
}

#[derive(Default, Deserialize)]
struct Tags {
    language: Option<String>,
    title: Option<String>,
}

#[derive(Default, Deserialize)]
struct Disposition {
    #[serde(default)]
    default: u8,
    #[serde(default)]
    forced: u8,
}

/// Bounded [`MediaInfo`] from ffprobe's JSON.
fn parse(stdout: &[u8]) -> Option<MediaInfo> {
    let raw: Raw = serde_json::from_slice(stdout).ok()?;
    let format = raw.format?;
    let bps = |bits: Option<&String>| bits.and_then(|b| b.parse::<u64>().ok()).map(|b| b / 8);
    let streams = raw
        .streams
        .into_iter()
        .take(MAX_STREAMS)
        .map(|s| MediaStream {
            kind: s
                .codec_type
                .as_deref()
                .and_then(text)
                .unwrap_or_else(|| "unknown".to_owned()),
            codec: s
                .codec_name
                .as_deref()
                .and_then(text)
                .unwrap_or_else(|| "unknown".to_owned()),
            profile: s.profile.as_deref().and_then(text),
            width: s.width,
            height: s.height,
            frame_rate: s.avg_frame_rate.as_deref().and_then(rate),
            hdr: match s.color_transfer.as_deref() {
                Some("smpte2084") => Some("pq".to_owned()),
                Some("arib-std-b67") => Some("hlg".to_owned()),
                _ => None,
            },
            channels: s.channels,
            channel_layout: s.channel_layout.as_deref().and_then(text),
            sample_rate: s.sample_rate.and_then(|r| r.parse().ok()),
            bps: bps(s.bit_rate.as_ref()),
            language: s
                .tags
                .language
                .as_deref()
                .filter(|l| *l != "und")
                .and_then(text),
            title: s.tags.title.as_deref().and_then(text),
            default: s.disposition.default != 0,
            forced: s.disposition.forced != 0,
        })
        .collect();
    Some(MediaInfo {
        v: VERSION,
        container: format
            .format_name
            .as_deref()
            .and_then(text)
            .unwrap_or_else(|| "unknown".to_owned()),
        duration_ms: format
            .duration
            .and_then(|d| d.parse::<f64>().ok())
            .filter(|d| d.is_finite() && *d >= 0.0)
            .map(|d| {
                #[allow(
                    clippy::cast_possible_truncation,
                    clippy::cast_sign_loss,
                    reason = "finite, non-negative, and saturating"
                )]
                let ms = (d * 1000.0).round() as u64;
                ms
            }),
        bps: bps(format.bit_rate.as_ref()),
        streams,
    })
}

/// `num/den` frames per second; `None` for `0/0`.
fn rate(r: &str) -> Option<f64> {
    let (num, den) = r.split_once('/')?;
    let (num, den) = (num.parse::<f64>().ok()?, den.parse::<f64>().ok()?);
    (num > 0.0 && den > 0.0).then(|| num / den)
}

/// Printable, trimmed and at most [`MAX_TEXT`] characters; `None` when empty.
fn text(s: &str) -> Option<String> {
    let s: String = s
        .chars()
        .filter(|c| !c.is_control())
        .take(MAX_TEXT)
        .collect();
    let s = s.trim();
    (!s.is_empty()).then(|| s.to_owned())
}

/// Serves the file's bytes, one range per request, within the run's limits.
#[allow(clippy::significant_drop_tightening)] // False positive: the reader moves into the body.
async fn serve(State(file): State<Arc<File>>, method: Method, headers: HeaderMap) -> Response {
    let served = &file.served;
    if served.requests.fetch_add(1, Ordering::Relaxed) >= MAX_REQUESTS {
        served.limited.store(true, Ordering::Relaxed);
        return StatusCode::TOO_MANY_REQUESTS.into_response();
    }
    let size = file.size;
    let mut out = HeaderMap::new();
    out.insert(header::ACCEPT_RANGES, HeaderValue::from_static("bytes"));
    let (status, start, end) = match infinidysk_webdav::range(&headers, size) {
        Ok(Some((start, end))) => {
            if let Ok(value) = HeaderValue::try_from(format!("bytes {start}-{}/{size}", end - 1)) {
                out.insert(header::CONTENT_RANGE, value);
            }
            (StatusCode::PARTIAL_CONTENT, start, end)
        }
        Ok(None) if !headers.contains_key(header::RANGE) => (StatusCode::OK, 0, size),
        // A range this server does not serve one-to-one.
        Ok(None) => return StatusCode::BAD_REQUEST.into_response(),
        Err(()) => {
            if let Ok(value) = HeaderValue::try_from(format!("bytes */{size}")) {
                out.insert(header::CONTENT_RANGE, value);
            }
            return (StatusCode::RANGE_NOT_SATISFIABLE, out).into_response();
        }
    };
    out.insert(header::CONTENT_LENGTH, HeaderValue::from(end - start));
    if method == Method::HEAD {
        return (status, out).into_response();
    }
    let mut reader = match file.vfs.open_analysis(&file.item).await {
        Ok(reader) => reader,
        Err(error) => {
            tracing::debug!(reason = %error, "analysis read could not open");
            served.fault.store(true, Ordering::Relaxed);
            return StatusCode::INTERNAL_SERVER_ERROR.into_response();
        }
    };
    reader.set_end(end);
    if let Err(error) = reader.seek(io::SeekFrom::Start(start)).await {
        tracing::debug!(reason = %error, "analysis read could not seek");
        served.fault.store(true, Ordering::Relaxed);
        return StatusCode::INTERNAL_SERVER_ERROR.into_response();
    }
    let metered = Metered {
        inner: reader.take(end - start),
        served: Arc::clone(&file.served),
    };
    let body = Body::from_stream(ReaderStream::with_capacity(metered, CHUNK));
    (status, out, body).into_response()
}

/// Counts bytes against [`MAX_BYTES`] and flags read faults.
struct Metered<R> {
    inner: R,
    served: Arc<Served>,
}

impl<R: AsyncRead + Unpin> AsyncRead for Metered<R> {
    fn poll_read(
        mut self: Pin<&mut Self>,
        cx: &mut Context<'_>,
        buf: &mut ReadBuf<'_>,
    ) -> Poll<io::Result<()>> {
        let before = buf.filled().len();
        let result = std::task::ready!(Pin::new(&mut self.inner).poll_read(cx, buf));
        if let Err(error) = result {
            self.served.fault.store(true, Ordering::Relaxed);
            return Poll::Ready(Err(error));
        }
        let n = (buf.filled().len() - before) as u64;
        if self.served.bytes.fetch_add(n, Ordering::Relaxed) + n > MAX_BYTES {
            self.served.limited.store(true, Ordering::Relaxed);
            return Poll::Ready(Err(io::Error::other("analysis read limit reached")));
        }
        Poll::Ready(Ok(()))
    }
}

#[cfg(test)]
mod tests {
    use std::os::unix::process::ExitStatusExt as _;

    use super::*;

    #[test]
    fn keeps_a_bounded_summary_of_ffprobe_output() {
        let json = br#"{"streams": [
            {"codec_name": "hevc", "profile": "Main 10", "codec_type": "video", "width": 3840,
             "height": 2160, "avg_frame_rate": "24000/1001", "color_transfer": "smpte2084",
             "disposition": {"default": 1, "forced": 0}, "tags": {"language": "und"}},
            {"codec_name": "eac3", "codec_type": "audio", "sample_rate": "48000", "channels": 6,
             "channel_layout": "5.1(side)", "avg_frame_rate": "0/0", "bit_rate": "640000",
             "disposition": {"default": 1, "forced": 0}, "tags": {"language": "eng", "title": "Surround\u0007"}}
        ], "format": {"format_name": "matroska,webm", "duration": "2.5", "bit_rate": "8000000"}}"#;
        let Analysis::Discovered(stored) = classify(json, b"", ExitStatus::from_raw(0)) else {
            panic!("discovered");
        };
        let info = MediaInfo::parse(&stored).unwrap();
        assert_eq!(
            (info.container.as_str(), info.duration_ms, info.bps),
            ("matroska,webm", Some(2500), Some(1_000_000))
        );
        let (video, audio) = (&info.streams[0], &info.streams[1]);
        assert_eq!(
            (video.width, video.hdr.as_deref(), video.language.as_deref()),
            (Some(3840), Some("pq"), None)
        );
        assert!((video.frame_rate.unwrap() - 23.976).abs() < 0.001);
        assert_eq!(
            (audio.channels, audio.bps, audio.title.as_deref()),
            (Some(6), Some(80_000), Some("Surround"))
        );

        let subtitles =
            br#"{"streams": [{"codec_type": "subtitle", "codec_name": "subrip"}], "format": {}}"#;
        assert!(matches!(
            classify(subtitles, b"", ExitStatus::from_raw(0)),
            Analysis::NoStreams
        ));
        let refused = b"[mov] Format not on whitelist 'x'\nhttp://a: Invalid argument\n";
        assert!(matches!(
            classify(b"{}", refused, ExitStatus::from_raw(256)),
            Analysis::Unsupported(_)
        ));
        let failed = b"http://a: Connection refused\n";
        let Analysis::Inconclusive(reason) = classify(b"{}", failed, ExitStatus::from_raw(256))
        else {
            panic!("inconclusive");
        };
        assert_eq!(reason, "ffprobe failed: Connection refused");
    }
}
