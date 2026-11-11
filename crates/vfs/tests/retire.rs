//! Open readers must drop ciphertext, plaintext and prefetched data after retirement.
#![allow(clippy::unwrap_used)] // Assertions and setup for deterministic tests.

use std::io::SeekFrom;
use std::sync::{Arc, RwLock};

use infinidysk_core::crypt::{Cipher, Crypt, Key};
use infinidysk_core::{
    BlobId, ByteRange, FileMeta, FilePart, ItemId, ItemKind, MultipartFile, NzbFile, Segment,
    Segments, blob,
};
use infinidysk_db::{Db, Item};
use infinidysk_nntp::{Body, Client};
use infinidysk_stream::Budget;
use infinidysk_vfs::{Delivery, Node, Origin, Vfs};
use tokio::io::{AsyncReadExt, AsyncSeekExt};

#[tokio::test]
async fn retirement_replaces_buffered_bytes_at_the_open_cursor() {
    let db = Db::connect("sqlite::memory:").await.unwrap();
    let parent = db.item(ItemId::CONTENT).await.unwrap().unwrap();
    let before = vec![17; 8192];
    let after = vec![29; 8192];
    let body = Arc::new(RwLock::new(("article@test".to_owned(), before.clone())));
    let client = Client::new([]);
    client.set_repairs(Box::new({
        let body = Arc::clone(&body);
        move |key| {
            let infinidysk_nntp::RepairKey::Article(id) = key else {
                return None;
            };
            let (expected, data) = body.read().unwrap().clone();
            if id != expected {
                return None;
            }
            Some(Box::pin(async move {
                Ok(Some(Body {
                    data: data.into(),
                    offset: 0,
                    file_size: 8192,
                    part: None,
                    total: None,
                }))
            }))
        }
    }));
    let client = Arc::new(client);
    let vfs = Vfs::new(db.clone(), client, Budget::new(1 << 20), 1 << 20, "/mnt");
    let segments = Segments {
        items: vec![Segment {
            id: "article@test".into(),
            size: 8192,
            fallbacks: vec![],
        }],
        sizes_trusted: true,
        ..Segments::default()
    };
    let crypt = Crypt {
        key: Key::Aes256([9; 32]),
        iv: [3; 16],
        size: 8185,
    };
    for mode in 0..6 {
        *body.write().unwrap() = ("article@test".to_owned(), before.clone());
        let mut meta = if mode % 3 == 0 {
            FileMeta::Nzb(NzbFile {
                segments: segments.clone(),
                proof: None,
            })
        } else {
            FileMeta::Multipart(MultipartFile {
                parts: vec![FilePart {
                    segments: segments.clone(),
                    offset: 0,
                    data: ByteRange {
                        start: 0,
                        end: 8192,
                    },
                    split_after: false,
                    proof: None,
                }],
                crypt: (mode % 3 == 2).then(|| crypt.clone()),
            })
        };
        let kind = if mode % 3 == 0 {
            ItemKind::NzbFile
        } else {
            ItemKind::Multipart
        };
        let mut item = Item::child(&parent, &format!("file-{mode}.bin"), kind).unwrap();
        let metadata = BlobId::new();
        item.meta_blob = Some(metadata);
        item.size = Some(meta.size());
        db.insert(
            std::slice::from_ref(&item),
            &[(metadata, &blob::encode_meta(&meta).unwrap())],
        )
        .await
        .unwrap();
        let mut reader = vfs
            .open(
                &Node::Item(item.clone()),
                Origin {
                    delivery: Delivery::WebDav,
                    client: "test".into(),
                    player: None,
                },
            )
            .await
            .unwrap()
            .unwrap();
        reader.set_end(4000);
        let mut analysis = vfs.open_analysis(&item).await.unwrap();
        let mut first = [0; 17];
        reader.read_exact(&mut first).await.unwrap();
        analysis.read_exact(&mut first).await.unwrap();
        let fence = vfs.read_fence(item.id);
        let mut publication = fence.write().await;
        if mode >= 3 {
            let segments = match &mut meta {
                FileMeta::Nzb(file) => &mut file.segments,
                FileMeta::Multipart(file) => &mut file.parts[0].segments,
            };
            segments.items[0].id = "replacement@test".into();
            db.update_blob(metadata, &blob::encode_meta(&meta).unwrap())
                .await
                .unwrap();
            *body.write().unwrap() = ("replacement@test".to_owned(), after.clone());
        } else {
            body.write().unwrap().1 = after.clone();
        }
        publication.retire().unwrap();
        drop(publication);
        let mut expected = after.clone();
        if mode % 3 == 2 {
            Cipher::new(&crypt.key).decrypt(&mut crypt.iv.clone(), &mut expected);
        }
        let mut next = [0; 31];
        reader.read_exact(&mut next).await.unwrap();
        assert_eq!(next, expected[17..48], "reader mode {mode}");
        analysis.read_exact(&mut next).await.unwrap();
        assert_eq!(next, expected[17..48], "analysis mode {mode}");
        reader.seek(SeekFrom::Start(20)).await.unwrap();
        reader.read_exact(&mut next).await.unwrap();
        assert_eq!(next, expected[20..51]);
        if mode == 2 {
            let mut publication = fence.write().await;
            db.update_blob(metadata, b"invalid metadata").await.unwrap();
            publication.retire().unwrap();
            drop(publication);
            assert!(reader.read_exact(&mut next).await.is_err());
            let mut publication = fence.write().await;
            db.update_blob(metadata, &blob::encode_meta(&meta).unwrap())
                .await
                .unwrap();
            publication.retire().unwrap();
            drop(publication);
            reader.read_exact(&mut next).await.unwrap();
            assert_eq!(next, expected[51..82]);
        }
    }
}
