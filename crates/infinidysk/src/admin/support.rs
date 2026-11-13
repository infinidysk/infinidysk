//! Support packs: a ZIP of recent logs, settings and status to attach to a bug report,
//! with secrets removed.

use std::collections::{BTreeMap, HashMap};
use std::io::{Cursor, Write as _};
use std::net::{IpAddr, Ipv4Addr, Ipv6Addr};
use std::sync::{Arc, LazyLock};
use std::time::{Duration, SystemTime, UNIX_EPOCH};

use axum::Json;
use axum::extract::{Query, State};
use axum::http::header;
use axum::response::{IntoResponse, Response};
use regex::{Captures, Regex};
use serde::{Deserialize, Serialize};
use serde_json::{Value, json};
use utoipa::ToSchema;

use super::{Admin, Fail, Page, PerformanceQuery};
use crate::config::{MASK, Settings};
use crate::logs::{self, Level};
use crate::update::{COMMIT, VERSION};
use crate::{metrics, quota};

/// Bumped when entries change meaning, so tooling can tell packs apart. 2: trace headers
/// carry `clock`, `limits` and `coverage`; request events; `status/resources.json`;
/// `traces/fuse-ops.json`; `generated_unix_ms`.
const PACK_VERSION: u32 = 2;
const MAX_NOTE_CHARS: usize = 10_000;
const HISTORY: usize = 50;
const WARNINGS: usize = 1000;
const REDACTED: &str = "[REDACTED]";
/// A section that takes longer is left out, so one hung part cannot hold up the pack.
const SECTION_TIMEOUT: Duration = Duration::from_secs(10);

const README: &str = "\
infinidysk support pack

Attach this file to a bug report. It holds recent logs, settings and status; the server
keeps no copy of it.

  NOTE.txt               What the user wrote when generating the pack (when given).
  manifest.json          Pack version, what was collected, sections that failed, and how
                         many values were redacted. generated_unix_ms is the wall clock
                         when the pack was started.
  system.json            Version, OS, architecture, database type, mount state and
                         selected environment variables.
  settings.json          Saved settings with passwords, keys and usernames masked, plus
                         the settings fixed by environment variables.
  status/*.json          Live provider and connection stats, performance and cache, file
                         health, the queue, recent history, Arr discovery, provider usage.
  status/live.json       The latest 2 s sample. reads[].pending lists FUSE requests in the
                         daemon with their stage and age when the pack was built.
  status/resources.json  Memory evidence for the last 10 minutes, one sample per 2 s:
                         memory.current and limit, swap (read once a minute, at swap_at),
                         memory.events and memory pressure (PSI).
  logs/recent.jsonl      The last events kept in memory, one JSON object per line.
  logs/warnings.jsonl    The last warnings and errors from the log files on disk.
  import-report.txt      What the upgrade from C# carried over and left behind (on
                         installs imported from C#).
  traces/                Read traces, when included: index.json lists them, one NDJSON
                         file per trace (header line, then one event per line).
                         fuse-ops.json lists FUSE metadata calls (lookup, getattr,
                         readlink, open, opendir and more) of 1 ms or more while
                         tracing was on (inodes, no paths).

Timing terms

  Clock          Trace event times (at_ns) count from the trace start on a monotonic
                 clock. Wall time = header clock.started_unix_ns + at_ns, to line up with
                 player logs. The host and player clocks may differ.
  Open           opened.before_ns: open work before the stream started (path, caller and
                 metadata lookups). The event's at_ns: the rest, to the open reply.
  Request        A FUSE read from its arrival in the daemon to the daemon's reply. Stages:
                 order (waiting for earlier reads of the handle), handle_lock (waiting
                 for the handle's reader), read (in the stream, including segment waits).
                 A request ends with data, error, or cancelled (interrupted).
  first_request_ns  Trace start to the first read request reaching the daemon.
  first_reply_ns    That first request's time in the daemon.
  first_byte_ns  Stream fetch latency: the reader's first prefetch window to its first
                 bytes. Not the daemon's request time, not click-to-play.
  first_byte_ms  (reads) Stream wait of the first read that returned bytes. Excludes
                 ordering and lock waits and everything before that read.
  no_request_ns  After the first request, time with no read request in the daemon. The
                 kernel, its page cache or the player had it; the pack cannot tell which.
  client_ns      Time between stream reads (Idle events). Includes ordering and lock
                 waits; not proof the player paused.

What a pack cannot show: time a request spent in the kernel before the daemon got it,
reads the kernel answered from its page cache, what the player did, and which thread a
memory stall hit. Pressure (PSI) shows that tasks in the cgroup stalled on memory, not
which ones; swap in use alone does not show a stall.

Privacy: passwords, API keys, provider usernames, credentials in URLs and public IP
addresses are removed. File paths, release names, host names and private network
addresses stay, because they are needed to diagnose problems. Review the pack and share
it only with people you trust.
";

#[derive(Deserialize, ToSchema)]
pub(super) struct SupportPackRequest {
    /// What happened and when; at most 10000 characters.
    note: Option<String>,
    /// Add the read traces the server still holds.
    #[serde(default)]
    include_traces: bool,
}

/// Builds a support pack; it is not kept on the server.
#[utoipa::path(post, path = "/api/v1/support-pack", tag = "status",
    request_body = SupportPackRequest,
    responses((status = 200, content_type = "application/zip", body = String),
        (status = 400, body = super::ApiError)))]
pub(super) async fn post_support_pack(
    State(admin): State<Arc<Admin>>,
    Json(request): Json<SupportPackRequest>,
) -> Result<Response, Fail> {
    let note = request
        .note
        .map(|n| n.trim().to_owned())
        .filter(|n| !n.is_empty());
    if note
        .as_ref()
        .is_some_and(|n| n.chars().count() > MAX_NOTE_CHARS)
    {
        return Err(Fail::Bad(format!(
            "note must be at most {MAX_NOTE_CHARS} characters"
        )));
    }
    let now = SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .unwrap_or_default();
    let stamp = Stamp::of(now.as_secs(), now.as_millis());
    let pack = build(&admin, note, request.include_traces, stamp).await?;
    tracing::info!(
        bytes = pack.len(),
        traces = request.include_traces,
        "built a support pack"
    );
    let file = format!("attachment; filename=\"infinidysk-support-{VERSION}-{stamp}.zip\"");
    Ok((
        [
            (header::CONTENT_TYPE, "application/zip".to_owned()),
            (header::CONTENT_DISPOSITION, file),
            (header::CACHE_CONTROL, "no-store".to_owned()),
        ],
        pack,
    )
        .into_response())
}

/// A UTC time of day, for the file name and ZIP entry times.
#[derive(Clone, Copy)]
struct Stamp {
    year: i64,
    month: i64,
    day: i64,
    secs: u64,
    unix_ms: u128,
}

impl Stamp {
    fn of(unix_secs: u64, unix_ms: u128) -> Self {
        let days = i64::try_from(unix_secs / 86_400).unwrap_or(0);
        let (year, month, day) = quota::civil(days);
        Self {
            year,
            month,
            day,
            secs: unix_secs % 86_400,
            unix_ms,
        }
    }

    fn zip_time(self) -> zip::DateTime {
        let n = |v: i64| u8::try_from(v).unwrap_or(1);
        let t = |v: u64| u8::try_from(v).unwrap_or(0);
        zip::DateTime::from_date_and_time(
            u16::try_from(self.year).unwrap_or(1980),
            n(self.month),
            n(self.day),
            t(self.secs / 3600),
            t(self.secs / 60 % 60),
            t(self.secs % 60),
        )
        .unwrap_or_default()
    }

    fn rfc3339(self) -> String {
        format!(
            "{:04}-{:02}-{:02}T{:02}:{:02}:{:02}Z",
            self.year,
            self.month,
            self.day,
            self.secs / 3600,
            self.secs / 60 % 60,
            self.secs % 60
        )
    }
}

impl std::fmt::Display for Stamp {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        write!(
            f,
            "{:04}{:02}{:02}-{:02}{:02}{:02}Z",
            self.year,
            self.month,
            self.day,
            self.secs / 3600,
            self.secs / 60 % 60,
            self.secs % 60
        )
    }
}

/// Entries by name, and why any section is missing.
#[derive(Default)]
struct Pack {
    entries: Vec<(String, String)>,
    failed: BTreeMap<String, String>,
}

impl Pack {
    fn add(&mut self, name: &str, body: String) {
        self.entries.push((name.to_owned(), body));
    }

    fn json(&mut self, name: &str, value: Result<impl Serialize, Fail>) {
        match value.map(|v| serde_json::to_string_pretty(&v)) {
            Ok(Ok(body)) => self.add(name, body),
            Ok(Err(e)) => {
                self.failed.insert(name.to_owned(), e.to_string());
            }
            Err(_) => {
                self.failed
                    .insert(name.to_owned(), "could not be collected".to_owned());
            }
        }
    }

    /// `value` within [`SECTION_TIMEOUT`], else records `name` as timed out.
    async fn timed<T>(&mut self, name: &str, value: impl Future<Output = T>) -> Option<T> {
        let value = tokio::time::timeout(SECTION_TIMEOUT, value).await.ok();
        if value.is_none() {
            self.failed.insert(
                name.to_owned(),
                format!("timed out after {} s", SECTION_TIMEOUT.as_secs()),
            );
        }
        value
    }

    async fn section<T: Serialize>(
        &mut self,
        name: &str,
        value: impl Future<Output = Result<T, Fail>>,
    ) {
        if let Some(value) = self.timed(name, value).await {
            self.json(name, value);
        }
    }
}

// ponytail: the pack is built in memory; retained traces set the ceiling. Stream it with
// `ZipWriter::new_stream` if packs grow too large.
async fn build(
    admin: &Arc<Admin>,
    note: Option<String>,
    include_traces: bool,
    stamp: Stamp,
) -> Result<Vec<u8>, Fail> {
    let state = || State(admin.clone());
    let mut pack = Pack::default();
    pack.add("README.txt", README.to_owned());
    if let Some(note) = note {
        pack.add("NOTE.txt", note + "\n");
    }

    let (redactor, settings) = {
        let store = admin.config.read().await;
        let mut settings = store.settings().masked();
        for p in &mut settings.providers {
            mask(&mut p.username);
        }
        mask(&mut settings.webdav.user);
        let locked: Vec<String> = store.locked().collect();
        (
            Redactor::new(store.settings()),
            json!({ "settings": settings, "locked": locked }),
        )
    };
    pack.json("settings.json", Ok(settings));
    pack.json("system.json", Ok(system(admin)));
    pack.section("status/live.json", async {
        Ok(metrics::get_live(state()).await.0)
    })
    .await;
    pack.json(
        "status/resources.json",
        Ok(admin.metrics.resource_history()),
    );
    pack.section("status/performance.json", async {
        let query = Query(PerformanceQuery { dir: None });
        Ok(super::get_performance(state(), query).await.0)
    })
    .await;
    pack.section("status/health.json", async {
        super::get_health_summary(state()).await.map(|j| j.0)
    })
    .await;
    pack.section("status/queue.json", async {
        super::get_queue(state()).await.map(|j| j.0)
    })
    .await;
    let page = Page {
        limit: Some(HISTORY),
        offset: None,
    };
    pack.section("status/history.json", async {
        super::get_history(state(), Query(page), Query(super::HistoryFilter::default()))
            .await
            .map(|j| j.0)
    })
    .await;
    pack.section("status/arr.json", async {
        Ok(super::get_arr_status(state()).await.0)
    })
    .await;
    pack.section("status/provider-usage.json", async {
        Ok(quota::get_provider_usage(state()).await.0)
    })
    .await;
    let report = crate::upgrade::get_upgrade_report(state());
    match pack.timed("import-report.txt", report).await {
        Some(Ok(j)) => {
            if let Some(report) = j.0.report {
                pack.add("import-report.txt", report);
            }
        }
        Some(Err(_)) => {
            pack.failed.insert(
                "import-report.txt".to_owned(),
                "could not be collected".to_owned(),
            );
        }
        None => {}
    }
    pack.add(
        "logs/recent.jsonl",
        lines(&logs::LOGS.snapshot(Level::Trace, None)),
    );
    let warnings = tokio::task::spawn_blocking(|| logs::warnings(WARNINGS));
    match pack.timed("logs/warnings.jsonl", warnings).await {
        Some(Ok(warnings)) => pack.add("logs/warnings.jsonl", lines(&warnings)),
        Some(Err(e)) => {
            pack.failed
                .insert("logs/warnings.jsonl".to_owned(), e.to_string());
        }
        None => {}
    }
    if include_traces {
        pack.section("traces/index.json", async {
            Ok(super::get_traces(state()).await.0)
        })
        .await;
        pack.json("traces/fuse-ops.json", Ok(fuse_ops(admin)));
        for session in admin.vfs.sessions().traced() {
            let name = format!("traces/{}.ndjson", session.id);
            match pack
                .timed(&name, super::trace_body(admin, session.id, false))
                .await
            {
                Some(Ok(body)) => pack.add(&name, body),
                // Dropped between listing and reading.
                Some(Err(_)) => {
                    pack.failed.insert(name, "no longer kept".to_owned());
                }
                None => {}
            }
        }
    }

    tokio::task::spawn_blocking(move || write(pack, redactor, include_traces, stamp))
        .await
        .map_err(|e| {
            tracing::warn!(reason = %e, "building a support pack failed");
            Fail::Internal
        })?
        .map_err(|e| {
            tracing::warn!(reason = %e, "writing a support pack failed");
            Fail::Internal
        })
}

fn mask(value: &mut String) {
    if !value.is_empty() {
        MASK.clone_into(value);
    }
}

/// Slow and unfinished FUSE metadata calls noted while tracing, by inode; paths stay out.
fn fuse_ops(admin: &Admin) -> Value {
    let sessions = admin.vfs.sessions();
    let ops = sessions.fuse_ops();
    let listed: Vec<Value> = ops
        .listed
        .iter()
        .map(|op| {
            json!({
                "op": op.op,
                "request": op.request,
                "inode": op.inode,
                "child_inode": op.child,
                "arrived_unix_ns": infinidysk_core::trace::unix_ns(op.arrived),
                "took_us": u64::try_from(op.took.as_micros()).unwrap_or(u64::MAX),
                "errno": op.errno,
            })
        })
        .collect();
    let pending: Vec<Value> = sessions
        .pending_ops()
        .iter()
        .map(|op| {
            json!({
                "op": op.op,
                "request": op.request,
                "inode": op.inode,
                "arrived_unix_ns": infinidysk_core::trace::unix_ns(op.arrived),
                "age_us": u64::try_from(op.age.as_micros()).unwrap_or(u64::MAX),
            })
        })
        .collect();
    json!({
        "note": "Only while tracing was on, since the server started. Calls under \
                 min_took_us are counted in fast, not listed; the oldest listed calls \
                 go first (dropped). Times are the daemon's: from the call's arrival to \
                 its reply. open's inode matches the trace's opened event; lookup's \
                 inode is the parent directory and its child_inode is the one an open \
                 names. pending lists lookup, getattr, readlink, open and opendir calls \
                 with no reply at export, oldest first, with their age at export.",
        "min_took_us": u64::try_from(infinidysk_vfs::FuseOps::MIN.as_micros()).unwrap_or(u64::MAX),
        "max_listed": infinidysk_vfs::FuseOps::KEPT,
        "seen": ops.seen,
        "fast": ops.fast,
        "dropped": ops.dropped,
        "ops": listed,
        "pending": pending,
    })
}

fn lines<T: Serialize>(items: &[T]) -> String {
    items
        .iter()
        .filter_map(|i| serde_json::to_string(i).ok())
        .map(|l| l + "\n")
        .collect()
}

/// Environment variables shown with their values; others under our prefix by name only.
const SHOWN_ENV: [&str; 9] = [
    "INFINIDYSK_LISTEN",
    "INFINIDYSK_LOG_DIR",
    "INFINIDYSK_METRICS_PATH",
    "INFINIDYSK_TRACE_MINUTES",
    "INFINIDYSK_FFPROBE",
    "PUID",
    "PGID",
    "TZ",
    "RUST_LOG",
];

fn system(admin: &Admin) -> Value {
    let mut env = BTreeMap::new();
    let mut set = Vec::new();
    for (key, value) in std::env::vars_os() {
        let Some(key) = key.to_str() else { continue };
        if SHOWN_ENV.contains(&key) {
            env.insert(key.to_owned(), value.to_string_lossy().into_owned());
        } else if key.starts_with("INFINIDYSK") {
            set.push(key.to_owned());
        }
    }
    set.sort_unstable();
    let database = admin.database.backend();
    let mount = admin.mount.borrow().clone();
    let kernel = mount.as_ref().ok().map(|k| {
        json!({
            "read_ahead_kb": k.read_ahead_kb,
            "max_background": k.max_background,
            "congestion_threshold": k.congestion_threshold,
            "waiting": k.waiting(),
        })
    });
    json!({
        "version": VERSION,
        "commit": COMMIT,
        "os": std::env::consts::OS,
        "arch": std::env::consts::ARCH,
        "cpus": std::thread::available_parallelism().map_or(0, std::num::NonZero::get),
        "container": std::path::Path::new("/.dockerenv").exists(),
        "database": database,
        "mount": {
            "dir": admin.live.mount_dir(),
            "enabled": admin.live.fuse_enabled(),
            "mounted": mount.is_ok(),
            "reason": mount.err(),
            "kernel": kernel,
        },
        "env": env,
        "env_set": set,
    })
}

fn write(
    pack: Pack,
    mut redactor: Redactor,
    include_traces: bool,
    stamp: Stamp,
) -> zip::result::ZipResult<Vec<u8>> {
    let options = zip::write::SimpleFileOptions::default()
        .compression_method(zip::CompressionMethod::Deflated)
        .last_modified_time(stamp.zip_time());
    let mut zip = zip::ZipWriter::new(Cursor::new(Vec::new()));
    let mut names = Vec::with_capacity(pack.entries.len());
    for (name, body) in pack.entries {
        zip.start_file(name.as_str(), options)?;
        zip.write_all(redactor.redact(&body).as_bytes())?;
        names.push(name);
    }
    let manifest = json!({
        "pack_version": PACK_VERSION,
        "generated_at": stamp.rfc3339(),
        "generated_unix_ms": stamp.unix_ms,
        "version": VERSION,
        "commit": COMMIT,
        "include_traces": include_traces,
        "entries": names,
        "failed": pack.failed,
        "redacted": {
            "secrets": redactor.secrets_found,
            "credentials": redactor.credentials_found,
            "ip_addresses": redactor.ips.len(),
        },
    });
    zip.start_file("manifest.json", options)?;
    zip.write_all(format!("{manifest:#}").as_bytes())?;
    Ok(zip.finish()?.into_inner())
}

/// URL query values, URL logins and `Authorization` values.
static CREDENTIALS: LazyLock<[(Regex, &str); 3]> = LazyLock::new(|| {
    let re = |p| Regex::new(p).expect("the pattern is a valid literal");
    [
        (
            re(
                r#"(?i)([?&](?:apikey|api_key|access_token|token|pass|password|auth|downloadkey|key)=)[^&\s"'#\[]+"#,
            ),
            "${1}[REDACTED]",
        ),
        (
            re(r#"(?i)\b([a-z][a-z0-9+.-]*://)[^/\s"'@\[]+@"#),
            "${1}[REDACTED]@",
        ),
        (
            re(r"(?i)\b(basic|bearer)\s+[A-Za-z0-9._~+/=-]{8,}"),
            "$1 [REDACTED]",
        ),
    ]
});

/// A dotted quad, with the `Product/` before it when it is a user-agent version.
static IPV4: LazyLock<Regex> = LazyLock::new(|| {
    Regex::new(r"([A-Za-z]+/)?\b(\d{1,3}(?:\.\d{1,3}){3})\b")
        .expect("the pattern is a valid literal")
});

/// A whole token with a colon in it; only tokens that parse as an address are replaced, so
/// `crate::module` paths and times stay.
static IPV6: LazyLock<Regex> =
    LazyLock::new(|| Regex::new(r"[\w.:]*:[\w.:]*").expect("the pattern is a valid literal"));

/// Removes secrets from text; public IP addresses get stable per-pack aliases.
struct Redactor {
    /// Longest first, so a secret containing another is replaced whole.
    secrets: Vec<String>,
    ips: HashMap<IpAddr, usize>,
    secrets_found: usize,
    credentials_found: usize,
}

impl Redactor {
    fn new(settings: &Settings) -> Self {
        let mut secrets: Vec<String> = [
            &settings.sab.api_key,
            &settings.auth.api_key,
            &settings.auth.session_key,
            &settings.auth.oidc.client_secret,
            &settings.webdav.password,
            &settings.webdav.download_key,
            &settings.imports.strm_key,
        ]
        .into_iter()
        .chain(settings.providers.iter().map(|p| &p.password))
        .chain(settings.arr_instances.iter().map(|a| &a.api_key))
        .map(|s| s.0.clone())
        .chain(settings.providers.iter().map(|p| p.username.clone()))
        .filter(|s| s.chars().count() >= 4)
        .flat_map(|s| {
            // As it appears inside JSON strings too.
            let escaped = serde_json::to_string(&s).unwrap_or_default();
            let escaped = escaped
                .strip_prefix('"')
                .and_then(|e| e.strip_suffix('"'))
                .unwrap_or_default()
                .to_owned();
            [s, escaped]
        })
        .filter(|s| !s.is_empty())
        .collect();
        secrets.sort_unstable_by(|a, b| b.len().cmp(&a.len()).then_with(|| a.cmp(b)));
        secrets.dedup();
        Self {
            secrets,
            ips: HashMap::new(),
            secrets_found: 0,
            credentials_found: 0,
        }
    }

    fn redact(&mut self, text: &str) -> String {
        let mut text = text.to_owned();
        for secret in &self.secrets {
            let n = text.matches(secret.as_str()).count();
            if n > 0 {
                self.secrets_found += n;
                text = text.replace(secret.as_str(), REDACTED);
            }
        }
        for (re, with) in CREDENTIALS.iter() {
            let n = re.find_iter(&text).count();
            if n > 0 {
                self.credentials_found += n;
                text = re.replace_all(&text, *with).into_owned();
            }
        }
        let text = IPV4.replace_all(&text, |c: &Captures| match c[2].parse::<Ipv4Addr>() {
            Ok(ip) if c.get(1).is_none() && public_v4(ip) => self.alias(ip.into()),
            _ => c[0].to_owned(),
        });
        IPV6.replace_all(&text, |c: &Captures| match c[0].parse::<Ipv6Addr>() {
            Ok(ip) if public_v6(ip) => self.alias(ip.into()),
            _ => c[0].to_owned(),
        })
        .into_owned()
    }

    fn alias(&mut self, ip: IpAddr) -> String {
        let next = self.ips.len() + 1;
        format!("[IP-{}]", self.ips.entry(ip).or_insert(next))
    }
}

const fn public_v4(ip: Ipv4Addr) -> bool {
    !(ip.is_private()
        || ip.is_loopback()
        || ip.is_link_local()
        || ip.is_unspecified()
        || ip.is_broadcast()
        || ip.is_documentation())
}

const fn public_v6(ip: Ipv6Addr) -> bool {
    let first = ip.segments()[0];
    !(ip.is_loopback()
        || ip.is_unspecified()
        || first & 0xfe00 == 0xfc00
        || first & 0xffc0 == 0xfe80)
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::config::{Provider, Secret};

    #[test]
    fn redacts_secrets_credentials_and_public_addresses() {
        let mut settings = Settings::default();
        settings.providers.push(Provider {
            username: "provider-login".to_owned(),
            password: Secret("pa\"ss-word".to_owned()),
            ..Provider::default()
        });
        let mut r = Redactor::new(&settings);
        let out = r.redact(
            r#"login provider-login pw {"p":"pa\"ss-word"} url http://u:p@h/x?apikey=abc123&a=1 Bearer abcdefghijkl from 8.8.8.8 and 8.8.8.8, 192.168.1.2, 2001:4860::8888, ::1 ua Chrome/120.0.0.0 at infinidysk::admin 12:03:04"#,
        );
        assert_eq!(
            out,
            r#"login [REDACTED] pw {"p":"[REDACTED]"} url http://[REDACTED]@h/x?apikey=[REDACTED]&a=1 Bearer [REDACTED] from [IP-1] and [IP-1], 192.168.1.2, [IP-2], ::1 ua Chrome/120.0.0.0 at infinidysk::admin 12:03:04"#
        );
        assert_eq!((r.secrets_found, r.credentials_found), (2, 3));
    }
}
