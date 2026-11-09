//! The few Arr calls repair needs: find the media file imported from a download,
//! blocklist the release, and get a replacement searched. Sonarr, Radarr, Whisparr
//! (shaped like one of those), Lidarr and Chaptarr.

use std::time::Duration;

use infinidysk_core::media;
use reqwest::Method;
use serde::Deserialize;
use serde_json::json;

use crate::{Arr, Error, Profile, Session, fetch, send, version};

/// History `eventType` values shared by Sonarr, Radarr, Lidarr and Chaptarr.
const GRABBED: &str = "1";
const IMPORTED: &str = "3";
const FAILED: &str = "4";
/// How often the Arr's commands are read while it searches the media being repaired.
const SEARCH_POLL: Duration = Duration::from_secs(5);
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
    /// The library file the import created, where the history names it.
    pub file_id: Option<i64>,
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
    /// Episode ids, with the series and season of the first one.
    Episodes {
        ids: Vec<i64>,
        series: i64,
        season: i64,
    },
    Movie(i64),
    Album {
        id: i64,
        artist: i64,
    },
    Book {
        id: i64,
        author: i64,
    },
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
        Profile::Sportarr => None,
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
            Media::Episodes { ids, .. } => ("episode", ids),
            Media::Movie(id) => ("movie", std::slice::from_ref(id)),
            Media::Album { id, .. } => ("album", std::slice::from_ref(id)),
            Media::Book { id, .. } => ("book", std::slice::from_ref(id)),
        }
    }
}

#[derive(Deserialize)]
#[serde(rename_all = "camelCase")]
struct QueueRecord {
    download_id: Option<String>,
}

/// An entry of the Arr's command list.
#[derive(Default, Deserialize)]
#[serde(default)]
struct Command {
    name: String,
    status: String,
    body: CommandBody,
}

/// What a search command names.
#[derive(Default, Deserialize)]
#[serde(default, rename_all = "camelCase")]
struct CommandBody {
    episode_ids: Vec<i64>,
    series_id: i64,
    /// Season 0 (specials) is a real season.
    season_number: Option<i64>,
    movie_ids: Vec<i64>,
    album_ids: Vec<i64>,
    artist_id: i64,
    book_ids: Vec<i64>,
    author_id: i64,
}

/// Whether `command` is a queued or running search that covers `media`.
fn searching(media: &Media, command: &Command) -> bool {
    let Command { name, status, body } = command;
    let running = status.eq_ignore_ascii_case("queued") || status.eq_ignore_ascii_case("started");
    running
        && match (media, name.as_str()) {
            (Media::Episodes { ids, .. }, "EpisodeSearch") => {
                body.episode_ids.iter().any(|id| ids.contains(id))
            }
            (Media::Episodes { series, season, .. }, "SeasonSearch") => {
                body.series_id == *series && body.season_number == Some(*season)
            }
            (Media::Episodes { series, .. }, "SeriesSearch") => body.series_id == *series,
            (Media::Movie(id), "MoviesSearch") => body.movie_ids.contains(id),
            (Media::Album { id, .. }, "AlbumSearch") => body.album_ids.contains(id),
            (Media::Album { artist, .. }, "ArtistSearch") => body.artist_id == *artist,
            (Media::Book { id, .. }, "BookSearch") => body.book_ids.contains(id),
            (Media::Book { author, .. }, "AuthorSearch") => {
                body.author_id != 0 && body.author_id == *author
            }
            _ => false,
        }
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
    /// Sent as a string; a number is accepted too.
    file_id: Option<serde_json::Value>,
}

impl Record {
    fn into_import(self) -> Option<Import> {
        let file_id = self
            .data
            .file_id
            .as_ref()
            .and_then(|v| v.as_i64().or_else(|| v.as_str()?.trim().parse().ok()));
        Some(Import {
            download_id: self.download_id?.trim().to_owned(),
            dropped: self.data.dropped_path?,
            imported: self.data.imported_path?,
            file_id,
        })
    }
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
    /// Lidarr's name their artist too.
    #[serde(default)]
    artist_id: Option<i64>,
    /// Chaptarr's name their book.
    #[serde(default)]
    book_id: Option<i64>,
    #[serde(default)]
    author_id: Option<i64>,
}

#[derive(Deserialize)]
#[serde(rename_all = "camelCase")]
struct Episode {
    #[serde(rename = "episodeFileId")]
    file_id: i64,
    #[serde(default)]
    series_id: i64,
    #[serde(default)]
    season_number: i64,
}

#[derive(Deserialize)]
#[serde(rename_all = "camelCase")]
struct Track {
    track_file_id: i64,
    #[serde(default)]
    artist_id: i64,
}

/// An episode as `GET episode` lists it.
#[derive(Deserialize)]
#[serde(rename_all = "camelCase")]
struct Listed {
    id: i64,
    #[serde(default)]
    series_id: i64,
    #[serde(default)]
    season_number: i64,
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
    let s = Session::open(http, arr).await?;
    let call = |method, path: String| s.call(method, &path);
    let imported: Page = fetch(call(Method::GET, "history".to_owned()).query(&[
        ("downloadId", job),
        ("eventType", IMPORTED),
        ("pageSize", "1000"),
    ]))
    .await?;
    let last_import = imported
        .records
        .iter()
        .filter_map(|r| r.date.as_deref()?.parse::<jiff::Timestamp>().ok())
        .max();
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
            let current = match episode.file_id {
                0 => None,
                id => Some(fetch(call(Method::GET, format!("episodefile/{id}"))).await?),
            };
            let (series, season) = (episode.series_id, episode.season_number);
            let media = Media::Episodes {
                ids,
                series,
                season,
            };
            (media, current)
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
            let artist = track.artist_id;
            (Media::Album { id: album, artist }, current)
        }
        Shape::Books => {
            let id = records
                .iter()
                .find_map(|r| r.book_id)
                .ok_or(Error::Unexpected("import record without bookId"))?;
            let files: Vec<MediaFile> =
                fetch(call(Method::GET, "bookfile".to_owned()).query(&[("bookId", id)])).await?;
            let author = files
                .iter()
                .find_map(|file| file.author_id)
                .unwrap_or_default();
            let current = current_book_file(files, &imported_path, last_import, recorded_file);
            (Media::Book { id, author }, current)
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
/// hold an audiobook and an ebook, and an audiobook many files: the other chapters of the
/// same download are no replacement, even renamed. Prefers the recorded file, then the
/// imported path, then the newest of the same format added after the download's
/// `last_import`.
fn current_book_file(
    files: Vec<MediaFile>,
    imported_path: &str,
    last_import: Option<jiff::Timestamp>,
    recorded_file: Option<i64>,
) -> Option<MediaFile> {
    let format = |path: &str| match media::kind(path) {
        Some(media::Kind::Book) => Some(media::Kind::Book),
        _ if media::fits(path, media::Kind::Audio) => Some(media::Kind::Audio),
        _ => None,
    };
    let wanted = format(imported_path)?;
    let added = |f: &MediaFile| {
        f.date_added
            .as_deref()
            .and_then(|d| d.parse::<jiff::Timestamp>().ok())
    };
    let mut files: Vec<MediaFile> = files
        .into_iter()
        .filter(|f| format(&f.path) == Some(wanted))
        .collect();
    let at = files
        .iter()
        .position(|f| Some(f.id) == recorded_file)
        .or_else(|| files.iter().position(|f| f.path == imported_path))
        .or_else(|| {
            (0..files.len())
                .filter(|&i| {
                    last_import
                        .zip(added(&files[i]))
                        .is_some_and(|(t, a)| a.duration_since(t) > DATE_SLACK)
                })
                .max_by_key(|&i| added(&files[i]))
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
    let s = Session::open(http, arr).await?;
    let kind = match shape {
        Shape::Series => "episodefile",
        Shape::Movies => "moviefile",
        Shape::Music => "trackfile",
        Shape::Books => "bookfile",
    };
    let current: MediaFile = match fetch(s.call(Method::GET, &format!("{kind}/{file}"))).await {
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
            let episodes: Vec<Listed> = fetch(
                s.call(Method::GET, "episode")
                    .query(&[("episodeFileId", file.as_str())]),
            )
            .await?;
            let Some(first) = episodes.first() else {
                return Ok(None);
            };
            Media::Episodes {
                ids: episodes.iter().map(|e| e.id).collect(),
                series: first.series_id,
                season: first.season_number,
            }
        }
        Shape::Movies => Media::Movie(
            current
                .movie_id
                .ok_or(Error::Unexpected("movie file without movieId"))?,
        ),
        Shape::Music => Media::Album {
            id: current
                .album_id
                .ok_or(Error::Unexpected("track file without albumId"))?,
            artist: current.artist_id.unwrap_or_default(),
        },
        Shape::Books => Media::Book {
            id: current
                .book_id
                .ok_or(Error::Unexpected("book file without bookId"))?,
            author: current.author_id.unwrap_or_default(),
        },
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
    let s = Session::open(http, arr).await?;
    let page: Page = fetch(s.call(Method::GET, "history").query(&[
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
    let s = Session::open(http, arr).await?;
    let size = PAGE.to_string();
    for page in 1_u32.. {
        let page = page.to_string();
        let found: Page = fetch(s.call(Method::GET, "history").query(&[
            ("eventType", IMPORTED),
            ("page", &page),
            ("pageSize", &size),
        ]))
        .await?;
        let last = found.records.len() < PAGE;
        imports.extend(found.records.into_iter().filter_map(Record::into_import));
        if last {
            break;
        }
    }
    Ok(imports)
}

/// Gets `owner` a replacement for download `job`: blocklist the release, wait up to `wait`
/// while the Arr searches the media, else delete our file and search. Once the blocklist
/// succeeds, `resume` holds the newest grab id read before it; given one, the release is
/// already blocklisted, so this goes straight to the wait. With no download known, or no
/// grab of it in the Arr's history, nothing gets blocklisted and so nothing triggers the
/// Arr's search; it deletes and searches at once. Returns at once if a replacement is
/// already imported or queued.
pub async fn replace(
    http: &reqwest::Client,
    arr: &Arr,
    job: Option<&str>,
    owner: &Owner,
    wait: Duration,
    resume: &mut Option<i64>,
) -> Result<(), Error> {
    if owner.replaced() {
        return Ok(());
    }
    let s = Session::open(http, arr).await?;
    let call = |method, path: String| s.call(method, &path);
    if replacing(&s, job, owner).await? {
        return Ok(());
    }
    if let Some(job) = job {
        if resume.is_none() || wait.is_zero() {
            let grabbed: Page = fetch(call(Method::GET, "history".to_owned()).query(&[
                ("downloadId", job),
                ("eventType", GRABBED),
                ("pageSize", "1"),
            ]))
            .await?;
            if let Some(grab) = grabbed.records.first() {
                // Read before the blocklist, so a grab its search makes has a larger id.
                let since = if wait.is_zero() {
                    None
                } else {
                    Some(
                        newest_grabs(&s, owner)
                            .await?
                            .iter()
                            .map(|grab| grab.id)
                            .max()
                            .unwrap_or(0),
                    )
                };
                send(call(Method::POST, format!("history/failed/{}", grab.id)).json(&json!({})))
                    .await?;
                *resume = since;
            } else {
                tracing::warn!(
                    arr = %arr.name,
                    reason = "no grab in its history",
                    "could not blocklist the broken release; deleting the file and searching now"
                );
            }
        }
        if !wait.is_zero()
            && let Some(since) = *resume
            && own_search(&s, job, owner, since, wait).await?
        {
            return Ok(());
        }
    }
    let (kind, search) = match &owner.media {
        Media::Episodes { ids, .. } => (
            "episodefile",
            json!({ "name": "EpisodeSearch", "episodeIds": ids }),
        ),
        Media::Movie(id) => (
            "moviefile",
            json!({ "name": "MoviesSearch", "movieIds": [id] }),
        ),
        // Lidarr searches whole albums; one bad track gets the album searched.
        Media::Album { id, .. } => (
            "trackfile",
            json!({ "name": "AlbumSearch", "albumIds": [id] }),
        ),
        Media::Book { id, .. } => ("bookfile", json!({ "name": "BookSearch", "bookIds": [id] })),
    };
    if let Some(file) = &owner.current {
        send(call(Method::DELETE, format!("{kind}/{}", file.id))).await?;
    }
    send(call(Method::POST, "command".to_owned()).json(&search)).await?;
    Ok(())
}

/// Whether the Arr queue holds a download other than `job` for the owner's media.
async fn replacing(s: &Session<'_>, job: Option<&str>, owner: &Owner) -> Result<bool, Error> {
    let query: Vec<(&str, i64)> = match &owner.media {
        Media::Episodes { ids, .. } => ids.iter().map(|&id| ("episodeIds", id)).collect(),
        Media::Movie(id) => vec![("movieId", *id)],
        Media::Album { id, .. } => vec![("albumIds", *id)],
        Media::Book { id, .. } => vec![("bookIds", *id)],
    };
    let queued: Vec<QueueRecord> =
        fetch(s.call(Method::GET, "queue/details").query(&query)).await?;
    Ok(queued.iter().any(|r| {
        r.download_id
            .as_deref()
            .is_some_and(|id| job.is_none_or(|job| !id.eq_ignore_ascii_case(job)))
    }))
}

/// Waits while the Arr searches the owner's media after the blocklist; `true` once a
/// replacement is queued, or was grabbed after `since` without a later download failure.
/// A search still queued or running after `wait` is [`Error::Searching`], so the file is
/// never deleted under it.
async fn own_search(
    s: &Session<'_>,
    job: &str,
    owner: &Owner,
    since: i64,
    wait: Duration,
) -> Result<bool, Error> {
    let deadline = tokio::time::Instant::now() + wait;
    loop {
        let commands: Vec<Command> = fetch(s.call(Method::GET, "command")).await?;
        if !commands.iter().any(|c| searching(&owner.media, c)) {
            // History precedes queue refresh, but a later failure invalidates its grab.
            for grab in newest_grabs(s, owner).await? {
                if grab.id <= since {
                    continue;
                }
                let download = grab
                    .download_id
                    .as_deref()
                    .filter(|id| !id.trim().is_empty())
                    .ok_or(Error::Unexpected("grab history without downloadId"))?;
                let failed: Page = fetch(s.call(Method::GET, "history").query(&[
                    ("downloadId", download),
                    ("eventType", FAILED),
                    ("pageSize", "1"),
                ]))
                .await?;
                if !failed.records.iter().any(|failure| failure.id > grab.id) {
                    return Ok(true);
                }
            }
            return replacing(s, Some(job), owner).await;
        }
        if replacing(s, Some(job), owner).await? {
            return Ok(true);
        }
        let now = tokio::time::Instant::now();
        if now >= deadline {
            return Err(Error::Searching);
        }
        tokio::time::sleep(SEARCH_POLL.min(deadline - now)).await;
    }
}

/// The Arr's newest grab of each of the owner's media.
async fn newest_grabs(s: &Session<'_>, owner: &Owner) -> Result<Vec<Record>, Error> {
    let filters: Vec<(&str, i64)> = match &owner.media {
        Media::Episodes { ids, .. } => ids.iter().map(|&id| ("episodeId", id)).collect(),
        Media::Movie(id) => vec![("movieIds", *id)],
        Media::Album { id, .. } => vec![("albumId", *id)],
        Media::Book { id, .. } => vec![("bookId", *id)],
    };
    let mut newest = Vec::new();
    for filter in filters {
        let page: Page = fetch(
            s.call(Method::GET, "history")
                .query(&[("eventType", GRABBED), ("pageSize", "1")])
                .query(&[filter]),
        )
        .await?;
        newest.extend(page.records.into_iter().take(1));
    }
    Ok(newest)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn an_import_keeps_the_file_id_its_history_names() {
        let import = |download: &str, file_id: serde_json::Value| {
            let r: Record = serde_json::from_value(json!({
                "id": 1, "downloadId": download,
                "data": { "droppedPath": "/d/a.mkv", "importedPath": "/lib/a.mkv", "fileId": file_id },
            }))
            .expect("a history record");
            r.into_import().expect("an import")
        };
        let record = |file_id| import("abc", file_id).file_id;
        assert_eq!(record(json!("42")), Some(42));
        assert_eq!(record(json!(42)), Some(42));
        assert_eq!(record(json!("")), None);
        assert_eq!(record(serde_json::Value::Null), None);
        assert_eq!(import(" abc ", json!(1)).download_id, "abc");
    }

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
                artist_id: None,
                book_id: None,
                author_id: None,
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
            artist_id: None,
            book_id: Some(7),
            author_id: None,
        };
        let files = || {
            vec![
                file(1, "/books/a/a.epub", "2024-07-01T00:00:00Z"),
                file(2, "/books/a/01.mp3", "2024-05-01T00:00:00Z"),
                file(3, "/books/a/02.mp3", "2024-06-01T00:00:00Z"),
            ]
        };
        let pick = |path, recorded, last_import: &str| {
            let last_import = Some(last_import.parse().expect("a timestamp"));
            current_book_file(files(), path, last_import, recorded).map(|f| f.id)
        };
        let early = "2024-04-01T00:00:00Z";
        assert_eq!(pick("/books/a/01.mp3", Some(2), early), Some(2));
        assert_eq!(pick("/books/a/01.mp3", None, early), Some(2));
        assert_eq!(pick("/books/a/gone.mp3", Some(9), early), Some(3));
        assert_eq!(pick("/books/a/gone.epub", None, early), Some(1));
        assert_eq!(pick("/books/a/gone.m4b", None, early), Some(3));
        assert_eq!(pick("/books/a/gone.nfo", None, early), None);
        // A missing chapter: the chapters of its download, however slow or renamed since,
        // are no replacement.
        assert_eq!(
            pick("/books/a/gone.mp3", Some(9), "2024-06-01T00:00:00Z"),
            None
        );
    }

    #[test]
    fn a_search_counts_while_queued_or_started_and_covering_the_media() {
        let command = |name: &str, status: &str, body: serde_json::Value| -> Command {
            serde_json::from_value(json!({ "name": name, "status": status, "body": body }))
                .expect("a command")
        };
        let started = |name, body| command(name, "started", body);
        let episodes = Media::Episodes {
            ids: vec![5, 6],
            series: 9,
            season: 1,
        };
        let album = Media::Album { id: 4, artist: 2 };
        let movie = Media::Movie(3);
        let ids = |ids: &[i64]| json!({ "episodeIds": ids });
        let season = |n: i64| json!({ "seriesId": 9, "seasonNumber": n });
        assert!(searching(
            &episodes,
            &started("EpisodeSearch", ids(&[6, 7]))
        ));
        assert!(searching(
            &episodes,
            &command("EpisodeSearch", "queued", ids(&[5]))
        ));
        assert!(!searching(
            &episodes,
            &command("EpisodeSearch", "completed", ids(&[5]))
        ));
        assert!(!searching(&episodes, &started("EpisodeSearch", ids(&[7]))));
        assert!(searching(&episodes, &started("SeasonSearch", season(1))));
        assert!(!searching(&episodes, &started("SeasonSearch", season(2))));
        for (series, status, expected) in [
            (9, "started", true),
            (9, "queued", true),
            (10, "started", false),
            (9, "completed", false),
        ] {
            assert_eq!(
                searching(
                    &episodes,
                    &command("SeriesSearch", status, json!({ "seriesId": series }))
                ),
                expected
            );
        }
        assert!(searching(
            &movie,
            &started("MoviesSearch", json!({ "movieIds": [3] }))
        ));
        assert!(!searching(&movie, &started("EpisodeSearch", ids(&[3]))));
        assert!(searching(
            &album,
            &started("AlbumSearch", json!({ "albumIds": [4] }))
        ));
        assert!(searching(
            &album,
            &started("ArtistSearch", json!({ "artistId": 2 }))
        ));
        assert!(!searching(
            &album,
            &started("ArtistSearch", json!({ "artistId": 3 }))
        ));
        let book = Media::Book { id: 8, author: 2 };
        assert!(searching(
            &book,
            &started("BookSearch", json!({ "bookIds": [8] }))
        ));
        for (author, status, expected) in [
            (2, "started", true),
            (2, "queued", true),
            (3, "started", false),
            (2, "completed", false),
        ] {
            assert_eq!(
                searching(
                    &book,
                    &command("AuthorSearch", status, json!({ "authorId": author }))
                ),
                expected
            );
        }
        assert!(!searching(
            &Media::Book { id: 8, author: 0 },
            &started("AuthorSearch", json!({}))
        ));
    }
}
