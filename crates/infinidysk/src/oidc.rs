//! Single sign-on through one OIDC provider: the authorization code flow with
//! PKCE, `state` and a nonce. The browser carries the flow in a signed cookie; nothing is
//! stored.

use std::future::Future;
use std::net::{IpAddr, SocketAddr};
use std::pin::Pin;
use std::sync::Arc;
use std::time::{Duration, Instant};

use axum::extract::rejection::QueryRejection;
use axum::extract::{ConnectInfo, Query, State};
use axum::http::{Extensions, HeaderMap, StatusCode, header};
use axum::response::{AppendHeaders, IntoResponse, Response};
use base64::Engine as _;
use base64::engine::general_purpose::URL_SAFE_NO_PAD;
use hmac::{Hmac, KeyInit as _, Mac as _};
use openidconnect::core::{
    CoreAuthenticationFlow, CoreClient, CoreClientAuthMethod, CoreErrorResponseType, CoreIdToken,
    CoreJwsSigningAlgorithm, CoreProviderMetadata,
};
use openidconnect::{
    AsyncHttpClient, AuthType, AuthorizationCode, ClaimsVerificationError, ClientId, ClientSecret,
    CsrfToken, DiscoveryError, IssuerUrl, Nonce, PkceCodeChallenge, PkceCodeVerifier, RedirectUrl,
    RequestTokenError, Scope, StandardErrorResponse, TokenResponse as _,
};
use serde::{Deserialize, Serialize};
use serde_json::{Map, Value};
use sha2::Sha256;

use crate::auth::Auth;
use crate::config::{self, Settings};

pub const LOGIN: &str = "/auth/oidc/login";
pub const CALLBACK: &str = "/auth/oidc/callback";
const FLOW_COOKIE: &str = "infinidysk_sso";
/// Marks a login already sent to the redirect URI's host, so a proxy that rewrites `Host`
/// cannot loop it.
const CANONICAL: &str = "canonical";
/// Time to finish signing in at the provider.
const FLOW_SECS: u64 = 10 * 60;
/// How long provider metadata and signing keys are reused.
const DISCOVERY_TTL: Duration = Duration::from_mins(1);
/// How long a failed discovery is reused, so an outage costs one attempt per interval.
const RETRY_AFTER_FAILURE: Duration = Duration::from_secs(10);
/// Bounds one discovery attempt (document and keys).
const DISCOVERY_TIMEOUT: Duration = Duration::from_secs(15);
/// Bounds a whole metadata lookup, waiting for the cache lock included.
const METADATA_DEADLINE: Duration = Duration::from_secs(20);
const HTTP_TIMEOUT: Duration = Duration::from_secs(10);
/// Longest username kept from a claim; the same bound as a local username.
const MAX_USER: usize = 1024;

/// Why SSO sign-in failed. Messages never contain provider text, tokens, codes or usernames.
#[derive(Clone, Debug, thiserror::Error)]
pub(crate) enum SsoError {
    #[error("SSO is turned off")]
    Off,
    #[error("the session key is missing")]
    NoSessionKey,
    #[error("the request has no usable Host header; set the public URL")]
    NoHost,
    #[error("the issuer or redirect URI is not a valid URL")]
    BadUrl,
    #[error(
        "the identity provider named a plain http:// endpoint; only https:// is allowed (http:// only on this machine)"
    )]
    Insecure,
    #[error("the identity provider did not answer in time")]
    Timeout,
    #[error("the identity provider could not be reached")]
    Unreachable,
    #[error("discovery failed: the identity provider answered HTTP {0}")]
    DiscoveryStatus(u16),
    #[error(
        "discovery failed: the document does not match the issuer; copy the issuer URL exactly, including any trailing slash"
    )]
    IssuerMismatch,
    #[error("discovery failed: the document is not valid OpenID Connect metadata")]
    DiscoveryInvalid,
    #[error("discovery stopped unexpectedly")]
    Internal,
    #[error("the callback URL is malformed")]
    BadCallback,
    #[error("the sign-in expired or started in another browser")]
    FlowMissing,
    #[error("the sign-in state does not match")]
    StateMismatch,
    #[error("the SSO settings changed during sign-in; sign in again")]
    SettingsChanged,
    #[error("the identity provider refused the sign-in ({0})")]
    Refused(&'static str),
    #[error("the identity provider sent no code")]
    NoCode,
    #[error("the identity provider has no token endpoint")]
    NoTokenEndpoint,
    #[error("the token request was refused ({0})")]
    TokenRefused(&'static str),
    #[error("the token response is not valid")]
    TokenInvalid,
    #[error("the token response has no ID token")]
    NoIdToken,
    #[error("the ID token {0}")]
    IdToken(&'static str),
    #[error("the ID token has no usable username claim")]
    NoUsername,
    #[error("the username claim is longer than 1024 bytes")]
    UsernameTooLong,
}

/// A standard OAuth 2.0 / OIDC error code; anything else is provider text.
fn oauth_code(code: &str) -> &'static str {
    const KNOWN: [&str; 14] = [
        "access_denied",
        "invalid_request",
        "unauthorized_client",
        "unsupported_response_type",
        "invalid_scope",
        "server_error",
        "temporarily_unavailable",
        "interaction_required",
        "login_required",
        "account_selection_required",
        "consent_required",
        "invalid_client",
        "invalid_grant",
        "unsupported_grant_type",
    ];
    KNOWN
        .into_iter()
        .find(|k| *k == code)
        .unwrap_or("unrecognized error code")
}

impl From<HttpError> for SsoError {
    fn from(error: HttpError) -> Self {
        match error {
            HttpError::Insecure => Self::Insecure,
            HttpError::Transport(e) if e.is_timeout() => Self::Timeout,
            HttpError::Transport(_) => Self::Unreachable,
        }
    }
}

impl From<DiscoveryError<HttpError>> for SsoError {
    fn from(error: DiscoveryError<HttpError>) -> Self {
        match error {
            DiscoveryError::Request(e) => e.into(),
            DiscoveryError::Response(status, ..) => Self::DiscoveryStatus(status.as_u16()),
            DiscoveryError::Validation(_) => Self::IssuerMismatch,
            DiscoveryError::UrlParse(_) => Self::BadUrl,
            _ => Self::DiscoveryInvalid,
        }
    }
}

type TokenError = RequestTokenError<HttpError, StandardErrorResponse<CoreErrorResponseType>>;

impl From<TokenError> for SsoError {
    fn from(error: TokenError) -> Self {
        match error {
            RequestTokenError::ServerResponse(r) => {
                Self::TokenRefused(oauth_code(r.error().as_ref()))
            }
            RequestTokenError::Request(e) => e.into(),
            RequestTokenError::Parse(..) | RequestTokenError::Other(_) => Self::TokenInvalid,
        }
    }
}

impl From<ClaimsVerificationError> for SsoError {
    fn from(error: ClaimsVerificationError) -> Self {
        Self::IdToken(match error {
            ClaimsVerificationError::Expired(_) => "has expired; check the server clock",
            ClaimsVerificationError::InvalidAudience(_) => "is for another client",
            ClaimsVerificationError::InvalidIssuer(_) => "is from another issuer",
            ClaimsVerificationError::InvalidNonce(_) => "nonce does not match",
            ClaimsVerificationError::SignatureVerification(_) => "signature is not valid",
            _ => "is not valid",
        })
    }
}

/// `https://`, or `http://` to this machine: the only transports SSO uses, for the issuer
/// and for every endpoint discovery names.
pub(crate) fn secure(scheme: &str, host: &str) -> bool {
    let host = host.trim_start_matches('[').trim_end_matches(']');
    scheme == "https"
        || (scheme == "http"
            && (host.eq_ignore_ascii_case("localhost")
                || host.parse::<IpAddr>().is_ok_and(|ip| ip.is_loopback())))
}

#[derive(Debug, thiserror::Error)]
enum HttpError {
    #[error("plain http:// endpoint")]
    Insecure,
    #[error("request failed")]
    Transport(#[from] reqwest::Error),
}

/// openidconnect's HTTP calls over our reqwest client.
async fn send(
    http: &reqwest::Client,
    request: openidconnect::HttpRequest,
) -> Result<openidconnect::HttpResponse, HttpError> {
    let uri = request.uri();
    if !secure(
        uri.scheme_str().unwrap_or_default(),
        uri.host().unwrap_or_default(),
    ) {
        return Err(HttpError::Insecure);
    }
    let (parts, body) = request.into_parts();
    let response = http
        .request(parts.method, parts.uri.to_string())
        .headers(parts.headers)
        .body(body)
        .send()
        .await?;
    let status = response.status();
    let headers = response.headers().clone();
    let mut out = openidconnect::HttpResponse::new(response.bytes().await?.to_vec());
    *out.status_mut() = status;
    *out.headers_mut() = headers;
    Ok(out)
}

/// [`send`] as openidconnect's client; a closure is not `Send` for every lifetime, which
/// `tokio::spawn` needs.
#[derive(Clone)]
struct Http(reqwest::Client);

impl<'c> AsyncHttpClient<'c> for Http {
    type Error = HttpError;
    type Future =
        Pin<Box<dyn Future<Output = Result<openidconnect::HttpResponse, HttpError>> + Send + 'c>>;

    fn call(&'c self, request: openidconnect::HttpRequest) -> Self::Future {
        Box::pin(send(&self.0, request))
    }
}

pub(crate) struct Sso {
    http: Http,
    discovered: Arc<tokio::sync::Mutex<Option<Discovered>>>,
}

struct Discovered {
    issuer: String,
    at: Instant,
    result: Result<CoreProviderMetadata, SsoError>,
}

impl Discovered {
    fn fresh(&self, issuer: &str) -> bool {
        let ttl = if self.result.is_ok() {
            DISCOVERY_TTL
        } else {
            RETRY_AFTER_FAILURE
        };
        self.issuer == issuer && self.at.elapsed() < ttl
    }
}

impl Sso {
    pub(crate) fn new() -> Result<Self, reqwest::Error> {
        let http = reqwest::Client::builder()
            .user_agent(format!("infinidysk/{}", crate::update::VERSION))
            // Following redirects would let a provider send us anywhere (SSRF).
            .redirect(reqwest::redirect::Policy::none())
            .timeout(HTTP_TIMEOUT)
            .build()?;
        Ok(Self {
            http: Http(http),
            discovered: Arc::default(),
        })
    }

    /// Provider metadata and keys, fetched at most once a minute per issuer, and retried
    /// at most every 10 seconds after a failure.
    #[allow(clippy::significant_drop_tightening)] // The lock is held across the attempt so concurrent sign-ins share it.
    async fn metadata(&self, issuer: &str) -> Result<CoreProviderMetadata, SsoError> {
        let lookup = async {
            let mut cached = Arc::clone(&self.discovered).lock_owned().await;
            if let Some(d) = cached.as_ref().filter(|d| d.fresh(issuer)) {
                return d.result.clone();
            }
            let (http, issuer) = (self.http.clone(), issuer.to_owned());
            // The attempt owns the lock and caches its result, so a sign-in dropped
            // mid-discovery does not leave the next one to start over.
            tokio::spawn(async move {
                let result = discover(&http, &issuer).await;
                *cached = Some(Discovered {
                    issuer,
                    at: Instant::now(),
                    result: result.clone(),
                });
                result
            })
            .await
            .unwrap_or(Err(SsoError::Internal))
        };
        tokio::time::timeout(METADATA_DEADLINE, lookup)
            .await
            .unwrap_or(Err(SsoError::Timeout))
    }

    /// The provider's sign-in URL for a new flow, and the flow cookie value.
    async fn start(
        &self,
        settings: &Settings,
        redirect: String,
    ) -> Result<(String, String), SsoError> {
        let (oidc, key) = (&settings.auth.oidc, &settings.auth.session_key.0);
        let metadata = self.metadata(&oidc.issuer).await?;
        let client = CoreClient::from_provider_metadata(
            metadata,
            ClientId::new(oidc.client_id.clone()),
            Some(ClientSecret::new(oidc.client_secret.0.clone())),
        )
        .set_redirect_uri(RedirectUrl::new(redirect.clone()).map_err(|_| SsoError::BadUrl)?);
        let (challenge, verifier) = PkceCodeChallenge::new_random_sha256();
        let mut request = client
            .authorize_url(
                CoreAuthenticationFlow::AuthorizationCode,
                CsrfToken::new_random,
                Nonce::new_random,
            )
            .set_pkce_challenge(challenge);
        // openidconnect always asks for `openid` itself.
        for scope in oidc
            .scopes
            .split(' ')
            .filter(|s| !s.is_empty() && *s != "openid")
        {
            request = request.add_scope(Scope::new(scope.to_owned()));
        }
        let (url, state, nonce) = request.url();
        if !secure(url.scheme(), url.host_str().unwrap_or_default()) {
            return Err(SsoError::Insecure);
        }
        let flow = Flow {
            state: state.into_secret(),
            nonce: nonce.secret().clone(),
            verifier: verifier.into_secret(),
            redirect_uri: redirect,
            settings: crate::auth::sso_settings(key, settings),
        };
        Ok((
            url.to_string(),
            flow.seal(key, crate::auth::now() + FLOW_SECS),
        ))
    }

    /// Exchanges `code`, checks the ID token and returns the username.
    async fn finish(
        &self,
        oidc: &config::Oidc,
        flow: Flow,
        code: String,
    ) -> Result<String, SsoError> {
        let metadata = self.metadata(&oidc.issuer).await?;
        let algs: Vec<CoreJwsSigningAlgorithm> = metadata
            .id_token_signing_alg_values_supported()
            .iter()
            .filter(|a| **a != CoreJwsSigningAlgorithm::None)
            .cloned()
            .collect();
        // 1.x sent the secret in the form body; keep that where the provider allows it.
        // Discovery says an omitted list means `client_secret_basic`.
        let auth_type = match metadata.token_endpoint_auth_methods_supported() {
            Some(m) if m.contains(&CoreClientAuthMethod::ClientSecretPost) => AuthType::RequestBody,
            _ => AuthType::BasicAuth,
        };
        let client = CoreClient::from_provider_metadata(
            metadata,
            ClientId::new(oidc.client_id.clone()),
            Some(ClientSecret::new(oidc.client_secret.0.clone())),
        )
        .set_redirect_uri(RedirectUrl::new(flow.redirect_uri).map_err(|_| SsoError::BadUrl)?)
        .set_auth_type(auth_type);
        let token = client
            .exchange_code(AuthorizationCode::new(code))
            .map_err(|_| SsoError::NoTokenEndpoint)?
            .set_pkce_verifier(PkceCodeVerifier::new(flow.verifier))
            .request_async(&self.http)
            .await?;
        let id_token = token.id_token().ok_or(SsoError::NoIdToken)?;
        let verifier = client.id_token_verifier().set_allowed_algs(algs);
        id_token.claims(&verifier, &Nonce::new(flow.nonce))?;
        let claims = payload(id_token).ok_or(SsoError::NoUsername)?;
        let user = username(&claims, &oidc.username_claim).ok_or(SsoError::NoUsername)?;
        if user.len() > MAX_USER {
            return Err(SsoError::UsernameTooLong);
        }
        Ok(user)
    }
}

/// One discovery attempt: the document and its keys.
async fn discover(http: &Http, issuer: &str) -> Result<CoreProviderMetadata, SsoError> {
    let url = IssuerUrl::new(issuer.to_owned()).map_err(|_| SsoError::BadUrl)?;
    tokio::time::timeout(
        DISCOVERY_TIMEOUT,
        CoreProviderMetadata::discover_async(url, http),
    )
    .await
    .map_or(Err(SsoError::Timeout), |r| r.map_err(SsoError::from))
}

/// The claims of an ID token whose signature was already checked. openidconnect's core
/// types drop custom claims, and `username_claim` can name any claim.
fn payload(token: &CoreIdToken) -> Option<Map<String, Value>> {
    let jwt = token.to_string();
    let claims = jwt.split('.').nth(1)?;
    serde_json::from_slice(&URL_SAFE_NO_PAD.decode(claims).ok()?).ok()
}

/// The configured claim, then `preferred_username`, `email` and `sub`: the first non-empty
/// string, as 1.x did.
fn username(claims: &Map<String, Value>, configured: &str) -> Option<String> {
    [configured, "preferred_username", "email", "sub"]
        .into_iter()
        .filter(|c| !c.is_empty())
        .find_map(|c| {
            claims
                .get(c)?
                .as_str()
                .map(str::trim)
                .filter(|v| !v.is_empty())
        })
        .map(str::to_owned)
}

/// Where the provider sends the browser back: the setting, else the public URL, else the
/// address this request came to. Forwarded headers count only from a trusted proxy.
fn redirect_uri(settings: &Settings, headers: &HeaderMap, peer: Option<IpAddr>) -> Option<String> {
    let oidc = &settings.auth.oidc;
    if !oidc.redirect_uri.is_empty() {
        return Some(oidc.redirect_uri.clone());
    }
    if !settings.network.public_url.is_empty() {
        return Some(format!("{}{CALLBACK}", settings.network.public_url));
    }
    let trusted = peer.is_some_and(|ip| {
        let ip = ip.to_canonical();
        settings
            .network
            .trusted_proxies
            .iter()
            .filter_map(|p| infinidysk_webdav::parse_proxy(p))
            .any(|net| net.contains(&ip))
    });
    let forwarded = |name: &str| {
        trusted
            .then(|| {
                headers
                    .get(name)?
                    .to_str()
                    .ok()?
                    .split(',')
                    .next()
                    .map(str::trim)
            })
            .flatten()
    };
    let scheme = forwarded("x-forwarded-proto")
        .filter(|s| matches!(*s, "http" | "https"))
        .unwrap_or("http");
    let host =
        forwarded("x-forwarded-host").or_else(|| headers.get(header::HOST)?.to_str().ok())?;
    if host.is_empty()
        || host.contains(|c: char| c.is_whitespace() || matches!(c, '/' | '\\' | '?' | '#' | '@'))
    {
        return None;
    }
    Some(format!("{scheme}://{host}{CALLBACK}"))
}

/// This login on the redirect URI's host, when the browser came in through another one:
/// the flow cookie must be set where the callback lands. `None` when the host matches or
/// the request names none.
fn elsewhere(redirect: &str, headers: &HeaderMap) -> Option<String> {
    let here: Vec<&str> = ["x-forwarded-host", "host"]
        .into_iter()
        .filter_map(|name| headers.get(name)?.to_str().ok()?.split(',').next())
        .map(str::trim)
        .collect();
    let url = openidconnect::url::Url::parse(redirect).ok()?;
    let host = url.host_str()?;
    let host = url
        .port()
        .map_or_else(|| host.to_owned(), |p| format!("{host}:{p}"));
    if here.is_empty() || here.iter().any(|h| h.eq_ignore_ascii_case(&host)) {
        return None;
    }
    Some(format!(
        "{}{LOGIN}?{CANONICAL}",
        url.origin().ascii_serialization()
    ))
}

#[derive(Debug, PartialEq, Eq, Serialize, Deserialize)]
struct Flow {
    state: String,
    nonce: String,
    verifier: String,
    redirect_uri: String,
    /// [`crate::auth::sso_settings`] when the flow started; the callback refuses any other.
    settings: String,
}

impl Flow {
    /// `<expiry>.<flow>.<mac>`, signed with the session key.
    fn seal(&self, key: &str, expiry: u64) -> String {
        let expiry = expiry.to_string();
        let body = URL_SAFE_NO_PAD
            .encode(serde_json::to_vec(self).expect("a struct of strings serializes"));
        let mac = flow_mac(key, &expiry, &body).finalize().into_bytes();
        format!("{expiry}.{body}.{}", URL_SAFE_NO_PAD.encode(mac))
    }

    fn open(key: &str, value: &str, now: u64) -> Option<Self> {
        let (expiry, rest) = value.split_once('.')?;
        let (body, mac) = rest.split_once('.')?;
        if key.is_empty() || expiry.parse::<u64>().ok()? <= now {
            return None;
        }
        flow_mac(key, expiry, body)
            .verify_slice(&URL_SAFE_NO_PAD.decode(mac).ok()?)
            .ok()?;
        serde_json::from_slice(&URL_SAFE_NO_PAD.decode(body).ok()?).ok()
    }
}

fn flow_mac(key: &str, expiry: &str, body: &str) -> Hmac<Sha256> {
    let mut mac =
        Hmac::<Sha256>::new_from_slice(key.as_bytes()).expect("HMAC accepts keys of any length");
    for part in ["sso-flow", "\0", expiry, "\0", body] {
        mac.update(part.as_bytes());
    }
    mac
}

fn flow_cookie(headers: &HeaderMap, value: &str, max_age: u64) -> String {
    // Lax: the provider's redirect back is a cross-site top-level GET.
    crate::auth::cookie_header(headers, FLOW_COOKIE, value, max_age, "Lax")
}

/// A redirect that is never cached, with `cookies` set.
fn found<const N: usize>(location: &str, cookies: [String; N]) -> Response {
    (
        StatusCode::FOUND,
        [
            (header::LOCATION, location.to_owned()),
            (header::CACHE_CONTROL, "no-store".to_owned()),
        ],
        AppendHeaders(cookies.map(|c| (header::SET_COOKIE, c))),
    )
        .into_response()
}

/// Starts SSO: redirects to the provider. Public.
pub(crate) async fn login(
    State(auth): State<Arc<Auth>>,
    uri: axum::http::Uri,
    headers: HeaderMap,
    extensions: Extensions,
) -> Response {
    let settings = auth.settings().await;
    if !settings.auth.oidc.enabled() {
        return found("/?sso=off", []);
    }
    let peer = extensions
        .get::<ConnectInfo<SocketAddr>>()
        .map(|ConnectInfo(a)| a.ip());
    let redirect = redirect_uri(&settings, &headers, peer);
    if uri.query() != Some(CANONICAL)
        && let Some(there) = redirect.as_deref().and_then(|r| elsewhere(r, &headers))
    {
        return found(&there, []);
    }
    let started = async {
        if settings.auth.session_key.0.is_empty() {
            return Err(SsoError::NoSessionKey);
        }
        let redirect = redirect.ok_or(SsoError::NoHost)?;
        auth.sso.start(&settings, redirect).await
    };
    match started.await {
        Ok((url, flow)) => found(&url, [flow_cookie(&headers, &flow, FLOW_SECS)]),
        Err(error) => {
            tracing::warn!(reason = %error, "SSO sign-in could not start");
            found("/?sso=failed", [])
        }
    }
}

/// The provider's answer. `error_description` is provider text and is never read.
#[derive(Deserialize)]
pub(crate) struct Answer {
    code: Option<String>,
    state: Option<String>,
    error: Option<String>,
}

/// The provider's redirect back: checks it and starts a session. Public. Every outcome,
/// including a query that does not parse, clears the flow cookie and redirects.
pub(crate) async fn callback(
    State(auth): State<Arc<Auth>>,
    headers: HeaderMap,
    query: Result<Query<Answer>, QueryRejection>,
) -> Response {
    let clear = flow_cookie(&headers, "", 0);
    let settings = auth.settings().await;
    let checked = match query {
        Ok(Query(answer)) => check(&auth, &settings, &headers, answer).await,
        Err(_) => Err(SsoError::BadCallback),
    };
    match checked {
        Ok((user, proven)) => {
            tracing::info!("signed in with SSO");
            let session = crate::auth::sso_session(&headers, &settings.auth, &user, &proven);
            found("/", [clear, session])
        }
        Err(error) => {
            tracing::warn!(reason = %error, "SSO sign-in failed");
            found("/?sso=failed", [clear])
        }
    }
}

/// The username, and the settings fingerprint the sign-in proved.
async fn check(
    auth: &Auth,
    settings: &Settings,
    headers: &HeaderMap,
    answer: Answer,
) -> Result<(String, String), SsoError> {
    let oidc = &settings.auth.oidc;
    let key = &settings.auth.session_key.0;
    if !oidc.enabled() {
        return Err(SsoError::Off);
    }
    if let Some(error) = answer.error {
        return Err(SsoError::Refused(oauth_code(&error)));
    }
    let flow = crate::auth::cookie(headers, FLOW_COOKIE)
        .and_then(|v| Flow::open(key, v, crate::auth::now()))
        .ok_or(SsoError::FlowMissing)?;
    let state = answer.state.unwrap_or_default();
    if !crate::sab::same(state.as_bytes(), flow.state.as_bytes()) {
        return Err(SsoError::StateMismatch);
    }
    // The exchange below uses the flow's redirect URI; a session must only vouch for
    // settings the whole sign-in ran under.
    let current = crate::auth::sso_settings(key, settings);
    if !crate::sab::same(flow.settings.as_bytes(), current.as_bytes()) {
        return Err(SsoError::SettingsChanged);
    }
    let code = answer.code.ok_or(SsoError::NoCode)?;
    let user = auth.sso.finish(oidc, flow, code).await?;
    Ok((user, current))
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn username_follows_the_configured_claim_then_the_1x_fallbacks() {
        let claims = |v: Value| v.as_object().cloned().expect("an object");
        let all = claims(serde_json::json!({
            "name_id": "carol",
            "preferred_username": " bob ",
            "email": "b@example.com",
            "sub": "s1",
        }));
        assert_eq!(username(&all, "name_id").as_deref(), Some("carol"));
        assert_eq!(username(&all, "missing").as_deref(), Some("bob"));
        let empty = claims(serde_json::json!({
            "preferred_username": "",
            "email": "b@example.com",
            "sub": "s1",
        }));
        assert_eq!(
            username(&empty, "missing").as_deref(),
            Some("b@example.com")
        );
        let sub = claims(serde_json::json!({ "sub": "s1" }));
        assert_eq!(username(&sub, "preferred_username").as_deref(), Some("s1"));
        let number = claims(serde_json::json!({ "preferred_username": 7, "sub": "s1" }));
        assert_eq!(username(&number, "").as_deref(), Some("s1"));
    }

    #[test]
    fn redirect_uri_prefers_the_setting_then_the_public_url_then_the_request() {
        let mut settings = Settings::default();
        let mut headers = HeaderMap::new();
        headers.insert(header::HOST, "dysk.lan:3000".parse().expect("valid"));
        headers.insert("x-forwarded-host", "evil.example".parse().expect("valid"));
        headers.insert("x-forwarded-proto", "https".parse().expect("valid"));
        let untrusted = Some("203.0.113.9".parse().expect("valid"));
        let trusted = Some("10.0.0.2".parse().expect("valid"));

        settings.auth.oidc.redirect_uri = "https://sso.example/cb".to_owned();
        settings.network.public_url = "https://dysk.example.com".to_owned();
        assert_eq!(
            redirect_uri(&settings, &headers, untrusted).as_deref(),
            Some("https://sso.example/cb")
        );
        settings.auth.oidc.redirect_uri.clear();
        assert_eq!(
            redirect_uri(&settings, &headers, untrusted).as_deref(),
            Some("https://dysk.example.com/auth/oidc/callback")
        );
        settings.network.public_url.clear();
        assert_eq!(
            redirect_uri(&settings, &headers, untrusted).as_deref(),
            Some("http://dysk.lan:3000/auth/oidc/callback")
        );
        assert_eq!(
            redirect_uri(&settings, &headers, trusted).as_deref(),
            Some("https://evil.example/auth/oidc/callback")
        );
        let mut slash = HeaderMap::new();
        slash.insert(header::HOST, "a/b".parse().expect("valid"));
        assert_eq!(redirect_uri(&settings, &slash, untrusted), None);
    }

    #[test]
    fn elsewhere_moves_only_a_login_on_another_host() {
        let headers = |pairs: &[(&'static str, &str)]| {
            let mut h = HeaderMap::new();
            for (k, v) in pairs {
                h.insert(*k, v.parse().expect("valid"));
            }
            h
        };
        let redirect = "https://dysk.example.com:8443/auth/oidc/callback";
        assert_eq!(
            elsewhere(redirect, &headers(&[("host", "10.0.0.5:3000")])).as_deref(),
            Some("https://dysk.example.com:8443/auth/oidc/login?canonical")
        );
        for same in [
            headers(&[("host", "DYSK.example.com:8443")]),
            headers(&[
                ("host", "app:3000"),
                ("x-forwarded-host", "dysk.example.com:8443"),
            ]),
            HeaderMap::new(),
        ] {
            assert_eq!(elsewhere(redirect, &same), None);
        }
    }

    #[test]
    fn flow_cookie_rejects_tampering_and_expiry() {
        let flow = Flow {
            state: "s".to_owned(),
            nonce: "n".to_owned(),
            verifier: "v".to_owned(),
            redirect_uri: "http://dysk.test/auth/oidc/callback".to_owned(),
            settings: "f".to_owned(),
        };
        let now = 1_000;
        let (key, other) = (
            uuid::Uuid::now_v7().to_string(),
            uuid::Uuid::now_v7().to_string(),
        );
        let sealed = flow.seal(&key, now + 60);
        assert_eq!(Flow::open(&key, &sealed, now), Some(flow));

        let (expiry, rest) = sealed.split_once('.').expect("three parts");
        let (body, mac) = rest.split_once('.').expect("three parts");
        let mut tampered = body.as_bytes().to_vec();
        tampered[3] = if tampered[3] == b'A' { b'B' } else { b'A' };
        let tampered = format!(
            "{expiry}.{}.{mac}",
            String::from_utf8(tampered).expect("ascii")
        );
        assert_eq!(Flow::open(&key, &tampered, now), None);
        assert_eq!(Flow::open(&other, &sealed, now), None);
        assert_eq!(Flow::open(&key, &sealed, now + 60), None);
    }

    #[test]
    fn secure_allows_https_and_loopback_http_only() {
        assert!(secure("https", "auth.example.com"));
        assert!(secure("https", "10.0.0.5"));
        for host in ["localhost", "127.0.0.1", "[::1]"] {
            assert!(secure("http", host), "{host}");
        }
        for host in ["10.0.0.5", "auth.example.com"] {
            assert!(!secure("http", host), "{host}");
        }
        assert!(!secure("ftp", "localhost"));
    }

    #[tokio::test]
    async fn send_refuses_plain_http_to_other_hosts() {
        let http = Sso::new().expect("client builds").http.0;
        let request = openidconnect::HttpRequest::new(Vec::new());
        let (mut parts, body) = request.into_parts();
        parts.method = axum::http::Method::POST;
        parts.uri = "http://192.0.2.1/token".parse().expect("valid");
        let request = openidconnect::HttpRequest::from_parts(parts, body);
        assert!(matches!(
            send(&http, request).await,
            Err(HttpError::Insecure)
        ));
    }

    #[test]
    fn reasons_never_quote_the_provider() {
        let reasons = [
            SsoError::from(DiscoveryError::<HttpError>::Validation(
                "PROVIDER-TEXT".into(),
            )),
            SsoError::from(ClaimsVerificationError::InvalidAudience(
                "PROVIDER-TEXT".into(),
            )),
            SsoError::Refused(oauth_code("PROVIDER-TEXT")),
        ];
        for reason in reasons {
            assert!(!reason.to_string().contains("PROVIDER-TEXT"), "{reason}");
        }
        assert_eq!(oauth_code("access_denied"), "access_denied");
    }
}
