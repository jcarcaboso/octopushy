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
//!   out/recap.json      what `octopushy_recap` recorded
//!   out/prepared.json   what a prepare playbook wrote
//!   home/               HOME for Ansible
//! ```
//!
//! Secrets never enter the workspace. They reach `octopushy-ansible` on stdin, which keeps
//! them in memory; see `agent/ansible/octopushy-ansible`.

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
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
struct Host {
    name: String,
    address: String,
    #[serde(default = "default_port")]
    port: u16,
    user: String,
    /// The pinned public keys, as `type base64 [comment]`, one per line.
    host_key: String,
}

fn default_port() -> u16 {
    22
}

/// What `octopushy_recap` writes.
#[derive(Debug, Default, Deserialize)]
#[serde(rename_all = "camelCase", default)]
struct Recap {
    reached: bool,
    finished: bool,
    tasks: u64,
    stats: Option<Value>,
    failed_task: Option<String>,
    unreachable: Option<String>,
    unreachable_task: Option<String>,
    outcome: Option<String>,
    detail: Option<String>,
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
    let (known, fingerprints) = known_hosts(&input.host)?;

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
    let ssh_args = [
        "-F /dev/null".to_owned(),
        format!("-o UserKnownHostsFile={known_path}"),
        "-o GlobalKnownHostsFile=/dev/null".to_owned(),
        "-o StrictHostKeyChecking=yes".to_owned(),
        "-o HashKnownHosts=no".to_owned(),
        "-o BatchMode=yes".to_owned(),
        "-o PasswordAuthentication=no".to_owned(),
        "-o KbdInteractiveAuthentication=no".to_owned(),
        "-o PreferredAuthentications=publickey".to_owned(),
        "-o ControlMaster=no".to_owned(),
        "-o ControlPath=none".to_owned(),
        "-o ServerAliveInterval=15".to_owned(),
        "-o ServerAliveCountMax=8".to_owned(),
    ]
    .join(" ");
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
        "ansible_ssh_args": ssh_args,
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

    let settings = vec![
        ("HOME".to_owned(), tool_path(&home)?),
        (
            "OCTOPUSHY_RESULT_FILE".to_owned(),
            tool_path(&out_dir.join("recap.json"))?,
        ),
    ];
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
fn secrets_document(input: &Input, ctx: &ExecContext) -> Result<Vec<u8>, Failure> {
    let resolve = |names: &BTreeMap<String, String>| -> Result<Map<String, Value>, Failure> {
        names
            .iter()
            .map(|(name, secret)| Ok((name.clone(), json!(ctx.secrets.get(secret)?))))
            .collect()
    };
    let key = input
        .ssh_key
        .as_ref()
        .ok_or_else(|| Failure::msg("the input names no SSH key"))?;
    let document = json!({
        "sshKey": ctx.secrets.get(key)?,
        "secretVars": resolve(&input.secret_vars)?,
        "secretFiles": resolve(&input.secret_files)?,
    });
    Ok(serde_json::to_vec(&document).map_err(anyhow::Error::from)?)
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
    let stdin = secrets_document(&prep.input, ctx)?;

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
    let stop = ctx.cancel.child_token();
    let timer = {
        let stop = stop.clone();
        tokio::spawn(async move {
            tokio::time::sleep(Duration::from_secs(minutes.saturating_mul(60))).await;
            stop.cancel();
        })
    };
    let out = tool
        .run("playbook", args, settings, Some(stdin), false, &stop)
        .await;
    timer.abort();
    if !cfg.runtime.is_container() {
        remove_secret_dir(&ctx.attempt_id);
    }
    let out = out?;
    let recap = fs::read(prep.out_dir.join("recap.json"))
        .ok()
        .and_then(|text| serde_json::from_slice::<Recap>(&text).ok());
    settle(&prep, ctx, &out, recap, minutes)
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
fn settle(
    prep: &Prepared,
    ctx: &ExecContext,
    out: &ToolOutput,
    recap: Option<Recap>,
    minutes: u64,
) -> ExecResult {
    if out.cancelled {
        return Err(Failure::Uncertain(format!(
            "the playbook passed its limit of {minutes} minutes and was stopped; whether it finished what it was doing is unknown. Inspect the guest, and any lock it holds, before running it again"
        )));
    }
    let code = exit_text(out);
    let Some(recap) = recap else {
        return Err(Failure::msg(failed_before_tasks(out)));
    };
    let outputs = |recap: &Recap| {
        json!({
            "exitCode": out.code,
            "tasks": recap.tasks,
            "recap": recap.stats,
            "failedTask": recap.failed_task.as_deref().map(|t| ctx.log.mask(t)),
            "unreachableTask": recap.unreachable_task,
        })
    };
    if !recap.finished {
        if recap.tasks == 0 {
            return Err(Failure::msg(failed_before_tasks(out)));
        }
        if never_connected(&recap) {
            return Err(unreachable_failure(prep, &recap, &outputs(&recap)));
        }
        return Err(Failure::Uncertain(format!(
            "ansible-playbook ended (exit status {code}) without finishing; whether a change was made is unknown. Inspect the guest, and any lock it holds, before running it again"
        )));
    }
    if let Some(outcome) = &recap.outcome {
        if !prep.input.outcomes.iter().any(|o| o == outcome) {
            return Err(Failure::with_outputs(
                format!(
                    "the playbook reported the outcome {outcome:?}, which the recipe does not declare"
                ),
                outputs(&recap),
            ));
        }
        let mut result = outputs(&recap);
        result["outcome"] = json!(outcome);
        result["detail"] = json!(recap.detail.as_deref().map(|d| ctx.log.mask(d)));
        return Ok(Success::outputs(result));
    }
    if out.code == Some(0) {
        return Ok(Success::outputs(outputs(&recap)));
    }
    if recap.unreachable.is_some() {
        if never_connected(&recap) {
            return Err(unreachable_failure(prep, &recap, &outputs(&recap)));
        }
        return Err(Failure::Uncertain(format!(
            "the guest stopped answering during {}; whether the task finished is unknown. Inspect the guest, and any lock it holds, before running it again",
            recap.unreachable_task.as_deref().unwrap_or("a task")
        )));
    }
    Err(Failure::with_outputs(
        match recap.failed_task.as_deref() {
            Some(task) => format!("the task {task:?} failed (exit status {code})"),
            None => format!("ansible-playbook failed (exit status {code})"),
        },
        outputs(&recap),
    ))
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
    Failure::with_outputs(
        format!("could not reach the guest over SSH, so nothing ran: {message}"),
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
        }
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
