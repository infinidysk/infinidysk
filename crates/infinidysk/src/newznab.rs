//! Bounded Newznab caps and search requests for configured indexers.
use std::sync::Arc;
use std::time::Duration;

use quick_xml::{Reader, events::Event};
use reqwest::Url;
use serde::Serialize;
use uuid::Uuid;

use crate::config::Indexer;

const TIMEOUT: Duration = Duration::from_secs(10);
const MAX_BODY: usize = 256_000;
const MAX_RESULTS: usize = 500;
const MAX_TITLE_BYTES: usize = 512;
const MAX_LINK_BYTES: usize = 2048;
const MAX_GUID_BYTES: usize = 256;
pub(crate) const BASE_URL_KEY: &str =
    "Indexer API keys must be configured separately from the URL.";

pub(crate) fn base_url(indexer: &Indexer) -> Result<Url, &'static str> {
    let url = Url::parse(&indexer.base_url).map_err(|_| "Invalid indexer base URL.")?;
    crate::fetch::allowed(&url).map_err(|_| "Indexer URL is not allowed.")?;
    if !url.username().is_empty() || url.password().is_some() || url.fragment().is_some() {
        return Err("Indexer base URL must not contain credentials or a fragment.");
    }
    if base_url_contains_key(&indexer.base_url, &url, &indexer.api_key.0)
        || url.query_pairs().any(|(key, _)| {
            ["apikey", "api_key", "key", "token", "password"]
                .iter()
                .any(|secret| key.eq_ignore_ascii_case(secret))
        })
    {
        return Err(BASE_URL_KEY);
    }
    Ok(url)
}

pub(crate) fn base_url_contains_key(raw: &str, url: &Url, api_key: &str) -> bool {
    // Check the raw URL because normalization can discard text containing the key.
    raw.split_once(':')
        .is_some_and(|(_, rest)| crate::fetch::contains_api_key(rest, api_key))
        || crate::fetch::url_contains_key(url, api_key)
}

pub(crate) fn endpoint(indexer: &Indexer) -> Result<Url, &'static str> {
    let mut url = base_url(indexer)?;
    if indexer.api_key.0.trim().is_empty() || indexer.api_key.0 == crate::config::MASK {
        return Err("An indexer API key is required.");
    }
    let params: Vec<_> = url
        .query_pairs()
        .filter(|(key, _)| !key.eq_ignore_ascii_case("t"))
        .map(|(key, value)| (key.into_owned(), value.into_owned()))
        .collect();
    let path = url.path().trim_end_matches('/');
    let path = if path
        .rsplit('/')
        .next()
        .is_some_and(|last| last.eq_ignore_ascii_case("api"))
    {
        path.to_owned()
    } else {
        format!("{path}/api")
    };
    url.set_path(&path);
    url.set_query(None);
    url.query_pairs_mut()
        .extend_pairs(
            params
                .iter()
                .map(|(key, value)| (key.as_str(), value.as_str())),
        )
        .append_pair("t", "caps")
        .append_pair("apikey", &indexer.api_key.0);
    Ok(url)
}

pub(crate) async fn test(indexer: &Indexer) -> Result<(), &'static str> {
    check(indexer, TIMEOUT).await
}

async fn indexer_client(
    url: &Url,
    timeout: Duration,
) -> Result<(reqwest::Client, crate::fetch::IndexerDns), &'static str> {
    let source = crate::fetch::IndexerDns::lookup(url)
        .await
        .map_err(|_| "Could not resolve indexer.")?;
    let http = reqwest::Client::builder()
        .user_agent(crate::fetch::USER_AGENT)
        .redirect(reqwest::redirect::Policy::none())
        .no_proxy()
        .dns_resolver(source.guard())
        .connect_timeout(timeout)
        .build()
        .map_err(|_| "Could not create indexer client.")?;
    Ok((http, source))
}

async fn check(indexer: &Indexer, timeout: Duration) -> Result<(), &'static str> {
    let url = endpoint(indexer)?;
    tokio::time::timeout(timeout, async {
        let (http, _) = indexer_client(&url, timeout).await?;
        let body = read_response(&http, url).await?;
        caps(&body)
    })
    .await
    .map_err(|_| "Indexer connection test timed out.")?
}

async fn read_response(http: &reqwest::Client, url: Url) -> Result<Vec<u8>, &'static str> {
    let mut response = http
        .get(url)
        .send()
        .await
        .map_err(|_| "Could not contact indexer.")?;
    match response.status().as_u16() {
        401 | 403 => return Err("Indexer authentication failed."),
        200..=299 => {}
        _ => return Err("Indexer returned an unsuccessful HTTP status."),
    }
    let mut body = Vec::new();
    while let Some(chunk) = response
        .chunk()
        .await
        .map_err(|_| "Could not read indexer response.")?
    {
        if chunk.len() > MAX_BODY - body.len() {
            return Err("Indexer response exceeds 256 KB.");
        }
        body.extend_from_slice(&chunk);
    }
    Ok(body)
}

fn caps(body: &[u8]) -> Result<(), &'static str> {
    let mut reader = Reader::from_reader(body);
    let mut depth = 0usize;
    let mut root = false;
    let mut found_caps = false;
    loop {
        let event = reader.read_event().map_err(|_| "Malformed indexer XML.")?;
        let empty = matches!(&event, Event::Empty(_));
        match event {
            Event::Start(e) | Event::Empty(e) => {
                if depth == 0 {
                    if root {
                        return Err("Malformed indexer XML.");
                    }
                    root = true;
                    if e.local_name().as_ref().eq_ignore_ascii_case("error") {
                        let auth_failure = e.attributes().any(|attribute| {
                            let Ok(attribute) = attribute else {
                                return false;
                            };
                            let code = attribute.value.as_ref();
                            attribute.key.as_ref() == "code"
                                && (code == "100" || code == "101" || code == "102")
                        });
                        return Err(if auth_failure {
                            "Indexer authentication failed."
                        } else {
                            "Indexer rejected the API request."
                        });
                    }
                }
                if e.local_name().as_ref().eq_ignore_ascii_case("caps") {
                    found_caps = true;
                }
                if !empty {
                    depth += 1;
                }
            }
            Event::End(_) => depth = depth.checked_sub(1).ok_or("Malformed indexer XML.")?,
            Event::DocType(_) | Event::GeneralRef(_) => return Err("Unsupported indexer XML."),
            Event::Text(e)
                if depth == 0 && !e.as_ref().as_bytes().iter().all(u8::is_ascii_whitespace) =>
            {
                return Err("Malformed indexer XML.");
            }
            Event::Eof => {
                return if !root || depth != 0 {
                    Err("Malformed indexer XML.")
                } else if found_caps {
                    Ok(())
                } else {
                    Err("Indexer did not return Newznab caps.")
                };
            }
            _ => {}
        }
    }
}

#[derive(Clone, Debug, Serialize, utoipa::ToSchema)]
pub struct SearchResult {
    pub title: String,
    pub size_bytes: u64,
    pub indexer_id: Uuid,
    #[serde(serialize_with = "serialize_indexer_name")]
    #[schema(value_type = String)]
    pub indexer_name: Arc<str>,
    pub nzb_ref: String,
    pub can_queue: bool,
    #[serde(skip_serializing)]
    #[schema(ignore)]
    pub(crate) download_url: Option<String>,
    #[serde(skip_serializing)]
    #[schema(ignore)]
    pub(crate) source_dns: Option<crate::fetch::IndexerDns>,
}

fn serialize_indexer_name<S: serde::Serializer>(
    name: &Arc<str>,
    serializer: S,
) -> Result<S::Ok, S::Error> {
    serializer.serialize_str(name)
}

#[derive(Default)]
struct Item {
    title: String,
    title_overflow: bool,
    link_overflow: bool,
    size: Option<u64>,
    enclosure_size: Option<u64>,
    enclosure_url: Option<String>,
    link: String,
    guid: String,
}

pub(crate) async fn search(
    indexer: &Indexer,
    query: &str,
    limit: u16,
) -> Result<Vec<SearchResult>, &'static str> {
    search_with_timeout(indexer, query, limit, TIMEOUT).await
}

async fn search_with_timeout(
    indexer: &Indexer,
    query: &str,
    limit: u16,
    timeout: Duration,
) -> Result<Vec<SearchResult>, &'static str> {
    let mut url = endpoint(indexer)?;
    let params: Vec<_> = url
        .query_pairs()
        .filter(|(key, _)| {
            !["t", "q", "limit", "extended", "apikey"]
                .iter()
                .any(|skip| key.eq_ignore_ascii_case(skip))
        })
        .map(|(key, value)| (key.into_owned(), value.into_owned()))
        .collect();
    url.set_query(None);
    url.query_pairs_mut()
        .extend_pairs(
            params
                .iter()
                .map(|(key, value)| (key.as_str(), value.as_str())),
        )
        .append_pair("t", "search")
        .append_pair("q", query)
        .append_pair("limit", &limit.to_string())
        .append_pair("extended", "1")
        .append_pair("apikey", &indexer.api_key.0);

    tokio::time::timeout(timeout, async {
        let (http, source) = indexer_client(&url, timeout).await?;
        let body = read_response(&http, url).await?;
        let mut results = parse_search(&body, indexer, source.is_local())?;
        results.truncate(usize::from(limit).min(MAX_RESULTS));
        if source.is_local() {
            for result in &mut results {
                result.source_dns = Some(source.clone());
            }
        }
        Ok(results)
    })
    .await
    .map_err(|_| "Indexer search timed out.")?
}

fn search_error(e: &quick_xml::events::BytesStart<'_>) -> &'static str {
    let auth = e.attributes().flatten().any(|a| {
        a.key.local_name().as_ref().eq_ignore_ascii_case("code")
            && matches!(a.value.as_ref(), "100" | "101" | "102")
    });
    if auth {
        "Indexer authentication failed."
    } else {
        "Indexer rejected the API request."
    }
}

fn text_field(tag: &str) -> bool {
    ["title", "size", "link", "guid"]
        .iter()
        .any(|name| tag.eq_ignore_ascii_case(name))
}

fn append_bounded(target: &mut String, value: &str, max_bytes: usize) -> bool {
    let complete = value.len() <= max_bytes.saturating_sub(target.len());
    for ch in value.chars() {
        if target.len() + ch.len_utf8() > max_bytes {
            break;
        }
        target.push(ch);
    }
    complete
}

fn downloadable_url(
    endpoint: &Url,
    api_key: &str,
    item: &Item,
    source_local: bool,
) -> Option<String> {
    let link = item
        .enclosure_url
        .as_deref()
        .filter(|s| !s.trim().is_empty())
        .or_else(|| {
            (!item.link_overflow && !item.link.trim().is_empty()).then_some(item.link.as_str())
        })?;
    let mut url = Url::parse(link).ok().or_else(|| endpoint.join(link).ok())?;
    if url.origin() == endpoint.origin() && url.path() == endpoint.path() {
        let mut is_get = false;
        let mut has_id = false;
        let mut has_key = false;
        for (name, value) in url.query_pairs() {
            match name.to_ascii_lowercase().as_str() {
                "t" if value.eq_ignore_ascii_case("get") => is_get = true,
                "id" if !value.is_empty() => has_id = true,
                "apikey" => has_key = true,
                _ => {}
            }
        }
        if is_get && has_id && !has_key {
            url.query_pairs_mut().append_pair("apikey", api_key);
        }
    }
    crate::fetch::allowed_indexer_url(&url, endpoint, api_key, source_local).ok()?;
    (url.as_str().len() <= MAX_LINK_BYTES).then(|| url.into())
}

fn parse_search(
    body: &[u8],
    indexer: &Indexer,
    source_local: bool,
) -> Result<Vec<SearchResult>, &'static str> {
    let endpoint = endpoint(indexer).ok();
    let mut xml = Reader::from_reader(body);
    let mut depth = 0usize;
    let mut root = false;
    let mut channel = None;
    let mut found_channel = false;
    let mut current = None;
    let mut field: Option<(usize, String)> = None;
    let mut item = Item::default();
    let mut results = Vec::new();
    let indexer_name: Arc<str> = indexer
        .name
        .replace(&indexer.api_key.0, "[redacted]")
        .into();
    let mut seen = std::collections::HashSet::new();

    loop {
        let event = xml.read_event().map_err(|_| "Malformed indexer XML.")?;
        let empty = matches!(&event, Event::Empty(_));
        match event {
            Event::Start(e) | Event::Empty(e) => {
                let name = e.local_name();
                let name = name.as_ref();
                if depth == 0 {
                    if root {
                        return Err("Malformed indexer XML.");
                    }
                    root = true;
                    if name.eq_ignore_ascii_case("error") {
                        return Err(search_error(&e));
                    }
                    if !name.eq_ignore_ascii_case("rss") {
                        return Err("Indexer did not return RSS.");
                    }
                } else if depth == 1 && name.eq_ignore_ascii_case("channel") {
                    found_channel = true;
                    channel = Some(depth);
                } else if channel == Some(depth.saturating_sub(1))
                    && name.eq_ignore_ascii_case("item")
                {
                    current = Some(depth);
                    item = Item::default();
                } else if current == Some(depth.saturating_sub(1)) {
                    if text_field(name) {
                        field = Some((depth, name.to_ascii_lowercase()));
                    } else if name.eq_ignore_ascii_case("enclosure") {
                        for a in e.attributes().flatten() {
                            let value = a
                                .normalized_value(quick_xml::XmlVersion::Implicit1_0)
                                .map_err(|_| "Malformed indexer XML.")?;
                            if a.key.local_name().as_ref().eq_ignore_ascii_case("url")
                                && !value.trim().is_empty()
                                && value.len() <= MAX_LINK_BYTES
                            {
                                item.enclosure_url = Some(value.into_owned());
                            } else if a.key.local_name().as_ref().eq_ignore_ascii_case("length") {
                                item.enclosure_size = value.parse().ok();
                            }
                        }
                    } else if name.eq_ignore_ascii_case("attr") {
                        let mut attr_name = String::new();
                        let mut attr_value = String::new();
                        for a in e.attributes().flatten() {
                            let value = a
                                .normalized_value(quick_xml::XmlVersion::Implicit1_0)
                                .map_err(|_| "Malformed indexer XML.")?;
                            if a.key.local_name().as_ref().eq_ignore_ascii_case("name") {
                                attr_name = value.into_owned();
                            } else if a.key.local_name().as_ref().eq_ignore_ascii_case("value") {
                                attr_value = value.into_owned();
                            }
                        }
                        if attr_name.eq_ignore_ascii_case("size") {
                            item.size = attr_value.parse().ok();
                        }
                    }
                }
                if empty {
                    if field.as_ref().is_some_and(|(at, _)| *at == depth) {
                        field = None;
                    }
                    if current == Some(depth) && name.eq_ignore_ascii_case("item") {
                        current = None;
                    }
                    if channel == Some(depth) && name.eq_ignore_ascii_case("channel") {
                        channel = None;
                    }
                } else {
                    depth += 1;
                }
            }
            Event::Text(e) => {
                if let Some((at, tag)) = &field {
                    if depth == *at + 1 {
                        let value = e.xml10_content();
                        let value = value.as_ref();
                        match tag.as_str() {
                            "title" => {
                                item.title_overflow |=
                                    !append_bounded(&mut item.title, value, MAX_TITLE_BYTES);
                            }
                            "size" => item.size = value.parse().ok(),
                            "link" => {
                                item.link_overflow |=
                                    !append_bounded(&mut item.link, value, MAX_LINK_BYTES);
                            }
                            "guid" => {
                                append_bounded(&mut item.guid, value, MAX_GUID_BYTES);
                            }
                            _ => {}
                        }
                    }
                } else if depth == 0 && !e.as_ref().chars().all(char::is_whitespace) {
                    return Err("Malformed indexer XML.");
                }
            }
            Event::CData(e) => {
                if let Some((at, tag)) = &field
                    && depth == *at + 1
                {
                    let value = e.xml10_content();
                    match tag.as_str() {
                        "title" => {
                            item.title_overflow |=
                                !append_bounded(&mut item.title, &value, MAX_TITLE_BYTES);
                        }
                        "link" => {
                            item.link_overflow |=
                                !append_bounded(&mut item.link, &value, MAX_LINK_BYTES);
                        }
                        "guid" => {
                            append_bounded(&mut item.guid, &value, MAX_GUID_BYTES);
                        }
                        _ => {}
                    }
                }
            }
            Event::End(e) => {
                depth = depth.checked_sub(1).ok_or("Malformed indexer XML.")?;
                if field.as_ref().is_some_and(|(at, _)| *at == depth) {
                    field = None;
                }
                if current == Some(depth) && e.local_name().as_ref().eq_ignore_ascii_case("item") {
                    current = None;
                    let reference = item
                        .enclosure_url
                        .as_deref()
                        .filter(|s| !s.trim().is_empty())
                        .or_else(|| (!item.link.trim().is_empty()).then_some(item.link.as_str()))
                        .or_else(|| (!item.guid.trim().is_empty()).then_some(item.guid.as_str()));
                    if let Some(reference) = reference.filter(|_| !item.title.trim().is_empty())
                        && results.len() < MAX_RESULTS
                        && seen.insert(reference.to_owned())
                    {
                        let nzb_ref = Uuid::new_v4().to_string();
                        let title = item.title.trim().replace(&indexer.api_key.0, "[redacted]");
                        let title = if item.title_overflow
                            || crate::fetch::contains_api_key(&title, &indexer.api_key.0)
                        {
                            "[redacted]".to_owned()
                        } else {
                            title
                        };
                        let download_url = endpoint.as_ref().and_then(|endpoint| {
                            downloadable_url(endpoint, &indexer.api_key.0, &item, source_local)
                        });
                        results.push(SearchResult {
                            title,
                            size_bytes: item.enclosure_size.or(item.size).unwrap_or(0),
                            indexer_id: indexer.id,
                            indexer_name: Arc::clone(&indexer_name),
                            nzb_ref,
                            can_queue: download_url.is_some(),
                            download_url,
                            source_dns: None,
                        });
                    }
                }
                if channel == Some(depth) && e.local_name().as_ref().eq_ignore_ascii_case("channel")
                {
                    channel = None;
                }
            }
            Event::GeneralRef(e) => {
                let value = match e.as_ref() {
                    "amp" => "&".to_owned(),
                    "lt" => "<".to_owned(),
                    "gt" => ">".to_owned(),
                    "apos" => "'".to_owned(),
                    "quot" => "\"".to_owned(),
                    _ => e
                        .resolve_char_ref()
                        .map_err(|_| "Malformed indexer XML.")?
                        .map(|c| c.to_string())
                        .ok_or("Unsupported indexer XML.")?,
                };
                if let Some((at, tag)) = &field {
                    if depth == *at + 1 {
                        match tag.as_str() {
                            "title" => {
                                item.title_overflow |=
                                    !append_bounded(&mut item.title, &value, MAX_TITLE_BYTES);
                            }
                            "link" => {
                                item.link_overflow |=
                                    !append_bounded(&mut item.link, &value, MAX_LINK_BYTES);
                            }
                            "guid" => {
                                append_bounded(&mut item.guid, &value, MAX_GUID_BYTES);
                            }
                            _ => {}
                        }
                    }
                } else if depth == 0 {
                    return Err("Malformed indexer XML.");
                }
            }
            Event::DocType(_) => return Err("Unsupported indexer XML."),
            Event::Eof => {
                if !root || depth != 0 || channel.is_some() || current.is_some() {
                    return Err("Malformed indexer XML.");
                }
                return if found_channel {
                    Ok(results)
                } else {
                    Err("Indexer did not return RSS channel.")
                };
            }
            _ => {}
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::config::{Secret, Settings};
    use tokio::io::{AsyncReadExt, AsyncWriteExt};

    async fn fake(
        status: &str,
        body: &str,
        delay: Duration,
    ) -> (Indexer, tokio::task::JoinHandle<()>) {
        let listener = tokio::net::TcpListener::bind("127.0.0.1:0").await.unwrap();
        let base_url = format!("http://{}", listener.local_addr().unwrap());
        let response = format!(
            "HTTP/1.1 {status}\r\nContent-Length: {}\r\nConnection: close\r\n\r\n{body}",
            body.len()
        );
        let task = tokio::spawn(async move {
            let (mut socket, _) = listener.accept().await.unwrap();
            let mut request = vec![0; 4096];
            let n = socket.read(&mut request).await.unwrap();
            let request = std::str::from_utf8(&request[..n]).unwrap();
            assert!(request.starts_with("GET /api?t=caps&apikey=test-key "));
            assert!(request.lines().any(|line| {
                line.split_once(':').is_some_and(|(name, value)| {
                    name.eq_ignore_ascii_case("user-agent")
                        && value.trim() == crate::fetch::USER_AGENT
                })
            }));
            tokio::time::sleep(delay).await;
            let _ = socket.write_all(response.as_bytes()).await;
        });
        (
            Indexer {
                id: uuid::Uuid::now_v7(),
                name: "Mock".into(),
                base_url,
                api_key: Secret("test-key".into()),
                enabled: true,
            },
            task,
        )
    }

    #[test]
    fn parses_rss_xml_variants_without_exposing_credentials() {
        let indexer = Indexer {
            id: uuid::Uuid::now_v7(),
            name: "Mock".into(),
            base_url: "https://indexer.invalid/api".into(),
            api_key: Secret("test-key".into()),
            enabled: true,
        };
        let xml = r#"<r:rss xmlns:r="urn:rss" xmlns:nz="http://www.newznab.com/DTD/2010/feeds/attributes/"><r:channel>
          <r:item><r:title>A &amp; B</r:title><r:guid>one</r:guid><r:enclosure url="https://example.invalid/get?apikey=test-key&amp;id=1" length="123"/></r:item>
          <r:item><r:title><![CDATA[Title 2]]></r:title><r:link>https://example.invalid/two</r:link><nz:attr name="size" value="456"/></r:item>
          <r:item><r:title>Title 3</r:title><r:guid>third</r:guid><r:size>789</r:size></r:item>
          <r:item><r:title>test-key appears here</r:title><r:link>https://example.invalid/secret</r:link></r:item>
          <r:item><r:title>Missing reference</r:title></r:item>
          <r:item><r:guid>Missing title</r:guid></r:item>
          <r:item><r:title>Duplicate</r:title><r:guid>one</r:guid><r:enclosure url="https://example.invalid/get?apikey=test-key&amp;id=1"/></r:item>
        </r:channel></r:rss>"#;
        let results = parse_search(xml.as_bytes(), &indexer, false).unwrap();
        assert_eq!(results.len(), 4);
        assert_eq!(results[0].title, "A & B");
        assert_eq!(results[0].size_bytes, 123);
        assert_eq!(results[1].title, "Title 2");
        assert_eq!(results[1].size_bytes, 456);
        assert_eq!(results[2].size_bytes, 789);
        assert_eq!(results[3].title, "[redacted] appears here");
        assert!(results.iter().all(|result| result.indexer_id == indexer.id));
        let serialized = serde_json::to_string(&results).unwrap();
        assert!(!serialized.contains("test-key"));
        assert!(!serialized.contains("https://"));
        assert!(!serialized.contains("apikey"));
        assert!(
            results
                .iter()
                .all(|result| Uuid::parse_str(&result.nzb_ref).is_ok())
        );
        let again = parse_search(xml.as_bytes(), &indexer, false).unwrap();
        assert_ne!(results[0].nzb_ref, again[0].nzb_ref);
    }

    #[test]
    fn guid_only_and_blocked_urls_are_not_downloadable() {
        let indexer = Indexer {
            id: Uuid::now_v7(),
            name: "mock".into(),
            base_url: "http://127.0.0.1:8080/api".into(),
            api_key: Secret("mock-secret".into()),
            enabled: true,
        };
        let xml = r#"<rss><channel>
            <item><title>GUID-only</title><guid>https://host.invalid/get/123</guid></item>
            <item><title>Unsafe</title><enclosure url="http://169.254.169.254/latest" length="12"/></item>
            <item><title>File scheme</title><link>file:///etc/passwd</link></item>
            <item><title>Relative</title><link>/api?t=get&amp;id=5</link></item>
            <item><title>Valid</title><enclosure url="http://127.0.0.1:8080/nzb" length="20"/></item>
        </channel></rss>"#;
        let results = parse_search(xml.as_bytes(), &indexer, true).unwrap();
        assert_eq!(results.len(), 5);
        for result in &results[..3] {
            assert!(result.download_url.is_none());
        }
        assert_eq!(
            results[3].download_url.as_deref(),
            Some("http://127.0.0.1:8080/api?t=get&id=5&apikey=mock-secret")
        );
        assert_eq!(
            results[4].download_url.as_deref(),
            Some("http://127.0.0.1:8080/nzb")
        );
        let encoded = serde_json::to_string(&results).unwrap();
        assert!(!encoded.contains("download_url"));
        assert!(!encoded.contains("http://"));
        assert!(!encoded.contains("mock-secret"));
    }

    #[test]
    fn relative_newznab_links_only_get_credentials_on_the_same_origin() {
        let indexer = Indexer {
            id: Uuid::now_v7(),
            name: "Public".into(),
            base_url: "https://indexer.example/v2/api".into(),
            api_key: Secret("secret-abc".into()),
            enabled: true,
        };
        let xml = r#"<rss><channel>
            <item><title>Relative</title><enclosure url="?t=get&amp;id=42"/></item>
            <item><title>Root relative</title><link>/v2/api?t=get&amp;id=43</link></item>
            <item><title>Absolute same origin</title><link>https://indexer.example/v2/api?t=get&amp;id=44</link></item>
            <item><title>CDN</title><enclosure url="https://cdn.example/nzb/45?token=other"/></item>
            <item><title>Foreign secret</title><enclosure url="https://cdn.example/get?apikey=secret-abc"/></item>
            <item><title>Private</title><enclosure url="http://192.168.1.10/get"/></item>
            <item><title>Loopback</title><enclosure url="http://127.0.0.1:8989/get"/></item>
            <item><title>Guid-only</title><guid>1234</guid></item>
        </channel></rss>"#;
        let results = parse_search(xml.as_bytes(), &indexer, false).unwrap();
        assert_eq!(results.len(), 8);
        for (i, id) in ["42", "43", "44"].into_iter().enumerate() {
            assert!(results[i].can_queue);
            assert_eq!(
                results[i].download_url.as_deref(),
                Some(
                    format!("https://indexer.example/v2/api?t=get&id={id}&apikey=secret-abc")
                        .as_str()
                )
            );
        }
        assert_eq!(
            results[3].download_url.as_deref(),
            Some("https://cdn.example/nzb/45?token=other")
        );
        assert!(results[3].can_queue);
        for result in &results[4..] {
            assert!(!result.can_queue);
            assert!(result.download_url.is_none());
        }
        let wire = serde_json::to_string(&results).unwrap();
        assert!(!wire.contains("secret-abc"));
        assert!(!wire.contains("https://"));
        assert!(!wire.contains("download_url"));
        assert!(wire.contains("\"can_queue\":false"));
        assert!(wire.contains("\"can_queue\":true"));
    }

    #[test]
    fn rejects_invalid_search_xml_and_accepts_empty_channel() {
        let indexer = Indexer {
            id: uuid::Uuid::now_v7(),
            name: "Mock".into(),
            base_url: "https://indexer.invalid/api".into(),
            api_key: Secret("test-key".into()),
            enabled: true,
        };
        for xml in [
            "",
            "<rss><channel><item></channel></rss>",
            "<error code=\"100\" description=\"test-key\"/>",
            "<error code=\"999\" description=\"test-key\"/>",
            "<caps/>",
            "<rss/>",
            "<!DOCTYPE rss><rss><channel/></rss>",
            "<rss><channel/></rss><rss><channel/></rss>",
        ] {
            assert!(
                parse_search(xml.as_bytes(), &indexer, false).is_err(),
                "{xml}"
            );
            assert!(
                !parse_search(xml.as_bytes(), &indexer, false)
                    .unwrap_err()
                    .contains("test-key")
            );
        }
        assert!(
            parse_search(b"<rss><channel/></rss>", &indexer, false)
                .unwrap()
                .is_empty()
        );
        assert!(
            parse_search(b"<rss xmlns=\"urn:rss\"><channel/></rss>", &indexer, false)
                .unwrap()
                .is_empty()
        );
        assert_eq!(
            parse_search(
                b"<error code=\"100\" description=\"test-key\"/>",
                &indexer,
                false
            )
            .unwrap_err(),
            "Indexer authentication failed."
        );
    }

    async fn mock_search(
        status: &str,
        body: &str,
        delay: Duration,
    ) -> (Indexer, tokio::task::JoinHandle<()>) {
        let listener = tokio::net::TcpListener::bind("127.0.0.1:0").await.unwrap();
        let base_url = format!(
            "http://{}/proxy?source=mock&t=caps&limit=999",
            listener.local_addr().unwrap()
        );
        let response = format!(
            "HTTP/1.1 {status}\r\nContent-Length: {}\r\nConnection: close\r\n\r\n{body}",
            body.len()
        );
        let task = tokio::spawn(async move {
            let (mut socket, _) = listener.accept().await.unwrap();
            let mut request = vec![0; 4096];
            let count = socket.read(&mut request).await.unwrap();
            let request = std::str::from_utf8(&request[..count]).unwrap();
            assert!(request.contains(
                "/proxy/api?source=mock&t=search&q=two+words&limit=12&extended=1&apikey=test-key"
            ));
            assert!(
                request
                    .lines()
                    .any(|line| line.to_ascii_lowercase().starts_with("user-agent: "))
            );
            tokio::time::sleep(delay).await;
            let _ = socket.write_all(response.as_bytes()).await;
        });
        (
            Indexer {
                id: uuid::Uuid::now_v7(),
                name: "Mock".into(),
                base_url,
                api_key: Secret("test-key".into()),
                enabled: true,
            },
            task,
        )
    }

    #[tokio::test]
    async fn local_search_keeps_verified_dns_private_and_server_side() {
        let rss = "<rss><channel><item><title>Result</title><link>?t=get&amp;id=4</link></item></channel></rss>";
        let (indexer, task) = mock_search("200 OK", rss, Duration::ZERO).await;
        let results = search(&indexer, "two words", 12).await.unwrap();
        task.await.unwrap();
        assert_eq!(results.len(), 1);
        let pinned = results[0].source_dns.as_ref().unwrap();
        assert!(pinned.is_local());
        assert!(results[0].can_queue);
        let response = serde_json::to_string(&results).unwrap();
        assert!(!response.contains("source_dns"));
        assert!(!response.contains("127.0.0.1"));
        assert!(!response.contains("api_key"));
        assert!(!response.contains("test-key"));
    }

    #[tokio::test]
    async fn search_limit_is_enforced_even_when_indexer_returns_more() {
        use std::fmt::Write as _;
        let mut rss = String::from("<rss><channel>");
        for n in 0..35 {
            write!(
                rss,
                "<item><title>Result {n}</title><link>https://example.invalid/{n}</link></item>"
            )
            .unwrap();
        }
        rss.push_str("</channel></rss>");
        let (indexer, task) = mock_search("200 OK", &rss, Duration::ZERO).await;
        let results = search(&indexer, "two words", 12).await.unwrap();
        task.await.unwrap();
        assert_eq!(results.len(), 12);
    }

    #[test]
    fn search_fields_remain_bounded() {
        let indexer = Indexer {
            id: Uuid::now_v7(),
            name: "mock".into(),
            base_url: "https://indexer.invalid/api".into(),
            api_key: Secret("test-key".into()),
            enabled: true,
        };
        let rss = format!(
            "<rss><channel><item><title>{}</title><link>https://example.invalid/{}</link></item></channel></rss>",
            "x".repeat(16_000),
            "y".repeat(16_000)
        );
        let results = parse_search(rss.as_bytes(), &indexer, false).unwrap();
        assert!(results[0].title.len() <= 512);
        assert!(
            results[0]
                .download_url
                .as_ref()
                .is_none_or(|url| url.len() <= 2048)
        );
    }

    #[test]
    fn truncated_fields_do_not_expose_partial_credentials_or_queue_broken_links() {
        let indexer = Indexer {
            id: Uuid::new_v4(),
            name: "Mock".into(),
            base_url: "https://indexer.invalid/api".into(),
            api_key: Secret("test-key".into()),
            enabled: true,
        };
        let prefix = "https://example.invalid/nzb/";
        let link = format!(
            "{}{}test-key",
            prefix,
            "x".repeat(MAX_LINK_BYTES - prefix.len() - 3)
        );
        let xml = format!(
            "<rss><channel><item><title>{}test-key</title><link>{link}</link></item></channel></rss>",
            "x".repeat(MAX_TITLE_BYTES - 3)
        );
        let results = parse_search(xml.as_bytes(), &indexer, false).unwrap();
        assert_eq!(results.len(), 1);
        assert_eq!(results[0].title, "[redacted]");
        assert!(!results[0].can_queue);
        assert!(results[0].download_url.is_none());
    }

    #[tokio::test]
    async fn search_http_bounded_and_isolates_errors() {
        let valid = "<rss><channel><item><title>One</title><link>https://example.invalid/1</link></item></channel></rss>";
        for (status, body, expected) in [
            ("200 OK", valid, None),
            ("200 OK", "<rss><channel/></rss>", None),
            ("200 OK", "", Some("Malformed indexer XML.")),
            (
                "200 OK",
                "<rss><channel><item></rss>",
                Some("Malformed indexer XML."),
            ),
            (
                "200 OK",
                "<error code=\"100\" description=\"test-key\"/>",
                Some("Indexer authentication failed."),
            ),
            (
                "401 Unauthorized",
                "test-key",
                Some("Indexer authentication failed."),
            ),
            (
                "403 Forbidden",
                "test-key",
                Some("Indexer authentication failed."),
            ),
            (
                "503 Service Unavailable",
                "test-key",
                Some("Indexer returned an unsuccessful HTTP status."),
            ),
            (
                "302 Found",
                "test-key",
                Some("Indexer returned an unsuccessful HTTP status."),
            ),
        ] {
            let (indexer, task) = mock_search(status, body, Duration::ZERO).await;
            let result = search(&indexer, "two words", 12).await;
            assert_eq!(result.as_ref().err().copied(), expected, "{status}");
            task.await.unwrap();
        }
        let big = "x".repeat(MAX_BODY + 1);
        let (indexer, task) = mock_search("200 OK", &big, Duration::ZERO).await;
        assert_eq!(
            search(&indexer, "two words", 12).await.unwrap_err(),
            "Indexer response exceeds 256 KB."
        );
        task.await.unwrap();

        let (indexer, task) = mock_search("200 OK", valid, Duration::from_secs(30)).await;
        assert_eq!(
            search_with_timeout(&indexer, "two words", 12, Duration::from_millis(100))
                .await
                .unwrap_err(),
            "Indexer search timed out."
        );
        task.abort();
    }

    #[tokio::test]
    async fn sends_shared_user_agent() {
        let (indexer, task) = fake("200 OK", "<caps/>", Duration::ZERO).await;
        assert_eq!(test(&indexer).await, Ok(()));
        task.await.unwrap();
    }

    #[tokio::test]
    async fn caps_rejects_same_unsafe_address_as_search() {
        let (mut indexer, task) = fake("200 OK", "<caps/>", Duration::ZERO).await;
        let mut url = Url::parse(&indexer.base_url).unwrap();
        url.set_host(Some("0.0.0.0")).unwrap();
        indexer.base_url = url.into();

        let search_error = search(&indexer, "two words", 12).await.unwrap_err();
        let caps = test(&indexer).await;
        task.abort();
        assert_eq!(search_error, "Could not resolve indexer.");
        assert_eq!(caps, Err("Could not resolve indexer."));
    }

    #[tokio::test]
    async fn local_caps_responses() {
        for (status, body, expected) in [
            (
                "200 OK",
                "<?xml version=\"1.0\"?><caps><server version=\"1\"/></caps>",
                Ok(()),
            ),
            ("200 OK", "<caps/>", Ok(())),
            ("200 OK", "<CAPS><server/></CAPS>", Ok(())),
            (
                "200 OK",
                "<nz:caps xmlns:nz=\"urn:newznab\"><nz:server/></nz:caps>",
                Ok(()),
            ),
            ("200 OK", "<response><caps/></response>", Ok(())),
            (
                "200 OK",
                "<rss><channel/></rss>",
                Err("Indexer did not return Newznab caps."),
            ),
            (
                "401 Unauthorized",
                "test-key",
                Err("Indexer authentication failed."),
            ),
            (
                "200 OK",
                "<error code=\"100\" description=\"test-key\"/>",
                Err("Indexer authentication failed."),
            ),
            (
                "200 OK",
                "<error code=\"200\" description=\"test-key\"/>",
                Err("Indexer rejected the API request."),
            ),
            (
                "200 OK",
                "<caps><server></caps>",
                Err("Malformed indexer XML."),
            ),
            (
                "302 Found",
                "test-key",
                Err("Indexer returned an unsuccessful HTTP status."),
            ),
        ] {
            let (indexer, task) = fake(status, body, Duration::ZERO).await;
            assert_eq!(test(&indexer).await, expected);
            task.await.unwrap();
        }
    }

    #[tokio::test]
    async fn bounds_and_timeout() {
        let (indexer, task) = fake("200 OK", &"x".repeat(MAX_BODY + 1), Duration::ZERO).await;
        assert_eq!(
            test(&indexer).await,
            Err("Indexer response exceeds 256 KB.")
        );
        task.await.unwrap();
        let (indexer, task) = fake("200 OK", "<caps/>", Duration::from_secs(60)).await;
        assert_eq!(
            check(&indexer, Duration::from_millis(100)).await,
            Err("Indexer connection test timed out.")
        );
        task.abort();
    }

    #[test]
    fn base_url_rejects_embedded_credentials() {
        for base_url in [
            "https://indexer.invalid/proxy/fake%20key/api",
            "https://indexer.invalid/proxy/fake%252520key",
            "https://indexer.invalid/api?source=prefix-fake+key-suffix",
            "https://indexer.invalid/api?t=fake%2520key",
        ] {
            let indexer = Indexer {
                id: Uuid::now_v7(),
                name: "Example".into(),
                base_url: base_url.into(),
                api_key: Secret("fake key".into()),
                enabled: true,
            };
            assert!(indexer.validate().is_err(), "{base_url}");
            assert!(!format!("{indexer:?}").contains(base_url));
            let settings = Settings {
                indexers: vec![indexer],
                ..Settings::default()
            };
            assert_eq!(settings.masked().indexers[0].base_url, crate::config::MASK);
        }
    }

    #[test]
    fn settings_and_url_security() {
        let mut settings = Settings {
            indexers: vec![Indexer {
                id: uuid::Uuid::now_v7(),
                name: "Example".into(),
                base_url: "https://indexer.invalid/base/".into(),
                api_key: Secret("test-key".into()),
                enabled: true,
            }],
            ..Settings::default()
        };
        assert!(!format!("{settings:?}").contains("test-key"));
        assert_eq!(settings.masked().indexers[0].api_key.0, crate::config::MASK);
        for (base, target) in [
            ("https://indexer.invalid", "/api?t=caps&apikey=test-key"),
            ("https://indexer.invalid/api", "/api?t=caps&apikey=test-key"),
            (
                "https://indexer.invalid/API/",
                "/API?t=caps&apikey=test-key",
            ),
            (
                "https://indexer.invalid/base/",
                "/base/api?t=caps&apikey=test-key",
            ),
            (
                "https://indexer.invalid/base/api/?source=proxy",
                "/base/api?source=proxy&t=caps&apikey=test-key",
            ),
            (
                "https://indexer.invalid/proxy?t=search&source=proxy",
                "/proxy/api?source=proxy&t=caps&apikey=test-key",
            ),
        ] {
            settings.indexers[0].base_url = base.into();
            let url = endpoint(&settings.indexers[0]).unwrap();
            assert_eq!(format!("{}?{}", url.path(), url.query().unwrap()), target);
        }
        for url in [
            "file:///tmp/test",
            "http://169.254.169.254",
            "http://[::ffff:169.254.169.254]",
            "https://user:pass@indexer.invalid",
            "https://indexer.invalid?apikey=secret",
            "https://indexer.invalid/#secret",
        ] {
            settings.indexers[0].base_url = url.into();
            assert!(endpoint(&settings.indexers[0]).is_err());
        }
    }

    #[test]
    fn a_full_search_reuses_a_single_indexer_name_allocation() {
        use std::fmt::Write as _;
        let indexer = Indexer {
            id: Uuid::new_v4(),
            name: "N".repeat(128),
            base_url: "https://indexer.invalid/api".into(),
            api_key: Secret("secret".into()),
            enabled: true,
        };
        let mut xml = String::from("<rss><channel>");
        for n in 0..450 {
            write!(
                xml,
                "<item><title>Result {n}</title><link>https://example.invalid/nzb/{n}</link></item>"
            )
            .unwrap();
        }
        xml.push_str("</channel></rss>");
        let results = parse_search(xml.as_bytes(), &indexer, false).unwrap();
        assert_eq!(results.len(), 450);
        for result in &results {
            assert!(Arc::ptr_eq(&results[0].indexer_name, &result.indexer_name));
        }
        let wire = serde_json::to_string(&results).unwrap();
        assert!(wire.contains(&indexer.name));
    }
}
