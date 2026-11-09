use infinidysk_core::{BlobId, ItemId, ItemKind, JobId};
use infinidysk_db::{
    ArrOwner, Db, Defect, Error, FileQuery, FileSort, FirstCheck, Handoff, HealthCheck,
    HealthResult, HealthState, HealthStatus, HistoryEntry, HistoryQuery, Item, JobStatus,
    MediaProbe, OwnedFile, ProbeAttempt, ProbeOutcome, QueueEntry, Recheck, Repair, SubtreeStats,
};

/// Exercises the tree, blob and config paths that must behave the same on both backends.
async fn exercise(db: &Db) {
    // Unreferenced NZBs go at once until the retention section at the end.
    db.set_nzb_retention_days(0);
    let content = db
        .item_by_path("/content")
        .await
        .expect("query")
        .expect("seeded");
    assert_eq!(content.id, ItemId::CONTENT);
    assert_eq!(
        db.item(ItemId::ROOT).await.expect("query").map(|i| i.path),
        Some("/".into())
    );

    let mut dir = Item::child(&content, "job", ItemKind::Directory).expect("valid");
    let nzb = BlobId::new();
    dir.nzb_blob = Some(nzb);
    let mut file = Item::child(&dir, "b.mkv", ItemKind::NzbFile).expect("valid");
    let meta = BlobId::new();
    file.meta_blob = Some(meta);
    file.nzb_blob = Some(nzb);
    file.size = Some(5 << 32);
    let mut other = Item::child(&dir, "a.mkv", ItemKind::Multipart).expect("valid");
    other.encrypted = true;
    db.insert(
        &[dir.clone(), file.clone(), other.clone()],
        &[(nzb, b"nzb"), (meta, b"meta")],
    )
    .await
    .expect("insert");

    assert_eq!(file.path, "/content/job/b.mkv");
    assert_eq!(
        db.item_by_path(&file.path).await.expect("query"),
        Some(file.clone())
    );
    let names: Vec<_> = db
        .children(dir.id)
        .await
        .expect("query")
        .into_iter()
        .map(|i| i.name)
        .collect();
    assert_eq!(names, ["a.mkv", "b.mkv"]);
    let mut names = db.child_names(dir.id).await.expect("query");
    names.sort();
    assert_eq!(names, ["a.mkv", "b.mkv"]);
    let ids = |items: Vec<Item>| items.into_iter().map(|i| i.id).collect::<Vec<_>>();
    assert_eq!(
        ids(db.files_in_id_range(dir.id, None).await.expect("query")),
        [file.id, other.id],
        "directories are skipped"
    );
    assert_eq!(
        ids(db
            .files_in_id_range(file.id, Some(other.id))
            .await
            .expect("query")),
        [file.id]
    );
    assert_eq!(
        db.blob(meta).await.expect("query").as_deref(),
        Some(&b"meta"[..])
    );
    db.update_blob(meta, b"meta2").await.expect("update");
    assert_eq!(
        db.blob(meta).await.expect("query").as_deref(),
        Some(&b"meta2"[..])
    );

    let dup = Item {
        id: ItemId::new(),
        ..other.clone()
    };
    assert!(db.insert(&[dup], &[]).await.is_err(), "paths are unique");
    assert!(matches!(
        Item::child(&dir, "x/y", ItemKind::NzbFile),
        Err(Error::InvalidName(_))
    ));

    db.record_health_check(&HealthCheck {
        created_at: 7,
        item_id: file.id,
        path: file.path.clone(),
        result: HealthResult::Missing,
        repair: Repair::ActionNeeded,
        message: Some("gone".into()),
    })
    .await
    .expect("record");
    let names = |q: FileQuery<'static>| async move {
        let (rows, total) = db.files(&q).await.expect("query");
        let names: Vec<String> = rows.into_iter().map(|r| r.item.name).collect();
        (names, total)
    };
    let page = |offset| FileQuery {
        limit: 10,
        offset,
        ..FileQuery::default()
    };
    assert_eq!(
        names(page(0)).await,
        (vec!["job".into(), "a.mkv".into(), "b.mkv".into()], 3),
        "directories first"
    );
    assert_eq!(
        names(FileQuery {
            limit: 1,
            ..page(1)
        })
        .await
        .0,
        ["a.mkv"]
    );
    let flat = FileQuery {
        files_only: true,
        sort: FileSort::Size,
        descending: true,
        ..page(0)
    };
    assert_eq!(names(flat.clone()).await.0, ["b.mkv", "a.mkv"]);
    for (q, want) in [
        (
            FileQuery {
                search: Some("B.M"),
                ..flat.clone()
            },
            "b.mkv",
        ),
        (
            FileQuery {
                kind: Some(ItemKind::Multipart),
                ..flat.clone()
            },
            "a.mkv",
        ),
        (
            FileQuery {
                min_size: Some(1),
                ..flat.clone()
            },
            "b.mkv",
        ),
        (
            FileQuery {
                result: Some(HealthResult::Missing),
                ..flat.clone()
            },
            "b.mkv",
        ),
        (
            FileQuery {
                state: Some(HealthState::Unchecked),
                min_size: Some(1),
                ..flat.clone()
            },
            "b.mkv",
        ),
        (
            FileQuery {
                repair: Some(Repair::ActionNeeded),
                ..flat.clone()
            },
            "b.mkv",
        ),
        (
            FileQuery {
                encrypted: Some(true),
                ..flat.clone()
            },
            "a.mkv",
        ),
        (
            FileQuery {
                encrypted: Some(false),
                ..flat.clone()
            },
            "b.mkv",
        ),
    ] {
        assert_eq!(names(q.clone()).await.0, [want], "{q:?}");
    }
    assert_eq!(
        names(FileQuery {
            search: Some("%"),
            ..flat.clone()
        })
        .await
        .1,
        0
    );
    assert_eq!(
        names(FileQuery {
            category: Some("job"),
            ..flat.clone()
        })
        .await
        .1,
        2
    );
    assert_eq!(
        names(FileQuery {
            category: Some("jo"),
            ..flat.clone()
        })
        .await
        .1,
        0
    );
    let (rows, _) = db
        .files(&FileQuery {
            search: Some("b.mkv"),
            ..flat
        })
        .await
        .expect("query");
    assert_eq!(
        rows[0].health.as_ref().map(|h| h.repair),
        Some(Repair::ActionNeeded)
    );
    assert_eq!(db.item_checks(file.id, 5).await.expect("query").len(), 1);

    // Health work: unchecked first, recheck reasons survive a concurrent write, hidden
    // items leave listings.
    let due = |routine| async move {
        let work = db.due_health_work(100, routine, 10).await.expect("query");
        work.into_iter().map(|(i, _)| i.id).collect::<Vec<_>>()
    };
    assert_eq!(due(true).await, [file.id], "only files with metadata");
    assert_eq!(due(false).await, [] as [ItemId; 0], "routine checks off");
    db.request_recheck(file.id, Recheck::Playback)
        .await
        .expect("recheck");
    db.request_recheck(file.id, Recheck::User)
        .await
        .expect("recheck");
    let (_, status) = db.health(file.id).await.expect("query").expect("exists");
    assert_eq!(status.recheck, Recheck::Playback, "the urgent reason wins");
    assert_eq!(due(false).await, [file.id], "rechecks run with routine off");
    let hidden = HealthStatus {
        state: HealthState::Hidden,
        next_check: Some(1_000),
        hidden_until: Some(2_000),
        ..status
    };
    db.set_health(file.id, &hidden, Recheck::User)
        .await
        .expect("set");
    let (item, stored) = db.health(file.id).await.expect("query").expect("exists");
    assert_eq!(item.health_state, HealthState::Hidden);
    assert_eq!(stored.recheck, Recheck::Playback, "not the handled reason");
    assert_eq!(
        due(true).await,
        [] as [ItemId; 0],
        "hidden waits for its time"
    );
    assert_eq!(
        names(flat.clone()).await.0,
        ["a.mkv"],
        "hidden files are not listed"
    );
    assert_eq!(
        names(FileQuery {
            state: Some(HealthState::Hidden),
            ..flat.clone()
        })
        .await
        .0,
        ["b.mkv"]
    );
    assert_eq!(
        db.health_summary().await.expect("query"),
        [(HealthState::Hidden, 1)]
    );

    db.reject_release("rel", &["a@b", "c@d"], 50)
        .await
        .expect("reject");
    media_probes(db, &other, &file).await;
    assert!(db.is_rejected("rel", &[], 10).await.expect("query"));
    assert!(db.is_rejected("other", &["c@d"], 10).await.expect("query"));
    let listed = db.rejected_releases(10).await.expect("query");
    assert_eq!(
        listed
            .iter()
            .map(|r| (r.name.as_str(), r.expires_at))
            .collect::<Vec<_>>(),
        [("rel", 50)]
    );
    assert!(listed[0].rejected_at > 0, "stamped from the id");
    assert_eq!(db.rejected_releases(50).await.expect("query"), []);
    assert!(
        !db.is_rejected("rel", &["a@b"], 50).await.expect("query"),
        "expired"
    );
    db.prune_rejected_releases(50).await.expect("prune");
    assert!(!db.is_rejected("other", &["c@d"], 0).await.expect("query"));
    assert_eq!(
        db.subtree_stats(dir.id).await.expect("query"),
        SubtreeStats {
            files: 2,
            directories: 1,
            bytes: 5 << 32
        }
    );

    db.delete_item(dir.id).await.expect("delete");
    assert_eq!(
        db.item(file.id).await.expect("query"),
        None,
        "subtree cascades"
    );
    assert_eq!(
        db.blob(meta).await.expect("query"),
        None,
        "unused blobs go too"
    );
    assert_eq!(db.blob(nzb).await.expect("query"), None);
    assert_eq!(
        db.media_probe(other.id).await.expect("query"),
        MediaProbe::default(),
        "probe rows cascade"
    );

    db.write_config(&[("sab", "{}"), ("providers", "[1]")], &[])
        .await
        .expect("write");
    db.write_config(&[("providers", "[2]")], &["sab"])
        .await
        .expect("write");
    assert_eq!(
        db.config().await.expect("query"),
        [("providers".into(), "[2]".into())]
    );

    let job = QueueEntry {
        id: JobId::new(),
        created_at: 1,
        file_name: "job.nzb".into(),
        job_name: "job".into(),
        category: "tv".into(),
        priority: 0,
        post_processing: 3,
        nzb_blob: BlobId::new(),
        nzb_size: 3,
        total_bytes: 5 << 32,
        paused: true,
        retry_at: Some(10),
        attempts: 0,
        stuck_count: 0,
        last_error: None,
        content_hash: None,
    };
    db.enqueue(&job, b"nzb").await.expect("enqueue");
    assert_eq!(
        db.unhashed_jobs().await.expect("query"),
        [(job.id, job.nzb_blob)]
    );
    db.set_content_hash(job.id, "ab").await.expect("hash");
    assert_eq!(db.unhashed_jobs().await.expect("query"), []);
    assert_eq!(
        db.job_with_content("ab").await.expect("query"),
        Some(job.id)
    );
    assert_eq!(db.job_with_content("cd").await.expect("query"), None);
    let job = QueueEntry {
        content_hash: Some("ab".into()),
        ..job
    };
    assert_eq!(db.next_jobs(10, 1).await.expect("query"), [], "paused");
    assert_eq!(db.pause_jobs(&[job.id], false).await.expect("resume"), 1);
    assert_eq!(db.next_jobs(9, 1).await.expect("query"), [], "waiting");
    db.retry_job(job.id, 10, "down").await.expect("retry");
    let job = QueueEntry {
        paused: false,
        attempts: 1,
        last_error: Some("down".into()),
        ..job
    };
    assert_eq!(
        db.next_jobs(10, 1).await.expect("query"),
        std::slice::from_ref(&job)
    );
    let mut out = Item::child(&content, "job", ItemKind::Directory).expect("valid");
    out.nzb_blob = Some(job.nzb_blob);
    let history = HistoryEntry {
        id: job.id,
        created_at: 2,
        file_name: job.file_name.clone(),
        job_name: job.job_name.clone(),
        category: job.category.clone(),
        status: JobStatus::Completed,
        total_bytes: job.total_bytes,
        download_seconds: 1,
        fail_message: None,
        download_dir: Some(out.id),
        nzb_blob: Some(job.nzb_blob),
        storage: None,
    };
    let handoff = Handoff {
        path: "/strm/tv/job/a.strm".into(),
        item: out.id,
        job: Some(job.id),
        size: None,
    };
    assert!(
        db.finish_job(
            &history,
            &[out.clone()],
            &[],
            &[],
            std::slice::from_ref(&handoff)
        )
        .await
        .expect("finish")
    );
    assert!(
        !db.finish_job(&history, &[], &[], &[], &[])
            .await
            .expect("finish"),
        "a job no longer queued is not finished twice"
    );
    assert_eq!(
        db.handoff_item(&handoff.path).await.expect("query"),
        Some(out.id)
    );
    let gone = [
        Handoff {
            path: "/strm/tv/gone/a.strm".into(),
            job: Some(JobId::new()),
            ..handoff.clone()
        },
        Handoff {
            path: "/strm/tv/old/a.srt".into(),
            item: ItemId::new(),
            job: None,
            size: Some(7),
        },
    ];
    db.record_handoffs(&gone).await.expect("record");
    let mut stale = db.stale_handoffs().await.expect("query");
    stale.sort_by(|a, b| a.path.cmp(&b.path));
    assert_eq!(
        stale,
        [gone[0].clone(), gone[1].clone()],
        "job or item gone"
    );
    db.forget_handoffs(&[gone[0].path.clone(), gone[1].path.clone()])
        .await
        .expect("forget");
    assert_eq!(db.stale_handoffs().await.expect("query"), []);
    let category = job.category.to_lowercase();
    let pending = db.pending_refreshes().await.expect("query");
    assert_eq!(pending, [(category.clone(), 1)]);
    let gone = JobId::new();
    db.add_history(
        &HistoryEntry {
            id: gone,
            download_dir: None,
            ..history.clone()
        },
        &[],
        &[],
    )
    .await
    .expect("history");
    db.refreshed(&category, 1).await.expect("refreshed");
    assert_eq!(
        db.pending_refreshes().await.expect("query"),
        [(category.clone(), 2)],
        "a finish after the read stays pending"
    );
    db.refreshed(&category, 2).await.expect("refreshed");
    assert_eq!(db.pending_refreshes().await.expect("query"), []);

    // Search limit: per media, all or nothing for a pack, sliding window, per instance.
    let arr = uuid::Uuid::now_v7();
    let reserve = |ids: &'static [i64], at: i64| db.reserve_searches(arr, "episode", ids, at);
    for at in [0, 1, 2] {
        assert!(reserve(&[1], at).await.expect("reserve"));
    }
    assert!(
        !reserve(&[2, 1], 3).await.expect("reserve"),
        "episode 1 is at the limit"
    );
    assert!(
        reserve(&[2], 3).await.expect("reserve"),
        "the denied pack reserved nothing"
    );
    assert!(
        db.reserve_searches(uuid::Uuid::now_v7(), "episode", &[1], 3)
            .await
            .expect("reserve")
    );
    assert!(
        reserve(&[1], infinidysk_db::SEARCH_WINDOW_MS)
            .await
            .expect("reserve")
    );
    assert_eq!(db.queue().await.expect("query"), []);
    assert_eq!(
        db.history_entry(job.id).await.expect("query"),
        Some(history.clone())
    );
    assert_eq!(
        db.completed_dirs(ItemId::CONTENT).await.expect("query"),
        [out.clone()]
    );
    assert!(db.is_completed_dir(out.id).await.expect("query"));
    assert!(!db.is_completed_dir(content.id).await.expect("query"));

    // History filters run in SQL; a completed job without its folder reports as failed.
    let mut both = vec![job.id, gone];
    both.sort_by_key(|id| id.0);
    let all = HistoryQuery::default();
    assert_eq!(history_ids(db, all).await, (both.clone(), 2));
    let completed = HistoryQuery {
        status: Some(JobStatus::Completed),
        ..all
    };
    assert_eq!(history_ids(db, completed).await, (vec![job.id], 1));
    let failed = HistoryQuery {
        status: Some(JobStatus::Failed),
        search: Some("JOB.N"),
        category: Some("tv"),
        ..all
    };
    assert_eq!(history_ids(db, failed).await, (vec![gone], 1));
    // The cutoff drops stored failures only; a missing folder cannot be retried.
    let stored_failure = JobId::new();
    db.add_history(
        &HistoryEntry {
            id: stored_failure,
            status: JobStatus::Failed,
            download_dir: None,
            ..history.clone()
        },
        &[],
        &[],
    )
    .await
    .expect("history");
    let recent = HistoryQuery {
        failed_since: Some(i64::MAX),
        ..all
    };
    assert_eq!(history_ids(db, recent).await, (both.clone(), 2));
    let recent_ids = HistoryQuery {
        ids: Some(&[stored_failure, gone]),
        ..recent
    };
    assert_eq!(history_ids(db, recent_ids).await, (vec![gone], 1));
    let recent_failed = HistoryQuery {
        status: Some(JobStatus::Failed),
        ..recent
    };
    assert_eq!(history_ids(db, recent_failed).await, (vec![gone], 1));
    let recent_failed_ids = HistoryQuery {
        ids: Some(&[stored_failure, gone]),
        ..recent_failed
    };
    assert_eq!(history_ids(db, recent_failed_ids).await, (vec![gone], 1));
    let old = HistoryQuery {
        failed_since: Some(i64::MIN),
        ..all
    };
    assert_eq!(history_ids(db, old).await.1, 3);
    let at_cutoff = HistoryQuery {
        failed_since: Some(history.created_at),
        ..all
    };
    let (rows, total) = history_ids(db, at_cutoff).await;
    assert_eq!(total, 3);
    assert!(rows.contains(&stored_failure));
    let at_cutoff_ids = HistoryQuery {
        ids: Some(&[stored_failure]),
        ..at_cutoff
    };
    assert_eq!(
        history_ids(db, at_cutoff_ids).await,
        (vec![stored_failure], 1)
    );
    assert_eq!(
        db.delete_history(&[stored_failure]).await.expect("delete"),
        1
    );
    let by_id = HistoryQuery {
        ids: Some(&[gone, JobId::new(), job.id]),
        offset: 1,
        limit: Some(5),
        ..all
    };
    assert_eq!(history_ids(db, by_id).await, (vec![both[1]], 2), "paged");
    let escaped = HistoryQuery {
        search: Some("j%"),
        ..all
    };
    let other = HistoryQuery {
        category: Some("TV"),
        ..all
    };
    for q in [escaped, other] {
        assert_eq!(history_ids(db, q).await, (vec![], 0));
    }
    assert_eq!(db.history_ids(true).await.expect("query"), []);
    assert_eq!(db.history_ids(false).await.expect("query").len(), 2);

    assert_eq!(db.delete_history(&[job.id]).await.expect("delete"), 1);
    assert_eq!(
        db.job_at(&out.path).await.expect("query"),
        Some((job.id, job.job_name.clone())),
        "provenance outlives history"
    );
    assert_eq!(db.job_at(&content.path).await.expect("query"), None);

    // Arr owners: an item is free once its last owner forgets it.
    assert_eq!(
        db.job_dir(job.id).await.expect("query"),
        Some(out.path.clone())
    );
    let sub = Item::child(&out, "S", ItemKind::Directory).expect("valid");
    let ep = Item::child(&sub, "e.mkv", ItemKind::NzbFile).expect("valid");
    db.insert(&[sub.clone(), ep.clone()], &[])
        .await
        .expect("insert");
    let (a, b) = (uuid::Uuid::now_v7(), uuid::Uuid::now_v7());
    db.record_owner(a, ep.id, Some(7), "/tv/S/e.mkv", Some(job.id))
        .await
        .expect("record");
    db.record_owner(b, ep.id, None, "/tv2/S/e.mkv", None)
        .await
        .expect("record");
    db.rename_owner(a, &OwnedFile::Id(7), "/tv/S/f.mkv")
        .await
        .expect("rename");
    let forget = |arr, which| async move { db.forget_owners(arr, &which).await.expect("forget") };
    assert_eq!(forget(a, OwnedFile::Path("/tv/S/e.mkv".into())).await, []);
    let mut owners = db.owner_instances(ep.id).await.expect("query");
    owners.sort_unstable();
    assert_eq!(owners, [a.min(b), a.max(b)], "renamed, not forgotten");
    assert_eq!(
        forget(a, OwnedFile::Under("/tv/S".into())).await,
        [],
        "b still owns it"
    );
    assert_eq!(forget(b, OwnedFile::Under("/tv".into())).await, []);
    assert_eq!(forget(b, OwnedFile::Under("/tv2/".into())).await, [ep.id]);
    db.record_owner(a, ep.id, Some(9), "/tv/x.mkv", None)
        .await
        .expect("record");
    let owner = ArrOwner {
        item: ep.id,
        file_id: Some(9),
        path: "/tv/x.mkv".into(),
    };
    assert_eq!(
        db.owners(a).await.expect("query"),
        std::slice::from_ref(&owner)
    );
    let stale = ArrOwner {
        path: "/tv/old.mkv".into(),
        ..owner.clone()
    };
    assert_eq!(
        db.forget_owner(a, &stale).await.expect("forget"),
        None,
        "changed since read"
    );
    db.refile_owner(a, &owner, 10).await.expect("refile");
    let owner = ArrOwner {
        file_id: Some(10),
        ..owner
    };
    assert_eq!(
        db.owners(a).await.expect("query"),
        std::slice::from_ref(&owner)
    );
    assert_eq!(
        db.forget_owner(a, &owner).await.expect("forget"),
        Some(ep.id)
    );

    // Cleanup deletes an item only while no Arr owns it.
    let re = Item::child(&sub, "f.mkv", ItemKind::NzbFile).expect("valid");
    db.insert(std::slice::from_ref(&re), &[])
        .await
        .expect("insert");
    let owner = ArrOwner {
        item: re.id,
        file_id: Some(11),
        path: "/tv/f.mkv".into(),
    };
    db.record_owner(a, re.id, owner.file_id, &owner.path, None)
        .await
        .expect("record");
    assert_eq!(
        db.forget_owner(a, &owner).await.expect("forget"),
        Some(re.id)
    );
    let owner = ArrOwner {
        item: re.id,
        file_id: None,
        path: "/tv2/f.mkv".into(),
    };
    db.record_owner(b, re.id, owner.file_id, &owner.path, None)
        .await
        .expect("record");
    assert!(
        !db.delete_unowned(re.id).await.expect("delete"),
        "b adopted it"
    );
    assert!(db.item(re.id).await.expect("query").is_some());
    assert_eq!(db.owner_instances(re.id).await.expect("query"), [b]);
    assert_eq!(
        db.forget_owner(b, &owner).await.expect("forget"),
        Some(re.id)
    );
    assert!(db.delete_unowned(re.id).await.expect("delete"));
    assert_eq!(db.item(re.id).await.expect("query"), None);
    assert!(
        !db.delete_unowned(ItemId::new()).await.expect("delete"),
        "no such item"
    );

    db.push_arr_event(a, b"1").await.expect("push");
    db.push_arr_event(b, b"2").await.expect("push");
    let (id, arr, body) = db.next_arr_event().await.expect("query").expect("stored");
    assert_eq!((arr, body.as_slice()), (a, &b"1"[..]));
    db.drop_arr_event(id).await.expect("drop");
    let (id, _, _) = db.next_arr_event().await.expect("query").expect("stored");
    db.drop_arr_event(id).await.expect("drop");
    assert_eq!(db.next_arr_event().await.expect("query"), None);

    let job = QueueEntry {
        id: JobId::new(),
        nzb_blob: BlobId::new(),
        ..job
    };
    db.enqueue(&job, b"nzb").await.expect("enqueue");
    assert!(db.remove_job(job.id).await.expect("remove"));
    assert!(!db.remove_job(job.id).await.expect("remove"));
    assert_eq!(db.blob(job.nzb_blob).await.expect("query"), None);

    // NZB retention: kept through the window, reused by identical content, then expired.
    db.set_nzb_retention_days(1);
    let job = QueueEntry {
        id: JobId::new(),
        nzb_blob: BlobId::new(),
        content_hash: Some("ef".into()),
        ..job
    };
    db.enqueue(&job, b"nzb").await.expect("enqueue");
    assert_eq!(
        db.nzbs_with_content("ef").await.expect("query"),
        [job.nzb_blob]
    );
    assert!(db.remove_job(job.id).await.expect("remove"));
    assert_eq!(db.delete_expired_nzbs().await.expect("expire"), 0);
    let again = QueueEntry {
        id: JobId::new(),
        ..job.clone()
    };
    db.enqueue(&again, b"unused").await.expect("reuse");
    assert_eq!(
        db.blob(job.nzb_blob).await.expect("query").as_deref(),
        Some(&b"nzb"[..]),
        "the kept NZB is reused"
    );
    db.set_nzb_retention_days(0);
    assert_eq!(
        db.delete_expired_nzbs().await.expect("expire"),
        0,
        "referenced again"
    );
    db.set_nzb_retention_days(1);
    assert!(db.remove_job(again.id).await.expect("remove"));
    db.set_nzb_retention_days(0);
    assert_eq!(db.delete_expired_nzbs().await.expect("expire"), 1);
    assert_eq!(db.blob(job.nzb_blob).await.expect("query"), None);

    assert_eq!(db.prune_history(3).await.expect("prune"), 1);
    assert_eq!(db.prune_history(3).await.expect("prune"), 0);
    assert!(
        db.item(out.id).await.expect("query").is_some(),
        "pruning keeps downloads"
    );
    db.delete_item(ep.id).await.expect("delete");
    assert_eq!(
        db.item(out.id).await.expect("query"),
        None,
        "an emptied job folder goes"
    );
    assert!(db.item(content.id).await.expect("query").is_some());
    unlinked(db).await;
    restored_nzb_links(db).await;
    first_checks(db).await;
}

/// A finished job stores its files' first health checks with its output.
async fn first_checks(db: &Db) {
    let content = db
        .item(ItemId::CONTENT)
        .await
        .expect("query")
        .expect("seeded");
    let job = QueueEntry {
        id: JobId::new(),
        created_at: 1,
        file_name: "first.nzb".into(),
        job_name: "first".into(),
        category: "tv".into(),
        priority: 0,
        post_processing: 3,
        nzb_blob: BlobId::new(),
        nzb_size: 3,
        total_bytes: 1,
        paused: false,
        retry_at: None,
        attempts: 0,
        stuck_count: 0,
        last_error: None,
        content_hash: None,
    };
    db.enqueue(&job, b"nzb").await.expect("enqueue");
    let dir = Item::child(&content, "first", ItemKind::Directory).expect("valid");
    let good = Item::child(&dir, "a.mkv", ItemKind::NzbFile).expect("valid");
    let broken = Item::child(&dir, "b.mp4", ItemKind::NzbFile).expect("valid");
    let healthy = FirstCheck {
        status: HealthStatus {
            state: HealthState::Healthy,
            last_check: Some(5),
            next_check: Some(6),
            ..HealthStatus::default()
        },
        result: HealthResult::Healthy,
        message: None,
        defect: None,
        analyze: true,
    };
    let defect = Defect {
        offset: 32,
        size: 1 << 40,
        seen_at: 5,
        source: 3,
    };
    let suspect = FirstCheck {
        status: HealthStatus {
            state: HealthState::Suspect,
            next_check: Some(7),
            ..healthy.status
        },
        result: HealthResult::Damaged,
        message: Some("broken".into()),
        defect: Some(defect),
        analyze: false,
    };
    let history = HistoryEntry {
        id: job.id,
        created_at: 2,
        file_name: job.file_name.clone(),
        job_name: job.job_name.clone(),
        category: job.category.clone(),
        status: JobStatus::Completed,
        total_bytes: 1,
        download_seconds: 1,
        fail_message: None,
        download_dir: Some(dir.id),
        nzb_blob: Some(job.nzb_blob),
        storage: None,
    };
    let checks = [(good.id, healthy.clone()), (broken.id, suspect.clone())];
    let items = [dir, good.clone(), broken.clone()];
    assert!(
        db.finish_job(&history, &items, &[], &checks, &[])
            .await
            .expect("finish")
    );
    for (item, check) in [(&good, &healthy), (&broken, &suspect)] {
        let (_, status) = db.health(item.id).await.expect("query").expect("stored");
        assert_eq!(status, check.status);
        let recorded = HealthCheck {
            created_at: 5,
            item_id: item.id,
            path: item.path.clone(),
            result: check.result,
            repair: Repair::None,
            message: check.message.clone(),
        };
        assert_eq!(db.item_checks(item.id, 5).await.expect("query"), [recorded]);
    }
    let probe = db.media_probe(good.id).await.expect("query");
    assert_eq!(
        (probe.requested_at, probe.structural_hold),
        (Some(5), false)
    );
    let probe = db.media_probe(broken.id).await.expect("query");
    assert_eq!(
        (probe.requested_at, probe.defect, probe.structural_hold),
        (None, Some(defect), true)
    );
}

async fn history_ids(db: &Db, q: HistoryQuery<'_>) -> (Vec<JobId>, u64) {
    let (rows, total) = db.history_page(&q).await.expect("history");
    (rows.into_iter().map(|(h, _)| h.id).collect(), total)
}

/// Requests coalesce, hidden files wait, completion keeps newer requests and metadata.
async fn media_probes(db: &Db, open: &Item, hidden: &Item) {
    db.request_media_probe(hidden.id, 5).await.expect("request");
    db.request_media_probe(open.id, 10).await.expect("request");
    db.request_media_probe(open.id, 20).await.expect("request");
    let (item, at) = db
        .next_media_probe()
        .await
        .expect("query")
        .expect("pending");
    assert_eq!((item.id, at), (open.id, 10), "hidden files wait");
    let found = ProbeAttempt {
        at: 30,
        outcome: ProbeOutcome::Discovered,
        reason: None,
        info: Some("{\"v\":1}"),
    };
    db.finish_media_probe(open.id, 9, &found)
        .await
        .expect("finish");
    assert_eq!(
        db.media_probe(open.id).await.expect("query").requested_at,
        Some(10),
        "a newer request survives"
    );
    db.finish_media_probe(open.id, 10, &found)
        .await
        .expect("finish");
    let failed = ProbeAttempt {
        at: 40,
        outcome: ProbeOutcome::Inconclusive,
        reason: Some("timed out"),
        info: None,
    };
    db.finish_media_probe(open.id, 10, &failed)
        .await
        .expect("finish");
    let defect = Defect {
        offset: 1 << 33,
        size: 4,
        seen_at: 50,
        source: -7,
    };
    db.set_structural(open.id, Some(&defect), true)
        .await
        .expect("set");
    db.set_structural(ItemId::new(), Some(&defect), true)
        .await
        .expect("gone items are skipped");
    assert_eq!(
        db.media_probe(open.id).await.expect("query"),
        MediaProbe {
            requested_at: None,
            attempted_at: Some(40),
            outcome: Some(ProbeOutcome::Inconclusive),
            reason: Some("timed out".into()),
            info: Some("{\"v\":1}".into()),
            info_at: Some(30),
            defect: Some(defect),
            structural_hold: true,
        },
        "a failed attempt keeps the last metadata"
    );
    assert_eq!(
        db.media_probe(hidden.id).await.expect("query").requested_at,
        Some(5)
    );
}

/// Unlinked-file cleanup: only files outside every download in history go, then the
/// folders that leaves empty.
async fn unlinked(db: &Db) {
    let content = db
        .item_by_path("/content")
        .await
        .expect("query")
        .expect("seeded");
    let cat = Item::child(&content, "maint", ItemKind::Directory).expect("valid");
    let mut kept = Item::child(&cat, "kept", ItemKind::Directory).expect("valid");
    let mut loose = Item::child(&cat, "loose", ItemKind::Directory).expect("valid");
    let (kept_nzb, loose_nzb) = (BlobId::new(), BlobId::new());
    kept.nzb_blob = Some(kept_nzb);
    loose.nzb_blob = Some(loose_nzb);
    let kept_file = Item::child(&kept, "a.mkv", ItemKind::NzbFile).expect("valid");
    let loose_file = Item::child(&loose, "b.mkv", ItemKind::NzbFile).expect("valid");
    let empty = Item::child(&loose, "sub", ItemKind::Directory).expect("valid");
    db.insert(
        &[
            cat.clone(),
            kept.clone(),
            loose.clone(),
            kept_file.clone(),
            loose_file.clone(),
            empty.clone(),
        ],
        &[(kept_nzb, b"nzb"), (loose_nzb, b"nzb")],
    )
    .await
    .expect("insert");
    db.import_history(&[HistoryEntry {
        id: JobId::new(),
        created_at: 1,
        file_name: "kept.nzb".into(),
        job_name: "kept".into(),
        category: "maint".into(),
        status: JobStatus::Completed,
        total_bytes: 1,
        download_seconds: 1,
        fail_message: None,
        download_dir: Some(kept.id),
        nzb_blob: Some(kept_nzb),
        storage: None,
    }])
    .await
    .expect("history");
    let before = infinidysk_db::now_ms() + 1;
    let paths: Vec<String> = db
        .files_outside_history(before)
        .await
        .expect("query")
        .into_iter()
        .map(|i| i.path)
        .filter(|p| p.starts_with("/content/maint/"))
        .collect();
    assert_eq!(paths, std::slice::from_ref(&loose_file.path));
    assert!(
        !db.delete_unlinked(kept_file.id, before)
            .await
            .expect("delete")
    );
    assert!(
        !db.delete_unlinked(loose_file.id, 0).await.expect("delete"),
        "files added after the run started stay"
    );
    assert!(
        db.delete_unlinked(loose_file.id, before)
            .await
            .expect("delete")
    );
    assert!(db.delete_empty_dirs(before).await.expect("sweep") >= 1);
    assert_eq!(db.item(loose.id).await.expect("query"), None);
    assert!(db.item(kept.id).await.expect("query").is_some());
    assert!(db.item(cat.id).await.expect("query").is_some());
    assert_eq!(db.blob(loose_nzb).await.expect("query"), None);
}

async fn restored_nzb_links(db: &Db) {
    let content = db
        .item(ItemId::CONTENT)
        .await
        .expect("NZB recovery fixture")
        .expect("NZB recovery fixture");
    let item = Item::child(&content, "restored", ItemKind::NzbFile).expect("NZB recovery fixture");
    let download = BlobId::new();
    db.insert(std::slice::from_ref(&item), &[(download, b"nzb")])
        .await
        .expect("NZB recovery fixture");
    let history = HistoryEntry {
        id: JobId::new(),
        created_at: 1,
        file_name: "restored.nzb".into(),
        job_name: "restored".into(),
        category: String::new(),
        status: JobStatus::Completed,
        total_bytes: 0,
        download_seconds: 0,
        fail_message: None,
        download_dir: None,
        nzb_blob: None,
        storage: None,
    };
    db.import_history(std::slice::from_ref(&history))
        .await
        .expect("NZB recovery fixture");
    let (items, histories) = db.missing_nzb_links().await.expect("NZB recovery fixture");
    assert!(items.contains(&item.id));
    assert!(histories.contains(&history.id));
    let missing = BlobId::new();
    assert_eq!(
        db.restore_nzb_links(&[(item.id, missing)], &[(history.id, missing)])
            .await
            .expect("NZB recovery fixture"),
        0
    );
    assert_eq!(
        db.restore_nzb_links(&[(item.id, download)], &[(history.id, download)])
            .await
            .expect("NZB recovery fixture"),
        2
    );
    let other = BlobId::new();
    db.insert(&[], &[(other, b"other")])
        .await
        .expect("NZB recovery fixture");
    assert_eq!(
        db.restore_nzb_links(&[(item.id, other)], &[(history.id, other)])
            .await
            .expect("NZB recovery fixture"),
        0
    );
    assert_eq!(
        db.item(item.id)
            .await
            .expect("NZB recovery fixture")
            .expect("NZB recovery fixture")
            .nzb_blob,
        Some(download)
    );
    assert_eq!(
        db.history_entry(history.id)
            .await
            .expect("NZB recovery fixture")
            .expect("NZB recovery fixture")
            .nzb_blob,
        Some(download)
    );
    assert_eq!(
        db.restore_nzb_links(&[(ItemId::new(), download)], &[(JobId::new(), download)])
            .await
            .expect("NZB recovery fixture"),
        0
    );
    let (items, histories) = db.missing_nzb_links().await.expect("NZB recovery fixture");
    assert!(!items.contains(&item.id));
    assert!(!histories.contains(&history.id));
}

/// An owner insert still in progress when cleanup checks the item keeps the item.
async fn adoption_during_cleanup_keeps_the_item(db: &Db, url: &str) {
    const RECORD: &str = "INSERT INTO arr_owners (instance_id, item_id, file_id, path, job_id) \
                          VALUES ($1, $2, $3, $4, $5) ON CONFLICT (instance_id, item_id) DO UPDATE \
                          SET file_id = excluded.file_id, path = excluded.path, job_id = excluded.job_id";
    let content = db
        .item_by_path("/content")
        .await
        .expect("query")
        .expect("seeded");
    let item = Item::child(&content, "adopted.mkv", ItemKind::NzbFile).expect("valid");
    db.insert(std::slice::from_ref(&item), &[])
        .await
        .expect("insert");
    let arr = uuid::Uuid::now_v7();
    let id = item.id;
    let cleanup = |db: Db| tokio::spawn(async move { db.delete_unowned(id).await });
    let pause = std::time::Duration::from_millis(200);
    let deleted = if url.starts_with("sqlite:") {
        let raw = sqlx::SqlitePool::connect(url).await.expect("open");
        let mut tx = raw.begin().await.expect("begin");
        sqlx::query(RECORD)
            .bind(arr)
            .bind(id.0)
            .bind(Some(1_i64))
            .bind("/tv/adopted.mkv")
            .bind(None::<uuid::Uuid>)
            .execute(&mut *tx)
            .await
            .expect("record");
        let deleted = cleanup(db.clone());
        tokio::time::sleep(pause).await;
        tx.commit().await.expect("commit");
        let deleted = deleted.await.expect("join");
        raw.close().await;
        deleted
    } else {
        let raw = sqlx::PgPool::connect(url).await.expect("open");
        let mut tx = raw.begin().await.expect("begin");
        sqlx::query(RECORD)
            .bind(arr)
            .bind(id.0)
            .bind(Some(1_i64))
            .bind("/tv/adopted.mkv")
            .bind(None::<uuid::Uuid>)
            .execute(&mut *tx)
            .await
            .expect("record");
        let deleted = cleanup(db.clone());
        let deadline = tokio::time::Instant::now() + std::time::Duration::from_secs(10);
        while sqlx::query_scalar::<_, i64>(
            "SELECT COUNT(*) FROM pg_stat_activity WHERE datname = current_database() \
             AND wait_event_type = 'Lock' AND query LIKE '%FROM items WHERE id = $1 FOR UPDATE'",
        )
        .fetch_one(&raw)
        .await
        .expect("query")
            == 0
        {
            assert!(
                tokio::time::Instant::now() < deadline,
                "cleanup never waited on the item lock"
            );
            tokio::time::sleep(std::time::Duration::from_millis(10)).await;
        }
        tx.commit().await.expect("commit");
        let deleted = deleted.await.expect("join");
        raw.close().await;
        deleted
    };
    assert!(
        !matches!(deleted, Ok(true)),
        "cleanup deleted an adopted item: {deleted:?}"
    );
    assert!(db.item(id).await.expect("query").is_some());
    assert_eq!(db.owner_instances(id).await.expect("query"), [arr]);
}

#[tokio::test]
async fn sqlite_storage_round_trips() {
    let path = std::env::temp_dir().join(format!("infinidysk-db-{}.sqlite", ItemId::new()));
    let url = format!("sqlite:{}", path.display());
    let db = Db::connect(&url).await.expect("connect");
    exercise(&db).await;
    adoption_during_cleanup_keeps_the_item(&db, &url).await;
    db.close().await;
    for suffix in ["", "-wal", "-shm"] {
        let _ = std::fs::remove_file(format!("{}{suffix}", path.display()));
    }
}

#[tokio::test]
async fn pausing_writes_waits_for_permits_and_holds_new_ones() {
    use std::time::Duration;
    use tokio::time::timeout;
    let path = std::env::temp_dir().join(format!("infinidysk-db-{}.sqlite", ItemId::new()));
    let db = Db::connect(&format!("sqlite:{}", path.display()))
        .await
        .expect("connect");
    let wait = Duration::from_millis(50);
    let permit = db.write_permit().await;
    let pause = db.pause_writes();
    tokio::pin!(pause);
    assert!(
        timeout(wait, &mut pause).await.is_err(),
        "a permit holds the pause back"
    );
    drop(permit);
    let paused = timeout(wait, pause)
        .await
        .expect("pauses once the permit drops");
    let clone = db.clone();
    assert!(
        timeout(wait, clone.write_permit()).await.is_err(),
        "the pause holds new permits back, on clones too"
    );
    drop(paused);
    timeout(wait, db.write_permit())
        .await
        .expect("permits resume");
    db.close().await;
    for suffix in ["", "-wal", "-shm"] {
        let _ = std::fs::remove_file(format!("{}{suffix}", path.display()));
    }
}

#[tokio::test]
async fn a_database_from_another_build_is_refused() {
    let path = std::env::temp_dir().join(format!("infinidysk-db-{}.sqlite", ItemId::new()));
    let url = format!("sqlite:{}", path.display());
    Db::connect(&url).await.expect("connect").close().await;
    let pool = sqlx::SqlitePool::connect(&url).await.expect("open");
    sqlx::query(
        "INSERT INTO _sqlx_migrations (version, description, success, checksum, execution_time) \
         VALUES (999, 'unknown', TRUE, X'00', 0)",
    )
    .execute(&pool)
    .await
    .expect("insert");
    pool.close().await;
    assert!(matches!(
        Db::connect(&url).await,
        Err(infinidysk_db::Error::OtherSchema(_))
    ));
    for suffix in ["", "-wal", "-shm"] {
        let _ = std::fs::remove_file(format!("{}{suffix}", path.display()));
    }
}

/// Runs when `INFINIDYSK_TEST_POSTGRES_URL` points at a server whose user may create databases.
#[tokio::test]
async fn postgres_storage_round_trips() {
    let Ok(admin_url) = std::env::var("INFINIDYSK_TEST_POSTGRES_URL") else {
        eprintln!("skipped: INFINIDYSK_TEST_POSTGRES_URL is not set");
        return;
    };
    let name = format!("infinidysk_test_{}", ItemId::new().0.simple());
    let admin = sqlx::PgPool::connect(&admin_url)
        .await
        .expect("connect admin");
    let create = sqlx::AssertSqlSafe(format!("CREATE DATABASE {name}"));
    sqlx::query(create).execute(&admin).await.expect("create");
    let (base, _) = admin_url.rsplit_once('/').expect("url has a database path");

    let url = format!("{base}/{name}");
    let db = Db::connect(&url).await.expect("connect");
    exercise(&db).await;
    adoption_during_cleanup_keeps_the_item(&db, &url).await;
    db.close().await;
    let drop = sqlx::AssertSqlSafe(format!("DROP DATABASE {name}"));
    sqlx::query(drop).execute(&admin).await.expect("drop");
}

fn temp_sqlite() -> std::path::PathBuf {
    std::env::temp_dir().join(format!("infinidysk-db-{}.sqlite", ItemId::new()))
}

fn remove_set(path: &std::path::Path) {
    for suffix in ["", "-wal", "-shm"] {
        let _ = std::fs::remove_file(format!("{}{suffix}", path.display()));
    }
}

/// Runs `op` while another connection holds an uncommitted write, then commits it.
/// A cleanup that reads in a deferred transaction fails here with code 517.
async fn under_contention<T: Send + 'static>(
    url: &str,
    op: impl Future<Output = Result<T, infinidysk_db::Error>> + Send + 'static,
) -> T {
    let raw = sqlx::SqlitePool::connect(url).await.expect("open");
    sqlx::query("CREATE TABLE IF NOT EXISTS contention (x INTEGER)")
        .execute(&raw)
        .await
        .expect("create");
    let mut tx = raw.begin().await.expect("begin");
    sqlx::query("INSERT INTO contention VALUES (1)")
        .execute(&mut *tx)
        .await
        .expect("write");
    let op = tokio::spawn(op);
    tokio::time::sleep(std::time::Duration::from_millis(200)).await;
    tx.commit().await.expect("commit");
    let out = op
        .await
        .expect("join")
        .expect("cleanup survives the other writer");
    raw.close().await;
    out
}

#[tokio::test]
async fn cleanup_survives_a_concurrent_sqlite_writer() {
    let path = temp_sqlite();
    let url = format!("sqlite:{}", path.display());
    let db = Db::connect(&url).await.expect("connect");
    let content = db
        .item_by_path("/content")
        .await
        .expect("query")
        .expect("seeded");
    let cat = Item::child(&content, "contention", ItemKind::Directory).expect("valid");
    let job = Item::child(&cat, "job", ItemKind::Directory).expect("valid");
    let file = Item::child(&job, "a.mkv", ItemKind::NzbFile).expect("valid");
    let folder = Item::child(&cat, "nofiles", ItemKind::Directory).expect("valid");
    let unowned = Item::child(&content, "unowned.mkv", ItemKind::NzbFile).expect("valid");
    let empty = Item::child(&cat, "empty", ItemKind::Directory).expect("valid");
    db.insert(
        &[
            cat,
            job,
            file.clone(),
            folder.clone(),
            unowned.clone(),
            empty,
        ],
        &[],
    )
    .await
    .expect("insert");
    let before = infinidysk_db::now_ms() + 1;

    let d = db.clone();
    assert!(under_contention(&url, async move { d.delete_unowned(unowned.id).await }).await);
    let d = db.clone();
    assert!(under_contention(&url, async move { d.delete_without_files(folder.id).await }).await);
    let d = db.clone();
    assert!(
        under_contention(
            &url,
            async move { d.delete_unlinked(file.id, before).await }
        )
        .await
    );
    let d = db.clone();
    assert!(under_contention(&url, async move { d.delete_empty_dirs(before).await }).await >= 1);

    db.close().await;
    remove_set(&path);
}

#[tokio::test]
async fn backup_into_copies_a_consistent_database() {
    let path = temp_sqlite();
    let db = Db::connect(&format!("sqlite:{}", path.display()))
        .await
        .expect("connect");
    let content = db
        .item_by_path("/content")
        .await
        .expect("query")
        .expect("seeded");
    let dir = Item::child(&content, "job", ItemKind::Directory).expect("valid");
    let file = Item::child(&dir, "a.mkv", ItemKind::NzbFile).expect("valid");
    db.insert(&[dir, file], &[]).await.expect("insert");
    db.write_config(&[("backup.test", "\"kept\"")], &[])
        .await
        .expect("config");
    let copy = temp_sqlite();
    db.backup_into(&copy, || false).await.expect("backup");
    let pool = sqlx::SqlitePool::connect(&format!("sqlite:{}", path.display()))
        .await
        .expect("open");
    let count: i64 = sqlx::query_scalar("SELECT count(*) FROM items")
        .fetch_one(&pool)
        .await
        .expect("count");
    pool.close().await;
    let copied = sqlx::SqlitePool::connect(&format!("sqlite:{}", copy.display()))
        .await
        .expect("open copy");
    let kept: Option<String> =
        sqlx::query_scalar("SELECT value FROM config WHERE key = 'backup.test'")
            .fetch_optional(&copied)
            .await
            .expect("config row");
    copied.close().await;
    assert_eq!(kept.as_deref(), Some("\"kept\""));
    let check = infinidysk_db::check_file(&copy, || false)
        .await
        .expect("check");
    assert_eq!(check.incompatible, None);
    assert!(check.problems.is_empty(), "{:?}", check.problems);
    assert_eq!(check.items, u64::try_from(count).expect("positive"));
    assert_eq!(check.schema_version, infinidysk_db::latest_sqlite_schema());
    db.close().await;
    remove_set(&path);
    remove_set(&copy);
}

#[tokio::test]
async fn backup_into_stops_when_asked() {
    let path = temp_sqlite();
    let url = format!("sqlite:{}", path.display());
    let db = Db::connect(&url).await.expect("connect");
    let pool = sqlx::SqlitePool::connect(&url).await.expect("open");
    sqlx::query(
        "WITH RECURSIVE n(i) AS (SELECT 1 UNION ALL SELECT i + 1 FROM n WHERE i < 50000) \
         INSERT INTO config (key, value) SELECT 'k' || i, 'v' FROM n",
    )
    .execute(&pool)
    .await
    .expect("seed");
    pool.close().await;
    let copy = temp_sqlite();
    assert!(matches!(
        db.backup_into(&copy, || true).await,
        Err(Error::Interrupted)
    ));
    db.close().await;
    remove_set(&path);
    remove_set(&copy);
}

#[tokio::test]
async fn check_file_refuses_a_newer_database() {
    let path = temp_sqlite();
    let db = Db::connect(&format!("sqlite:{}", path.display()))
        .await
        .expect("connect");
    let copy = temp_sqlite();
    db.backup_into(&copy, || false).await.expect("backup");
    db.close().await;
    let pool = sqlx::SqlitePool::connect(&format!("sqlite:{}", copy.display()))
        .await
        .expect("open");
    sqlx::query(
        "INSERT INTO _sqlx_migrations (version, description, success, checksum, execution_time) \
         VALUES ($1, 'next', TRUE, X'00', 0)",
    )
    .bind(infinidysk_db::latest_sqlite_schema() + 1)
    .execute(&pool)
    .await
    .expect("insert");
    pool.close().await;
    let check = infinidysk_db::check_file(&copy, || false)
        .await
        .expect("check");
    assert!(
        check
            .incompatible
            .as_deref()
            .is_some_and(|w| w.contains("newer")),
        "{check:?}"
    );
    remove_set(&path);
    remove_set(&copy);
}

#[tokio::test]
async fn check_file_refuses_an_unfinished_migration() {
    let path = temp_sqlite();
    let db = Db::connect(&format!("sqlite:{}", path.display()))
        .await
        .expect("connect");
    let copy = temp_sqlite();
    db.backup_into(&copy, || false).await.expect("backup");
    db.close().await;
    let pool = sqlx::SqlitePool::connect(&format!("sqlite:{}", copy.display()))
        .await
        .expect("open");
    sqlx::query("UPDATE _sqlx_migrations SET success = FALSE WHERE version = $1")
        .bind(infinidysk_db::latest_sqlite_schema())
        .execute(&pool)
        .await
        .expect("update");
    pool.close().await;
    let check = infinidysk_db::check_file(&copy, || false)
        .await
        .expect("check");
    assert!(
        check
            .incompatible
            .as_deref()
            .is_some_and(|w| w.contains("did not finish")),
        "{check:?}"
    );
    remove_set(&path);
    remove_set(&copy);
}

#[tokio::test]
async fn check_file_names_a_1x_database() {
    let path = temp_sqlite();
    let pool = sqlx::SqlitePool::connect(&format!("sqlite:{}?mode=rwc", path.display()))
        .await
        .expect("open");
    sqlx::query(r#"CREATE TABLE "__EFMigrationsHistory" (x)"#)
        .execute(&pool)
        .await
        .expect("create");
    pool.close().await;
    let check = infinidysk_db::check_file(&path, || false)
        .await
        .expect("check");
    assert!(
        check
            .incompatible
            .as_deref()
            .is_some_and(|w| w.contains("1.x")),
        "{check:?}"
    );
    std::fs::write(&path, vec![7u8; 8192]).expect("write");
    let check = infinidysk_db::check_file(&path, || false)
        .await
        .expect("check");
    assert!(check.incompatible.is_some(), "{check:?}");
    remove_set(&path);
}
