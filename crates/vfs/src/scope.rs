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

    pub(crate) fn touches(&self, ids: &BTreeSet<String>) -> bool {
        self.articles.iter().any(|id| ids.contains(id))
    }
}
