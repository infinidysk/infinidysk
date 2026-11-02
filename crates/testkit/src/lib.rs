//! Test support: a loopback NNTP server and a deterministic yEnc corpus.
//!
//! Dev-dependency only. Never link this into the product binary.

mod corpus;
mod server;

pub use corpus::{Article, Corpus, post};
pub use server::{Impairment, LoopbackServer};
