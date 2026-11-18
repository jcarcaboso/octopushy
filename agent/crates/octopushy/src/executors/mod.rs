//! Executors: the code that carries out an assignment's `executor`/`action`.
//!
//! Inputs and outputs follow `contracts/agent/v1/executors.md`.

pub mod ansible;
#[cfg(test)]
mod contract_inputs;
pub mod nix;
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
use crate::images::ServerLink;
use crate::tools::{self, ToolRuntime};

/// Executors this binary provides, as announced in `Hello`.
pub const EXECUTORS: &[&str] = &["opentofu", "proxmox", "ansible", "nix"];

/// Actions that change infrastructure. They are never cancelled, and an
/// interruption by a restart makes their outcome uncertain. Only for an assignment
/// whose `mutating` is absent, from an older server: the server's flag decides otherwise.
pub fn is_mutating(executor: &str, action: &str) -> bool {
    matches!(
        (executor, action),
        ("opentofu", "apply") | ("proxmox", "set-protection") | ("ansible", "run")
    ) || cfg!(test) && matches!((executor, action), ("test", "mutate") | ("test", "change"))
}

/// Actions that are a change whatever the server says: nothing makes `tofu apply` or the
/// protection PUT a read. A playbook is not listed, since one that only verifies is a read.
pub fn always_changes(executor: &str, action: &str) -> bool {
    matches!(
        (executor, action),
        ("opentofu", "apply") | ("proxmox", "set-protection")
    )
}

/// Actions whose every path calls [`ExecContext::begin_change`] before the first thing that
/// changes anything: `tofu apply`, each playbook run, the protection PUT. Only for these does a
/// restart before that call mean nothing changed. An action that is not listed, or whose marker
/// predates this list, stays uncertain when a restart interrupts it.
pub const MARKS_CHANGE: &[(&str, &str)] = &[
    ("opentofu", "apply"),
    ("ansible", "run"),
    ("proxmox", "set-protection"),
    #[cfg(test)]
    ("test", "change"),
];

/// Whether `executor`/`action` is in [`MARKS_CHANGE`].
pub fn marks_change(executor: &str, action: &str) -> bool {
    MARKS_CHANGE.contains(&(executor, action))
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
    /// The wait before a step that is safe to repeat is tried again, doubled for each later try.
    pub retry_delay: Duration,
    /// Where images go to and come from.
    pub server: ServerLink,
    /// The `nix` executor's store and its limit.
    pub nix: nix::NixSettings,
}

/// How many times a step that is safe to repeat is tried: `tofu init`, an image pull, a Proxmox
/// GET and the protection PUT. Nothing that changes infrastructure is ever tried again.
pub const SAFE_TRIES: u32 = 3;

impl ExecutorConfig {
    /// The wait before try `next` (from 2) of a step that is safe to repeat.
    pub fn retry_wait(&self, next: u32) -> Duration {
        self.retry_delay
            .saturating_mul(1 << next.saturating_sub(2).min(16))
    }
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
    /// Whether the attempt is a change, as the agent decided when it accepted it.
    pub mutating: bool,
    /// Where a running apply leaves its state for the connection to send.
    pub snapshots: opentofu::state::Snapshots,
    /// The user the attempt's tools run as, when each attempt has its own; see `tools::users`.
    pub tool_user: Option<tools::users::ToolUser>,
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

    /// Logs why `what` failed and that it is tried again, then waits before try `next` of
    /// [`SAFE_TRIES`]. Only for steps that change nothing. Fails as [`ExecContext::sleep`] does.
    pub async fn before_retry(
        &self,
        cfg: &ExecutorConfig,
        what: &str,
        why: &str,
        next: u32,
    ) -> Result<(), Failure> {
        let delay = cfg.retry_wait(next);
        self.log.agent(format!(
            "{what} failed: {why}; trying again in {:.1}s (try {next} of {SAFE_TRIES})",
            delay.as_secs_f64()
        ));
        self.sleep(delay).await
    }

    /// Records, durably, that the first change is about to start. Call right before sending or
    /// spawning it. When this fails, the change must not start. Again later does nothing.
    pub fn begin_change(&self) -> Result<(), Failure> {
        crate::attempts::workspace::mark_change_started(&self.workspace, self.mutating).map_err(
            |e| {
                Failure::msg(format!(
                    "could not record that the change starts, so it was not started: {e:#}"
                ))
            },
        )
    }

    pub fn check_cancelled(&self) -> Result<(), Failure> {
        if self.cancel.is_cancelled() {
            Err(Failure::Cancelled)
        } else {
            Ok(())
        }
    }
}

/// The tool's image, pulled in a step of its own before the tool first runs, so a registry
/// failure is named as one and tried again. Only for a container runtime. The pull gives up at
/// `deadline`.
pub async fn pull_image(
    cfg: &ExecutorConfig,
    ctx: &ExecContext,
    image: &str,
    deadline: tokio::time::Instant,
) -> Result<(), Failure> {
    if !cfg.runtime.is_container() || image.trim().is_empty() {
        return Ok(());
    }
    let mut tried = 1;
    loop {
        let pulled =
            tokio::time::timeout_at(deadline, cfg.runtime.pull(image, &ctx.log, &ctx.cancel))
                .await
                .map_err(|_| Failure::msg(format!("pulling the image {image} timed out")))?;
        let why = match pulled? {
            tools::Pull::Done => return Ok(()),
            tools::Pull::Cancelled => return Err(Failure::Cancelled),
            tools::Pull::Failed(why) => why,
        };
        if tried >= SAFE_TRIES {
            return Err(Failure::msg(format!(
                "pulling the image {image} failed {SAFE_TRIES} times: {why}"
            )));
        }
        tried += 1;
        ctx.before_retry(cfg, &format!("pulling the image {image}"), &why, tried)
            .await?;
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

    /// What went wrong, for people to read.
    pub fn message(&self) -> &str {
        match self {
            Failure::Uncertain(message) | Failure::Failed { message, .. } => message,
            Failure::Cancelled => "cancelled",
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
        "opentofu" => opentofu::run(cfg, ctx).await,
        "ansible" => ansible::run(cfg, ctx).await,
        "nix" => nix::run(cfg, ctx).await,
        #[cfg(test)]
        "test" => test_executor(ctx).await,
        other => Err(Failure::msg(format!(
            "unknown executor {other:?}; this agent provides {}",
            EXECUTORS.join(", ")
        ))),
    }
}

/// An executor for unit tests: `sleep` and `mutate` wait for `input.ms`
/// milliseconds (or a cancel), `echo` returns its input. `change` waits for
/// `input.before` milliseconds, records the start of its change, then waits for `input.ms`.
#[cfg(test)]
async fn test_executor(ctx: &ExecContext) -> ExecResult {
    #[derive(serde::Deserialize)]
    struct Input {
        #[serde(default)]
        ms: u64,
        #[serde(default)]
        before: u64,
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
        "change" => {
            tokio::time::sleep(Duration::from_millis(input.before)).await;
            ctx.begin_change()?;
            tokio::time::sleep(Duration::from_millis(input.ms)).await;
            Ok(Success::outputs(serde_json::json!({ "changed": true })))
        }
        "stopped" => {
            // Says it cannot tell when it is stopped, like a playbook.
            tokio::select! {
                _ = tokio::time::sleep(Duration::from_millis(input.ms)) => {
                    Ok(Success::outputs(serde_json::json!({ "ran": true })))
                }
                _ = ctx.cancel.cancelled() => Err(Failure::Uncertain("stopped midway".into())),
            }
        }
        "fail" => Err(Failure::msg(format!(
            "failed with secret {}",
            ctx.secrets.get("s").unwrap_or("none")
        ))),
        _ => Err(Failure::msg("unknown test action")),
    }
}
