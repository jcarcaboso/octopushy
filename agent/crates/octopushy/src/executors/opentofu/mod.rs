//! The `opentofu` executor: `preview` (init + plan) and `apply` (of the saved
//! plan from the preview).
//!
//! Workspace layout of an attempt:
//!
//! ```text
//! <work>/<attempt>/
//!   attempt.json   marker (see attempts::workspace)
//!   repo/          the extracted repository snapshot; `root` is inside it
//!   state/         the built-in state, which the controller keeps
//!   out/plan.bin   the saved plan
//!   home/          HOME for OpenTofu; home/.ssh/known_hosts holds the input's `knownHosts`
//!   tmp/           TMPDIR (local runtime only; containers get a tmpfs)
//!   plugin-cache/  the provider cache, when each attempt has a user of its own: a cache shared
//!                  with other attempts would let one plant a provider another runs
//! ```
//!
//! A built template's image, when the input names `templateImage`, is fetched into
//! `<root>/.octopushy-images/<fileName>` before OpenTofu runs: inside the root, so every tool
//! runtime sees it, and at a path relative to the root, as a saved plan names it.

pub mod archive;
pub mod plan;
pub mod state;

use std::collections::BTreeMap;
use std::fs;
use std::path::{Component, Path, PathBuf};
use std::time::Duration;
use tokio::time::Instant;

use octopushy_proto::v1::Artifact;
use serde::Deserialize;
use serde_json::{Map, Value, json};

use super::{ExecContext, ExecResult, ExecutorConfig, Failure, SAFE_TRIES, Success};
use crate::images::{self, ImageDigest};
use crate::tls::sha256_hex;
use crate::tools::{Invocation, Tool, ToolOutput};

pub const PLAN_ARTIFACT: &str = "plan.tar.gz";
const MAX_PLAN_BYTES: usize = 32 * 1024 * 1024;
const LOCK_FILE: &str = ".terraform.lock.hcl";
const TFVARS_FILE: &str = "octopushy.auto.tfvars.json";
const BACKEND_FILE: &str = "octopushy_backend.tf.json";
/// The folder in the root a built template's image is fetched into.
pub const IMAGES_DIR: &str = ".octopushy-images";

/// A built template's image (`templateImage`).
#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
struct TemplateImage {
    sha256: String,
    file_name: String,
    size: u64,
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
pub(crate) struct Input {
    #[serde(default)]
    stack: String,
    root: String,
    #[serde(default)]
    image: String,
    #[serde(default)]
    variables: Map<String, Value>,
    #[serde(default)]
    env: BTreeMap<String, String>,
    #[serde(default)]
    secret_env: BTreeMap<String, String>,
    #[serde(default)]
    destroy: bool,
    #[serde(default)]
    timeout_minutes: Option<u64>,
    /// The SSH host keys a provider may connect to, as `known_hosts` lines. The Proxmox provider
    /// reads `$HOME/.ssh/known_hosts` and refuses a host whose key differs, but adds any host it
    /// has not seen, so without this its first connection would trust whoever answered.
    #[serde(default)]
    known_hosts: Option<String>,
    #[serde(default)]
    template_image: Option<TemplateImage>,
}

/// Checks `knownHosts` before it is written: printable lines, nothing that could end a line early.
fn check_known_hosts(text: &str) -> Result<(), Failure> {
    const LIMIT: usize = 16 * 1024;
    let lines: Vec<&str> = text.lines().filter(|l| !l.trim().is_empty()).collect();
    if text.len() > LIMIT
        || lines.is_empty()
        || text.chars().any(|c| c.is_control() && c != '\n')
        || lines.iter().any(|l| l.split_whitespace().count() < 3)
    {
        return Err(Failure::msg(
            "knownHosts must be known_hosts lines: a host pattern, a key type and a key",
        ));
    }
    Ok(())
}

pub async fn run(cfg: &ExecutorConfig, ctx: &ExecContext) -> ExecResult {
    match ctx.action.as_str() {
        "preview" => preview(cfg, ctx).await,
        "apply" => apply(cfg, ctx).await,
        other => Err(Failure::msg(format!(
            "unknown opentofu action {other:?}; expected preview or apply"
        ))),
    }
}

/// What the input's `env` and `secretEnv` may set: what the controller sends, the Proxmox
/// provider's settings and OpenTofu variables. Workflows declare no environment of their own.
pub const ENV_PREFIXES: &[&str] = &["PROXMOX_VE_", "TF_VAR_"];

/// Allows only names starting with one of [`ENV_PREFIXES`], so nothing the agent sets
/// itself, or that changes how OpenTofu or the container runtime behaves, can be set.
fn check_env_name(name: &str) -> Result<(), Failure> {
    let valid = !name.is_empty()
        && !name.starts_with(|c: char| c.is_ascii_digit())
        && name.bytes().all(|b| b.is_ascii_alphanumeric() || b == b'_');
    if !valid {
        return Err(Failure::msg(format!(
            "{name:?} is not a valid environment variable name"
        )));
    }
    if !ENV_PREFIXES
        .iter()
        .any(|p| name.len() > p.len() && name.starts_with(p))
    {
        return Err(Failure::msg(format!(
            "the environment variable {name} is not allowed: OpenTofu's environment takes only \
             names starting with {}",
            ENV_PREFIXES.join(" or ")
        )));
    }
    Ok(())
}

/// A relative directory inside the repository, without `..`.
pub(crate) fn check_root(root: &str) -> Result<PathBuf, Failure> {
    let path = Path::new(root);
    let mut out = PathBuf::new();
    for c in path.components() {
        match c {
            Component::Normal(p) => out.push(p),
            Component::CurDir => {}
            _ => {
                return Err(Failure::msg(format!(
                    "root {root:?} must be a relative path inside the repository"
                )));
            }
        }
    }
    Ok(out)
}

/// Removes whatever is at `path` (a file or symlink from the repository) and
/// writes a fresh file, so the write never follows a symlink.
pub(crate) fn write_fresh(path: &Path, contents: &[u8]) -> Result<(), Failure> {
    if let Ok(meta) = fs::symlink_metadata(path) {
        if meta.is_dir() {
            return Err(Failure::msg(format!("{} is a directory", path.display())));
        }
        fs::remove_file(path)
            .map_err(|e| Failure::msg(format!("removing {}: {e}", path.display())))?;
    }
    fs::write(path, contents).map_err(|e| Failure::msg(format!("writing {}: {e}", path.display())))
}

struct Prepared {
    input: Input,
    root: PathBuf,
    out_dir: PathBuf,
    plugin_cache: PathBuf,
    settings: Vec<(String, String)>,
    env: Vec<(String, String)>,
    deadline: Instant,
}

/// Validates the input, extracts the repository, and writes the generated files.
fn prepare(cfg: &ExecutorConfig, ctx: &ExecContext) -> Result<Prepared, Failure> {
    let input: Input = ctx.input()?;
    let minutes = input.timeout_minutes.unwrap_or(60);
    if !(1..=1440).contains(&minutes) {
        return Err(Failure::msg("timeoutMinutes must be between 1 and 1440"));
    }
    let deadline = Instant::now() + Duration::from_secs(minutes * 60);
    let rt = &cfg.runtime;
    let ws = &ctx.workspace;

    if rt.is_container() && input.image.trim().is_empty() {
        return Err(Failure::msg("the input names no OpenTofu image"));
    }
    if let Some(image) = &input.template_image {
        images::check_file_name(&image.file_name)
            .map_err(|e| Failure::msg(format!("templateImage.fileName: {e:#}")))?;
        images::check_sha256(&image.sha256)
            .map_err(|e| Failure::msg(format!("templateImage.sha256: {e:#}")))?;
    }

    // Environment: ours, then `env`, then `secretEnv`.
    let mk = |p: &Path| {
        fs::create_dir_all(p).map_err(|e| Failure::msg(format!("creating {}: {e}", p.display())))
    };
    let home = ws.join("home");
    let out_dir = ws.join("out");
    mk(&home)?;
    mk(&out_dir)?;
    if let Some(known) = &input.known_hosts {
        check_known_hosts(known)?;
        let ssh = home.join(".ssh");
        crate::fsutil::ensure_private_dir(&ssh)
            .map_err(|e| Failure::msg(format!("creating {}: {e}", ssh.display())))?;
        crate::fsutil::write_atomic(&ssh.join("known_hosts"), known.as_bytes(), 0o600)
            .map_err(|e| Failure::msg(format!("writing known_hosts: {e}")))?;
    }
    let plugin_cache = if ctx.tool_user.is_some() {
        ws.join("plugin-cache")
    } else {
        cfg.provider_cache.clone()
    };
    let mut settings: Vec<(String, String)> = vec![
        ("TF_IN_AUTOMATION".into(), "1".into()),
        ("TF_INPUT".into(), "0".into()),
        (
            "TF_PLUGIN_CACHE_DIR".into(),
            rt.plugin_cache_path(&plugin_cache),
        ),
        ("HOME".into(), rt.path(ws, &home)?),
    ];
    if !rt.is_container() {
        let tmp = ws.join("tmp");
        mk(&tmp)?;
        for name in ["TMPDIR", "TMP", "TEMP"] {
            settings.push((name.into(), tmp.to_string_lossy().into_owned()));
        }
    }
    let mut env: Vec<(String, String)> = Vec::new();
    let mut names: Vec<String> = Vec::new();
    for (k, v) in &input.env {
        check_env_name(k)?;
        env.push((k.clone(), v.clone()));
        names.push(k.clone());
    }
    for (k, secret) in &input.secret_env {
        check_env_name(k)?;
        if input.env.contains_key(k) {
            return Err(Failure::msg(format!(
                "{k} is set in both env and secretEnv"
            )));
        }
        env.push((k.clone(), ctx.secrets.get(secret)?.to_owned()));
        names.push(format!("{k} (secret)"));
    }
    fs::create_dir_all(&plugin_cache)
        .map_err(|e| Failure::msg(format!("creating the provider cache: {e}")))?;

    // Repository.
    let repo = ws.join("repo");
    let stats = archive::extract_tar_gz(&ctx.files_tar_gz, &repo, archive::Limits::default())
        .map_err(|e| Failure::msg(format!("cannot extract the repository files: {e:#}")))?;
    ctx.log.agent(format!(
        "extracted {} files ({} KiB) for stack {}",
        stats.files,
        stats.bytes / 1024,
        if input.stack.is_empty() {
            "?"
        } else {
            &input.stack
        }
    ));
    let root = repo.join(check_root(&input.root)?);
    let canonical = root.canonicalize().map_err(|_| {
        Failure::msg(format!(
            "root {:?} is not in the repository files",
            input.root
        ))
    })?;
    if !canonical.starts_with(repo.canonicalize().map_err(anyhow::Error::from)?)
        || !canonical.is_dir()
    {
        return Err(Failure::msg(format!(
            "root {:?} is not a directory in the repository",
            input.root
        )));
    }
    let root = canonical;

    // Generated files.
    let tfvars = serde_json::to_vec_pretty(&Value::Object(input.variables.clone()))
        .map_err(anyhow::Error::from)?;
    write_fresh(&root.join(TFVARS_FILE), &tfvars)?;
    state::restore(ctx)?;
    state::record_root(ws, &root)?;
    let backend = json!({ "terraform": { "backend": {
        "local": { "path": state::backend_path(ws, &root)? }
    } } });
    write_fresh(&root.join(BACKEND_FILE), backend.to_string().as_bytes())?;

    ctx.log.agent(format!(
        "tool runtime {}; environment: TF_IN_AUTOMATION, TF_INPUT, TF_PLUGIN_CACHE_DIR, HOME{}{}",
        rt.kind.as_str(),
        if names.is_empty() { "" } else { ", " },
        names.join(", ")
    ));
    Ok(Prepared {
        input,
        root,
        out_dir,
        plugin_cache,
        settings,
        env,
        deadline,
    })
}

/// Removes whatever `path` is (a folder, file or link from the repository) without following it.
fn remove_any(path: &Path) -> Result<(), Failure> {
    let removed = match fs::symlink_metadata(path) {
        Ok(meta) if meta.is_dir() => fs::remove_dir_all(path),
        Ok(_) => fs::remove_file(path),
        Err(e) if e.kind() == std::io::ErrorKind::NotFound => Ok(()),
        Err(e) => Err(e),
    };
    removed.map_err(|e| Failure::msg(format!("removing {}: {e}", path.display())))
}

/// Fetches the input's `templateImage` into the root, replacing whatever the repository has
/// there, and checks its size and SHA-256.
async fn fetch_template_image(
    cfg: &ExecutorConfig,
    ctx: &ExecContext,
    prep: &Prepared,
) -> Result<(), Failure> {
    let Some(image) = &prep.input.template_image else {
        return Ok(());
    };
    let dir = prep.root.join(IMAGES_DIR);
    remove_any(&dir)?;
    fs::create_dir(&dir).map_err(|e| Failure::msg(format!("creating {}: {e}", dir.display())))?;
    let expected = ImageDigest {
        sha256: image.sha256.to_ascii_lowercase(),
        size: image.size,
    };
    ctx.log.agent(format!(
        "fetching the template image {IMAGES_DIR}/{} ({:.1} MiB, sha256 {})",
        image.file_name,
        image.size as f64 / (1024.0 * 1024.0),
        expected.sha256
    ));
    let started = std::time::Instant::now();
    let download = images::download(
        &cfg.server,
        &ctx.attempt_id,
        &expected,
        &image.file_name,
        &dir,
    );
    tokio::select! {
        r = download => r.map_err(|e| Failure::msg(format!("fetching the template image: {e:#}")))?,
        _ = ctx.cancel.cancelled() => return Err(Failure::Cancelled),
    };
    ctx.log.agent(format!(
        "fetched the template image in {:.1}s",
        started.elapsed().as_secs_f64()
    ));
    Ok(())
}

/// Runs OpenTofu steps for one attempt.
struct Tofu<'a> {
    cfg: &'a ExecutorConfig,
    ctx: &'a ExecContext,
    prep: &'a Prepared,
}

impl Tofu<'_> {
    async fn run(
        &self,
        step: &str,
        args: Vec<String>,
        capture: bool,
    ) -> Result<ToolOutput, Failure> {
        let mut name = format!("octopushy-{}-{step}", self.ctx.attempt_id);
        name.truncate(200);
        let timeout = || {
            let message = format!(
                "OpenTofu exceeded timeoutMinutes={} while running {step}",
                self.prep.input.timeout_minutes.unwrap_or(60)
            );
            if self.ctx.action == "apply" {
                Failure::Uncertain(message)
            } else {
                Failure::msg(message)
            }
        };
        if Instant::now() >= self.prep.deadline {
            return Err(timeout());
        }
        let stop = self.ctx.cancel.child_token();
        let run = self.cfg.runtime.run(
            Invocation {
                tool: Tool::OpenTofu,
                name,
                image: &self.prep.input.image,
                workspace: &self.ctx.workspace,
                workdir: &self.prep.root,
                plugin_cache: Some(&self.prep.plugin_cache),
                args,
                settings: self.prep.settings.clone(),
                env: self.prep.env.clone(),
                stdin: None,
                capture_stdout: capture,
                stderr_mark: (step == "apply").then_some(state::NOT_SAVED),
                control_prefix: None,
                user: self.ctx.tool_user,
            },
            &self.ctx.log,
            &stop,
        );
        tokio::pin!(run);
        let mut timed_out = false;
        let out = tokio::select! {
            out = &mut run => out,
            _ = tokio::time::sleep_until(self.prep.deadline) => {
                timed_out = true;
                self.ctx.log.agent("OpenTofu timeout reached; stopping gracefully to save state");
                stop.cancel();
                run.await
            }
        };
        if timed_out {
            return Err(timeout());
        }
        let out = out?;
        if out.cancelled {
            return Err(if self.ctx.action == "apply" {
                Failure::Uncertain(
                    "OpenTofu was stopped; inspect its saved state before planning again".into(),
                )
            } else {
                Failure::Cancelled
            });
        }
        Ok(out)
    }

    /// Runs a step that must exit 0.
    async fn run_ok(
        &self,
        step: &str,
        args: Vec<String>,
        capture: bool,
    ) -> Result<ToolOutput, Failure> {
        let out = self.run(step, args, capture).await?;
        if out.code == Some(0) {
            Ok(out)
        } else {
            Err(step_failed(step, &out))
        }
    }

    fn path(&self, host: &Path) -> Result<String, Failure> {
        Ok(self.cfg.runtime.path(&self.ctx.workspace, host)?)
    }

    async fn version(&self) -> Result<Option<String>, Failure> {
        let out = self
            .run_ok("version", strings(&["version", "-json"]), true)
            .await?;
        let version = plan::tool_version(&out.stdout);
        self.ctx.log.agent(format!(
            "OpenTofu {}",
            version.as_deref().unwrap_or("(unknown version)")
        ));
        Ok(version)
    }

    async fn init(&self, readonly_lock: bool) -> Result<(), Failure> {
        let mut args = strings(&["init", "-input=false", "-no-color"]);
        if readonly_lock {
            args.push("-lockfile=readonly".into());
        }
        // Init only fetches providers and modules into the root and readies the local backend,
        // so a registry or network failure is tried again. A stop or a timeout is not.
        let mut tried = 1;
        loop {
            let out = self.run("init", args.clone(), false).await?;
            if out.code == Some(0) {
                return Ok(());
            }
            let failure = step_failed("init", &out);
            if tried >= SAFE_TRIES {
                return Err(failure);
            }
            tried += 1;
            self.ctx
                .before_retry(self.cfg, "tofu init", failure.message(), tried)
                .await?;
        }
    }
}

fn strings(args: &[&str]) -> Vec<String> {
    args.iter().map(|s| (*s).to_owned()).collect()
}

fn step_failed(step: &str, out: &ToolOutput) -> Failure {
    let code = out
        .code
        .map(|c| c.to_string())
        .unwrap_or_else(|| "a signal".into());
    let detail = out.stderr_summary(5);
    Failure::msg(if detail.is_empty() {
        format!("tofu {step} failed (exit status {code})")
    } else {
        format!("tofu {step} failed (exit status {code}): {detail}")
    })
}

async fn preview(cfg: &ExecutorConfig, ctx: &ExecContext) -> ExecResult {
    let prep = prepare(cfg, ctx)?;
    fetch_template_image(cfg, ctx, &prep).await?;
    let tofu = Tofu {
        cfg,
        ctx,
        prep: &prep,
    };
    super::pull_image(cfg, ctx, &prep.input.image, prep.deadline).await?;
    let tool_version = tofu.version().await?;
    tofu.init(false).await?;

    let plan_path = prep.out_dir.join("plan.bin");
    let plan_arg = tofu.path(&plan_path)?;
    let mut args = strings(&["plan", "-input=false", "-no-color", "-detailed-exitcode"]);
    args.push(format!("-out={plan_arg}"));
    if prep.input.destroy {
        args.push("-destroy".into());
    }
    let out = tofu.run("plan", args, false).await?;
    let has_changes = match out.code {
        Some(0) => false,
        Some(2) => true,
        _ => return Err(step_failed("plan", &out)),
    };

    let mut summary = plan::PlanSummary::default();
    let mut artifacts = Vec::new();
    if has_changes {
        let show = tofu
            .run_ok(
                "show",
                vec!["show".into(), "-json".into(), "-no-color".into(), plan_arg],
                true,
            )
            .await?;
        summary = plan::summarize(&show.stdout)?;
        drop(show);
    } else {
        ctx.log.agent("no changes");
    }

    if has_changes {
        let plan_bin = read_plan(&ctx.workspace, &plan_path)?;
        let lock_path = prep.root.join(LOCK_FILE);
        let lock = read_lock(&ctx.workspace, &lock_path)?;
        let mut files: Vec<(&str, &[u8])> = vec![("plan.bin", &plan_bin)];
        match &lock {
            Some(l) => files.push((LOCK_FILE, l)),
            None => ctx.log.agent("no .terraform.lock.hcl after init (no providers); the artifact holds only plan.bin"),
        }
        let content = archive::pack(&files)?;
        if content.len() > MAX_PLAN_BYTES {
            return Err(Failure::msg(format!(
                "saved plan artifact exceeds the {MAX_PLAN_BYTES} byte cap"
            )));
        }
        let sha256 = sha256_hex(&content);
        ctx.log.agent(format!(
            "saved plan: {} to create, {} to update, {} to delete, {} to replace (artifact sha256 {sha256})",
            summary.counts.create, summary.counts.update, summary.counts.delete, summary.counts.replace
        ));
        artifacts.push(Artifact {
            name: PLAN_ARTIFACT.into(),
            content,
            sha256,
        });
    }

    Ok(Success {
        outputs: json!({
            "hasChanges": has_changes,
            "changes": summary.changes,
            "counts": summary.counts,
            "toolVersion": tool_version,
        }),
        artifacts,
    })
}

async fn apply(cfg: &ExecutorConfig, ctx: &ExecContext) -> ExecResult {
    let artifact = ctx
        .artifacts
        .iter()
        .find(|a| a.name == PLAN_ARTIFACT)
        .ok_or_else(|| {
            Failure::msg(format!(
                "the assignment carries no {PLAN_ARTIFACT} artifact"
            ))
        })?;
    let actual = sha256_hex(&artifact.content);
    // The artifact's sha256 is the approved plan's hash, and the server always names
    // it (the proto documents it, and the controller refuses to build an assignment
    // without one). An artifact that carries none is not the approved plan.
    if artifact.sha256.is_empty() {
        return Err(Failure::msg(format!(
            "{PLAN_ARTIFACT} carries no sha256, so it is not the approved plan \
             (its content hashes to {actual})"
        )));
    }
    if !artifact.sha256.eq_ignore_ascii_case(&actual) {
        return Err(Failure::msg(format!(
            "{PLAN_ARTIFACT} is corrupt: sha256 {actual}, expected {}",
            artifact.sha256
        )));
    }

    let prep = prepare(cfg, ctx)?;
    fetch_template_image(cfg, ctx, &prep).await?;
    let tofu = Tofu {
        cfg,
        ctx,
        prep: &prep,
    };

    // Restore plan.bin and the lock file from the preview.
    let limits = archive::Limits {
        max_bytes: 256 * 1024 * 1024,
        max_entries: 8,
    };
    archive::extract_tar_gz(&artifact.content, &prep.out_dir, limits)
        .map_err(|e| Failure::msg(format!("cannot extract {PLAN_ARTIFACT}: {e:#}")))?;
    for entry in fs::read_dir(&prep.out_dir).map_err(anyhow::Error::from)? {
        let entry = entry.map_err(anyhow::Error::from)?;
        let name = entry.file_name().to_string_lossy().into_owned();
        let is_file = entry.file_type().map(|t| t.is_file()).unwrap_or(false);
        if !(name == "plan.bin" || name == LOCK_FILE) || !is_file {
            return Err(Failure::msg(format!(
                "{PLAN_ARTIFACT} holds unexpected entry {name:?}"
            )));
        }
    }
    let plan_path = prep.out_dir.join("plan.bin");
    if !plan_path.is_file() {
        return Err(Failure::msg(format!("{PLAN_ARTIFACT} holds no plan.bin")));
    }
    let saved_lock = prep.out_dir.join(LOCK_FILE);
    let lock = read_lock(&ctx.workspace, &saved_lock)?;
    if let Some(lock) = &lock {
        write_fresh(&prep.root.join(LOCK_FILE), lock)?;
        ctx.log
            .agent("restored .terraform.lock.hcl from the preview");
    }

    super::pull_image(cfg, ctx, &prep.input.image, prep.deadline).await?;
    tofu.version().await?;
    tofu.init(lock.is_some()).await?;
    let plan_arg = tofu.path(&plan_path)?;
    // Everything above changes nothing outside the workspace; from here on a restart is uncertain.
    ctx.begin_change()?;
    let watcher = tokio::spawn(state::watch(
        ctx.workspace.clone(),
        cfg.poll_interval,
        ctx.snapshots.clone(),
    ));
    let applied = tofu
        .run(
            "apply",
            vec![
                "apply".into(),
                "-input=false".into(),
                "-no-color".into(),
                plan_arg,
            ],
            false,
        )
        .await;
    watcher.abort();
    let applied = applied?;
    state::read_checked(&ctx.workspace)
        .map_err(|e| Failure::Uncertain(format!("cannot read the saved OpenTofu state: {e:#}")))?;
    if applied.code != Some(0) {
        // OpenTofu exits 1 after saving what it did. Anything else, such as being
        // killed, leaves work done since its last write out of the state.
        if applied.code != Some(1) {
            return Err(Failure::Uncertain(format!(
                "{}; the state may not include its last changes, so inspect the guest before planning again",
                step_failed("apply", &applied).message()
            )));
        }
        if applied.stderr_marked && !state::has_emergency(&ctx.workspace) {
            return Err(Failure::Uncertain(
                "OpenTofu could not save its state anywhere; inspect the guest before planning again"
                    .into(),
            ));
        }
        return Err(step_failed("apply", &applied));
    }

    // The change is made; failing to read outputs does not undo it.
    let (outputs, sensitive) = match tofu
        .run_ok("output", strings(&["output", "-json", "-no-color"]), true)
        .await
    {
        Ok(out) => match plan::split_outputs(&out.stdout) {
            Ok(split) => split,
            Err(e) => {
                ctx.log
                    .agent(format!("warning: could not read the outputs: {e:#}"));
                (Map::new(), Vec::new())
            }
        },
        Err(Failure::Failed { message, .. }) => {
            ctx.log
                .agent(format!("warning: could not read the outputs: {message}"));
            (Map::new(), Vec::new())
        }
        Err(Failure::Cancelled) => (Map::new(), Vec::new()),
        Err(error @ Failure::Uncertain(_)) => return Err(error),
    };
    if !sensitive.is_empty() {
        ctx.log.agent(format!(
            "withheld sensitive outputs: {}",
            sensitive.join(", ")
        ));
    }
    Ok(Success::outputs(json!({
        "outputs": outputs,
        "sensitiveOutputs": sensitive,
    })))
}

fn read_plan(workspace: &Path, path: &Path) -> Result<Vec<u8>, Failure> {
    crate::fsutil::read_tool_file(workspace, path, MAX_PLAN_BYTES).map_err(Failure::from)
}

fn read_lock(workspace: &Path, path: &Path) -> Result<Option<Vec<u8>>, Failure> {
    match crate::fsutil::read_tool_file(workspace, path, 1024 * 1024) {
        Ok(bytes) => Ok(Some(bytes)),
        Err(e)
            if e.downcast_ref::<std::io::Error>()
                .is_some_and(|e| e.kind() == std::io::ErrorKind::NotFound) =>
        {
            Ok(None)
        }
        Err(e) => Err(e.into()),
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn both_saved_and_tool_written_lock_links_are_rejected() {
        use std::os::unix::fs::symlink;
        let dir = tempfile::tempdir().unwrap();
        for file in [".terraform.lock.hcl", "saved.lock"] {
            let path = dir.path().join(file);
            symlink("/etc/passwd", &path).unwrap();
            let error = read_lock(dir.path(), &path).unwrap_err();
            assert!(error.message().contains("without following links"));
        }
    }

    #[test]
    fn saved_plan_is_bounded() {
        let dir = tempfile::tempdir().unwrap();
        let path = dir.path().join("plan.bin");
        let file = fs::File::create(&path).unwrap();
        file.set_len(MAX_PLAN_BYTES as u64 + 1).unwrap();
        assert!(
            read_plan(dir.path(), &path)
                .unwrap_err()
                .message()
                .contains("byte cap")
        );
        fs::write(&path, b"plan").unwrap();
        assert_eq!(read_plan(dir.path(), &path).unwrap(), b"plan");
    }

    #[test]
    fn known_hosts_lines() {
        assert!(check_known_hosts("* ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIKii\n").is_ok());
        assert!(check_known_hosts("* ssh-ed25519 AAAA\n* ecdsa-sha2-nistp256 AAAA\n").is_ok());
        for bad in [
            "",
            "\n",
            "ssh-ed25519 AAAA",
            "* ssh-ed25519 AAAA\r\n",
            "* ssh-ed25519 AAAA\u{0}",
        ] {
            assert!(check_known_hosts(bad).is_err(), "{bad:?}");
        }
        assert!(check_known_hosts(&"* ssh-ed25519 AAAA\n".repeat(2000)).is_err());
    }

    #[test]
    fn env_names() {
        // Every name the controller sends (RunFactory, SecretNames).
        for good in [
            "PROXMOX_VE_ENDPOINT",
            "PROXMOX_VE_INSECURE",
            "PROXMOX_VE_API_TOKEN",
            "PROXMOX_VE_SSH_USERNAME",
            "PROXMOX_VE_SSH_PRIVATE_KEY",
            "TF_VAR_octopushy_host_key",
        ] {
            assert!(check_env_name(good).is_ok(), "{good}");
        }
        for bad in [
            "",
            "1X",
            "A-B",
            "HOME",
            "PATH",
            "PG_CONN_STR",
            "LD_PRELOAD",
            "HTTPS_PROXY",
            "TF_VAR_",
            "PROXMOX_VE_",
            "TF_PLUGIN_CACHE_DIR",
            "TF_CLI_ARGS_plan",
            "TF_CLI_CONFIG_FILE",
            "CONTAINERS_CONF",
            "DOCKER_HOST",
        ] {
            assert!(check_env_name(bad).is_err(), "{bad}");
        }
    }

    #[test]
    fn the_images_folder_is_removed_without_following_links() {
        let dir = tempfile::tempdir().unwrap();
        let outside = dir.path().join("outside");
        fs::create_dir(&outside).unwrap();
        fs::write(outside.join("keep"), b"x").unwrap();
        let root = dir.path().join("root");
        fs::create_dir(&root).unwrap();
        let images = root.join(IMAGES_DIR);
        std::os::unix::fs::symlink(&outside, &images).unwrap();
        remove_any(&images).unwrap();
        assert!(fs::symlink_metadata(&images).is_err());
        assert!(outside.join("keep").exists());
        fs::create_dir_all(images.join("sub")).unwrap();
        fs::write(images.join("sub/a.qcow2"), b"x").unwrap();
        remove_any(&images).unwrap();
        assert!(!images.exists());
        remove_any(&images).unwrap();
    }

    #[test]
    fn roots() {
        assert_eq!(
            check_root("modules/opentofu/x").unwrap(),
            PathBuf::from("modules/opentofu/x")
        );
        assert_eq!(check_root("./a").unwrap(), PathBuf::from("a"));
        assert!(check_root("../a").is_err());
        assert!(check_root("/a").is_err());
    }
}
