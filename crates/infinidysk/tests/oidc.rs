//! Single sign-on against a fake OIDC provider on loopback.
#![allow(clippy::unwrap_used)] // Test helpers outside `#[test]` fns; panics are the assertions.

use std::collections::HashMap;
use std::net::SocketAddr;
use std::sync::atomic::{AtomicBool, AtomicUsize, Ordering};
use std::sync::{Arc, Mutex};
use std::time::{SystemTime, UNIX_EPOCH};

use axum::body::Body;
use axum::extract::{ConnectInfo, Form, State};
use axum::http::{HeaderMap, Request, StatusCode, header};
use axum::response::{IntoResponse, Response};
use axum::routing::{get, post};
use axum::{Json, Router};
use base64::Engine as _;
use base64::engine::general_purpose::{STANDARD, URL_SAFE_NO_PAD};
use hmac::{Hmac, KeyInit as _, Mac as _};
use http_body_util::BodyExt;
use infinidysk::auth::{Auth, require, router};
use infinidysk::config::{ConfigStore, Secret, Settings};
use infinidysk_core::ItemId;
use infinidysk_db::Db;
use reqwest::Url;
use serde_json::{Value, json};
use sha2::{Digest as _, Sha256};
use tokio::sync::{Notify, RwLock, watch};
use tower::ServiceExt;

const CLIENT: &str = "dysk-client";
const SECRET: &str = "secret";
const SETUP: &str = r#"{"username":"admin","password":"password1"}"#;

/// The fake identity provider.
struct Idp {
    base: String,
    /// `code → (nonce, code_challenge)`, filled by the test playing `/authorize`.
    codes: Mutex<HashMap<String, (String, String)>>,
    discoveries: AtomicUsize,
    token_requests: AtomicUsize,
    /// Signs ID tokens; the client secret unless a test wants a bad signature.
    key: String,
    fail_discovery: AtomicBool,
    /// Advertises and accepts only `client_secret_basic`.
    basic_only: AtomicBool,
    /// Leaves `token_endpoint_auth_methods_supported` out, which means Basic.
    no_methods: AtomicBool,
    /// Issues ID tokens that expired an hour ago.
    expired: AtomicBool,
    authorization_endpoint: Option<String>,
    arrived: Notify,
    gate: watch::Sender<bool>,
}

async fn idp(key: &str, authorization_endpoint: Option<&str>) -> Arc<Idp> {
    let listener = tokio::net::TcpListener::bind("127.0.0.1:0").await.unwrap();
    let idp = Arc::new(Idp {
        base: format!("http://{}", listener.local_addr().unwrap()),
        codes: Mutex::default(),
        discoveries: AtomicUsize::new(0),
        token_requests: AtomicUsize::new(0),
        key: key.to_owned(),
        fail_discovery: AtomicBool::new(false),
        basic_only: AtomicBool::new(false),
        no_methods: AtomicBool::new(false),
        expired: AtomicBool::new(false),
        authorization_endpoint: authorization_endpoint.map(str::to_owned),
        arrived: Notify::new(),
        gate: watch::Sender::new(true),
    });
    let app = Router::new()
        .route("/.well-known/openid-configuration", get(discovery))
        .route("/jwks", get(|| async { Json(json!({ "keys": [] })) }))
        .route("/token", post(token))
        .with_state(Arc::clone(&idp));
    tokio::spawn(async move { axum::serve(listener, app).await });
    idp
}

async fn discovery(State(idp): State<Arc<Idp>>) -> Response {
    idp.discoveries.fetch_add(1, Ordering::SeqCst);
    idp.arrived.notify_one();
    let _ = idp.gate.subscribe().wait_for(|open| *open).await;
    if idp.fail_discovery.load(Ordering::SeqCst) {
        return StatusCode::INTERNAL_SERVER_ERROR.into_response();
    }
    let base = &idp.base;
    let methods = if idp.no_methods.load(Ordering::SeqCst) {
        Value::Null
    } else if idp.basic_only.load(Ordering::SeqCst) {
        json!(["client_secret_basic"])
    } else {
        json!(["client_secret_basic", "client_secret_post"])
    };
    Json(json!({
        "issuer": base,
        "token_endpoint_auth_methods_supported": methods,
        "authorization_endpoint": idp
            .authorization_endpoint
            .clone()
            .unwrap_or_else(|| format!("{base}/authorize")),
        "token_endpoint": format!("{base}/token"),
        "jwks_uri": format!("{base}/jwks"),
        "response_types_supported": ["code"],
        "subject_types_supported": ["public"],
        "id_token_signing_alg_values_supported": ["HS256"],
    }))
    .into_response()
}

fn now() -> u64 {
    SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .unwrap()
        .as_secs()
}

fn b64(bytes: &[u8]) -> String {
    URL_SAFE_NO_PAD.encode(bytes)
}

async fn token(
    State(idp): State<Arc<Idp>>,
    headers: HeaderMap,
    Form(form): Form<HashMap<String, String>>,
) -> Response {
    idp.token_requests.fetch_add(1, Ordering::SeqCst);
    let field = |k: &str| form.get(k).map_or("", String::as_str);
    assert_eq!(field("grant_type"), "authorization_code");
    let basic = headers
        .get(header::AUTHORIZATION)
        .map(|v| v.to_str().unwrap().to_owned());
    if idp.basic_only.load(Ordering::SeqCst) || idp.no_methods.load(Ordering::SeqCst) {
        let expected = format!("Basic {}", STANDARD.encode(format!("{CLIENT}:{SECRET}")));
        assert_eq!(
            basic.as_deref(),
            Some(expected.as_str()),
            "client_secret_basic"
        );
        assert_eq!(field("client_secret"), "");
    } else {
        assert_eq!(basic, None);
        assert_eq!(field("client_id"), CLIENT);
        assert_eq!(field("client_secret"), SECRET, "client_secret_post");
    }
    assert_eq!(field("redirect_uri"), "http://dysk.test/auth/oidc/callback");
    let (nonce, challenge) = idp
        .codes
        .lock()
        .unwrap()
        .get(field("code"))
        .cloned()
        .expect("a code the provider issued");
    assert_eq!(
        b64(&Sha256::digest(field("code_verifier").as_bytes())),
        challenge,
        "PKCE"
    );
    let header = b64(br#"{"alg":"HS256","typ":"JWT"}"#);
    let issued = if idp.expired.load(Ordering::SeqCst) {
        now() - 7200
    } else {
        now()
    };
    let claims = b64(&serde_json::to_vec(&json!({
        "iss": idp.base,
        "aud": CLIENT,
        "sub": "user-1",
        "preferred_username": "alice",
        "name_id": "alice-sso",
        "nonce": nonce,
        "iat": issued,
        "exp": issued + 300,
    }))
    .unwrap());
    let mut mac = Hmac::<Sha256>::new_from_slice(idp.key.as_bytes()).unwrap();
    mac.update(format!("{header}.{claims}").as_bytes());
    let jwt = format!("{header}.{claims}.{}", b64(&mac.finalize().into_bytes()));
    Json(json!({
        "access_token": "at",
        "token_type": "Bearer",
        "expires_in": 300,
        "id_token": jwt,
    }))
    .into_response()
}

struct Dysk {
    app: Router,
    config: Arc<RwLock<ConfigStore>>,
    idp: Arc<Idp>,
}

async fn dysk(idp: Arc<Idp>, sso: bool, extra_env: &[(&str, &str)]) -> Dysk {
    let path = std::env::temp_dir().join(format!("infinidysk-oidc-{}.sqlite", ItemId::new()));
    let db = Db::connect(&format!("sqlite:{}", path.display()))
        .await
        .unwrap();
    let mut env = vec![(
        "INFINIDYSK__AUTH__SESSION_KEY".to_owned(),
        "signing".to_owned(),
    )];
    env.extend(
        extra_env
            .iter()
            .map(|(k, v)| ((*k).to_owned(), (*v).to_owned())),
    );
    let config = Arc::new(RwLock::new(
        ConfigStore::load(db.clone(), env).await.unwrap(),
    ));
    let auth = Arc::new(Auth::load(db, Arc::clone(&config)).await.unwrap());
    let probe = Router::new()
        .route("/probe", get(|| async { "ok" }))
        .route_layer(axum::middleware::from_fn_with_state(
            Arc::clone(&auth),
            require,
        ));
    let dysk = Dysk {
        app: router(auth).merge(probe),
        config,
        idp,
    };
    if sso {
        dysk.save(sso_on(&dysk.idp.base)).await;
    }
    dysk
}

fn sso_on(issuer: &str) -> impl Fn(&mut Settings) {
    let issuer = issuer.to_owned();
    move |s| {
        s.auth.oidc.issuer.clone_from(&issuer);
        CLIENT.clone_into(&mut s.auth.oidc.client_id);
        s.auth.oidc.client_secret = Secret(SECRET.into());
        s.auth.oidc.redirect_uri.clear();
        "name_id".clone_into(&mut s.auth.oidc.username_claim);
        "http://dysk.test".clone_into(&mut s.network.public_url);
    }
}

#[derive(Debug)]
struct Reply {
    code: StatusCode,
    location: String,
    cache_control: String,
    cookies: Vec<String>,
    body: Value,
}

impl Reply {
    /// The full `Set-Cookie` for `name`.
    fn set_cookie(&self, name: &str) -> Option<&str> {
        self.cookies
            .iter()
            .map(String::as_str)
            .find(|c| c.starts_with(&format!("{name}=")))
    }

    /// `name=value`, ready for a `Cookie` header.
    fn cookie(&self, name: &str) -> Option<String> {
        self.set_cookie(name)
            .map(|c| c.split(';').next().unwrap().to_owned())
    }
}

impl Dysk {
    async fn save(&self, change: impl FnOnce(&mut Settings)) {
        let mut settings = self.config.read().await.settings().clone();
        change(&mut settings);
        self.config.write().await.save(settings).await.unwrap();
    }

    async fn call(
        &self,
        method: &str,
        uri: &str,
        cookie: Option<&str>,
        peer: Option<&str>,
        body: &str,
    ) -> Reply {
        call(self.app.clone(), method, uri, cookie, peer, body).await
    }

    async fn status(&self, cookie: Option<&str>, peer: Option<&str>) -> Value {
        self.call("GET", "/api/v1/auth", cookie, peer, "")
            .await
            .body
    }

    /// `GET /auth/oidc/login`: the provider URL and the flow cookie.
    async fn begin(&self) -> (Url, String) {
        let r = self.call("GET", "/auth/oidc/login", None, None, "").await;
        assert_eq!(r.code, StatusCode::FOUND);
        (
            Url::parse(&r.location).unwrap(),
            r.cookie("infinidysk_sso").unwrap(),
        )
    }

    /// Plays the provider's `/authorize` for `authorize` and calls the callback.
    async fn finish(&self, authorize: &Url, flow: &str) -> Reply {
        self.finish_with_nonce(authorize, flow, None).await
    }

    /// [`Self::finish`], with the provider putting `nonce` in the ID token instead.
    async fn finish_with_nonce(&self, authorize: &Url, flow: &str, nonce: Option<&str>) -> Reply {
        let param = |k: &str| {
            authorize
                .query_pairs()
                .find(|(n, _)| n == k)
                .map(|(_, v)| v.into_owned())
                .unwrap()
        };
        self.idp.codes.lock().unwrap().insert(
            "code-1".to_owned(),
            (
                nonce.map_or_else(|| param("nonce"), str::to_owned),
                param("code_challenge"),
            ),
        );
        self.callback(&[("code", "code-1"), ("state", &param("state"))], flow)
            .await
    }

    async fn callback(&self, query: &[(&str, &str)], flow: &str) -> Reply {
        let mut url = Url::parse("http://dysk.test/auth/oidc/callback").unwrap();
        url.query_pairs_mut().extend_pairs(query);
        let uri = format!("{}?{}", url.path(), url.query().unwrap());
        self.call("GET", &uri, Some(flow), None, "").await
    }

    /// A full SSO sign-in: the session cookie.
    async fn sign_in(&self) -> String {
        let (authorize, flow) = self.begin().await;
        let r = self.finish(&authorize, &flow).await;
        assert_eq!(r.location, "/");
        r.cookie("infinidysk_session").unwrap()
    }
}

async fn call(
    app: Router,
    method: &str,
    uri: &str,
    cookie: Option<&str>,
    peer: Option<&str>,
    body: &str,
) -> Reply {
    let mut req = Request::builder()
        .method(method)
        .uri(uri)
        .header(header::CONTENT_TYPE, "application/json");
    if let Some(cookie) = cookie {
        req = req.header(header::COOKIE, cookie);
    }
    if let Some(peer) = peer {
        req = req.extension(ConnectInfo(SocketAddr::new(peer.parse().unwrap(), 1)));
    }
    let res = app
        .oneshot(req.body(Body::from(body.to_owned())).unwrap())
        .await
        .unwrap();
    let (parts, body) = res.into_parts();
    let text = |name| {
        parts
            .headers
            .get(name)
            .map_or_else(String::new, |v| v.to_str().unwrap().to_owned())
    };
    let (location, cache_control) = (text(header::LOCATION), text(header::CACHE_CONTROL));
    let cookies = parts
        .headers
        .get_all(header::SET_COOKIE)
        .iter()
        .map(|v| v.to_str().unwrap().to_owned())
        .collect();
    let bytes = body.collect().await.unwrap().to_bytes();
    Reply {
        code: parts.status,
        location,
        cache_control,
        cookies,
        body: serde_json::from_slice(&bytes).unwrap_or(Value::Null),
    }
}

#[tokio::test]
async fn sso_sign_in_starts_a_session() {
    let idp = idp(SECRET, None).await;
    let dysk = dysk(Arc::clone(&idp), true, &[]).await;

    let r = dysk.call("GET", "/auth/oidc/login", None, None, "").await;
    assert_eq!(r.code, StatusCode::FOUND);
    assert_eq!(r.cache_control, "no-store");
    let flow_cookie = r.set_cookie("infinidysk_sso").unwrap();
    assert!(
        flow_cookie.contains("HttpOnly") && flow_cookie.contains("SameSite=Lax"),
        "{flow_cookie}"
    );
    let authorize = Url::parse(&r.location).unwrap();
    assert_eq!(
        authorize.as_str().split('?').next(),
        Some(format!("{}/authorize", idp.base).as_str())
    );
    let params: HashMap<_, _> = authorize.query_pairs().into_owned().collect();
    assert_eq!(params["response_type"], "code");
    assert_eq!(params["client_id"], CLIENT);
    assert_eq!(params["code_challenge_method"], "S256");
    assert_eq!(
        params["redirect_uri"],
        "http://dysk.test/auth/oidc/callback"
    );
    assert!(params.contains_key("nonce") && params.contains_key("state"));
    let scopes: Vec<_> = params["scope"].split(' ').collect();
    assert_eq!(scopes.iter().filter(|s| **s == "openid").count(), 1);
    assert!(scopes.contains(&"profile") && scopes.contains(&"email"));
    let flow = r.cookie("infinidysk_sso").unwrap();

    // Another sign-in starting does not spoil the first.
    dysk.begin().await;
    let r = dysk.finish(&authorize, &flow).await;
    assert_eq!((r.code, r.location.as_str()), (StatusCode::FOUND, "/"));
    let session = r.set_cookie("infinidysk_session").unwrap();
    assert!(session.contains("SameSite=Strict"), "{session}");
    let cleared = r.set_cookie("infinidysk_sso").unwrap();
    assert!(
        cleared.starts_with("infinidysk_sso=;") && cleared.contains("Max-Age=0"),
        "{cleared}"
    );
    assert_eq!(idp.discoveries.load(Ordering::SeqCst), 1);
    let session = r.cookie("infinidysk_session").unwrap();

    let s = dysk.status(Some(&session), None).await;
    assert_eq!(s["user"], "alice-sso");
    assert_eq!(s["method"], "sso");
    assert_eq!(s["sso_current"], true);
    assert_eq!(s["sso"], true);
    assert_eq!(s["setup_required"], false);

    // The public URL supplies the redirect URI, so it decides whether the session proves
    // the saved settings.
    dysk.save(|s| s.network.public_url = "http://dysk.example".to_owned())
        .await;
    let s = dysk.status(Some(&session), None).await;
    assert_eq!(
        (&s["user"], &s["sso_current"]),
        (&json!("alice-sso"), &json!(false))
    );
    dysk.save(|s| s.network.public_url = "http://dysk.test".to_owned())
        .await;
    assert_eq!(dysk.status(Some(&session), None).await["sso_current"], true);

    dysk.save(|s| s.auth.oidc.scopes = "openid profile".to_owned())
        .await;
    let s = dysk.status(Some(&session), None).await;
    assert_eq!(
        (&s["user"], &s["sso_current"]),
        (&json!("alice-sso"), &json!(false))
    );

    dysk.save(|s| s.auth.oidc.client_id = "other-client".to_owned())
        .await;
    assert!(dysk.status(Some(&session), None).await["user"].is_null());
}

fn failed(r: &Reply) {
    assert_eq!(
        (r.code, r.location.as_str()),
        (StatusCode::FOUND, "/?sso=failed")
    );
    assert!(r.set_cookie("infinidysk_session").is_none());
}

#[tokio::test]
async fn settings_changed_during_sign_in_are_not_certified() {
    let idp = idp(SECRET, None).await;
    let dysk = dysk(Arc::clone(&idp), true, &[]).await;
    let (authorize, flow) = dysk.begin().await;
    dysk.save(|s| s.auth.oidc.redirect_uri = "http://other.test/auth/oidc/callback".to_owned())
        .await;
    failed(&dysk.finish(&authorize, &flow).await);
    assert_eq!(idp.token_requests.load(Ordering::SeqCst), 0);
}

#[tokio::test]
async fn sso_rejects_a_token_signed_with_another_key() {
    let dysk = dysk(idp("not-the-secret", None).await, true, &[]).await;
    let (authorize, flow) = dysk.begin().await;
    let r = dysk.finish(&authorize, &flow).await;
    failed(&r);
    assert!(
        r.set_cookie("infinidysk_sso")
            .unwrap()
            .contains("Max-Age=0")
    );
}

#[tokio::test]
async fn sso_rejects_an_id_token_with_another_nonce() {
    let dysk = dysk(idp(SECRET, None).await, true, &[]).await;
    let (authorize, flow) = dysk.begin().await;
    failed(
        &dysk
            .finish_with_nonce(&authorize, &flow, Some("replayed"))
            .await,
    );
}

#[tokio::test]
async fn sso_rejects_an_expired_id_token() {
    let idp = idp(SECRET, None).await;
    idp.expired.store(true, Ordering::SeqCst);
    let dysk = dysk(idp, true, &[]).await;
    let (authorize, flow) = dysk.begin().await;
    failed(&dysk.finish(&authorize, &flow).await);
}

#[tokio::test]
async fn sso_uses_basic_auth_when_the_provider_only_takes_that() {
    let idp = idp(SECRET, None).await;
    idp.basic_only.store(true, Ordering::SeqCst);
    let dysk = dysk(Arc::clone(&idp), true, &[]).await;
    dysk.sign_in().await;
    assert_eq!(idp.token_requests.load(Ordering::SeqCst), 1);
}

#[tokio::test]
async fn sso_uses_basic_auth_when_the_provider_lists_no_methods() {
    let idp = idp(SECRET, None).await;
    idp.no_methods.store(true, Ordering::SeqCst);
    let dysk = dysk(Arc::clone(&idp), true, &[]).await;
    dysk.sign_in().await;
    assert_eq!(idp.token_requests.load(Ordering::SeqCst), 1);
}

#[tokio::test]
async fn sso_starts_on_the_redirect_uri_host() {
    let dysk = dysk(idp(SECRET, None).await, true, &[]).await;
    let get = |uri: &'static str, host: &'static str| {
        let req = Request::get(uri)
            .header(header::HOST, host)
            .body(Body::empty())
            .unwrap();
        dysk.app.clone().oneshot(req)
    };
    let location = |r: &Response| r.headers()[header::LOCATION].to_str().unwrap().to_owned();
    let cookies = |r: &Response| r.headers().get_all(header::SET_COOKIE).iter().count();

    let r = get("/auth/oidc/login", "192.168.1.5:3000").await.unwrap();
    assert_eq!(location(&r), "http://dysk.test/auth/oidc/login?canonical");
    assert_eq!(cookies(&r), 0);
    // A proxy that rewrites Host does not loop the browser.
    let r = get("/auth/oidc/login?canonical", "192.168.1.5:3000")
        .await
        .unwrap();
    assert!(location(&r).starts_with(&dysk.idp.base), "{}", location(&r));
    assert_eq!(cookies(&r), 1);
    let r = get("/auth/oidc/login", "dysk.test").await.unwrap();
    assert!(location(&r).starts_with(&dysk.idp.base), "{}", location(&r));
}

#[tokio::test]
async fn sso_rejects_a_callback_with_another_state() {
    let idp = idp(SECRET, None).await;
    let dysk = dysk(Arc::clone(&idp), true, &[]).await;
    let (_, flow) = dysk.begin().await;
    failed(
        &dysk
            .callback(&[("code", "code-1"), ("state", "forged")], &flow)
            .await,
    );
    assert_eq!(idp.token_requests.load(Ordering::SeqCst), 0);
}

#[tokio::test]
async fn a_malformed_callback_still_redirects() {
    let dysk = dysk(idp(SECRET, None).await, true, &[]).await;
    let (_, flow) = dysk.begin().await;
    let r = dysk
        .callback(&[("state", "a"), ("state", "b")], &flow)
        .await;
    failed(&r);
    assert!(
        r.set_cookie("infinidysk_sso")
            .unwrap()
            .contains("Max-Age=0")
    );
}

#[tokio::test]
async fn sso_refuses_a_plain_http_authorization_endpoint() {
    let dysk = dysk(
        idp(SECRET, Some("http://idp.example/authorize")).await,
        true,
        &[],
    )
    .await;
    let r = dysk.call("GET", "/auth/oidc/login", None, None, "").await;
    assert_eq!(r.location, "/?sso=failed");
    assert!(r.set_cookie("infinidysk_sso").is_none());
}

#[tokio::test]
async fn failed_discovery_is_shared_by_concurrent_sign_ins() {
    let idp = idp(SECRET, None).await;
    let dysk = dysk(Arc::clone(&idp), true, &[]).await;
    idp.fail_discovery.store(true, Ordering::SeqCst);
    idp.gate.send_replace(false);
    let logins: Vec<_> = (0..8)
        .map(|_| {
            let app = dysk.app.clone();
            tokio::spawn(async move { call(app, "GET", "/auth/oidc/login", None, None, "").await })
        })
        .collect();
    idp.arrived.notified().await;
    idp.gate.send_replace(true);
    for login in logins {
        assert_eq!(login.await.unwrap().location, "/?sso=failed");
    }
    assert_eq!(idp.discoveries.load(Ordering::SeqCst), 1);
}

#[tokio::test]
async fn a_dropped_sign_in_does_not_restart_discovery() {
    let idp = idp(SECRET, None).await;
    let dysk = dysk(Arc::clone(&idp), true, &[]).await;
    idp.gate.send_replace(false);
    let login = |app: Router| {
        tokio::spawn(async move { call(app, "GET", "/auth/oidc/login", None, None, "").await })
    };
    let a = login(dysk.app.clone());
    idp.arrived.notified().await;
    a.abort();
    assert!(a.await.unwrap_err().is_cancelled());
    let b = login(dysk.app.clone());
    idp.gate.send_replace(true);
    let r = b.await.unwrap();
    assert!(
        r.location.starts_with(&format!("{}/authorize", idp.base)),
        "{}",
        r.location
    );
    assert_eq!(idp.discoveries.load(Ordering::SeqCst), 1);
}

#[tokio::test]
async fn changing_the_password_keeps_the_sso_session() {
    let dysk = dysk(idp(SECRET, None).await, false, &[]).await;
    let r = dysk
        .call("POST", "/api/v1/auth/setup", None, None, SETUP)
        .await;
    assert_eq!(r.code, StatusCode::NO_CONTENT);
    let on = sso_on(&dysk.idp.base);
    dysk.save(|s| {
        on(s);
        s.auth.local_login = false;
    })
    .await;
    let session = dysk.sign_in().await;
    let change = r#"{"current_password":"password1","password":"password2"}"#;
    let r = dysk
        .call("PUT", "/api/v1/auth/password", Some(&session), None, change)
        .await;
    assert_eq!(r.code, StatusCode::NO_CONTENT);
    assert!(r.set_cookie("infinidysk_session").is_none());
    assert_eq!(dysk.status(Some(&session), None).await["method"], "sso");
}

#[tokio::test]
async fn recovery_without_an_account_uses_local_mode() {
    let dysk = dysk(
        idp(SECRET, None).await,
        true,
        &[("INFINIDYSK__AUTH__MODE", "local")],
    )
    .await;
    let lan = Some("192.168.1.20");
    assert_eq!(dysk.status(None, lan).await["required"], false);
    let r = dysk.call("GET", "/probe", None, lan, "").await;
    assert_eq!(r.code, StatusCode::OK);

    let remote = Some("203.0.113.9");
    let s = dysk.status(None, remote).await;
    assert_eq!(
        (&s["required"], &s["password_login"], &s["sso"]),
        (&json!(true), &json!(false), &json!(true))
    );
    let r = dysk.call("GET", "/probe", None, remote, "").await;
    assert_eq!(r.code, StatusCode::UNAUTHORIZED);
    let r = dysk
        .call("POST", "/api/v1/auth/setup", None, remote, SETUP)
        .await;
    assert_eq!(r.code, StatusCode::FORBIDDEN);
}

#[tokio::test]
async fn password_sign_in_turns_off_only_while_sso_is_on() {
    let dysk = dysk(idp(SECRET, None).await, true, &[]).await;
    let r = dysk
        .call("POST", "/api/v1/auth/setup", None, None, SETUP)
        .await;
    assert_eq!(r.code, StatusCode::FORBIDDEN);
    let s = dysk.status(None, None).await;
    assert_eq!(
        (&s["setup_required"], &s["password_login"], &s["sso"]),
        (&json!(false), &json!(false), &json!(true))
    );

    let sso_off = |s: &mut Settings| s.auth.oidc.issuer.clear();
    dysk.save(sso_off).await;
    let r = dysk
        .call("POST", "/api/v1/auth/setup", None, None, SETUP)
        .await;
    assert_eq!(r.code, StatusCode::NO_CONTENT);
    let session = r.cookie("infinidysk_session").unwrap();

    dysk.save(sso_on(&dysk.idp.base)).await;
    assert_eq!(
        dysk.status(Some(&session), None).await["method"],
        "password"
    );
    assert_eq!(dysk.status(None, None).await["password_login"], true);

    dysk.save(|s| s.auth.local_login = false).await;
    assert!(dysk.status(Some(&session), None).await["user"].is_null());
    let r = dysk
        .call("POST", "/api/v1/auth/login", None, None, SETUP)
        .await;
    assert_eq!(r.code, StatusCode::FORBIDDEN);

    dysk.save(sso_off).await;
    let r = dysk
        .call("POST", "/api/v1/auth/login", None, None, SETUP)
        .await;
    assert_eq!(r.code, StatusCode::NO_CONTENT);
}
