//! The admin login lifecycle: setup, sessions, password change and the admin API key.
#![allow(clippy::unwrap_used)] // Test helpers outside `#[test]` fns; panics are the assertions.

use std::net::SocketAddr;
use std::sync::Arc;

use axum::Router;
use axum::body::Body;
use axum::extract::ConnectInfo;
use axum::http::{Request, StatusCode, header};
use http_body_util::BodyExt;
use infinidysk::auth::{Auth, router};
use infinidysk::config::ConfigStore;
use infinidysk_core::ItemId;
use infinidysk_db::Db;
use serde_json::Value;
use tokio::sync::RwLock;
use tower::ServiceExt;

struct Reply {
    code: StatusCode,
    cookie: Option<String>,
    body: Value,
}

async fn send(
    app: &Router,
    method: &str,
    uri: &str,
    auth: Option<(&str, &str)>,
    body: &str,
) -> Reply {
    let mut req = Request::builder()
        .method(method)
        .uri(uri)
        .header(header::CONTENT_TYPE, "application/json");
    if let Some((name, value)) = auth {
        req = req.header(name, value);
    }
    let res = app
        .clone()
        .oneshot(req.body(Body::from(body.to_owned())).unwrap())
        .await
        .unwrap();
    let code = res.status();
    let cookie = res
        .headers()
        .get(header::SET_COOKIE)
        .map(|v| v.to_str().unwrap().split(';').next().unwrap().to_owned());
    let bytes = res.into_body().collect().await.unwrap().to_bytes();
    let body = serde_json::from_slice(&bytes).unwrap_or(Value::Null);
    Reply { code, cookie, body }
}

#[tokio::test]
async fn setup_login_password_change_and_api_key() {
    let path = std::env::temp_dir().join(format!("infinidysk-auth-{}.sqlite", ItemId::new()));
    let db = Db::connect(&format!("sqlite:{}", path.display()))
        .await
        .unwrap();
    let env = [
        (
            "INFINIDYSK__AUTH__API_KEY".to_owned(),
            "adminkey".to_owned(),
        ),
        (
            "INFINIDYSK__AUTH__SESSION_KEY".to_owned(),
            "signing".to_owned(),
        ),
    ];
    let config = Arc::new(RwLock::new(
        ConfigStore::load(db.clone(), env).await.unwrap(),
    ));
    let app = router(Arc::new(Auth::load(db.clone(), config).await.unwrap()));
    let change = r#"{"current_password":"password1","password":"password2"}"#;

    let r = send(&app, "GET", "/api/v1/auth", None, "").await;
    assert_eq!(
        (r.body["setup_required"].as_bool(), r.body["user"].is_null()),
        (Some(true), true)
    );
    let r = send(&app, "PUT", "/api/v1/auth/password", None, change).await;
    assert_eq!(r.code, StatusCode::UNAUTHORIZED);
    let short = r#"{"username":"admin","password":"short"}"#;
    assert_eq!(
        send(&app, "POST", "/api/v1/auth/setup", None, short)
            .await
            .code,
        StatusCode::BAD_REQUEST
    );

    let first = r#"{"username":"admin","password":"password1"}"#;
    let r = send(&app, "POST", "/api/v1/auth/setup", None, first).await;
    assert_eq!(r.code, StatusCode::NO_CONTENT);
    let session = r.cookie.unwrap();
    let r = send(&app, "POST", "/api/v1/auth/setup", None, first).await;
    assert_eq!(r.code, StatusCode::CONFLICT);
    let r = send(&app, "GET", "/api/v1/auth", Some(("cookie", &session)), "").await;
    assert_eq!(r.body["user"], "admin");

    let wrong = r#"{"username":"other","password":"password1"}"#;
    let r = send(&app, "POST", "/api/v1/auth/login", None, wrong).await;
    assert_eq!(r.code, StatusCode::UNAUTHORIZED);
    assert_eq!(r.body["error"], "wrong username or password");

    let r = send(
        &app,
        "PUT",
        "/api/v1/auth/password",
        Some(("cookie", &session)),
        change,
    )
    .await;
    assert_eq!(r.code, StatusCode::NO_CONTENT);
    let renewed = r.cookie.unwrap();
    // The old cookie was signed over the old password hash.
    let r = send(&app, "GET", "/api/v1/auth", Some(("cookie", &session)), "").await;
    assert!(r.body["user"].is_null());
    let r = send(&app, "GET", "/api/v1/auth", Some(("cookie", &renewed)), "").await;
    assert_eq!(r.body["user"], "admin");

    let old = send(&app, "POST", "/api/v1/auth/login", None, first).await;
    assert_eq!(old.code, StatusCode::UNAUTHORIZED);
    let new = r#"{"username":"admin","password":"password2"}"#;
    let r = send(&app, "POST", "/api/v1/auth/login", None, new).await;
    assert!(r.code == StatusCode::NO_CONTENT && r.cookie.is_some());

    let back = r#"{"current_password":"password2","password":"password1"}"#;
    let r = send(
        &app,
        "PUT",
        "/api/v1/auth/password",
        Some(("x-api-key", "nope")),
        back,
    )
    .await;
    assert_eq!(r.code, StatusCode::UNAUTHORIZED);
    let r = send(
        &app,
        "PUT",
        "/api/v1/auth/password",
        Some(("x-api-key", "adminkey")),
        back,
    )
    .await;
    assert_eq!(r.code, StatusCode::NO_CONTENT);
    // Survives a restart.
    let config = Arc::new(RwLock::new(
        ConfigStore::load(db.clone(), []).await.unwrap(),
    ));
    let app = router(Arc::new(Auth::load(db, config).await.unwrap()));
    assert_eq!(
        send(&app, "POST", "/api/v1/auth/login", None, first)
            .await
            .code,
        StatusCode::NO_CONTENT
    );
}

#[tokio::test]
async fn a_csharp_admin_password_signs_in_once_then_is_rehashed() {
    let path = std::env::temp_dir().join(format!("infinidysk-auth-{}.sqlite", ItemId::new()));
    let db = Db::connect(&format!("sqlite:{}", path.display()))
        .await
        .unwrap();
    // C# PBKDF2-HMAC-SHA256 of "hunter2!" + salt "pepper".
    let csharp =
        "AQAAAAEAAAPoAAAAEAABAgMEBQYHCAkKCwwNDg9jYQWw1WgkjgmoogcR/ruPfjli4Hi9Uv0XM22X8Y5naw==";
    db.create_admin("admin", &infinidysk_import::legacy_hash(csharp, "pepper"))
        .await
        .unwrap();
    let config = Arc::new(RwLock::new(
        ConfigStore::load(db.clone(), []).await.unwrap(),
    ));
    let app = router(Arc::new(Auth::load(db.clone(), config).await.unwrap()));
    let login = r#"{"username":"admin","password":"hunter2!"}"#;
    let r = send(&app, "POST", "/api/v1/auth/login", None, login).await;
    assert!(r.code == StatusCode::NO_CONTENT && r.cookie.is_some());
    let (_, hash) = db.admin().await.unwrap().unwrap();
    assert!(hash.starts_with("$argon2"), "replaced by our own hash");
    // C# lowercased usernames on sign-in, so users may type any case.
    let login = r#"{"username":"Admin","password":"hunter2!"}"#;
    let r = send(&app, "POST", "/api/v1/auth/login", None, login).await;
    assert_eq!(r.code, StatusCode::NO_CONTENT);
}

#[tokio::test]
async fn auth_mode_exempts_clients() {
    let app = |mode: &str| {
        let env = [("INFINIDYSK__AUTH__MODE".to_owned(), mode.to_owned())];
        async move {
            let path =
                std::env::temp_dir().join(format!("infinidysk-auth-{}.sqlite", ItemId::new()));
            let db = Db::connect(&format!("sqlite:{}", path.display()))
                .await
                .unwrap();
            let config = Arc::new(RwLock::new(
                ConfigStore::load(db.clone(), env).await.unwrap(),
            ));
            router(Arc::new(Auth::load(db, config).await.unwrap()))
        }
    };
    let required = |app: Router,
                    peer: Option<&'static str>,
                    header: Option<(&'static str, &'static str)>| async move {
        let mut req = Request::builder().uri("/api/v1/auth");
        if let Some(peer) = peer {
            req = req.extension(ConnectInfo(SocketAddr::new(peer.parse().unwrap(), 1)));
        }
        if let Some((name, value)) = header {
            req = req.header(name, value);
        }
        let res = app.oneshot(req.body(Body::empty()).unwrap()).await.unwrap();
        let bytes = res.into_body().collect().await.unwrap().to_bytes();
        serde_json::from_slice::<Value>(&bytes).unwrap()["required"].as_bool()
    };

    let disabled = app("disabled").await;
    assert_eq!(required(disabled.clone(), None, None).await, Some(false));
    let first = r#"{"username":"admin","password":"password1"}"#;
    send(&disabled, "POST", "/api/v1/auth/setup", None, first).await;
    // Past the middleware: only the wrong current password is refused.
    let change = r#"{"current_password":"wrong","password":"password2"}"#;
    let r = send(&disabled, "PUT", "/api/v1/auth/password", None, change).await;
    assert_eq!(r.code, StatusCode::BAD_REQUEST);

    let local = app("local").await;
    for (peer, header, want) in [
        (Some("127.0.0.1"), None, false),
        (Some("192.168.1.5"), None, false),
        (Some("::ffff:10.1.2.3"), None, false),
        (Some("203.0.113.9"), None, true),
        (
            Some("127.0.0.1"),
            Some(("x-forwarded-for", "192.168.1.5")),
            false,
        ),
        // A local reverse proxy forwarding a remote client.
        (
            Some("127.0.0.1"),
            Some(("x-forwarded-for", "203.0.113.9")),
            true,
        ),
        (Some("127.0.0.1"), Some(("x-real-ip", "127.0.0.1")), true),
        // Only local proxies are trusted by default.
        (
            Some("203.0.113.9"),
            Some(("x-forwarded-for", "127.0.0.1")),
            true,
        ),
        (None, None, true),
    ] {
        assert_eq!(
            required(local.clone(), peer, header).await,
            Some(want),
            "{peer:?} {header:?}"
        );
    }
    assert_eq!(
        required(app("required").await, Some("127.0.0.1"), None).await,
        Some(true)
    );
}
