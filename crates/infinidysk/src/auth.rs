//! Admin login: one account (argon2id) or single sign-on, HMAC-signed session cookies, and
//! the admin API key.
//!
//! A password session cookie is `<expiry>.<mac>`, signed over the expiry and the current
//! password hash, so changing the password or `auth.session_key` ends every password
//! session. An SSO session is signed over the issuer and client ID instead.

use std::net::{IpAddr, SocketAddr};
use std::sync::Arc;
use std::time::{SystemTime, UNIX_EPOCH};

use argon2::{Argon2, PasswordHasher as _, PasswordVerifier as _};
use axum::extract::{ConnectInfo, Request, State};
use axum::http::{Extensions, HeaderMap, StatusCode, header};
use axum::middleware::Next;
use axum::response::{IntoResponse, Response};
use axum::routing::{get, post, put};
use axum::{Json, Router};
use base64::Engine as _;
use base64::engine::general_purpose::URL_SAFE_NO_PAD;
use hmac::{Hmac, KeyInit as _, Mac as _};
use infinidysk_db::Db;
use serde::{Deserialize, Serialize};
use serde_json::json;
use sha2::Sha256;
use tokio::sync::{RwLock, Semaphore};
use utoipa::ToSchema;

use crate::admin::ApiError;
use crate::config::{self, AuthMode, ConfigStore, Settings};
use crate::sab::same;

pub(crate) const COOKIE: &str = "infinidysk_session";
/// Marks a session started through SSO: `sso.<expiry>.<username>.<settings>.<mac>`. A
/// password session is `<expiry>.<mac>` and starts with a digit.
const SSO: &str = "sso.";
const SESSION_SECS: u64 = 30 * 24 * 60 * 60;
const MIN_PASSWORD: usize = 8;
const MAX_FIELD: usize = 1024;

#[derive(Clone)]
struct Admin {
    username: String,
    hash: String,
}

/// How a session was started.
#[derive(Clone, Copy, Debug, PartialEq, Eq, Serialize, ToSchema)]
#[serde(rename_all = "snake_case")]
pub(crate) enum Method {
    Password,
    Sso,
}

pub(crate) struct Session {
    user: String,
    method: Method,
    /// An SSO session started under the saved SSO settings.
    current: bool,
}

#[derive(Debug, thiserror::Error)]
pub enum LoadError {
    #[error(transparent)]
    Db(#[from] infinidysk_db::Error),
    #[error("building the SSO HTTP client failed")]
    Http(#[from] reqwest::Error),
}

pub struct Auth {
    db: Db,
    config: Arc<RwLock<ConfigStore>>,
    admin: RwLock<Option<Admin>>,
    /// One password hash at a time, so login attempts cannot starve streaming of CPU.
    hashing: Semaphore,
    pub(crate) sso: crate::oidc::Sso,
}

impl Auth {
    pub async fn load(db: Db, config: Arc<RwLock<ConfigStore>>) -> Result<Self, LoadError> {
        let admin = db
            .admin()
            .await?
            .map(|(username, hash)| Admin { username, hash });
        let mode = config.read().await.settings().auth.mode;
        if mode != AuthMode::Required {
            tracing::warn!(
                ?mode,
                "admin authentication is not required for every client"
            );
        }
        Ok(Self {
            db,
            config,
            admin: RwLock::new(admin),
            hashing: Semaphore::new(1),
            sso: crate::oidc::Sso::new()?,
        })
    }

    pub(crate) async fn settings(&self) -> Settings {
        self.config.read().await.settings().clone()
    }

    async fn session_key(&self) -> String {
        self.config
            .read()
            .await
            .settings()
            .auth
            .session_key
            .0
            .clone()
    }

    /// The signed-in session, if `headers` carry a valid session cookie.
    async fn session(&self, headers: &HeaderMap) -> Option<Session> {
        let token = cookie(headers, COOKIE)?;
        let config = self.config.read().await;
        let settings = config.settings();
        let key = settings.auth.session_key.0.clone();
        if key.is_empty() {
            return None;
        }
        if let Some(rest) = token.strip_prefix(SSO) {
            let oidc = &settings.auth.oidc;
            if !oidc.enabled() {
                return None;
            }
            let (issuer, client_id) = (oidc.issuer.clone(), oidc.client_id.clone());
            let saved = sso_settings(&key, settings);
            drop(config);
            let mut parts = rest.split('.');
            let (Some(expiry), Some(user), Some(proven), Some(mac), None) = (
                parts.next(),
                parts.next(),
                parts.next(),
                parts.next(),
                parts.next(),
            ) else {
                return None;
            };
            if expiry.parse::<u64>().ok()? <= now() {
                return None;
            }
            let mac = URL_SAFE_NO_PAD.decode(mac).ok()?;
            sign_sso(&key, expiry, user, proven, &issuer, &client_id)
                .verify_slice(&mac)
                .ok()?;
            return Some(Session {
                user: String::from_utf8(URL_SAFE_NO_PAD.decode(user).ok()?).ok()?,
                method: Method::Sso,
                current: same(proven.as_bytes(), saved.as_bytes()),
            });
        }
        let password = settings.auth.password_login();
        drop(config);
        if !password {
            return None;
        }
        let (expiry, mac) = token.split_once('.')?;
        if expiry.parse::<u64>().ok()? <= now() {
            return None;
        }
        let mac = URL_SAFE_NO_PAD.decode(mac).ok()?;
        let admin = self.admin.read().await.clone()?;
        sign(&key, expiry, &admin).verify_slice(&mac).ok()?;
        Some(Session {
            user: admin.username,
            method: Method::Password,
            current: false,
        })
    }

    /// A valid session cookie, or `X-Api-Key` / `Authorization: Bearer` equal to
    /// `auth.api_key` (scrapers can only send the latter).
    async fn allowed(&self, headers: &HeaderMap) -> bool {
        let bearer = headers
            .get(header::AUTHORIZATION)
            .and_then(|v| v.as_bytes().strip_prefix(b"Bearer "));
        if let Some(given) = headers
            .get("x-api-key")
            .map(header::HeaderValue::as_bytes)
            .or(bearer)
        {
            let key = self.config.read().await.settings().auth.api_key.0.clone();
            return !key.is_empty() && same(given, key.as_bytes());
        }
        self.session(headers).await.is_some()
    }

    /// Whether `auth.mode` lets this client in without signing in.
    async fn exempt(&self, headers: &HeaderMap, extensions: &Extensions) -> bool {
        let config = self.config.read().await;
        let settings = config.settings();
        let mode = settings.auth.mode;
        let trusted: Vec<_> = settings
            .network
            .trusted_proxies
            .iter()
            .filter_map(|p| infinidysk_webdav::parse_proxy(p))
            .collect();
        drop(config);
        match mode {
            AuthMode::Required => false,
            AuthMode::Disabled => true,
            AuthMode::Local => extensions
                .get::<ConnectInfo<SocketAddr>>()
                .and_then(|ConnectInfo(addr)| {
                    infinidysk_webdav::client_ip(headers, addr.ip(), &trusted)
                })
                .is_some_and(local),
        }
    }

    async fn issue(&self, headers: &HeaderMap, admin: &Admin) -> [(header::HeaderName, String); 1] {
        let expiry = (now() + SESSION_SECS).to_string();
        let mac = sign(&self.session_key().await, &expiry, admin).finalize();
        let token = format!("{expiry}.{}", URL_SAFE_NO_PAD.encode(mac.into_bytes()));
        set_cookie(headers, &token, SESSION_SECS)
    }

    async fn hash(&self, password: String) -> Result<String, Fail> {
        let _permit = self.hashing.acquire().await.map_err(|_| Fail::Internal)?;
        tokio::task::spawn_blocking(move || {
            Argon2::default()
                .hash_password(password.as_bytes())
                .map(|h| h.to_string())
        })
        .await
        .map_err(|_| Fail::Internal)?
        .map_err(|error| {
            tracing::warn!(reason = %error, "password hashing failed");
            Fail::Internal
        })
    }

    async fn verify(&self, password: String, hash: String) -> Result<bool, Fail> {
        let _permit = self.hashing.acquire().await.map_err(|_| Fail::Internal)?;
        tokio::task::spawn_blocking(move || {
            infinidysk_import::verify_legacy(&hash, &password).unwrap_or_else(|| {
                Argon2::default()
                    .verify_password(password.as_bytes(), hash.as_str())
                    .is_ok()
            })
        })
        .await
        .map_err(|_| Fail::Internal)
    }
}

/// Gates admin routes on a session cookie or the admin API key.
pub async fn require(State(auth): State<Arc<Auth>>, request: Request, next: Next) -> Response {
    if auth.exempt(request.headers(), request.extensions()).await
        || auth.allowed(request.headers()).await
    {
        next.run(request).await
    } else {
        Fail::Unauthorized.into_response()
    }
}

pub fn router(auth: Arc<Auth>) -> Router {
    Router::new()
        .route("/api/v1/auth/password", put(change_password))
        .route_layer(axum::middleware::from_fn_with_state(
            Arc::clone(&auth),
            require,
        ))
        .route("/api/v1/auth", get(status))
        .route("/api/v1/auth/setup", post(setup))
        .route("/api/v1/auth/login", post(login))
        .route("/api/v1/auth/logout", post(logout))
        .route(crate::oidc::LOGIN, get(crate::oidc::login))
        .route(crate::oidc::CALLBACK, get(crate::oidc::callback))
        .with_state(auth)
}

pub(crate) enum Fail {
    Bad(&'static str),
    Unauthorized,
    Forbidden(&'static str),
    WrongLogin,
    Conflict,
    Internal,
}

impl From<infinidysk_db::Error> for Fail {
    fn from(error: infinidysk_db::Error) -> Self {
        tracing::warn!(reason = %error, "auth request failed");
        Self::Internal
    }
}

impl IntoResponse for Fail {
    fn into_response(self) -> Response {
        let (code, error) = match self {
            Self::Bad(msg) => (StatusCode::BAD_REQUEST, msg),
            Self::Unauthorized => (StatusCode::UNAUTHORIZED, "not signed in"),
            Self::Forbidden(msg) => (StatusCode::FORBIDDEN, msg),
            Self::WrongLogin => (StatusCode::UNAUTHORIZED, "wrong username or password"),
            Self::Conflict => (StatusCode::CONFLICT, "an admin account already exists"),
            Self::Internal => (StatusCode::INTERNAL_SERVER_ERROR, "internal error"),
        };
        (code, Json(json!({ "error": error }))).into_response()
    }
}

#[derive(Deserialize, ToSchema)]
pub(crate) struct Login {
    username: String,
    password: String,
}

#[derive(Deserialize, ToSchema)]
pub(crate) struct NewPassword {
    current_password: String,
    /// Keeps the current username when absent.
    username: Option<String>,
    /// At least 8 characters.
    password: String,
}

#[derive(Serialize, ToSchema)]
#[allow(clippy::struct_excessive_bools)] // Independent flags the sign-in page reads.
pub(crate) struct AuthStatus {
    /// This client must sign in; `false` when `auth.mode` exempts it.
    required: bool,
    /// No admin account exists and SSO is off; `POST /api/v1/auth/setup` creates it.
    setup_required: bool,
    /// An admin account with a password exists.
    account: bool,
    /// The sign-in page offers username and password.
    password_login: bool,
    /// The sign-in page offers Sign in with SSO.
    sso: bool,
    /// The signed-in username.
    user: Option<String>,
    /// How `user` signed in.
    method: Option<Method>,
    /// The SSO session was started under the saved SSO settings, so it proves they work.
    sso_current: bool,
}

fn check(username: &str, password: &str) -> Result<(), Fail> {
    if username.trim().is_empty() || username.len() > MAX_FIELD {
        return Err(Fail::Bad("username must be 1-1024 bytes"));
    }
    if password.chars().count() < MIN_PASSWORD || password.len() > MAX_FIELD {
        return Err(Fail::Bad("password must be at least 8 characters"));
    }
    Ok(())
}

#[utoipa::path(get, path = "/api/v1/auth", tag = "auth", security(()),
    responses((status = 200, body = AuthStatus)))]
pub(crate) async fn status(
    State(auth): State<Arc<Auth>>,
    headers: HeaderMap,
    extensions: Extensions,
) -> Json<AuthStatus> {
    let account = auth.admin.read().await.is_some();
    let config = auth.config.read().await;
    let a = &config.settings().auth;
    let (sso, password) = (a.oidc.enabled(), a.password_login());
    drop(config);
    let session = auth.session(&headers).await;
    Json(AuthStatus {
        required: !auth.exempt(&headers, &extensions).await,
        setup_required: !account && !sso,
        account,
        password_login: account && password,
        sso,
        method: session.as_ref().map(|s| s.method),
        sso_current: session
            .as_ref()
            .is_some_and(|s| s.method == Method::Sso && s.current),
        user: session.map(|s| s.user),
    })
}

async fn sso_on(auth: &Auth) -> bool {
    auth.config.read().await.settings().auth.oidc.enabled()
}

/// Creates the admin account and signs in; only while none exists.
// ponytail: whoever reaches a fresh install first claims it; an env-provisioned admin is
// the upgrade path if exposed first runs become a problem.
#[utoipa::path(post, path = "/api/v1/auth/setup", tag = "auth", security(()), request_body = Login,
    responses(
        (status = 204, description = "Signed in; sets the session cookie"),
        (status = 400, body = ApiError),
        (status = 403, body = ApiError, description = "Sign-in uses SSO"),
        (status = 409, body = ApiError, description = "An admin account already exists"),
    ))]
pub(crate) async fn setup(
    State(auth): State<Arc<Auth>>,
    headers: HeaderMap,
    Json(body): Json<Login>,
) -> Result<Response, Fail> {
    if sso_on(&auth).await {
        return Err(Fail::Forbidden(
            "sign-in uses SSO, so no admin account can be created",
        ));
    }
    check(&body.username, &body.password)?;
    if auth.admin.read().await.is_some() {
        return Err(Fail::Conflict);
    }
    let hash = auth.hash(body.password).await?;
    let admin = Admin {
        username: body.username,
        hash,
    };
    let mut current = auth.admin.write().await;
    if current.is_some() || !auth.db.create_admin(&admin.username, &admin.hash).await? {
        return Err(Fail::Conflict);
    }
    *current = Some(admin.clone());
    drop(current);
    tracing::info!("admin account created");
    Ok((StatusCode::NO_CONTENT, auth.issue(&headers, &admin).await).into_response())
}

#[utoipa::path(post, path = "/api/v1/auth/login", tag = "auth", security(()), request_body = Login,
    responses(
        (status = 204, description = "Signed in; sets the session cookie"),
        (status = 401, body = ApiError),
        (status = 403, body = ApiError, description = "Password sign-in is turned off"),
    ))]
pub(crate) async fn login(
    State(auth): State<Arc<Auth>>,
    headers: HeaderMap,
    Json(body): Json<Login>,
) -> Result<Response, Fail> {
    if !auth.config.read().await.settings().auth.password_login() {
        return Err(Fail::Forbidden(
            "password sign-in is turned off; sign in with SSO",
        ));
    }
    let Some(admin) = auth.admin.read().await.clone() else {
        return Err(Fail::Unauthorized);
    };
    // Hash first so a wrong username costs the same as a wrong password.
    let ok = auth
        .verify(body.password.clone(), admin.hash.clone())
        .await?
        // Case-insensitive, as in C#, which stored usernames lowercased.
        && same(
            body.username.to_lowercase().as_bytes(),
            admin.username.to_lowercase().as_bytes(),
        );
    if !ok {
        tracing::warn!(
            reason = "wrong username or password",
            "admin login rejected"
        );
        return Err(Fail::WrongLogin);
    }
    let admin = if infinidysk_import::is_legacy(&admin.hash) {
        let admin = Admin {
            hash: auth.hash(body.password).await?,
            ..admin
        };
        auth.db.set_admin(&admin.username, &admin.hash).await?;
        *auth.admin.write().await = Some(admin.clone());
        tracing::info!("admin password carried over from the C# install");
        admin
    } else {
        admin
    };
    Ok((StatusCode::NO_CONTENT, auth.issue(&headers, &admin).await).into_response())
}

#[utoipa::path(post, path = "/api/v1/auth/logout", tag = "auth", security(()),
    responses((status = 204, description = "Clears the session cookie")))]
pub(crate) async fn logout(headers: HeaderMap) -> impl IntoResponse {
    (StatusCode::NO_CONTENT, set_cookie(&headers, "", 0))
}

/// Changes the admin password (and optionally username); other password sessions end.
#[utoipa::path(put, path = "/api/v1/auth/password", tag = "auth", request_body = NewPassword,
    responses(
        (status = 204, description = "Changed; a password session gets a fresh cookie"),
        (status = 400, body = ApiError),
    ))]
pub(crate) async fn change_password(
    State(auth): State<Arc<Auth>>,
    headers: HeaderMap,
    Json(body): Json<NewPassword>,
) -> Result<Response, Fail> {
    let Some(current) = auth.admin.read().await.clone() else {
        return Err(Fail::Unauthorized);
    };
    // Read before the new hash ends password sessions.
    let caller = auth.session(&headers).await.map(|s| s.method);
    let username = body.username.unwrap_or(current.username);
    check(&username, &body.password)?;
    if !auth.verify(body.current_password, current.hash).await? {
        return Err(Fail::Bad("current password is wrong"));
    }
    let admin = Admin {
        hash: auth.hash(body.password).await?,
        username,
    };
    auth.db.set_admin(&admin.username, &admin.hash).await?;
    *auth.admin.write().await = Some(admin.clone());
    tracing::info!("admin password changed; other password sessions signed out");
    if caller == Some(Method::Password) {
        return Ok((StatusCode::NO_CONTENT, auth.issue(&headers, &admin).await).into_response());
    }
    Ok(StatusCode::NO_CONTENT.into_response())
}

fn sign(key: &str, expiry: &str, admin: &Admin) -> Hmac<Sha256> {
    let mut mac =
        Hmac::<Sha256>::new_from_slice(key.as_bytes()).expect("HMAC accepts keys of any length");
    for part in [expiry, "\0", &admin.hash, "\0", &admin.username] {
        mac.update(part.as_bytes());
    }
    mac
}

fn sign_sso(
    key: &str,
    expiry: &str,
    user: &str,
    settings: &str,
    issuer: &str,
    client_id: &str,
) -> Hmac<Sha256> {
    let mut mac =
        Hmac::<Sha256>::new_from_slice(key.as_bytes()).expect("HMAC accepts keys of any length");
    for part in [
        "sso", "\0", expiry, "\0", user, "\0", settings, "\0", issuer, "\0", client_id,
    ] {
        mac.update(part.as_bytes());
    }
    mac
}

/// Names the settings that decide an SSO sign-in, so a session can say whether it proves
/// the saved ones work. Keyed, so the cookie reveals nothing about the client secret.
pub(crate) fn sso_settings(key: &str, settings: &Settings) -> String {
    let oidc = &settings.auth.oidc;
    // An empty redirect URI is derived from the public URL, so that decides the callback.
    let (source, redirect) = if oidc.redirect_uri.is_empty() {
        ("public-url", settings.network.public_url.as_str())
    } else {
        ("redirect-uri", oidc.redirect_uri.as_str())
    };
    let mut mac =
        Hmac::<Sha256>::new_from_slice(key.as_bytes()).expect("HMAC accepts keys of any length");
    for part in [
        "sso-settings",
        "\0",
        &oidc.issuer,
        "\0",
        &oidc.client_id,
        "\0",
        &oidc.client_secret.0,
        "\0",
        source,
        "\0",
        redirect,
        "\0",
        &oidc.scopes,
        "\0",
        &oidc.username_claim,
    ] {
        mac.update(part.as_bytes());
    }
    URL_SAFE_NO_PAD.encode(&mac.finalize().into_bytes()[..16])
}

/// The `Set-Cookie` value for a session `user` started through SSO under the settings
/// fingerprint `proven`.
pub(crate) fn sso_session(
    headers: &HeaderMap,
    auth: &config::Auth,
    user: &str,
    proven: &str,
) -> String {
    let key = &auth.session_key.0;
    let expiry = (now() + SESSION_SECS).to_string();
    let user = URL_SAFE_NO_PAD.encode(user);
    let mac = sign_sso(
        key,
        &expiry,
        &user,
        proven,
        &auth.oidc.issuer,
        &auth.oidc.client_id,
    )
    .finalize()
    .into_bytes();
    let token = format!(
        "{SSO}{expiry}.{user}.{proven}.{}",
        URL_SAFE_NO_PAD.encode(mac)
    );
    cookie_header(headers, COOKIE, &token, SESSION_SECS, "Strict")
}

pub(crate) fn cookie<'a>(headers: &'a HeaderMap, name: &str) -> Option<&'a str> {
    headers
        .get_all(header::COOKIE)
        .iter()
        .filter_map(|v| v.to_str().ok())
        .flat_map(|v| v.split(';'))
        .find_map(|c| c.trim().strip_prefix(name)?.strip_prefix('='))
}

fn set_cookie(headers: &HeaderMap, value: &str, max_age: u64) -> [(header::HeaderName, String); 1] {
    [(
        header::SET_COOKIE,
        cookie_header(headers, COOKIE, value, max_age, "Strict"),
    )]
}

/// A `Set-Cookie` value; `Secure` behind an HTTPS proxy.
pub(crate) fn cookie_header(
    headers: &HeaderMap,
    name: &str,
    value: &str,
    max_age: u64,
    same_site: &str,
) -> String {
    let https = headers
        .get("x-forwarded-proto")
        .and_then(|v| v.to_str().ok())
        .and_then(|v| v.split(',').next())
        .is_some_and(|v| v.trim().eq_ignore_ascii_case("https"));
    let secure = if https { "; Secure" } else { "" };
    format!("{name}={value}; Path=/; Max-Age={max_age}; HttpOnly; SameSite={same_site}{secure}")
}

const fn local(ip: IpAddr) -> bool {
    match ip {
        IpAddr::V4(v4) => v4.is_loopback() || v4.is_private() || v4.is_link_local(),
        IpAddr::V6(v6) => v6.is_loopback() || v6.is_unique_local() || v6.is_unicast_link_local(),
    }
}

pub(crate) fn now() -> u64 {
    SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .map_or(0, |d| d.as_secs())
}
