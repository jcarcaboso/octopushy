//! `octopushy agent run`: sets up the state directory, recovers from a restart,
//! and runs the agent channel.

use std::path::PathBuf;
use std::sync::Arc;
use std::time::Duration;

use anyhow::{Context, Result};
use tokio_util::sync::CancellationToken;

use crate::attempts::AttemptManager;
use crate::connection::{self, Agent};
use crate::executors::opentofu::state;
use crate::executors::{EXECUTORS, ExecutorConfig};
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
    let (paths, work_dir, provider_cache) = directories(&opts)?;
    let id = identity::load(&paths)?;
    tracing::info!(
        agent_id = %id.record.agent_id, server = %id.record.server_url,
        state_dir = %paths.root.display(), "starting octopushy agent {}", crate::VERSION
    );

    let outbox = Outbox::open(paths.outbox())?;

    let runtime = ToolRuntime::new(
        opts.tool_runtime,
        opts.container_binary.clone(),
        opts.tofu_binary.clone(),
        opts.container_network.clone(),
    );
    let tools_available = match runtime.probe().await {
        Ok(v) => {
            tracing::info!("tool runtime {}: {v}", opts.tool_runtime.as_str());
            true
        }
        Err(e) => {
            tracing::warn!(
                "tool runtime {} is not usable ({e:#}); not advertising opentofu (restart after fixing it)",
                opts.tool_runtime.as_str()
            );
            false
        }
    };
    let executors = EXECUTORS
        .iter()
        .filter(|&&name| tools_available || name != "opentofu")
        .map(|&name| name.to_owned())
        .collect();
    let capabilities = tools_available
        .then(|| state::CAPABILITY.to_owned())
        .into_iter()
        .collect();
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
        executors,
        capabilities,
    });
    connection::run(agent, shutdown).await
}

/// Container mounts and OpenTofu's canonical repository root must use the same
/// absolute paths, including when state, work or cache are symlinked.
fn directories(opts: &RunOptions) -> Result<(StatePaths, PathBuf, PathBuf)> {
    let paths = StatePaths::new(
        opts.state_dir
            .canonicalize()
            .context("resolving the enrolled state directory")?,
    );
    let work = opts
        .work_dir
        .clone()
        .unwrap_or_else(|| paths.default_work_dir());
    let cache = paths.provider_cache();
    ensure_private_dir(&work)?;
    ensure_private_dir(&cache)?;
    Ok((paths, work.canonicalize()?, cache.canonicalize()?))
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::fs;
    use std::os::unix::fs::symlink;

    #[test]
    fn relative_and_symlinked_directories_use_canonical_container_paths() {
        // A relative temporary path exercises the bug without changing process cwd.
        let temp = tempfile::tempdir_in(".").unwrap();
        let real = temp.path().canonicalize().unwrap();
        let relative = real
            .strip_prefix(std::env::current_dir().unwrap().canonicalize().unwrap())
            .unwrap()
            .to_path_buf();
        fs::create_dir(real.join("state")).unwrap();
        fs::create_dir(real.join("work")).unwrap();
        fs::create_dir(real.join("cache")).unwrap();
        symlink(real.join("state"), temp.path().join("state-link")).unwrap();
        symlink(real.join("work"), real.join("state/work")).unwrap();
        symlink(real.join("cache"), real.join("state/cache")).unwrap();
        symlink(real.join("work"), temp.path().join("work-link")).unwrap();

        for work_dir in [None, Some(relative.join("work-link"))] {
            let mut opts = RunOptions::new(relative.join("state-link"));
            assert!(opts.state_dir.is_relative());
            opts.work_dir = work_dir;
            let (paths, work, cache) = directories(&opts).unwrap();
            assert_eq!(paths.root, real.join("state"));
            assert_eq!(work, real.join("work"));
            assert_eq!(cache, real.join("cache/providers"));
            let workspace = work.join("attempt");
            let root = workspace.join("repo/module");
            fs::create_dir_all(&root).unwrap();
            let runtime = ToolRuntime::new(RuntimeKind::Podman, None, "tofu".into(), None);
            assert_eq!(
                runtime
                    .path(&workspace, &root.canonicalize().unwrap())
                    .unwrap(),
                "/work/repo/module"
            );
        }
    }
}
