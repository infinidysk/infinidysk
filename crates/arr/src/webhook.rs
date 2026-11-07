//! Webhooks (import policy §10.4): registering ours on an Arr, and the few fields of a
//! delivery we act on. Deliveries are read by field name, the same way for every profile.

use reqwest::Method;
use serde_json::{Value, json};

use crate::{Api, Arr, Error, Profile, Session, fetch, send};

/// A library file an Arr reports.
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct File {
    /// Absent where the Arr sends none (Sportarr, Radarr's replaced files).
    pub id: Option<i64>,
    pub path: String,
    /// Where it was imported from, when the Arr says.
    pub source: Option<String>,
}

#[derive(Debug, PartialEq, Eq)]
pub enum Event {
    Test,
    /// `files` were imported from download `download_id`, replacing `replaced`.
    Imported {
        download_id: Option<String>,
        files: Vec<File>,
        replaced: Vec<File>,
    },
    /// Library files were deleted.
    Deleted(Vec<File>),
    /// A series/movie/artist/author folder was deleted with its files.
    FolderDeleted(String),
    /// Files were deleted without their paths (Lidarr's album delete); only a
    /// reconciliation with the library can tell which.
    Reconcile,
    /// Files moved: each at its new path, with its previous one.
    Renamed(Vec<(File, String)>),
    /// Nothing we act on.
    Ignored,
}

/// Reads a delivery.
pub fn parse(body: &[u8]) -> Result<Event, serde_json::Error> {
    let v: Value = serde_json::from_slice(body)?;
    let files = |keys: &[&str]| -> Vec<File> {
        keys.iter()
            .flat_map(|k| match &v[*k] {
                Value::Array(list) => list.iter().filter_map(file).collect::<Vec<_>>(),
                one => file(one).into_iter().collect(),
            })
            .collect()
    };
    Ok(match v["eventType"].as_str().unwrap_or_default() {
        "Test" => Event::Test,
        "Download" => Event::Imported {
            download_id: v["downloadId"]
                .as_str()
                .filter(|id| !id.is_empty())
                .map(str::to_owned),
            files: files(&[
                "episodeFile",
                "episodeFiles",
                "movieFile",
                "trackFiles",
                "bookFiles",
            ]),
            replaced: files(&["deletedFiles"]),
        },
        // The file may be back after a mount outage; daily reconciliation catches real removals.
        kind if kind.ends_with("FileDelete")
            && v["deleteReason"]
                .as_str()
                .is_some_and(|r| r.eq_ignore_ascii_case("missingFromDisk")) =>
        {
            Event::Ignored
        }
        // Chaptarr sends no reason, so the file may only be missing from disk.
        "BookFileDelete" if v["deleteReason"].is_null() => Event::Ignored,
        kind if kind.ends_with("FileDelete") => Event::Deleted(files(&[
            "episodeFile",
            "movieFile",
            "bookFile",
            "trackFile",
        ])),
        "SeriesDelete" | "MovieDelete" | "ArtistDelete" | "AuthorDelete"
            if v["deletedFiles"] == true =>
        {
            [
                &v["series"]["path"],
                &v["movie"]["folderPath"],
                &v["artist"]["path"],
                &v["author"]["path"],
            ]
            .into_iter()
            .find_map(Value::as_str)
            .filter(|p| !p.is_empty())
            .map_or(Event::Ignored, |p| Event::FolderDeleted(p.to_owned()))
        }
        // The album shares its artist's folder, and the delivery lists no files.
        "AlbumDelete" if v["deletedFiles"] == true => Event::Reconcile,
        "Rename" => Event::Renamed(
            [
                "renamedEpisodeFiles",
                "renamedMovieFiles",
                "renamedTrackFiles",
                "renamedBookFiles",
            ]
            .iter()
            .filter_map(|k| v[*k].as_array())
            .flatten()
            .filter_map(|f| Some((file(f)?, f["previousPath"].as_str()?.to_owned())))
            .collect(),
        ),
        _ => Event::Ignored,
    })
}

fn file(v: &Value) -> Option<File> {
    Some(File {
        id: v["id"].as_i64(),
        path: v["path"].as_str().filter(|p| !p.is_empty())?.to_owned(),
        source: v["sourcePath"]
            .as_str()
            .filter(|p| !p.is_empty())
            .map(str::to_owned),
    })
}

/// Where our webhook sends, and how it signs in.
pub struct Hook<'a> {
    pub name: &'a str,
    pub url: &'a str,
    pub username: &'a str,
    pub password: &'a str,
}

/// Creates or updates the webhook named `hook.name`; saved even when the Arr cannot reach
/// it yet.
pub async fn register(http: &reqwest::Client, arr: &Arr, hook: &Hook<'_>) -> Result<(), Error> {
    let s = Session::open(http, arr).await?;
    let list = match s.api {
        Api::Classic => "notification",
        Api::V5 => "connection",
    };
    let existing: Vec<Value> = fetch(s.call(Method::GET, list)).await?;
    let id = existing
        .iter()
        // Arrs require names unique ignoring case.
        .find(|n| {
            n["name"]
                .as_str()
                .is_some_and(|name| name.eq_ignore_ascii_case(hook.name))
        })
        .and_then(|n| n["id"].as_i64());
    let mut body = if arr.profile == Profile::Sportarr {
        let config = json!({
            "webhook": hook.url, "method": "POST", "username": hook.username,
            "password": hook.password, "onDownload": true, "onUpgrade": true,
            "onRename": true, "onEventDelete": true, "onEventFileDelete": true,
            "onEventFileDeleteForUpgrade": true,
        });
        json!({
            "id": id.unwrap_or(0), "name": hook.name, "implementation": "Webhook",
            "enabled": true, "configJson": config.to_string(), "tags": [],
        })
    } else {
        // Flags a profile lacks are ignored by it.
        json!({
            "id": id.unwrap_or(0), "name": hook.name, "implementation": "Webhook",
            "configContract": "WebhookSettings", "tags": [],
            "fields": [
                { "name": "url", "value": hook.url },
                { "name": "method", "value": 1 },
                { "name": "username", "value": hook.username },
                { "name": "password", "value": hook.password },
            ],
            "onDownload": true, "onReleaseImport": true, "onUpgrade": true, "onRename": true,
            "onSeriesDelete": true, "onEpisodeFileDelete": true,
            "onEpisodeFileDeleteForUpgrade": true, "onMovieDelete": true,
            "onMovieFileDelete": true, "onMovieFileDeleteForUpgrade": true,
            "onArtistDelete": true, "onAlbumDelete": true, "onAuthorDelete": true,
            "onBookFileDelete": true, "onBookFileDeleteForUpgrade": true,
        })
    };
    // forceSave still tests a new enabled webhook, so one the Arr cannot reach would not
    // save: create it with every event off (disabled, untested), then turn them on.
    let id = match (id, arr.profile, s.api) {
        (None, profile, Api::Classic) if profile != Profile::Sportarr => {
            let mut off = body.clone();
            if let Some(fields) = off.as_object_mut() {
                for (key, value) in fields.iter_mut() {
                    if key.starts_with("on") {
                        *value = Value::Bool(false);
                    }
                }
            }
            let created: Value = fetch(
                s.call(Method::POST, list)
                    .query(&[("forceSave", "true")])
                    .json(&off),
            )
            .await?;
            let id = created["id"]
                .as_i64()
                .ok_or(Error::Unexpected("the created webhook has no id"))?;
            body["id"] = id.into();
            Some(id)
        }
        (id, ..) => id,
    };
    let request = match id {
        Some(id) => s.call(Method::PUT, &format!("{list}/{id}")),
        None => s.call(Method::POST, list),
    };
    // On update, skips the test and warnings, but not errors.
    let request = match (arr.profile, s.api) {
        (Profile::Sportarr, _) => request,
        (_, Api::Classic) => request.query(&[("forceSave", "true")]),
        (_, Api::V5) => request.query(&[("skipTesting", "true"), ("skipValidation", "warnings")]),
    };
    send(request.json(&body)).await
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn reads_deliveries_of_every_shape() {
        let read = |v: Value| parse(v.to_string().as_bytes()).unwrap();
        let f = |id: Option<i64>, path: &str, source: Option<&str>| File {
            id,
            path: path.to_owned(),
            source: source.map(str::to_owned),
        };
        assert_eq!(
            read(json!({
                "eventType": "Download", "downloadId": "abc",
                "episodeFile": { "id": 7, "path": "/tv/a.mkv", "sourcePath": "/dl/tv/j/a.mkv" },
                "deletedFiles": [{ "path": "/tv/old.mkv" }],
            })),
            Event::Imported {
                download_id: Some("abc".to_owned()),
                files: vec![f(Some(7), "/tv/a.mkv", Some("/dl/tv/j/a.mkv"))],
                replaced: vec![f(None, "/tv/old.mkv", None)],
            }
        );
        assert_eq!(
            read(
                json!({ "eventType": "Download", "trackFiles": [{ "id": 1, "path": "/m/1.flac" }] })
            ),
            Event::Imported {
                download_id: None,
                files: vec![f(Some(1), "/m/1.flac", None)],
                replaced: vec![],
            }
        );
        assert_eq!(
            read(
                json!({ "eventType": "MovieFileDelete", "movieFile": { "id": 3, "path": "/mv/a.mkv" } })
            ),
            Event::Deleted(vec![f(Some(3), "/mv/a.mkv", None)])
        );
        assert_eq!(
            read(json!({
                "eventType": "EpisodeFileDelete", "deleteReason": "MissingFromDisk",
                "episodeFile": { "id": 3, "path": "/tv/a.mkv" },
            })),
            Event::Ignored
        );
        assert_eq!(
            read(json!({
                "eventType": "BookFileDelete", "bookFile": { "id": 3, "path": "/b/a.mp3" },
            })),
            Event::Ignored
        );
        assert_eq!(
            read(
                json!({ "eventType": "MovieDelete", "deletedFiles": true, "movie": { "folderPath": "/mv/A" } })
            ),
            Event::FolderDeleted("/mv/A".to_owned())
        );
        assert_eq!(
            read(
                json!({ "eventType": "SeriesDelete", "deletedFiles": false, "series": { "path": "/tv/A" } })
            ),
            Event::Ignored
        );
        assert_eq!(
            read(json!({
                "eventType": "AlbumDelete", "deletedFiles": true,
                "artist": { "id": 1, "path": "/music/A" }, "album": { "id": 2 },
            })),
            Event::Reconcile
        );
        assert_eq!(
            read(json!({
                "eventType": "AlbumDelete", "deletedFiles": false,
                "artist": { "id": 1, "path": "/music/A" }, "album": { "id": 2 },
            })),
            Event::Ignored
        );
        assert_eq!(
            read(json!({
                "eventType": "Rename",
                "renamedEpisodeFiles": [{ "id": 7, "path": "/tv/b.mkv", "previousPath": "/tv/a.mkv" }],
            })),
            Event::Renamed(vec![(
                f(Some(7), "/tv/b.mkv", None),
                "/tv/a.mkv".to_owned()
            )])
        );
        assert_eq!(read(json!({ "eventType": "Test" })), Event::Test);
        assert_eq!(read(json!({ "eventType": "Grab" })), Event::Ignored);
    }

    /// Sportarr sends no file ids, and the same `EpisodeFileDelete` for a removal and
    /// for an upgrade's replaced file, often at the new file's path. Until its ownership
    /// is recorded, none of its deletes may release anything.
    #[test]
    fn sportarr_deliveries_release_nothing_by_themselves() {
        let read = |v: Value| parse(v.to_string().as_bytes()).unwrap();
        let f = |path: &str| File {
            id: None,
            path: path.to_owned(),
            source: None,
        };
        assert_eq!(
            read(json!({
                "eventType": "Download", "downloadId": "abc", "isUpgrade": true,
                "episodeFile": { "relativePath": "a.mkv", "path": "/sports/L/a.mkv" },
                "deletedFiles": [{ "relativePath": "a.mkv", "path": "/sports/L/a.mkv" }],
            })),
            Event::Imported {
                download_id: Some("abc".to_owned()),
                files: vec![f("/sports/L/a.mkv")],
                replaced: vec![f("/sports/L/a.mkv")],
            }
        );
        assert_eq!(
            read(json!({
                "eventType": "EpisodeFileDelete",
                "deletedFiles": [{ "relativePath": "a.mkv", "path": "/sports/L/a.mkv" }],
            })),
            Event::Deleted(vec![])
        );
        assert_eq!(
            read(json!({
                "eventType": "SeriesDelete", "series": { "path": "/sports/L" },
                "deletedFiles": [{ "relativePath": "a.mkv", "path": "/sports/L/a.mkv" }],
            })),
            Event::Ignored
        );
    }
}
