//! HTTP client for the Arr apps: which app answers, which SABnzbd download clients it
//! holds, and the calls health repair needs.

pub mod inventory;
pub mod library;
pub mod queue;
mod repair;
pub mod webhook;

use infinidysk_core::media::Kind;
use reqwest::{Method, RequestBuilder};
use serde::Deserialize;
use serde::de::DeserializeOwned;
use serde_json::Value;
use uuid::Uuid;

pub use repair::{Import, Owner, all_imports, imports, owner, owner_of_file, replace};

/// What the Arr shows in place of a stored secret.
const MASK: &str = "********";
static NULL: Value = Value::Null;

/// An enabled Arr instance.
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct Arr {
    pub id: Uuid,
    pub name: String,
    pub profile: Profile,
    /// Base URL, e.g. `http://sonarr:8989`.
    pub url: String,
    pub api_key: String,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq, Hash)]
pub enum Profile {
    Sonarr,
    Radarr,
    Lidarr,
    Chaptarr,
    Whisparr,
    Sportarr,
}

impl Profile {
    /// Root of the endpoints we call; Sportarr's are its native ones.
    const fn root(self) -> &'static str {
        match self {
            Self::Lidarr | Self::Chaptarr => "api/v1",
            Self::Sportarr => "api",
            Self::Sonarr | Self::Radarr | Self::Whisparr => "api/v3",
        }
    }

    /// The `appName` its system status reports.
    const fn app_name(self) -> &'static str {
        match self {
            Self::Sonarr => "Sonarr",
            Self::Radarr => "Radarr",
            Self::Lidarr => "Lidarr",
            Self::Chaptarr => "Chaptarr",
            Self::Whisparr => "Whisparr",
            Self::Sportarr => "Sportarr",
        }
    }

    /// What its categories import; any one of them satisfies it.
    #[must_use]
    pub const fn media(self) -> &'static [Kind] {
        match self {
            Self::Lidarr => &[Kind::Audio],
            Self::Chaptarr => &[Kind::Book, Kind::Audio],
            Self::Sonarr | Self::Radarr | Self::Whisparr | Self::Sportarr => &[Kind::Video],
        }
    }

    /// Health repair can ask it for a replacement.
    // ponytail: Sportarr (own API, no download ids in history) has no repair; add a
    // `repair::Shape` for it.
    #[must_use]
    pub const fn repairs(self) -> bool {
        matches!(
            self,
            Self::Sonarr | Self::Radarr | Self::Lidarr | Self::Chaptarr | Self::Whisparr
        )
    }
}

#[derive(Debug, thiserror::Error)]
pub enum Error {
    #[error("{}", with_cause(.0))]
    Http(#[from] reqwest::Error),
    #[error("{status} for {url}: {reason}")]
    Rejected {
        status: reqwest::StatusCode,
        url: String,
        reason: String,
    },
    #[error("unexpected response: {0}")]
    Unexpected(&'static str),
    #[error("it answers as {found}, not {expected}")]
    WrongApp {
        expected: &'static str,
        found: String,
    },
    #[error("its API {0} is newer than this version of infinidysk supports")]
    Unsupported(String),
    /// Its search of the media still ran when repair stopped waiting.
    #[error("its own search for a replacement is still running")]
    Searching,
}

impl Error {
    /// The Arr was unreachable, slow or failing, or dropped the connection mid-request,
    /// so retrying later may work.
    #[must_use]
    pub fn is_transient(&self) -> bool {
        match self {
            Self::Http(e) => {
                e.is_connect()
                    || e.is_timeout()
                    || e.status().is_some_and(|s| s.is_server_error())
                    || dropped(e)
            }
            Self::Rejected { status, .. } => status.is_server_error(),
            Self::Unexpected(_)
            | Self::WrongApp { .. }
            | Self::Unsupported(_)
            | Self::Searching => false,
        }
    }
}

/// The connection failed while sending the request or reading the response body. A
/// body cut short reaches JSON decoding as a body error in the cause chain; malformed
/// JSON that arrived whole does not.
fn dropped(error: &reqwest::Error) -> bool {
    let mut cause: Option<&(dyn std::error::Error + 'static)> = Some(error);
    while let Some(current) = cause {
        if let Some(e) = current.downcast_ref::<reqwest::Error>()
            && (e.is_request() || e.is_body())
        {
            return true;
        }
        cause = current.source();
    }
    false
}

/// reqwest's own message hides why a request failed, such as a refused connection.
fn with_cause(error: &reqwest::Error) -> String {
    let Some(mut root) = std::error::Error::source(error) else {
        return error.to_string();
    };
    while let Some(source) = root.source() {
        root = source;
    }
    format!("{error}: {root}")
}

/// A SABnzbd download client configured in an Arr.
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct SabClient {
    pub id: i64,
    pub name: String,
    pub enabled: bool,
    /// The categories it downloads into, each with the media the Arr imports from it.
    pub categories: Vec<(String, &'static [Kind])>,
    pub host: String,
    pub port: u16,
    pub use_ssl: bool,
    pub url_base: String,
    /// `None` when the Arr masks or lacks it.
    pub api_key: Option<String>,
}

#[derive(Deserialize)]
#[serde(rename_all = "camelCase")]
struct SystemStatus {
    app_name: Option<String>,
    version: Option<String>,
    sportarr_version: Option<String>,
}

/// Checks the instance answers as its profile and returns its version.
pub async fn version(http: &reqwest::Client, arr: &Arr) -> Result<String, Error> {
    let path = match arr.profile {
        Profile::Sportarr => "api/v3/system/status".to_owned(),
        profile => format!("{}/system/status", profile.root()),
    };
    let status: SystemStatus = fetch(request(http, arr, Method::GET, &path)).await?;
    let found = if status.sportarr_version.is_some() {
        "Sportarr"
    } else {
        status.app_name.as_deref().unwrap_or("an unknown app")
    };
    let expected = arr.profile.app_name();
    if !found.eq_ignore_ascii_case(expected) {
        return Err(Error::WrongApp {
            expected,
            found: found.to_owned(),
        });
    }
    Ok(status
        .sportarr_version
        .or(status.version)
        .unwrap_or_default())
}

/// Whisparr from v3 is built on Radarr (movies); before, on Sonarr (series).
fn whisparr_has_movies(version: &str) -> bool {
    version
        .split('.')
        .next()
        .and_then(|major| major.parse::<u32>().ok())
        .is_some_and(|major| major >= 3)
}

/// The SABnzbd download clients the instance holds.
pub async fn sab_clients(http: &reqwest::Client, arr: &Arr) -> Result<Vec<SabClient>, Error> {
    let s = Session::open(http, arr).await?;
    let clients: Vec<Value> = fetch(s.call(Method::GET, "downloadclient")).await?;
    Ok(clients
        .iter()
        .filter_map(|c| sab_client(c, arr.profile))
        .collect())
}

/// Reads one download client: Servarr resources keep settings in `fields`, Sportarr's
/// native ones at the top level.
fn sab_client(client: &Value, profile: Profile) -> Option<SabClient> {
    let native = profile == Profile::Sportarr;
    let sab = if native {
        client["type"].as_i64() == Some(5)
            || client["type"]
                .as_str()
                .is_some_and(|t| t.eq_ignore_ascii_case("sabnzbd"))
    } else {
        client["implementation"]
            .as_str()
            .is_some_and(|i| i.eq_ignore_ascii_case("sabnzbd"))
    };
    if !sab {
        return None;
    }
    let get = |name: &str| -> &Value {
        if native {
            &client[name]
        } else {
            client["fields"]
                .as_array()
                .and_then(|fields| fields.iter().find(|f| f["name"] == name))
                .map_or(&NULL, |f| &f["value"])
        }
    };
    let text = |name: &str| get(name).as_str().unwrap_or_default().to_owned();
    let category = |keys: &[&str]| {
        keys.iter()
            .find_map(|k| get(k).as_str())
            .filter(|c| !c.is_empty())
            .map(str::to_owned)
    };
    let mut categories: Vec<(String, &'static [Kind])> = Vec::new();
    if profile == Profile::Chaptarr {
        // Each falls back to the hidden `musicCategory`; one category may serve both.
        for (key, media) in [
            ("audiobookCategory", &[Kind::Audio][..]),
            ("ebookCategory", &[Kind::Book][..]),
        ] {
            let Some(name) = category(&[key, "musicCategory"]) else {
                continue;
            };
            match categories
                .iter_mut()
                .find(|(c, _)| c.eq_ignore_ascii_case(&name))
            {
                Some(both) => both.1 = profile.media(),
                None => categories.push((name, media)),
            }
        }
    } else if let Some(name) =
        category(&["category", "tvCategory", "movieCategory", "musicCategory"])
    {
        categories.push((name, profile.media()));
    }
    let api_key = text("apiKey");
    Some(SabClient {
        id: client["id"].as_i64()?,
        name: client["name"].as_str().unwrap_or_default().to_owned(),
        enabled: client[if native { "enabled" } else { "enable" }]
            .as_bool()
            .unwrap_or(false),
        categories,
        host: text("host"),
        port: get("port")
            .as_u64()
            .and_then(|p| u16::try_from(p).ok())
            .unwrap_or(0),
        use_ssl: get("useSsl").as_bool().unwrap_or(false),
        url_base: text("urlBase"),
        api_key: (!api_key.is_empty() && api_key != MASK).then_some(api_key),
    })
}

/// Whether Lidarr writes tags into the audio files it imports; files here cannot change.
/// Always `false` for other profiles.
pub async fn writes_audio_tags(http: &reqwest::Client, arr: &Arr) -> Result<bool, Error> {
    if arr.profile != Profile::Lidarr {
        return Ok(false);
    }
    let config: Value = fetch(request(
        http,
        arr,
        Method::GET,
        "api/v1/config/metadataprovider",
    ))
    .await?;
    Ok(config["writeAudioTags"]
        .as_str()
        .is_some_and(|v| !v.eq_ignore_ascii_case("no")))
}

/// Has the Arr test its download client `id` with `marker` appended to the client's URL
/// base, so the test's calls arrive at `…/<marker>/api`. The test's verdict is ignored:
/// whether those calls arrived is the evidence.
pub async fn probe(http: &reqwest::Client, arr: &Arr, id: i64, marker: &str) -> Result<(), Error> {
    let s = Session::open(http, arr).await?;
    let mut client: Value = fetch(s.call(Method::GET, &format!("downloadclient/{id}"))).await?;
    let base = client["fields"]
        .as_array_mut()
        .and_then(|fields| fields.iter_mut().find(|f| f["name"] == "urlBase"))
        .ok_or(Error::Unexpected("download client without urlBase"))?;
    let value = format!(
        "{}/{marker}",
        base["value"]
            .as_str()
            .unwrap_or_default()
            .trim_end_matches('/')
    );
    base["value"] = Value::String(value);
    s.call(Method::POST, "downloadclient/test")
        .json(&client)
        .send()
        .await?;
    Ok(())
}

/// Asks the Arr to check its download clients now rather than at its next poll. Sportarr
/// has no such command and polls on its own.
pub async fn refresh_downloads(http: &reqwest::Client, arr: &Arr) -> Result<(), Error> {
    if arr.profile == Profile::Sportarr {
        return Ok(());
    }
    let s = Session::open(http, arr).await?;
    send(
        s.call(Method::POST, "command")
            .json(&serde_json::json!({ "name": "RefreshMonitoredDownloads" })),
    )
    .await
}

/// Which of an instance's APIs an operation talks to.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
enum Api {
    /// The profile's long-standing one (see [`Profile::root`]).
    Classic,
    /// Sonarr v5's, which marks v3 deprecated.
    V5,
}

#[derive(Deserialize)]
struct ApiInfo {
    current: String,
    #[serde(default)]
    deprecated: Vec<String>,
}

/// The newest API both sides speak, from Sonarr's `GET /api`. Without that answer it is a
/// Sonarr that only has v3.
// ponytail: one extra request per operation; cache it per instance if that ever shows up.
async fn api(http: &reqwest::Client, arr: &Arr) -> Result<Api, Error> {
    if arr.profile != Profile::Sonarr {
        return Ok(Api::Classic);
    }
    let response = match accepted(request(http, arr, Method::GET, "api")).await {
        Err(Error::Rejected {
            status: reqwest::StatusCode::NOT_FOUND,
            ..
        }) => return Ok(Api::Classic),
        other => other?,
    };
    let Ok(info) = serde_json::from_slice::<ApiInfo>(&response.bytes().await?) else {
        return Ok(Api::Classic);
    };
    let offered = |v: &str| info.current == v || info.deprecated.iter().any(|d| d == v);
    if offered("v5") {
        Ok(Api::V5)
    } else if offered("v3") {
        Ok(Api::Classic)
    } else {
        Err(Error::Unsupported(info.current))
    }
}

/// An instance with its API settled once for one operation, so the calls of an operation
/// never mix two APIs.
#[derive(Clone, Copy)]
struct Session<'a> {
    http: &'a reqwest::Client,
    arr: &'a Arr,
    api: Api,
}

impl<'a> Session<'a> {
    async fn open(http: &'a reqwest::Client, arr: &'a Arr) -> Result<Self, Error> {
        Ok(Self {
            http,
            arr,
            api: api(http, arr).await?,
        })
    }

    /// A call under the API root.
    fn call(&self, method: Method, path: &str) -> RequestBuilder {
        let root = match self.api {
            Api::Classic => self.arr.profile.root(),
            Api::V5 => "api/v5",
        };
        request(self.http, self.arr, method, &format!("{root}/{path}"))
    }
}

fn request(http: &reqwest::Client, arr: &Arr, method: Method, path: &str) -> RequestBuilder {
    let request = http
        .request(method, format!("{}/{path}", arr.url.trim_end_matches('/')))
        .header("X-Api-Key", &arr.api_key);
    // Sonarr records this as the source of the blocklist entries we add.
    if arr.profile == Profile::Sonarr {
        request.header("X-Sonarr-Client", "infinidysk")
    } else {
        request
    }
}

async fn fetch<T: DeserializeOwned>(request: RequestBuilder) -> Result<T, Error> {
    Ok(accepted(request).await?.json().await?)
}

async fn send(request: RequestBuilder) -> Result<(), Error> {
    accepted(request).await?;
    Ok(())
}

async fn accepted(request: RequestBuilder) -> Result<reqwest::Response, Error> {
    let response = request.send().await?;
    let status = response.status();
    if !status.is_client_error() && !status.is_server_error() {
        return Ok(response);
    }
    let url = response.url().to_string();
    let body = response.text().await.unwrap_or_default();
    Err(Error::Rejected {
        status,
        url,
        reason: reason(&body),
    })
}

/// Why the Arr refused, from its error body. Servarr validation failures also carry an
/// `attemptedValue`, which is left out: it can be a password or API key.
fn reason(body: &str) -> String {
    let reason = match serde_json::from_str::<Value>(body) {
        Ok(Value::Array(failures)) => failures
            .iter()
            .filter_map(|f| {
                let message = f["errorMessage"].as_str()?;
                Some(match f["propertyName"].as_str().filter(|p| !p.is_empty()) {
                    Some(property) => format!("{property}: {message}"),
                    None => message.to_owned(),
                })
            })
            .collect::<Vec<_>>()
            .join("; "),
        Ok(v) => ["message", "error", "title"]
            .into_iter()
            .find_map(|k| v[k].as_str())
            .unwrap_or_default()
            .to_owned(),
        Err(_) => body.trim().to_owned(),
    };
    if reason.is_empty() {
        return "no details given".to_owned();
    }
    reason.chars().take(300).collect()
}

#[cfg(test)]
mod tests {
    use serde_json::json;

    use super::*;

    #[test]
    fn reads_why_an_arr_refused() {
        let failures = json!([
            { "propertyName": "Name", "errorMessage": "Should be unique", "attemptedValue": "hook" },
            { "propertyName": "Password", "errorMessage": "Required", "attemptedValue": "secret" },
        ]);
        assert_eq!(
            reason(&failures.to_string()),
            "Name: Should be unique; Password: Required"
        );
        assert_eq!(reason(r#"{"message":"Not Found"}"#), "Not Found");
        assert_eq!(reason(""), "no details given");
    }

    #[test]
    fn reads_native_sportarr_clients() {
        let sab = json!({
            "id": 4, "name": "sab", "type": 5, "enabled": true, "host": "h", "port": 3000,
            "useSsl": true, "urlBase": "/x", "apiKey": "key", "category": "sports",
        });
        let client = sab_client(&sab, Profile::Sportarr).unwrap();
        assert_eq!(
            client,
            SabClient {
                id: 4,
                name: "sab".to_owned(),
                enabled: true,
                categories: vec![("sports".to_owned(), &[Kind::Video][..])],
                host: "h".to_owned(),
                port: 3000,
                use_ssl: true,
                url_base: "/x".to_owned(),
                api_key: Some("key".to_owned()),
            }
        );
        assert!(sab_client(&json!({ "id": 5, "type": 1 }), Profile::Sportarr).is_none());
    }

    #[test]
    fn chaptarr_clients_split_audiobook_and_ebook_categories() {
        let sab = |audio: &str, ebook: &str| {
            let client = json!({
                "id": 1, "name": "sab", "implementation": "Sabnzbd", "enable": true,
                "fields": [
                    { "name": "audiobookCategory", "value": audio },
                    { "name": "ebookCategory", "value": ebook },
                    { "name": "musicCategory", "value": "books" },
                ],
            });
            sab_client(&client, Profile::Chaptarr).unwrap().categories
        };
        assert_eq!(
            sab("audiobooks", "ebooks"),
            [
                ("audiobooks".to_owned(), &[Kind::Audio][..]),
                ("ebooks".to_owned(), &[Kind::Book][..]),
            ]
        );
        assert_eq!(
            sab("books", "Books"),
            [("books".to_owned(), &[Kind::Book, Kind::Audio][..])]
        );
        assert_eq!(sab("", ""), []);
    }
}
