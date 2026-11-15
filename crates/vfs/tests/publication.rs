//! Publication selects shared articles and keeps unrelated delivery admitted.
#![allow(clippy::unwrap_used)] // Deterministic test setup and assertions.

use infinidysk_core::{
    BlobId, ByteRange, FileMeta, ItemId, ItemKind, NzbFile, PatchId, Segment, Segments, blob,
    repair::{Patch, Source},
};
use infinidysk_db::{Db, Item};
use infinidysk_nntp::Client;
use infinidysk_stream::Budget;
use infinidysk_vfs::{Delivery, Node, Origin, Vfs};
use std::collections::BTreeSet;
use std::sync::Arc;
use std::time::Duration;
use tokio::io::AsyncReadExt;

fn origin() -> Origin {
    Origin {
        delivery: Delivery::WebDav,
        client: "test".into(),
        player: None,
    }
}

async fn setup() -> (Vfs, Vec<Item>, BlobId, Patch) {
    let db = Db::connect("sqlite::memory:").await.unwrap();
    let parent = db.item(ItemId::CONTENT).await.unwrap().unwrap();
    let (nzb, other) = (BlobId::new(), BlobId::new());
    db.insert(&[], &[(nzb, b"original"), (other, b"other")])
        .await
        .unwrap();
    let mut items = Vec::new();
    for (index, (source, id)) in [
        (nzb, "first@test"),
        (other, "shared@test"),
        (other, "unrelated@test"),
        (other, "shared@test"),
    ]
    .into_iter()
    .enumerate()
    {
        let meta = FileMeta::Nzb(NzbFile {
            segments: Segments {
                items: vec![Segment {
                    id: id.into(),
                    size: 1,
                    fallbacks: vec![],
                }],
                sizes_trusted: true,
                ..Segments::default()
            },
            proof: None,
        });
        let mut item =
            Item::child(&parent, &format!("file-{index}.bin"), ItemKind::NzbFile).unwrap();
        let metadata = BlobId::new();
        item.meta_blob = Some(metadata);
        item.nzb_blob = Some(source);
        item.size = Some(1);
        db.insert(
            std::slice::from_ref(&item),
            &[(metadata, &blob::encode_meta(&meta).unwrap())],
        )
        .await
        .unwrap();
        items.push(item);
    }
    let patch = Patch {
        id: PatchId::new(),
        source: Source {
            set: [1; 16],
            file: [2; 16],
            range: ByteRange { start: 0, end: 1 },
            file_length: 1,
            message_id: Some("shared@test".into()),
            part: None,
            total: None,
        },
        sha256: [3; 32],
    };
    (
        Vfs::new(db, Arc::new(Client::new([])), Budget::new(1024), 0, "/mnt"),
        items,
        nzb,
        patch,
    )
}

#[tokio::test]
async fn shared_articles_and_source_members_are_fenced_but_other_opens_continue() {
    let (vfs, items, nzb, patch) = setup().await;
    let mut handles = Vec::new();
    for item in &items[..3] {
        handles.push(
            vfs.open(&Node::Item(item.clone()), origin())
                .await
                .unwrap()
                .unwrap(),
        );
    }
    let unaffected = vfs.read_fence(items[2].id).version();
    let publication = vfs.begin_publication(nzb, &[patch]).unwrap();
    let mut writers = publication.writers().await;
    assert_eq!(
        writers
            .iter()
            .map(infinidysk_vfs::WriteLease::item)
            .collect::<BTreeSet<_>>(),
        [items[0].id, items[1].id].into()
    );
    let node = Node::Item(items[3].clone());
    let mut opening = Box::pin(vfs.open(&node, origin()));
    assert!(
        tokio::time::timeout(Duration::from_millis(15), &mut opening)
            .await
            .is_err()
    );
    assert!(
        tokio::time::timeout(
            Duration::from_secs(1),
            vfs.open(&Node::Item(items[2].clone()), origin())
        )
        .await
        .unwrap()
        .unwrap()
        .is_some()
    );
    for writer in &mut writers {
        writer.retire().unwrap();
    }
    assert!(unaffected.is_current());
    drop(writers);
    assert!(
        tokio::time::timeout(Duration::from_millis(15), &mut opening)
            .await
            .is_err()
    );
    drop(publication);
    assert!(
        tokio::time::timeout(Duration::from_secs(1), opening)
            .await
            .unwrap()
            .unwrap()
            .is_some()
    );
    drop(handles);
}

#[tokio::test]
async fn cancelled_admission_releases_partial_writers_and_pending_opens() {
    let (vfs, items, nzb, patch) = setup().await;
    let mut handles = Vec::new();
    for item in &items[..2] {
        handles.push(
            vfs.open(&Node::Item(item.clone()), origin())
                .await
                .unwrap()
                .unwrap(),
        );
    }
    let reader = vfs.read_fence(items[1].id).read().await.unwrap();
    let publication = vfs.begin_publication(nzb, &[patch]).unwrap();
    assert!(
        tokio::time::timeout(Duration::from_millis(15), publication.writers())
            .await
            .is_err()
    );
    drop(vfs.read_fence(items[0].id).read().await.unwrap());
    let node = Node::Item(items[0].clone());
    let mut opening = Box::pin(vfs.open(&node, origin()));
    assert!(
        tokio::time::timeout(Duration::from_millis(15), &mut opening)
            .await
            .is_err()
    );
    drop(publication);
    assert!(opening.await.unwrap().is_some());
    drop(reader);
    drop(handles);
}

#[tokio::test]
async fn unfinished_publication_denies_old_buffers_until_reconciled() {
    let (vfs, items, nzb, patch) = setup().await;
    let node = Node::Item(items[0].clone());
    let mut reader = vfs.open(&node, origin()).await.unwrap().unwrap();
    let publication = vfs
        .begin_publication(nzb, std::slice::from_ref(&patch))
        .unwrap();
    let mut writers = publication.writers().await;
    for writer in &mut writers {
        writer.block();
    }
    drop(writers);
    drop(publication);
    assert!(reader.read(&mut [0; 1]).await.is_err());
    assert!(vfs.open(&node, origin()).await.is_err());
    let publication = vfs.begin_publication(nzb, &[patch]).unwrap();
    let mut writers = publication.writers().await;
    for writer in &mut writers {
        writer.retire().unwrap();
        writer.acknowledge();
    }
    drop(writers);
    drop(publication);
    assert!(vfs.open(&node, origin()).await.unwrap().is_some());
}
