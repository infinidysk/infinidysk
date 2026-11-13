use infinidysk::config::{
    ArrInstance, ArrProfile, ConfigStore, Error, LibraryDir, MASK, Provider, Secret, Settings,
};
use infinidysk_db::Db;

#[tokio::test]
async fn par2_limits_persist_validate_and_respect_environment_locks() {
    let db = Db::connect("sqlite::memory:").await.unwrap();
    let mut store = ConfigStore::load(db.clone(), []).await.unwrap();
    let mut settings = store.settings().clone();
    settings.repair.memory_mb = 512;
    settings.repair.transfer_gb = 200;
    settings.repair.storage_gb = 50;
    settings.repair.timeout_minutes = 120;
    settings.repair.attempts = 5;
    settings.repair.check_concurrency = 128;
    store.save(settings.clone()).await.unwrap();
    let reloaded = ConfigStore::load(db.clone(), []).await.unwrap();
    assert_eq!(reloaded.settings().repair, settings.repair);
    for (key, value) in [
        ("memory_mb", 95),
        ("memory_mb", 65537),
        ("transfer_gb", 0),
        ("storage_gb", 0),
        ("timeout_minutes", 0),
        ("attempts", 0),
        ("attempts", 11),
        ("check_concurrency", 0),
        ("check_concurrency", 257),
    ] {
        let mut invalid = serde_json::to_value(&settings).unwrap();
        invalid["repair"][key] = value.into();
        let error = store
            .save(serde_json::from_value(invalid).unwrap())
            .await
            .unwrap_err();
        assert_eq!(error.path(), Some(format!("repair.{key}").as_str()));
    }
    let mut locked =
        ConfigStore::load(db, [("INFINIDYSK__REPAIR__STORAGE_GB".into(), "75".into())])
            .await
            .unwrap();
    assert_eq!(locked.settings().repair.storage_gb, 75);
    let mut change = locked.settings().clone();
    change.repair.storage_gb = 100;
    assert!(matches!(locked.save(change).await, Err(Error::Locked(p)) if p == "repair.storage_gb"));
}

#[tokio::test]
async fn env_overrides_win_are_never_stored_and_masked_secrets_survive_saves() {
    let path =
        std::env::temp_dir().join(format!("infinidysk-config-{}.sqlite", uuid::Uuid::now_v7()));
    let db = Db::connect(&format!("sqlite:{}", path.display()))
        .await
        .expect("connect");
    let env = [
        ("INFINIDYSK__SAB__API_KEY".to_owned(), "123".to_owned()),
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
    assert_eq!(store.locked().collect::<Vec<_>>(), ["sab.api_key"]);

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
    assert!(!format!("{:?}", store.settings()).contains("pw"));
    assert!(!format!("{:?}", store.settings()).contains("sso-secret"));
    masked.providers[0].priority = 1;
    store.save(masked.clone()).await.expect("save");
    assert_eq!(store.settings().providers[0].password, Secret("pw".into()));
    assert_eq!(
        store.settings().auth.oidc.client_secret,
        Secret("sso-secret".into())
    );
    assert_eq!(store.settings().providers[0].priority, 1);

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
