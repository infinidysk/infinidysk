//! Tells whether the public image registry has a release newer than this build.

use std::sync::{Mutex, PoisonError};
use std::time::Duration;

use reqwest::header::{HeaderMap, LINK};
use semver::Version;
use serde::{Deserialize, Serialize};
use tokio::time::MissedTickBehavior;
use tokio_util::sync::CancellationToken;
use utoipa::ToSchema;

pub const VERSION: &str = include_str!("../../../version.txt").trim_ascii();
/// The full commit this binary was built from, when the build passed one.
pub const COMMIT: Option<&str> = match option_env!("INFINIDYSK_COMMIT") {
    Some(commit) if !commit.is_empty() => Some(commit),
    _ => None,
};
const CHECK_EVERY: Duration = Duration::from_hours(1);
const REGISTRY: &str = "https://ghcr.io";
const IMAGE: &str = "infinidysk/infinidysk-next";
/// Bounds a registry that keeps answering with a next page; 1000 tags each.
const MAX_PAGES: usize = 100;
/// The release workflow publishes this page before it pushes the image tag.
const RELEASE_URL: &str = "https://github.com/infinidysk/infinidysk/releases/tag/";

#[derive(Clone, Serialize, ToSchema)]
pub struct Update {
    /// Image tag of the newer release.
    pub version: String,
    /// The GitHub release notes of that release.
    pub url: String,
}

#[derive(Default)]
pub struct Updates(Mutex<Option<Update>>);

impl Updates {
    pub fn get(&self) -> Option<Update> {
        self.0
            .lock()
            .unwrap_or_else(PoisonError::into_inner)
            .clone()
    }

    /// Checks at once and then hourly until `cancel`; does nothing for a build without a commit.
    pub async fn run(&self, cancel: CancellationToken) {
        let Some(commit) = COMMIT else {
            return;
        };
        let current = match Version::parse(VERSION) {
            Ok(current) => current,
            Err(error) => {
                tracing::warn!(reason = %error, version = VERSION, "cannot check for a newer build");
                return;
            }
        };
        let http = match reqwest::Client::builder()
            .user_agent(format!("infinidysk/{VERSION}"))
            .timeout(Duration::from_secs(10))
            .build()
        {
            Ok(http) => http,
            Err(error) => {
                tracing::warn!(reason = %error, "cannot check for a newer build");
                return;
            }
        };
        let mut tick = tokio::time::interval(CHECK_EVERY);
        tick.set_missed_tick_behavior(MissedTickBehavior::Delay);
        loop {
            tokio::select! {
                () = cancel.cancelled() => return,
                _ = tick.tick() => {}
            }
            match check(&http, commit, &current).await {
                Ok(update) => *self.0.lock().unwrap_or_else(PoisonError::into_inner) = update,
                Err(error) => tracing::warn!(reason = %error, "could not check for a newer build"),
            }
        }
    }
}

#[derive(Deserialize)]
struct Token {
    token: String,
}

#[derive(Deserialize)]
struct Tags {
    tags: Option<Vec<String>>,
}

async fn check(
    http: &reqwest::Client,
    commit: &str,
    current: &Version,
) -> reqwest::Result<Option<Update>> {
    // The package is public, so an anonymous pull token lists its tags.
    let Token { token } = http
        .get(format!("{REGISTRY}/token"))
        .query(&[("scope", format!("repository:{IMAGE}:pull"))])
        .send()
        .await?
        .error_for_status()?
        .json()
        .await?;
    let mut tags = Vec::new();
    let mut next = Some(format!("{REGISTRY}/v2/{IMAGE}/tags/list?n=1000"));
    for _ in 0..MAX_PAGES {
        let Some(url) = next.take() else { break };
        let response = http
            .get(url)
            .bearer_auth(&token)
            .send()
            .await?
            .error_for_status()?;
        next = next_page(response.headers());
        tags.extend(response.json::<Tags>().await?.tags.unwrap_or_default());
    }
    Ok(update(commit, current, &tags))
}

/// The `rel="next"` page of a registry `Link` header; only ever on the registry, which gets the token.
fn next_page(headers: &HeaderMap) -> Option<String> {
    let target = headers
        .get(LINK)?
        .to_str()
        .ok()?
        .split(',')
        .find_map(|link| {
            let (target, params) = link.split_once(';')?;
            params.contains(r#"rel="next""#).then_some(target)
        })?;
    let target = target.trim().strip_prefix('<')?.strip_suffix('>')?;
    let path = target.strip_prefix(REGISTRY).unwrap_or(target);
    path.starts_with('/').then(|| format!("{REGISTRY}{path}"))
}

/// The newest release tag past `current`, only for a build the registry published as `sha-<commit>`.
/// A stable build is not offered prereleases. Comparing versions is enough because the release
/// workflow only pushes an image together with a new version tag; `edge` never moves on its own.
fn update(commit: &str, current: &Version, tags: &[String]) -> Option<Update> {
    let own = format!("sha-{}", commit.get(..12)?);
    if !tags.contains(&own) {
        tracing::debug!("this build is not a published image");
        return None;
    }
    tags.iter()
        .filter_map(|tag| Some((Version::parse(tag.strip_prefix('v')?).ok()?, tag)))
        .filter(|(version, _)| {
            version > current && (version.pre.is_empty() || !current.pre.is_empty())
        })
        .max_by(|(a, _), (b, _)| a.cmp(b))
        .map(|(_, tag)| Update {
            version: tag.clone(),
            url: format!("{RELEASE_URL}{tag}"),
        })
}

#[cfg(test)]
mod tests {
    use super::*;

    const COMMIT: &str = "cbc5d44f01df0000000000000000000000000000";

    fn newest(current: &str, tags: &[&str]) -> Option<String> {
        let tags: Vec<String> = tags.iter().map(|&tag| tag.to_owned()).collect();
        update(COMMIT, &Version::parse(current).unwrap(), &tags).map(|u| u.version)
    }

    #[test]
    fn offers_the_newest_release_past_this_build() {
        let tags = [
            "edge",
            "sha-cbc5d44f01df",
            "v2.0.0-alpha.9",
            "v2.0.0-alpha.10",
            "v2.0.0-alpha.12",
            "v2.0.0-alpha.11",
        ];
        assert_eq!(
            newest("2.0.0-alpha.10", &tags).as_deref(),
            Some("v2.0.0-alpha.12")
        );
        assert_eq!(newest("2.0.0-alpha.12", &tags), None);
    }

    #[test]
    fn a_stable_build_is_not_offered_prereleases() {
        let tags = ["sha-cbc5d44f01df", "v2.0.0", "v2.1.0-alpha.1"];
        assert_eq!(newest("2.0.0", &tags), None);
        assert_eq!(
            newest("2.0.0-alpha.3", &tags).as_deref(),
            Some("v2.1.0-alpha.1")
        );
    }

    #[test]
    fn links_the_release_notes_of_the_newer_tag() {
        let tags = ["sha-cbc5d44f01df".to_owned(), "v2.0.0-beta.6".to_owned()];
        let found = update(COMMIT, &Version::parse("2.0.0-beta.5").unwrap(), &tags).unwrap();
        assert_eq!(
            found.url,
            "https://github.com/infinidysk/infinidysk/releases/tag/v2.0.0-beta.6"
        );
    }

    #[test]
    fn follows_only_next_pages_on_the_registry() {
        let link = |value: &str| {
            let mut headers = HeaderMap::new();
            headers.insert(LINK, value.parse().unwrap());
            next_page(&headers)
        };
        assert_eq!(
            link(r#"</v2/a/tags/list?last=sha-7cf518baecd9&n=2>; rel="next""#).as_deref(),
            Some("https://ghcr.io/v2/a/tags/list?last=sha-7cf518baecd9&n=2")
        );
        assert_eq!(
            link(r#"<https://ghcr.io/v2/a/tags/list?last=x>; rel="next""#).as_deref(),
            Some("https://ghcr.io/v2/a/tags/list?last=x")
        );
        assert_eq!(link(r#"<https://elsewhere.example/v2>; rel="next""#), None);
        assert_eq!(link(r#"</v2/a/tags/list?last=x>; rel="prev""#), None);
        assert_eq!(
            link(r#"</v2/a?last=p>; rel="prev", </v2/a?last=n>; rel="next""#).as_deref(),
            Some("https://ghcr.io/v2/a?last=n")
        );
        assert_eq!(next_page(&HeaderMap::new()), None);
    }

    #[test]
    fn an_unpublished_build_is_never_out_of_date() {
        assert_eq!(
            newest("2.0.0-alpha.1", &["sha-000000000000", "v2.0.0"]),
            None
        );
    }
}
