//! `octopushy agent run`: sets up the state directory, recovers from a restart,
//! and runs the agent channel.

use std::path::PathBuf;
use std::sync::Arc;
use std::time::Duration;

use anyhow::Result;
use tokio_util::sync::CancellationToken;

use crate::attempts::AttemptManager;
use crate::connection::{self, Agent};
use crate::executors::ExecutorConfig;
use crate::fsutil::ensure_private_dir;
use crate::identity;
use crate::outbox::Outbox;
use crate::paths::StatePaths;
use crate::tools::{RuntimeKind, ToolRuntime};

/// Options of `octopushy agent run`.
#[derive(Debug, Clone)]
pub struct RunOptions {
    pub state_dir: PathBuf,
    pub work_dir: Option<PathBuf>,
    pub tool_runtime: RuntimeKind,
    pub tofu_binary: PathBuf,
    pub container_binary: Option<PathBuf>,
    pub container_network: Option<String>,
    pub poll_interval: Duration,
}

impl RunOptions {
    pub fn new(state_dir: PathBuf) -> Self {
        Self {
            state_dir,
            work_dir: None,
            tool_runtime: RuntimeKind::Podman,
            tofu_binary: PathBuf::from("tofu"),
            container_binary: None,
            container_network: None,
            poll_interval: Duration::from_secs(5),
        }
    }
}

/// Runs the agent until `shutdown` is cancelled and running attempts are done.
pub async fn run(opts: RunOptions, shutdown: CancellationToken) -> Result<()> {
    let paths = StatePaths::new(&opts.state_dir);
    let id = identity::load(&paths)?;
    tracing::info!(
        agent_id = %id.record.agent_id, server = %id.record.server_url,
        state_dir = %paths.root.display(), "starting octopushy agent {}", crate::VERSION
    );

    let work_dir = opts
        .work_dir
        .clone()
        .unwrap_or_else(|| paths.default_work_dir());
    ensure_private_dir(&work_dir)?;
    let provider_cache = paths.provider_cache();
    ensure_private_dir(&provider_cache)?;
    let outbox = Outbox::open(paths.outbox())?;

    let runtime = ToolRuntime::new(
        opts.tool_runtime,
        opts.container_binary.clone(),
        opts.tofu_binary.clone(),
        opts.container_network.clone(),
    );
    match runtime.probe().await {
        Ok(v) => tracing::info!("tool runtime {}: {v}", opts.tool_runtime.as_str()),
        Err(e) => tracing::warn!(
            "tool runtime {} is not usable ({e:#}); opentofu attempts will fail",
            opts.tool_runtime.as_str()
        ),
    }
    let exec = Arc::new(ExecutorConfig {
        runtime,
        provider_cache,
        poll_interval: opts.poll_interval,
        proxmox_timeout: Duration::from_secs(30),
    });

    let manager = AttemptManager::new(work_dir, outbox, exec);
    let pending = manager.recover()?;
    if pending > 0 {
        tracing::info!("{pending} results wait in the outbox for acknowledgement");
    }

    let agent = Arc::new(Agent {
        paths,
        manager,
        tool_runtime: opts.tool_runtime,
    });
    connection::run(agent, shutdown).await
}
