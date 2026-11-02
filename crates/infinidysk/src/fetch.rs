//! Fetches NZBs for `mode=addurl`: http(s) only, bounded in time, redirects and size, and
//! never from link-local or cloud-metadata addresses. LAN and loopback stay allowed, since
//! indexer proxies live there.

use std::net::{IpAddr, Ipv6Addr, SocketAddr};
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
        let url = Url::parse(url).map_err(|_| "The URL is not valid.".to_owned())?;
        tokio::time::timeout(TOTAL, self.follow(url))
            .await
            .unwrap_or_else(|_| Err("Fetching the NZB timed out after 60 seconds.".to_owned()))
    }

    async fn follow(&self, mut url: Url) -> Result<Fetched, String> {
        for _ in 0..=MAX_REDIRECTS {
            allowed(&url)?;
            let mut response = self
                .http
                .get(url.clone())
                .send()
                .await
                .map_err(|e| reason(&e))?;
            let status = response.status();
            if status.is_redirection() {
                let location = response
                    .headers()
                    .get(LOCATION)
                    .and_then(|l| l.to_str().ok())
                    .ok_or_else(|| format!("The indexer answered {status} without a location."))?;
                url = url
                    .join(location)
                    .map_err(|_| "The indexer redirected to an invalid URL.".to_owned())?;
                continue;
            }
            if !status.is_success() {
                return Err(format!("The indexer answered {status}."));
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

/// Refuses other schemes and literal addresses we never fetch from; names are checked
/// when they resolve.
fn allowed(url: &Url) -> Result<(), String> {
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

const REFUSED: &str = "The URL points to a link-local or cloud-metadata address.";

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
struct Guard;

impl Resolve for Guard {
    fn resolve(&self, name: Name) -> Resolving {
        let host = name.as_str().to_owned();
        Box::pin(async move {
            let addrs: Vec<SocketAddr> =
                tokio::net::lookup_host((host.as_str(), 0)).await?.collect();
            if addrs.iter().any(|a| denied(a.ip())) {
                return Err(REFUSED.into());
            }
            Ok(Box::new(addrs.into_iter()) as Addrs)
        })
    }
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
