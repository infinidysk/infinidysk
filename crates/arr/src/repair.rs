//! The few Arr calls repair needs: find the media file imported from a download,
//! blocklist the release, and get a replacement searched. Sonarr, Radarr, Whisparr
//! (shaped like one of those), Lidarr and Chaptarr.

use std::time::Duration;

use infinidysk_core::media;
use reqwest::Method;
use serde::Deserialize;
use serde_json::json;

use crate::{Arr, Error, Profile, call, fetch, send, version};

/// History `eventType` values shared by Sonarr, Radarr, Lidarr and Chaptarr.
const GRABBED: &str = "1";
const IMPORTED: &str = "3";
/// How often the Arr queue is polled while waiting for its own search.
const QUEUE_POLL: Duration = Duration::from_secs(5);
/// History records per page.
const PAGE: usize = 1000;
/// Slack between an import's history date and its file's `dateAdded`.
const DATE_SLACK: jiff::SignedDuration = jiff::SignedDuration::from_secs(60);

/// A file an Arr imported from a download.
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct Import {
    pub download_id: String,
    pub dropped: String,
    pub imported: String,
}

/// The media an Arr imported from a download, and the file it holds for it now.
pub struct Owner {
    media: Media,
    imported_path: String,
    /// When the Arr imported it, per its history.
    imported_at: Option<String>,
    /// The Arr's id of the file we recorded it importing, if any.
    recorded_file: Option<i64>,
    current: Option<MediaFile>,
}

enum Media {
    Episodes(Vec<i64>),
    Movie(i64),
    Album(i64),
    Book(i64),
}

/// Which API an instance repairs through.
#[derive(Clone, Copy, PartialEq, Eq)]
enum Shape {
    Series,
    Movies,
    Music,
    Books,
}

async fn shape(http: &reqwest::Client, arr: &Arr) -> Result<Option<Shape>, Error> {
    Ok(match arr.profile {
        Profile::Sonarr => Some(Shape::Series),
        Profile::Radarr => Some(Shape::Movies),
        Profile::Lidarr => Some(Shape::Music),
        Profile::Chaptarr => Some(Shape::Books),
        Profile::Whisparr => Some(if crate::whisparr_has_movies(&version(http, arr).await?) {
            Shape::Movies
        } else {
            Shape::Series
        }),
        Profile::Readarr | Profile::Sportarr => None,
    })
}

impl Owner {
    /// The Arr has since imported a different file for this media. Compared by file id
    /// where we recorded one, else by the file being added after the import, else by
    /// path; so the same file under a new path (a STRM, a rename) is not a replacement.
    #[must_use]
    pub fn replaced(&self) -> bool {
        let parse = |d: Option<&str>| d.and_then(|d| d.parse::<jiff::Timestamp>().ok());
        self.current.as_ref().is_some_and(|f| {
            if let Some(id) = self.recorded_file {
                return f.id != id;
            }
            match (
                parse(f.date_added.as_deref()),
                parse(self.imported_at.as_deref()),
            ) {
                (Some(added), Some(imported)) => added.duration_since(imported) > DATE_SLACK,
                _ => f.path != self.imported_path,
            }
        })
    }

    /// The media kind and ids a replacement search covers, for the shared search limit.
    #[must_use]
    pub fn searches(&self) -> (&'static str, &[i64]) {
        match &self.media {
            Media::Episodes(ids) => ("episode", ids),
            Media::Movie(id) => ("movie", std::slice::from_ref(id)),
            Media::Album(id) => ("album", std::slice::from_ref(id)),
            Media::Book(id) => ("book", std::slice::from_ref(id)),
        }
    }
}

#[derive(Deserialize)]
#[serde(rename_all = "camelCase")]
struct QueueRecord {
    download_id: Option<String>,
}

#[derive(Deserialize)]
struct Page {
    records: Vec<Record>,
}

#[derive(Deserialize)]
#[serde(rename_all = "camelCase")]
struct Record {
    id: i64,
    download_id: Option<String>,
    episode_id: Option<i64>,
    movie_id: Option<i64>,
    track_id: Option<i64>,
    album_id: Option<i64>,
    book_id: Option<i64>,
    date: Option<String>,
    #[serde(default)]
    data: Data,
}

#[derive(Default, Deserialize)]
#[serde(rename_all = "camelCase")]
struct Data {
    dropped_path: Option<String>,
    imported_path: Option<String>,
}

#[derive(Deserialize)]
#[serde(rename_all = "camelCase")]
struct MediaFile {
    id: i64,
    path: String,
    date_added: Option<String>,
    /// Radarr's file records name their movie.
    #[serde(default)]
    movie_id: Option<i64>,
    /// Lidarr's name their album.
    #[serde(default)]
    album_id: Option<i64>,
    /// Chaptarr's name their book.
    #[serde(default)]
    book_id: Option<i64>,
}

#[derive(Deserialize)]
#[serde(rename_all = "camelCase")]
struct Episode {
    episode_file_id: i64,
}

#[derive(Deserialize)]
#[serde(rename_all = "camelCase")]
struct Track {
    track_file_id: i64,
}

#[derive(Deserialize)]
struct Id {
    id: i64,
}

#[derive(Deserialize)]
#[serde(rename_all = "camelCase")]
struct Movie {
    movie_file: Option<MediaFile>,
}

/// The media this instance imported from download `job` at a path ending in one of
/// `dropped` (the path under the completed-downloads folder, from the category on, by
/// each name it was handed off under); `None` if it imported nothing from there or its
/// profile has no repair support. `recorded_file` is the Arr's file id we recorded.
pub async fn owner(
    http: &reqwest::Client,
    arr: &Arr,
    job: &str,
    dropped: &[String],
    recorded_file: Option<i64>,
) -> Result<Option<Owner>, Error> {
    let Some(shape) = shape(http, arr).await? else {
        return Ok(None);
    };
    let call = |method, path: String| call(http, arr, method, &path);
    let imported: Page = fetch(call(Method::GET, "history".to_owned()).query(&[
        ("downloadId", job),
        ("eventType", IMPORTED),
        ("pageSize", "1000"),
    ]))
    .await?;
    let records: Vec<_> = imported
        .records
        .into_iter()
        .filter(|r| {
            r.data
                .dropped_path
                .as_deref()
                .is_some_and(|p| dropped.iter().any(|d| p.ends_with(d.as_str())))
        })
        .collect();
    let Some((imported_path, imported_at)) = records.iter().find_map(|r| {
        let path = r.data.imported_path.clone()?;
        Some((path, r.date.clone()))
    }) else {
        return Ok(None);
    };
    let (media, current) = match shape {
        Shape::Series => {
            let ids: Vec<i64> = records.iter().filter_map(|r| r.episode_id).collect();
            let first = ids
                .first()
                .ok_or(Error::Unexpected("import record without episodeId"))?;
            let episode: Episode = fetch(call(Method::GET, format!("episode/{first}"))).await?;
            let current = match episode.episode_file_id {
                0 => None,
                id => Some(fetch(call(Method::GET, format!("episodefile/{id}"))).await?),
            };
            (Media::Episodes(ids), current)
        }
        Shape::Movies => {
            let id = records
                .iter()
                .find_map(|r| r.movie_id)
                .ok_or(Error::Unexpected("import record without movieId"))?;
            let movie: Movie = fetch(call(Method::GET, format!("movie/{id}"))).await?;
            (Media::Movie(id), movie.movie_file)
        }
        Shape::Music => {
            let (track, album) = records
                .iter()
                .find_map(|r| Some((r.track_id?, r.album_id?)))
                .ok_or(Error::Unexpected("import record without trackId"))?;
            let track: Track = fetch(call(Method::GET, format!("track/{track}"))).await?;
            let current = match track.track_file_id {
                0 => None,
                id => Some(fetch(call(Method::GET, format!("trackfile/{id}"))).await?),
            };
            (Media::Album(album), current)
        }
        Shape::Books => {
            let id = records
                .iter()
                .find_map(|r| r.book_id)
                .ok_or(Error::Unexpected("import record without bookId"))?;
            let files: Vec<MediaFile> =
                fetch(call(Method::GET, "bookfile".to_owned()).query(&[("bookId", id)])).await?;
            (
                Media::Book(id),
                current_book_file(files, &imported_path, recorded_file),
            )
        }
    };
    Ok(Some(Owner {
        media,
        imported_path,
        imported_at,
        recorded_file,
        current,
    }))
}

/// The file a book holds now in place of the one imported at `imported_path`. A book can
/// hold an audiobook and an ebook, and an audiobook many files; only files of the same
/// media kind stand in for each other. Prefers the recorded file, then the imported path,
/// then the newest.
fn current_book_file(
    files: Vec<MediaFile>,
    imported_path: &str,
    recorded_file: Option<i64>,
) -> Option<MediaFile> {
    let kind = media::kind(imported_path);
    let mut files: Vec<MediaFile> = files
        .into_iter()
        .filter(|f| media::kind(&f.path) == kind)
        .collect();
    let at = files
        .iter()
        .position(|f| Some(f.id) == recorded_file)
        .or_else(|| files.iter().position(|f| f.path == imported_path))
        .or_else(|| {
            let parse = |f: &MediaFile| {
                f.date_added
                    .as_deref()
                    .and_then(|d| d.parse::<jiff::Timestamp>().ok())
            };
            (0..files.len()).max_by_key(|&i| parse(&files[i]))
        })?;
    Some(files.swap_remove(at))
}

/// The media this instance's library file `file` holds, found without its import
/// history; `None` if the file is gone or its profile has no repair support.
pub async fn owner_of_file(
    http: &reqwest::Client,
    arr: &Arr,
    file: i64,
) -> Result<Option<Owner>, Error> {
    let Some(shape) = shape(http, arr).await? else {
        return Ok(None);
    };
    let kind = match shape {
        Shape::Series => "episodefile",
        Shape::Movies => "moviefile",
        Shape::Music => "trackfile",
        Shape::Books => "bookfile",
    };
    let current: MediaFile =
        match fetch(call(http, arr, Method::GET, &format!("{kind}/{file}"))).await {
            Err(Error::Rejected {
                status: reqwest::StatusCode::NOT_FOUND,
                ..
            }) => {
                return Ok(None);
            }
            other => other?,
        };
    let media = match shape {
        Shape::Series => {
            let file = file.to_string();
            let episodes: Vec<Id> = fetch(
                call(http, arr, Method::GET, "episode").query(&[("episodeFileId", file.as_str())]),
            )
            .await?;
            if episodes.is_empty() {
                return Ok(None);
            }
            Media::Episodes(episodes.into_iter().map(|e| e.id).collect())
        }
        Shape::Movies => Media::Movie(
            current
                .movie_id
                .ok_or(Error::Unexpected("movie file without movieId"))?,
        ),
        Shape::Music => Media::Album(
            current
                .album_id
                .ok_or(Error::Unexpected("track file without albumId"))?,
        ),
        Shape::Books => Media::Book(
            current
                .book_id
                .ok_or(Error::Unexpected("book file without bookId"))?,
        ),
    };
    Ok(Some(Owner {
        media,
        imported_path: current.path.clone(),
        imported_at: None,
        recorded_file: Some(file),
        current: Some(current),
    }))
}

/// What this instance imported from download `job`, as `(dropped, imported)` paths per
/// file. Sportarr's history does not carry download ids.
pub async fn imports(
    http: &reqwest::Client,
    arr: &Arr,
    job: &str,
) -> Result<Vec<(String, String)>, Error> {
    if arr.profile == Profile::Sportarr {
        return Ok(Vec::new());
    }
    let page: Page = fetch(call(http, arr, Method::GET, "history").query(&[
        ("downloadId", job),
        ("eventType", IMPORTED),
        ("pageSize", "1000"),
    ]))
    .await?;
    Ok(page
        .records
        .into_iter()
        .filter_map(|r| Some((r.data.dropped_path?, r.data.imported_path?)))
        .collect())
}

/// Every import in this instance's history, newest first. Every page must load for the
/// result to count. Sportarr's history does not carry download ids.
pub async fn all_imports(http: &reqwest::Client, arr: &Arr) -> Result<Vec<Import>, Error> {
    let mut imports = Vec::new();
    if arr.profile == Profile::Sportarr {
        return Ok(imports);
    }
    let size = PAGE.to_string();
    for page in 1_u32.. {
        let page = page.to_string();
        let found: Page = fetch(call(http, arr, Method::GET, "history").query(&[
            ("eventType", IMPORTED),
            ("page", &page),
            ("pageSize", &size),
        ]))
        .await?;
        let last = found.records.len() < PAGE;
        imports.extend(found.records.into_iter().filter_map(|r| {
            Some(Import {
                download_id: r.download_id?,
                dropped: r.data.dropped_path?,
                imported: r.data.imported_path?,
            })
        }));
        if last {
            break;
        }
    }
    Ok(imports)
}

/// Gets `owner` a replacement for download `job`: blocklist the release, give the Arr's
/// own search `wait` to queue one, else delete our file and search. With no download
/// known, or no grab of it in the Arr's history, nothing gets blocklisted and so nothing
/// triggers the Arr's search; it deletes and searches at once. Returns at once if a
/// replacement is already imported or queued.
pub async fn replace(
    http: &reqwest::Client,
    arr: &Arr,
    job: Option<&str>,
    owner: &Owner,
    wait: Duration,
) -> Result<(), Error> {
    let call = |method, path: String| call(http, arr, method, &path);
    if owner.replaced() || replacing(http, arr, job, owner).await? {
        return Ok(());
    }
    if let Some(job) = job {
        let grabbed: Page = fetch(call(Method::GET, "history".to_owned()).query(&[
            ("downloadId", job),
            ("eventType", GRABBED),
            ("pageSize", "1"),
        ]))
        .await?;
        if let Some(grab) = grabbed.records.first() {
            send(call(Method::POST, format!("history/failed/{}", grab.id)).json(&json!({})))
                .await?;
            // ponytail: also waits when the Arr's automatic redownload is off; reading its
            // download-client config and the grab's release source would skip that too.
            let deadline = tokio::time::Instant::now() + wait;
            while tokio::time::Instant::now() < deadline {
                tokio::time::sleep(QUEUE_POLL.min(deadline - tokio::time::Instant::now())).await;
                if replacing(http, arr, Some(job), owner).await? {
                    return Ok(());
                }
            }
        } else {
            tracing::warn!(
                arr = %arr.name,
                reason = "no grab in its history",
                "could not blocklist the broken release; deleting the file and searching now"
            );
        }
    }
    let (kind, search) = match &owner.media {
        Media::Episodes(ids) => (
            "episodefile",
            json!({ "name": "EpisodeSearch", "episodeIds": ids }),
        ),
        Media::Movie(id) => (
            "moviefile",
            json!({ "name": "MoviesSearch", "movieIds": [id] }),
        ),
        // Lidarr searches whole albums; one bad track gets the album searched.
        Media::Album(id) => (
            "trackfile",
            json!({ "name": "AlbumSearch", "albumIds": [id] }),
        ),
        Media::Book(id) => ("bookfile", json!({ "name": "BookSearch", "bookIds": [id] })),
    };
    if let Some(file) = &owner.current {
        send(call(Method::DELETE, format!("{kind}/{}", file.id))).await?;
    }
    send(call(Method::POST, "command".to_owned()).json(&search)).await?;
    Ok(())
}

/// Whether the Arr queue holds a download other than `job` for the owner's media.
async fn replacing(
    http: &reqwest::Client,
    arr: &Arr,
    job: Option<&str>,
    owner: &Owner,
) -> Result<bool, Error> {
    let query: Vec<(&str, i64)> = match &owner.media {
        Media::Episodes(ids) => ids.iter().map(|&id| ("episodeIds", id)).collect(),
        Media::Movie(id) => vec![("movieId", *id)],
        Media::Album(id) => vec![("albumIds", *id)],
        Media::Book(id) => vec![("bookIds", *id)],
    };
    let queued: Vec<QueueRecord> =
        fetch(call(http, arr, Method::GET, "queue/details").query(&query)).await?;
    Ok(queued.iter().any(|r| {
        r.download_id
            .as_deref()
            .is_some_and(|id| job.is_none_or(|job| !id.eq_ignore_ascii_case(job)))
    }))
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn a_renamed_file_is_replaced_only_when_added_after_the_import() {
        let owner = |added: &str| Owner {
            media: Media::Movie(1),
            imported_path: "/movies/a.mkv".to_owned(),
            imported_at: Some("2024-05-01T12:00:00Z".to_owned()),
            recorded_file: None,
            current: Some(MediaFile {
                id: 2,
                path: "/movies/renamed.mkv".to_owned(),
                date_added: Some(added.to_owned()),
                movie_id: None,
                album_id: None,
                book_id: None,
            }),
        };
        assert!(!owner("2024-05-01T12:00:00.5Z").replaced());
        assert!(owner("2024-06-01T00:00:00Z").replaced());
    }

    #[test]
    fn a_book_file_stands_in_only_for_one_of_its_own_kind() {
        let file = |id, path: &str, added: &str| MediaFile {
            id,
            path: path.to_owned(),
            date_added: Some(added.to_owned()),
            movie_id: None,
            album_id: None,
            book_id: Some(7),
        };
        let files = || {
            vec![
                file(1, "/books/a/a.epub", "2024-07-01T00:00:00Z"),
                file(2, "/books/a/01.mp3", "2024-05-01T00:00:00Z"),
                file(3, "/books/a/02.mp3", "2024-06-01T00:00:00Z"),
            ]
        };
        let pick = |path, recorded| current_book_file(files(), path, recorded).map(|f| f.id);
        assert_eq!(pick("/books/a/01.mp3", Some(2)), Some(2));
        assert_eq!(pick("/books/a/01.mp3", None), Some(2));
        assert_eq!(pick("/books/a/gone.mp3", Some(9)), Some(3));
        assert_eq!(pick("/books/a/gone.epub", None), Some(1));
        assert_eq!(pick("/books/a/gone.m4b", None), Some(3));
        assert_eq!(pick("/books/a/gone.nfo", None), None);
    }
}
