//! Tells the RC server of an external rclone mount of the `WebDAV` root which cached
//! directory listings changed (`vfs/forget`), so new and removed entries show before
//! its directory cache expires. Best effort: rclone's cache time stays the fallback.

use std::collections::{BTreeMap, BTreeSet};
use std::fmt;
use std::time::Duration;

use anyhow::Context as _;
use infinidysk_db::ListingChange;

use crate::config::{RcloneRc, Secret};

/// The `WebDAV` roots whose listings change; `/view` is not one of them.
const ROOTS: [&str; 4] = ["nzbs", "content", "completed-symlinks", ".ids"];
/// The library roots: what a removal deep in the tree can change.
const TREE: [&str; 3] = ["content", "completed-symlinks", ".ids"];
/// More distinct directories than this forget all roots instead.
const MAX_DIRS: usize = 64;
/// Bound on a `vfs/forget` reply; a real one names only the requested directories.
const MAX_REPLY: usize = 1024 * 1024;
const ATTEMPT_TIMEOUT: Duration = Duration::from_secs(2);
const DELIVERY_TIMEOUT: Duration = Duration::from_secs(5);
const RETRY_AFTER: Duration = Duration::from_millis(250);

/// Mount-relative directories to forget in one request.
#[derive(Default)]
pub(crate) struct Batch {
    dirs: BTreeSet<String>,
    /// All roots are in; nothing more to add.
    full: bool,
}

impl Batch {
    /// Forgets every root.
    pub(crate) fn all(&mut self) {
        self.dirs = ROOTS.into_iter().map(str::to_owned).collect();
        self.full = true;
    }

    /// Forgets the library roots.
    pub(crate) fn tree(&mut self) {
        for root in TREE {
            self.add(root);
        }
    }

    pub(crate) fn record(&mut self, event: ListingChange) {
        match event {
            ListingChange::Nzbs => self.add("nzbs"),
            ListingChange::Completed => self.add("completed-symlinks"),
            ListingChange::Tree => self.tree(),
            ListingChange::Link(path) => {
                if let Some(tail) = path.strip_prefix("/content/") {
                    let parent = tail.rsplit_once('/').map_or("", |(dir, _)| dir);
                    self.add(format!("completed-symlinks/{parent}").trim_end_matches('/'));
                }
            }
            ListingChange::Items(items) => {
                for (path, file) in items.iter() {
                    let Some(tail) = path.strip_prefix("/content/") else {
                        continue;
                    };
                    let dir = if let Some(id) = file {
                        if let Some((shard, _)) = id.ids_path().rsplit_once('/') {
                            self.add(shard);
                        }
                        tail.rsplit_once('/').map_or("", |(parent, _)| parent)
                    } else {
                        tail
                    };
                    self.add(format!("content/{dir}").trim_end_matches('/'));
                    self.add(format!("completed-symlinks/{dir}").trim_end_matches('/'));
                }
            }
        }
    }

    pub(crate) const fn dirs(&self) -> &BTreeSet<String> {
        &self.dirs
    }

    fn add(&mut self, dir: &str) {
        if self.full || dir.is_empty() || self.dirs.contains(dir) {
            return;
        }
        // Forgetting a directory drops its cached descendants too.
        if self.dirs.iter().any(|d| below(dir, d)) {
            return;
        }
        self.dirs.retain(|d| !below(d, dir));
        if self.dirs.len() >= MAX_DIRS {
            self.all();
        } else {
            self.dirs.insert(dir.to_owned());
        }
    }
}

/// Whether `dir` lies under `ancestor`, by whole path components.
fn below(dir: &str, ancestor: &str) -> bool {
    dir.strip_prefix(ancestor)
        .is_some_and(|rest| rest.starts_with('/'))
}

/// Why a `vfs/forget` failed; the reasons never echo the server, URL or credentials.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub(crate) enum Failure {
    Transport,
    TimedOut,
    Busy,
    Server,
    Auth,
    Rejected,
    InvalidReply,
}

impl fmt::Display for Failure {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        f.write_str(match self {
            Self::Transport => "could not reach the RC server",
            Self::TimedOut => "RC request timed out",
            Self::Busy => "RC server is busy",
            Self::Server => "RC server returned a server error",
            Self::Auth => "RC credentials were rejected",
            Self::Rejected => "RC endpoint rejected the request; check the base URL and VFS name",
            Self::InvalidReply => "response did not confirm the requested directories",
        })
    }
}

#[derive(serde::Deserialize)]
struct Forgotten {
    forgotten: Vec<String>,
}

/// The RC server of one rclone mount. No `Debug`: it holds the RC password.
pub(crate) struct RcClient {
    http: reqwest::Client,
    url: reqwest::Url,
    fs: String,
    user: String,
    password: Secret,
}

impl RcClient {
    pub(crate) fn new(rc: &RcloneRc) -> anyhow::Result<Self> {
        let url = rc.forget_url().context("reading the rclone RC URL")?;
        let mut http = reqwest::Client::builder()
            .redirect(reqwest::redirect::Policy::none())
            .timeout(ATTEMPT_TIMEOUT);
        // Plain HTTP is only allowed to loopback; a proxy would take it off the host.
        if url.scheme() == "http" {
            http = http.no_proxy();
        }
        Ok(Self {
            http: http.build().context("building the rclone RC client")?,
            url,
            fs: rc.fs.clone(),
            user: rc.user.clone(),
            password: rc.password.clone(),
        })
    }

    /// Forgets `dirs`, retrying a transient failure once; bounded in time.
    pub(crate) async fn deliver(&self, dirs: &BTreeSet<String>) -> Result<(), Failure> {
        tokio::time::timeout(DELIVERY_TIMEOUT, async {
            match self.once(dirs).await {
                Err(Failure::TimedOut | Failure::Transport | Failure::Busy | Failure::Server) => {
                    tokio::time::sleep(RETRY_AFTER).await;
                    self.once(dirs).await
                }
                result => result,
            }
        })
        .await
        .unwrap_or(Err(Failure::TimedOut))
    }

    async fn once(&self, dirs: &BTreeSet<String>) -> Result<(), Failure> {
        // An empty `vfs/forget` forgets everything.
        if dirs.is_empty() {
            return Ok(());
        }
        let mut body = BTreeMap::new();
        for (index, dir) in dirs.iter().enumerate() {
            let key = if index == 0 {
                "dir".to_owned()
            } else {
                format!("dir{}", index + 1)
            };
            body.insert(key, dir.as_str());
        }
        if !self.fs.is_empty() {
            body.insert("fs".to_owned(), self.fs.as_str());
        }
        let mut post = self.http.post(self.url.clone()).json(&body);
        if !self.user.is_empty() {
            post = post.basic_auth(&self.user, Some(&self.password.0));
        }
        let classify = |error: reqwest::Error| {
            if error.is_timeout() {
                Failure::TimedOut
            } else {
                Failure::Transport
            }
        };
        let mut response = post.send().await.map_err(classify)?;
        match response.status().as_u16() {
            200..=299 => {}
            401 | 403 => return Err(Failure::Auth),
            408 | 429 => return Err(Failure::Busy),
            500..=599 => return Err(Failure::Server),
            _ => return Err(Failure::Rejected),
        }
        let mut bytes = Vec::new();
        while let Some(chunk) = response.chunk().await.map_err(classify)? {
            if chunk.len() > MAX_REPLY.saturating_sub(bytes.len()) {
                return Err(Failure::InvalidReply);
            }
            bytes.extend_from_slice(&chunk);
        }
        let reply: Forgotten = serde_json::from_slice(&bytes).map_err(|_| Failure::InvalidReply)?;
        if reply.forgotten.into_iter().collect::<BTreeSet<_>>() != *dirs {
            return Err(Failure::InvalidReply);
        }
        Ok(())
    }
}

#[cfg(test)]
mod tests {
    use std::collections::VecDeque;
    use std::net::SocketAddr;
    use std::sync::atomic::{AtomicUsize, Ordering};
    use std::sync::{Arc, Mutex};

    use axum::Router;
    use axum::http::{HeaderMap, StatusCode, header};
    use axum::response::{IntoResponse, Response};
    use axum::routing::{any, post};
    use base64::Engine as _;
    use infinidysk_core::ItemId;
    use serde_json::{Map, Value};
    use uuid::Uuid;

    use super::*;

    async fn serve(router: Router) -> SocketAddr {
        let listener = tokio::net::TcpListener::bind("127.0.0.1:0").await.unwrap();
        let addr = listener.local_addr().unwrap();
        tokio::spawn(async move { axum::serve(listener, router).await });
        addr
    }

    fn client(addr: SocketAddr, path: &str, fs: &str, user: &str, password: &str) -> RcClient {
        RcClient::new(&RcloneRc {
            enabled: true,
            url: format!("http://{addr}{path}"),
            fs: fs.to_owned(),
            user: user.to_owned(),
            password: Secret(password.to_owned()),
        })
        .unwrap()
    }

    /// What rclone answers: every requested directory, in any order.
    fn forgotten(body: &Map<String, Value>) -> Value {
        let mut dirs: Vec<&Value> = body
            .iter()
            .filter(|(key, _)| key.starts_with("dir"))
            .map(|(_, dir)| dir)
            .collect();
        dirs.reverse();
        serde_json::json!({ "forgotten": dirs })
    }

    type Seen = Arc<Mutex<Vec<(Map<String, Value>, Option<String>)>>>;

    fn recorder(seen: Seen) -> Router {
        Router::new().route(
            "/rc/base/vfs/forget",
            post(
                move |headers: HeaderMap, axum::Json(body): axum::Json<Map<String, Value>>| {
                    let seen = Arc::clone(&seen);
                    async move {
                        let auth = headers
                            .get(header::AUTHORIZATION)
                            .and_then(|v| v.to_str().ok())
                            .map(str::to_owned);
                        let reply = forgotten(&body);
                        seen.lock().unwrap().push((body, auth));
                        axum::Json(reply)
                    }
                },
            ),
        )
    }

    fn set(dirs: &[&str]) -> BTreeSet<String> {
        dirs.iter().map(|d| (*d).to_owned()).collect()
    }

    #[tokio::test]
    async fn forget_uses_mount_relative_dirs_and_preserves_the_rc_base_path() {
        let seen = Seen::default();
        let addr = serve(recorder(Arc::clone(&seen))).await;
        let rc = client(addr, "/rc/base", "library:", "rc", "rc-secret");

        let id = ItemId(Uuid::from_u128(0xabcd_ef01_2345_6789_abcd_ef01_2345_6789));
        let mut batch = Batch::default();
        batch.record(ListingChange::Items(
            vec![
                ("/content/tv/Some Job/episode.mkv".to_owned(), Some(id)),
                ("/content/tv/Some Job".to_owned(), None),
            ]
            .into(),
        ));
        batch.record(ListingChange::Link(
            "/content/tv/Some Job/episode.mkv".to_owned(),
        ));
        let shard = id.ids_path().rsplit_once('/').unwrap().0.to_owned();
        let expected = set(&[
            "content/tv/Some Job",
            "completed-symlinks/tv/Some Job",
            &shard,
        ]);
        assert_eq!(*batch.dirs(), expected);
        assert!(shard.starts_with(".ids/"));

        rc.deliver(batch.dirs()).await.unwrap();

        let seen = std::mem::take(&mut *seen.lock().unwrap());
        assert_eq!(seen.len(), 1);
        let (body, auth) = &seen[0];
        let keys: BTreeSet<&str> = body.keys().map(String::as_str).collect();
        assert_eq!(keys, BTreeSet::from(["dir", "dir2", "dir3", "fs"]));
        assert_eq!(body["fs"], "library:");
        let dirs: BTreeSet<String> = body
            .iter()
            .filter(|(key, _)| key.starts_with("dir"))
            .map(|(_, dir)| dir.as_str().unwrap().to_owned())
            .collect();
        assert_eq!(dirs, expected);
        let basic = base64::engine::general_purpose::STANDARD.encode("rc:rc-secret");
        assert_eq!(auth.as_deref(), Some(format!("Basic {basic}").as_str()));
    }

    #[tokio::test]
    async fn empty_and_overflow_batches_never_send_an_unqualified_forget() {
        let seen = Seen::default();
        let addr = serve(recorder(Arc::clone(&seen))).await;
        let rc = client(addr, "/rc/base/", "", "", "");

        rc.deliver(Batch::default().dirs()).await.unwrap();
        assert!(seen.lock().unwrap().is_empty());

        // An explicit root covers what lies below it, by whole components.
        let mut batch = Batch::default();
        batch.record(ListingChange::Items(
            vec![("/content/tv".to_owned(), None)].into(),
        ));
        batch.record(ListingChange::Items(
            vec![("/content/tv-extra".to_owned(), None)].into(),
        ));
        batch.add("contents");
        batch.tree();
        assert_eq!(
            *batch.dirs(),
            set(&["content", "completed-symlinks", ".ids", "contents"])
        );

        let mut batch = Batch::default();
        for n in 0..=MAX_DIRS {
            batch.record(ListingChange::Items(
                vec![(format!("/content/cat-{n}"), None)].into(),
            ));
        }
        let roots = set(&ROOTS);
        assert_eq!(*batch.dirs(), roots);
        batch.record(ListingChange::Nzbs);
        batch.record(ListingChange::Items(
            vec![("/content/later".to_owned(), None)].into(),
        ));
        assert_eq!(*batch.dirs(), roots);

        let mut lagged = Batch::default();
        lagged.all();
        assert_eq!(*lagged.dirs(), roots);

        rc.deliver(batch.dirs()).await.unwrap();
        let seen = std::mem::take(&mut *seen.lock().unwrap());
        assert_eq!(seen.len(), 1);
        let (body, auth) = &seen[0];
        assert!(!body.contains_key("fs"));
        assert!(auth.is_none());
        let sent: BTreeSet<String> = body
            .values()
            .map(|dir| dir.as_str().unwrap().to_owned())
            .collect();
        assert_eq!(sent, roots);
    }

    enum Reply {
        Status(StatusCode),
        Redirect(String),
        Html,
        Forgotten,
    }

    async fn scripted(replies: Vec<Reply>) -> (SocketAddr, Arc<AtomicUsize>) {
        let calls = Arc::new(AtomicUsize::new(0));
        let replies = Arc::new(Mutex::new(VecDeque::from(replies)));
        let router = Router::new().route("/vfs/forget", {
            let calls = Arc::clone(&calls);
            post(move |axum::Json(body): axum::Json<Map<String, Value>>| {
                let calls = Arc::clone(&calls);
                let replies = Arc::clone(&replies);
                async move {
                    calls.fetch_add(1, Ordering::SeqCst);
                    let reply = replies.lock().unwrap().pop_front();
                    let response: Response = match reply {
                        Some(Reply::Status(status)) => status.into_response(),
                        Some(Reply::Redirect(to)) => {
                            (StatusCode::FOUND, [(header::LOCATION, to)]).into_response()
                        }
                        Some(Reply::Html) => {
                            axum::response::Html("<html>sign in</html>").into_response()
                        }
                        Some(Reply::Forgotten) | None => {
                            axum::Json(forgotten(&body)).into_response()
                        }
                    };
                    response
                }
            })
        });
        (serve(router).await, calls)
    }

    #[tokio::test]
    async fn retries_transient_failure_but_not_auth_or_redirect() {
        let dirs = set(&["nzbs"]);

        let (addr, calls) = scripted(vec![
            Reply::Status(StatusCode::SERVICE_UNAVAILABLE),
            Reply::Forgotten,
        ])
        .await;
        client(addr, "", "", "", "").deliver(&dirs).await.unwrap();
        assert_eq!(calls.load(Ordering::SeqCst), 2);

        let (addr, calls) = scripted(vec![Reply::Status(StatusCode::UNAUTHORIZED)]).await;
        let failure = client(addr, "", "", "rc", "wrong")
            .deliver(&dirs)
            .await
            .unwrap_err();
        assert_eq!(failure, Failure::Auth);
        assert_eq!(failure.to_string(), "RC credentials were rejected");
        assert_eq!(calls.load(Ordering::SeqCst), 1);

        let elsewhere = Arc::new(AtomicUsize::new(0));
        let target = serve(Router::new().fallback(any({
            let elsewhere = Arc::clone(&elsewhere);
            move || {
                elsewhere.fetch_add(1, Ordering::SeqCst);
                async { axum::Json(serde_json::json!({ "forgotten": ["nzbs"] })) }
            }
        })))
        .await;
        let (addr, calls) =
            scripted(vec![Reply::Redirect(format!("http://{target}/vfs/forget"))]).await;
        let failure = client(addr, "", "", "", "")
            .deliver(&dirs)
            .await
            .unwrap_err();
        assert_eq!(failure, Failure::Rejected);
        assert_eq!(
            failure.to_string(),
            "RC endpoint rejected the request; check the base URL and VFS name"
        );
        assert_eq!(calls.load(Ordering::SeqCst), 1);
        assert_eq!(elsewhere.load(Ordering::SeqCst), 0);

        let (addr, calls) = scripted(vec![Reply::Html]).await;
        let failure = client(addr, "", "", "", "")
            .deliver(&dirs)
            .await
            .unwrap_err();
        assert_eq!(failure, Failure::InvalidReply);
        assert_eq!(
            failure.to_string(),
            "response did not confirm the requested directories"
        );
        assert_eq!(calls.load(Ordering::SeqCst), 1);
    }
}
