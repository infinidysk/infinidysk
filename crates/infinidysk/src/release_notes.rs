//! Release notes from the `CHANGELOG.md` release-please writes, built into the binary so
//! they always match the running version.

use axum::Json;
use serde::Serialize;
use utoipa::ToSchema;

const CHANGELOG: &str = include_str!("../../../CHANGELOG.md");

#[derive(Debug, PartialEq, Eq, Serialize, ToSchema)]
pub struct Release {
    pub version: String,
    /// `YYYY-MM-DD`.
    pub date: Option<String>,
    /// The changes since the previous release on GitHub.
    pub url: Option<String>,
    pub sections: Vec<ReleaseSection>,
}

#[derive(Debug, PartialEq, Eq, Serialize, ToSchema)]
pub struct ReleaseSection {
    /// Such as "Features" or "Bug Fixes".
    pub title: String,
    /// Changes that need action when upgrading.
    pub breaking: bool,
    pub changes: Vec<ReleaseChange>,
}

#[derive(Debug, PartialEq, Eq, Serialize, ToSchema)]
pub struct ReleaseChange {
    /// The part of infinidysk it touches, such as `queue`.
    pub scope: Option<String>,
    pub text: String,
    /// The issue, then the commit.
    pub links: Vec<ReleaseLink>,
}

#[derive(Debug, PartialEq, Eq, Serialize, ToSchema)]
pub struct ReleaseLink {
    pub label: String,
    pub url: String,
}

/// Release notes of this version and every one before it, newest first.
#[utoipa::path(get, path = "/api/v1/release-notes", tag = "status",
    responses((status = 200, body = Vec<Release>)))]
pub async fn get_release_notes() -> Json<Vec<Release>> {
    Json(parse(CHANGELOG))
}

fn parse(changelog: &str) -> Vec<Release> {
    let mut releases = Vec::<Release>::new();
    let mut change: Option<String> = None;
    for line in changelog.lines().map(str::trim) {
        let heading = line
            .strip_prefix("## ")
            .or_else(|| line.strip_prefix("### "));
        let bullet = line.strip_prefix("* ").or_else(|| line.strip_prefix("- "));
        if heading.is_some() || bullet.is_some() || line.is_empty() {
            push(&mut releases, change.take());
        }
        if let Some(heading) = heading {
            if let Some(release) = release(heading) {
                releases.push(release);
            } else if let Some(release) = releases.last_mut() {
                let title = heading.trim_start_matches('⚠').trim();
                release.sections.push(ReleaseSection {
                    title: title.to_owned(),
                    breaking: title.eq_ignore_ascii_case("breaking changes"),
                    changes: Vec::new(),
                });
            }
        } else if let Some(bullet) = bullet {
            change = Some(bullet.to_owned());
        } else if let Some(change) = &mut change
            && !line.is_empty()
        {
            change.push(' ');
            change.push_str(line);
        }
    }
    push(&mut releases, change);
    releases
}

/// `[1.2.0](compare-url) (2026-11-01)` or `1.2.0 (2026-11-01)`; other headings are sections.
fn release(heading: &str) -> Option<Release> {
    let (version, url, rest) = if let Some(linked) = heading.strip_prefix('[') {
        let (version, rest) = linked.split_once("](")?;
        let (url, rest) = rest.split_once(')')?;
        (version, https(url), rest)
    } else {
        let (version, rest) = heading.split_once(' ').unwrap_or((heading, ""));
        (version, None, rest)
    };
    if !version.starts_with(|c: char| c.is_ascii_digit()) {
        return None;
    }
    let date = rest
        .trim()
        .strip_prefix('(')
        .and_then(|d| d.strip_suffix(')'))
        .map(str::to_owned);
    Some(Release {
        version: version.to_owned(),
        date,
        url,
        sections: Vec::new(),
    })
}

fn push(releases: &mut [Release], change: Option<String>) {
    if let Some(change) = change
        && let Some(section) = releases.last_mut().and_then(|r| r.sections.last_mut())
    {
        section.changes.push(parse_change(&change));
    }
}

/// `**scope:** text ([#41](url)) ([abc1234](url))`.
fn parse_change(line: &str) -> ReleaseChange {
    let (scope, mut rest) = match line.strip_prefix("**").and_then(|l| l.split_once(":**")) {
        Some((scope, rest)) => (Some(scope.to_owned()), rest),
        None => (None, line),
    };
    let mut links = Vec::new();
    while let Some(head) = rest.strip_suffix("))")
        && let Some((head, link)) = head.rsplit_once(" ([")
        && let Some((label, url)) = link.split_once("](")
        && let Some(url) = https(url)
    {
        links.push(ReleaseLink {
            label: label.to_owned(),
            url,
        });
        rest = head;
    }
    links.reverse();
    ReleaseChange {
        scope,
        text: rest.trim().to_owned(),
        links,
    }
}

/// Only web links reach the UI's `href`s.
fn https(url: &str) -> Option<String> {
    url.starts_with("https://").then(|| url.to_owned())
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn parses_release_please_output() {
        let changelog = "# Changelog

## [2.0.0](https://example.com/compare/v1.0.0...v2.0.0) (2026-11-01)


### ⚠ BREAKING CHANGES

* **db:** old databases no longer open. Back up the config
directory first.

### Bug Fixes

* **queue:** keep paused jobs paused ([#41](https://example.com/issues/41)) ([abc1234](https://example.com/commit/abc1234))
* unscoped fix ([def5678](https://example.com/commit/def5678))
* not a link ([x](javascript:alert(1)))

## 1.0.0 (2026-10-01)

### Features

* first release
";
        let link = |label: &str, url: &str| ReleaseLink {
            label: label.to_owned(),
            url: url.to_owned(),
        };
        assert_eq!(
            parse(changelog),
            [
                Release {
                    version: "2.0.0".to_owned(),
                    date: Some("2026-11-01".to_owned()),
                    url: Some("https://example.com/compare/v1.0.0...v2.0.0".to_owned()),
                    sections: vec![
                        ReleaseSection {
                            title: "BREAKING CHANGES".to_owned(),
                            breaking: true,
                            changes: vec![ReleaseChange {
                                scope: Some("db".to_owned()),
                                text: "old databases no longer open. Back up the config directory first."
                                    .to_owned(),
                                links: vec![],
                            }],
                        },
                        ReleaseSection {
                            title: "Bug Fixes".to_owned(),
                            breaking: false,
                            changes: vec![
                                ReleaseChange {
                                    scope: Some("queue".to_owned()),
                                    text: "keep paused jobs paused".to_owned(),
                                    links: vec![
                                        link("#41", "https://example.com/issues/41"),
                                        link("abc1234", "https://example.com/commit/abc1234"),
                                    ],
                                },
                                ReleaseChange {
                                    scope: None,
                                    text: "unscoped fix".to_owned(),
                                    links: vec![link(
                                        "def5678",
                                        "https://example.com/commit/def5678"
                                    )],
                                },
                                ReleaseChange {
                                    scope: None,
                                    text: "not a link ([x](javascript:alert(1)))".to_owned(),
                                    links: vec![],
                                },
                            ],
                        },
                    ],
                },
                Release {
                    version: "1.0.0".to_owned(),
                    date: Some("2026-10-01".to_owned()),
                    url: None,
                    sections: vec![ReleaseSection {
                        title: "Features".to_owned(),
                        breaking: false,
                        changes: vec![ReleaseChange {
                            scope: None,
                            text: "first release".to_owned(),
                            links: vec![],
                        }],
                    }],
                },
            ]
        );
    }

    #[test]
    fn reads_every_change_of_the_shipped_changelog() {
        let releases = parse(CHANGELOG);
        assert_eq!(
            releases.first().map(|r| r.version.as_str()),
            Some(crate::update::VERSION)
        );
        let bullets = CHANGELOG.lines().filter(|l| l.starts_with("* ")).count();
        let changes: Vec<_> = releases
            .iter()
            .flat_map(|r| &r.sections)
            .flat_map(|s| &s.changes)
            .collect();
        assert_eq!(changes.len(), bullets);
        assert!(changes.iter().all(|c| !c.text.is_empty()));
        assert!(
            releases
                .iter()
                .all(|r| r.date.is_some() && !r.sections.is_empty())
        );
    }
}
