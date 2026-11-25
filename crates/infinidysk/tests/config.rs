use infinidysk::config::{
    ArrInstance, ArrProfile, ConfigStore, Error, LibraryDir, MASK, Provider, RcloneRc, Secret,
    Settings,
};
use infinidysk_db::Db;

#[tokio::test]
async fn env_overrides_win_are_never_stored_and_masked_secrets_survive_saves() {
    let path =
        std::env::temp_dir().join(format!("infinidysk-config-{}.sqlite", uuid::Uuid::now_v7()));
    let db = Db::connect(&format!("sqlite:{}", path.display()))
        .await
        .expect("connect");
    let env = [
        ("INFINIDYSK__SAB__API_KEY".to_owned(), "123".to_owned()),
        ("INFINIDYSK__RCLONE_RC__USER".to_owned(), "rc".to_owned()),
        (
            "INFINIDYSK__RCLONE_RC__PASSWORD".to_owned(),
            "rc-env-secret".to_owned(),
        ),
        ("UNRELATED".to_owned(), "x".to_owned()),
    ];
    let mut store = ConfigStore::load(db.clone(), env.clone())
        .await
        .expect("load");
    assert_eq!(
        store.settings().sab.api_key,
        Secret("123".into()),
        "string fields stay verbatim"
    );
    let mut locked: Vec<_> = store.locked().collect();
    locked.sort();
    assert_eq!(
        locked,
        ["rclone_rc.password", "rclone_rc.user", "sab.api_key"]
    );

    let mut settings = store.settings().clone();
    settings.providers.push(Provider {
        profile: Some("eweka".into()),
        password: Secret("pw".into()),
        ..Provider::default()
    });
    settings.auth.oidc.client_secret = Secret("sso-secret".into());
    store.save(settings).await.expect("save");
    let provider = &store.settings().providers[0];
    assert_eq!(
        (provider.hostname.as_str(), provider.port),
        ("news.eweka.nl", 563)
    );
    assert_eq!(
        provider.backbone.as_deref(),
        Some("eweka"),
        "catalog fills the backbone"
    );

    // The UI round-trips masked settings; secrets must survive and never leak.
    let mut masked = store.settings().masked();
    assert_eq!(masked.providers[0].password, Secret(MASK.into()));
    assert_eq!(masked.auth.oidc.client_secret, Secret(MASK.into()));
    assert_eq!(masked.rclone_rc.password, Secret(MASK.into()));
    assert!(!format!("{:?}", store.settings()).contains("pw"));
    assert!(!format!("{:?}", store.settings()).contains("sso-secret"));
    assert!(!format!("{:?}", store.settings()).contains("rc-env-secret"));
    masked.providers[0].priority = 1;
    store.save(masked.clone()).await.expect("save");
    assert_eq!(store.settings().providers[0].password, Secret("pw".into()));
    assert_eq!(
        store.settings().auth.oidc.client_secret,
        Secret("sso-secret".into())
    );
    assert_eq!(
        store.settings().rclone_rc.password,
        Secret("rc-env-secret".into())
    );
    assert_eq!(store.settings().providers[0].priority, 1);

    let mut renamed = masked.clone();
    renamed.rclone_rc.user = "someone-else".into();
    assert!(matches!(store.save(renamed).await, Err(Error::Locked(p)) if p == "rclone_rc.user"));

    masked.sab.api_key = Secret("changed".into());
    assert!(matches!(store.save(masked).await, Err(Error::Locked(p)) if p == "sab.api_key"));

    let keys: Vec<_> = db
        .config()
        .await
        .expect("query")
        .into_iter()
        .map(|(k, _)| k)
        .collect();
    assert_eq!(
        keys,
        ["auth", "providers"],
        "env values and defaults are not stored"
    );
    let reloaded = ConfigStore::load(db.clone(), []).await.expect("reload");
    assert_eq!(reloaded.settings().sab.api_key, Secret::default());
    assert_eq!(reloaded.settings().providers, store.settings().providers);

    db.close().await;
    for suffix in ["", "-wal", "-shm"] {
        let _ = std::fs::remove_file(format!("{}{suffix}", path.display()));
    }
}

#[tokio::test]
async fn rclone_rc_is_off_until_explicitly_enabled() {
    let path =
        std::env::temp_dir().join(format!("infinidysk-config-{}.sqlite", uuid::Uuid::now_v7()));
    let db = Db::connect(&format!("sqlite:{}", path.display()))
        .await
        .expect("connect");
    // Settings saved before the section existed.
    db.write_config(&[("webdav", r#"{"user":"admin"}"#)], &[])
        .await
        .expect("legacy settings");
    let mut store = ConfigStore::load(db.clone(), []).await.expect("load");
    store.generate_missing_secrets().await.expect("secrets");
    assert_eq!(store.settings().rclone_rc, RcloneRc::default());
    assert!(!store.settings().rclone_rc.enabled);

    let base = store.settings().clone();
    let with = |rc: RcloneRc| {
        let mut settings = base.clone();
        settings.rclone_rc = rc;
        settings
    };
    let on = RcloneRc {
        enabled: true,
        url: " http://127.0.0.1:5572/rc/ ".into(),
        fs: "library:".into(),
        user: "rc".into(),
        password: Secret("rc-secret".into()),
    };

    for url in [
        "https://rc-user:rc-pass@rc.example/",
        "https://rc-user@rc.example/",
        "https://:rc-pass@rc.example/",
        "https://rc.example/?token=tok-123",
        "https://rc.example/#secret-anchor",
        "http://rc.example/",
        "not a url",
    ] {
        let error = store
            .save(with(RcloneRc {
                url: url.into(),
                ..on.clone()
            }))
            .await
            .expect_err(url);
        assert_eq!(error.path(), Some("rclone_rc.url"), "{url}");
        let message = error.to_string();
        for leak in [
            "rc-user",
            "rc-pass",
            "tok-123",
            "secret-anchor",
            "rc.example",
        ] {
            assert!(!message.contains(leak), "{message}");
        }
    }
    let missing = store
        .save(with(RcloneRc {
            url: String::new(),
            ..on.clone()
        }))
        .await
        .expect_err("on needs a URL");
    assert_eq!(missing.path(), Some("rclone_rc.url"));
    let user_only = store
        .save(with(RcloneRc {
            password: Secret::default(),
            ..on.clone()
        }))
        .await
        .expect_err("user without password");
    assert_eq!(user_only.path(), Some("rclone_rc.password"));
    let password_only = store
        .save(with(RcloneRc {
            user: String::new(),
            ..on.clone()
        }))
        .await
        .expect_err("password without user");
    assert_eq!(password_only.path(), Some("rclone_rc.user"));
    // Off, a half-filled form is kept for later.
    store
        .save(with(RcloneRc {
            enabled: false,
            password: Secret::default(),
            ..on.clone()
        }))
        .await
        .expect("off");

    store.save(with(on.clone())).await.expect("on");
    let saved = &store.settings().rclone_rc;
    assert_eq!(saved.url, "http://127.0.0.1:5572/rc/", "URL prefix kept");
    assert_eq!(saved.password, Secret("rc-secret".into()));

    // A masked password follows the same server, never a new one.
    let masked = store.settings().masked();
    let mut moved = masked.clone();
    moved.rclone_rc.url = "https://rc.example/rc/".into();
    let error = store.save(moved).await.expect_err("new origin");
    assert_eq!(error.path(), Some("rclone_rc.password"));
    let mut same = masked;
    same.rclone_rc.url = "http://127.0.0.1:5572/other/".into();
    store.save(same).await.expect("same origin");
    assert_eq!(
        store.settings().rclone_rc.password,
        Secret("rc-secret".into())
    );

    db.close().await;
    for suffix in ["", "-wal", "-shm"] {
        let _ = std::fs::remove_file(format!("{}{suffix}", path.display()));
    }
}

#[tokio::test]
async fn rclone_rc_env_url_never_carries_a_password_to_another_server() {
    let path =
        std::env::temp_dir().join(format!("infinidysk-config-{}.sqlite", uuid::Uuid::now_v7()));
    let db = Db::connect(&format!("sqlite:{}", path.display()))
        .await
        .expect("connect");
    let env_url = |url: &str| ("INFINIDYSK__RCLONE_RC__URL".to_owned(), url.to_owned());
    let env_password = || {
        (
            "INFINIDYSK__RCLONE_RC__PASSWORD".to_owned(),
            "rc-env-secret".to_owned(),
        )
    };
    let on = RcloneRc {
        enabled: true,
        url: "http://127.0.0.1:5572/rc/".into(),
        fs: "library:".into(),
        user: "rc".into(),
        password: Secret("rc-secret".into()),
    };
    let mut store = ConfigStore::load(db.clone(), []).await.expect("load");
    let mut settings = store.settings().clone();
    settings.rclone_rc = on.clone();
    store.save(settings).await.expect("save");

    let error = ConfigStore::load(db.clone(), [env_url("https://rc.example/rc/")])
        .await
        .err()
        .expect("saved password, env URL elsewhere");
    assert_eq!(error.path(), Some("rclone_rc.url"));
    assert!(!error.to_string().contains("rc-secret"));
    let same = ConfigStore::load(db.clone(), [env_url("http://127.0.0.1:5572/other/")])
        .await
        .expect("same origin");
    assert_eq!(same.settings().rclone_rc.password, on.password);
    let both = ConfigStore::load(
        db.clone(),
        [env_url("https://rc.example/rc/"), env_password()],
    )
    .await
    .expect("env password goes with the env URL");
    assert_eq!(
        both.settings().rclone_rc.password,
        Secret("rc-env-secret".into())
    );

    // A password entered under an env URL is stored with that URL.
    let mut cleared = store.settings().clone();
    cleared.rclone_rc.enabled = false;
    cleared.rclone_rc.user = String::new();
    cleared.rclone_rc.password = Secret::default();
    store.save(cleared).await.expect("clear");
    let mut store = ConfigStore::load(db.clone(), [env_url("https://rc.example/rc/")])
        .await
        .expect("no saved password");
    let mut entered = store.settings().masked();
    entered.rclone_rc.user = "rc".into();
    entered.rclone_rc.password = Secret("new-secret".into());
    store.save(entered).await.expect("enter password");
    let reloaded = ConfigStore::load(db.clone(), [env_url("https://rc.example/rc/")])
        .await
        .expect("password belongs to the env URL");
    assert_eq!(
        reloaded.settings().rclone_rc.password,
        Secret("new-secret".into())
    );

    // An env password only follows a URL that env sets.
    db.write_config(&[], &["rclone_rc"]).await.expect("reset");
    let mut store = ConfigStore::load(db.clone(), [env_password()])
        .await
        .expect("env password");
    let mut first = store.settings().masked();
    first.rclone_rc.url = "https://rc.example/rc/".into();
    let error = store
        .save(first)
        .await
        .expect_err("UI URL for env password");
    assert_eq!(error.path(), Some("rclone_rc.url"));
    let mut typed = store.settings().masked();
    typed.rclone_rc.url = "https://rc.example/rc/".into();
    typed.rclone_rc.password = Secret("rc-env-secret".into());
    let error = store
        .save(typed)
        .await
        .expect_err("UI URL for the typed env password");
    assert_eq!(error.path(), Some("rclone_rc.url"));
    let mut same = store.settings().masked();
    same.rclone_rc.fs = "library:".into();
    store.save(same).await.expect("same-origin edit");

    db.close().await;
    for suffix in ["", "-wal", "-shm"] {
        let _ = std::fs::remove_file(format!("{}{suffix}", path.display()));
    }
}

#[tokio::test]
async fn rclone_rc_env_overrides_never_rebind_what_is_saved() {
    let path =
        std::env::temp_dir().join(format!("infinidysk-config-{}.sqlite", uuid::Uuid::now_v7()));
    let db = Db::connect(&format!("sqlite:{}", path.display()))
        .await
        .expect("connect");
    let env = |key: &str, value: &str| (format!("INFINIDYSK__RCLONE_RC__{key}"), value.to_owned());
    let saved = RcloneRc {
        enabled: true,
        url: "http://127.0.0.1:5572/rc/".into(),
        fs: String::new(),
        user: "rc".into(),
        password: Secret("rc-secret".into()),
    };
    let mut store = ConfigStore::load(db.clone(), []).await.expect("load");
    let mut settings = store.settings().clone();
    settings.rclone_rc = saved.clone();
    store.save(settings).await.expect("save");

    // Filling missing secrets at startup enters no RC password, so it keeps the saved URL.
    let mut store = ConfigStore::load(db.clone(), [env("URL", "http://127.0.0.1:5572/other/")])
        .await
        .expect("env URL, same origin");
    assert_eq!(store.settings().sab.api_key.0, "");
    store.generate_missing_secrets().await.expect("secrets");
    let reloaded = ConfigStore::load(db.clone(), []).await.expect("reload");
    assert_ne!(reloaded.settings().sab.api_key.0, "");
    assert_eq!(reloaded.settings().rclone_rc, saved);

    // Env blanks the credentials; a new URL would leave the saved password beside it.
    let mut store = ConfigStore::load(db.clone(), [env("USER", ""), env("PASSWORD", "")])
        .await
        .expect("blanked credentials");
    let mut moved = store.settings().masked();
    assert_eq!(moved.rclone_rc.password, Secret::default());
    moved.rclone_rc.url = "https://rc.example/rc/".into();
    let error = store
        .save(moved)
        .await
        .expect_err("saved password, new URL");
    assert_eq!(error.path(), Some("rclone_rc.url"));
    assert!(!error.to_string().contains("rc-secret"));
    let reloaded = ConfigStore::load(db.clone(), []).await.expect("reload");
    assert_eq!(reloaded.settings().rclone_rc, saved);

    // Entering the env password does not bind the hidden saved one to a new URL.
    let mut store = ConfigStore::load(db.clone(), [env("PASSWORD", "rc-env-secret")])
        .await
        .expect("env password");
    let mut moved = store.settings().masked();
    moved.rclone_rc.url = "https://rc.example/rc/".into();
    moved.rclone_rc.password = Secret("rc-env-secret".into());
    let error = store
        .save(moved)
        .await
        .expect_err("hidden saved password, new URL");
    assert_eq!(error.path(), Some("rclone_rc.url"));
    let reloaded = ConfigStore::load(db.clone(), []).await.expect("reload");
    assert_eq!(reloaded.settings().rclone_rc, saved);

    // An unrelated save under a passwordless env URL stores nothing of it.
    db.write_config(&[], &["rclone_rc"]).await.expect("reset");
    let env_on = [env("ENABLED", "true"), env("URL", "https://rc.example/rc/")];
    let mut store = ConfigStore::load(db.clone(), env_on.clone())
        .await
        .expect("env URL");
    let mut other = store.settings().masked();
    other.sab.manual_category = "manual".into();
    store.save(other).await.expect("unrelated save");
    let reloaded = ConfigStore::load(db.clone(), []).await.expect("reload");
    assert_eq!(reloaded.settings().rclone_rc, RcloneRc::default());
    assert_eq!(reloaded.settings().sab.manual_category, "manual");

    db.close().await;
    for suffix in ["", "-wal", "-shm"] {
        let _ = std::fs::remove_file(format!("{}{suffix}", path.display()));
    }
}

#[tokio::test]
async fn import_sample_percent_defaults_independently_and_rejects_invalid_values() {
    let path =
        std::env::temp_dir().join(format!("infinidysk-config-{}.sqlite", uuid::Uuid::now_v7()));
    let db = Db::connect(&format!("sqlite:{}", path.display()))
        .await
        .expect("connect");
    db.write_config(&[("health", r#"{"check_percent":100}"#)], &[])
        .await
        .expect("legacy settings");
    let store = ConfigStore::load(db.clone(), []).await.expect("load");
    assert_eq!(store.settings().health.check_percent, 100);
    assert_eq!(store.settings().health.import_check_percent, 5);

    assert!(
        serde_json::from_str::<Settings>(
            r#"{"health":{"check_percent":100,"import_check_percent":null}}"#,
        )
        .is_err()
    );
    for percent in [4, 101] {
        let mut settings = store.settings().clone();
        settings.health.import_check_percent = percent;
        let error = settings.validate().expect_err("invalid percentage");
        assert_eq!(error.path(), Some("health.import_check_percent"));
    }

    drop(store);
    db.close().await;
    for suffix in ["", "-wal", "-shm"] {
        let _ = std::fs::remove_file(format!("{}{suffix}", path.display()));
    }
}

#[tokio::test]
async fn a_readarr_instance_stops_loading_with_what_to_do() {
    let path =
        std::env::temp_dir().join(format!("infinidysk-config-{}.sqlite", uuid::Uuid::now_v7()));
    let db = Db::connect(&format!("sqlite:{}", path.display()))
        .await
        .expect("connect");
    let readarr = r#"[{"id":"0192f0a0-0000-7000-8000-000000000001","name":"books","profile":"readarr","url":"http://readarr:8787","api_key":"k"}]"#;
    let stopped = |result: Result<ConfigStore, Error>| match result {
        Err(Error::Invalid { path, message }) => {
            assert_eq!(path, "arr_instances");
            assert!(message.contains("\"books\" is a Readarr"), "{message}");
        }
        other => panic!("loaded: {:?}", other.err()),
    };

    let env = [("INFINIDYSK__ARR_INSTANCES".to_owned(), readarr.to_owned())];
    stopped(ConfigStore::load(db.clone(), env).await);
    db.write_config(&[("arr_instances", readarr)], &[])
        .await
        .expect("write");
    stopped(ConfigStore::load(db.clone(), []).await);
    let cleared = [("INFINIDYSK__ARR_INSTANCES".to_owned(), "[]".to_owned())];
    ConfigStore::load(db.clone(), cleared)
        .await
        .expect("the env list replaces the stored Readarr");

    db.close().await;
    for suffix in ["", "-wal", "-shm"] {
        let _ = std::fs::remove_file(format!("{}{suffix}", path.display()));
    }
}

#[tokio::test]
async fn deployment_inputs_are_strict_but_stored_values_still_load() {
    const SENTINEL: &str = "sentinel-secret-5d1c";
    const P1: &str = "0190f6c4-0000-7000-8000-0000000000a1";
    const P2: &str = "0190f6c4-0000-7000-8000-0000000000a2";
    let path =
        std::env::temp_dir().join(format!("infinidysk-config-{}.sqlite", uuid::Uuid::now_v7()));
    let db = Db::connect(&format!("sqlite:{}", path.display()))
        .await
        .expect("connect");
    let vars = |pairs: &[(&str, &str)]| -> Vec<(String, String)> {
        pairs
            .iter()
            .map(|(k, v)| ((*k).to_owned(), (*v).to_owned()))
            .collect()
    };
    let provider = |id: &str, extra: &str| {
        format!(
            r#"{{"id":"{id}","name":"p","hostname":"news.example","password":"{SENTINEL}"{extra}}}"#
        )
    };
    let arr = |id: &str| {
        format!(
            r#"{{"id":"{id}","name":"tv","profile":"sonarr","url":"http://sonarr:8989","api_key":"{SENTINEL}"}}"#
        )
    };
    let rejected = async |pairs: &[(&str, &str)]| -> String {
        match ConfigStore::load(db.clone(), vars(pairs)).await {
            Ok(_) => panic!("loaded: {pairs:?}"),
            Err(error) => {
                let text = error.to_string();
                assert!(!text.contains(SENTINEL), "{text}");
                error.path().expect("names the setting").to_owned()
            }
        }
    };

    let cases: Vec<(Vec<(&str, String)>, &str)> = vec![
        (
            vec![("INFINIDYSK__QUEUE__CONCURENCY", "3".into())],
            "INFINIDYSK__QUEUE__CONCURENCY",
        ),
        (
            vec![(
                "INFINIDYSK__PROVIDERS",
                format!(
                    "[{}]",
                    provider(P1, r#","quota":{"gb":5,"period":"monthly","resetday":2}"#)
                ),
            )],
            "providers[0].quota.resetday",
        ),
        (
            vec![("INFINIDYSK__QUEUE__CONCURRENCY", "\"three\"".into())],
            "queue.concurrency",
        ),
        (
            vec![("INFINIDYSK__PROVIDERS", format!("[{}", provider(P1, "")))],
            "INFINIDYSK__PROVIDERS",
        ),
        (
            vec![("INFINIDYSK__MAINTENANCE__SCHEDULED", r#"["dust"]"#.into())],
            "maintenance.scheduled[0]",
        ),
        (
            vec![(
                "INFINIDYSK__PROVIDERS",
                format!(
                    r#"[{{"id":null,"name":"p","hostname":"news.example","password":"{SENTINEL}"}}]"#
                ),
            )],
            "providers[0].id",
        ),
        (
            vec![(
                "INFINIDYSK__ARR_INSTANCES",
                format!(
                    r#"[{{"id":"invalid","name":"tv","profile":"sonarr","url":"http://sonarr:8989","api_key":"{SENTINEL}"}}]"#
                ),
            )],
            "arr_instances[0].id",
        ),
        (
            vec![(
                "INFINIDYSK__PROVIDERS",
                format!("[{},{}]", provider(P1, ""), provider(P1, "")),
            )],
            "providers",
        ),
        (
            vec![
                ("INFINIDYSK__QUEUE__CONCURRENCY", "3".into()),
                ("INFINIDYSK__queue__concurrency", "3".into()),
            ],
            "INFINIDYSK__queue__concurrency",
        ),
        (
            vec![
                ("INFINIDYSK__QUEUE", r#"{"concurrency":3}"#.into()),
                ("INFINIDYSK__QUEUE__CONCURRENCY", "3".into()),
            ],
            "INFINIDYSK__QUEUE__CONCURRENCY",
        ),
        (
            vec![
                ("INFINIDYSK__QUEUE__CONCURRENCY", "3".into()),
                ("INFINIDYSK__QUEUE", r#"{"concurrency":3}"#.into()),
            ],
            "INFINIDYSK__QUEUE__CONCURRENCY",
        ),
        (
            vec![("INFINIDYSK__AUTH__API_KEY", String::new())],
            "auth.api_key",
        ),
        (
            vec![("INFINIDYSK__QUEUE__CONCURRENCY", "50".into())],
            "queue.concurrency",
        ),
        (
            vec![("INFINIDYSK__MAINTENANCE__WINDOW__END", "25:00".into())],
            "maintenance.window",
        ),
    ];
    for (pairs, want) in &cases {
        let pairs: Vec<(&str, &str)> = pairs.iter().map(|(k, v)| (*k, v.as_str())).collect();
        assert_eq!(rejected(&pairs).await, *want, "{pairs:?}");
    }
    let Err(Error::Invalid { message, .. }) = ConfigStore::load(
        db.clone(),
        vars(&[("INFINIDYSK__QUEUE__CONCURRENCY", "50")]),
    )
    .await
    else {
        panic!("out-of-range concurrency loaded");
    };
    assert_eq!(message, "Concurrency must be 1-10");

    let lists = vars(&[
        ("INFINIDYSK__PROVIDERS", &format!("[{}]", provider(P2, ""))),
        ("INFINIDYSK__ARR_INSTANCES", &format!("[{}]", arr(P1))),
        ("INFINIDYSK__EXPERIMENTS", r#"["retired-experiment"]"#),
    ]);
    let store = ConfigStore::load(db.clone(), lists)
        .await
        .expect("valid lists");
    assert_eq!(store.settings().providers[0].id.to_string(), P2);
    assert_eq!(store.settings().arr_instances[0].id.to_string(), P1);
    assert_eq!(store.settings().experiments, Vec::<String>::new());

    // Stored values are not deployment input: they load or reset as before.
    db.write_config(
        &[
            ("queue", r#"{"concurrency":50}"#),
            ("experiments", r#"["retired-experiment"]"#),
            ("maintenance", r#"{"window":{"start":"3am","end":"06:00"}}"#),
        ],
        &[],
    )
    .await
    .expect("write");
    let store = ConfigStore::load(db.clone(), []).await.expect("stored");
    assert_eq!(store.settings().queue.concurrency, 2);
    assert_eq!(store.settings().experiments, Vec::<String>::new());

    let window = |store: &ConfigStore| {
        let w = &store.settings().maintenance.window;
        (w.start.clone(), w.end.clone())
    };
    let end = |value: &str| vars(&[("INFINIDYSK__MAINTENANCE__WINDOW__END", value)]);
    let store = ConfigStore::load(db.clone(), end("10:00"))
        .await
        .expect("mixed window");
    assert_eq!(window(&store), ("03:00".into(), "10:00".into()));

    db.write_config(
        &[(
            "maintenance",
            r#"{"window":{"start":"06:00","end":"09:00"}}"#,
        )],
        &[],
    )
    .await
    .expect("write");
    let store = ConfigStore::load(db.clone(), end("12:00"))
        .await
        .expect("stored start");
    assert_eq!(window(&store), ("06:00".into(), "12:00".into()));

    db.write_config(
        &[(
            "maintenance",
            r#"{"window":{"start":"08:00","end":"09:00"}}"#,
        )],
        &[],
    )
    .await
    .expect("write");
    assert_eq!(
        rejected(&[("INFINIDYSK__MAINTENANCE__WINDOW__END", "08:00")]).await,
        "maintenance.window"
    );

    db.close().await;
    for suffix in ["", "-wal", "-shm"] {
        let _ = std::fs::remove_file(format!("{}{suffix}", path.display()));
    }
}

#[tokio::test]
async fn automatic_env_ids_follow_entries_not_positions_or_credentials() {
    use serde_json::{Value, json};
    let db = Db::connect("sqlite::memory:").await.unwrap();
    let load = async |providers: Value, arrs: Value| {
        ConfigStore::load(
            db.clone(),
            [
                ("INFINIDYSK__PROVIDERS".into(), providers.to_string()),
                ("INFINIDYSK__ARR_INSTANCES".into(), arrs.to_string()),
            ],
        )
        .await
    };
    let named = json!({"name":"tv","profile":"eweka","password":"old"});
    let catalog = json!({"profile":"eweka"});
    let custom = json!({"hostname":"NEWS.example"});
    let tv = json!({"name":"tv","profile":"sonarr","url":"http://tv","api_key":"old"});
    let other = json!({"name":"other","profile":"sonarr","url":"http://other","api_key":"other"});
    let first = load(json!([named, catalog, custom]), json!([tv, other]))
        .await
        .unwrap();
    let mut renamed_host = custom;
    renamed_host["hostname"] = json!("news.example");
    let mut rotated = named.clone();
    rotated["password"] = json!("new");
    let mut moved = tv.clone();
    moved["url"] = json!("https://new-host");
    moved["api_key"] = json!("new");
    let second = load(
        json!([renamed_host, rotated, catalog]),
        json!([other, moved]),
    )
    .await
    .unwrap();
    let a = first.settings();
    let b = second.settings();
    assert_eq!(a.providers[0].id, b.providers[1].id);
    assert_eq!(a.providers[1].id, b.providers[2].id);
    assert_eq!(a.providers[2].id, b.providers[0].id);
    assert_eq!(a.arr_instances[0].id, b.arr_instances[1].id);
    assert_eq!(a.arr_instances[1].id, b.arr_instances[0].id);
    assert_ne!(a.providers[0].id, a.arr_instances[0].id);
    // Pin the public identity across releases, not just across two loads of this build.
    assert_eq!(
        a.arr_instances[0].id,
        uuid::uuid!("db8c5954-c7fc-56f3-978c-a34c39bafadf")
    );
    assert_eq!(
        a.providers[1].id,
        uuid::uuid!("f1a09c54-fdab-5582-a477-2c9ffcb405df")
    );
    for (providers, arrs, path) in [
        (json!([named, named]), json!([]), "providers"),
        (json!([]), json!([tv, tv]), "arr_instances"),
    ] {
        assert!(matches!(load(providers, arrs).await, Err(e) if e.path() == Some(path)));
    }
}

#[tokio::test]
async fn file_overrides_round_trip_without_becoming_stored_settings() {
    const SENTINEL: &str = "sentinel-secret-9b2e";
    let dir = std::env::temp_dir().join(format!("infinidysk-files-{}", uuid::Uuid::now_v7()));
    std::fs::create_dir(&dir).expect("dir");
    let file = |name: &str, text: &str| {
        let path = dir.join(name);
        std::fs::write(&path, text).expect("write");
        path.display().to_string()
    };
    let db = Db::connect(&format!("sqlite:{}", dir.join("db.sqlite").display()))
        .await
        .expect("connect");

    let mut store = ConfigStore::load(db.clone(), []).await.expect("load");
    let mut settings = store.settings().clone();
    settings.sab.api_key = Secret("stored-sab-key".into());
    settings.providers.push(Provider {
        profile: Some("eweka".into()),
        password: Secret("stored-pw".into()),
        ..Provider::default()
    });
    store.save(settings).await.expect("save");
    let stored = db.config().await.expect("query");

    let providers = file(
        "providers.json",
        &format!(
            "[\n  {{\"id\":\"0190f6c4-0000-7000-8000-0000000000b1\",\"name\":\"p\",\"hostname\":\"news.example\",\"password\":\"{SENTINEL}\"}}\n]\n"
        ),
    );
    let arrs = file(
        "arrs.json",
        &format!(
            r#"[{{"id":"0190f6c4-0000-7000-8000-0000000000b2","name":"tv","profile":"sonarr","url":"http://sonarr:8989","api_key":"{SENTINEL}"}}]"#
        ),
    );
    let env = vec![
        (
            "INFINIDYSK__SAB__API_KEY_FILE".to_owned(),
            file("sab", "0123\r\n"),
        ),
        ("INFINIDYSK__PROVIDERS_FILE".to_owned(), providers.clone()),
        ("INFINIDYSK__ARR_INSTANCES_file".to_owned(), arrs),
    ];
    let mut store = ConfigStore::load(db.clone(), env).await.expect("files");
    assert_eq!(store.settings().sab.api_key, Secret("0123".into()));
    assert_eq!(
        store.settings().providers[0].password,
        Secret(SENTINEL.into())
    );
    assert_eq!(
        store.settings().arr_instances[0].api_key,
        Secret(SENTINEL.into())
    );
    let mut locked: Vec<_> = store.locked().collect();
    locked.sort();
    assert_eq!(locked, ["arr_instances", "providers", "sab.api_key"]);

    let mut settings = store.settings().clone();
    settings.sab.api_key = Secret(MASK.into());
    settings.queue.concurrency = 3;
    store.save(settings).await.expect("unrelated save");
    let after = db.config().await.expect("query");
    let text = format!("{after:?}");
    assert!(!text.contains(SENTINEL) && !text.contains("0123"), "{text}");
    for (key, value) in &stored {
        let kept = after.iter().find(|(k, _)| k == key).map(|(_, v)| v);
        assert_eq!(kept, Some(value), "{key} kept");
    }

    let store = ConfigStore::load(db.clone(), []).await.expect("reload");
    assert_eq!(
        store.settings().sab.api_key,
        Secret("stored-sab-key".into())
    );
    assert_eq!(
        store.settings().providers[0].password,
        Secret("stored-pw".into())
    );
    assert_eq!(store.settings().queue.concurrency, 3);

    let rejected = async |env: Vec<(&str, String)>| -> String {
        let env = env.into_iter().map(|(k, v)| (k.to_owned(), v));
        match ConfigStore::load(db.clone(), env).await {
            Ok(_) => panic!("loaded"),
            Err(error) => {
                let text = error.to_string();
                assert!(!text.contains(SENTINEL), "{text}");
                error.path().expect("names the input").to_owned()
            }
        }
    };
    assert_eq!(
        rejected(vec![
            ("INFINIDYSK__PROVIDERS", String::new()),
            ("INFINIDYSK__PROVIDERS_FILE", providers),
        ])
        .await,
        "INFINIDYSK__PROVIDERS_FILE"
    );
    assert_eq!(
        rejected(vec![(
            "INFINIDYSK__SAB__API_KEY_FILE",
            dir.join("absent").display().to_string()
        )])
        .await,
        "INFINIDYSK__SAB__API_KEY_FILE"
    );
    assert_eq!(
        rejected(vec![(
            "INFINIDYSK__PROVIDERS_FILE",
            file("broken.json", &format!("[{{\"password\":\"{SENTINEL}\""))
        )])
        .await,
        "INFINIDYSK__PROVIDERS_FILE"
    );
    assert_eq!(
        rejected(vec![(
            "INFINIDYSK__NETWORK__PUBLIC_URL_FILE",
            file("url", &format!("https://media.example/?token={SENTINEL}\n"))
        )])
        .await,
        "network.public_url"
    );

    db.close().await;
    let _ = std::fs::remove_dir_all(&dir);
}

#[tokio::test]
async fn out_of_range_stored_values_load_as_defaults_but_never_save() {
    let path =
        std::env::temp_dir().join(format!("infinidysk-config-{}.sqlite", uuid::Uuid::now_v7()));
    let db = Db::connect(&format!("sqlite:{}", path.display()))
        .await
        .expect("connect");
    let mut store = ConfigStore::load(db.clone(), []).await.expect("load");
    let mut settings = store.settings().clone();
    settings.providers.push(Provider {
        profile: Some("eweka".into()),
        password: Secret("pw".into()),
        ..Provider::default()
    });
    store.save(settings).await.expect("save");

    // As an older build or a hand edit could have stored them.
    let (_, raw) = db
        .config()
        .await
        .expect("query")
        .into_iter()
        .find(|(k, _)| k == "providers")
        .expect("providers stored");
    let mut providers: serde_json::Value = serde_json::from_str(&raw).expect("json");
    providers[0]["pipeline_depth"] = 99.into();
    db.write_config(
        &[
            ("providers", &providers.to_string()),
            ("sab", r#"{"manual_category":" "}"#),
        ],
        &[],
    )
    .await
    .expect("write");

    let mut store = ConfigStore::load(db.clone(), []).await.expect("load");
    assert_eq!(store.settings().providers[0].pipeline_depth, 4);
    assert_eq!(store.settings().sab.manual_category, "uncategorized");

    let mut blank = store.settings().clone();
    blank.sab.manual_category = String::new();
    assert!(matches!(
        store.save(blank).await,
        Err(Error::Invalid { .. })
    ));

    db.close().await;
    for suffix in ["", "-wal", "-shm"] {
        let _ = std::fs::remove_file(format!("{}{suffix}", path.display()));
    }
}

#[test]
fn library_dirs_stored_as_plain_paths_still_load() {
    let dirs: Vec<LibraryDir> =
        serde_json::from_str(r#"["/old", {"path": "/new", "arr_path": "/arr"}]"#).expect("parse");
    assert_eq!(
        dirs,
        [
            LibraryDir {
                path: "/old".into(),
                arr_path: String::new()
            },
            LibraryDir {
                path: "/new".into(),
                arr_path: "/arr".into()
            },
        ]
    );
}

#[test]
fn idle_memory_trim_is_opt_in_and_survives_a_settings_round_trip() {
    let enabled = |settings: &Settings| {
        settings
            .experiments
            .iter()
            .any(|name| name == "idle-memory-trim")
    };
    assert!(!enabled(&Settings::default()));
    assert!(!enabled(&serde_json::from_str("{}").expect("old settings")));
    let settings: Settings =
        serde_json::from_str(r#"{"experiments":["idle-memory-trim"]}"#).expect("opt in");
    let saved = serde_json::to_vec(&settings).expect("save");
    assert!(enabled(&serde_json::from_slice(&saved).expect("reload")));
}

#[test]
fn retired_experiments_are_dropped_on_load() {
    let settings: Settings =
        serde_json::from_str(r#"{"experiments": ["retired", "win-floor", "win-floor"]}"#)
            .expect("parse");
    assert_eq!(settings.experiments, ["win-floor"]);
}

#[test]
fn sso_settings_are_checked_only_while_an_issuer_is_set() {
    let sso = |issuer: &str| {
        let mut s = Settings::default();
        s.auth.oidc.issuer = issuer.to_owned();
        s.auth.oidc.client_id = "dysk".to_owned();
        s.auth.oidc.client_secret = Secret("secret".into());
        s
    };
    let path = |mut s: Settings| s.validate().err().and_then(|e| e.path().map(str::to_owned));

    assert_eq!(path(sso("")), None);
    let authentik = "https://auth.example.com/application/o/dysk/";
    let mut no_secret = sso(authentik);
    no_secret.auth.oidc.client_secret = Secret::default();
    assert_eq!(path(no_secret).as_deref(), Some("auth.oidc.client_secret"));
    let mut ok = sso(authentik);
    ok.validate().expect("valid");
    assert_eq!(ok.auth.oidc.issuer, authentik, "the trailing slash stays");

    for issuer in ["http://auth.example.com", "http://10.0.0.5"] {
        assert_eq!(
            path(sso(issuer)).as_deref(),
            Some("auth.oidc.issuer"),
            "{issuer}"
        );
    }
    for issuer in [
        "http://127.0.0.1:9000",
        "http://localhost:9000",
        "http://[::1]:9000",
    ] {
        assert_eq!(path(sso(issuer)), None, "{issuer}");
    }

    let mut scopes = sso(authentik);
    scopes.auth.oidc.scopes = "  profile   email ".to_owned();
    scopes.validate().expect("valid");
    assert_eq!(scopes.auth.oidc.scopes, "profile email");

    let mut name = sso(authentik);
    name.auth.oidc.display_name = "  Acme   Account ".to_owned();
    name.validate().expect("valid");
    assert_eq!(name.auth.oidc.display_name(), "Acme Account");
    assert_eq!(sso(authentik).auth.oidc.display_name(), "SSO");
    for bad in ["a".repeat(41), "Acme\u{7}".to_owned()] {
        let mut s = sso(authentik);
        s.auth.oidc.display_name = bad;
        assert_eq!(path(s).as_deref(), Some("auth.oidc.display_name"));
    }

    for (uri, want) in [
        ("https://dysk.example.com/sso/cb", None),
        (
            "https://dysk.example.com/cb#x",
            Some("auth.oidc.redirect_uri"),
        ),
        ("ftp://dysk.example.com/cb", Some("auth.oidc.redirect_uri")),
    ] {
        let mut s = sso(authentik);
        s.auth.oidc.redirect_uri = uri.to_owned();
        assert_eq!(path(s).as_deref(), want, "{uri}");
    }
}

#[test]
fn password_login_stays_on_while_sso_is_off() {
    let mut s = Settings::default();
    s.auth.local_login = false;
    assert!(s.auth.password_login());
    s.auth.oidc.issuer = "https://auth.example.com".to_owned();
    s.auth.oidc.client_id = "dysk".to_owned();
    s.auth.oidc.client_secret = Secret("secret".into());
    s.validate().expect("valid");
    assert!(!s.auth.password_login());
}

#[test]
fn validation_rejects_unknown_catalog_entries_and_bad_arr_urls() {
    let custom = |backbone: Option<&str>| Settings {
        providers: vec![Provider {
            hostname: "news.example.com".into(),
            backbone: backbone.map(Into::into),
            ..Provider::default()
        }],
        ..Settings::default()
    };
    assert!(
        custom(None).validate().is_ok(),
        "a custom provider may be its own group"
    );
    assert!(custom(Some("eweka")).validate().is_ok());
    assert!(matches!(
        custom(Some("mine")).validate(),
        Err(Error::Invalid { .. })
    ));

    let mut wrong_host = Settings {
        providers: vec![Provider {
            profile: Some("eweka".into()),
            hostname: "news.example.com".into(),
            ..Provider::default()
        }],
        ..Settings::default()
    };
    assert!(matches!(wrong_host.validate(), Err(Error::Invalid { .. })));

    let old: Provider = serde_json::from_str(r#"{"hostname":"news.example.com"}"#).expect("json");
    assert_eq!(old.pipeline_depth, 4, "saved providers keep the old depth");
    for (depth, ok) in [(0, false), (1, true), (16, true), (17, false)] {
        let mut settings = custom(None);
        settings.providers[0].pipeline_depth = depth;
        assert_eq!(settings.validate().is_ok(), ok, "depth {depth}");
    }

    let mut arr = Settings {
        arr_instances: vec![ArrInstance {
            id: uuid::Uuid::now_v7(),
            name: "tv".into(),
            profile: ArrProfile::Sonarr,
            url: "ftp://arr".into(),
            api_key: Secret("k".into()),
            enabled: true,
        }],
        ..Settings::default()
    };
    assert!(matches!(arr.validate(), Err(Error::Invalid { .. })));
    arr.arr_instances[0].url = "http://sonarr:8989".into();
    assert!(arr.validate().is_ok());

    let mut network = Settings::default();
    network.network.trusted_proxies = vec![" 172.18.0.0/16 ".into(), String::new(), "::1".into()];
    assert!(network.validate().is_ok());
    assert_eq!(network.network.trusted_proxies, ["172.18.0.0/16", "::1"]);
    network.network.trusted_proxies.push("proxy.lan".into());
    assert!(matches!(network.validate(), Err(Error::Invalid { .. })));

    let mut strm = Settings::default();
    strm.imports.strategy = infinidysk::config::ImportStrategy::Strm;
    assert!(
        matches!(strm.validate(), Err(Error::Invalid { .. })),
        "STRM links need a public URL"
    );
    strm.network.public_url = "https://media.example/".into();
    assert!(strm.validate().is_ok());
    let old: Settings = serde_json::from_str("{}").expect("json");
    assert!(
        old.network
            .trusted_proxies
            .contains(&"192.168.0.0/16".to_owned())
    );

    let mut public = Settings::default();
    public.network.public_url = " https://media.example.com/infinidysk/ ".into();
    assert!(public.validate().is_ok());
    assert_eq!(
        public.network.public_url,
        "https://media.example.com/infinidysk"
    );
    public.network.public_url = "media.example.com".into();
    assert!(matches!(public.validate(), Err(Error::Invalid { .. })));
}
