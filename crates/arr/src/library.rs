//! What a library conversion asks of Sonarr and Radarr
//! (`infinidysk-lab:docs/strm-support-proposal.md` §4.5): rescan one series or movie, then give
//! the files it re-imported back the details and monitoring the rescan reset. Removing a dead
//! library link (`infinidysk-lab:docs/maintenance-policy.md`) deletes the file record it was
//! and searches the media again.

use reqwest::Method;
use serde::{Deserialize, Serialize};
use serde_json::{Map, Value, json};

use crate::{Arr, Error, Profile, Session, fetch, send};

/// File fields a rescan resets and the bulk editor takes back.
const DETAILS: [&str; 7] = [
    "quality",
    "languages",
    "releaseGroup",
    "sceneName",
    "indexerFlags",
    "releaseType",
    "edition",
];

/// A series or movie.
#[derive(Clone, Copy, Debug, PartialEq, Eq, Hash, PartialOrd, Ord, Serialize, Deserialize)]
pub enum Media {
    Series(i64),
    Movie(i64),
}

/// A library file with what a rescan would reset.
#[derive(Clone, Debug, PartialEq)]
pub struct MediaFile {
    pub id: i64,
    pub path: String,
    pub media: Media,
    pub details: Value,
}

/// Whether the instance takes part in conversions; others only get their paths updated.
#[must_use]
pub const fn converts(arr: &Arr) -> bool {
    matches!(arr.profile, Profile::Sonarr | Profile::Radarr)
}

/// Every library file of a Sonarr or Radarr instance.
pub async fn media_files(http: &reqwest::Client, arr: &Arr) -> Result<Vec<MediaFile>, Error> {
    let (list, kind): (&str, fn(i64) -> Media) = match arr.profile {
        Profile::Sonarr => ("series", Media::Series),
        Profile::Radarr => ("movie", Media::Movie),
        _ => return Ok(Vec::new()),
    };
    let s = Session::open(http, arr).await?;
    let mut files = Vec::new();
    for media in fetch::<Vec<Value>>(s.library_list(list)).await? {
        let id = media["id"]
            .as_i64()
            .ok_or(Error::Unexpected("library entry without id"))?;
        files.extend(files_in(&s, kind(id)).await?);
    }
    Ok(files)
}

/// The library files of one series or movie.
pub async fn files_of(
    http: &reqwest::Client,
    arr: &Arr,
    media: Media,
) -> Result<Vec<MediaFile>, Error> {
    files_in(&Session::open(http, arr).await?, media).await
}

async fn files_in(s: &Session<'_>, media: Media) -> Result<Vec<MediaFile>, Error> {
    let request = match media {
        Media::Series(id) => s
            .call(Method::GET, "episodefile")
            .query(&[("seriesId", id)]),
        Media::Movie(id) => s.call(Method::GET, "moviefile").query(&[("movieId", id)]),
    };
    fetch::<Vec<Value>>(request)
        .await?
        .iter()
        .map(|f| {
            Ok(MediaFile {
                id: f["id"]
                    .as_i64()
                    .ok_or(Error::Unexpected("library file without id"))?,
                path: f["path"]
                    .as_str()
                    .ok_or(Error::Unexpected("library file without path"))?
                    .to_owned(),
                media,
                details: Value::Object(
                    DETAILS
                        .iter()
                        .filter_map(|k| Some(((*k).to_owned(), f.get(*k)?.clone())))
                        .collect::<Map<_, _>>(),
                ),
            })
        })
        .collect()
}

/// Monitored episodes of a series, or the movie when monitored, as `(id, file id)`.
pub async fn monitored(
    http: &reqwest::Client,
    arr: &Arr,
    media: Media,
) -> Result<Vec<(i64, i64)>, Error> {
    let s = Session::open(http, arr).await?;
    let entries = match media {
        Media::Series(id) => {
            fetch::<Vec<Value>>(s.call(Method::GET, "episode").query(&[("seriesId", id)])).await?
        }
        Media::Movie(id) => {
            vec![fetch::<Value>(s.call(Method::GET, &format!("movie/{id}"))).await?]
        }
    };
    let file = match media {
        Media::Series(_) => "episodeFileId",
        Media::Movie(_) => "movieFileId",
    };
    Ok(entries
        .iter()
        .filter(|e| e["monitored"].as_bool() == Some(true))
        .filter_map(|e| Some((e["id"].as_i64()?, e[file].as_i64().filter(|f| *f > 0)?)))
        .collect())
}

/// Asks the Arr to rescan the files of `media`; returns the command id.
pub async fn rescan(http: &reqwest::Client, arr: &Arr, media: Media) -> Result<i64, Error> {
    let body = match media {
        Media::Series(id) => json!({ "name": "RescanSeries", "seriesId": id }),
        Media::Movie(id) => json!({ "name": "RescanMovie", "movieId": id }),
    };
    let s = Session::open(http, arr).await?;
    let command: Value = fetch(s.call(Method::POST, "command").json(&body)).await?;
    command["id"]
        .as_i64()
        .ok_or(Error::Unexpected("command without id"))
}

/// `None` while command `id` runs, then whether it completed.
pub async fn command_done(
    http: &reqwest::Client,
    arr: &Arr,
    id: i64,
) -> Result<Option<bool>, Error> {
    let s = Session::open(http, arr).await?;
    let command: Value = fetch(s.call(Method::GET, &format!("command/{id}"))).await?;
    Ok(match command["status"].as_str().unwrap_or_default() {
        "queued" | "started" => None,
        status => Some(status == "completed"),
    })
}

/// Gives file `id` of `media` back the details recorded from the file it replaced.
pub async fn restore(
    http: &reqwest::Client,
    arr: &Arr,
    media: Media,
    id: i64,
    details: &Value,
) -> Result<(), Error> {
    let mut file = details.as_object().cloned().unwrap_or_default();
    file.insert("id".to_owned(), id.into());
    let path = match media {
        Media::Series(_) => "episodefile/bulk",
        Media::Movie(_) => "moviefile/bulk",
    };
    send(
        Session::open(http, arr)
            .await?
            .call(Method::PUT, path)
            .json(&[file]),
    )
    .await
}

/// Monitors the episodes or movie `ids` of `media` again.
pub async fn monitor(
    http: &reqwest::Client,
    arr: &Arr,
    media: Media,
    ids: &[i64],
) -> Result<(), Error> {
    if ids.is_empty() {
        return Ok(());
    }
    let s = Session::open(http, arr).await?;
    let request = match media {
        Media::Series(_) => s
            .call(Method::PUT, "episode/monitor")
            .json(&json!({ "episodeIds": ids, "monitored": true })),
        Media::Movie(_) => s
            .call(Method::PUT, "movie/editor")
            .json(&json!({ "movieIds": ids, "monitored": true })),
    };
    send(request).await
}

/// Deletes the record of `file` if the Arr still has it at the same path, then searches
/// the episodes or movie `ids`; whether it deleted it.
pub async fn replace_file(
    http: &reqwest::Client,
    arr: &Arr,
    file: &MediaFile,
    ids: &[i64],
) -> Result<bool, Error> {
    let (kind, search) = match file.media {
        Media::Series(_) => (
            "episodefile",
            json!({ "name": "EpisodeSearch", "episodeIds": ids }),
        ),
        Media::Movie(_) => (
            "moviefile",
            json!({ "name": "MoviesSearch", "movieIds": ids }),
        ),
    };
    let s = Session::open(http, arr).await?;
    let at = format!("{kind}/{}", file.id);
    let current: Value = match fetch(s.call(Method::GET, &at)).await {
        Err(Error::Rejected {
            status: reqwest::StatusCode::NOT_FOUND,
            ..
        }) => {
            return Ok(false);
        }
        other => other?,
    };
    if current["path"].as_str() != Some(file.path.as_str()) {
        return Ok(false);
    }
    send(s.call(Method::DELETE, &at)).await?;
    if !ids.is_empty() {
        send(s.call(Method::POST, "command").json(&search)).await?;
    }
    Ok(true)
}

/// The instance's root folders.
pub async fn root_folders(http: &reqwest::Client, arr: &Arr) -> Result<Vec<String>, Error> {
    let s = Session::open(http, arr).await?;
    let roots: Vec<Value> = fetch(s.call(Method::GET, "rootfolder")).await?;
    Ok(roots
        .iter()
        .filter_map(|r| r["path"].as_str())
        .map(|p| p.trim_end_matches('/').to_owned())
        .collect())
}
