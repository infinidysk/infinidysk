//! Managed provider catalog, compiled from `providers/*.toml` by `build.rs`.
//!
//! Custom providers may pick a backbone from [`BACKBONES`] but never invent one;
//! without a pick they form their own group.

#[derive(Debug, PartialEq, Eq)]
pub struct Backbone {
    pub id: &'static str,
    pub name: &'static str,
}

#[derive(Debug, PartialEq, Eq)]
pub struct Profile {
    pub id: &'static str,
    pub name: &'static str,
    pub website: &'static str,
    /// First entry is the default server.
    pub servers: &'static [Server],
}

#[derive(Debug, PartialEq, Eq)]
pub struct Server {
    pub hostname: &'static str,
    /// ISO 3166 alpha-2; `None` for geo-balanced hostnames.
    pub region: Option<&'static str>,
    pub port: u16,
    pub backbone: &'static str,
}

include!(concat!(env!("OUT_DIR"), "/catalog.rs"));

#[must_use]
pub fn profile(id: &str) -> Option<&'static Profile> {
    PROFILES.iter().find(|p| p.id == id)
}

#[must_use]
pub fn backbone(id: &str) -> Option<&'static Backbone> {
    BACKBONES.iter().find(|b| b.id == id)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn every_server_resolves_to_a_known_backbone() {
        assert_ne!(PROFILES.len(), 0);
        for p in PROFILES {
            assert_eq!(profile(p.id), Some(p));
            for s in p.servers {
                assert!(backbone(s.backbone).is_some(), "{} {}", p.id, s.hostname);
            }
        }
    }
}
