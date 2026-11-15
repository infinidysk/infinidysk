//! `WebDAV` over the virtual tree, plus `/view/<path>` downloads keyed by query string.
//!
//! Read-only except `/nzbs/<category>/<file>`, where `PUT` queues an NZB and `DELETE`
//! drops a queued one. Links under `/completed-symlinks` appear as `<name>.rclonelink`
//! files whose body is the link target.

mod body;

use std::fmt::Write as _;
use std::io::SeekFrom;
use std::net::{IpAddr, SocketAddr};
use std::sync::atomic::{AtomicU64, Ordering};
use std::sync::{Arc, PoisonError, RwLock, RwLockReadGuard};
use std::time::{Duration, SystemTime, UNIX_EPOCH};

use axum::Router;
use axum::body::{Body, to_bytes};
use axum::extract::{ConnectInfo, Query, Request, State};
use axum::http::{Extensions, HeaderMap, HeaderName, HeaderValue, Method, StatusCode, Uri, header};
use axum::response::{IntoResponse, Response};
use base64::Engine as _;
use base64::engine::general_purpose::{STANDARD, URL_SAFE_NO_PAD};
use futures_util::{TryStreamExt as _, stream};
use hmac::{Hmac, KeyInit as _, Mac as _};
use infinidysk_queue::{Queue, Submission, service};
use infinidysk_vfs::{Delivery, Kind, Node, Origin, Vfs};
use percent_encoding::{AsciiSet, NON_ALPHANUMERIC, percent_decode_str, utf8_percent_encode};
use quick_xml::escape::escape;
use serde::Deserialize;
use sha2::{Digest as _, Sha256};
use tokio::io::{AsyncReadExt, AsyncSeekExt};

pub use ipnet::IpNet;

const LINK_SUFFIX: &str = ".rclonelink";
const LINKS: &str = "/completed-symlinks";
/// Largest body accepted by `PUT`; the NZB inside is capped by the parser.
const MAX_NZB: usize = infinidysk_queue::nzb::MAX_UPLOAD;
/// Bytes per body chunk streamed to the client.
const CHUNK: usize = 256 * 1024;
const ALLOW: &str = "OPTIONS, GET, HEAD, PROPFIND, PUT, DELETE";
/// Longer `X-Forwarded-For` headers are ignored as malformed.
const MAX_FORWARDED_BYTES: usize = 4096;
const MAX_FORWARDED_HOPS: usize = 32;
/// Characters of a user agent kept in read history.
const MAX_USER_AGENT: usize = 200;
/// Unreserved characters (RFC 3986) and `/` stay literal in hrefs.
const PATH: &AsciiSet = &NON_ALPHANUMERIC
    .remove(b'-')
    .remove(b'.')
    .remove(b'_')
    .remove(b'~');

pub enum Auth {
    /// HTTP Basic with these credentials.
    Basic {
        user: String,
        password: String,
    },
    Disabled,
}

pub struct WebDav {
    pub vfs: Arc<Vfs>,
    pub queue: Arc<Queue>,
    /// Swapped when settings change.
    pub access: RwLock<Access>,
}

pub struct Access {
    pub auth: Auth,
    /// `downloadKey` that `/view` requires; `/view` refuses everything when `None`.
    pub download_key: Option<String>,
    /// Key of [`sign_view`] tokens, which `/view` also accepts for their one path.
    pub link_key: Option<String>,
    /// Key of STRM URLs, which `/view/.ids/…` accepts per item ([`strm_key`]).
    pub strm_key: Option<String>,
    /// Keys `strm_key` replaced, still accepted during their grace period, counting uses.
    pub previous_strm: Vec<(String, Arc<AtomicU64>)>,
    /// Also accept C#'s earlier STRM key form, counting each use.
    pub legacy_strm: Option<Arc<AtomicU64>>,
    /// Peers whose `X-Forwarded-For` names the client in read history.
    pub trusted_proxies: Vec<IpNet>,
}

/// A trusted proxy entry: a CIDR range or a single address.
pub fn parse_proxy(entry: &str) -> Option<IpNet> {
    let entry = entry.trim();
    entry
        .parse::<IpNet>()
        .map(|net| net.trunc())
        .ok()
        .or_else(|| entry.parse::<IpAddr>().ok().map(IpNet::from))
}

/// A `token` for `/view/<path>` alone, valid until `expires` (Unix seconds).
pub fn sign_view(key: &str, path: &str, expires: u64) -> String {
    let mac = view_mac(key, path, expires).finalize().into_bytes();
    format!("{expires}.{}", URL_SAFE_NO_PAD.encode(mac))
}

fn verify_view(key: &str, path: &str, token: &str, now: u64) -> bool {
    let Some((expires, mac)) = token.split_once('.') else {
        return false;
    };
    let (Ok(expires), Ok(mac)) = (expires.parse::<u64>(), URL_SAFE_NO_PAD.decode(mac)) else {
        return false;
    };
    now < expires && view_mac(key, path, expires).verify_slice(&mac).is_ok()
}

fn view_mac(key: &str, path: &str, expires: u64) -> Hmac<Sha256> {
    #[allow(clippy::expect_used)] // HMAC takes keys of any length.
    let mut mac =
        Hmac::<Sha256>::new_from_slice(key.as_bytes()).expect("HMAC accepts keys of any length");
    mac.update(format!("view\0{expires}\0{}", normalize(path)).as_bytes());
    mac
}

/// The `downloadKey` of a STRM URL for `path` (`.ids/…`, no leading slash):
/// `hex(HMAC-SHA256(key, path))`, as C# wrote it.
pub fn strm_key(key: &str, path: &str) -> String {
    #[allow(clippy::expect_used)] // HMAC takes keys of any length.
    let mut mac =
        Hmac::<Sha256>::new_from_slice(key.as_bytes()).expect("HMAC accepts keys of any length");
    mac.update(path.as_bytes());
    hex(&mac.finalize().into_bytes())
}

/// C#'s earlier STRM key form: `hex(SHA-256(path + "_" + key))`.
pub fn legacy_strm_key(key: &str, path: &str) -> String {
    hex(&Sha256::digest(format!("{path}_{key}")))
}

fn hex(bytes: &[u8]) -> String {
    bytes
        .iter()
        .fold(String::with_capacity(bytes.len() * 2), |mut s, b| {
            let _ = write!(s, "{b:02x}");
            s
        })
}

/// When a refused request last logged C#'s retired STRM key form, Unix seconds.
static LEGACY_WARNED: AtomicU64 = AtomicU64::new(0);

/// Whether `got` is the STRM key of `path` (`/.ids/…`).
fn strm_allowed(access: &Access, path: &str, got: &str) -> bool {
    let (Some(path), Some(key)) = (
        path.strip_prefix('/').filter(|p| p.starts_with(".ids/")),
        &access.strm_key,
    ) else {
        return false;
    };
    if same(strm_key(key, path).as_bytes(), got.as_bytes()) {
        return true;
    }
    if let Some((_, uses)) = access
        .previous_strm
        .iter()
        .find(|(old, _)| same(strm_key(old, path).as_bytes(), got.as_bytes()))
    {
        uses.fetch_add(1, Ordering::Relaxed);
        return true;
    }
    let legacy = std::iter::once(key)
        .chain(access.previous_strm.iter().map(|(old, _)| old))
        .any(|k| same(legacy_strm_key(k, path).as_bytes(), got.as_bytes()));
    if !legacy {
        return false;
    }
    if let Some(uses) = &access.legacy_strm {
        uses.fetch_add(1, Ordering::Relaxed);
        return true;
    }
    let now = SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .unwrap_or_default()
        .as_secs();
    let last = LEGACY_WARNED.load(Ordering::Relaxed);
    if now >= last + 3600
        && LEGACY_WARNED
            .compare_exchange(last, now, Ordering::Relaxed, Ordering::Relaxed)
            .is_ok()
    {
        tracing::warn!(
            reason = "STRM link in a pre-migration format; rescan the media server library",
            "refused a STRM playback link"
        );
    }
    false
}

impl WebDav {
    fn access(&self) -> RwLockReadGuard<'_, Access> {
        self.access.read().unwrap_or_else(PoisonError::into_inner)
    }
}

pub fn router(state: Arc<WebDav>) -> Router {
    Router::new()
        .route("/view/{*path}", axum::routing::get(view).head(view))
        .fallback(dav)
        .with_state(state)
}

#[derive(Deserialize)]
struct ViewQuery {
    #[serde(rename = "downloadKey")]
    download_key: Option<String>,
    /// From [`sign_view`].
    token: Option<String>,
    /// Tags the read so a player can find its own session.
    player: Option<String>,
}

async fn view(
    State(dav): State<Arc<WebDav>>,
    Query(query): Query<ViewQuery>,
    method: Method,
    headers: HeaderMap,
    extensions: Extensions,
    uri: Uri,
) -> Response {
    let Some(path) = decode(uri.path()) else {
        return StatusCode::BAD_REQUEST.into_response();
    };
    let path = path.strip_prefix("/view").unwrap_or(&path);
    let allowed = {
        let access = dav.access();
        let now = SystemTime::now()
            .duration_since(UNIX_EPOCH)
            .unwrap_or_default()
            .as_secs();
        matches!(
            (&access.download_key, &query.download_key),
            (Some(want), Some(got)) if same(want.as_bytes(), got.as_bytes())
        ) || matches!(
            (&access.link_key, &query.token),
            (Some(key), Some(token)) if verify_view(key, path, token, now)
        ) || query
            .download_key
            .as_deref()
            .is_some_and(|got| strm_allowed(&access, path, got))
    };
    if !allowed {
        return StatusCode::UNAUTHORIZED.into_response();
    }
    let player = query
        .player
        .filter(|p| p.len() <= 64 && p.bytes().all(|b| b.is_ascii_alphanumeric() || b == b'-'));
    let client = client(&headers, peer(&extensions), &dav.access().trusted_proxies);
    match resolve(&dav.vfs, path).await {
        Ok(Some(node)) => {
            let head = method == Method::HEAD;
            get(&dav.vfs, &node, &headers, head, player, client).await
        }
        Ok(None) => StatusCode::NOT_FOUND.into_response(),
        Err(error) => failed(&error),
    }
}

async fn dav(State(dav): State<Arc<WebDav>>, request: Request) -> Response {
    if !authorized(&dav.access().auth, request.headers()) {
        return (
            StatusCode::UNAUTHORIZED,
            [(header::WWW_AUTHENTICATE, "Basic realm=\"infinidysk\"")],
        )
            .into_response();
    }
    let Some(path) = decode(request.uri().path()) else {
        return StatusCode::BAD_REQUEST.into_response();
    };
    let path = normalize(&path);
    let method = request.method().clone();
    let result = match method.as_str() {
        "OPTIONS" => Ok(([
            (HeaderName::from_static("dav"), "1"),
            (header::ALLOW, ALLOW),
            (HeaderName::from_static("ms-author-via"), "DAV"),
        ],)
            .into_response()),
        "PROPFIND" => propfind(&dav.vfs, &path, request.headers()).await,
        "GET" | "HEAD" => {
            let head = method == Method::HEAD;
            let headers = request.headers();
            let peer = peer(request.extensions());
            let client = client(headers, peer, &dav.access().trusted_proxies);
            match resolve(&dav.vfs, &path).await {
                Ok(Some(node)) => Ok(get(&dav.vfs, &node, headers, head, None, client).await),
                Ok(None) => Ok(StatusCode::NOT_FOUND.into_response()),
                Err(error) => Err(error),
            }
        }
        "PUT" => put(&dav.queue, &path, request.into_body()).await,
        "DELETE" => delete(&dav.vfs, &path).await,
        "MKCOL" => match resolve(&dav.vfs, &path).await {
            Ok(Some(_)) => Ok(StatusCode::METHOD_NOT_ALLOWED.into_response()),
            Ok(None) => Ok(StatusCode::FORBIDDEN.into_response()),
            Err(error) => Err(error),
        },
        _ => Ok(StatusCode::FORBIDDEN.into_response()),
    };
    result.unwrap_or_else(|error| failed(&error))
}

fn peer(extensions: &Extensions) -> Option<IpAddr> {
    extensions
        .get::<ConnectInfo<SocketAddr>>()
        .map(|info| info.0.ip())
}

/// Who a read is for in read history: the user agent and the peer address, or the
/// forwarded client when the peer is a trusted proxy.
fn client(headers: &HeaderMap, peer: Option<IpAddr>, trusted: &[IpNet]) -> String {
    let agent: String = headers
        .get(header::USER_AGENT)
        .and_then(|v| v.to_str().ok())
        .unwrap_or_default()
        .trim()
        .chars()
        .filter(|c| !c.is_control())
        .take(MAX_USER_AGENT)
        .collect();
    let address = peer.map(|peer| {
        let peer = peer.to_canonical();
        match forwarded(headers, peer, trusted) {
            Some(client) => format!("{client} via {peer}"),
            None => peer.to_string(),
        }
    });
    match address {
        Some(address) if agent.is_empty() => address,
        Some(address) => format!("{agent} · {address}"),
        None => agent,
    }
}

/// The client's address, or `None` when it cannot be trusted: an untrusted peer sent
/// proxy headers, or a trusted proxy sent them without a valid `X-Forwarded-For`.
pub fn client_ip(headers: &HeaderMap, peer: IpAddr, trusted: &[IpNet]) -> Option<IpAddr> {
    let peer = peer.to_canonical();
    let proxied = ["x-forwarded-for", "forwarded", "x-real-ip"]
        .iter()
        .any(|h| headers.contains_key(*h));
    if !proxied {
        return Some(peer);
    }
    if trusted.iter().any(|net| net.contains(&peer)) {
        return forwarded(headers, peer, trusted);
    }
    None
}

/// The client a trusted `peer` forwarded for: the rightmost `X-Forwarded-For` hop that is
/// not itself a trusted proxy. `None` when the peer is untrusted or the header is missing
/// or malformed.
fn forwarded(headers: &HeaderMap, peer: IpAddr, trusted: &[IpNet]) -> Option<IpAddr> {
    let is_trusted = |ip: &IpAddr| trusted.iter().any(|net| net.contains(ip));
    if !is_trusted(&peer) {
        return None;
    }
    let mut hops = Vec::new();
    let mut bytes = 0;
    for value in &headers.get_all("x-forwarded-for") {
        let value = value.to_str().ok()?;
        bytes += value.len();
        if bytes > MAX_FORWARDED_BYTES {
            return None;
        }
        for hop in value.split(',') {
            hops.push(hop.trim().parse::<IpAddr>().ok()?.to_canonical());
            if hops.len() > MAX_FORWARDED_HOPS {
                return None;
            }
        }
    }
    let mut client = None;
    for hop in hops.into_iter().rev() {
        client = Some(hop);
        if !is_trusted(&hop) {
            break;
        }
    }
    client
}

fn failed(error: &infinidysk_vfs::Error) -> Response {
    tracing::warn!(reason = %error, "webdav request failed");
    StatusCode::INTERNAL_SERVER_ERROR.into_response()
}

/// Whether `headers` carry the credentials `auth` asks for.
pub fn authorized(auth: &Auth, headers: &HeaderMap) -> bool {
    let Auth::Basic { user, password } = auth else {
        return true;
    };
    let Some(encoded) = headers
        .get(header::AUTHORIZATION)
        .and_then(|v| v.to_str().ok())
        .and_then(|v| v.split_once(' '))
        .filter(|(scheme, _)| scheme.eq_ignore_ascii_case("basic"))
        .map(|(_, rest)| rest.trim())
    else {
        return false;
    };
    let Ok(decoded) = STANDARD.decode(encoded) else {
        return false;
    };
    let Some(colon) = decoded.iter().position(|&b| b == b':') else {
        return false;
    };
    // Both compared in full so timing does not reveal which one was wrong.
    let user_ok = same(&decoded[..colon], user.as_bytes());
    let password_ok = same(&decoded[colon + 1..], password.as_bytes());
    user_ok & password_ok
}

/// Constant-time for equal lengths.
fn same(a: &[u8], b: &[u8]) -> bool {
    a.len() == b.len() && a.iter().zip(b).fold(0, |acc, (x, y)| acc | (x ^ y)) == 0
}

fn decode(path: &str) -> Option<String> {
    percent_decode_str(path)
        .decode_utf8()
        .ok()
        .map(std::borrow::Cow::into_owned)
}

/// `/a/b` without empty components; `/` for the root.
fn normalize(path: &str) -> String {
    let mut out = String::with_capacity(path.len());
    for part in path.split('/').filter(|p| !p.is_empty()) {
        out.push('/');
        out.push_str(part);
    }
    if out.is_empty() {
        out.push('/');
    }
    out
}

/// The node at a `WebDAV` path, where links carry [`LINK_SUFFIX`].
async fn resolve(vfs: &Vfs, path: &str) -> Result<Option<Node>, infinidysk_vfs::Error> {
    let path = normalize(path);
    if path.starts_with(LINKS)
        && let Some(stripped) = path.strip_suffix(LINK_SUFFIX)
        && let Some(node @ Node::Link { .. }) = vfs.lookup(stripped).await?
    {
        return Ok(Some(node));
    }
    Ok(vfs
        .lookup(&path)
        .await?
        .filter(|n| !matches!(n, Node::Link { .. })))
}

fn display_name(node: &Node) -> String {
    match node {
        Node::Link { .. } => format!("{}{LINK_SUFFIX}", node.name()),
        _ => node.name(),
    }
}

async fn propfind(
    vfs: &Vfs,
    path: &str,
    headers: &HeaderMap,
) -> Result<Response, infinidysk_vfs::Error> {
    let depth = headers.get("depth").map(HeaderValue::as_bytes);
    let children = match depth {
        Some(b"0") => false,
        // Missing means infinity (RFC 4918), but clients that omit it expect a listing.
        Some(b"1") | None => true,
        Some(_) => {
            return Ok((
                StatusCode::FORBIDDEN,
                [(header::CONTENT_TYPE, "application/xml; charset=utf-8")],
                "<?xml version=\"1.0\" encoding=\"utf-8\"?>\
                 <D:error xmlns:D=\"DAV:\"><D:propfind-finite-depth/></D:error>",
            )
                .into_response());
        }
    };
    let Some(node) = resolve(vfs, path).await? else {
        return Ok(StatusCode::NOT_FOUND.into_response());
    };
    let mut xml = String::from(
        "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<D:multistatus xmlns:D=\"DAV:\">",
    );
    let href = encode(path);
    response(&mut xml, &href, &node);
    if children {
        let base = if href == "/" { "" } else { href.as_str() };
        for child in vfs.list(&node).await? {
            let name = display_name(&child);
            let href = format!("{base}/{}", utf8_percent_encode(&name, PATH));
            response(&mut xml, &href, &child);
        }
    }
    xml.push_str("</D:multistatus>");
    Ok((
        StatusCode::MULTI_STATUS,
        [(header::CONTENT_TYPE, "application/xml; charset=utf-8")],
        xml,
    )
        .into_response())
}

fn encode(path: &str) -> String {
    if path == "/" {
        return "/".to_owned();
    }
    let mut out = String::with_capacity(path.len());
    for part in path.split('/').filter(|p| !p.is_empty()) {
        out.push('/');
        out.extend(utf8_percent_encode(part, PATH));
    }
    out
}

fn response(xml: &mut String, href: &str, node: &Node) {
    let name = display_name(node);
    let modified = http_date(node.modified());
    let _ = write!(
        xml,
        "<D:response><D:href>{}</D:href><D:propstat><D:prop>\
         <D:displayname>{}</D:displayname><D:getlastmodified>{modified}</D:getlastmodified>",
        escape(href),
        escape(&name),
    );
    if node.kind() == Kind::Dir {
        xml.push_str(
            "<D:resourcetype><D:collection/></D:resourcetype>\
             <D:getcontentlength>0</D:getcontentlength><D:iscollection>1</D:iscollection>",
        );
    } else {
        let _ = write!(
            xml,
            "<D:resourcetype/><D:getcontentlength>{}</D:getcontentlength>\
             <D:getcontenttype>{}</D:getcontenttype><D:iscollection>0</D:iscollection>",
            node.size(),
            content_type(&name),
        );
    }
    xml.push_str("</D:prop><D:status>HTTP/1.1 200 OK</D:status></D:propstat></D:response>");
}

fn http_date(ms: i64) -> String {
    let at = UNIX_EPOCH + Duration::from_millis(u64::try_from(ms).unwrap_or(0));
    httpdate::fmt_http_date(at)
}

/// One satisfiable `bytes` range as `start..end`; `Err` when unsatisfiable. Anything
/// else (missing, malformed, several ranges) serves the whole file.
///
/// # Errors
///
/// The range starts at or past the end of the file.
#[allow(
    clippy::result_unit_err,
    reason = "the only error is 416, which needs no detail"
)]
pub fn range(headers: &HeaderMap, size: u64) -> Result<Option<(u64, u64)>, ()> {
    let Some(spec) = headers
        .get(header::RANGE)
        .and_then(|v| v.to_str().ok())
        .and_then(|v| v.trim().strip_prefix("bytes="))
        .filter(|v| !v.contains(','))
    else {
        return Ok(None);
    };
    let Some((first, last)) = spec.trim().split_once('-') else {
        return Ok(None);
    };
    let (first, last) = (first.trim(), last.trim());
    let parsed = if first.is_empty() {
        let Ok(suffix) = last.parse::<u64>() else {
            return Ok(None);
        };
        if suffix == 0 || size == 0 {
            return Err(());
        }
        (size.saturating_sub(suffix), size)
    } else {
        let Ok(start) = first.parse::<u64>() else {
            return Ok(None);
        };
        let end = if last.is_empty() {
            size
        } else {
            match last.parse::<u64>() {
                Ok(last) if last >= start => last.saturating_add(1).min(size),
                _ => return Ok(None),
            }
        };
        if start >= size {
            return Err(());
        }
        (start, end)
    };
    Ok(Some(parsed))
}

#[allow(clippy::significant_drop_tightening)] // False positive: the reader moves into the body.
async fn get(
    vfs: &Vfs,
    node: &Node,
    headers: &HeaderMap,
    head: bool,
    player: Option<String>,
    client: String,
) -> Response {
    if node.kind() == Kind::Dir {
        return (StatusCode::METHOD_NOT_ALLOWED, [(header::ALLOW, ALLOW)]).into_response();
    }
    let size = node.size();
    let mut out = HeaderMap::new();
    let name = display_name(node);
    out.insert(
        header::CONTENT_TYPE,
        HeaderValue::from_static(content_type(&name)),
    );
    out.insert(header::ACCEPT_RANGES, HeaderValue::from_static("bytes"));
    if let Ok(modified) = HeaderValue::try_from(http_date(node.modified())) {
        out.insert(header::LAST_MODIFIED, modified);
    }
    if head {
        out.insert(header::CONTENT_LENGTH, HeaderValue::from(size));
        return (StatusCode::OK, out).into_response();
    }
    let (status, start, end) = match range(headers, size) {
        Ok(None) => (StatusCode::OK, 0, size),
        Ok(Some((start, end))) => {
            if let Ok(value) = HeaderValue::try_from(format!("bytes {start}-{}/{size}", end - 1)) {
                out.insert(header::CONTENT_RANGE, value);
            }
            (StatusCode::PARTIAL_CONTENT, start, end)
        }
        Err(()) => {
            if let Ok(value) = HeaderValue::try_from(format!("bytes */{size}")) {
                out.insert(header::CONTENT_RANGE, value);
            }
            return (StatusCode::RANGE_NOT_SATISFIABLE, out).into_response();
        }
    };
    let origin = Origin {
        delivery: Delivery::WebDav,
        client,
        player,
    };
    let mut reader = match vfs.open(node, origin).await {
        Ok(Some(reader)) => reader,
        Ok(None) => return StatusCode::NOT_FOUND.into_response(),
        Err(error) => return failed(&error),
    };
    reader.set_end(end);
    if start > 0
        && let Err(error) = reader.seek(SeekFrom::Start(start)).await
    {
        tracing::warn!(reason = %error, "webdav seek failed");
        return StatusCode::INTERNAL_SERVER_ERROR.into_response();
    }
    out.insert(header::CONTENT_LENGTH, HeaderValue::from(end - start));
    // The first chunk is read before the status line, so providers that cannot serve it
    // yield a retryable 503 instead of a body cut short. Missing articles never fail
    // here: they are zero-filled and reported to health.
    let fence = match node {
        Node::Item(item) | Node::IdFile(item) => Some(vfs.read_fence(item.id)),
        _ => None,
    };
    let mut reader = reader.take(end - start);
    let first = match body::chunk(&mut reader, fence.as_ref()).await {
        Ok(first) => first,
        Err(error) => {
            tracing::warn!(reason = %error, "webdav read failed before the response started");
            return (
                StatusCode::SERVICE_UNAVAILABLE,
                [(header::RETRY_AFTER, HeaderValue::from_static("5"))],
            )
                .into_response();
        }
    };
    let body = Body::from_stream(
        stream::try_unfold(
            (reader, fence, first),
            |(mut reader, fence, first)| async move {
                let next = match first {
                    Some(data) => Some(data),
                    None => body::chunk(&mut reader, fence.as_ref()).await?,
                };
                Ok::<_, std::io::Error>(next.map(|data| (data, (reader, fence, None))))
            },
        )
        .inspect_err(|error| {
            tracing::warn!(reason = %error, "webdav read failed mid-stream");
        }),
    );
    (status, out, body).into_response()
}

/// `/nzbs/<category>/<file>` as `(category, file)`.
fn nzb_target(path: &str) -> Option<(&str, &str)> {
    let mut parts = path.split('/').filter(|p| !p.is_empty());
    match (parts.next(), parts.next(), parts.next(), parts.next()) {
        (Some("nzbs"), Some(category), Some(file), None) => Some((category, file)),
        _ => None,
    }
}

async fn put(queue: &Queue, path: &str, body: Body) -> Result<Response, infinidysk_vfs::Error> {
    let Some((category, file_name)) = nzb_target(path) else {
        return Ok(StatusCode::FORBIDDEN.into_response());
    };
    let Ok(raw) = to_bytes(body, MAX_NZB).await else {
        return Ok(StatusCode::PAYLOAD_TOO_LARGE.into_response());
    };
    let job = Submission {
        file_name,
        category,
        priority: 0,
        post_processing: 3,
    };
    Ok(match queue.enqueue_owned(&job, Vec::from(raw)).await {
        Ok(_) => StatusCode::CREATED.into_response(),
        Err(error @ (service::Error::Nzb(_) | service::Error::Invalid { .. })) => {
            (StatusCode::BAD_REQUEST, error.to_string()).into_response()
        }
        Err(error) => {
            tracing::warn!(reason = %error, "could not queue an NZB put over webdav");
            StatusCode::INTERNAL_SERVER_ERROR.into_response()
        }
    })
}

async fn delete(vfs: &Vfs, path: &str) -> Result<Response, infinidysk_vfs::Error> {
    if nzb_target(path).is_some() {
        let Some(Node::Nzb(entry)) = vfs.lookup(path).await? else {
            return Ok(StatusCode::NOT_FOUND.into_response());
        };
        return Ok(if vfs.db().remove_job(entry.id).await? {
            StatusCode::NO_CONTENT
        } else {
            StatusCode::NOT_FOUND
        }
        .into_response());
    }
    let Some(node) = resolve(vfs, path).await? else {
        return Ok(StatusCode::NOT_FOUND.into_response());
    };
    Ok(if vfs.delete_link(&node) {
        StatusCode::NO_CONTENT
    } else {
        StatusCode::FORBIDDEN
    }
    .into_response())
}

/// By extension; unknown types are `application/octet-stream`.
fn content_type(name: &str) -> &'static str {
    let ext = name
        .rsplit_once('.')
        .map(|(_, ext)| ext.to_ascii_lowercase())
        .unwrap_or_default();
    match ext.as_str() {
        "mkv" => "video/x-matroska",
        "mp4" | "m4v" => "video/mp4",
        "avi" => "video/x-msvideo",
        "mov" => "video/quicktime",
        "wmv" => "video/x-ms-wmv",
        "webm" => "video/webm",
        "ts" | "m2ts" => "video/mp2t",
        "mpg" | "mpeg" => "video/mpeg",
        "mp3" => "audio/mpeg",
        "flac" => "audio/flac",
        "m4a" | "m4b" => "audio/mp4",
        "aac" => "audio/aac",
        "ogg" | "opus" => "audio/ogg",
        "wav" => "audio/wav",
        "srt" => "application/x-subrip",
        "ass" | "ssa" => "text/x-ssa",
        "vtt" => "text/vtt",
        "nfo" | "txt" => "text/plain",
        "jpg" | "jpeg" => "image/jpeg",
        "png" => "image/png",
        "nzb" => "application/x-nzb",
        "epub" => "application/epub+zip",
        "pdf" => "application/pdf",
        _ => "application/octet-stream",
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn ranges_follow_rfc_9110() {
        let with = |v: &'static str| {
            let mut h = HeaderMap::new();
            h.insert(header::RANGE, HeaderValue::from_static(v));
            range(&h, 100)
        };
        assert_eq!(range(&HeaderMap::new(), 100), Ok(None));
        assert_eq!(with("bytes=10-19"), Ok(Some((10, 20))));
        assert_eq!(with("bytes=90-"), Ok(Some((90, 100))));
        assert_eq!(with("bytes=95-500"), Ok(Some((95, 100))));
        assert_eq!(with("bytes=-5"), Ok(Some((95, 100))));
        assert_eq!(with("bytes=-500"), Ok(Some((0, 100))));
        assert_eq!(with("bytes=100-"), Err(()));
        assert_eq!(with("bytes=0-1,5-6"), Ok(None), "several ranges");
        assert_eq!(with("items=0-1"), Ok(None));
        assert_eq!(with("bytes=5-1"), Ok(None));
    }

    #[test]
    fn clients_name_the_peer_and_trust_only_configured_proxies() {
        let trusted: Vec<IpNet> = ["10.0.0.0/8", "fd00::1"]
            .iter()
            .filter_map(|p| parse_proxy(p))
            .collect();
        assert_eq!(parse_proxy("10.1.2.3/8"), parse_proxy("10.0.0.0/8"));
        assert_eq!(parse_proxy("nope"), None);
        let ip = |s: &str| Some(s.parse::<IpAddr>().unwrap());
        let label = |agent: &str, forwarded: &[&'static str], peer: Option<IpAddr>| {
            let mut h = HeaderMap::new();
            if !agent.is_empty() {
                h.insert(header::USER_AGENT, HeaderValue::from_str(agent).unwrap());
            }
            for value in forwarded {
                h.append("x-forwarded-for", HeaderValue::from_static(value));
            }
            client(&h, peer, &trusted)
        };
        assert_eq!(label("VLC/3", &[], None), "VLC/3");
        assert_eq!(label("", &[], ip("192.0.2.1")), "192.0.2.1");
        assert_eq!(
            label("VLC/3", &[], ip("::ffff:192.0.2.1")),
            "VLC/3 \u{b7} 192.0.2.1"
        );
        // An untrusted peer cannot claim to forward.
        assert_eq!(label("", &["198.51.100.7"], ip("192.0.2.1")), "192.0.2.1");
        assert_eq!(
            label("", &["198.51.100.7"], ip("10.0.0.2")),
            "198.51.100.7 via 10.0.0.2"
        );
        // The rightmost untrusted hop wins; anything left of it is client-supplied.
        assert_eq!(
            label(
                "",
                &["203.0.113.9, 198.51.100.7", "10.0.0.3"],
                ip("10.0.0.2")
            ),
            "198.51.100.7 via 10.0.0.2"
        );
        assert_eq!(
            label("", &["10.0.0.4, fd00::1"], ip("10.0.0.2")),
            "10.0.0.4 via 10.0.0.2",
            "all hops trusted"
        );
        for bad in ["", "198.51.100.7:80", "unknown, 198.51.100.7"] {
            assert_eq!(label("", &[bad], ip("10.0.0.2")), "10.0.0.2", "{bad:?}");
        }
        let long = "1.1.1.1,".repeat(MAX_FORWARDED_HOPS) + "1.1.1.1";
        let mut h = HeaderMap::new();
        h.insert("x-forwarded-for", HeaderValue::from_str(&long).unwrap());
        assert_eq!(forwarded(&h, "10.0.0.2".parse().unwrap(), &trusted), None);

        let peer: IpAddr = "127.0.0.1".parse().unwrap();
        assert_eq!(client_ip(&HeaderMap::new(), peer, &trusted), Some(peer));
        let mut h = HeaderMap::new();
        h.insert("x-real-ip", HeaderValue::from_static("127.0.0.1"));
        assert_eq!(client_ip(&h, peer, &trusted), None, "untrusted proxy");
        let proxy = "10.0.0.2".parse().unwrap();
        assert_eq!(client_ip(&HeaderMap::new(), proxy, &trusted), Some(proxy));
        assert_eq!(client_ip(&h, proxy, &trusted), None, "no X-Forwarded-For");
        h.insert("x-forwarded-for", HeaderValue::from_static("192.168.1.5"));
        assert_eq!(client_ip(&h, proxy, &trusted), ip("192.168.1.5"));
    }
}
