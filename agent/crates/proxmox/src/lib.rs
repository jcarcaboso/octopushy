//! Octopushy's own client for the Proxmox VE API.
//!
//! It covers what Octopushy needs around OpenTofu: reading the installation
//! (version, cluster, nodes, guests), locating a VM, reading its addresses through
//! the QEMU guest agent, checking the token's privileges, and toggling VM
//! protection. It grows as needed.
//!
//! ```no_run
//! # async fn demo() -> Result<(), octopushy_proxmox::Error> {
//! use std::time::Duration;
//! use octopushy_proxmox::{ApiToken, Client, ClientConfig, Tls};
//!
//! let client = Client::new(ClientConfig {
//!     endpoint: "https://10.1.0.10:8006/".into(),
//!     token: ApiToken::new("octopushy@pve!agent=00000000-0000-0000-0000-000000000000")?,
//!     tls: Tls::Insecure,
//!     timeout: Duration::from_secs(30),
//! })?;
//! let version = client.version().await?;
//! println!("Proxmox VE {}", version.version);
//! # Ok(()) }
//! ```

mod client;
pub mod de;
mod error;
mod tls;
mod types;

pub use client::{ApiToken, Client, ClientConfig};
pub use error::{Error, Result};
pub use tls::{AcceptAnyCertificate, Tls};
pub use types::*;
