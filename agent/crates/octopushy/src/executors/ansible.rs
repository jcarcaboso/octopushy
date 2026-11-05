//! The `ansible` executor: `preview` (what a run would do, with no secrets and no contact with
//! the guest) and `run` (the playbook, with the preview an approval named).
//!
//! Workspace layout of an attempt:
//!
//! ```text
//! <work>/<attempt>/
//!   attempt.json        marker (see attempts::workspace)
//!   repo/               the extracted repository snapshot; `root` is inside it
//!   ansible/            the generated inventory, known_hosts and vars: nothing secret
//!   out/prepared.json   what a prepare playbook wrote
//!   home/               HOME for Ansible
//! ```
//!
//! Secrets never enter the workspace. They reach `octopushy-ansible` on stdin, which keeps
//! them in memory; see `agent/ansible/octopushy-ansible`. The recap, which can hold a templated
//! task name, comes back as a control line on stderr when the playbook ends. It decides the
//! attempt's outcome, so it is parsed as it was written and is never logged; only then is what
//! it says masked and cut. A run with no recap did not end normally, so it is uncertain.

use std::collections::BTreeMap;
use std::fs;
use std::path::{Component, Path, PathBuf};
use std::time::Duration;

use base64::Engine;
use octopushy_proto::v1::Artifact;
use serde::Deserialize;
use serde_json::{Map, Value, json};
use sha2::{Digest, Sha256};
use tokio_util::sync::CancellationToken;

use super::opentofu::archive;
use super::{ExecContext, ExecResult, ExecutorConfig, Failure, Success};
use crate::tls::sha256_hex;
use crate::tools::{Invocation, Tool, ToolOutput};

pub const PREVIEW_ARTIFACT: &str = "preview.json";

/// Largest prepared document, and the most task lines a preview keeps.
const MAX_PREPARED: u64 = 256 * 1024;
const MAX_TASK_LINES: usize = 2000;
const DEFAULT_TIMEOUT_MINUTES: u64 = 120;
const SECRET_DIR_PREFIX: &str = "/tmp/octopushy-attempt-";
const RECAP_PREFIX: &str = "octopushy-recap: ";

/// Exit statuses that mean the tool never started: `octopushy-ansible` refused to run, or
/// the container runtime could not start it.
const NEVER_STARTED: &[i32] = &[78, 125, 126, 127];
/// The longest name or detail kept, after it is masked: cutting first would keep part of a secret.
const MAX_REPORTED: usize = 1024;

/// What ssh says when it never got as far as running anything on the guest.
const NEVER_CONNECTED: &[&str] = &[
    "Host key verification failed",
    "Permission denied",
    "Connection refused",
    "Connection timed out",
    "No route to host",
    "Network is unreachable",
    "Could not resolve hostname",
    "Too many authentication failures",
];

/// What ssh says once sshd has taken a key, as `LogLevel=VERBOSE`. It says so before a session
/// starts, so nothing a forced command prints can take it back. The second is one key of two.
const KEY_TAKEN: &[&str] = &["Authenticated to ", "Authenticated using "];

/// Refused logins that sshd's PerSourcePenalties lets through in a row. Past that, this address
/// would be blocked, and a blocked login says nothing about a key.
const FREE_REFUSALS: usize = 2;
/// Long enough for one refusal's penalty to run out before the next is earned.
const REFUSAL_SPACING: Duration = Duration::from_secs(6);

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
struct Input {
    #[serde(default)]
    image: String,
    root: String,
    action: String,
    playbook: String,
    /// A playbook to run on the agent during `preview`.
    #[serde(default)]
    prepare: Option<String>,
    /// What an earlier run prepared, for an action that reuses it.
    #[serde(default)]
    prepared: Option<Map<String, Value>>,
    host: Host,
    #[serde(default)]
    vars: Map<String, Value>,
    /// What approving a declared outcome adds. Set on the attempt that approval starts.
    #[serde(default)]
    approved_vars: Map<String, Value>,
    /// Extra var name to secret name.
    #[serde(default)]
    secret_vars: BTreeMap<String, String>,
    #[serde(default)]
    secret_files: BTreeMap<String, String>,
    /// The secret holding the SSH private key.
    #[serde(default)]
    ssh_key: Option<String>,
    /// The `octopushy` variable.
    #[serde(default)]
    context: Map<String, Value>,
    #[serde(default)]
    guest_directory: Option<String>,
    /// The outcomes a playbook may report.
    #[serde(default)]
    outcomes: Vec<String>,
    #[serde(default)]
    timeout_minutes: Option<u64>,
    /// What the controller wants an approval to cover, recorded in the preview unchanged.
    #[serde(default)]
    manifest: Value,
    /// The secret holding keys that were removed. The run logs in with each one alone and needs
    /// sshd to turn it away, and then needs `ssh_key` to get in, at that moment and from here.
    #[serde(default)]
    expect_refused: Option<String>,
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
struct Host {
    name: String,
    address: String,
    #[serde(default = "default_port")]
    port: u16,
    user: String,
    /// The pinned public keys, as `type base64 [comment]`, one per line; or, with
    /// `host_key_fingerprints`, the pinned SHA-256 fingerprints as `type SHA256:…`.
    host_key: String,
    /// The pin is fingerprints: those Proxmox logged when it generated a container's host keys.
    #[serde(default)]
    host_key_fingerprints: bool,
}

fn default_port() -> u16 {
    22
}

/// What `octopushy_recap` records, as `octopushy-ansible` reports it.
#[derive(Debug, Default, Deserialize)]
#[serde(rename_all = "camelCase", default)]
struct Recap {
    reached: bool,
    tasks: u64,
    stats: Option<Value>,
    failed_task: Option<String>,
    unreachable: Option<String>,
    unreachable_task: Option<String>,
    outcome: Option<String>,
    detail: Option<String>,
    /// Whether ssh said sshd took the key, read before `unreachable` is cut to size.
    #[serde(skip)]
    key_taken: bool,
}

pub async fn run(cfg: &ExecutorConfig, ctx: &ExecContext) -> ExecResult {
    match ctx.action.as_str() {
        "preview" => preview(cfg, ctx).await,
        "run" => run_playbook(cfg, ctx).await,
        other => Err(Failure::msg(format!(
            "unknown ansible action {other:?}; expected preview or run"
        ))),
    }
}

/// Where the `local` runtime's program keeps an attempt's secrets. It removes them itself; this
/// is for when it could not.
fn secret_dir(attempt: &str) -> PathBuf {
    PathBuf::from(format!("{SECRET_DIR_PREFIX}{attempt}"))
}

/// Removes what a run that was killed left. A container takes its tmpfs with it.
pub fn remove_secret_dir(attempt: &str) {
    let _ = fs::remove_dir_all(secret_dir(attempt));
}

/// A relative path inside the repository, without `..`.
fn relative(path: &str, what: &str) -> Result<PathBuf, Failure> {
    let mut out = PathBuf::new();
    for c in Path::new(path).components() {
        match c {
            Component::Normal(p) => out.push(p),
            Component::CurDir => {}
            _ => {
                return Err(Failure::msg(format!(
                    "{what} {path:?} must be a relative path inside the repository"
                )));
            }
        }
    }
    Ok(out)
}

/// Names that pin the connection or that Octopushy sets itself cannot come from a definition or
/// a prepare step: extra vars win over everything, so one would undo the host key check.
fn check_var_name(name: &str) -> Result<(), Failure> {
    let lower = name.to_ascii_lowercase();
    let valid = !name.is_empty()
        && !name.starts_with(|c: char| c.is_ascii_digit())
        && name.bytes().all(|b| b.is_ascii_alphanumeric() || b == b'_');
    if !valid || lower.starts_with("ansible_") || lower.starts_with("octopushy") {
        return Err(Failure::msg(format!(
            "{name:?} cannot be set as an extra var"
        )));
    }
    Ok(())
}

fn check_vars(vars: &Map<String, Value>) -> Result<(), Failure> {
    vars.keys().try_for_each(|name| check_var_name(name))
}

/// The `known_hosts` lines for the pinned keys, and their SHA-256 fingerprints.
fn known_hosts(host: &Host) -> Result<(String, Vec<String>), Failure> {
    let name = if host.port == 22 {
        host.address.clone()
    } else {
        format!("[{}]:{}", host.address, host.port)
    };
    let mut lines = String::new();
    let mut fingerprints = Vec::new();
    for line in host
        .host_key
        .lines()
        .map(str::trim)
        .filter(|l| !l.is_empty())
    {
        let mut parts = line.split_whitespace();
        let (Some(kind), Some(key)) = (parts.next(), parts.next()) else {
            return Err(Failure::msg(format!(
                "the pinned host key for {} is not `type base64`",
                host.name
            )));
        };
        let blob = base64::engine::general_purpose::STANDARD.decode(key);
        let known = kind.starts_with("ssh-")
            || kind.starts_with("ecdsa-")
            || kind.starts_with("sk-ssh-")
            || kind.starts_with("sk-ecdsa-");
        let (true, Ok(blob)) = (known, blob) else {
            return Err(Failure::msg(format!(
                "the pinned host key for {} is not `type base64`",
                host.name
            )));
        };
        lines.push_str(&format!("{name} {kind} {key}\n"));
        let digest = base64::engine::general_purpose::STANDARD_NO_PAD.encode(Sha256::digest(&blob));
        fingerprints.push(format!("{kind} SHA256:{digest}"));
    }
    if lines.is_empty() {
        return Err(Failure::msg(format!(
            "the pinned host key for {} is empty",
            host.name
        )));
    }
    Ok((lines, fingerprints))
}

/// A container's host keys pinned by fingerprint: `rsa`, `ecdsa` or `ed25519` and its
/// `SHA256:…`, one per line, as Proxmox's create task logged them.
fn pinned_fingerprints(host: &Host) -> Result<Vec<(String, String)>, Failure> {
    let mut pins = Vec::new();
    for line in host
        .host_key
        .lines()
        .map(str::trim)
        .filter(|l| !l.is_empty())
    {
        let mut parts = line.split_whitespace();
        let (Some(kind), Some(fingerprint), None) = (parts.next(), parts.next(), parts.next())
        else {
            return Err(Failure::msg(format!(
                "the pinned host key fingerprint for {} is not `type SHA256:…`",
                host.name
            )));
        };
        if host_key_algorithms(kind).is_none()
            || !octopushy_proxmox::is_sha256_fingerprint(fingerprint)
        {
            return Err(Failure::msg(format!(
                "the pinned host key fingerprint for {} is not `type SHA256:…`",
                host.name
            )));
        }
        pins.push((kind.to_owned(), fingerprint.to_owned()));
    }
    if pins.is_empty() {
        return Err(Failure::msg(format!(
            "the pinned host key fingerprints for {} are empty",
            host.name
        )));
    }
    Ok(pins)
}

/// What ssh may negotiate for a host key of this type, as ssh-keygen names the type.
fn host_key_algorithms(kind: &str) -> Option<&'static str> {
    match kind {
        "ed25519" => Some("ssh-ed25519"),
        "ecdsa" => Some("ecdsa-sha2-nistp256,ecdsa-sha2-nistp384,ecdsa-sha2-nistp521"),
        "rsa" => Some("rsa-sha2-512,rsa-sha2-256"),
        _ => None,
    }
}

/// The `KnownHostsCommand` for a pin by fingerprint. ssh runs it once it has the server's host
/// key, with `%I %H %f %t %K`: why it asks, the name it looks up in known_hosts form, the key's
/// SHA-256 fingerprint, its type and the key itself. It prints a known_hosts line for exactly that
/// key when its fingerprint is pinned, and nothing otherwise, so `StrictHostKeyChecking=yes` refuses
/// any other key as it refuses one missing from known_hosts. A fingerprint names one key: the line
/// it prints is the key the pin was made for, whoever sent it. Asked for the order of algorithms
/// (`ORDER`, before any key), it prints nothing.
fn known_hosts_command(pins: &[(String, String)]) -> String {
    let patterns = pins
        .iter()
        .map(|(_, fingerprint)| format!("'{fingerprint}'"))
        .collect::<Vec<_>>()
        .join("|");
    format!(
        "# Written by Octopushy for one attempt: the host keys of a container, pinned by fingerprint.\n\
         [ \"$1\" = HOSTNAME ] || [ \"$1\" = ADDRESS ] || exit 0\n\
         [ \"$#\" -eq 5 ] || exit 0\n\
         case \"$3\" in\n\
         {patterns}) printf '%s %s %s\\n' \"$2\" \"$4\" \"$5\" ;;\n\
         esac\n\
         exit 0\n"
    )
}

struct Prepared {
    input: Input,
    root: PathBuf,
    generated: PathBuf,
    out_dir: PathBuf,
    settings: Vec<(String, String)>,
    fingerprints: Vec<String>,
}

fn create(path: &Path) -> Result<(), Failure> {
    fs::create_dir_all(path).map_err(|e| Failure::msg(format!("creating {}: {e}", path.display())))
}

fn write(path: &Path, contents: &[u8]) -> Result<(), Failure> {
    fs::write(path, contents).map_err(|e| Failure::msg(format!("writing {}: {e}", path.display())))
}

/// Validates the input, extracts the repository, and writes what is not secret.
fn prepare(cfg: &ExecutorConfig, ctx: &ExecContext) -> Result<Prepared, Failure> {
    let input: Input = ctx.input()?;
    let rt = &cfg.runtime;
    let ws = &ctx.workspace;
    if rt.is_container() && input.image.trim().is_empty() {
        return Err(Failure::msg("the input names no Ansible image"));
    }
    if !rt.is_container() && !input.image.trim().is_empty() {
        ctx.log.agent(format!(
            "the local tool runtime ignores the image {}: Ansible is the one in the agent's image",
            input.image
        ));
    }
    check_vars(&input.vars)?;
    check_vars(&input.approved_vars)?;
    if let Some(prepared) = &input.prepared {
        check_vars(prepared)?;
    }
    for name in input.secret_vars.keys().chain(input.secret_files.keys()) {
        check_var_name(name)?;
    }
    let host_name = &input.host.name;
    if host_name.is_empty()
        || !host_name
            .bytes()
            .all(|b| b.is_ascii_alphanumeric() || b == b'-')
    {
        return Err(Failure::msg(format!("invalid guest name {host_name:?}")));
    }
    if input.host.address.is_empty()
        || input
            .host
            .address
            .bytes()
            .any(|b| b.is_ascii_whitespace() || b == b',')
    {
        return Err(Failure::msg("invalid guest address"));
    }
    if !input.host.user.is_empty()
        && !input
            .host
            .user
            .bytes()
            .all(|b| b.is_ascii_alphanumeric() || b"._-".contains(&b))
    {
        return Err(Failure::msg("invalid login user"));
    }
    let pins = if input.host.host_key_fingerprints {
        Some(pinned_fingerprints(&input.host)?)
    } else {
        None
    };
    let (known, fingerprints) = match &pins {
        // Nothing in the file: the command vouches for the one key whose fingerprint is pinned.
        Some(pins) => (
            String::new(),
            pins.iter().map(|(k, f)| format!("{k} {f}")).collect(),
        ),
        None => known_hosts(&input.host)?,
    };

    let home = ws.join("home");
    let out_dir = ws.join("out");
    let generated = ws.join("ansible");
    create(&home)?;
    create(&out_dir)?;
    create(&generated)?;

    let repo = ws.join("repo");
    let stats = archive::extract_tar_gz(&ctx.files_tar_gz, &repo, archive::Limits::default())
        .map_err(|e| Failure::msg(format!("cannot extract the repository files: {e:#}")))?;
    ctx.log.agent(format!(
        "extracted {} files ({} KiB) for {}",
        stats.files,
        stats.bytes / 1024,
        input.host.name
    ));
    let root = repo.join(relative(&input.root, "root")?);
    let canonical = root
        .canonicalize()
        .map_err(|_| Failure::msg(format!("root {:?} is not in the repository", input.root)))?;
    let canonical_repo = repo.canonicalize().map_err(anyhow::Error::from)?;
    if !canonical.starts_with(&canonical_repo) || !canonical.is_dir() {
        return Err(Failure::msg(format!(
            "root {:?} is not a directory in the repository",
            input.root
        )));
    }
    for (what, path) in [
        ("playbook", Some(&input.playbook)),
        ("prepare playbook", input.prepare.as_ref()),
    ] {
        let Some(path) = path else { continue };
        let found = canonical
            .join(relative(path, what)?)
            .canonicalize()
            .map_err(|_| Failure::msg(format!("the {what} {path:?} is not in {}", input.root)))?;
        if !found.starts_with(&canonical) || !found.is_file() {
            return Err(Failure::msg(format!(
                "the {what} {path:?} is not a file in {}",
                input.root
            )));
        }
    }

    write(
        &generated.join("inventory.ini"),
        format!("{host_name}\n").as_bytes(),
    )?;
    write(&generated.join("known_hosts"), known.as_bytes())?;
    let tool_path = |p: &Path| -> Result<String, Failure> { Ok(rt.path(ws, p)?) };
    let known_path = tool_path(&generated.join("known_hosts"))?;
    // Extra vars outrank a play's own, and the first `-o` of an option wins. Together these
    // keep ansible_ssh_common_args or a play's vars from undoing the host key check.
    let mut ssh_args = vec![
        "-F /dev/null".to_owned(),
        format!("-o UserKnownHostsFile={known_path}"),
        "-o GlobalKnownHostsFile=/dev/null".to_owned(),
        "-o StrictHostKeyChecking=yes".to_owned(),
        "-o HashKnownHosts=no".to_owned(),
        "-o BatchMode=yes".to_owned(),
        "-o PasswordAuthentication=no".to_owned(),
        "-o KbdInteractiveAuthentication=no".to_owned(),
        "-o PreferredAuthentications=publickey".to_owned(),
        // The key in the private agent and nothing else: not a default key in a home directory,
        // which is what a run on the local runtime would otherwise offer too.
        "-o IdentityFile=none".to_owned(),
        "-o ControlMaster=no".to_owned(),
        "-o ControlPath=none".to_owned(),
        "-o ServerAliveInterval=15".to_owned(),
        "-o ServerAliveCountMax=8".to_owned(),
    ];
    if let Some(pins) = &pins {
        let command = generated.join("known-hosts-command.sh");
        write(&command, known_hosts_command(pins).as_bytes())?;
        let command = tool_path(&command)?;
        if command
            .bytes()
            .any(|b| b.is_ascii_whitespace() || b == b'"' || b == b'%')
        {
            return Err(Failure::msg(format!(
                "the workspace path {command:?} cannot be named in a KnownHostsCommand"
            )));
        }
        let mut algorithms: Vec<&str> = Vec::new();
        for (kind, _) in pins {
            let named = host_key_algorithms(kind).expect("checked with the pin");
            if !algorithms.contains(&named) {
                algorithms.push(named);
            }
        }
        ssh_args.push("-o FingerprintHash=sha256".to_owned());
        ssh_args.push(format!("-o HostKeyAlgorithms={}", algorithms.join(",")));
        ssh_args.push(format!(
            "-o \"KnownHostsCommand=/bin/sh {command} %I %H %f %t %K\""
        ));
    }
    if input.expect_refused.is_some() {
        ssh_args.push("-o LogLevel=VERBOSE".to_owned());
    }
    let ssh_args = ssh_args.join(" ");
    let mut octopushy = input.context.clone();
    octopushy.insert("repository".into(), json!(tool_path(&repo)?));
    if let Some(directory) = &input.guest_directory {
        let path = repo.join(relative(directory, "guest directory")?);
        octopushy.insert("guest_directory".into(), json!(tool_path(&path)?));
    }
    let connection = json!({
        "ansible_connection": "ssh",
        "ansible_host": input.host.address,
        "ansible_port": input.host.port,
        "ansible_user": input.host.user,
        "ansible_host_key_checking": true,
        "ansible_ssh_host_key_checking": true,
        "ansible_ssh_args": ssh_args,
        "ansible_ssh_executable": "/usr/bin/ssh",
        "ansible_scp_executable": "/usr/bin/scp",
        "ansible_sftp_executable": "/usr/bin/sftp",
        "octopushy": octopushy,
    });
    write(
        &generated.join("connection.json"),
        connection.to_string().as_bytes(),
    )?;
    write(
        &generated.join("vars.json"),
        Value::Object(input.vars.clone()).to_string().as_bytes(),
    )?;

    let settings = vec![("HOME".to_owned(), tool_path(&home)?)];
    Ok(Prepared {
        input,
        root: canonical,
        generated,
        out_dir,
        settings,
        fingerprints,
    })
}

/// Runs `octopushy-ansible` for one attempt.
struct Ansible<'a> {
    cfg: &'a ExecutorConfig,
    ctx: &'a ExecContext,
    prep: &'a Prepared,
}

impl Ansible<'_> {
    fn path(&self, host: &Path) -> Result<String, Failure> {
        Ok(self.cfg.runtime.path(&self.ctx.workspace, host)?)
    }

    fn generated(&self, name: &str) -> Result<String, Failure> {
        self.path(&self.prep.generated.join(name))
    }

    #[allow(clippy::too_many_arguments)]
    async fn run(
        &self,
        step: &str,
        args: Vec<String>,
        settings: Vec<(String, String)>,
        stdin: Option<Vec<u8>>,
        capture: bool,
        stop: &CancellationToken,
    ) -> Result<ToolOutput, Failure> {
        let mut name = format!("octopushy-{}-{step}", self.ctx.attempt_id);
        name.truncate(200);
        Ok(self
            .cfg
            .runtime
            .run(
                Invocation {
                    tool: Tool::Ansible,
                    name,
                    image: &self.prep.input.image,
                    workspace: &self.ctx.workspace,
                    workdir: &self.prep.root,
                    plugin_cache: None,
                    args,
                    settings: self.prep.settings.iter().cloned().chain(settings).collect(),
                    env: Vec::new(),
                    stdin,
                    capture_stdout: capture,
                    stderr_mark: None,
                    control_prefix: Some(RECAP_PREFIX),
                },
                &self.ctx.log,
                stop,
            )
            .await?)
    }

    /// Runs a step of a preview, which must exit 0.
    async fn run_ok(
        &self,
        step: &str,
        args: Vec<String>,
        settings: Vec<(String, String)>,
        capture: bool,
    ) -> Result<ToolOutput, Failure> {
        let out = self
            .run(step, args, settings, None, capture, &self.ctx.cancel)
            .await?;
        if out.cancelled {
            return Err(Failure::Cancelled);
        }
        if out.code != Some(0) {
            return Err(step_failed(step, &out));
        }
        Ok(out)
    }

    /// The arguments every playbook run shares. The connection comes last so it wins.
    fn common_args(&self, extra: &[String]) -> Result<Vec<String>, Failure> {
        let mut args = vec!["-i".to_owned(), self.generated("inventory.ini")?];
        args.extend([
            "-e".to_owned(),
            format!("@{}", self.generated("vars.json")?),
        ]);
        for file in extra {
            args.extend(["-e".to_owned(), format!("@{file}")]);
        }
        args.extend([
            "-e".to_owned(),
            format!("@{}", self.generated("connection.json")?),
        ]);
        Ok(args)
    }
}

fn step_failed(step: &str, out: &ToolOutput) -> Failure {
    let code = exit_text(out);
    let detail = out.stderr_summary(5);
    Failure::msg(if detail.is_empty() {
        format!("ansible-playbook {step} failed (exit status {code})")
    } else {
        format!("ansible-playbook {step} failed (exit status {code}): {detail}")
    })
}

fn exit_text(out: &ToolOutput) -> String {
    out.code
        .map(|c| c.to_string())
        .unwrap_or_else(|| "a signal".into())
}

/// What `--list-tasks` prints, without its blank lines. It can hold a task's name, not a value.
fn task_lines(stdout: &[u8]) -> Vec<String> {
    String::from_utf8_lossy(stdout)
        .lines()
        .map(str::trim_end)
        .filter(|l| !l.trim().is_empty())
        .take(MAX_TASK_LINES)
        .map(str::to_owned)
        .collect()
}

fn read_prepared(path: &Path) -> Result<Map<String, Value>, Failure> {
    let size = fs::metadata(path)
        .map_err(|_| Failure::msg("the prepare playbook wrote nothing to OCTOPUSHY_PREPARED_FILE"))?
        .len();
    if size > MAX_PREPARED {
        return Err(Failure::msg(format!(
            "the prepare playbook wrote {size} bytes; the limit is {MAX_PREPARED}"
        )));
    }
    let text = fs::read(path).map_err(anyhow::Error::from)?;
    let value: Value = serde_json::from_slice(&text)
        .map_err(|e| Failure::msg(format!("the prepare playbook did not write JSON: {e}")))?;
    let Value::Object(prepared) = value else {
        return Err(Failure::msg(
            "the prepare playbook must write a JSON object",
        ));
    };
    check_vars(&prepared)?;
    Ok(prepared)
}

async fn preview(cfg: &ExecutorConfig, ctx: &ExecContext) -> ExecResult {
    let prep = prepare(cfg, ctx)?;
    let tool = Ansible {
        cfg,
        ctx,
        prep: &prep,
    };
    let version_out = tool
        .run_ok("version", vec!["--version".into()], Vec::new(), true)
        .await?;
    let tool_version = String::from_utf8_lossy(&version_out.stdout)
        .lines()
        .next()
        .unwrap_or("")
        .trim()
        .to_owned();
    ctx.log.agent(tool_version.clone());

    let playbook = tool.path(&prep.root.join(relative(&prep.input.playbook, "playbook")?))?;
    // Secrets are not here, and the check needs their names: a vars file may refer to them.
    let names: Map<String, Value> = prep
        .input
        .secret_vars
        .keys()
        .chain(prep.input.secret_files.keys())
        .map(|name| (name.clone(), json!("(secret)")))
        .collect();
    write(
        &prep.generated.join("placeholders.json"),
        Value::Object(names).to_string().as_bytes(),
    )?;
    let placeholders = tool.generated("placeholders.json")?;
    let mut args = tool.common_args(std::slice::from_ref(&placeholders))?;
    args.push(playbook);
    let mut check = args.clone();
    check.push("--syntax-check".into());
    tool.run_ok("syntax-check", check, Vec::new(), false)
        .await?;
    let mut list = args;
    list.push("--list-tasks".into());
    let listed = tool.run_ok("list-tasks", list, Vec::new(), true).await?;
    let tasks = task_lines(&listed.stdout);

    let prepared = match (&prep.input.prepare, &prep.input.prepared) {
        (Some(_), Some(_)) => {
            return Err(Failure::msg(
                "an action either prepares or reuses a snapshot, not both",
            ));
        }
        (None, reused) => reused.clone(),
        (Some(path), None) => {
            let file = prep.out_dir.join("prepared.json");
            let target = tool.path(&prep.root.join(relative(path, "prepare playbook")?))?;
            // The agent itself: `hosts: localhost` needs no route to, or secret for, the guest.
            write(
                &prep.generated.join("prepare.json"),
                json!({ "ansible_python_interpreter": "{{ ansible_playbook_python }}" })
                    .to_string()
                    .as_bytes(),
            )?;
            let args = vec![
                "-i".to_owned(),
                "localhost,".to_owned(),
                "-c".to_owned(),
                "local".to_owned(),
                "-e".to_owned(),
                format!("@{}", tool.generated("vars.json")?),
                "-e".to_owned(),
                format!("@{placeholders}"),
                "-e".to_owned(),
                format!("@{}", tool.generated("prepare.json")?),
                target,
            ];
            tool.run_ok(
                "prepare",
                args,
                vec![("OCTOPUSHY_PREPARED_FILE".to_owned(), tool.path(&file)?)],
                false,
            )
            .await?;
            Some(read_prepared(&file)?)
        }
    };

    let document = json!({
        "schemaVersion": 1,
        "action": prep.input.action,
        "playbook": prep.input.playbook,
        "tasks": tasks,
        "prepared": prepared,
        "toolVersion": tool_version,
        "hostKeyFingerprints": prep.fingerprints,
        "manifest": prep.input.manifest,
    });
    let content = serde_json::to_vec(&document).map_err(anyhow::Error::from)?;
    let sha256 = sha256_hex(&content);
    ctx.log.agent(format!(
        "preview: {} task lines (artifact sha256 {sha256})",
        tasks.len()
    ));
    Ok(Success {
        outputs: document,
        artifacts: vec![Artifact {
            name: PREVIEW_ARTIFACT.into(),
            content,
            sha256,
        }],
    })
}

/// The preview's prepared values, from the artifact an approval named.
fn approved_preview(ctx: &ExecContext) -> Result<Option<Map<String, Value>>, Failure> {
    let artifact = ctx
        .artifacts
        .iter()
        .find(|a| a.name == PREVIEW_ARTIFACT)
        .ok_or_else(|| {
            Failure::msg(format!(
                "the assignment carries no {PREVIEW_ARTIFACT} artifact"
            ))
        })?;
    let actual = sha256_hex(&artifact.content);
    if !artifact.sha256.eq_ignore_ascii_case(&actual) {
        return Err(Failure::msg(format!(
            "{PREVIEW_ARTIFACT} is corrupt: sha256 {actual}, expected {}",
            artifact.sha256
        )));
    }
    let document: Value = serde_json::from_slice(&artifact.content)
        .map_err(|e| Failure::msg(format!("{PREVIEW_ARTIFACT} is not JSON: {e}")))?;
    match document.get("prepared") {
        Some(Value::Object(prepared)) => {
            check_vars(prepared)?;
            Ok(Some(prepared.clone()))
        }
        Some(Value::Null) | None => Ok(None),
        Some(_) => Err(Failure::msg(format!(
            "{PREVIEW_ARTIFACT} holds prepared values that are not an object"
        ))),
    }
}

/// The document `octopushy-ansible` reads from stdin.
fn secrets_document(input: &Input, ctx: &ExecContext, key: &str) -> Result<Vec<u8>, Failure> {
    let resolve = |names: &BTreeMap<String, String>| -> Result<Map<String, Value>, Failure> {
        names
            .iter()
            .map(|(name, secret)| Ok((name.clone(), json!(ctx.secrets.get(secret)?))))
            .collect()
    };
    let document = json!({
        "sshKey": key,
        "secretVars": resolve(&input.secret_vars)?,
        "secretFiles": resolve(&input.secret_files)?,
    });
    Ok(serde_json::to_vec(&document).map_err(anyhow::Error::from)?)
}

/// The SSH key the input names.
fn own_key<'c>(input: &Input, ctx: &'c ExecContext) -> Result<&'c str, Failure> {
    let name = input
        .ssh_key
        .as_ref()
        .ok_or_else(|| Failure::msg("the input names no SSH key"))?;
    ctx.secrets.get(name)
}

/// The keys a value holds, one after another, each ending at its PEM end line.
fn split_keys(value: &str) -> Result<Vec<String>, Failure> {
    let mut keys = Vec::new();
    let mut key = String::new();
    for line in value.lines() {
        if key.is_empty() && line.trim().is_empty() {
            continue;
        }
        key.push_str(line);
        key.push('\n');
        if line.trim_start().starts_with("-----END ") {
            keys.push(std::mem::take(&mut key));
        }
    }
    if keys.is_empty() || !key.is_empty() {
        return Err(Failure::msg("the keys to try could not be told apart"));
    }
    Ok(keys)
}

/// One `ansible-playbook` run, under the run's limit.
struct Play<'a> {
    tool: Ansible<'a>,
    args: Vec<String>,
    settings: Vec<(String, String)>,
    minutes: u64,
}

impl Play<'_> {
    async fn run(&self, step: &str, stdin: Vec<u8>) -> Result<ToolOutput, Failure> {
        let stop = self.tool.ctx.cancel.child_token();
        let timer = {
            let stop = stop.clone();
            let minutes = self.minutes;
            tokio::spawn(async move {
                tokio::time::sleep(Duration::from_secs(minutes.saturating_mul(60))).await;
                stop.cancel();
            })
        };
        let out = self
            .tool
            .run(
                step,
                self.args.clone(),
                self.settings.clone(),
                Some(stdin),
                false,
                &stop,
            )
            .await;
        timer.abort();
        out
    }

    async fn once(&self) -> ExecResult {
        let (prep, ctx) = (self.tool.prep, self.tool.ctx);
        let stdin = secrets_document(&prep.input, ctx, own_key(&prep.input, ctx)?)?;
        let out = self.run("playbook", stdin).await?;
        let recap = read_recap(&out, ctx);
        settle(prep, &out, recap, self.minutes)
    }

    /// Each key alone, so sshd's answer is about that key and every one of them was offered.
    /// The key in use comes last: a refusal only means something if sshd takes a key that is
    /// authorized, at that moment, from this address.
    async fn confirm_refused(&self, secret: &str) -> ExecResult {
        let (prep, ctx) = (self.tool.prep, self.tool.ctx);
        let keys = split_keys(ctx.secrets.get(secret)?)?;
        let total = keys.len();
        for (index, key) in keys.iter().enumerate() {
            if index >= FREE_REFUSALS {
                tokio::time::sleep(REFUSAL_SPACING).await;
            }
            let which = format!("old key {} of {total}", index + 1);
            ctx.log
                .agent(format!("logging in with {which}, which sshd has to refuse"));
            let stdin = secrets_document(&prep.input, ctx, key)?;
            let out = self.run(&format!("refused-{index}"), stdin).await?;
            let recap = read_recap(&out, ctx);
            settle_refused(prep, &out, recap, self.minutes, &which)?;
        }
        ctx.log
            .agent("logging in with the key in use, which sshd has to take");
        let stdin = secrets_document(&prep.input, ctx, own_key(&prep.input, ctx)?)?;
        let out = self.run("control", stdin).await?;
        let recap = read_recap(&out, ctx);
        let mut success = settle(prep, &out, recap, self.minutes).map_err(|failure| {
            Failure::msg(format!(
                "the key in use did not log in straight afterwards, so sshd turning the old keys away shows nothing about them: {}",
                failure.message()
            ))
        })?;
        success.outputs["refusedKeys"] = json!(total);
        Ok(success)
    }
}

async fn run_playbook(cfg: &ExecutorConfig, ctx: &ExecContext) -> ExecResult {
    let prepared = approved_preview(ctx)?;
    let prep = prepare(cfg, ctx)?;
    let tool = Ansible {
        cfg,
        ctx,
        prep: &prep,
    };
    let mut extra = Vec::new();
    if let Some(prepared) = &prepared {
        write(
            &prep.generated.join("prepared.json"),
            Value::Object(prepared.clone()).to_string().as_bytes(),
        )?;
        extra.push(tool.generated("prepared.json")?);
    }
    if !prep.input.approved_vars.is_empty() {
        write(
            &prep.generated.join("approved.json"),
            Value::Object(prep.input.approved_vars.clone())
                .to_string()
                .as_bytes(),
        )?;
        extra.push(tool.generated("approved.json")?);
        ctx.log.agent(format!(
            "with the approved variables: {}",
            prep.input
                .approved_vars
                .keys()
                .cloned()
                .collect::<Vec<_>>()
                .join(", ")
        ));
    }
    let mut args = tool.common_args(&extra)?;
    args.push(tool.path(&prep.root.join(relative(&prep.input.playbook, "playbook")?))?);

    let mut settings = vec![("OCTOPUSHY_STDIN".to_owned(), "1".to_owned())];
    if !cfg.runtime.is_container() {
        settings.push((
            "OCTOPUSHY_SECRET_DIR".to_owned(),
            secret_dir(&ctx.attempt_id).to_string_lossy().into_owned(),
        ));
    }

    let minutes = prep
        .input
        .timeout_minutes
        .unwrap_or(DEFAULT_TIMEOUT_MINUTES);
    let play = Play {
        tool,
        args,
        settings,
        minutes,
    };
    let result = match &prep.input.expect_refused {
        Some(secret) => play.confirm_refused(secret).await,
        None => play.once().await,
    };
    if !cfg.runtime.is_container() {
        remove_secret_dir(&ctx.attempt_id);
    }
    result
}

/// The recap, with what is free text masked and then cut to size. Masking comes after parsing
/// so it cannot rename a field, and a value is matched as it is, not as JSON escaped it. ssh
/// gives its reason last, so what it said keeps its end.
fn read_recap(out: &ToolOutput, ctx: &ExecContext) -> Option<Recap> {
    let mut recap: Recap = serde_json::from_str(out.control.as_deref()?).ok()?;
    recap.key_taken = recap
        .unreachable
        .as_deref()
        .is_some_and(|m| KEY_TAKEN.iter().any(|s| m.contains(s)));
    for value in [
        &mut recap.failed_task,
        &mut recap.unreachable_task,
        &mut recap.detail,
    ]
    .into_iter()
    .flatten()
    {
        *value = ctx.log.mask(value).chars().take(MAX_REPORTED).collect();
    }
    if let Some(value) = &mut recap.unreachable {
        let masked = ctx.log.mask(value);
        let skip = masked.chars().count().saturating_sub(MAX_REPORTED);
        *value = masked.chars().skip(skip).collect();
    }
    Some(recap)
}

/// Whether ssh failed before anything ran on the guest.
fn never_connected(recap: &Recap) -> bool {
    !recap.reached
        && recap
            .unreachable
            .as_deref()
            .is_some_and(|m| NEVER_CONNECTED.iter().any(|s| m.contains(s)))
}

/// What a run's exit status and recap mean. Whatever cannot be shown to have stayed off the
/// guest, or to have ended there, is uncertain.
fn settle(prep: &Prepared, out: &ToolOutput, recap: Option<Recap>, minutes: u64) -> ExecResult {
    let recap = finished(out, recap, minutes)?;
    let code = exit_text(out);
    // Before any outcome, and even at exit 0: a play that ignores an unreachable host has not
    // found out how that task ended, and an earlier outcome says nothing about it.
    if recap.unreachable.is_some() {
        if never_connected(&recap) {
            return Err(unreachable_failure(prep, &recap, &outputs(out, &recap)));
        }
        return Err(Failure::Uncertain(format!(
            "the guest stopped answering during {}; whether the task finished is unknown. Inspect the guest, and any lock it holds, before running it again",
            recap.unreachable_task.as_deref().unwrap_or("a task")
        )));
    }
    if let Some(outcome) = &recap.outcome {
        if !prep.input.outcomes.iter().any(|o| o == outcome) {
            // Not repeated: only a declared name is known not to be a secret.
            return Err(Failure::with_outputs(
                "the playbook reported an outcome the workflow does not declare",
                outputs(out, &recap),
            ));
        }
        let mut result = outputs(out, &recap);
        result["outcome"] = json!(outcome);
        result["detail"] = json!(recap.detail);
        return Ok(Success::outputs(result));
    }
    if out.code == Some(0) {
        return Ok(Success::outputs(outputs(out, &recap)));
    }
    Err(Failure::with_outputs(
        match recap.failed_task.as_deref() {
            Some(task) => format!("the task {task:?} failed (exit status {code})"),
            None => format!("ansible-playbook failed (exit status {code})"),
        },
        outputs(out, &recap),
    ))
}

/// What the recap says, once it is known the run ended and left one.
fn finished(out: &ToolOutput, recap: Option<Recap>, minutes: u64) -> Result<Recap, Failure> {
    if out.cancelled {
        return Err(Failure::Uncertain(format!(
            "the playbook passed its limit of {minutes} minutes and was stopped; whether it finished what it was doing is unknown. Inspect the guest, and any lock it holds, before running it again"
        )));
    }
    let code = exit_text(out);
    let Some(recap) = recap else {
        if out.code.is_some_and(|c| NEVER_STARTED.contains(&c)) {
            return Err(Failure::msg(failed_before_tasks(out)));
        }
        return Err(Failure::Uncertain(format!(
            "ansible-playbook ended (exit status {code}) without saying how far it got; whether a change was made is unknown. Inspect the guest, and any lock it holds, before running it again"
        )));
    };
    Ok(recap)
}

fn outputs(out: &ToolOutput, recap: &Recap) -> Value {
    json!({
        "exitCode": out.code,
        "tasks": recap.tasks,
        "recap": recap.stats,
        "failedTask": recap.failed_task,
        "unreachableTask": recap.unreachable_task,
    })
}

/// Only sshd's own refusal counts: a guest that did not answer has said nothing about a key.
fn settle_refused(
    prep: &Prepared,
    out: &ToolOutput,
    recap: Option<Recap>,
    minutes: u64,
    which: &str,
) -> Result<(), Failure> {
    let recap = finished(out, recap, minutes)?;
    // A session that failed after the login reads like a refusal, down to the words if a forced
    // command runs an ssh of its own. Whether sshd took the key decides it.
    let refused = !recap.reached
        && !recap.key_taken
        && recap
            .unreachable
            .as_deref()
            .is_some_and(|m| refusal_of(m, &prep.input.host));
    if refused {
        return Ok(());
    }
    let result = outputs(out, &recap);
    if recap.reached || recap.key_taken {
        return Err(Failure::with_outputs(
            format!(
                "sshd still accepts {which}, which was removed. It is not in authorized_keys, so another file or setting authorizes it; it stays listed as authorized until sshd refuses it"
            ),
            result,
        ));
    }
    Err(unreachable_failure(prep, &recap, &result))
}

/// Whether ssh ended by giving up on this login. It says so last, naming the login and the host,
/// lowercased as ssh does, with sshd's methods in the bracket. Anything before that can be a
/// banner, and a failure without those words can be a local firewall.
fn refusal_of(message: &str, host: &Host) -> bool {
    let last = message.trim_end().lines().last().unwrap_or("");
    let said = last.split_once("via ssh: ").map_or(last, |(_, said)| said);
    let verdict = format!(
        "{}@{}: Permission denied (",
        host.user,
        host.address.to_ascii_lowercase()
    );
    said.starts_with(&verdict) && said.ends_with(").")
}

fn failed_before_tasks(out: &ToolOutput) -> String {
    let detail = out.stderr_summary(5);
    let code = exit_text(out);
    if detail.is_empty() {
        format!("ansible-playbook stopped before any task ran (exit status {code})")
    } else {
        format!("ansible-playbook stopped before any task ran (exit status {code}): {detail}")
    }
}

fn unreachable_failure(prep: &Prepared, recap: &Recap, outputs: &Value) -> Failure {
    let message = recap.unreachable.as_deref().unwrap_or("");
    if message.contains("Host key verification failed") {
        return Failure::with_outputs(
            format!(
                "the guest's SSH host key is not the pinned one ({}). If the guest was rebuilt, read its new key through Proxmox and update the store; nothing ran",
                prep.fingerprints.join(", ")
            ),
            outputs.clone(),
        );
    }
    let reason = message
        .lines()
        .rev()
        .map(str::trim)
        .find(|l| !l.is_empty())
        .unwrap_or("no reason given");
    Failure::with_outputs(
        format!("could not reach the guest over SSH, so nothing ran: {reason}"),
        outputs.clone(),
    )
}

#[cfg(test)]
mod tests {
    use super::*;

    fn host(key: &str, port: u16) -> Host {
        Host {
            name: "g".into(),
            address: "10.0.0.5".into(),
            port,
            user: "ops".into(),
            host_key: key.into(),
            host_key_fingerprints: false,
        }
    }

    fn login(address: &str, user: &str) -> Host {
        Host {
            name: "g".into(),
            address: address.into(),
            port: 22,
            user: user.into(),
            host_key: String::new(),
            host_key_fingerprints: false,
        }
    }

    #[test]
    fn a_refusal_is_what_ssh_says_last_about_this_login() {
        let via = "Task failed: Failed to connect to the host via ssh: ";
        let guest = login("10.0.0.5", "ops");
        for message in [
            "ops@10.0.0.5: Permission denied (publickey).",
            "ops@10.0.0.5: Permission denied (publickey,password).\r\n",
            "a banner\r\nmore banner\r\nops@10.0.0.5: Permission denied (publickey).\r\n",
        ] {
            assert!(refusal_of(message, &guest), "{message:?}");
            assert!(
                refusal_of(&format!("{via}{message}"), &guest),
                "{message:?}"
            );
        }
        for message in [
            "",
            "(too long to report)",
            "Permission denied (publickey).",
            "ops@inner: Permission denied (publickey).",
            "other@10.0.0.5: Permission denied (publickey).",
            "ops@10.0.0.5: Permission denied",
            "ssh: connect to host 10.0.0.5 port 22: Permission denied",
            // A banner can say anything, and what comes after it is what happened.
            "ops@10.0.0.5: Permission denied (publickey).\nConnection closed by 10.0.0.5 port 22",
            "ops@10.0.0.5: Permission denied (publickey).\nConnection timed out during banner exchange",
        ] {
            assert!(!refusal_of(message, &guest), "{message:?}");
            assert!(
                !refusal_of(&format!("{via}{message}"), &guest),
                "{message:?}"
            );
        }
    }

    #[test]
    fn a_refusal_names_the_host_as_ssh_writes_it() {
        for (address, printed) in [
            ("LOCALHOST", "localhost"),
            ("Guest.Example", "guest.example"),
            ("fd00::1", "fd00::1"),
            ("FD00::1", "fd00::1"),
        ] {
            let message = format!("ops@{printed}: Permission denied (publickey).");
            assert!(refusal_of(&message, &login(address, "ops")), "{address}");
        }
        // Only the host: a login keeps its case.
        let message = "Ops@guest: Permission denied (publickey).";
        assert!(refusal_of(message, &login("guest", "Ops")));
        assert!(!refusal_of(message, &login("guest", "ops")));
    }

    #[test]
    fn var_names_that_pin_the_connection_are_refused() {
        assert!(check_var_name("ai_server_tools").is_ok());
        for bad in [
            "",
            "1x",
            "a-b",
            "ansible_ssh_common_args",
            "ANSIBLE_host",
            "octopushy",
            "octopushy_x",
        ] {
            assert!(check_var_name(bad).is_err(), "{bad}");
        }
    }

    #[test]
    fn known_hosts_pins_each_key_and_keeps_no_comment() {
        let key = "ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIL4yMxTo2ZC6CYBxvdtnHHtR5HBTjQeL+w9jI3rj+PyY root@g";
        let (lines, prints) = known_hosts(&host(key, 22)).unwrap();
        assert_eq!(
            lines,
            "10.0.0.5 ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIL4yMxTo2ZC6CYBxvdtnHHtR5HBTjQeL+w9jI3rj+PyY\n"
        );
        assert_eq!(prints.len(), 1);
        assert!(prints[0].starts_with("ssh-ed25519 SHA256:"));
        let (lines, _) = known_hosts(&host(key, 2222)).unwrap();
        assert!(lines.starts_with("[10.0.0.5]:2222 ssh-ed25519 "));
    }

    #[test]
    fn a_host_key_that_is_not_one_is_refused() {
        for bad in ["", "   \n", "ssh-ed25519", "nonsense AAAA", "ssh-rsa !!!"] {
            assert!(known_hosts(&host(bad, 22)).is_err(), "{bad:?}");
        }
    }

    const ED: &str = "SHA256:Zm9vYmFyYmF6cXV4cXV1eGNvcmdlZ3JhdWx0Z2FycA0";
    const RSA: &str = "SHA256:cnNhcnNhcnNhcnNhcnNhcnNhcnNhcnNhcnNhcnNhcnM";

    #[test]
    fn fingerprint_pins_are_types_and_sha256_fingerprints() {
        let pinned = Host {
            host_key_fingerprints: true,
            ..host(&format!("ed25519 {ED}\nrsa {RSA}\n"), 22)
        };
        let pins = pinned_fingerprints(&pinned).unwrap();
        assert_eq!(
            pins,
            vec![
                ("ed25519".to_owned(), ED.to_owned()),
                ("rsa".to_owned(), RSA.to_owned())
            ]
        );
        for bad in [
            "".to_owned(),
            format!("dsa {ED}"),
            format!("ed25519 {ED} extra"),
            "ed25519 SHA256:short".to_owned(),
            format!("ed25519 {ED}'"),
            "ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIL4yMxTo2ZC6CYBxvdtnHHtR5HBTjQeL+w9jI3rj+PyY"
                .to_owned(),
        ] {
            let host = Host {
                host_key_fingerprints: true,
                ..host(&bad, 22)
            };
            assert!(pinned_fingerprints(&host).is_err(), "{bad:?}");
        }
    }

    /// Runs the command as ssh would, through /bin/sh, with ssh's tokens as arguments.
    fn vouches(script: &str, args: &[&str]) -> String {
        let dir = tempfile::tempdir().unwrap();
        let path = dir.path().join("known-hosts-command.sh");
        fs::write(&path, script).unwrap();
        let out = std::process::Command::new("/bin/sh")
            .arg(&path)
            .args(args)
            .output()
            .unwrap();
        assert!(
            out.status.success(),
            "the command must not end the connection itself"
        );
        String::from_utf8(out.stdout).unwrap()
    }

    #[test]
    fn the_known_hosts_command_vouches_only_for_a_pinned_key() {
        let script =
            known_hosts_command(&[("ed25519".into(), ED.into()), ("rsa".into(), RSA.into())]);
        let key = "AAAAC3NzaC1lZDI1NTE5AAAAIL4yMxTo2ZC6CYBxvdtnHHtR5HBTjQeL+w9jI3rj+PyY";
        assert_eq!(
            vouches(&script, &["HOSTNAME", "10.0.0.5", ED, "ssh-ed25519", key]),
            format!("10.0.0.5 ssh-ed25519 {key}\n")
        );
        assert_eq!(
            vouches(
                &script,
                &["HOSTNAME", "[10.0.0.5]:2222", RSA, "ssh-rsa", "AAAA"]
            ),
            "[10.0.0.5]:2222 ssh-rsa AAAA\n"
        );
        let other = "SHA256:b3RoZXJvdGhlcm90aGVyb3RoZXJvdGhlcm90aGVyb3Q";
        for args in [
            vec!["HOSTNAME", "10.0.0.5", other, "ssh-ed25519", key],
            vec!["ORDER", "10.0.0.5"],
            vec!["HOSTNAME", "10.0.0.5", ED],
            vec!["OTHER", "10.0.0.5", ED, "ssh-ed25519", key],
            vec!["HOSTNAME", "10.0.0.5", "*", "ssh-ed25519", key],
        ] {
            assert_eq!(vouches(&script, &args), "", "{args:?}");
        }
    }

    #[test]
    fn a_relative_path_stays_inside() {
        assert_eq!(relative("./a/b", "x").unwrap(), PathBuf::from("a/b"));
        assert!(relative("../a", "x").is_err());
        assert!(relative("/a", "x").is_err());
    }

    #[test]
    fn only_a_failure_before_connecting_counts_as_never_connected() {
        let recap = |reached: bool, message: &str| Recap {
            reached,
            unreachable: Some(message.into()),
            ..Recap::default()
        };
        assert!(never_connected(&recap(
            false,
            "Host key verification failed."
        )));
        assert!(never_connected(&recap(false, "ssh: Connection refused")));
        assert!(!never_connected(&recap(true, "Connection refused")));
        assert!(!never_connected(&recap(false, "Shared connection closed")));
    }
}
