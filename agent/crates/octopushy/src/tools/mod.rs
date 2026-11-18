//! Running tools (OpenTofu, Ansible, Nix) for attempts: in a rootless container
//! (`podman`, `docker`) or directly (`local`). Nix runs only directly.
//!
//! Secrets never appear on a command line, where `/proc/<pid>/cmdline` would
//! show them. The `local` runtime sets the tool's environment on the child process.
//! A container runtime reads the input's environment from `--env-file /dev/fd/N`, an
//! in-memory file only it holds, so those names and values stay out of its own
//! environment; a value on more than one line, which an env file cannot hold, is passed
//! as `-e NAME` and copied from the runtime's environment. A tool that takes secrets on
//! stdin gets `-i` and a pipe.
//!
//! Every container carries [`AGENT_LABEL`] with this agent's ID, so at startup
//! [`ToolRuntime::remove_leftovers`] removes what a killed agent left, and nothing of
//! another agent's sharing the runtime.

mod lines;
pub mod users;

use std::io::Write;
use std::os::fd::{AsRawFd, OwnedFd};
use std::path::{Path, PathBuf};
use std::process::Stdio;
use std::sync::Arc;
use std::time::Duration;

use anyhow::{Context, Result, bail};
use tokio::io::{AsyncReadExt, AsyncWriteExt};
use tokio::process::{Child, Command};
use tokio::sync::OnceCell;
use tokio::task::{JoinError, JoinHandle};
use tokio_util::sync::CancellationToken;

use crate::attempts::logs::AttemptLogger;
use users::{ToolUser, UserPool};

/// Where the attempt workspace appears inside a container.
pub const CONTAINER_WORKSPACE: &str = "/work";
/// Where the provider cache appears inside a container.
pub const CONTAINER_PLUGIN_CACHE: &str = "/octopushy/plugin-cache";

/// The label naming the agent a container belongs to: its agent ID.
pub const AGENT_LABEL: &str = "io.octopushy.agent";
/// The label naming the attempt a tool container runs for.
pub const ATTEMPT_LABEL: &str = "io.octopushy.attempt";
/// The default `--pids-limit` of a container.
pub const DEFAULT_PIDS_LIMIT: u32 = 4096;
/// How long one runtime command at startup may take.
const STARTUP_TIMEOUT: Duration = Duration::from_secs(60);
/// How long a `--version` probe may take before its program is killed and the tool counted
/// unavailable. A cold `podman --version` answers well within a second.
pub const PROBE_TIMEOUT: Duration = Duration::from_secs(20);
/// The longest value passed through `--env-file`; longer ones go by name. Both runtimes read
/// the file a line at a time with a 64 KiB line limit.
const MAX_ENV_FILE_VALUE: usize = 32 * 1024;

/// Lines of stderr kept for error messages.
const STDERR_TAIL: usize = 20;
/// Largest captured stdout (`show -json`, `output -json`).
const MAX_CAPTURE: u64 = 64 * 1024 * 1024;

#[derive(Debug, Clone, Copy, PartialEq, Eq, clap::ValueEnum)]
pub enum RuntimeKind {
    Podman,
    Docker,
    Local,
}

impl RuntimeKind {
    pub fn as_str(self) -> &'static str {
        match self {
            RuntimeKind::Podman => "podman",
            RuntimeKind::Docker => "docker",
            RuntimeKind::Local => "local",
        }
    }
}

/// Which tool an [`Invocation`] runs.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum Tool {
    OpenTofu,
    Ansible,
    Nix,
}

impl Tool {
    fn label(self) -> &'static str {
        match self {
            Tool::OpenTofu => "tofu",
            Tool::Ansible => "ansible-playbook",
            Tool::Nix => "nix",
        }
    }
}

/// How tools are started.
#[derive(Debug)]
pub struct ToolRuntime {
    pub kind: RuntimeKind,
    /// `podman` or `docker` (looked up on `PATH` unless absolute).
    pub container_binary: PathBuf,
    /// The `tofu` binary for the `local` runtime.
    pub tofu_binary: PathBuf,
    /// The `octopushy-ansible` program for the `local` runtime.
    pub ansible_binary: PathBuf,
    /// The `nix` binary (`local` runtime only).
    pub nix_binary: PathBuf,
    /// `--network` for containers.
    pub network: Option<String>,
    /// How long a cancelled tool gets to exit after SIGTERM.
    pub kill_grace: Duration,
    /// This agent's ID, set on every container as [`AGENT_LABEL`]. Without it containers carry
    /// no owner and [`ToolRuntime::remove_leftovers`] removes nothing.
    pub owner: Option<String>,
    /// `--pids-limit` for containers; `None` leaves the runtime's default.
    pub pids_limit: Option<u32>,
    /// `--memory` for containers, such as `4g`; `None` sets no limit.
    pub memory_limit: Option<String>,
    /// Refuse an image that is not named by digest (`...@sha256:<64 hex>`).
    pub require_image_digest: bool,
    /// With the `local` runtime as root: the users tools run as, one per attempt. `None`: tools
    /// run as the agent's own user, which then takes one attempt at a time.
    pub users: Option<Arc<UserPool>>,
    docker_rootless: OnceCell<bool>,
}

/// One tool run.
#[derive(Debug)]
pub struct Invocation<'a> {
    pub tool: Tool,
    /// Container name, unique per run.
    pub name: String,
    /// The image (container runtimes only).
    pub image: &'a str,
    /// The attempt workspace (mounted at [`CONTAINER_WORKSPACE`]).
    pub workspace: &'a Path,
    /// Working directory; must be inside `workspace`.
    pub workdir: &'a Path,
    /// The provider cache (mounted at [`CONTAINER_PLUGIN_CACHE`]).
    pub plugin_cache: Option<&'a Path>,
    /// Tool arguments, with paths already translated by [`ToolRuntime::path`].
    pub args: Vec<String>,
    /// Settings the agent chose itself (`HOME`, `TF_PLUGIN_CACHE_DIR`, ...).
    /// Not secret: container runtimes get them as `-e NAME=VALUE`, so they never
    /// change the runtime's own behavior (podman reads `HOME`, for example).
    pub settings: Vec<(String, String)>,
    /// The input's `env` and `secretEnv`. Container runtimes read them from an in-memory env
    /// file, never from their command line or, unless a value has more than one line, their
    /// own environment.
    pub env: Vec<(String, String)>,
    /// Written to the tool's stdin, then closed. Without it stdin is empty.
    pub stdin: Option<Vec<u8>>,
    /// Capture stdout instead of logging it.
    pub capture_stdout: bool,
    /// A whole stderr line to report in [`ToolOutput::stderr_marked`].
    pub stderr_mark: Option<&'a str>,
    /// Stderr lines starting with this are the tool talking to the agent, not output: they
    /// are held back from the log, unmasked, and the last is [`ToolOutput::control`].
    pub control_prefix: Option<&'a str>,
    /// The attempt's own user, with the `local` runtime as root; see [`users`].
    pub user: Option<ToolUser>,
}

/// What a tool run produced. `Debug` leaves out what was not masked.
#[derive(Default)]
pub struct ToolOutput {
    /// Exit code, or `None` if killed by a signal.
    pub code: Option<i32>,
    /// Captured stdout (when requested).
    pub stdout: Vec<u8>,
    /// The last stderr lines, masked.
    pub stderr_tail: Vec<String>,
    /// Whether stderr had the line [`Invocation::stderr_mark`] names, anywhere.
    pub stderr_marked: bool,
    /// What followed [`Invocation::control_prefix`] on the last such line. It is not masked,
    /// so whoever reads it masks what it keeps.
    pub control: Option<String>,
    /// Whether the run was cancelled.
    pub cancelled: bool,
}

impl std::fmt::Debug for ToolOutput {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("ToolOutput")
            .field("code", &self.code)
            .field("stderr_tail", &self.stderr_tail)
            .field("stderr_marked", &self.stderr_marked)
            .field("cancelled", &self.cancelled)
            .finish_non_exhaustive()
    }
}

/// How one [`ToolRuntime::pull`] ended.
#[derive(Debug, PartialEq, Eq)]
pub enum Pull {
    /// The runtime has the image.
    Done,
    /// Why the pull failed, masked.
    Failed(String),
    Cancelled,
}

impl ToolOutput {
    /// The last few stderr lines that are not blank, for error messages.
    pub fn stderr_summary(&self, max: usize) -> String {
        let lines: Vec<&str> = self
            .stderr_tail
            .iter()
            .map(|l| l.trim())
            .filter(|l| !l.is_empty())
            .collect();
        let start = lines.len().saturating_sub(max);
        lines[start..].join(" / ")
    }
}

impl ToolRuntime {
    pub fn new(
        kind: RuntimeKind,
        container_binary: Option<PathBuf>,
        tofu_binary: PathBuf,
        network: Option<String>,
    ) -> Self {
        Self {
            kind,
            container_binary: container_binary.unwrap_or_else(|| PathBuf::from(kind.as_str())),
            tofu_binary,
            ansible_binary: PathBuf::from("octopushy-ansible"),
            nix_binary: PathBuf::from("nix"),
            network,
            kill_grace: Duration::from_secs(10),
            owner: None,
            pids_limit: Some(DEFAULT_PIDS_LIMIT),
            memory_limit: None,
            require_image_digest: false,
            users: None,
            docker_rootless: OnceCell::new(),
        }
    }

    /// Refuses an image not named by digest when [`ToolRuntime::require_image_digest`] is on.
    pub fn check_image(&self, image: &str) -> Result<()> {
        if self.require_image_digest && !is_pinned(image) {
            bail!(
                "the image {image} is not pinned by digest, and this agent requires one \
                 (--require-image-digest, OCTOPUSHY_REQUIRE_IMAGE_DIGEST): name it as \
                 image:tag@sha256:<64 hex> in the workflow or the server's Octopushy:Tools settings"
            );
        }
        Ok(())
    }

    /// Removes every container, running or not, that carries this agent's [`AGENT_LABEL`]: what
    /// a killed agent or a crashed runtime left, with the environment its config holds on disk.
    /// Runs at startup before recovery, so a tool still running is stopped before its attempt's
    /// saved state is read. That attempt is reported as a restart left it (uncertain for a
    /// change) and never retried. Returns how many were removed.
    pub async fn remove_leftovers(&self) -> Result<usize> {
        let Some(owner) = self.owner.as_deref().filter(|_| self.is_container()) else {
            return Ok(0);
        };
        let filter = format!("label={AGENT_LABEL}={owner}");
        let ids = self
            .startup_command(&["ps", "-a", "-q", "--no-trunc", "--filter", &filter])
            .await?;
        let ids: Vec<&str> = ids.split_whitespace().collect();
        if ids.is_empty() {
            return Ok(0);
        }
        let format = format!(
            "{{{{.Id}}}} {{{{index .Config.Labels \"{AGENT_LABEL}\"}}}} \
             {{{{index .Config.Labels \"{ATTEMPT_LABEL}\"}}}}"
        );
        let mut args = vec!["inspect", "--format", &format, "--"];
        args.extend(&ids);
        let found = self.startup_command(&args).await?;
        let mut removed = 0;
        for line in found.lines() {
            let mut fields = line.split_whitespace();
            let (Some(id), Some(agent)) = (fields.next(), fields.next()) else {
                continue;
            };
            // The filter is the runtime's; the label is checked here too.
            if agent != owner {
                continue;
            }
            let attempt = fields.next().unwrap_or("");
            tracing::warn!(
                container = %id, attempt = %attempt,
                "removing a container a previous agent process left"
            );
            match self.startup_command(&["rm", "-f", "--", id]).await {
                Ok(_) => removed += 1,
                Err(e) => tracing::warn!(container = %id, "cannot remove the container: {e:#}"),
            }
        }
        Ok(removed)
    }

    /// Runs `<runtime> args` and returns its stdout, within [`STARTUP_TIMEOUT`].
    async fn startup_command(&self, args: &[&str]) -> Result<String> {
        let out = Command::new(&self.container_binary)
            .args(args)
            .stdin(Stdio::null())
            .kill_on_drop(true)
            .output();
        let out = tokio::time::timeout(STARTUP_TIMEOUT, out)
            .await
            .with_context(|| format!("{} {} timed out", self.kind.as_str(), args[0]))?
            .with_context(|| format!("cannot run {}", self.container_binary.display()))?;
        if !out.status.success() {
            bail!(
                "{} {} exited with {}: {}",
                self.kind.as_str(),
                args[0],
                out.status,
                String::from_utf8_lossy(&out.stderr).trim()
            );
        }
        Ok(String::from_utf8_lossy(&out.stdout).into_owned())
    }

    pub fn is_container(&self) -> bool {
        self.kind != RuntimeKind::Local
    }

    /// `host` as the tool sees it.
    pub fn path(&self, workspace: &Path, host: &Path) -> Result<String> {
        if !self.is_container() {
            return Ok(host.to_string_lossy().into_owned());
        }
        let rel = host
            .strip_prefix(workspace)
            .with_context(|| format!("{} is outside the workspace", host.display()))?;
        let mut out = String::from(CONTAINER_WORKSPACE);
        for c in rel.components() {
            out.push('/');
            out.push_str(&c.as_os_str().to_string_lossy());
        }
        Ok(out)
    }

    /// The provider cache as the tool sees it.
    pub fn plugin_cache_path(&self, host: &Path) -> String {
        if self.is_container() {
            CONTAINER_PLUGIN_CACHE.to_owned()
        } else {
            host.to_string_lossy().into_owned()
        }
    }

    /// Checks that the runtime can start: `<runtime> --version`, or `tofu version` with the local
    /// runtime. Never pulls an image or starts a container.
    pub async fn probe(&self) -> Result<String> {
        let (bin, args): (&Path, &[&str]) = match self.kind {
            RuntimeKind::Local => (&self.tofu_binary, &["version"]),
            _ => (&self.container_binary, &["--version"]),
        };
        version_line(bin, args, self.probe_user()).await
    }

    /// Whether Ansible can run. A container runtime pulls its image when it is needed.
    pub async fn probe_ansible(&self) -> Result<String> {
        if self.is_container() {
            return Ok("from its image".to_owned());
        }
        version_line(&self.ansible_binary, &["--version"], self.probe_user()).await
    }

    /// Whether `nix` can run: only directly, never in a container.
    pub async fn probe_nix(&self) -> Result<String> {
        if self.is_container() {
            bail!("nix runs only with the local tool runtime");
        }
        let user = match &self.users {
            // Never root: the Nix store, and the program in it, are its owner's to change.
            Some(pool) => Some(pool.nix_user().context(
                "the Nix store is root's; builds run as its owner, which must be an unprivileged user",
            )?),
            None => None,
        };
        version_line(&self.nix_binary, &["--version"], user).await
    }

    /// Who a local probe runs as: nobody, when the agent is root.
    fn probe_user(&self) -> Option<ToolUser> {
        self.users
            .as_ref()
            .filter(|_| !self.is_container())
            .map(|p| p.probe_user())
    }

    fn local_binary(&self, tool: Tool) -> &Path {
        match tool {
            Tool::OpenTofu => &self.tofu_binary,
            Tool::Ansible => &self.ansible_binary,
            Tool::Nix => &self.nix_binary,
        }
    }

    async fn docker_is_rootless(&self) -> bool {
        *self
            .docker_rootless
            .get_or_init(|| async {
                let out = Command::new(&self.container_binary)
                    .args(["info", "--format", "{{json .SecurityOptions}}"])
                    .stdin(Stdio::null())
                    // A probe that gives up waiting stops it too.
                    .kill_on_drop(true)
                    .output()
                    .await;
                let rootless = matches!(&out, Ok(o) if o.status.success()
                    && String::from_utf8_lossy(&o.stdout).contains("rootless"));
                if !rootless {
                    tracing::warn!(
                        "docker is not rootless; containers run as the agent's user, but \
                         access to a rootful docker daemon is equivalent to root"
                    );
                }
                rootless
            })
            .await
    }

    /// `<runtime> run` with everything a tool's container and a reachability probe's share: the
    /// same least privilege, user mapping and network, so a probe connects as the tool would.
    /// What follows is the caller's: mounts, working directory, environment, image.
    pub(crate) async fn container_run(
        &self,
        name: &str,
        label: &str,
        interactive: bool,
    ) -> Command {
        let mut cmd = Command::new(&self.container_binary);
        cmd.args(["run", "--rm"]);
        if interactive {
            cmd.arg("-i");
        }
        cmd.args(["--name", name]);
        cmd.args(["--label", label]);
        if let Some(owner) = &self.owner {
            cmd.args(["--label".to_owned(), format!("{AGENT_LABEL}={owner}")]);
        }
        if let Some(pids) = self.pids_limit {
            cmd.arg(format!("--pids-limit={pids}"));
        }
        if let Some(memory) = &self.memory_limit {
            // Swap as large as memory: the limit is the whole of what the tool may use.
            cmd.arg(format!("--memory={memory}"));
            cmd.arg(format!("--memory-swap={memory}"));
        }
        // The tool's output may contain secrets; keep it out of the
        // runtime's log store (journald by default on Fedora).
        cmd.arg("--log-driver=none");
        cmd.arg("--pull=missing");
        // Least privilege: no capabilities, no privilege escalation, a
        // read-only image with a private /tmp.
        cmd.args(["--cap-drop=all", "--security-opt=no-new-privileges"]);
        cmd.args(["--read-only", "--tmpfs", "/tmp:rw,mode=1777"]);
        if self.kind == RuntimeKind::Podman {
            // Run as the agent's own UID so written files stay its own.
            cmd.arg("--userns=keep-id");
        } else if !self.docker_is_rootless().await {
            // SAFETY: getuid/getgid have no preconditions.
            let (uid, gid) = unsafe { (libc::getuid(), libc::getgid()) };
            cmd.args(["--user".to_owned(), format!("{uid}:{gid}")]);
        }
        if let Some(net) = &self.network {
            cmd.args(["--network", net]);
        }
        cmd
    }

    /// Pulls `image` unless the runtime has it, once. A pull only fetches the image, so whoever
    /// calls this may try again. The runtime's output goes to the log, masked.
    pub async fn pull(
        &self,
        image: &str,
        log: &AttemptLogger,
        cancel: &CancellationToken,
    ) -> Result<Pull> {
        self.check_image(image)?;
        let present = Command::new(&self.container_binary)
            .args(["image", "inspect", "--", image])
            .stdin(Stdio::null())
            .stdout(Stdio::null())
            .stderr(Stdio::null())
            .kill_on_drop(true)
            .status();
        let present = tokio::select! {
            status = present => status
                .with_context(|| format!("cannot start {}", self.container_binary.display()))?,
            _ = cancel.cancelled() => return Ok(Pull::Cancelled),
        };
        if present.success() {
            log.agent(format!("the image {image} is present"));
            return Ok(Pull::Done);
        }
        log.agent(format!("$ {} pull {image}", self.kind.as_str()));
        let pull = Command::new(&self.container_binary)
            .args(["pull", "--", image])
            .stdin(Stdio::null())
            .stdout(Stdio::null())
            .stderr(Stdio::piped())
            .kill_on_drop(true)
            .output();
        let out = tokio::select! {
            out = pull => out
                .with_context(|| format!("cannot start {}", self.container_binary.display()))?,
            _ = cancel.cancelled() => return Ok(Pull::Cancelled),
        };
        let stderr: Vec<String> = String::from_utf8_lossy(&out.stderr)
            .lines()
            .map(|l| log.stderr(l))
            .collect();
        if out.status.success() {
            return Ok(Pull::Done);
        }
        let code = out
            .status
            .code()
            .map(|c| c.to_string())
            .unwrap_or_else(|| "a signal".into());
        let tail: Vec<&str> = stderr
            .iter()
            .map(|l| l.trim())
            .filter(|l| !l.is_empty())
            .collect();
        let tail = tail[tail.len().saturating_sub(3)..].join(" / ");
        Ok(Pull::Failed(if tail.is_empty() {
            format!("exit status {code}")
        } else {
            format!("exit status {code}: {tail}")
        }))
    }

    /// The command line (without the tool's own arguments' secrets: there are
    /// none) and the process to start, with the env file it reads, which must stay open
    /// until it has started.
    async fn command(&self, inv: &Invocation<'_>) -> Result<(Command, Option<OwnedFd>)> {
        let mut cmd;
        let mut env_file = None;
        match self.kind {
            RuntimeKind::Local => {
                cmd = Command::new(self.local_binary(inv.tool));
                match (inv.user, &self.users) {
                    (Some(user), Some(_)) => user.apply(&mut cmd),
                    (None, None) => {}
                    // Never as root, and never as a user the agent did not lease.
                    (None, Some(_)) => bail!("no user of its own to run the tool as"),
                    (Some(_), None) => bail!("this agent runs every tool as its own user"),
                }
                cmd.args(&inv.args).current_dir(inv.workdir).env_clear();
                if let Some(path) = std::env::var_os("PATH") {
                    cmd.env("PATH", path);
                }
                cmd.envs(inv.settings.iter().chain(&inv.env).map(|(k, v)| (k, v)));
            }
            RuntimeKind::Podman | RuntimeKind::Docker => {
                if inv.tool == Tool::Nix {
                    bail!("nix runs only with the local tool runtime");
                }
                if inv.image.trim().is_empty() {
                    bail!("the input names no container image");
                }
                self.check_image(inv.image)?;
                for p in [Some(inv.workspace), inv.plugin_cache]
                    .into_iter()
                    .flatten()
                {
                    let s = p.to_string_lossy();
                    if s.contains(':') || s.contains(',') {
                        bail!("{s} cannot be mounted: it contains ':' or ','");
                    }
                }
                // The workspace is named after the attempt.
                let attempt = inv
                    .workspace
                    .file_name()
                    .map(|n| n.to_string_lossy().into_owned())
                    .unwrap_or_default();
                cmd = self
                    .container_run(
                        &inv.name,
                        &format!("{ATTEMPT_LABEL}={attempt}"),
                        inv.stdin.is_some(),
                    )
                    .await;
                // The workspace is private to this container (`Z`); the cache is
                // shared by concurrent attempts (`z`), so it gets the shared label.
                cmd.args([
                    "-v".to_owned(),
                    format!("{}:{CONTAINER_WORKSPACE}:Z", inv.workspace.display()),
                ]);
                if let Some(cache) = inv.plugin_cache {
                    cmd.args([
                        "-v".to_owned(),
                        format!("{}:{CONTAINER_PLUGIN_CACHE}:z", cache.display()),
                    ]);
                }
                cmd.args(["-w".to_owned(), self.path(inv.workspace, inv.workdir)?]);
                for (k, v) in &inv.settings {
                    cmd.args(["-e".to_owned(), format!("{k}={v}")]);
                }
                let (in_file, by_name): (Vec<_>, Vec<_>) =
                    inv.env.iter().partition(|(_, v)| fits_env_file(v));
                if !in_file.is_empty() {
                    let fd = env_memfd(&in_file)?;
                    let raw = fd.as_raw_fd();
                    cmd.args(["--env-file".to_owned(), format!("/dev/fd/{raw}")]);
                    // SAFETY: fcntl is async-signal-safe, and only the child's copy of the
                    // descriptor loses close-on-exec; the agent's other children never get it.
                    unsafe {
                        cmd.pre_exec(move || {
                            let flags = libc::fcntl(raw, libc::F_GETFD);
                            if flags < 0
                                || libc::fcntl(raw, libc::F_SETFD, flags & !libc::FD_CLOEXEC) < 0
                            {
                                return Err(std::io::Error::last_os_error());
                            }
                            Ok(())
                        });
                    }
                    env_file = Some(fd);
                }
                // A value an env file cannot hold: by name, copied from the runtime's own
                // environment, never its command line.
                for (k, _) in &by_name {
                    cmd.args(["-e", k]);
                }
                cmd.envs(by_name.iter().map(|(k, v)| (k, v)));
                cmd.arg(inv.image);
                // The image's entrypoint is the tool.
                cmd.args(&inv.args);
            }
        }
        cmd.stdin(if inv.stdin.is_some() {
            Stdio::piped()
        } else {
            Stdio::null()
        })
        .stdout(Stdio::piped())
        .stderr(Stdio::piped())
        .kill_on_drop(true)
        // Own process group: a Ctrl-C at the agent's terminal does not reach
        // the tool, and cancel can signal the whole group.
        .process_group(0);
        Ok((cmd, env_file))
    }

    /// Runs the tool, streaming its output into the attempt log.
    pub async fn run(
        &self,
        mut inv: Invocation<'_>,
        log: &AttemptLogger,
        cancel: &CancellationToken,
    ) -> Result<ToolOutput> {
        log.agent(format!("$ {} {}", inv.tool.label(), inv.args.join(" ")));
        let (mut cmd, env_file) = self.command(&inv).await?;
        // Only a user the pool leased: `command` refused anything else.
        let user = inv.user.filter(|_| !self.is_container());
        if let Some(user) = user {
            let ws = inv.workspace.to_path_buf();
            tokio::task::spawn_blocking(move || users::hand_over(&ws, user))
                .await
                .context("handing the workspace over")?
                .context("handing the workspace over to the tool's user")?;
        }
        let mut child = cmd.spawn().with_context(|| match self.kind {
            RuntimeKind::Local => {
                format!("cannot start {}", self.local_binary(inv.tool).display())
            }
            _ => format!("cannot start {}", self.container_binary.display()),
        })?;
        // The runtime holds its own copy.
        drop(env_file);
        let pgid = child.id().map(|pid| pid as libc::pid_t);
        if let Some(input) = inv.stdin.take() {
            let mut stdin = child.stdin.take().expect("stdin is piped");
            // On its own task: a tool that exits early must not leave this waiting on a full pipe.
            tokio::spawn(async move {
                let _ = stdin.write_all(&input).await;
            });
        }

        let stdout = child.stdout.take().expect("stdout is piped");
        let stderr = child.stderr.take().expect("stderr is piped");
        let capture_failed = CancellationToken::new();
        let mut out_task = {
            let capture_failed = capture_failed.clone();
            let log = log.clone();
            let capture = inv.capture_stdout;
            tokio::spawn(async move {
                if capture {
                    let captured = capture_stdout(stdout, MAX_CAPTURE).await;
                    if captured.is_err() {
                        capture_failed.cancel();
                    }
                    captured
                } else {
                    lines::for_each_line(stdout, |l| {
                        log.stdout(&l);
                    })
                    .await?;
                    Ok(Vec::new())
                }
            })
        };
        let mut err_task = {
            let log = log.clone();
            let mark = inv.stderr_mark.map(str::to_owned);
            let prefix = inv.control_prefix.map(str::to_owned);
            tokio::spawn(async move {
                let mut tail: std::collections::VecDeque<String> = Default::default();
                let mut marked = false;
                let mut control = None;
                let _ = lines::for_each_line(stderr, |l| {
                    marked |= mark.as_deref() == Some(l.trim_end());
                    if let Some(rest) = prefix.as_deref().and_then(|p| l.strip_prefix(p)) {
                        control = Some(rest.to_owned());
                        return;
                    }
                    let masked = log.stderr(&l);
                    if tail.len() == STDERR_TAIL {
                        tail.pop_front();
                    }
                    tail.push_back(masked);
                })
                .await;
                (tail.into_iter().collect::<Vec<_>>(), marked, control)
            })
        };

        let mut cancelled = false;
        let status = tokio::select! {
            status = child.wait() => status?,
            _ = capture_failed.cancelled() => {
                self.terminate(&mut child, &inv.name, inv.tool, user).await;
                child.wait().await?
            }
            _ = cancel.cancelled() => {
                cancelled = true;
                log.agent("stopping the tool");
                self.terminate(&mut child, &inv.name, inv.tool, user).await;
                child.wait().await?
            }
        };
        // The tool has ended, and nothing it started outlives it: the agent then reads what it
        // left, and writes beside it, with no process of the user's still changing it.
        if let Some(user) = user {
            kill_user(user).await;
        }
        // Something the tool left behind can hold its pipes open after it exits, so the limit
        // has to keep applying until the output has ended. What was read by then decides nothing:
        // a stopped run is uncertain.
        let (mut out_read, mut err_read) = (None, None);
        tokio::select! {
            () = drain(&mut out_task, &mut err_task, &mut out_read, &mut err_read) => {}
            _ = async { tokio::select! { _ = cancel.cancelled() => {}, _ = capture_failed.cancelled() => {} } } => {
                cancelled |= cancel.is_cancelled();
                log.agent("stopping what the tool left running");
                self.kill_group(&inv.name, pgid, user).await;
                let rest = drain(&mut out_task, &mut err_task, &mut out_read, &mut err_read);
                if tokio::time::timeout(self.kill_grace, rest).await.is_err() {
                    out_task.abort();
                    err_task.abort();
                    log.agent("gave up waiting for the tool's output");
                }
            }
        }
        let stdout = match out_read {
            Some(result) => result.context("stdout reader")??,
            None => Vec::new(),
        };
        let (stderr_tail, stderr_marked, control) = match err_read {
            Some(result) => result.context("stderr reader")?,
            None => Default::default(),
        };
        Ok(ToolOutput {
            code: status.code(),
            stdout,
            stderr_tail,
            stderr_marked,
            control,
            cancelled,
        })
    }

    /// OpenTofu gets SIGINT first so it can finish in-flight work and save state.
    /// After one grace period, escalate to SIGTERM, then a hard kill. Other tools
    /// start at SIGTERM. Waiting stays bounded even when a container CLI hangs.
    async fn terminate(
        &self,
        child: &mut Child,
        container: &str,
        tool: Tool,
        user: Option<ToolUser>,
    ) {
        let Some(pid) = child.id() else { return };
        let pgid = pid as libc::pid_t;
        if tool == Tool::OpenTofu {
            self.signal_group(container, pgid, libc::SIGINT, "SIGINT")
                .await;
            if tokio::time::timeout(self.kill_grace, child.wait())
                .await
                .is_ok()
            {
                return;
            }
        }
        self.signal_group(container, pgid, libc::SIGTERM, "SIGTERM")
            .await;
        if tokio::time::timeout(self.kill_grace, child.wait())
            .await
            .is_ok()
        {
            return;
        }
        self.kill_group(container, Some(pgid), user).await;
    }

    async fn signal_group(&self, container: &str, pgid: libc::pid_t, signal: i32, name: &str) {
        if self.is_container() {
            // Signal the container itself, not just the attached CLI process.
            let kill = Command::new(&self.container_binary)
                .args(["kill", "--signal", name, container])
                .stdin(Stdio::null())
                .stdout(Stdio::null())
                .stderr(Stdio::null())
                .kill_on_drop(true)
                .status();
            let _ = tokio::time::timeout(self.kill_grace, kill).await;
        } else {
            // SAFETY: plain signal delivery to our own child's process group.
            unsafe {
                libc::kill(-pgid, signal);
            }
        }
    }

    /// A hard kill of the container and the process group, and of every process of the tool's
    /// own user, wherever it moved.
    pub(crate) async fn kill_group(
        &self,
        container: &str,
        pgid: Option<libc::pid_t>,
        user: Option<ToolUser>,
    ) {
        if self.is_container() {
            // A runtime that stops answering must not keep the run from ending.
            let kill = Command::new(&self.container_binary)
                .args(["kill", container])
                .stdin(Stdio::null())
                .stdout(Stdio::null())
                .stderr(Stdio::null())
                .kill_on_drop(true)
                .status();
            let _ = tokio::time::timeout(self.kill_grace, kill).await;
        }
        if let Some(pgid) = pgid {
            // SAFETY: plain signal delivery to our own child's process group.
            unsafe {
                libc::kill(-pgid, libc::SIGKILL);
            }
        }
        if let Some(user) = user {
            kill_user(user).await;
        }
    }
}

/// Kills every process of `user`, off the async threads.
async fn kill_user(user: ToolUser) {
    let killed = tokio::task::spawn_blocking(move || users::kill_all(user, KILL_USER_TIMEOUT))
        .await
        .unwrap_or(false);
    if !killed {
        tracing::error!(
            uid = user.uid,
            "processes of the tool's user survive SIGKILL"
        );
    }
}

/// How long the processes of a tool's user may take to die.
const KILL_USER_TIMEOUT: Duration = Duration::from_secs(30);

/// Whether `image` is named by digest: `name[:tag]@sha256:<64 lowercase hex>`.
pub fn is_pinned(image: &str) -> bool {
    image.split_once("@sha256:").is_some_and(|(name, digest)| {
        !name.is_empty()
            && digest.len() == 64
            && digest
                .bytes()
                .all(|b| b.is_ascii_digit() || (b'a'..=b'f').contains(&b))
    })
}

/// Whether a value can go in an env file: both runtimes read one line per variable and
/// drop a trailing `\r`.
fn fits_env_file(value: &str) -> bool {
    value.len() <= MAX_ENV_FILE_VALUE && !value.contains(['\n', '\r', '\0'])
}

/// An in-memory file of `NAME=VALUE` lines, closed on exec. Nothing of it reaches a file
/// system: the runtime opens it as `/dev/fd/N`.
fn env_memfd(env: &[&(String, String)]) -> Result<OwnedFd> {
    // SAFETY: a valid C string and flags; the result is checked before use.
    let raw = unsafe { libc::memfd_create(c"octopushy-env".as_ptr(), libc::MFD_CLOEXEC) };
    if raw < 0 {
        return Err(std::io::Error::last_os_error()).context("creating the env file");
    }
    // SAFETY: memfd_create returned a new descriptor that nothing else owns.
    let fd = unsafe { <OwnedFd as std::os::fd::FromRawFd>::from_raw_fd(raw) };
    let mut file = std::fs::File::from(fd);
    let mut text = String::new();
    for (k, v) in env {
        text.push_str(k);
        text.push('=');
        text.push_str(v);
        text.push('\n');
    }
    file.write_all(text.as_bytes())
        .context("writing the env file")?;
    Ok(file.into())
}

async fn capture_stdout(
    reader: impl tokio::io::AsyncRead + Unpin,
    cap: u64,
) -> std::io::Result<Vec<u8>> {
    let mut buf = Vec::new();
    reader.take(cap).read_to_end(&mut buf).await?;
    if buf.len() as u64 >= cap {
        return Err(std::io::Error::other(format!(
            "stdout capture reached the {cap} byte cap"
        )));
    }
    Ok(buf)
}

/// Waits for both readers, keeping what each returned: a handle cannot be awaited twice, and
/// this can be stopped and called again.
async fn drain<A, B>(
    first: &mut JoinHandle<A>,
    second: &mut JoinHandle<B>,
    first_read: &mut Option<Result<A, JoinError>>,
    second_read: &mut Option<Result<B, JoinError>>,
) {
    while first_read.is_none() || second_read.is_none() {
        tokio::select! {
            read = &mut *first, if first_read.is_none() => *first_read = Some(read),
            read = &mut *second, if second_read.is_none() => *second_read = Some(read),
        }
    }
}

/// The first line of `<bin> args`' stdout, within [`PROBE_TIMEOUT`]. The program is killed when
/// it takes longer. Its stderr is never kept: an error names the program and how it exited.
async fn version_line(bin: &Path, args: &[&str], user: Option<ToolUser>) -> Result<String> {
    let mut cmd = Command::new(bin);
    if let Some(user) = user {
        user.apply(&mut cmd);
        // Nothing of the agent's environment, as for a tool.
        cmd.env_clear();
        if let Some(path) = std::env::var_os("PATH") {
            cmd.env("PATH", path);
        }
        cmd.current_dir("/");
    }
    let out = cmd
        .args(args)
        .stdin(Stdio::null())
        .stderr(Stdio::null())
        .kill_on_drop(true)
        .output();
    let out = tokio::time::timeout(PROBE_TIMEOUT, out)
        .await
        .map_err(|_| {
            anyhow::anyhow!(
                "{} {} gave no answer within {}s",
                bin.display(),
                args.join(" "),
                PROBE_TIMEOUT.as_secs()
            )
        })?
        .with_context(|| format!("cannot run {}", bin.display()))?;
    if !out.status.success() {
        bail!(
            "{} {} exited with {}",
            bin.display(),
            args.join(" "),
            out.status
        );
    }
    Ok(String::from_utf8_lossy(&out.stdout)
        .lines()
        .next()
        .unwrap_or("")
        .trim()
        .to_owned())
}

#[cfg(test)]
mod tests {
    use super::*;

    #[tokio::test]
    async fn opentofu_stop_escalates_from_interrupt_to_term_to_kill() {
        use std::os::unix::fs::PermissionsExt;
        use std::sync::Arc;
        let dir = tempfile::tempdir().unwrap();
        let binary = dir.path().join("tofu");
        let signals = dir.path().join("signals");
        std::fs::write(&binary, format!("#!/bin/bash\ntrap 'echo INT >> {}' INT\ntrap 'echo TERM >> {}' TERM\ntouch ready\nwhile :; do sleep 1; done\n", signals.display(), signals.display())).unwrap();
        std::fs::set_permissions(&binary, std::fs::Permissions::from_mode(0o700)).unwrap();
        let mut runtime = ToolRuntime::new(RuntimeKind::Local, None, binary, None);
        runtime.kill_grace = Duration::from_millis(100);
        let cancel = CancellationToken::new();
        let stop = cancel.clone();
        let ready = dir.path().join("ready");
        let canceller = tokio::spawn(async move {
            tokio::time::timeout(Duration::from_secs(5), async {
                while !ready.exists() {
                    tokio::time::sleep(Duration::from_millis(5)).await;
                }
            })
            .await
            .unwrap();
            stop.cancel();
        });
        let log = AttemptLogger::new(
            Arc::new(crate::attempts::logs::LogBuffer::default()),
            Arc::new(crate::masking::Masker::default()),
            Arc::new(tokio::sync::Notify::new()),
        );
        let invocation = Invocation {
            tool: Tool::OpenTofu,
            name: "test-stop".into(),
            image: "",
            workspace: dir.path(),
            workdir: dir.path(),
            plugin_cache: None,
            args: vec![],
            settings: vec![],
            env: vec![],
            stdin: None,
            capture_stdout: false,
            stderr_mark: None,
            control_prefix: None,
            user: None,
        };
        let output = tokio::time::timeout(
            Duration::from_secs(5),
            runtime.run(invocation, &log, &cancel),
        )
        .await
        .unwrap()
        .unwrap();
        canceller.await.unwrap();
        assert!(output.cancelled);
        assert_eq!(output.code, None, "the stubborn tool was hard-killed");
        assert_eq!(std::fs::read_to_string(signals).unwrap(), "INT\nTERM\n");
    }

    #[tokio::test]
    async fn capture_fails_at_cap_instead_of_truncating() {
        assert_eq!(capture_stdout(&b"abc"[..], 4).await.unwrap(), b"abc");
        for data in [&b"abcd"[..], &b"abcde"[..]] {
            assert!(
                capture_stdout(data, 4)
                    .await
                    .unwrap_err()
                    .to_string()
                    .contains("4 byte cap")
            );
        }
    }

    #[test]
    fn container_paths() {
        let rt = ToolRuntime::new(RuntimeKind::Podman, None, "tofu".into(), None);
        let ws = Path::new("/var/lib/a/work/att-1");
        assert_eq!(
            rt.path(ws, &ws.join("repo/modules/x")).unwrap(),
            "/work/repo/modules/x"
        );
        assert_eq!(rt.path(ws, ws).unwrap(), "/work");
        assert!(rt.path(ws, Path::new("/etc")).is_err());
        let local = ToolRuntime::new(RuntimeKind::Local, None, "tofu".into(), None);
        assert_eq!(
            local.path(ws, &ws.join("out/plan.bin")).unwrap(),
            "/var/lib/a/work/att-1/out/plan.bin"
        );
    }

    #[test]
    fn debug_leaves_out_what_was_not_masked() {
        let out = ToolOutput {
            control: Some("a raw secret".into()),
            stdout: b"another raw secret".to_vec(),
            ..ToolOutput::default()
        };
        assert!(!format!("{out:?}").contains("secret"));
    }

    fn tofu_invocation<'a>(ws: &'a Path, workdir: &'a Path, cache: &'a Path) -> Invocation<'a> {
        Invocation {
            tool: Tool::OpenTofu,
            name: "octopushy-att-1-init".into(),
            image: "ghcr.io/opentofu/opentofu:1.13.1",
            workspace: ws,
            workdir,
            plugin_cache: Some(cache),
            args: vec!["init".into(), "-input=false".into()],
            settings: vec![("HOME".into(), "/work/home".into())],
            env: vec![
                ("PG_CONN_STR".into(), "postgres://u:supersecret@db/x".into()),
                (
                    "MULTI_LINE".into(),
                    "-----BEGIN KEY-----\nmultisecret\n".into(),
                ),
            ],
            stdin: None,
            capture_stdout: false,
            stderr_mark: None,
            control_prefix: None,
            user: None,
        }
    }

    fn args_of(cmd: &Command) -> String {
        cmd.as_std()
            .get_args()
            .map(|a| a.to_string_lossy().into_owned())
            .collect::<Vec<_>>()
            .join(" ")
    }

    #[tokio::test]
    async fn container_command_keeps_env_values_off_the_command_line_and_environment() {
        use std::io::{Read, Seek};
        let mut rt = ToolRuntime::new(
            RuntimeKind::Podman,
            None,
            "tofu".into(),
            Some("octonet".into()),
        );
        rt.owner = Some("agent-7".into());
        let ws = PathBuf::from("/srv/work/att-1");
        let workdir = ws.join("repo/root");
        let cache = PathBuf::from("/srv/cache");
        let inv = tofu_invocation(&ws, &workdir, &cache);
        let (cmd, env_file) = rt.command(&inv).await.unwrap();
        let joined = args_of(&cmd);
        assert!(!joined.contains("supersecret"), "{joined}");
        assert!(!joined.contains("multisecret"), "{joined}");
        assert!(!joined.contains("PG_CONN_STR"), "{joined}");
        let fd = env_file.expect("an env file");
        assert!(
            joined.contains(&format!("--env-file /dev/fd/{} ", fd.as_raw_fd())),
            "{joined}"
        );
        let mut file = std::fs::File::from(fd);
        file.rewind().unwrap();
        let mut text = String::new();
        file.read_to_string(&mut text).unwrap();
        assert_eq!(text, "PG_CONN_STR=postgres://u:supersecret@db/x\n");
        // A value on several lines cannot go in the file: by name, from the runtime's environment.
        assert!(joined.contains("-e MULTI_LINE "), "{joined}");
        assert!(joined.contains("-e HOME=/work/home"), "{joined}");
        assert!(
            joined.contains("--label io.octopushy.attempt=att-1"),
            "{joined}"
        );
        assert!(
            joined.contains("--label io.octopushy.agent=agent-7"),
            "{joined}"
        );
        assert!(joined.contains("--userns=keep-id"));
        assert!(joined.contains("-v /srv/work/att-1:/work:Z"));
        assert!(joined.contains("-v /srv/cache:/octopushy/plugin-cache:z"));
        assert!(joined.contains("-w /work/repo/root"));
        assert!(joined.contains("--network octonet"));
        assert!(!joined.contains("--privileged"));
        assert!(
            joined.ends_with("ghcr.io/opentofu/opentofu:1.13.1 init -input=false"),
            "{joined}"
        );
        let env: Vec<_> = cmd.as_std().get_envs().collect();
        assert!(
            !env.iter().any(|(k, _)| *k == "PG_CONN_STR"),
            "a one-line value stays out of the runtime's environment"
        );
        assert!(
            env.iter()
                .all(|(_, v)| v.is_none_or(|v| !v.to_string_lossy().contains("supersecret")))
        );
        assert!(
            !env.iter().any(|(k, _)| *k == "HOME"),
            "the runtime keeps its own HOME"
        );
    }

    #[tokio::test]
    async fn container_limits_default_to_pids_only() {
        let ws = PathBuf::from("/srv/work/att-1");
        let workdir = ws.join("repo/root");
        let cache = PathBuf::from("/srv/cache");
        let mut rt = ToolRuntime::new(RuntimeKind::Podman, None, "tofu".into(), None);
        let (cmd, _) = rt
            .command(&tofu_invocation(&ws, &workdir, &cache))
            .await
            .unwrap();
        let joined = args_of(&cmd);
        assert!(joined.contains("--pids-limit=4096"), "{joined}");
        assert!(!joined.contains("--memory"), "{joined}");
        assert!(
            !joined.contains("io.octopushy.agent"),
            "no owner, no owner label"
        );

        rt.memory_limit = Some("6g".into());
        rt.pids_limit = Some(512);
        let (cmd, _) = rt
            .command(&tofu_invocation(&ws, &workdir, &cache))
            .await
            .unwrap();
        let joined = args_of(&cmd);
        assert!(joined.contains("--pids-limit=512"), "{joined}");
        assert!(joined.contains("--memory=6g --memory-swap=6g"), "{joined}");
    }

    #[tokio::test]
    async fn an_image_named_by_tag_is_refused_only_when_digests_are_required() {
        let ws = PathBuf::from("/srv/work/att-1");
        let workdir = ws.join("repo/root");
        let cache = PathBuf::from("/srv/cache");
        let mut rt = ToolRuntime::new(RuntimeKind::Podman, None, "tofu".into(), None);
        let digest = format!("ghcr.io/opentofu/opentofu:1.13.1@sha256:{}", "a".repeat(64));
        let mut inv = tofu_invocation(&ws, &workdir, &cache);
        assert!(
            rt.command(&inv).await.is_ok(),
            "a tag is accepted by default"
        );

        rt.require_image_digest = true;
        let err = rt.command(&inv).await.unwrap_err().to_string();
        assert!(err.contains("not pinned by digest"), "{err}");
        assert!(err.contains("--require-image-digest"), "{err}");
        let log = AttemptLogger::new(
            std::sync::Arc::new(crate::attempts::logs::LogBuffer::default()),
            std::sync::Arc::new(crate::masking::Masker::default()),
            std::sync::Arc::new(tokio::sync::Notify::new()),
        );
        assert!(
            rt.pull(inv.image, &log, &CancellationToken::new())
                .await
                .is_err(),
            "nor pulled"
        );
        inv.image = &digest;
        assert!(rt.command(&inv).await.is_ok());

        for (image, pinned) in [
            (digest.as_str(), true),
            ("ghcr.io/opentofu/opentofu:1.13.1", false),
            ("ghcr.io/opentofu/opentofu@sha256:abc", false),
            (&format!("@sha256:{}", "a".repeat(64)), false),
            (&format!("x@sha256:{}", "A".repeat(64)), false),
            (&format!("x@sha256:{}", "b".repeat(64)), true),
        ] {
            assert_eq!(is_pinned(image), pinned, "{image}");
        }
    }

    #[tokio::test]
    async fn a_local_tool_runs_only_as_the_user_its_attempt_leased() {
        use users::{ToolUser, UserPool};
        let ws = PathBuf::from("/srv/work/att-1");
        let cache = PathBuf::from("/srv/cache");
        let user = |id| ToolUser { uid: id, gid: id };
        let mut rt = ToolRuntime::new(RuntimeKind::Local, None, "tofu".into(), None);
        let mut inv = tofu_invocation(&ws, &ws, &cache);
        inv.user = Some(user(4_200_001));
        let err = rt.command(&inv).await.unwrap_err().to_string();
        assert!(err.contains("its own user"), "{err}");
        rt.users = Some(Arc::new(
            UserPool::new(vec![user(4_200_001)], None, user(65534), vec![]).unwrap(),
        ));
        assert!(rt.command(&inv).await.is_ok());
        // Never as root, the agent's own user here.
        inv.user = None;
        let err = rt.command(&inv).await.unwrap_err().to_string();
        assert!(err.contains("no user of its own"), "{err}");
    }

    #[tokio::test]
    async fn local_runtime_sets_env_on_the_tool() {
        let rt = ToolRuntime::new(RuntimeKind::Local, None, "tofu".into(), None);
        let ws = PathBuf::from("/srv/work/att-1");
        let cache = PathBuf::from("/srv/cache");
        let (cmd, env_file) = rt
            .command(&tofu_invocation(&ws, &ws, &cache))
            .await
            .unwrap();
        assert!(env_file.is_none());
        assert!(
            cmd.as_std().get_envs().any(|(k, v)| k == "PG_CONN_STR"
                && v.is_some_and(|v| v == "postgres://u:supersecret@db/x"))
        );
    }
}
