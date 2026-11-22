//! Runtime article identities used to select readers and native cached aliases.

use infinidysk_core::{BlobId, FileMeta, repair::Patch};
use std::collections::BTreeSet;
use std::sync::{Arc, Weak};

pub(crate) struct Scope {
    pub(crate) nzb: Option<BlobId>,
    meta: Weak<FileMeta>,
}

pub(crate) struct Effects {
    nzb: BlobId,
    articles: BTreeSet<String>,
}

impl Scope {
    pub(crate) fn new(nzb: Option<BlobId>, meta: &Arc<FileMeta>) -> Self {
        Self {
            nzb,
            meta: Arc::downgrade(meta),
        }
    }

    /// Adds the article IDs of the layout this scope still holds.
    pub(crate) fn layout_ids(&self, ids: &mut BTreeSet<String>) {
        let Some(meta) = self.meta.upgrade() else {
            return;
        };
        let mut add = |segments: &infinidysk_core::Segments| {
            ids.extend(
                segments
                    .items
                    .iter()
                    .filter(|segment| !segment.id.is_empty())
                    .map(|segment| segment.id.clone()),
            );
        };
        match meta.as_ref() {
            FileMeta::Nzb(file) => add(&file.segments),
            FileMeta::Multipart(file) => {
                for part in &file.parts {
                    add(&part.segments);
                }
            }
        }
    }
}

impl Effects {
    pub(crate) fn new(nzb: BlobId, patches: &[Patch]) -> Self {
        Self {
            nzb,
            articles: patches
                .iter()
                .filter_map(|patch| patch.source.message_id.clone())
                .collect(),
        }
    }

    pub(crate) fn affects(&self, scope: &Scope) -> bool {
        if scope.nzb == Some(self.nzb) {
            return true;
        }
        let Some(meta) = scope.meta.upgrade() else {
            return false;
        };
        match meta.as_ref() {
            FileMeta::Nzb(file) => file.segments.items.iter().any(|s| self.contains(&s.id)),
            FileMeta::Multipart(file) => file
                .parts
                .iter()
                .any(|part| part.segments.items.iter().any(|s| self.contains(&s.id))),
        }
    }

    pub(crate) fn contains(&self, id: &str) -> bool {
        self.articles.contains(id)
    }

    pub(crate) fn remember_shared(&self, scope: &Scope, retained: &mut BTreeSet<String>) {
        if scope.nzb == Some(self.nzb) || self.articles.is_empty() {
            return;
        }
        let Some(meta) = scope.meta.upgrade() else {
            return;
        };
        let mut remember = |segments: &infinidysk_core::Segments| {
            retained.extend(
                segments
                    .items
                    .iter()
                    .filter(|segment| self.contains(&segment.id))
                    .map(|segment| segment.id.clone()),
            );
        };
        match meta.as_ref() {
            FileMeta::Nzb(file) => remember(&file.segments),
            FileMeta::Multipart(file) => {
                for part in &file.parts {
                    remember(&part.segments);
                }
            }
        }
    }

    pub(crate) fn touches(&self, ids: &BTreeSet<String>) -> bool {
        self.articles.iter().any(|id| ids.contains(id))
    }
}
