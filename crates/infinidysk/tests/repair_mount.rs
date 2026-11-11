//! Real kernel validation of the coordinated commit boundary; never use live providers.
#![cfg(target_os = "linux")]
#![allow(clippy::unwrap_used)] // Fixture setup and behavioral assertions.

use infinidysk_core::{
    BlobId, ByteRange, FileMeta, ItemId, ItemKind, NzbFile, PatchId, Segment, Segments, blob,
    repair::{Patch, Source as Posted},
};
use infinidysk_db::{Db, Item, repair::Owner};
use infinidysk_nntp::{Client, Downloads, Endpoint, Pool, PoolConfig, Provider, TlsMode};
use infinidysk_stream::Budget;
use infinidysk_testkit::{LoopbackServer, post};
use sha2::{Digest as _, Sha256};
use std::sync::Arc;
use std::time::Duration;
use tokio_util::sync::CancellationToken;

#[tokio::test(flavor = "multi_thread")]
#[ignore = "needs /dev/fuse and permission to mount with allow_other"]
async fn committed_repairs_replace_cached_pages_for_both_open_aliases() {
    const ARTICLE: usize = 4096;
    let index = infinidysk_par2::read(include_bytes!("../../par2/tests/fixtures/set.par2"));
    let protected = index
        .files
        .iter()
        .find(|file| file.name == "a.bin")
        .unwrap();
    let data: Vec<u8> = (0..usize::try_from(protected.length).unwrap())
        .map(|i| ((i * 31 + i / 251) % 256).to_le_bytes()[0])
        .collect();
    let mut verifier = infinidysk_par2::Verifier::new(
        protected.length,
        protected.hash,
        index.slice_size,
        &protected.slices,
    )
    .unwrap();
    verifier.push(&data, || false).unwrap();
    verifier.finish(|| false).unwrap();
    let mut damaged = data.clone();
    for byte in &mut damaged[..ARTICLE] {
        *byte ^= 0x80;
    }
    let articles = post("repair-mount", "source.bin", &damaged, ARTICLE);
    let server = LoopbackServer::start(articles.clone()).await.unwrap();
    let endpoint = Endpoint::new(
        "127.0.0.1",
        server.addr().port(),
        TlsMode::Plain,
        None,
        Duration::from_secs(5),
    )
    .unwrap();
    let pool = Pool::new(
        endpoint,
        PoolConfig {
            max_connections: 4,
            pipeline_depth: 4,
            idle_timeout: Duration::from_secs(5),
            min_connections: 0,
        },
        Downloads::new(16),
    )
    .unwrap();
    let client = Arc::new(Client::new([Provider {
        pool,
        key: String::new(),
        name: String::new(),
        priority: 0,
        backbone: None,
    }]));
    let root = std::env::temp_dir().join(format!("infinidysk-repair-mount-{}", ItemId::new()));
    std::fs::create_dir(&root).unwrap();
    let db = Db::connect(&format!("sqlite:{}", root.join("data.sqlite").display()))
        .await
        .unwrap();
    let parent = db.item(ItemId::CONTENT).await.unwrap().unwrap();
    let mut item = Item::child(&parent, "source.bin", ItemKind::NzbFile).unwrap();
    let (metadata, nzb) = (BlobId::new(), BlobId::new());
    let meta = FileMeta::Nzb(NzbFile {
        segments: Segments {
            items: articles
                .iter()
                .enumerate()
                .map(|(i, article)| Segment {
                    id: article.message_id.clone(),
                    size: u32::try_from(ARTICLE.min(data.len() - i * ARTICLE)).unwrap(),
                    fallbacks: vec![],
                })
                .collect(),
            sizes_trusted: true,
            ..Segments::default()
        },
        proof: None,
    });
    item.meta_blob = Some(metadata);
    item.nzb_blob = Some(nzb);
    item.size = Some(data.len() as u64);
    db.insert(
        std::slice::from_ref(&item),
        &[
            (metadata, &blob::encode_meta(&meta).unwrap()),
            (nzb, &blob::encode_nzb(b"<nzb/>").unwrap()),
        ],
    )
    .await
    .unwrap();
    let source = db
        .repair_source(Owner::Item(item.id))
        .await
        .unwrap()
        .unwrap();
    let repairs = infinidysk::repair::install(&client, db.clone(), root.join("repairs"));
    let mount = root.join("mount");
    let vfs = Arc::new(infinidysk_vfs::Vfs::new(
        db.clone(),
        client,
        Budget::new(1_000_000),
        1_000_000,
        mount.to_str().unwrap(),
    ));
    let native = Arc::new(infinidysk_fuse::Invalidator::default());
    let publisher = infinidysk::repair::Publisher::new(Arc::clone(&repairs), Arc::clone(&vfs))
        .unwrap()
        .with_mount(Arc::clone(&native));
    let cancel = CancellationToken::new();
    let (state, mut ready) = tokio::sync::watch::channel(Err(String::new()));
    let task = tokio::spawn(infinidysk_fuse::run(
        vfs,
        native,
        infinidysk_fuse::Config {
            mount_dir: mount.clone(),
            uid: nix::unistd::geteuid().as_raw(),
            gid: nix::unistd::getegid().as_raw(),
            max_read: 1 << 20,
            max_readahead: 1_000_000,
            max_background: 16,
            epoch_times: false,
        },
        state,
        cancel.clone(),
    ));
    tokio::time::timeout(Duration::from_secs(5), ready.wait_for(Result::is_ok))
        .await
        .unwrap()
        .unwrap();
    let path = mount.join("content/source.bin");
    let ids = mount.join(item.id.ids_path());
    let (first, alias, path, ids) = tokio::task::spawn_blocking(move || {
        let first = std::fs::File::open(&path).unwrap();
        let alias = std::fs::File::open(&ids).unwrap();
        let mut bytes = [0; ARTICLE];
        for file in [&first, &alias] {
            std::os::unix::fs::FileExt::read_exact_at(file, &mut bytes, 0).unwrap();
            assert_eq!(&bytes, &damaged[..ARTICLE]);
        }
        (first, alias, path, ids)
    })
    .await
    .unwrap();
    let patch = Patch {
        id: PatchId::new(),
        source: Posted {
            set: index.set_id.unwrap(),
            file: protected.id,
            range: ByteRange {
                start: 0,
                end: ARTICLE as u64,
            },
            file_length: data.len() as u64,
            message_id: Some(articles[0].message_id.clone()),
            part: Some(1),
            total: Some(u32::try_from(articles.len()).unwrap()),
        },
        sha256: Sha256::digest(&data[..ARTICLE]).into(),
    };
    let receipt = repairs
        .store(1_000_000)
        .unwrap()
        .write(&patch, &data[..ARTICLE], || false)
        .unwrap();
    let published = publisher.publish(source, vec![receipt]).await;
    let checked = tokio::task::spawn_blocking(move || {
        let mut bytes = vec![0; data.len()];
        for file in [&first, &alias] {
            std::os::unix::fs::FileExt::read_exact_at(file, &mut bytes, 0).unwrap();
            assert_eq!(bytes, data);
        }
        drop((first, alias));
        for path in [path, ids] {
            assert_eq!(std::fs::read(path).unwrap(), data);
        }
    })
    .await;
    cancel.cancel();
    task.await.unwrap();
    assert!(published.unwrap());
    checked.unwrap();
    db.close().await;
    std::fs::remove_dir_all(root).unwrap();
}
