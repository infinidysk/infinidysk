//! An Arr's download queue: reading it, judging its import messages, and removing
//! entries (`infinidysk-lab:docs/import-policy.md` §10.3).

use reqwest::Method;
use serde_json::Value;

use crate::{Arr, Error, Profile, fetch, request, send};

const PAGE: usize = 200;
/// ponytail: queues past 10,000 entries are read only in part; page further if one shows up.
const MAX_PAGES: usize = 50;
/// The header over per-file rejections when only part of a release imported; the
/// rejections follow as their own entries.
const PARTIAL: &str = "expected in this release were not imported or missing";

/// One queue entry. A multi-episode download lists one entry per episode.
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
    pub media: Option<(&'static str, i64)>,
}

/// What to do with an entry whose import is stuck; the stronger one wins.
#[derive(Clone, Copy, Debug, PartialEq, Eq, PartialOrd, Ord)]
pub enum Action {
    /// Remove it from the queue.
    Remove,
    /// Remove it, blocklist the release and search for another.
    Replace,
}

const REMOVE: &[&str] = &[
    "already imported at",
    "has already been imported",
    "not an upgrade for existing",
    "not a custom format upgrade",
    "not a quality revision upgrade",
];
const REPLACE: &[&str] = &[
    "unable to determine if file is a sample",
    "no files found are eligible for import in",
    "unsupported extension",
    "no audio tracks detected",
    "found archive file, might need to be extracted",
    "unable to parse file",
];

/// The action every message agrees on, or `None` when any message has no rule (or there
/// are none), which leaves the entry to the user.
#[must_use]
pub fn action(messages: &[String]) -> Option<Action> {
    let rule = |message: &String| {
        let lower = message.to_lowercase();
        if lower.trim() == "sample" || REPLACE.iter().any(|p| lower.contains(p)) {
            Some(Action::Replace)
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
    let path = format!("{}/queue", root(arr.profile));
    let mut entries = Vec::new();
    for page in 1..=MAX_PAGES {
        let body: Value = fetch(request(http, arr, Method::GET, &path).query(&[
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

/// Removes entry `id` and the download from our client (which keeps its content),
/// optionally blocklisting the release and letting the Arr search for another.
pub async fn remove(
    http: &reqwest::Client,
    arr: &Arr,
    id: i64,
    blocklist: bool,
    search: bool,
) -> Result<(), Error> {
    let path = format!("{}/queue/{id}", root(arr.profile));
    send(request(http, arr, Method::DELETE, &path).query(&[
        ("removeFromClient", "true"),
        ("blocklist", if blocklist { "true" } else { "false" }),
        ("skipRedownload", if search { "false" } else { "true" }),
    ]))
    .await
}

/// Sportarr's queue lives in its Sonarr-compatible API, not its native one.
const fn root(profile: Profile) -> &'static str {
    match profile {
        Profile::Sportarr => "api/v3",
        profile => profile.root(),
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
    let media = [
        ("episodeId", "episode"),
        ("movieId", "movie"),
        ("albumId", "album"),
        ("bookId", "book"),
    ]
    .into_iter()
    .find_map(|(key, kind)| {
        record[key]
            .as_i64()
            .filter(|&id| id > 0)
            .map(|id| (kind, id))
    });
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
        assert_eq!(judge(&["Sample"]), Some(Action::Replace));
        assert_eq!(judge(&["Sample rate too low"]), None);
        assert_eq!(
            judge(&["Not an upgrade for existing episode file(s)"]),
            Some(Action::Remove)
        );
        assert_eq!(
            judge(&["Has already been imported", "Unable to parse file"]),
            Some(Action::Replace)
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
        assert_eq!(entry.media, Some(("episode", 3)));
    }
}
