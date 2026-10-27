//! The Octopushy agent.
//!
//! - [`enrollment`]: one-time token + CSR → certificate, pinned to the server's CA.
//! - [`connection`]: the mutual-TLS gRPC channel (hello, heartbeats, reconnects).
//! - [`attempts`]: running assignments, logs, cancellation, restart recovery.
//! - [`outbox`]: results stored on disk until acknowledged.
//! - [`executors`]: `proxmox` and `opentofu`.
//! - [`tools`]: running OpenTofu with podman, docker, or locally.
//! - [`masking`]: secret masking for logs and errors.
//! - [`renewal`], [`identity`], [`tls`], [`paths`]: credentials and state.

pub mod attempts;
pub mod cli;
pub mod connection;
pub mod enrollment;
pub mod executors;
pub mod fsutil;
pub mod identity;
pub mod masking;
pub mod outbox;
pub mod paths;
pub mod renewal;
pub mod run;
pub mod status;
pub mod tls;
pub mod tools;

/// This binary's version.
pub const VERSION: &str = env!("CARGO_PKG_VERSION");

pub mod managed_identity;

pub mod managed_reporting;
pub mod operator;
