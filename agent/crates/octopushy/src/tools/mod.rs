//! Running tools (OpenTofu, Ansible) for attempts: in a rootless container
//! (`podman`, `docker`) or directly (`local`).
//!
//! Secrets never appear on a command line, where `/proc/<pid>/cmdline` would
//! show them: the tool's environment is set on the child process, and container
//! runtimes get only `-e NAME` so they copy the value from their own environment.
//! A tool that takes secrets on stdin gets `-i` and a pipe.

mod lines;

use std::path::{Path, PathBuf};
use std::process::Stdio;
use std::time::Duration;

use anyhow::{Context, Result, bail};
use tokio::io::{AsyncReadExt, AsyncWriteExt};
use tokio::process::{Child, Command};
use tokio::sync::OnceCell;
use tokio::task::{JoinError, JoinHandle};
use tokio_util::sync::CancellationToken;

use crate::attempts::logs::AttemptLogger;

/// Where the attempt workspace appears inside a container.
pub const CONTAINER_WORKSPACE: &str = "/work";
/// Where the provider cache appears inside a container.
pub const CONTAINER_PLUGIN_CACHE: &str = "/octopushy/plugin-cache";

/// Lines of stderr kept for error messages.
const STDERR_TAIL: usize = 20;
/// Largest captured stdout (`show -json`, `output -json`).
const MAX_CAPTURE: u64 = 512 * 1024 * 1024;

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
}

impl Tool {
    fn label(self) -> &'static str {
        match self {
            Tool::OpenTofu => "tofu",
            Tool::Ansible => "ansible-playbook",
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
    /// `--network` for containers.
    pub network: Option<String>,
    /// How long a cancelled tool gets to exit after SIGTERM.
    pub kill_grace: Duration,
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
    /// The input's `env` and `secretEnv`, passed by name only.
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
            network,
            kill_grace: Duration::from_secs(10),
            docker_rootless: OnceCell::new(),
        }
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

    /// Checks that the runtime can start (logs a warning otherwise).
    pub async fn probe(&self) -> Result<String> {
        let (bin, args): (&Path, &[&str]) = match self.kind {
            RuntimeKind::Local => (&self.tofu_binary, &["version"]),
            _ => (&self.container_binary, &["--version"]),
        };
        let out = Command::new(bin)
            .args(args)
            .stdin(Stdio::null())
            .output()
            .await
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

    /// Whether Ansible can run. A container runtime pulls its image when it is needed.
    pub async fn probe_ansible(&self) -> Result<String> {
        if self.is_container() {
            return Ok("from its image".to_owned());
        }
        let out = Command::new(&self.ansible_binary)
            .arg("--version")
            .stdin(Stdio::null())
            .output()
            .await
            .with_context(|| format!("cannot run {}", self.ansible_binary.display()))?;
        if !out.status.success() {
            bail!(
                "{} --version exited with {}",
                self.ansible_binary.display(),
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

    fn local_binary(&self, tool: Tool) -> &Path {
        match tool {
            Tool::OpenTofu => &self.tofu_binary,
            Tool::Ansible => &self.ansible_binary,
        }
    }

    async fn docker_is_rootless(&self) -> bool {
        *self
            .docker_rootless
            .get_or_init(|| async {
                let out = Command::new(&self.container_binary)
                    .args(["info", "--format", "{{json .SecurityOptions}}"])
                    .stdin(Stdio::null())
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

    /// The command line (without the tool's own arguments' secrets: there are
    /// none) and the process to start.
    async fn command(&self, inv: &Invocation<'_>) -> Result<Command> {
        let mut cmd;
        match self.kind {
            RuntimeKind::Local => {
                cmd = Command::new(self.local_binary(inv.tool));
                cmd.args(&inv.args).current_dir(inv.workdir).env_clear();
                if let Some(path) = std::env::var_os("PATH") {
                    cmd.env("PATH", path);
                }
                cmd.envs(inv.settings.iter().chain(&inv.env).map(|(k, v)| (k, v)));
            }
            RuntimeKind::Podman | RuntimeKind::Docker => {
                if inv.image.trim().is_empty() {
                    bail!("the input names no container image");
                }
                for p in [Some(inv.workspace), inv.plugin_cache]
                    .into_iter()
                    .flatten()
                {
                    let s = p.to_string_lossy();
                    if s.contains(':') || s.contains(',') {
                        bail!("{s} cannot be mounted: it contains ':' or ','");
                    }
                }
                cmd = Command::new(&self.container_binary);
                cmd.args(["run", "--rm"]);
                if inv.stdin.is_some() {
                    cmd.arg("-i");
                }
                cmd.args(["--name", &inv.name]);
                cmd.args(["--label", "io.octopushy.attempt=true"]);
                // The tool's output may contain secrets; keep it out of the
                // runtime's log store (journald by default on Fedora).
                cmd.arg("--log-driver=none");
                cmd.arg("--pull=missing");
                // Least privilege: no capabilities, no privilege escalation, a
                // read-only image with a private /tmp.
                cmd.args(["--cap-drop=all", "--security-opt=no-new-privileges"]);
                cmd.args(["--read-only", "--tmpfs", "/tmp:rw,mode=1777"]);
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
                for (k, v) in &inv.settings {
                    cmd.args(["-e".to_owned(), format!("{k}={v}")]);
                }
                // Names only: the runtime copies each value from its own
                // environment, set below on the runtime process.
                for (k, _) in &inv.env {
                    cmd.args(["-e", k]);
                }
                cmd.envs(inv.env.iter().map(|(k, v)| (k, v)));
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
        Ok(cmd)
    }

    /// Runs the tool, streaming its output into the attempt log.
    pub async fn run(
        &self,
        mut inv: Invocation<'_>,
        log: &AttemptLogger,
        cancel: &CancellationToken,
    ) -> Result<ToolOutput> {
        log.agent(format!("$ {} {}", inv.tool.label(), inv.args.join(" ")));
        let mut cmd = self.command(&inv).await?;
        let mut child = cmd.spawn().with_context(|| match self.kind {
            RuntimeKind::Local => {
                format!("cannot start {}", self.local_binary(inv.tool).display())
            }
            _ => format!("cannot start {}", self.container_binary.display()),
        })?;
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
        let mut out_task = {
            let log = log.clone();
            let capture = inv.capture_stdout;
            tokio::spawn(async move {
                if capture {
                    let mut buf = Vec::new();
                    stdout.take(MAX_CAPTURE).read_to_end(&mut buf).await?;
                    Ok::<_, std::io::Error>(buf)
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
            _ = cancel.cancelled() => {
                cancelled = true;
                log.agent("stopping the tool");
                self.terminate(&mut child, &inv.name).await;
                child.wait().await?
            }
        };
        // Something the tool left behind can hold its pipes open after it exits, so the limit
        // has to keep applying until the output has ended. What was read by then decides nothing:
        // a stopped run is uncertain.
        let (mut out_read, mut err_read) = (None, None);
        tokio::select! {
            () = drain(&mut out_task, &mut err_task, &mut out_read, &mut err_read) => {}
            _ = cancel.cancelled() => {
                cancelled = true;
                log.agent("stopping what the tool left running");
                self.kill_group(&inv.name, pgid).await;
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

    /// SIGTERM (forwarded to the tool by the container runtime), then after the
    /// grace period a hard kill of the container and the process group.
    async fn terminate(&self, child: &mut Child, container: &str) {
        let Some(pid) = child.id() else { return };
        let pgid = pid as libc::pid_t;
        // SAFETY: plain signal delivery to our own child's process group.
        unsafe {
            libc::kill(-pgid, libc::SIGTERM);
        }
        if tokio::time::timeout(self.kill_grace, child.wait())
            .await
            .is_ok()
        {
            return;
        }
        self.kill_group(container, Some(pgid)).await;
    }

    /// A hard kill of the container and the process group.
    async fn kill_group(&self, container: &str, pgid: Option<libc::pid_t>) {
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
    }
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

#[cfg(test)]
mod tests {
    use super::*;

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

    #[tokio::test]
    async fn container_command_passes_env_by_name_only() {
        let rt = ToolRuntime::new(
            RuntimeKind::Podman,
            None,
            "tofu".into(),
            Some("octonet".into()),
        );
        let ws = PathBuf::from("/srv/work/att-1");
        let workdir = ws.join("repo/root");
        let cache = PathBuf::from("/srv/cache");
        let inv = Invocation {
            tool: Tool::OpenTofu,
            name: "octopushy-att-1-init".into(),
            image: "ghcr.io/opentofu/opentofu:1.13.1",
            workspace: &ws,
            workdir: &workdir,
            plugin_cache: Some(&cache),
            args: vec!["init".into(), "-input=false".into()],
            settings: vec![("HOME".into(), "/work/home".into())],
            env: vec![("PG_CONN_STR".into(), "postgres://u:supersecret@db/x".into())],
            stdin: None,
            capture_stdout: false,
            stderr_mark: None,
            control_prefix: None,
        };
        let cmd = rt.command(&inv).await.unwrap();
        let std = cmd.as_std();
        let args: Vec<String> = std
            .get_args()
            .map(|a| a.to_string_lossy().into_owned())
            .collect();
        let joined = args.join(" ");
        assert!(!joined.contains("supersecret"), "{joined}");
        assert!(joined.contains("-e PG_CONN_STR "), "{joined}");
        assert!(joined.contains("-e HOME=/work/home"), "{joined}");
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
        let env: Vec<_> = std.get_envs().collect();
        assert!(
            env.iter().any(|(k, v)| *k == "PG_CONN_STR"
                && v.is_some_and(|v| v == "postgres://u:supersecret@db/x"))
        );
        assert!(
            !env.iter().any(|(k, _)| *k == "HOME"),
            "the runtime keeps its own HOME"
        );
    }
}
