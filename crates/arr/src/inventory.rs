//! An Arr's whole library, for reconciling the files we think it owns (import policy
//! §10.4). Every request must succeed for the result to count: a partial inventory would
//! make present files look deleted.

use reqwest::Method;
use serde_json::Value;

use crate::{Arr, Error, Profile, Session, fetch, version};

/// Media per `moviefile` request.
const BATCH: usize = 100;

/// A file in an Arr's library.
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct LibraryFile {
    pub id: i64,
    pub path: String,
}

/// Every file in the Arr's library; `None` for profiles without a file listing
/// (Sportarr).
pub async fn files(http: &reqwest::Client, arr: &Arr) -> Result<Option<Vec<LibraryFile>>, Error> {
    // Also proves the URL still answers as this profile.
    let version = version(http, arr).await?;
    let movies = match arr.profile {
        Profile::Radarr => true,
        Profile::Whisparr => crate::whisparr_has_movies(&version),
        Profile::Sonarr | Profile::Lidarr | Profile::Chaptarr => false,
        Profile::Sportarr => return Ok(None),
    };
    let s = Session::open(http, arr).await?;
    let mut files = Vec::new();
    if movies {
        let ids = ids(&fetch::<Vec<Value>>(s.library_list("movie")).await?)?;
        for batch in ids.chunks(BATCH) {
            let query: Vec<(&str, i64)> = batch.iter().map(|id| ("movieId", *id)).collect();
            let found = fetch::<Vec<Value>>(s.call(Method::GET, "moviefile").query(&query));
            files.extend(parse(&found.await?)?);
        }
        return Ok(Some(files));
    }
    let (parents, param, endpoint) = match arr.profile {
        Profile::Lidarr => ("artist", "artistId", "trackfile"),
        Profile::Chaptarr => ("author", "authorId", "bookfile"),
        _ => ("series", "seriesId", "episodefile"),
    };
    for id in ids(&fetch::<Vec<Value>>(s.library_list(parents)).await?)? {
        let found = fetch::<Vec<Value>>(s.call(Method::GET, endpoint).query(&[(param, id)]));
        files.extend(parse(&found.await?)?);
    }
    Ok(Some(files))
}

fn ids(list: &[Value]) -> Result<Vec<i64>, Error> {
    list.iter()
        .map(|v| {
            v["id"]
                .as_i64()
                .ok_or(Error::Unexpected("library entry without id"))
        })
        .collect()
}

fn parse(list: &[Value]) -> Result<Vec<LibraryFile>, Error> {
    list.iter()
        .map(|f| {
            Ok(LibraryFile {
                id: f["id"]
                    .as_i64()
                    .ok_or(Error::Unexpected("library file without id"))?,
                path: f["path"]
                    .as_str()
                    .ok_or(Error::Unexpected("library file without path"))?
                    .to_owned(),
            })
        })
        .collect()
}
