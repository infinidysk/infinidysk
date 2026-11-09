//! `replace` against a fake Radarr.
#![allow(clippy::unwrap_used)] // Test helpers outside `#[test]` fns; panics are the assertions.

use std::sync::{Arc, Mutex};
use std::time::{Duration, Instant};

use axum::Json;
use axum::extract::{Request, State};
use axum::http::StatusCode;
use axum::response::{IntoResponse, Response};
use infinidysk_arr::{Arr, Error, Profile, owner, owner_of_file, replace};
use serde_json::json;
use tokio::net::TcpListener;

/// What the Arr does about movie 3 once the release is blocklisted.
#[derive(Clone, Copy)]
enum Search {
    /// Interactive redownload is off; it only has an unrelated search of movie 4.
    Off,
    /// Its search keeps running, but a replacement shows up in the queue.
    Queues,
    /// Its search has ended by the first command read and grabbed a replacement that the
    /// queue does not show yet.
    Grabs,
    FailedGrab,
    OlderFailure,
    FailureUnavailable,
    MissingDownloadId,
    /// Its search runs until the second command read, then ends with no grab.
    FindsNothing,
    /// Its search has ended by the first command read with no grab, but the queue shows a
    /// replacement grabbed before the blocklist only from then on.
    QueuesLate,
    /// It reuses a search of movie 3 that stays queued.
    Queued,
}

/// What the fake Radarr's history holds for download `job`.
#[derive(Clone, Copy)]
enum Grab {
    /// No grab of it.
    Missing,
    /// A grab whose blocklist call fails.
    BlocklistFails,
    /// A grab it blocklists; the Arr then searches movie 3 as given.
    Blocklists(Search),
}

#[derive(Default)]
struct Calls {
    log: Vec<String>,
    /// `GET command` calls so far.
    reads: usize,
}

type Shared = (Grab, Arc<Mutex<Calls>>);

/// A history page of records with `ids`.
fn page(ids: &[i64]) -> Response {
    let records: Vec<_> = ids
        .iter()
        .map(|id| json!({ "id": id, "downloadId": "other", "data": {} }))
        .collect();
    Json(json!({ "records": records })).into_response()
}

/// A search of `movie` in the command list.
fn movies_search(id: i64, movie: i64, status: &str) -> serde_json::Value {
    json!({ "id": id, "name": "MoviesSearch", "status": status, "body": { "movieIds": [movie] } })
}

/// Answers like Radarr for movie 3 held in file 7, and logs every call. Its command list
/// always holds an ended search of movie 3.
async fn radarr(State((grab, calls)): State<Shared>, req: Request) -> Response {
    let call = format!("{} {}", req.method(), req.uri());
    // `GET command` reads so far, this one included.
    let reads = {
        let mut calls = calls.lock().unwrap();
        calls.log.push(call.clone());
        if call == "GET /api/v3/command" {
            calls.reads += 1;
        }
        calls.reads
    };
    let search = match grab {
        Grab::Blocklists(search) => Some(search),
        _ => None,
    };
    match call.as_str() {
        "GET /api/v3/moviefile/7" => {
            Json(json!({ "id": 7, "movieId": 3, "path": "/movies/A/a.mkv" })).into_response()
        }
        "GET /api/v3/queue/details?movieId=3" => {
            if matches!(search, Some(Search::Queues | Search::QueuesLate)) && reads > 0 {
                Json(json!([{ "downloadId": "other" }])).into_response()
            } else {
                Json(json!([])).into_response()
            }
        }
        "GET /api/v3/history?downloadId=job&eventType=1&pageSize=1" => match grab {
            Grab::Missing => page(&[]),
            _ => page(&[12]),
        },
        "GET /api/v3/history?eventType=1&pageSize=1&movieIds=3" => {
            if matches!(search, Some(Search::MissingDownloadId)) && reads > 0 {
                return Json(json!({ "records": [{ "id": 20, "data": {} }] })).into_response();
            }
            let grabbed = matches!(
                search,
                Some(
                    Search::Grabs
                        | Search::FailedGrab
                        | Search::OlderFailure
                        | Search::FailureUnavailable
                )
            ) && reads > 0;
            page(&[if grabbed { 20 } else { 12 }])
        }
        "GET /api/v3/history?downloadId=other&eventType=4&pageSize=1" => match search {
            Some(Search::FailedGrab) => page(&[21]),
            Some(Search::OlderFailure) => page(&[19]),
            Some(Search::FailureUnavailable) => StatusCode::SERVICE_UNAVAILABLE.into_response(),
            _ => page(&[]),
        },
        "POST /api/v3/history/failed/12" => match grab {
            Grab::BlocklistFails => StatusCode::INTERNAL_SERVER_ERROR.into_response(),
            _ => Json(json!({})).into_response(),
        },
        "GET /api/v3/command" => {
            let mut commands = vec![movies_search(1, 3, "completed")];
            match search {
                Some(Search::Off) => commands.push(movies_search(2, 4, "started")),
                Some(Search::Queues) => commands.push(movies_search(2, 3, "started")),
                Some(
                    Search::Grabs
                    | Search::FailedGrab
                    | Search::QueuesLate
                    | Search::OlderFailure
                    | Search::FailureUnavailable
                    | Search::MissingDownloadId,
                ) => {
                    commands.push(movies_search(2, 3, "completed"));
                }
                Some(Search::FindsNothing) => {
                    let status = if reads < 2 { "started" } else { "completed" };
                    commands.push(movies_search(2, 3, status));
                }
                Some(Search::Queued) => commands.push(movies_search(2, 3, "queued")),
                None => {}
            }
            Json(commands).into_response()
        }
        _ => Json(json!({})).into_response(),
    }
}

/// Runs `replace` for download `job` with `wait` and `resume` against a fake Radarr;
/// returns its result, `resume` afterwards, the calls it made and how long it took.
async fn repair(
    grab: Grab,
    wait: Duration,
    mut resume: Option<i64>,
) -> (Result<(), Error>, Option<i64>, Vec<String>, Duration) {
    let calls = Arc::new(Mutex::new(Calls::default()));
    let listener = TcpListener::bind("127.0.0.1:0").await.unwrap();
    let url = format!("http://{}", listener.local_addr().unwrap());
    let app = axum::Router::new()
        .fallback(radarr)
        .with_state((grab, Arc::clone(&calls)));
    tokio::spawn(async move { axum::serve(listener, app).await });
    let arr = Arr {
        id: uuid::Uuid::nil(),
        name: "radarr".to_owned(),
        profile: Profile::Radarr,
        url,
        api_key: "k".to_owned(),
    };
    let http = reqwest::Client::new();
    let owner = owner_of_file(&http, &arr, 7).await.unwrap().unwrap();
    let start = Instant::now();
    let result = tokio::time::timeout(
        Duration::from_secs(15),
        replace(&http, &arr, Some("job"), &owner, wait, &mut resume),
    )
    .await
    .expect("replace finished within 15 s");
    let elapsed = start.elapsed();
    let log = calls.lock().unwrap().log.clone();
    (result, resume, log, elapsed)
}

#[tokio::test(flavor = "multi_thread")]
async fn without_a_grab_it_searches_at_once() {
    let (result, _, calls, _) = repair(Grab::Missing, Duration::from_secs(60), None).await;
    result.unwrap();
    assert_eq!(
        calls,
        [
            "GET /api/v3/moviefile/7",
            "GET /api/v3/queue/details?movieId=3",
            "GET /api/v3/history?downloadId=job&eventType=1&pageSize=1",
            "DELETE /api/v3/moviefile/7",
            "POST /api/v3/command",
        ]
    );
}

#[tokio::test(flavor = "multi_thread")]
async fn after_a_blocklist_it_waits_for_the_arr_search() {
    let (result, _, calls, _) = repair(
        Grab::Blocklists(Search::Queues),
        Duration::from_secs(60),
        None,
    )
    .await;
    result.unwrap();
    assert_eq!(
        calls,
        [
            "GET /api/v3/moviefile/7",
            "GET /api/v3/queue/details?movieId=3",
            "GET /api/v3/history?downloadId=job&eventType=1&pageSize=1",
            "GET /api/v3/history?eventType=1&pageSize=1&movieIds=3",
            "POST /api/v3/history/failed/12",
            "GET /api/v3/command",
            "GET /api/v3/queue/details?movieId=3",
        ]
    );
}

#[tokio::test(flavor = "multi_thread")]
async fn a_grab_by_the_arr_search_is_left_to_it() {
    let (result, _, calls, _) = repair(
        Grab::Blocklists(Search::Grabs),
        Duration::from_secs(60),
        None,
    )
    .await;
    result.unwrap();
    assert_eq!(
        calls,
        [
            "GET /api/v3/moviefile/7",
            "GET /api/v3/queue/details?movieId=3",
            "GET /api/v3/history?downloadId=job&eventType=1&pageSize=1",
            "GET /api/v3/history?eventType=1&pageSize=1&movieIds=3",
            "POST /api/v3/history/failed/12",
            "GET /api/v3/command",
            "GET /api/v3/history?eventType=1&pageSize=1&movieIds=3",
            "GET /api/v3/history?downloadId=other&eventType=4&pageSize=1",
        ]
    );
}

#[tokio::test(flavor = "multi_thread")]
async fn an_interactive_grab_with_redownload_off_searches_at_once() {
    let (result, _, calls, elapsed) =
        repair(Grab::Blocklists(Search::Off), Duration::from_secs(60), None).await;
    result.unwrap();
    assert!(elapsed < Duration::from_secs(5), "{elapsed:?}");
    assert_eq!(
        calls,
        [
            "GET /api/v3/moviefile/7",
            "GET /api/v3/queue/details?movieId=3",
            "GET /api/v3/history?downloadId=job&eventType=1&pageSize=1",
            "GET /api/v3/history?eventType=1&pageSize=1&movieIds=3",
            "POST /api/v3/history/failed/12",
            "GET /api/v3/command",
            "GET /api/v3/history?eventType=1&pageSize=1&movieIds=3",
            "GET /api/v3/queue/details?movieId=3",
            "DELETE /api/v3/moviefile/7",
            "POST /api/v3/command",
        ]
    );
}

#[tokio::test(flavor = "multi_thread")]
async fn a_book_keeps_its_file_while_its_author_is_searched() {
    async fn chaptarr(State(calls): State<Arc<Mutex<Calls>>>, req: Request) -> Response {
        let call = format!("{} {}", req.method(), req.uri());
        calls.lock().unwrap().log.push(call.clone());
        let file = json!({ "id": 7, "bookId": 8, "authorId": 2, "path": "/books/a.epub" });
        Json(match call.as_str() {
            "GET /api/v1/bookfile/7" => file,
            "GET /api/v1/bookfile?bookId=8" => json!([file]),
            "GET /api/v1/history?downloadId=job&eventType=3&pageSize=1000" => {
                json!({ "records": [{ "id": 11, "bookId": 8, "data": {
                    "droppedPath": "/completed/job/a.epub", "importedPath": "/books/a.epub"
                } }] })
            }
            "GET /api/v1/queue/details?bookIds=8" => json!([]),
            "GET /api/v1/command" => json!([{
                "name": "AuthorSearch", "status": "started", "body": { "authorId": 2 }
            }]),
            _ => return StatusCode::NOT_FOUND.into_response(),
        })
        .into_response()
    }

    let calls = Arc::new(Mutex::new(Calls::default()));
    let listener = TcpListener::bind("127.0.0.1:0").await.unwrap();
    let url = format!("http://{}", listener.local_addr().unwrap());
    let app = axum::Router::new()
        .fallback(chaptarr)
        .with_state(Arc::clone(&calls));
    tokio::spawn(async move { axum::serve(listener, app).await });
    let arr = Arr {
        id: uuid::Uuid::nil(),
        name: "chaptarr".to_owned(),
        profile: Profile::Chaptarr,
        url,
        api_key: "k".to_owned(),
    };
    let http = reqwest::Client::new();
    let dropped = ["job/a.epub".to_owned()];
    for owner in [
        owner_of_file(&http, &arr, 7).await.unwrap().unwrap(),
        owner(&http, &arr, "job", &dropped, Some(7))
            .await
            .unwrap()
            .unwrap(),
    ] {
        let result = replace(
            &http,
            &arr,
            Some("job"),
            &owner,
            Duration::from_millis(1),
            &mut Some(12),
        )
        .await;
        assert!(matches!(result, Err(Error::Searching)), "{result:?}");
    }
    let calls = &calls.lock().unwrap().log;
    assert!(
        !calls
            .iter()
            .any(|call| call.starts_with("DELETE ") || call.starts_with("POST ")),
        "{calls:?}"
    );
}

#[tokio::test(flavor = "multi_thread")]
async fn a_replacement_queued_by_the_time_the_search_ends_is_left_to_it() {
    let (result, _, calls, _) = repair(
        Grab::Blocklists(Search::QueuesLate),
        Duration::from_secs(60),
        None,
    )
    .await;
    result.unwrap();
    assert_eq!(
        calls,
        [
            "GET /api/v3/moviefile/7",
            "GET /api/v3/queue/details?movieId=3",
            "GET /api/v3/history?downloadId=job&eventType=1&pageSize=1",
            "GET /api/v3/history?eventType=1&pageSize=1&movieIds=3",
            "POST /api/v3/history/failed/12",
            "GET /api/v3/command",
            "GET /api/v3/history?eventType=1&pageSize=1&movieIds=3",
            "GET /api/v3/queue/details?movieId=3",
        ]
    );
}

#[tokio::test(flavor = "multi_thread")]
async fn it_deletes_only_after_the_arr_search_ends() {
    let (result, _, calls, _) = repair(
        Grab::Blocklists(Search::FindsNothing),
        Duration::from_secs(60),
        None,
    )
    .await;
    result.unwrap();
    assert_eq!(
        calls,
        [
            "GET /api/v3/moviefile/7",
            "GET /api/v3/queue/details?movieId=3",
            "GET /api/v3/history?downloadId=job&eventType=1&pageSize=1",
            "GET /api/v3/history?eventType=1&pageSize=1&movieIds=3",
            "POST /api/v3/history/failed/12",
            "GET /api/v3/command",
            "GET /api/v3/queue/details?movieId=3",
            "GET /api/v3/command",
            "GET /api/v3/history?eventType=1&pageSize=1&movieIds=3",
            "GET /api/v3/queue/details?movieId=3",
            "DELETE /api/v3/moviefile/7",
            "POST /api/v3/command",
        ]
    );
}

#[tokio::test(flavor = "multi_thread")]
async fn a_running_search_keeps_the_file_and_names_where_to_resume() {
    let wait = Duration::from_secs(1);
    let (result, resume, calls, elapsed) =
        repair(Grab::Blocklists(Search::Queued), wait, None).await;
    assert!(matches!(result, Err(Error::Searching)), "{result:?}");
    assert_eq!(resume, Some(12));
    assert!(elapsed >= wait, "returned after {elapsed:?}");
    // How many polls fit in the wait depends on the runner; only their kind is fixed.
    let (fresh, polls) = calls.split_at(5);
    assert_eq!(
        fresh,
        [
            "GET /api/v3/moviefile/7",
            "GET /api/v3/queue/details?movieId=3",
            "GET /api/v3/history?downloadId=job&eventType=1&pageSize=1",
            "GET /api/v3/history?eventType=1&pageSize=1&movieIds=3",
            "POST /api/v3/history/failed/12",
        ]
    );
    assert!(
        polls
            .iter()
            .all(|c| c == "GET /api/v3/command" || c == "GET /api/v3/queue/details?movieId=3"),
        "{calls:?}"
    );
}

#[tokio::test(flavor = "multi_thread")]
async fn a_resumed_repair_does_not_blocklist_again() {
    let (result, _, calls, _) = repair(
        Grab::Blocklists(Search::Grabs),
        Duration::from_secs(60),
        Some(12),
    )
    .await;
    result.unwrap();
    assert_eq!(
        calls,
        [
            "GET /api/v3/moviefile/7",
            "GET /api/v3/queue/details?movieId=3",
            "GET /api/v3/command",
            "GET /api/v3/history?eventType=1&pageSize=1&movieIds=3",
            "GET /api/v3/history?downloadId=other&eventType=4&pageSize=1",
        ]
    );
}

#[tokio::test(flavor = "multi_thread")]
async fn a_failed_newer_grab_does_not_count_as_a_replacement() {
    for resume in [None, Some(12)] {
        let (result, _, calls, _) = repair(
            Grab::Blocklists(Search::FailedGrab),
            Duration::from_secs(60),
            resume,
        )
        .await;
        result.unwrap();
        assert!(
            calls.ends_with(&[
                "DELETE /api/v3/moviefile/7".to_owned(),
                "POST /api/v3/command".to_owned(),
            ]),
            "{calls:?}"
        );
        assert_eq!(
            calls
                .iter()
                .filter(|call| call.starts_with("POST /api/v3/history/failed/"))
                .count(),
            usize::from(resume.is_none()),
            "{calls:?}"
        );
    }
}

#[tokio::test(flavor = "multi_thread")]
async fn an_older_failure_does_not_invalidate_a_newer_grab() {
    let (result, _, calls, _) = repair(
        Grab::Blocklists(Search::OlderFailure),
        Duration::from_secs(60),
        Some(12),
    )
    .await;
    result.unwrap();
    assert_eq!(
        calls.last().unwrap(),
        "GET /api/v3/history?downloadId=other&eventType=4&pageSize=1"
    );
    assert!(
        !calls
            .iter()
            .any(|call| call.starts_with("DELETE ") || call.starts_with("POST ")),
        "{calls:?}"
    );
}

#[tokio::test(flavor = "multi_thread")]
async fn an_unreadable_failure_history_keeps_the_file_and_resume() {
    let (result, resume, calls, _) = repair(
        Grab::Blocklists(Search::FailureUnavailable),
        Duration::from_secs(60),
        Some(12),
    )
    .await;
    let error = result.unwrap_err();
    assert!(error.is_transient());
    assert!(matches!(error, Error::Rejected { status, .. } if status == 503));
    assert_eq!(resume, Some(12));
    assert!(
        !calls
            .iter()
            .any(|call| call.starts_with("DELETE ") || call.starts_with("POST ")),
        "{calls:?}"
    );
}

#[tokio::test(flavor = "multi_thread")]
async fn a_grab_without_a_download_id_keeps_the_file() {
    let (result, resume, calls, _) = repair(
        Grab::Blocklists(Search::MissingDownloadId),
        Duration::from_secs(60),
        Some(12),
    )
    .await;
    assert!(matches!(
        result,
        Err(Error::Unexpected("grab history without downloadId"))
    ));
    assert_eq!(resume, Some(12));
    assert!(
        !calls
            .iter()
            .any(|call| call.starts_with("DELETE ") || call.starts_with("POST ")),
        "{calls:?}"
    );
}

#[tokio::test(flavor = "multi_thread")]
async fn a_failed_blocklist_neither_deletes_nor_searches() {
    let (result, resume, calls, _) =
        repair(Grab::BlocklistFails, Duration::from_secs(60), None).await;
    assert!(
        matches!(result, Err(Error::Rejected { status, .. }) if status == 500),
        "{result:?}"
    );
    assert_eq!(resume, None);
    assert_eq!(
        calls,
        [
            "GET /api/v3/moviefile/7",
            "GET /api/v3/queue/details?movieId=3",
            "GET /api/v3/history?downloadId=job&eventType=1&pageSize=1",
            "GET /api/v3/history?eventType=1&pageSize=1&movieIds=3",
            "POST /api/v3/history/failed/12",
        ]
    );
}
