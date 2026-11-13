//! NNTP client for fetching yEnc article bodies from Usenet providers.

mod article;
mod bandwidth;
mod breaker;
pub mod catalog;
mod client;
mod connection;
mod downloads;
mod missing;
pub mod patch;
mod pool;
mod resolve;
mod stats;
mod tls;
mod usage;

use std::fmt;
use std::future::Future;
use std::pin::Pin;

use bytes::Bytes;

pub use article::{Body, MAX_DECODED_LEN};
pub use bandwidth::Bandwidth;
pub use client::{BodySink, Client, Lease, Provider, RepairKey, RepairLookup};
pub use connection::{Connection, ConnectionStats, Endpoint, Phase, Progress, Timing, TlsMode};
pub use downloads::Downloads;
pub use pool::{Pool, PoolConfig};
pub use stats::{LATENCY_BUCKETS, ProviderStats, Rescue, Workload, WorkloadStats};
pub use usage::Usage;

/// Provider login. `Debug` never prints the password.
#[derive(Clone)]
pub struct Credentials {
    pub username: String,
    pub password: String,
}

impl fmt::Debug for Credentials {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        f.debug_struct("Credentials")
            .field("username", &self.username)
            .finish_non_exhaustive()
    }
}

#[derive(Debug, thiserror::Error)]
pub enum Error {
    #[error("connection i/o failed")]
    Io(#[from] std::io::Error),
    #[error("timed out waiting for the server")]
    Timeout,
    #[error("server closed the connection")]
    Closed,
    #[error("malformed server response")]
    Protocol,
    #[error("article not found")]
    NotFound,
    /// Some provider was skipped or unreachable, so a miss is not conclusive.
    #[error("provider unavailable")]
    Unavailable,
    #[error("unexpected server response {code} {reply}")]
    Status { code: u16, reply: Box<str> },
    #[error("authentication rejected ({code} {reply})")]
    AuthRejected { code: u16, reply: Box<str> },
    #[error("invalid {0}")]
    InvalidArgument(&'static str),
    #[error("local repair unavailable: {0}")]
    Repair(&'static str),
    #[error("tls setup failed")]
    Tls(#[from] rustls::Error),
    #[error("article body exceeds {} bytes", article::MAX_DECODED_LEN)]
    TooLarge,
    #[error("invalid yEnc article: {0}")]
    Yenc(&'static str),
    #[error("yEnc CRC mismatch (expected {expected:08x}, got {actual:08x})")]
    CrcMismatch { expected: u32, actual: u32 },
}

/// A boxed [`Articles`] request.
pub type ArticleFuture<'a, T> = Pin<Box<dyn Future<Output = Result<T, Error>> + Send + 'a>>;

/// STAT and BODY under a caller's own budget, workload and reuse of bodies it already
/// holds, so one check runs the same within an import and on its own.
pub trait Articles: Send + Sync {
    fn stat<'a>(&'a self, id: &'a str) -> ArticleFuture<'a, ()>;
    /// The decoded body; `begin` as in [`Client::body_with`].
    fn body<'a>(&'a self, id: &'a str, begin: Option<u64>) -> ArticleFuture<'a, Bytes>;
    /// Original NZB and generation captured with the metadata or NZB being read.
    fn repair_source(&self) -> Option<(infinidysk_core::BlobId, i64)> {
        None
    }

    /// Verified local bytes only; unsupported access never falls back to an empty ID.
    fn omitted<'a>(
        &'a self,
        _key: &'a infinidysk_core::repair::Omitted,
    ) -> ArticleFuture<'a, Bytes> {
        Box::pin(async { Err(Error::NotFound) })
    }

    /// A metadata segment, including an omitted article with a trusted posted identity.
    fn read<'a>(
        &'a self,
        segment: &'a infinidysk_core::Segment,
        begin: Option<u64>,
        proof: Option<&'a infinidysk_core::Par2Proof>,
    ) -> ArticleFuture<'a, Bytes> {
        Box::pin(async move {
            if !segment.id.is_empty() {
                return self.body(&segment.id, begin).await;
            }
            let key = infinidysk_core::repair::Omitted::for_segment(
                self.repair_source(),
                segment,
                begin,
                proof,
            )
            .map_err(|_| Error::Repair("invalid omitted article layout"))?
            .ok_or(Error::NotFound)?;
            if key.range.len() > MAX_DECODED_LEN as u64 {
                return Err(Error::Repair(
                    "omitted article exceeds the decoded size limit",
                ));
            }
            let bytes = self.omitted(&key).await?;
            if bytes.len() as u64 != u64::from(segment.size) {
                return Err(Error::Repair("omitted article size differs from layout"));
            }
            Ok(bytes)
        })
    }
}

impl Error {
    /// Why a connect failed, worded for the person who set the provider up.
    #[must_use]
    pub fn connect_problem(&self) -> String {
        match self {
            Self::AuthRejected { code, reply } => {
                format!("The server rejected the username or password: {code} {reply}")
            }
            Self::Timeout => "The server did not answer in time.".to_owned(),
            Self::Closed => "The server closed the connection.".to_owned(),
            Self::Protocol => {
                "The server did not answer like a news server; check the port and TLS setting."
                    .to_owned()
            }
            Self::Status { code, reply } => {
                format!("The server refused the connection: {code} {reply}")
            }
            Self::Tls(error) => format!("The TLS handshake failed: {error}"),
            Self::Io(error) => match error
                .get_ref()
                .and_then(|inner| inner.downcast_ref::<rustls::Error>())
            {
                Some(tls) => format!("The TLS handshake failed: {tls}"),
                None => format!("Could not reach the server: {error}"),
            },
            Self::InvalidArgument(what) => format!("Invalid {what}."),
            other => other.to_string(),
        }
    }

    /// Whether the connection is still in a clean command state after this error. A 400
    /// is not: the server ends the session with it.
    #[must_use]
    pub const fn is_connection_reusable(&self) -> bool {
        match self {
            Self::Status { code, .. } => *code != 400,
            _ => matches!(
                self,
                Self::NotFound
                    | Self::InvalidArgument(_)
                    | Self::Yenc(_)
                    | Self::CrcMismatch { .. }
            ),
        }
    }
}
