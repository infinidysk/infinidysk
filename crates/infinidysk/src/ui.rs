//! The embedded web UI. Browsers `GET` it from any path the APIs and `WebDAV` do not own;
//! unknown paths get `index.html` so client-side routes survive a reload.

use axum::extract::Request;
use axum::http::{HeaderValue, Method, StatusCode, header};
use axum::middleware::Next;
use axum::response::{IntoResponse, Response};
use rust_embed::RustEmbed;
use std::sync::LazyLock;

#[derive(RustEmbed)]
#[folder = "../../ui/dist"]
struct Assets;

/// Paths served by the SAB/admin APIs, `WebDAV` and the container health check.
const BACKEND: [&str; 9] = [
    "/api",
    "/auth/oidc",
    "/metrics",
    "/view",
    "/.ids",
    "/nzbs",
    "/content",
    "/completed-symlinks",
    crate::healthcheck::PATH,
];
const CSP: &str = "default-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; \
                   frame-ancestors 'none'; base-uri 'none'; form-action 'self'";

/// The fingerprinted entry script `index.html` loads; it changes with every UI build.
pub fn entry() -> Option<&'static str> {
    static ENTRY: LazyLock<Option<String>> = LazyLock::new(|| {
        let index = Assets::get("index.html")?;
        let html = std::str::from_utf8(&index.data).ok()?;
        // The module script is the only element with a `src`.
        let start = html.find("src=\"")? + 5;
        let len = html[start..].find('"')?;
        Some(html[start..start + len].to_owned())
    });
    ENTRY.as_deref()
}

fn backend(path: &str) -> bool {
    BACKEND.iter().any(|prefix| {
        path.strip_prefix(prefix)
            .is_some_and(|rest| rest.is_empty() || rest.starts_with('/'))
    })
}

/// Answers UI requests; passes everything else on.
pub async fn serve(request: Request, next: Next) -> Response {
    let method = request.method();
    let path = request.uri().path();
    if (method != Method::GET && method != Method::HEAD) || backend(path) {
        return next.run(request).await;
    }
    let name = path.trim_start_matches('/');
    let (file, cache) = match Assets::get(name).filter(|_| !name.is_empty()) {
        // Vite fingerprints everything under `assets/`.
        Some(file) if name.starts_with("assets/") => (file, "public, max-age=31536000, immutable"),
        Some(file) => (file, "no-cache"),
        None if name.starts_with("assets/") => return StatusCode::NOT_FOUND.into_response(),
        None => match Assets::get("index.html") {
            Some(file) => (file, "no-cache"),
            None => return StatusCode::NOT_FOUND.into_response(),
        },
    };
    let mime = file.metadata.mimetype().to_owned();
    let mut response = (
        [
            (header::CONTENT_TYPE, mime.as_str()),
            (header::CACHE_CONTROL, cache),
            (header::X_CONTENT_TYPE_OPTIONS, "nosniff"),
            (header::REFERRER_POLICY, "same-origin"),
        ],
        file.data,
    )
        .into_response();
    if mime.starts_with("text/html") {
        let headers = response.headers_mut();
        headers.insert(
            header::CONTENT_SECURITY_POLICY,
            HeaderValue::from_static(CSP),
        );
        headers.insert(header::X_FRAME_OPTIONS, HeaderValue::from_static("DENY"));
    }
    response
}

#[cfg(test)]
mod tests {
    #[test]
    fn backend_prefixes_match_whole_segments() {
        assert!(super::backend("/api"));
        assert!(super::backend("/content/tv/x.mkv"));
        assert!(super::backend("/auth/oidc/callback"));
        assert!(!super::backend("/apifoo"));
        assert!(!super::backend("/"));
        assert!(!super::backend("/queue"));
    }
}
