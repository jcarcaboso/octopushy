//! Typed errors of the Proxmox client.
//!
//! No variant ever carries the API token: errors are built from the status line,
//! the response body, and transport errors, none of which contain request headers.

/// Errors returned by [`crate::Client`].
#[derive(Debug, thiserror::Error)]
pub enum Error {
    /// The client configuration is unusable (bad endpoint, token, or CA).
    #[error("invalid Proxmox client configuration: {0}")]
    Config(String),

    /// Proxmox rejected the API token (HTTP 401).
    #[error("Proxmox rejected the API token (HTTP 401)")]
    Unauthorized,

    /// The token is valid but lacks a privilege (HTTP 403).
    #[error("the API token lacks a required privilege (HTTP 403): {0}")]
    Forbidden(String),

    /// The resource does not exist (HTTP 404, or Proxmox's "does not exist" 500s).
    #[error("not found: {0}")]
    NotFound(String),

    /// The QEMU guest agent of the VM is not running or not configured.
    #[error("the QEMU guest agent is not available: {0}")]
    GuestAgentUnavailable(String),

    /// Any other non-success HTTP status.
    #[error("Proxmox returned HTTP {status}: {message}")]
    Status { status: u16, message: String },

    /// The request could not be sent or the response could not be read.
    #[error("could not reach Proxmox: {0}")]
    Transport(String),

    /// The response was not the JSON Proxmox should return.
    #[error("unexpected Proxmox response from {path}: {message}")]
    Decode { path: String, message: String },
}

impl Error {
    /// Whether retrying the same request later could succeed.
    pub fn is_transient(&self) -> bool {
        match self {
            Error::Transport(_) => true,
            Error::Status { status, .. } => *status >= 500,
            _ => false,
        }
    }

    pub(crate) fn transport(err: reqwest::Error) -> Self {
        // reqwest errors name the URL (which holds no secret) and wrap the cause
        // (connection refused, TLS failure, ...); include the whole chain.
        let mut message = err.to_string();
        let mut source = std::error::Error::source(&err);
        while let Some(cause) = source {
            let text = cause.to_string();
            if !message.contains(&text) {
                message.push_str(": ");
                message.push_str(&text);
            }
            source = cause.source();
        }
        if err.is_timeout() && !message.contains("timed out") {
            message.push_str(" (timed out)");
        }
        Error::Transport(message)
    }
}

/// Result type of this crate.
pub type Result<T, E = Error> = std::result::Result<T, E>;
