//! Admin login: one account (argon2id), HMAC-signed session cookies, and the admin API key.
//!
//! A session cookie is `<expiry>.<mac>`, signed over the expiry and the current password
//! hash, so changing the password or `auth.session_key` ends every session.

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
use crate::config::{AuthMode, ConfigStore};
use crate::sab::same;

pub(crate) const COOKIE: &str = "infinidysk_session";
const SESSION_SECS: u64 = 30 * 24 * 60 * 60;
const MIN_PASSWORD: usize = 8;
const MAX_FIELD: usize = 1024;

#[derive(Clone)]
struct Admin {
    username: String,
    hash: String,
}

pub struct Auth {
    db: Db,
    config: Arc<RwLock<ConfigStore>>,
    admin: RwLock<Option<Admin>>,
    /// One password hash at a time, so login attempts cannot starve streaming of CPU.
    hashing: Semaphore,
}

impl Auth {
    pub async fn load(
        db: Db,
        config: Arc<RwLock<ConfigStore>>,
    ) -> Result<Self, infinidysk_db::Error> {
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
        })
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

    /// The signed-in username, if `headers` carry a valid session cookie.
    async fn session(&self, headers: &HeaderMap) -> Option<String> {
        let (expiry, mac) = cookie(headers)?.split_once('.')?;
        if expiry.parse::<u64>().ok()? <= now() {
            return None;
        }
        let mac = URL_SAFE_NO_PAD.decode(mac).ok()?;
        let key = self.session_key().await;
        if key.is_empty() {
            return None;
        }
        let admin = self.admin.read().await.clone()?;
        sign(&key, expiry, &admin).verify_slice(&mac).ok()?;
        Some(admin.username)
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
        .with_state(auth)
}

pub(crate) enum Fail {
    Bad(&'static str),
    Unauthorized,
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
pub(crate) struct AuthStatus {
    /// This client must sign in; `false` when `auth.mode` exempts it.
    required: bool,
    /// No admin account exists yet; `POST /api/v1/auth/setup` creates it.
    setup_required: bool,
    /// The signed-in username.
    user: Option<String>,
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
    let setup_required = auth.admin.read().await.is_none();
    Json(AuthStatus {
        required: !auth.exempt(&headers, &extensions).await,
        setup_required,
        user: auth.session(&headers).await,
    })
}

/// Creates the admin account and signs in; only while none exists.
// ponytail: whoever reaches a fresh install first claims it; an env-provisioned admin is
// the upgrade path if exposed first runs become a problem.
#[utoipa::path(post, path = "/api/v1/auth/setup", tag = "auth", security(()), request_body = Login,
    responses(
        (status = 204, description = "Signed in; sets the session cookie"),
        (status = 400, body = ApiError),
        (status = 409, body = ApiError, description = "An admin account already exists"),
    ))]
pub(crate) async fn setup(
    State(auth): State<Arc<Auth>>,
    headers: HeaderMap,
    Json(body): Json<Login>,
) -> Result<Response, Fail> {
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
    ))]
pub(crate) async fn login(
    State(auth): State<Arc<Auth>>,
    headers: HeaderMap,
    Json(body): Json<Login>,
) -> Result<Response, Fail> {
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

/// Changes the admin password (and optionally username); other sessions end.
#[utoipa::path(put, path = "/api/v1/auth/password", tag = "auth", request_body = NewPassword,
    responses(
        (status = 204, description = "Changed; sets a fresh session cookie"),
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
    tracing::info!("admin password changed; other sessions signed out");
    Ok((StatusCode::NO_CONTENT, auth.issue(&headers, &admin).await).into_response())
}

fn sign(key: &str, expiry: &str, admin: &Admin) -> Hmac<Sha256> {
    let mut mac =
        Hmac::<Sha256>::new_from_slice(key.as_bytes()).expect("HMAC accepts keys of any length");
    for part in [expiry, "\0", &admin.hash, "\0", &admin.username] {
        mac.update(part.as_bytes());
    }
    mac
}

fn cookie(headers: &HeaderMap) -> Option<&str> {
    headers
        .get_all(header::COOKIE)
        .iter()
        .filter_map(|v| v.to_str().ok())
        .flat_map(|v| v.split(';'))
        .find_map(|c| c.trim().strip_prefix(COOKIE)?.strip_prefix('='))
}

fn set_cookie(headers: &HeaderMap, value: &str, max_age: u64) -> [(header::HeaderName, String); 1] {
    let https = headers
        .get("x-forwarded-proto")
        .is_some_and(|v| v.as_bytes().eq_ignore_ascii_case(b"https"));
    let secure = if https { "; Secure" } else { "" };
    [(
        header::SET_COOKIE,
        format!("{COOKIE}={value}; Path=/; Max-Age={max_age}; HttpOnly; SameSite=Strict{secure}"),
    )]
}

const fn local(ip: IpAddr) -> bool {
    match ip {
        IpAddr::V4(v4) => v4.is_loopback() || v4.is_private() || v4.is_link_local(),
        IpAddr::V6(v6) => v6.is_loopback() || v6.is_unique_local() || v6.is_unicast_link_local(),
    }
}

fn now() -> u64 {
    SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .map_or(0, |d| d.as_secs())
}
