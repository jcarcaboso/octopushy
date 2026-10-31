//! Executors: the code that carries out an assignment's `executor`/`action`.
//!
//! Inputs and outputs follow `contracts/agent/v1/executors.md`.

pub mod guest;
mod managed_update;
pub mod managed_worker;
pub mod nixos_rootfs;
pub mod opentofu;
pub mod proxmox;

use std::collections::HashMap;
use std::path::PathBuf;
use std::time::Duration;

use octopushy_proto::v1::Artifact;
use serde::de::DeserializeOwned;
use serde_json::Value;
use tokio_util::sync::CancellationToken;

use crate::attempts::logs::AttemptLogger;
use crate::tools::ToolRuntime;

/// Executors this binary provides, as announced in `Hello`.
pub const EXECUTORS: &[&str] = &[
    "opentofu",
    "proxmox",
    "nixos-rootfs",
    "guest",
    "managed-worker",
];

/// Actions that change infrastructure. They are never cancelled, and an
/// interruption by a restart makes their outcome uncertain.
pub fn is_mutating(executor: &str, action: &str) -> bool {
    matches!(
        (executor, action),
        ("opentofu", "apply")
            | ("proxmox", "set-protection")
            | ("nixos-rootfs", "grow")
            | ("guest", "install-disk-reporter")
            | ("managed-worker", "prepare" | "install" | "update-os")
    ) || cfg!(test) && (executor, action) == ("test", "mutate")
}

/// Settings shared by all attempts.
#[derive(Debug)]
pub struct ExecutorConfig {
    pub runtime: ToolRuntime,
    /// Persistent OpenTofu provider cache (`TF_PLUGIN_CACHE_DIR`).
    pub provider_cache: PathBuf,
    /// How often `verify-guest` and `verify-absent` poll.
    pub poll_interval: Duration,
    /// Timeout of each Proxmox API request.
    pub proxmox_timeout: Duration,
}

/// The secrets of one attempt, by name. `Debug` shows only the names.
pub struct Secrets(HashMap<String, String>);

impl Secrets {
    pub fn new(map: HashMap<String, String>) -> Self {
        Self(map)
    }

    pub fn get(&self, name: &str) -> Result<&str, Failure> {
        self.0.get(name).map(String::as_str).ok_or_else(|| {
            Failure::msg(format!(
                "the secret {name:?} was not provided with the assignment"
            ))
        })
    }
}

impl std::fmt::Debug for Secrets {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        let mut names: Vec<&String> = self.0.keys().collect();
        names.sort();
        write!(f, "Secrets({names:?})")
    }
}

/// Everything an executor gets for one attempt.
pub struct ExecContext {
    pub attempt_id: String,
    pub action: String,
    pub input_json: String,
    pub secrets: Secrets,
    pub files_tar_gz: Vec<u8>,
    pub artifacts: Vec<Artifact>,
    /// The attempt's private directory.
    pub workspace: PathBuf,
    pub log: AttemptLogger,
    pub cancel: CancellationToken,
    /// Where a running apply leaves its state for the connection to send.
    pub snapshots: opentofu::state::Snapshots,
}

impl ExecContext {
    /// Parses `input_json`; unknown fields are ignored.
    pub fn input<T: DeserializeOwned>(&self) -> Result<T, Failure> {
        serde_json::from_str(&self.input_json)
            .map_err(|e| Failure::msg(format!("invalid input for {}: {e}", self.action)))
    }

    /// Sleeps unless cancelled first.
    pub async fn sleep(&self, d: Duration) -> Result<(), Failure> {
        tokio::select! {
            _ = tokio::time::sleep(d) => Ok(()),
            _ = self.cancel.cancelled() => Err(Failure::Cancelled),
        }
    }

    pub fn check_cancelled(&self) -> Result<(), Failure> {
        if self.cancel.is_cancelled() {
            Err(Failure::Cancelled)
        } else {
            Ok(())
        }
    }
}

/// A successful attempt.
#[derive(Debug)]
pub struct Success {
    pub outputs: Value,
    pub artifacts: Vec<Artifact>,
}

impl Success {
    pub fn outputs(outputs: Value) -> Self {
        Self {
            outputs,
            artifacts: Vec::new(),
        }
    }
}

/// A failed or cancelled attempt. Messages are masked before they leave the
/// agent.
#[derive(Debug)]
pub enum Failure {
    /// A remote mutation may still be running; it must not be automatically retried.
    Uncertain(String),
    Failed {
        message: String,
        /// Outputs to report even though the attempt failed.
        outputs: Option<Value>,
    },
    Cancelled,
}

impl Failure {
    pub fn msg(message: impl Into<String>) -> Self {
        Failure::Failed {
            message: message.into(),
            outputs: None,
        }
    }

    pub fn with_outputs(message: impl Into<String>, outputs: Value) -> Self {
        Failure::Failed {
            message: message.into(),
            outputs: Some(outputs),
        }
    }
}

impl From<anyhow::Error> for Failure {
    fn from(e: anyhow::Error) -> Self {
        Failure::msg(format!("{e:#}"))
    }
}

impl From<octopushy_proxmox::Error> for Failure {
    fn from(e: octopushy_proxmox::Error) -> Self {
        Failure::msg(e.to_string())
    }
}

pub type ExecResult = Result<Success, Failure>;

/// Runs `executor` for `ctx.action`.
pub async fn run(cfg: &ExecutorConfig, executor: &str, ctx: &ExecContext) -> ExecResult {
    match executor {
        "proxmox" => proxmox::run(cfg, ctx).await,
        "guest" => guest::run(cfg, ctx).await,
        "opentofu" => opentofu::run(cfg, ctx).await,
        "managed-worker" => managed_worker::run(cfg, ctx).await,
        "nixos-rootfs" => nixos_rootfs::run(cfg, ctx).await,
        #[cfg(test)]
        "test" => test_executor(ctx).await,
        other => Err(Failure::msg(format!(
            "unknown executor {other:?}; this agent provides {}",
            EXECUTORS.join(", ")
        ))),
    }
}

/// An executor for unit tests: `sleep` and `mutate` wait for `input.ms`
/// milliseconds (or a cancel), `echo` returns its input.
#[cfg(test)]
async fn test_executor(ctx: &ExecContext) -> ExecResult {
    #[derive(serde::Deserialize)]
    struct Input {
        #[serde(default)]
        ms: u64,
    }
    let input: Input = ctx.input()?;
    match ctx.action.as_str() {
        "echo" => {
            ctx.log.stdout(&ctx.input_json);
            Ok(Success::outputs(
                serde_json::from_str(&ctx.input_json).unwrap(),
            ))
        }
        "sleep" => {
            ctx.sleep(Duration::from_millis(input.ms)).await?;
            Ok(Success::outputs(serde_json::json!({ "slept": input.ms })))
        }
        "mutate" => {
            // Ignores cancellation, like an apply.
            tokio::time::sleep(Duration::from_millis(input.ms)).await;
            Ok(Success::outputs(serde_json::json!({ "mutated": true })))
        }
        "fail" => Err(Failure::msg(format!(
            "failed with secret {}",
            ctx.secrets.get("s").unwrap_or("none")
        ))),
        _ => Err(Failure::msg("unknown test action")),
    }
}
