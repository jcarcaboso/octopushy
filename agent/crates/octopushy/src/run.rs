//! `octopushy agent run`: sets up the state directory, recovers from a restart,
//! and runs the agent channel.

use std::path::{Path, PathBuf};
use std::sync::Arc;
use std::time::Duration;

use anyhow::{Context, Result};
use tokio_util::sync::CancellationToken;

use tokio::sync::watch;

use crate::attempts::AttemptManager;
use crate::availability::{self, Availability, StorageDirs};
use crate::connection::{self, Agent, Storage};
use crate::executors::ExecutorConfig;
use crate::executors::nix::NixSettings;
use crate::executors::opentofu::state;
use crate::fsutil::ensure_private_dir;
use crate::identity;
use crate::images::ServerLink;
use crate::outbox::Outbox;
use crate::paths::StatePaths;
use crate::reachability::{self, Prober};
use crate::tools::{RuntimeKind, ToolRuntime, users};

/// Options of `octopushy agent run`.
#[derive(Debug, Clone)]
pub struct RunOptions {
    pub state_dir: PathBuf,
    pub work_dir: Option<PathBuf>,
    pub tool_runtime: RuntimeKind,
    pub tofu_binary: PathBuf,
    pub ansible_binary: PathBuf,
    pub nix_binary: PathBuf,
    /// The folder holding the Nix store.
    pub nix_store_dir: PathBuf,
    /// Free space the Nix store's file system needs for `nix` to be advertised.
    pub nix_min_free_bytes: u64,
    /// Above this, the Nix store is collected after a build.
    pub nix_store_max_bytes: u64,
    pub container_binary: Option<PathBuf>,
    pub container_network: Option<String>,
    /// `--pids-limit` for tool containers; `None` leaves the runtime's default.
    pub container_pids_limit: Option<u32>,
    /// `--memory` for tool containers; `None` sets no limit.
    pub container_memory: Option<String>,
    /// Refuse tool images not named by digest.
    pub require_image_digest: bool,
    pub poll_interval: Duration,
    /// How often executors are probed again while running.
    pub reprobe_interval: Duration,
}

impl RunOptions {
    pub fn new(state_dir: PathBuf) -> Self {
        Self {
            state_dir,
            work_dir: None,
            tool_runtime: RuntimeKind::Podman,
            tofu_binary: PathBuf::from("tofu"),
            ansible_binary: PathBuf::from("octopushy-ansible"),
            nix_binary: PathBuf::from("nix"),
            nix_store_dir: PathBuf::from("/nix"),
            nix_min_free_bytes: NixSettings::DEFAULT_MIN_FREE_GB << 30,
            nix_store_max_bytes: NixSettings::DEFAULT_STORE_MAX_GB << 30,
            container_binary: None,
            container_network: None,
            container_pids_limit: Some(crate::tools::DEFAULT_PIDS_LIMIT),
            container_memory: None,
            require_image_digest: false,
            poll_interval: Duration::from_secs(5),
            reprobe_interval: availability::REPROBE_INTERVAL,
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

    let runtime = tool_runtime(&opts, Some(id.record.agent_id.clone()))?;
    log_isolation(&runtime);
    if let Some(pool) = runtime.users.clone() {
        // Whatever the agent writes from now on is its own alone.
        // SAFETY: umask has no preconditions.
        unsafe { libc::umask(0o077) };
        pool.keep_out_of_sweeps([
            paths.root.clone(),
            work_dir.clone(),
            opts.nix_store_dir.clone(),
        ]);
        let nix_cache = paths.root.join("cache").join("nix");
        users::secure_state(
            &paths.root,
            &work_dir,
            pool.nix_user().map(|u| (nix_cache.as_path(), u)),
        )
        .context("making the state directory the agent's alone")?;
        // A tool a killed agent left running is stopped before its attempt is reported.
        // A user that cannot be cleaned up is quarantined; with none left the agent does not start.
        tokio::task::spawn_blocking(move || pool.clean_all())
            .await?
            .context("cleaning up after a previous agent process")?;
    }
    let probed = availability::probe(&runtime, &opts.nix_store_dir, opts.nix_min_free_bytes).await;
    log_changes(None, &probed);
    // Probes need no tool: an agent-process probe is the executor's own connection, and a tool
    // context that cannot be probed says so in its report. Built-in state is a feature of this
    // binary, offered whenever `opentofu` is, so it is advertised even while the runtime is down:
    // the runtime may come back without a reconnect.
    let capabilities = vec![
        state::CAPABILITY.to_owned(),
        crate::attempts::FORCE_STOP_CAPABILITY.to_owned(),
        reachability::CAPABILITY.to_owned(),
        reachability::UPTIME_CAPABILITY.to_owned(),
        reachability::UPTIME_TCP_CAPABILITY.to_owned(),
    ];
    let storage_dirs = storage_dirs(&opts, &paths.root, &work_dir, &provider_cache);
    let exec = Arc::new(ExecutorConfig {
        runtime,
        provider_cache,
        poll_interval: opts.poll_interval,
        proxmox_timeout: Duration::from_secs(30),
        retry_delay: Duration::from_secs(2),
        server: ServerLink::Enrolled(paths.clone()),
        nix: NixSettings::new(
            opts.nix_store_dir.clone(),
            opts.nix_store_max_bytes,
            Some(paths.root.join("cache").join("nix")),
        ),
    });

    let prober = Arc::new(Prober::new(exec.clone()));
    let exec_for_probes = exec.clone();
    // Before recovery: a tool a killed agent left running is stopped before its attempt's
    // saved state is read and its attempt reported. That attempt is uncertain if it was a
    // change, and nothing retries it.
    match exec.runtime.remove_leftovers().await {
        Ok(0) => {}
        Ok(n) => tracing::warn!("removed {n} containers a previous agent process left"),
        Err(e) => tracing::warn!("cannot remove containers a previous agent process left: {e:#}"),
    }
    let manager = AttemptManager::new(work_dir, outbox, exec);
    let pending = manager.recover()?;
    if pending > 0 {
        tracing::info!("{pending} results wait in the outbox for acknowledgement");
    }

    let agent = Arc::new(Agent {
        paths,
        manager,
        tool_runtime: opts.tool_runtime,
        availability: watch::channel(probed).0,
        capabilities,
        prober,
        storage: Storage::new(storage_dirs),
    });
    let reprobe = tokio::spawn(reprobe(
        agent.clone(),
        exec_for_probes,
        opts.nix_store_dir.clone(),
        opts.nix_min_free_bytes,
        opts.reprobe_interval,
        shutdown.clone(),
    ));
    let result = connection::run(agent, shutdown).await;
    reprobe.abort();
    result
}

/// Probes the executors again every `interval`, one probe at a time,
/// and measures the caches. A change goes to the open connection through the watch.
async fn reprobe(
    agent: Arc<Agent>,
    exec: Arc<ExecutorConfig>,
    nix_store_dir: PathBuf,
    nix_min_free: u64,
    interval: Duration,
    shutdown: CancellationToken,
) {
    let mut every = tokio::time::interval(interval);
    every.set_missed_tick_behavior(tokio::time::MissedTickBehavior::Delay);
    let mut first = true;
    loop {
        tokio::select! {
            _ = every.tick() => {}
            _ = shutdown.cancelled() => return,
        }
        // The first tick is at once: the caches are measured now, the executors were at startup.
        if !std::mem::take(&mut first) {
            let probed = availability::probe(&exec.runtime, &nix_store_dir, nix_min_free).await;
            agent.availability.send_if_modified(|current| {
                if *current == probed {
                    return false;
                }
                log_changes(Some(current), &probed);
                *current = probed;
                true
            });
        }
        agent.storage.measure_caches().await;
    }
}

/// Logs what is offered and why the rest is not; after a re-probe, only what changed.
fn log_changes(before: Option<&Availability>, after: &Availability) {
    for p in &after.probes {
        let was = before.and_then(|b| b.probes.iter().find(|q| q.name == p.name));
        if was.is_some_and(|q| q.result.is_ok() == p.result.is_ok()) {
            continue;
        }
        match &p.result {
            Ok(detail) => tracing::info!("offering {}: {detail}", p.name),
            Err(reason) if p.expected || was.is_some() => {
                tracing::warn!("not offering {}: {reason}", p.name)
            }
            Err(reason) => tracing::info!("not offering {}: {reason}", p.name),
        }
    }
}

/// Says how tools are kept apart, once, at start.
fn log_isolation(runtime: &ToolRuntime) {
    if runtime.is_container() {
        return;
    }
    match &runtime.users {
        Some(pool) => tracing::info!(
            "tool runtime local, isolated: each attempt runs its tools as a user of its own \
             ({} users; Nix builds as {}, one at a time)",
            pool.size(),
            pool.nix_user()
                .map(|u| users::name_of(u.uid))
                .unwrap_or_else(|| "nobody, as the store is root's: not offered".into())
        ),
        None => tracing::warn!(
            "tool runtime local, shared: tools run as the agent's own user ({}), one attempt at a \
             time; run the agent as root (the agent image does) to give each attempt a user of its own",
            // SAFETY: no preconditions.
            users::name_of(unsafe { libc::geteuid() })
        ),
    }
}

/// The tool runtime `opts` set up. `owner` labels its containers. With the `local` runtime as
/// root, tools run as the pool's users; it fails when there is none, rather than run them as root.
fn tool_runtime(opts: &RunOptions, owner: Option<String>) -> Result<ToolRuntime> {
    let mut runtime = ToolRuntime::new(
        opts.tool_runtime,
        opts.container_binary.clone(),
        opts.tofu_binary.clone(),
        opts.container_network.clone(),
    );
    runtime.ansible_binary = opts.ansible_binary.clone();
    runtime.nix_binary = opts.nix_binary.clone();
    runtime.owner = owner;
    runtime.pids_limit = opts.container_pids_limit;
    runtime.memory_limit = opts.container_memory.clone();
    runtime.require_image_digest = opts.require_image_digest;
    if opts.tool_runtime == RuntimeKind::Local && users::is_root() {
        runtime.users = Some(Arc::new(users::UserPool::discover(&opts.nix_store_dir)?));
    }
    Ok(runtime)
}

fn storage_dirs(opts: &RunOptions, state: &Path, work: &Path, providers: &Path) -> StorageDirs {
    StorageDirs {
        state: state.to_path_buf(),
        work: work.to_path_buf(),
        nix_store: opts.nix_store_dir.clone(),
        providers: providers.to_path_buf(),
        nix_cache: state.join("cache").join("nix"),
    }
}

/// `octopushy agent doctor`: the probes `run` makes with the same options, and the machine's
/// room, printed. Reads no enrollment, creates nothing and contacts nobody, so it runs next to a
/// running agent. Returns whether every executor the options mean to offer is available.
pub async fn doctor(opts: RunOptions) -> bool {
    let paths = StatePaths::new(opts.state_dir.clone());
    let work = opts
        .work_dir
        .clone()
        .unwrap_or_else(|| paths.default_work_dir());
    let runtime = match tool_runtime(&opts, None) {
        Ok(runtime) => runtime,
        Err(e) => {
            eprintln!("{e:#}");
            return false;
        }
    };
    let probed = availability::probe(&runtime, &opts.nix_store_dir, opts.nix_min_free_bytes).await;
    let dirs = storage_dirs(&opts, &paths.root, &work, &paths.provider_cache());
    tokio::task::spawn_blocking(move || {
        availability::print(opts.tool_runtime.as_str(), &probed, &dirs)
    })
    .await
    .unwrap_or(false)
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
