//! Fetches NZBs over HTTP(S) with time, redirect and size limits.
//! General SAB downloads require public addresses. Local indexers use a checked origin.

use std::net::{IpAddr, Ipv4Addr, Ipv6Addr, SocketAddr};
use std::time::Duration;

use infinidysk_queue::nzb::MAX_NZB;
use reqwest::dns::{Addrs, Name, Resolve, Resolving};
use reqwest::header::{CONTENT_DISPOSITION, LOCATION};
use reqwest::{Client, Url, redirect};

/// Covers DNS, every redirect and the whole body.
const TOTAL: Duration = Duration::from_mins(1);
const MAX_REDIRECTS: usize = 10;
/// Instance metadata on some clouds.
const METADATA_V6: Ipv6Addr = Ipv6Addr::new(0xfd00, 0x0ec2, 0, 0, 0, 0, 0, 0x0254);
/// Many indexers only hand NZBs to clients that look like SABnzbd.
pub(crate) const USER_AGENT: &str = "SABnzbd/5.1.3";

pub struct Fetched {
    /// From `Content-Disposition`, else a path segment ending in `.nzb`.
    pub file_name: Option<String>,
    pub body: Vec<u8>,
}

pub struct Fetcher {
    http: Client,
}

impl Fetcher {
    pub fn new() -> Result<Self, reqwest::Error> {
        let http = Client::builder()
            .user_agent(USER_AGENT)
            .redirect(redirect::Policy::none())
            .no_proxy()
            .dns_resolver(Guard)
            .build()?;
        Ok(Self { http })
    }

    /// Downloads `url`. The error is a reason fit for history: it never contains the URL,
    /// which may carry an indexer API key.
    pub async fn fetch(&self, url: &str) -> Result<Fetched, String> {
        self.fetch_with_timeout(url, TOTAL).await
    }

    async fn fetch_with_timeout(&self, url: &str, timeout: Duration) -> Result<Fetched, String> {
        let url = Url::parse(url).map_err(|_| "The URL is not valid.".to_owned())?;
        tokio::time::timeout(timeout, Self::follow(url, &self.http, None))
            .await
            .unwrap_or_else(|_| Err("Fetching the NZB timed out after 60 seconds.".to_owned()))
    }

    pub async fn fetch_indexer(
        url: &str,
        indexer_endpoint: &Url,
        api_key: &str,
        searched_dns: Option<&IndexerDns>,
    ) -> Result<Fetched, String> {
        let url = Url::parse(url).map_err(|_| "The NZB URL is invalid.".to_owned())?;
        tokio::time::timeout(TOTAL, async {
            let source = if let Some(expected) = searched_dns {
                let current = IndexerDns::lookup(indexer_endpoint)
                    .await
                    .map_err(|error| {
                        if error == "Indexer DNS returned an unsafe address." {
                            "Indexer DNS changed since the search.".to_owned()
                        } else {
                            "Could not resolve indexer DNS.".to_owned()
                        }
                    })?;
                if !expected.is_local() || !current.is_local() || !expected.same_addresses(&current)
                {
                    return Err("Indexer DNS changed since the search.".to_owned());
                }
                Some(expected.clone())
            } else {
                None
            };
            let local = source.is_some();
            let keys = std::slice::from_ref(&api_key);
            Self::fetch_with_indexer_dns(url, source, Some((indexer_endpoint, keys, local))).await
        })
        .await
        .unwrap_or_else(|_| Err("Fetching the NZB timed out after 60 seconds.".to_owned()))
    }

    /// Downloads `url` from a configured indexer at `indexer_endpoint`'s origin.
    ///
    /// `api_keys` holds the key of every configured indexer at that origin: indexers may share
    /// one with different paths, and a redirect must not carry any of their keys elsewhere.
    pub(crate) async fn fetch_configured(
        url: Url,
        indexer_endpoint: &Url,
        api_keys: &[&str],
    ) -> Result<Fetched, String> {
        tokio::time::timeout(TOTAL, async {
            let source = IndexerDns::lookup(indexer_endpoint)
                .await
                .map_err(|_| "Could not safely resolve the configured indexer.".to_owned())?;
            let policy = source
                .is_local()
                .then_some((indexer_endpoint, api_keys, true));
            Self::fetch_with_indexer_dns(url, Some(source), policy).await
        })
        .await
        .unwrap_or_else(|_| Err("Fetching the NZB timed out after 60 seconds.".to_owned()))
    }

    async fn fetch_with_indexer_dns(
        url: Url,
        source: Option<IndexerDns>,
        indexer: Option<(&Url, &[&str], bool)>,
    ) -> Result<Fetched, String> {
        let http = Client::builder()
            .user_agent(USER_AGENT)
            .redirect(redirect::Policy::none())
            .no_proxy()
            .dns_resolver(IndexerGuard {
                source,
                source_only: false,
            })
            .build()
            .map_err(|_| "Could not create indexer client.".to_owned())?;
        let mut fetched = Self::follow(url, &http, indexer).await?;
        if let Some((_, api_keys, _)) = indexer
            && fetched.file_name.as_ref().is_some_and(|name| {
                api_keys
                    .iter()
                    .any(|api_key| contains_api_key(name, api_key))
            })
        {
            fetched.file_name = None;
        }
        Ok(fetched)
    }

    async fn follow(
        mut url: Url,
        http: &Client,
        indexer: Option<(&Url, &[&str], bool)>,
    ) -> Result<Fetched, String> {
        for _ in 0..=MAX_REDIRECTS {
            if let Some((endpoint, api_keys, local)) = indexer {
                allowed_indexer_url_for(&url, endpoint, api_keys, local)?;
            } else {
                allowed_public_url(&url)?;
            }
            let mut response = http.get(url.clone()).send().await.map_err(|e| reason(&e))?;
            let status = response.status();
            if status.is_redirection() {
                let location = response
                    .headers()
                    .get(LOCATION)
                    .and_then(|l| l.to_str().ok())
                    .ok_or_else(|| format!("The indexer answered {status} without a location."))?;
                url = redirect_target(&url, location)?;
                continue;
            }
            if !status.is_success() {
                return Err(format!("The indexer answered {status}."));
            }
            if response.content_length().is_some_and(|size| size > MAX_NZB) {
                return Err("The NZB is larger than 256 MB.".to_owned());
            }
            let file_name = response
                .headers()
                .get(CONTENT_DISPOSITION)
                .and_then(|v| v.to_str().ok())
                .and_then(disposition_name)
                .or_else(|| path_name(&url));
            let mut body = Vec::new();
            while let Some(chunk) = response.chunk().await.map_err(|e| reason(&e))? {
                if (body.len() + chunk.len()) as u64 > MAX_NZB {
                    return Err("The NZB is larger than 256 MB.".to_owned());
                }
                body.extend_from_slice(&chunk);
            }
            return Ok(Fetched { file_name, body });
        }
        Err(format!(
            "The indexer redirected more than {MAX_REDIRECTS} times."
        ))
    }
}

fn redirect_target(current: &Url, location: &str) -> Result<Url, String> {
    let next = current
        .join(location)
        .map_err(|_| "The indexer redirected to an invalid URL.".to_owned())?;
    if current.scheme() == "https" && next.scheme() != "https" {
        return Err("An HTTPS download cannot redirect to HTTP.".to_owned());
    }
    Ok(next)
}

fn allowed_public_url(url: &Url) -> Result<(), String> {
    allowed(url)?;
    let host = url
        .host_str()
        .ok_or_else(|| "The URL has no host.".to_owned())?;
    let literal = host.trim_start_matches('[').trim_end_matches(']');
    if literal.parse::<IpAddr>().is_ok_and(|ip| !public_ip(ip)) {
        return Err(PRIVATE_REFUSED.to_owned());
    }
    Ok(())
}

/// Refuses other schemes and literal addresses we never fetch from; names are checked
/// when they resolve.
pub(crate) fn allowed(url: &Url) -> Result<(), String> {
    if !matches!(url.scheme(), "http" | "https") {
        return Err("Only http and https URLs can be fetched.".to_owned());
    }
    let Some(host) = url.host_str() else {
        return Err("The URL has no host.".to_owned());
    };
    let literal = host.trim_start_matches('[').trim_end_matches(']').parse();
    if literal.is_ok_and(denied) {
        return Err(REFUSED.to_owned());
    }
    Ok(())
}

pub(crate) fn local_indexer(addrs: &[SocketAddr]) -> bool {
    !addrs.is_empty()
        && addrs.iter().all(|addr| {
            let ip = addr.ip();
            !denied(ip)
                && match ip {
                    IpAddr::V4(v4) => v4.is_private() || v4.is_loopback(),
                    IpAddr::V6(v6) => v6.to_ipv4_mapped().map_or_else(
                        || v6.is_unique_local() || v6.is_loopback(),
                        |v4| v4.is_private() || v4.is_loopback(),
                    ),
                }
        })
}

/// [`allowed_indexer_url`] for every key in `api_keys`.
fn allowed_indexer_url_for(
    url: &Url,
    endpoint: &Url,
    api_keys: &[&str],
    local: bool,
) -> Result<(), String> {
    // The empty key runs the checks that need no key, even when `api_keys` is empty.
    std::iter::once("")
        .chain(api_keys.iter().copied())
        .try_for_each(|api_key| allowed_indexer_url(url, endpoint, api_key, local))
}

pub(crate) fn allowed_indexer_url(
    url: &Url,
    endpoint: &Url,
    api_key: &str,
    local: bool,
) -> Result<(), String> {
    allowed(url)?;
    if !url.username().is_empty() || url.password().is_some() || url.fragment().is_some() {
        return Err("The indexer download URL contains credentials or a fragment.".to_owned());
    }
    let host = url
        .host_str()
        .ok_or_else(|| "The URL has no host.".to_owned())?;
    let literal = host.trim_start_matches('[').trim_end_matches(']');
    let foreign = url.origin() != endpoint.origin();
    if (foreign || !local) && literal.parse::<IpAddr>().is_ok_and(|ip| !public_ip(ip)) {
        return Err("The indexer download URL targets a non-public address.".to_owned());
    }
    if foreign && local && url.host_str() == endpoint.host_str() {
        return Err("A local indexer redirect cannot change its origin.".to_owned());
    }
    if foreign && url_contains_key(url, api_key) {
        return Err("The indexer download URL would expose credentials.".to_owned());
    }
    Ok(())
}

pub(crate) fn url_contains_key(url: &Url, api_key: &str) -> bool {
    if api_key.is_empty() {
        return false;
    }
    if url.host_str().is_some_and(|host| {
        host.contains(&api_key.to_lowercase())
            || (!api_key.is_ascii() && url.domain().is_some() && {
                let (key, valid) = idna::domain_to_unicode(api_key);
                valid.is_ok() && !key.is_empty() && idna::domain_to_unicode(host).0.contains(&key)
            })
    }) {
        return true;
    }
    contains_api_key(url.path(), api_key)
        || query_contains_key(url, api_key)
        || url.query().is_some_and(|raw| {
            // Servers can also inspect the raw query or decode it without form semantics.
            raw.contains(api_key) || raw.split('&').any(|pair| contains_api_key(pair, api_key))
        })
}

fn query_contains_key(url: &Url, api_key: &str) -> bool {
    url.query_pairs()
        .any(|(name, value)| contains_api_key(&name, api_key) || contains_api_key(&value, api_key))
}

pub(crate) fn contains_api_key(value: &str, api_key: &str) -> bool {
    if api_key.is_empty() {
        return false;
    }
    let mut value = value.to_owned();
    for _ in 0..=16 {
        if value.contains(api_key) {
            return true;
        }
        let decoded = percent_encoding::percent_decode_str(&value).decode_utf8_lossy();
        if decoded == value {
            return false;
        }
        value = decoded.into_owned();
    }
    // Fail closed on excessive nesting without spending unbounded time decoding.
    true
}

fn public_ip(ip: IpAddr) -> bool {
    match ip {
        IpAddr::V4(ip) => {
            let [a, b, c, d] = ip.octets();
            !(a == 0
                || a == 10
                || a == 127
                || a >= 224
                || (a == 100 && (64..=127).contains(&b))
                || (a == 169 && b == 254)
                || (a == 172 && (16..=31).contains(&b))
                || (a == 192
                    && (b == 168
                        || (b == 0 && ((c == 0 && d != 9 && d != 10) || c == 2))
                        || (b == 88 && c == 99)))
                || (a == 198 && (b == 18 || b == 19 || (b == 51 && c == 100)))
                || (a == 203 && b == 0 && c == 113))
        }
        IpAddr::V6(ip) => {
            if let Some(ipv4) = ip.to_ipv4_mapped() {
                return public_ip(IpAddr::V4(ipv4));
            }
            let segments = ip.segments();
            if segments[..6] == [0x64, 0xff9b, 0, 0, 0, 0] {
                let v4 = Ipv4Addr::from((u32::from(segments[6]) << 16) | u32::from(segments[7]));
                return public_ip(IpAddr::V4(v4));
            }
            if segments[0] & 0xe000 != 0x2000
                || segments[0] == 0x2002
                || (segments[0] == 0x2001 && segments[1] == 0x0db8)
                || (segments[0] == 0x3fff && segments[1] < 0x1000)
            {
                return false;
            }
            if segments[0] == 0x2001 && segments[1] < 0x0200 {
                return (segments[1] == 1
                    && segments[2..7].iter().all(|segment| *segment == 0)
                    && (1..=3).contains(&segments[7]))
                    || segments[1] == 3
                    || (segments[1] == 4 && segments[2] == 0x0112)
                    || segments[1] & 0xfff0 == 0x0020
                    || segments[1] & 0xfff0 == 0x0030;
            }
            true
        }
    }
}

const REFUSED: &str = "The URL points to a link-local or cloud-metadata address.";
const PRIVATE_REFUSED: &str = "The NZB download resolved to a non-public address.";

/// Link-local and cloud-metadata addresses, IPv4-mapped ones included.
fn denied(ip: IpAddr) -> bool {
    match ip {
        IpAddr::V4(v4) => v4.is_link_local(),
        IpAddr::V6(v6) => match v6.to_ipv4_mapped() {
            Some(v4) => v4.is_link_local(),
            None => v6.segments()[0] & 0xffc0 == 0xfe80 || v6 == METADATA_V6,
        },
    }
}

/// Resolves names and refuses the whole answer if any address is denied, so the
/// connection only ever uses addresses checked here.
pub(crate) struct Guard;

impl Resolve for Guard {
    fn resolve(&self, name: Name) -> Resolving {
        resolve_checked(&name)
    }
}

#[derive(Clone, Debug)]
pub(crate) struct IndexerDns {
    host: String,
    addrs: Vec<SocketAddr>,
}

impl IndexerDns {
    pub(crate) async fn lookup(url: &Url) -> Result<Self, &'static str> {
        let host = url.host_str().ok_or("Indexer URL has no host.")?.to_owned();
        let literal = host.trim_start_matches('[').trim_end_matches(']');
        let addrs: Vec<SocketAddr> = if let Ok(ip) = literal.parse::<IpAddr>() {
            vec![SocketAddr::new(ip, 0)]
        } else {
            tokio::net::lookup_host((host.as_str(), 0))
                .await
                .map_err(|_| "Could not resolve indexer.")?
                .collect()
        };
        Self::checked(host, addrs)
    }

    fn checked(host: String, addrs: Vec<SocketAddr>) -> Result<Self, &'static str> {
        let has_public = addrs.iter().any(|addr| public_ip(addr.ip()));
        let has_non_public = addrs.iter().any(|addr| !public_ip(addr.ip()));
        if addrs.is_empty()
            || addrs.iter().any(|addr| denied(addr.ip()))
            || (has_public && has_non_public)
            || (has_non_public && !local_indexer(&addrs))
        {
            return Err("Indexer DNS returned an unsafe address.");
        }
        Ok(Self { host, addrs })
    }

    pub(crate) fn is_local(&self) -> bool {
        local_indexer(&self.addrs)
    }

    pub(crate) fn memory_bytes(&self) -> usize {
        self.host.capacity() + self.addrs.capacity() * std::mem::size_of::<SocketAddr>()
    }

    fn same_addresses(&self, other: &Self) -> bool {
        self.host.eq_ignore_ascii_case(&other.host)
            && self
                .addrs
                .iter()
                .map(SocketAddr::ip)
                .collect::<std::collections::HashSet<_>>()
                == other.addrs.iter().map(SocketAddr::ip).collect()
    }

    pub(crate) fn guard(&self) -> IndexerGuard {
        IndexerGuard {
            source: Some(self.clone()),
            source_only: true,
        }
    }
}

pub(crate) struct IndexerGuard {
    source: Option<IndexerDns>,
    source_only: bool,
}

impl Resolve for IndexerGuard {
    fn resolve(&self, name: Name) -> Resolving {
        if let Some(source) = &self.source
            && name.as_str().eq_ignore_ascii_case(&source.host)
        {
            let addrs = source.addrs.clone();
            return Box::pin(async move { Ok(Box::new(addrs.into_iter()) as Addrs) });
        }
        if self.source_only {
            return Box::pin(async {
                Err("The request host does not match the checked indexer.".into())
            });
        }
        resolve_checked(&name)
    }
}

fn resolve_checked(name: &Name) -> Resolving {
    let host = name.as_str().to_owned();
    Box::pin(async move {
        let resolved = tokio::net::lookup_host((host.as_str(), 0)).await?;
        let addrs = checked_resolved_addresses(resolved.collect())?;
        Ok(Box::new(addrs.into_iter()) as Addrs)
    })
}

fn checked_resolved_addresses(addrs: Vec<SocketAddr>) -> Result<Vec<SocketAddr>, &'static str> {
    if addrs.is_empty() || addrs.iter().any(|a| !public_ip(a.ip())) {
        return Err(PRIVATE_REFUSED);
    }
    if addrs.iter().any(|a| denied(a.ip())) {
        return Err(REFUSED);
    }
    Ok(addrs)
}

/// The error and its causes, without the URL.
fn reason(error: &reqwest::Error) -> String {
    if error.is_timeout() {
        return "Fetching the NZB timed out.".to_owned();
    }
    let mut out = "Could not fetch the NZB".to_owned();
    let mut source = std::error::Error::source(error);
    while let Some(cause) = source {
        out.push_str(": ");
        out.push_str(&cause.to_string());
        source = cause.source();
    }
    out.push('.');
    out
}

/// `filename*=UTF-8''…` or `filename="…"`.
fn disposition_name(value: &str) -> Option<String> {
    let mut plain = None;
    for part in value.split(';').map(str::trim) {
        let Some((key, v)) = part.split_once('=') else {
            continue;
        };
        match key.trim().to_ascii_lowercase().as_str() {
            "filename*" => {
                let encoded = v.trim().split_once("''").map_or(v, |(_, e)| e);
                let decoded = percent_encoding::percent_decode_str(encoded)
                    .decode_utf8()
                    .ok()?;
                return Some(decoded.into_owned()).filter(|n| !n.is_empty());
            }
            "filename" => plain = Some(v.trim().trim_matches('"').to_owned()),
            _ => {}
        }
    }
    plain.filter(|n| !n.is_empty())
}

/// The last path segment when it names an NZB file; other segments are often ids.
fn path_name(url: &Url) -> Option<String> {
    let last = url.path_segments()?.next_back()?;
    let last = percent_encoding::percent_decode_str(last)
        .decode_utf8()
        .ok()?;
    let is_nzb = last.len() > 4
        && last
            .get(last.len() - 4..)
            .is_some_and(|e| e.eq_ignore_ascii_case(".nzb"));
    is_nzb.then(|| last.into_owned())
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn foreign_download_checks_idna_hostname() {
        let source = Url::parse("https://indexer.example/api").unwrap();
        let url = Url::parse("https://prefix-m\u{00e9}ta-key.cdn.example/nzb").unwrap();
        assert!(url.host_str().unwrap().starts_with("xn--"));
        assert!(allowed_indexer_url(&url, &source, "M\u{00c9}TA-KEY", false).is_err());
        assert!(allowed_indexer_url(&url, &source, "unrelated-key", false).is_ok());
    }

    #[test]
    fn foreign_download_checks_form_query_credentials() {
        let source = Url::parse("https://indexer.example/api").unwrap();
        for (value, api_key, blocked) in [
            ("fake+key", "fake key", true),
            ("fake%20key", "fake key", true),
            ("fake%2520key", "fake key", true),
            ("fake%2Bkey", "fake+key", true),
            ("fake%252Bkey", "fake+key", true),
            ("f%61ke+key", "fake+key", true), // A recipient may read the raw query.
            ("fake%2Bkey", "fake key", false),
            ("fake%20key", "fake+key", false),
            ("public+feed", "fake key", false),
        ] {
            let url = Url::parse(&format!("https://cdn.example/nzb?token={value}")).unwrap();
            assert_eq!(
                allowed_indexer_url(&url, &source, api_key, false).is_err(),
                blocked,
                "{value}"
            );
        }
        let url = Url::parse("https://fake-key.cdn.example/nzb").unwrap();
        assert!(allowed_indexer_url(&url, &source, "FAKE-KEY", false).is_err());
        let url = Url::parse("https://cdn.example/fake+key").unwrap();
        assert!(allowed_indexer_url(&url, &source, "fake key", false).is_ok());
    }

    #[test]
    fn public_indexer_downloads_require_public_ips_and_no_foreign_credentials() {
        let public = Url::parse("https://indexer.example/api").unwrap();
        let local = Url::parse("http://127.0.0.1:3000/api").unwrap();
        let addresses = |ip: &str| vec![SocketAddr::new(ip.parse().unwrap(), 0)];
        assert!(!local_indexer(&addresses("8.8.8.8")));
        assert!(local_indexer(&addresses("127.0.0.1")));
        assert!(local_indexer(&addresses("192.168.1.10")));
        assert!(local_indexer(&addresses("fd00::1")));
        assert!(!local_indexer(&addresses("169.254.169.254")));
        assert!(!local_indexer(&[]));
        assert!(!local_indexer(&[
            SocketAddr::new("8.8.8.8".parse().unwrap(), 0),
            SocketAddr::new("10.0.0.2".parse().unwrap(), 0),
        ]));
        for host in ["indexer.lan", "indexer.local", "indexer", "indexer.example"] {
            let dns = IndexerDns {
                host: host.to_owned(),
                addrs: addresses("8.8.8.8"),
            };
            assert!(!dns.is_local(), "{host}");
        }

        for address in [
            "127.0.0.1",
            "10.20.30.40",
            "192.168.0.1",
            "172.16.0.1",
            "100.64.0.1",
            "169.254.169.254",
            "0.0.0.0",
            "224.0.0.1",
        ] {
            let ip = address.parse::<IpAddr>().unwrap();
            assert!(!public_ip(ip), "{address}");
            let url = Url::parse(&format!("http://{address}/nzb")).unwrap();
            assert!(
                allowed_indexer_url(&url, &public, "secret", false).is_err(),
                "{address}"
            );
        }
        for address in ["::1", "fc00::1", "fe80::1", "::ffff:127.0.0.1"] {
            assert!(!public_ip(address.parse().unwrap()), "{address}");
            let url = Url::parse(&format!("http://[{address}]/nzb")).unwrap();
            assert!(
                allowed_indexer_url(&url, &public, "secret", false).is_err(),
                "{address}"
            );
        }
        assert!(public_ip("8.8.8.8".parse().unwrap()));
        assert!(public_ip("2606:4700:4700::1111".parse().unwrap()));
        assert!(
            allowed_indexer_url(
                &Url::parse("https://cdn.example/nzb?id=11").unwrap(),
                &public,
                "secret",
                false
            )
            .is_ok()
        );
        assert!(
            allowed_indexer_url(
                &Url::parse("https://cdn.example/nzb?apikey=secret").unwrap(),
                &public,
                "secret",
                false
            )
            .is_err()
        );
        assert!(
            allowed_indexer_url(
                &Url::parse("https://cdn.example/nzb?other=secret").unwrap(),
                &public,
                "secret",
                false
            )
            .is_err()
        );
        assert!(
            allowed_indexer_url(
                &Url::parse("https://indexer.example/api?t=get&id=11&apikey=secret").unwrap(),
                &public,
                "secret",
                false
            )
            .is_ok()
        );
        assert!(
            allowed_indexer_url(
                &Url::parse("http://127.0.0.1:3000/nzb").unwrap(),
                &local,
                "secret",
                true
            )
            .is_ok()
        );
    }

    #[tokio::test]
    async fn pinned_source_dns_uses_the_checked_connection_address() {
        use tokio::io::{AsyncReadExt, AsyncWriteExt};

        let listener = tokio::net::TcpListener::bind("127.0.0.1:0").await.unwrap();
        let addr = listener.local_addr().unwrap();
        let source = IndexerDns {
            host: "public-looking.lan".to_owned(),
            addrs: vec![SocketAddr::new(addr.ip(), 0)],
        };
        assert!(source.is_local());
        let connected = tokio::spawn(async move {
            let (mut socket, _) = listener.accept().await.unwrap();
            let mut request = [0u8; 1024];
            let size = socket.read(&mut request).await.unwrap();
            assert!(String::from_utf8_lossy(&request[..size]).starts_with("GET /nzb HTTP/1.1"));
            socket
                .write_all(b"HTTP/1.1 200 OK\r\nContent-Length: 4\r\nConnection: close\r\n\r\nokay")
                .await
                .unwrap();
        });
        let client = Client::builder()
            .dns_resolver(source.guard())
            .no_proxy()
            .build()
            .unwrap();
        let response = client
            .get(format!("http://public-looking.lan:{}/nzb", addr.port()))
            .send()
            .await
            .unwrap();
        assert_eq!(response.status(), 200);
        assert_eq!(response.bytes().await.unwrap().as_ref(), b"okay");
        connected.await.unwrap();

        let rebind_public = IndexerDns {
            host: source.host.clone(),
            addrs: vec![SocketAddr::new("8.8.8.8".parse().unwrap(), 0)],
        };
        let rebind_mixed = IndexerDns {
            host: source.host,
            addrs: vec![
                SocketAddr::new("127.0.0.1".parse().unwrap(), 0),
                SocketAddr::new("8.8.8.8".parse().unwrap(), 0),
            ],
        };
        assert!(!rebind_public.is_local());
        assert!(!rebind_mixed.is_local());
        assert!(IndexerDns::checked(rebind_public.host, rebind_public.addrs).is_ok());
        assert!(IndexerDns::checked(rebind_mixed.host, rebind_mixed.addrs).is_err());
        assert!(
            IndexerDns::checked("indexer".to_owned(), source.addrs)
                .unwrap()
                .is_local()
        );
    }

    #[test]
    fn indexer_dns_checks_ipv4_and_ipv6_as_one_answer() {
        let checked = |ips: &[&str]| {
            IndexerDns::checked(
                "indexer.invalid".to_owned(),
                ips.iter()
                    .map(|ip| SocketAddr::new(ip.parse().unwrap(), 0))
                    .collect(),
            )
        };
        assert!(
            checked(&[
                "127.0.0.1",
                "::1",
                "192.168.1.10",
                "fd00::1",
                "::ffff:10.0.0.1"
            ])
            .unwrap()
            .is_local()
        );
        assert!(
            !checked(&["8.8.8.8", "2606:4700:4700::1111"])
                .unwrap()
                .is_local()
        );
        for ips in [
            ["8.8.8.8", "::ffff:127.0.0.1"],
            ["2606:4700:4700::1111", "fd00::1"],
            ["127.0.0.1", "fe80::1"],
            ["::1", "::ffff:169.254.169.254"],
        ] {
            assert!(checked(&ips).is_err(), "{ips:?}");
        }
    }

    #[test]
    fn local_dns_binding_compares_all_ipv4_and_ipv6_addresses() {
        let addrs = |first: &str, second: &str| {
            vec![
                SocketAddr::new(first.parse().unwrap(), 0),
                SocketAddr::new(second.parse().unwrap(), 0),
            ]
        };
        let original =
            IndexerDns::checked("indexer.lan".to_owned(), addrs("192.168.1.10", "fd00::10"))
                .unwrap();
        let reordered =
            IndexerDns::checked("INDEXER.LAN".to_owned(), addrs("fd00::10", "192.168.1.10"))
                .unwrap();
        let changed_v4 =
            IndexerDns::checked("indexer.lan".to_owned(), addrs("192.168.1.11", "fd00::10"))
                .unwrap();
        let changed_v6 =
            IndexerDns::checked("indexer.lan".to_owned(), addrs("192.168.1.10", "fd00::11"))
                .unwrap();
        assert!(original.same_addresses(&reordered));
        assert!(!original.same_addresses(&changed_v4));
        assert!(!original.same_addresses(&changed_v6));
        assert!(
            IndexerDns::checked("indexer.lan".to_owned(), addrs("192.168.1.10", "8.8.8.8"),)
                .is_err()
        );
        assert!(
            IndexerDns::checked("indexer.lan".to_owned(), addrs("fd00::10", "fe80::1"),).is_err()
        );
    }

    #[tokio::test]
    async fn local_search_reference_rejects_private_ip_rebinding() {
        use tokio::io::{AsyncReadExt, AsyncWriteExt};

        let listener = tokio::net::TcpListener::bind("127.0.0.2:0").await.unwrap();
        let addr = listener.local_addr().unwrap();
        let endpoint = Url::parse(&format!("http://{addr}/api")).unwrap();
        let searched_dns = IndexerDns {
            host: "127.0.0.2".to_owned(),
            addrs: vec![SocketAddr::new("127.0.0.1".parse().unwrap(), 0)],
        };
        assert!(searched_dns.is_local());
        let server = tokio::spawn(async move {
            let Ok(Ok((mut stream, _))) =
                tokio::time::timeout(Duration::from_millis(500), listener.accept()).await
            else {
                return false;
            };
            let mut request = [0u8; 512];
            let _ = stream.read(&mut request).await.unwrap();
            stream
                .write_all(b"HTTP/1.1 200 OK\r\nContent-Length: 4\r\nConnection: close\r\n\r\nokay")
                .await
                .unwrap();
            true
        });
        let fetched = Fetcher::fetch_indexer(
            &format!("http://{addr}/nzb"),
            &endpoint,
            "mock-key",
            Some(&searched_dns),
        )
        .await;
        assert_eq!(
            fetched.err().as_deref(),
            Some("Indexer DNS changed since the search.")
        );
        assert!(!server.await.unwrap());
    }

    #[tokio::test]
    async fn a_local_indexer_rebinding_to_public_cannot_receive_its_key() {
        let endpoint = Url::parse("http://8.8.8.8/api").unwrap();
        let searched_dns = IndexerDns {
            host: "8.8.8.8".to_owned(),
            addrs: vec![SocketAddr::new("127.0.0.1".parse().unwrap(), 0)],
        };
        let result = Fetcher::fetch_indexer(
            "http://8.8.8.8/api?t=get&id=1&apikey=secret",
            &endpoint,
            "secret",
            Some(&searched_dns),
        )
        .await;
        assert_eq!(
            result.err().as_deref(),
            Some("Indexer DNS changed since the search.")
        );
    }

    #[test]
    fn foreign_urls_never_receive_encoded_or_embedded_keys() {
        let endpoint = Url::parse("https://indexer.example/api").unwrap();
        let host_with_key = Url::parse("https://Secret123.cdn.example/nzb").unwrap();
        assert!(allowed_indexer_url(&host_with_key, &endpoint, "secret123", false).is_err());
        let key = "s3cr3t@42";
        for link in [
            "https://cdn.example/nzb/s3cr3t@42",
            "https://cdn.example/nzb/s3cr3t%4042",
            "https://cdn.example/nzb/%733cr3t%4042",
            "https://cdn.example/nzb/%25733cr3t%254042",
            "https://cdn.example/?token=prefixs3cr3t%4042suffix",
            "https://cdn.example/?token=prefixs3cr3t@42suffix",
            "https://cdn.example/?url=https%3A%2F%2Findexer.example%2Fapi%3Fapikey%3Ds3cr3t%254042",
        ] {
            let url = Url::parse(link).unwrap();
            let error = allowed_indexer_url(&url, &endpoint, key, false).unwrap_err();
            assert_eq!(
                error, "The indexer download URL would expose credentials.",
                "{link}"
            );
            assert!(!error.contains(key));
        }
        assert!(
            allowed_indexer_url(
                &Url::parse("https://cdn.example/nzb?token=unrelated-public-token").unwrap(),
                &endpoint,
                key,
                false
            )
            .is_ok()
        );
        assert!(
            allowed_indexer_url(
                &Url::parse("https://indexer.example/api?t=get&id=1&apikey=s3cr3t%4042").unwrap(),
                &endpoint,
                key,
                false
            )
            .is_ok()
        );
    }

    #[tokio::test]
    async fn local_and_cdn_nzb_downloads_still_work() {
        use tokio::io::{AsyncReadExt, AsyncWriteExt};

        let listener = tokio::net::TcpListener::bind("127.0.0.1:0").await.unwrap();
        let addr = listener.local_addr().unwrap();
        let task = tokio::spawn(async move {
            let (mut stream, _) = listener.accept().await.unwrap();
            let mut bytes = [0u8; 512];
            let read = stream.read(&mut bytes).await.unwrap();
            assert!(String::from_utf8_lossy(&bytes[..read]).starts_with("GET /release HTTP/1.1"));
            stream
                .write_all(
                    b"HTTP/1.1 200 OK\r\nContent-Length: 5\r\nConnection: close\r\n\r\nhello",
                )
                .await
                .unwrap();
        });
        let endpoint = Url::parse(&format!("http://{addr}/api")).unwrap();
        let nzb = Fetcher::fetch_indexer(
            &format!("http://{addr}/release"),
            &endpoint,
            "secret",
            Some(&IndexerDns::lookup(&endpoint).await.unwrap()),
        )
        .await
        .unwrap();
        assert_eq!(nzb.body, b"hello");
        task.await.unwrap();

        let listener = tokio::net::TcpListener::bind("127.0.0.1:0").await.unwrap();
        let addr = listener.local_addr().unwrap();
        let task = tokio::spawn(async move {
            let (mut stream, _) = listener.accept().await.unwrap();
            let mut bytes = [0u8; 512];
            let read = stream.read(&mut bytes).await.unwrap();
            assert!(String::from_utf8_lossy(&bytes[..read]).starts_with("GET /nzb HTTP/1.1"));
            stream
                .write_all(
                    b"HTTP/1.1 200 OK\r\nContent-Length: 5\r\nConnection: close\r\n\r\nhello",
                )
                .await
                .unwrap();
        });
        let origin = Url::parse("https://indexer.example/api").unwrap();
        let url = Url::parse(&format!("http://cdn.example:{}/nzb", addr.port())).unwrap();
        assert!(allowed_indexer_url(&url, &origin, "secret", false).is_ok());
        let client = Client::builder()
            .no_proxy()
            .dns_resolver(
                IndexerDns {
                    host: "cdn.example".into(),
                    addrs: vec![SocketAddr::new(addr.ip(), 0)],
                }
                .guard(),
            )
            .build()
            .unwrap();
        let fetched = Fetcher::follow(url, &client, Some((&origin, &["secret"], false)))
            .await
            .unwrap();
        assert_eq!(fetched.body, b"hello");
        task.await.unwrap();
    }

    #[tokio::test]
    async fn redirects_recheck_nested_and_encoded_secret_values() {
        use tokio::io::{AsyncReadExt, AsyncWriteExt};
        for (location, api_key) in [
            ("https://cdn.example/nzb/s3cr3t%4042", "s3cr3t@42"),
            ("https://cdn.example/nzb/%25733cr3t%254042", "s3cr3t@42"),
            (
                "https://cdn.example/nzb?token=prefixs3cr3t%4042suffix",
                "s3cr3t@42",
            ),
            ("https://cdn.example/nzb?token=prefix-fake+key", "fake key"),
        ] {
            let server = tokio::net::TcpListener::bind("127.0.0.1:0").await.unwrap();
            let addr = server.local_addr().unwrap();
            let response = format!(
                "HTTP/1.1 302 Found\r\nLocation: {location}\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"
            );
            let task = tokio::spawn(async move {
                let (mut socket, _) = server.accept().await.unwrap();
                let mut request = [0u8; 512];
                let _ = socket.read(&mut request).await.unwrap();
                socket.write_all(response.as_bytes()).await.unwrap();
            });
            let endpoint = Url::parse(&format!("http://{addr}/api")).unwrap();
            let result = Fetcher::fetch_indexer(
                &format!("http://{addr}/start"),
                &endpoint,
                api_key,
                Some(&IndexerDns::lookup(&endpoint).await.unwrap()),
            )
            .await;
            assert_eq!(
                result.err().as_deref(),
                Some("The indexer download URL would expose credentials.")
            );
            task.await.unwrap();
        }
    }

    #[tokio::test]
    async fn local_redirects_cannot_reach_other_private_services() {
        use tokio::io::{AsyncReadExt, AsyncWriteExt};
        for host in [
            "127.0.0.1",
            "localhost",
            "192.168.1.25",
            "10.1.2.3",
            "169.254.169.254",
        ] {
            let target = tokio::net::TcpListener::bind("127.0.0.1:0").await.unwrap();
            let target_addr = target.local_addr().unwrap();
            let origin = tokio::net::TcpListener::bind("127.0.0.1:0").await.unwrap();
            let origin_addr = origin.local_addr().unwrap();
            let destination = format!("http://{host}:{}/internal", target_addr.port());
            let task = tokio::spawn(async move {
                let (mut socket, _) = origin.accept().await.unwrap();
                let mut data = [0u8; 512];
                let _ = socket.read(&mut data).await.unwrap();
                let response = format!(
                    "HTTP/1.1 302 Found\r\nLocation: {destination}\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"
                );
                socket.write_all(response.as_bytes()).await.unwrap();
            });
            let endpoint = Url::parse(&format!("http://{origin_addr}/api")).unwrap();
            let result = Fetcher::fetch_indexer(
                &format!("http://{origin_addr}/start"),
                &endpoint,
                "mock-key",
                Some(&IndexerDns::lookup(&endpoint).await.unwrap()),
            )
            .await;
            assert!(result.is_err(), "{host}");
            task.await.unwrap();
            assert!(
                tokio::time::timeout(Duration::from_millis(100), target.accept())
                    .await
                    .is_err(),
                "{host}"
            );
        }
        let endpoint = Url::parse("http://127.0.0.1:8080/api").unwrap();
        for target in [
            "http://127.0.0.2:8080/private",
            "http://192.168.1.1/api",
            "http://[::1]:8080/api",
            "http://[fd00::1]:8080/api",
            "http://169.254.169.254/latest",
            "http://127.0.0.1:8181/other",
        ] {
            assert!(
                allowed_indexer_url(&Url::parse(target).unwrap(), &endpoint, "mock-key", true)
                    .is_err(),
                "{target}"
            );
        }
        assert!(
            allowed_indexer_url(
                &Url::parse("http://127.0.0.1:8080/api?t=get&id=1").unwrap(),
                &endpoint,
                "mock-key",
                true
            )
            .is_ok()
        );
        assert!(
            allowed_indexer_url(
                &Url::parse("https://cdn.example/file?token=public").unwrap(),
                &endpoint,
                "mock-key",
                true
            )
            .is_ok()
        );
    }

    #[test]
    fn redirects_expose_no_key_of_indexers_sharing_an_origin() {
        let endpoint = Url::parse("http://127.0.0.1:8080/first/api").unwrap();
        let keys = ["first-key", "second-key"];
        for key in keys {
            let foreign = Url::parse(&format!("https://cdn.example/nzb?apikey={key}")).unwrap();
            assert_eq!(
                allowed_indexer_url_for(&foreign, &endpoint, &keys, true).unwrap_err(),
                "The indexer download URL would expose credentials.",
                "{key}"
            );
        }
        let own = Url::parse("http://127.0.0.1:8080/second/api?t=get&apikey=second-key").unwrap();
        assert!(allowed_indexer_url_for(&own, &endpoint, &keys, true).is_ok());
        let public = Url::parse("https://cdn.example/file?token=public").unwrap();
        assert!(allowed_indexer_url_for(&public, &endpoint, &keys, true).is_ok());
        let private = Url::parse("http://192.168.1.1/api").unwrap();
        assert!(allowed_indexer_url_for(&private, &endpoint, &[], true).is_err());
    }

    #[tokio::test]
    async fn downloaded_filenames_never_carry_indexer_credentials() {
        use tokio::io::{AsyncReadExt, AsyncWriteExt};
        for (path, header) in [
            (
                "/nzb/file",
                "Content-Disposition: attachment; filename=\"mock-secret-123.nzb\"\r\n",
            ),
            ("/nzb/mock-secret-123.nzb", ""),
            ("/nzb/%6d%6f%63%6b-secret-123.nzb", ""),
            ("/nzb/%256d%256f%2563%256b-secret-123.nzb", ""),
            (
                "/nzb/public.nzb",
                "Content-Disposition: attachment; filename=\"public.nzb\"\r\n",
            ),
        ] {
            let listener = tokio::net::TcpListener::bind("127.0.0.1:0").await.unwrap();
            let addr = listener.local_addr().unwrap();
            let response = format!(
                "HTTP/1.1 200 OK\r\n{header}Content-Length: 4\r\nConnection: close\r\n\r\nNZB!"
            );
            let task = tokio::spawn(async move {
                let (mut socket, _) = listener.accept().await.unwrap();
                let mut request = [0u8; 1024];
                let _ = socket.read(&mut request).await.unwrap();
                socket.write_all(response.as_bytes()).await.unwrap();
            });
            let endpoint = Url::parse(&format!("http://{addr}/api")).unwrap();
            let fetched = Fetcher::fetch_indexer(
                &format!("http://{addr}{path}"),
                &endpoint,
                "mock-secret-123",
                Some(&IndexerDns::lookup(&endpoint).await.unwrap()),
            )
            .await
            .unwrap();
            if path.ends_with("public.nzb") {
                assert_eq!(fetched.file_name.as_deref(), Some("public.nzb"));
            } else {
                assert!(
                    fetched.file_name.is_none(),
                    "{path}: {:?}",
                    fetched.file_name
                );
            }
            task.await.unwrap();
        }
        for name in [
            "prefixmock-secret-123suffix.nzb",
            "mock%2dsecret%2d123.nzb",
            "mock%252dsecret%252d123.nzb",
        ] {
            assert!(contains_api_key(name, "mock-secret-123"), "{name}");
        }
    }

    #[tokio::test]
    async fn indexer_download_checks_dns_and_each_redirect() {
        use tokio::io::{AsyncReadExt, AsyncWriteExt};
        let public = Url::parse("https://indexer.example/api").unwrap();
        assert!(
            Fetcher::fetch_indexer("http://127.0.0.1:9/nzb", &public, "secret", None)
                .await
                .is_err()
        );
        let dns = Fetcher::fetch_indexer("http://localhost:9/nzb", &public, "secret", None).await;
        assert!(dns.is_err());
        let local_server = tokio::net::TcpListener::bind("127.0.0.1:0").await.unwrap();
        let address = local_server.local_addr().unwrap();
        let server = tokio::spawn(async move {
            let (mut stream, _) = local_server.accept().await.unwrap();
            let mut buf = [0; 1024];
            let _ = stream.read(&mut buf).await.unwrap();
            stream.write_all(
                b"HTTP/1.1 302 Found\r\nLocation: http://169.254.169.254/latest\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"
            ).await.unwrap();
        });
        let local = Url::parse(&format!("http://{address}/api")).unwrap();
        let result = Fetcher::fetch_indexer(
            &format!("http://{address}/start"),
            &local,
            "secret",
            Some(&IndexerDns::lookup(&local).await.unwrap()),
        )
        .await;
        assert!(result.is_err());
        server.await.unwrap();

        let local_server = tokio::net::TcpListener::bind("127.0.0.1:0").await.unwrap();
        let address = local_server.local_addr().unwrap();
        let server = tokio::spawn(async move {
            let (mut stream, _) = local_server.accept().await.unwrap();
            let mut buf = [0; 1024];
            let _ = stream.read(&mut buf).await.unwrap();
            stream.write_all(
                b"HTTP/1.1 302 Found\r\nLocation: https://cdn.example/nzb?apikey=secret\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"
            ).await.unwrap();
        });
        let local = Url::parse(&format!("http://{address}/api")).unwrap();
        assert!(
            Fetcher::fetch_indexer(
                &format!("http://{address}/start"),
                &local,
                "secret",
                Some(&IndexerDns::lookup(&local).await.unwrap())
            )
            .await
            .is_err()
        );
        server.await.unwrap();
    }

    #[tokio::test]
    async fn download_rejects_large_responses_and_deadlines() {
        use tokio::io::{AsyncReadExt, AsyncWriteExt};

        let listener = tokio::net::TcpListener::bind("127.0.0.1:0").await.unwrap();
        let addr = listener.local_addr().unwrap();
        let oversized = tokio::spawn(async move {
            let (mut socket, _) = listener.accept().await.unwrap();
            let mut request = [0; 1024];
            socket.read_exact(&mut request[..1]).await.unwrap();
            let response = format!(
                "HTTP/1.1 200 OK\r\nContent-Length: {}\r\nConnection: close\r\n\r\n",
                MAX_NZB + 1
            );
            socket.write_all(response.as_bytes()).await.unwrap();
        });
        let fetcher = Fetcher::new().unwrap();
        let endpoint = Url::parse(&format!("http://{addr}/api")).unwrap();
        let source = IndexerDns::lookup(&endpoint).await.unwrap();
        let error = Fetcher::fetch_indexer(
            &format!("http://{addr}/large"),
            &endpoint,
            "key",
            Some(&source),
        )
        .await
        .err()
        .unwrap();
        assert_eq!(error, "The NZB is larger than 256 MB.");
        oversized.await.unwrap();

        let listener = tokio::net::TcpListener::bind("127.0.0.1:0").await.unwrap();
        let addr = listener.local_addr().unwrap();
        let delayed = tokio::spawn(async move {
            let (mut socket, _) = listener.accept().await.unwrap();
            let mut request = [0; 1024];
            socket.read_exact(&mut request[..1]).await.unwrap();
            tokio::time::sleep(Duration::from_secs(5)).await;
        });
        let endpoint = Url::parse(&format!("http://{addr}/api")).unwrap();
        let source = IndexerDns::lookup(&endpoint).await.unwrap();
        let result = tokio::time::timeout(
            Duration::from_millis(100),
            Fetcher::fetch_indexer(
                &format!("http://{addr}/slow"),
                &endpoint,
                "key",
                Some(&source),
            ),
        )
        .await;
        assert!(result.is_err());
        delayed.abort();
        assert!(fetcher.fetch("http://169.254.169.254/nzb").await.is_err());
        assert!(fetcher.fetch("file:///etc/passwd").await.is_err());
    }

    #[tokio::test]
    async fn general_sab_fetch_does_not_connect_to_untrusted_loopback_or_localhost() {
        use tokio::io::{AsyncReadExt, AsyncWriteExt};

        for host in ["127.0.0.1", "localhost"] {
            let listener = tokio::net::TcpListener::bind("127.0.0.1:0").await.unwrap();
            let port = listener.local_addr().unwrap().port();
            let served = tokio::spawn(async move {
                let Ok(Ok((mut socket, _))) =
                    tokio::time::timeout(Duration::from_millis(600), listener.accept()).await
                else {
                    return false;
                };
                let mut request = [0u8; 1024];
                let _ = socket.read(&mut request).await.unwrap();
                socket
                    .write_all(
                        b"HTTP/1.1 200 OK\r\nContent-Length: 4\r\nConnection: close\r\n\r\nokay",
                    )
                    .await
                    .unwrap();
                true
            });
            let url = format!("http://{host}:{port}/sample.nzb");
            assert!(Fetcher::new().unwrap().fetch(&url).await.is_err(), "{url}");
            assert!(
                !served.await.unwrap(),
                "SAB requested an internal service via {host}"
            );
        }
    }

    #[test]
    fn sab_redirects_never_downgrade_https() {
        let https = Url::parse("https://public.example/start").unwrap();
        let http = Url::parse("http://public.example/start").unwrap();
        assert!(redirect_target(&https, "http://public.example/next").is_err());
        assert!(redirect_target(&https, "https://cdn.example/next").is_ok());
        assert!(redirect_target(&http, "http://cdn.example/next").is_ok());
        assert!(redirect_target(&http, "https://cdn.example/next").is_ok());
    }

    #[test]
    fn iana_special_addresses_are_rejected_as_direct_sab_urls() {
        for ip in [
            "192.0.0.0",
            "192.0.0.1",
            "192.0.0.8",
            "192.0.0.11",
            "192.0.0.170",
            "192.0.0.171",
            "192.0.0.255",
            "192.88.99.1",
            "192.88.99.2",
            "2001::1",
            "2001:2::1",
            "2001:4:113::1",
            "2001:10::1",
            "2001:db8::1",
            "2002::1",
            "3fff::1",
            "3fff:fff::1",
            "64:ff9b:1::1",
            "64:ff9b::a00:1",
            "64:ff9b::a9fe:a9fe",
            "::ffff:192.0.0.1",
        ] {
            let ip: IpAddr = ip.parse().unwrap();
            let url = match ip {
                IpAddr::V4(ip) => format!("https://{ip}/sample.nzb"),
                IpAddr::V6(ip) => format!("https://[{ip}]/sample.nzb"),
            };
            assert!(!public_ip(ip), "{ip}");
            assert!(
                allowed_public_url(&Url::parse(&url).unwrap()).is_err(),
                "{url}"
            );
        }
        for ip in [
            "192.0.0.9",
            "192.0.0.10",
            "192.31.196.1",
            "192.52.193.1",
            "192.175.48.1",
            "8.8.8.8",
            "2001:1::1",
            "2001:1::2",
            "2001:1::3",
            "2001:3::1",
            "2001:4:112::1",
            "2001:20::1",
            "2001:30::1",
            "64:ff9b::808:808",
            "2606:4700:4700::1111",
        ] {
            let ip: IpAddr = ip.parse().unwrap();
            let url = match ip {
                IpAddr::V4(ip) => format!("https://{ip}/sample.nzb"),
                IpAddr::V6(ip) => format!("https://[{ip}]/sample.nzb"),
            };
            assert!(public_ip(ip), "{ip}");
            assert!(
                allowed_public_url(&Url::parse(&url).unwrap()).is_ok(),
                "{url}"
            );
        }
    }

    #[test]
    fn iana_special_dns_answers_are_rejected_including_mixed_results() {
        let addresses = |ips: &[&str]| {
            ips.iter()
                .map(|ip| SocketAddr::new(ip.parse().unwrap(), 0))
                .collect::<Vec<_>>()
        };
        for ips in [
            vec!["192.0.0.1"],
            vec!["2001:2::1"],
            vec!["192.88.99.2"],
            vec!["3fff::1"],
            vec!["64:ff9b::a00:1"],
            vec!["8.8.8.8", "192.0.0.1"],
            vec!["2606:4700:4700::1111", "2001:2::1"],
            vec!["192.0.0.9", "192.0.0.1"],
            vec!["::ffff:192.0.0.1", "8.8.8.8"],
        ] {
            assert!(
                checked_resolved_addresses(addresses(&ips)).is_err(),
                "{ips:?}"
            );
        }
        for ips in [
            vec!["192.0.0.9", "192.0.0.10"],
            vec!["2001:1::1", "2001:3::1"],
            vec!["2606:4700:4700::1111", "192.0.0.9"],
        ] {
            assert!(
                checked_resolved_addresses(addresses(&ips)).is_ok(),
                "{ips:?}"
            );
        }
    }

    #[test]
    fn generic_sab_dns_denies_mixed_private_and_reserved_answers() {
        let addresses = |ips: &[&str]| {
            ips.iter()
                .map(|ip| SocketAddr::new(ip.parse().unwrap(), 0))
                .collect::<Vec<_>>()
        };
        for ips in [
            vec!["8.8.8.8", "127.0.0.1"],
            vec!["2606:4700:4700::1111", "fd00::1"],
            vec!["8.8.8.8", "169.254.169.254"],
            vec!["192.168.1.2"],
            vec!["::ffff:127.0.0.1"],
        ] {
            assert!(
                checked_resolved_addresses(addresses(&ips)).is_err(),
                "{ips:?}"
            );
        }
        assert!(checked_resolved_addresses(Vec::new()).is_err());
        assert!(
            checked_resolved_addresses(addresses(&["8.8.8.8", "2606:4700:4700::1111"])).is_ok()
        );
        for url in [
            "http://127.0.0.1/nzb",
            "http://10.0.0.1/nzb",
            "http://192.168.1.2/nzb",
            "http://100.64.1.2/nzb",
            "http://[::1]/nzb",
            "http://[::ffff:127.0.0.1]/nzb",
        ] {
            assert!(
                allowed_public_url(&Url::parse(url).unwrap()).is_err(),
                "{url}"
            );
        }
        assert!(allowed_public_url(&Url::parse("https://cdn.example/nzb").unwrap()).is_ok());
    }

    #[tokio::test]
    async fn generic_sab_redirects_recheck_the_destination_before_connecting() {
        use tokio::io::{AsyncReadExt, AsyncWriteExt};

        let origin = tokio::net::TcpListener::bind("127.0.0.1:0").await.unwrap();
        let origin_port = origin.local_addr().unwrap().port();
        let internal = tokio::net::TcpListener::bind("127.0.0.1:0").await.unwrap();
        let internal_port = internal.local_addr().unwrap().port();
        let origin_task = tokio::spawn(async move {
            let (mut stream, _) = origin.accept().await.unwrap();
            let mut buf = [0; 1024];
            let _ = stream.read(&mut buf).await.unwrap();
            let response = format!(
                "HTTP/1.1 302 Found\r\nLocation: http://127.0.0.1:{internal_port}/private\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"
            );
            stream.write_all(response.as_bytes()).await.unwrap();
        });
        let internal_task = tokio::spawn(async move {
            tokio::time::timeout(Duration::from_millis(700), internal.accept())
                .await
                .is_ok()
        });
        let client = Client::builder()
            .no_proxy()
            .redirect(redirect::Policy::none())
            .dns_resolver(
                IndexerDns {
                    host: "cdn.example".to_owned(),
                    addrs: vec![SocketAddr::new("127.0.0.1".parse().unwrap(), 0)],
                }
                .guard(),
            )
            .build()
            .unwrap();
        let url = Url::parse(&format!("http://cdn.example:{origin_port}/start")).unwrap();
        let result = Fetcher::follow(url, &client, None).await;
        assert!(result.is_err());
        origin_task.await.unwrap();
        assert!(!internal_task.await.unwrap());
    }

    #[tokio::test]
    async fn generic_sab_public_url_redirects_remain_usable() {
        use tokio::io::{AsyncReadExt, AsyncWriteExt};

        let listener = tokio::net::TcpListener::bind("127.0.0.1:0").await.unwrap();
        let port = listener.local_addr().unwrap().port();
        let server = tokio::spawn(async move {
            for response in [
                "HTTP/1.1 302 Found\r\nLocation: /download\r\nContent-Length: 0\r\nConnection: close\r\n\r\n",
                "HTTP/1.1 200 OK\r\nContent-Length: 4\r\nConnection: close\r\n\r\nokay",
            ] {
                let (mut stream, _) = listener.accept().await.unwrap();
                let mut buf = [0; 1024];
                let _ = stream.read(&mut buf).await.unwrap();
                stream.write_all(response.as_bytes()).await.unwrap();
            }
        });
        let client = Client::builder()
            .no_proxy()
            .redirect(redirect::Policy::none())
            .dns_resolver(
                IndexerDns {
                    host: "cdn.example".to_owned(),
                    addrs: vec![SocketAddr::new("127.0.0.1".parse().unwrap(), 0)],
                }
                .guard(),
            )
            .build()
            .unwrap();
        let url = Url::parse(&format!("http://cdn.example:{port}/start")).unwrap();
        let fetched = Fetcher::follow(url, &client, None).await.unwrap();
        assert_eq!(fetched.body, b"okay");
        server.await.unwrap();
    }

    #[test]
    fn link_local_and_metadata_addresses_are_denied() {
        let denied = |s: &str| denied(s.parse().expect("valid address literal"));
        assert!(denied("169.254.169.254"));
        assert!(denied("::ffff:169.254.169.254"));
        assert!(denied("fe80::1"));
        assert!(denied("febf::1"));
        assert!(denied("fd00:ec2::254"));
        assert!(!denied("127.0.0.1"));
        assert!(!denied("192.168.1.10"));
        assert!(!denied("::1"));
        assert!(!denied("fd00::254"));
    }

    #[test]
    fn names_come_from_the_header_or_an_nzb_path() {
        assert_eq!(
            disposition_name(r#"attachment; filename="a b.nzb""#).as_deref(),
            Some("a b.nzb")
        );
        assert_eq!(
            disposition_name("attachment; filename=x; filename*=UTF-8''a%20b.nzb").as_deref(),
            Some("a b.nzb")
        );
        let url = |s: &str| Url::parse(s).expect("valid url literal");
        assert_eq!(
            path_name(&url("http://h/get/a%20b.nzb?apikey=k")).as_deref(),
            Some("a b.nzb")
        );
        assert_eq!(path_name(&url("http://h/api?t=get&id=abc")), None);
    }
}
