//! An Arr's download queue: reading it, judging its import messages, and removing
//! entries (`infinidysk-lab:docs/import-policy.md` §10.3).

use reqwest::{Method, RequestBuilder};
use serde_json::Value;

use crate::{Api, Arr, Error, Profile, Session, fetch, request, send};

const PAGE: usize = 200;
/// ponytail: queues past 10,000 entries are read only in part; page further if one shows up.
const MAX_PAGES: usize = 50;
/// The header over per-file rejections when only part of a release imported; the
/// rejections follow as their own entries.
const PARTIAL: &str = "expected in this release were not imported or missing";

/// One queue entry. On Sonarr's v3 API a multi-episode download lists one entry per
/// episode; on v5 one entry lists them all.
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct Entry {
    pub id: i64,
    pub title: String,
    pub download_id: String,
    /// Name of the download client holding it.
    pub client: String,
    /// The client's status, e.g. `completed`.
    pub status: String,
    /// The Arr's tracking state, e.g. `importBlocked`.
    pub state: String,
    pub messages: Vec<String>,
    /// The media it stands for, as the search limit counts it.
    pub media: Vec<(&'static str, i64)>,
}

/// What to do with an entry whose import is stuck; the stronger one wins.
#[derive(Clone, Copy, Debug, PartialEq, Eq, PartialOrd, Ord)]
pub enum Action {
    /// Remove it from the queue.
    Remove,
    /// Remove it, blocklist the release with this reason and search for another.
    Replace(&'static str),
}

const REMOVE: &[&str] = &[
    "already imported at",
    "has already been imported",
    "not an upgrade for existing",
    "not a custom format upgrade",
    "not a quality revision upgrade",
];
/// Each with the reason the Arr's blocklist shows; it carries no paths or names.
const REPLACE: &[(&str, &str)] = &[
    (
        "unable to determine if file is a sample",
        "Import blocked: could not check for a sample",
    ),
    (
        "no files found are eligible for import in",
        "Import blocked: no eligible files",
    ),
    (
        "unsupported extension",
        "Import blocked: unsupported file type",
    ),
    (
        "no audio tracks detected",
        "Import blocked: no audio tracks",
    ),
    (
        "found archive file, might need to be extracted",
        "Import blocked: archive not extracted",
    ),
    (
        "unable to parse file",
        "Import blocked: file name not recognized",
    ),
];

/// The action every message agrees on, or `None` when any message has no rule (or there
/// are none), which leaves the entry to the user.
#[must_use]
pub fn action(messages: &[String]) -> Option<Action> {
    let rule = |message: &String| {
        let lower = message.to_lowercase();
        if lower.trim() == "sample" {
            Some(Action::Replace("Import blocked: sample"))
        } else if let Some((_, reason)) = REPLACE.iter().find(|(p, _)| lower.contains(p)) {
            Some(Action::Replace(reason))
        } else if REMOVE.iter().any(|p| lower.contains(p)) {
            Some(Action::Remove)
        } else {
            None
        }
    };
    messages
        .iter()
        .map(rule)
        .collect::<Option<Vec<_>>>()?
        .into_iter()
        .max()
}

/// Every entry in the queue, unknown media included.
pub async fn queue(http: &reqwest::Client, arr: &Arr) -> Result<Vec<Entry>, Error> {
    let s = Session::open(http, arr).await?;
    let mut entries = Vec::new();
    for page in 1..=MAX_PAGES {
        let body: Value = fetch(call(&s, Method::GET, "queue").query(&[
            ("page", page.to_string().as_str()),
            ("pageSize", PAGE.to_string().as_str()),
            ("includeUnknownSeriesItems", "true"),
            ("includeUnknownMovieItems", "true"),
            ("includeUnknownArtistItems", "true"),
            ("includeUnknownAuthorItems", "true"),
        ]))
        .await?;
        let records = body["records"]
            .as_array()
            .ok_or(Error::Unexpected("queue without records"))?;
        entries.extend(records.iter().filter_map(entry));
        if records.len() < PAGE {
            break;
        }
    }
    Ok(entries)
}

/// Removes entry `id` and the download from our client (which keeps its content). With a
/// `blocklist` reason it blocklists the release and, if `search` yields `true`, lets the
/// Arr search for another. `search` runs only once the Arr is reached and with a reason,
/// so a failed lookup spends no search budget. Returns whether it asked for a search.
pub async fn remove(
    http: &reqwest::Client,
    arr: &Arr,
    id: i64,
    blocklist: Option<&str>,
    search: impl Future<Output = bool>,
) -> Result<bool, Error> {
    let s = Session::open(http, arr).await?;
    let search = blocklist.is_some() && search.await;
    let request = call(&s, Method::DELETE, &format!("queue/{id}"));
    let request = match (s.api, blocklist) {
        (Api::Classic, _) => request.query(&[
            ("removeFromClient", "true"),
            (
                "blocklist",
                if blocklist.is_some() { "true" } else { "false" },
            ),
            ("skipRedownload", if search { "false" } else { "true" }),
        ]),
        (Api::V5, None) => request.query(&[
            ("removalMethod", "removeFromClient"),
            ("blocklistMethod", "doNotBlocklist"),
        ]),
        (Api::V5, Some(reason)) => request.query(&[
            ("removalMethod", "removeFromClient"),
            (
                "blocklistMethod",
                if search {
                    "blocklistAndSearch"
                } else {
                    "blocklistOnly"
                },
            ),
            ("message", reason),
        ]),
    };
    send(request).await?;
    Ok(search)
}

/// Sportarr's queue lives in its Sonarr-compatible API, not its native one.
fn call(s: &Session<'_>, method: Method, path: &str) -> RequestBuilder {
    match s.arr.profile {
        Profile::Sportarr => request(s.http, s.arr, method, &format!("api/v3/{path}")),
        _ => s.call(method, path),
    }
}

fn entry(record: &Value) -> Option<Entry> {
    let text = |key: &str| record[key].as_str().unwrap_or_default().to_owned();
    let messages = record["statusMessages"]
        .as_array()
        .into_iter()
        .flatten()
        .flat_map(|status| {
            let title = status["title"].as_str().unwrap_or_default();
            let messages: Vec<String> = status["messages"]
                .as_array()
                .into_iter()
                .flatten()
                .filter_map(|m| m.as_str().map(str::to_owned))
                .collect();
            if !messages.is_empty() {
                messages
            } else if title.is_empty() || title.to_lowercase().contains(PARTIAL) {
                Vec::new()
            } else {
                vec![title.to_owned()]
            }
        })
        .collect();
    let id = |key: &str| record[key].as_i64().filter(|&id| id > 0);
    let media = match record["episodeIds"].as_array() {
        Some(ids) => ids
            .iter()
            .filter_map(|id| id.as_i64().filter(|&id| id > 0))
            .map(|id| ("episode", id))
            .collect(),
        None => [
            ("episodeId", "episode"),
            ("movieId", "movie"),
            ("albumId", "album"),
            ("bookId", "book"),
        ]
        .into_iter()
        .find_map(|(key, kind)| Some((kind, id(key)?)))
        .into_iter()
        .collect(),
    };
    Some(Entry {
        id: record["id"].as_i64()?,
        title: text("title"),
        download_id: record["downloadId"].as_str()?.to_owned(),
        client: text("downloadClient"),
        status: text("status"),
        state: text("trackedDownloadState"),
        messages,
        media,
    })
}

#[cfg(test)]
mod tests {
    use serde_json::json;

    use super::*;

    #[test]
    fn judges_messages() {
        let judge =
            |messages: &[&str]| action(&messages.iter().map(|&m| m.to_owned()).collect::<Vec<_>>());
        assert_eq!(
            judge(&["Sample"]),
            Some(Action::Replace("Import blocked: sample"))
        );
        assert_eq!(judge(&["Sample rate too low"]), None);
        assert_eq!(
            judge(&["Not an upgrade for existing episode file(s)"]),
            Some(Action::Remove)
        );
        assert_eq!(
            judge(&["Has already been imported", "Unable to parse file"]),
            Some(Action::Replace("Import blocked: file name not recognized"))
        );
        assert_eq!(
            judge(&["Has already been imported", "Unknown Series"]),
            None
        );
        assert_eq!(judge(&[]), None);

        let record = json!({
            "id": 7, "downloadId": "abc", "downloadClient": "sab", "status": "completed",
            "trackedDownloadState": "importBlocked", "episodeId": 3,
            "statusMessages": [
                { "title": "One or more episodes expected in this release were not imported or missing from the release", "messages": [] },
                { "title": "file.mkv", "messages": ["Sample"] },
                { "title": "No files found are eligible for import in /x", "messages": [] },
            ],
        });
        let entry = entry(&record).unwrap();
        assert_eq!(
            entry.messages,
            ["Sample", "No files found are eligible for import in /x"]
        );
        assert_eq!(entry.media, [("episode", 3)]);

        let pack = json!({ "id": 8, "downloadId": "abc", "episodeIds": [3, 4, 0] });
        assert_eq!(
            super::entry(&pack).unwrap().media,
            [("episode", 3), ("episode", 4)]
        );
    }
}
