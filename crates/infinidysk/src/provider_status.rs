//! Provider connection checks: a one-off login with unsaved settings, and whether each
//! configured provider can connect right now.

use std::sync::Arc;
use std::time::Duration;

use axum::Json;
use axum::extract::State;
use infinidysk_nntp::{Connection, Error as NntpError};
use serde::Serialize;
use utoipa::ToSchema;

use crate::admin::{Admin, ApiError, Fail};
use crate::app;
use crate::config;

/// A check gives up after this long in total, whatever each step's own timeout.
const CHECK_TIMEOUT: Duration = Duration::from_secs(15);

#[derive(Serialize, ToSchema)]
pub struct ProviderCheck {
    /// Why the server could not be reached or refused the login; absent when it worked.
    error: Option<String>,
}

/// Opens one connection with a provider's settings, saved or not, and logs in. A masked
/// password is the saved password of the provider with the same id.
#[utoipa::path(post, path = "/api/v1/providers/check", tag = "settings", request_body = config::Provider,
    responses((status = 200, body = ProviderCheck), (status = 400, body = ApiError)))]
pub async fn post_provider_check(
    State(admin): State<Arc<Admin>>,
    Json(mut provider): Json<config::Provider>,
) -> Result<Json<ProviderCheck>, Fail> {
    if provider.password.0 == config::MASK {
        let saved = admin
            .config
            .read()
            .await
            .settings()
            .providers
            .iter()
            .find(|p| p.id == provider.id)
            .map(|p| p.password.clone())
            .ok_or_else(|| Fail::Bad("no saved password for this provider".to_owned()))?;
        provider.password.restore(&saved);
    }
    provider
        .resolve()
        .map_err(|error| Fail::Bad(error.to_string()))?;
    let endpoint = app::endpoint(&provider).map_err(|error| Fail::Bad(error.connect_problem()))?;
    let result = tokio::time::timeout(CHECK_TIMEOUT, Connection::connect(&endpoint))
        .await
        .unwrap_or(Err(NntpError::Timeout));
    Ok(Json(match result {
        Ok(_) => ProviderCheck { error: None },
        Err(error) => {
            tracing::warn!(provider = %provider.name, reason = %error, "provider connection check failed");
            ProviderCheck {
                error: Some(error.connect_problem()),
            }
        }
    }))
}

#[derive(Clone, Copy, Debug, PartialEq, Eq, Serialize, ToSchema)]
#[serde(rename_all = "snake_case")]
pub enum ConnectionState {
    /// A connection opened and the last connect did not fail.
    Connected,
    /// Nothing has needed a connection since the provider was saved.
    Waiting,
    /// Requests failed and the circuit breaker paused the provider.
    Paused,
    /// No connection could be opened.
    Failing,
}

#[derive(Serialize, ToSchema)]
pub struct ProviderStatus {
    /// The provider's settings id.
    id: String,
    state: ConnectionState,
    /// Why connecting fails, when `failing`.
    error: Option<String>,
    /// Pause left, when `paused`.
    open_for_ms: Option<u64>,
}

/// Connection state of each enabled provider.
#[utoipa::path(get, path = "/api/v1/providers/status", tag = "settings",
    responses((status = 200, body = Vec<ProviderStatus>)))]
pub async fn get_provider_status(State(admin): State<Arc<Admin>>) -> Json<Vec<ProviderStatus>> {
    Json(
        admin
            .client
            .stats()
            .into_iter()
            .map(|p| {
                let state = if p.connect_error.is_some() {
                    ConnectionState::Failing
                } else if p.open_for.is_some() {
                    ConnectionState::Paused
                } else if p.connected {
                    ConnectionState::Connected
                } else {
                    ConnectionState::Waiting
                };
                ProviderStatus {
                    id: p.key,
                    state,
                    error: p.connect_error.map(|e| e.to_string()),
                    open_for_ms: p
                        .open_for
                        .map(|d| u64::try_from(d.as_millis()).unwrap_or(u64::MAX)),
                }
            })
            .collect(),
    )
}
