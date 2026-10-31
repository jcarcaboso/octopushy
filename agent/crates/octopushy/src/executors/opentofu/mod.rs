//! The `opentofu` executor: `preview` (init + plan) and `apply` (of the saved
//! plan from the preview).
//!
//! Workspace layout of an attempt:
//!
//! ```text
//! <work>/<attempt>/
//!   attempt.json   marker (see attempts::workspace)
//!   repo/          the extracted repository snapshot; `root` is inside it
//!   state/         the built-in state, when there is no external backend
//!   out/plan.bin   the saved plan
//!   home/          HOME for OpenTofu
//!   tmp/           TMPDIR (local runtime only; containers get a tmpfs)
//! ```

pub mod archive;
pub mod plan;
pub mod state;

use std::collections::BTreeMap;
use std::fs;
use std::path::{Component, Path, PathBuf};

use octopushy_proto::v1::Artifact;
use serde::Deserialize;
use serde_json::{Map, Value, json};

use super::{ExecContext, ExecResult, ExecutorConfig, Failure, Success};
use crate::tls::sha256_hex;
use crate::tools::{Invocation, ToolOutput};

pub const PLAN_ARTIFACT: &str = "plan.tar.gz";
const LOCK_FILE: &str = ".terraform.lock.hcl";
const TFVARS_FILE: &str = "octopushy.auto.tfvars.json";
const BACKEND_FILE: &str = "octopushy_backend.tf.json";

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
struct Input {
    #[serde(default)]
    stack: String,
    root: String,
    #[serde(default)]
    image: String,
    backend: Backend,
    #[serde(default)]
    variables: Map<String, Value>,
    #[serde(default)]
    env: BTreeMap<String, String>,
    #[serde(default)]
    secret_env: BTreeMap<String, String>,
    #[serde(default)]
    destroy: bool,
    #[serde(default)]
    retain_no_changes_plan: bool,
}

#[derive(Debug, Deserialize)]
struct Backend {
    #[serde(rename = "type")]
    kind: String,
    #[serde(default)]
    config: BTreeMap<String, Value>,
    #[serde(default)]
    workspace: Option<String>,
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

/// Names the agent sets itself, or that would change how the container runtime
/// (which shares the tool's environment) behaves.
fn check_env_name(name: &str) -> Result<(), Failure> {
    let valid = !name.is_empty()
        && !name.starts_with(|c: char| c.is_ascii_digit())
        && name.bytes().all(|b| b.is_ascii_alphanumeric() || b == b'_');
    if !valid {
        return Err(Failure::msg(format!(
            "{name:?} is not a valid environment variable name"
        )));
    }
    const RESERVED: &[&str] = &[
        "TF_IN_AUTOMATION",
        "TF_INPUT",
        "TF_PLUGIN_CACHE_DIR",
        "TF_DATA_DIR",
        "HOME",
        "PATH",
        "TMPDIR",
        "USER",
        "XDG_RUNTIME_DIR",
    ];
    const RESERVED_PREFIXES: &[&str] = &[
        "TF_CLI_ARGS",
        "CONTAINERS_",
        "PODMAN_",
        "DOCKER_",
        "BUILDAH_",
    ];
    if RESERVED.contains(&name) || RESERVED_PREFIXES.iter().any(|p| name.starts_with(p)) {
        return Err(Failure::msg(format!(
            "the environment variable {name} is reserved by the agent"
        )));
    }
    Ok(())
}

/// A relative directory inside the repository, without `..`.
fn check_root(root: &str) -> Result<PathBuf, Failure> {
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

fn backend_value(key: &str, value: &Value) -> Result<Option<String>, Failure> {
    let valid_key = !key.is_empty() && key.bytes().all(|b| b.is_ascii_alphanumeric() || b == b'_');
    if !valid_key {
        return Err(Failure::msg(format!(
            "invalid backend setting name {key:?}"
        )));
    }
    match value {
        Value::Null => Ok(None),
        Value::String(s) => Ok(Some(s.clone())),
        Value::Bool(b) => Ok(Some(b.to_string())),
        Value::Number(n) => Ok(Some(n.to_string())),
        _ => Err(Failure::msg(format!(
            "backend setting {key} must be a string, number, or boolean"
        ))),
    }
}

/// Removes whatever is at `path` (a file or symlink from the repository) and
/// writes a fresh file, so the write never follows a symlink.
fn write_fresh(path: &Path, contents: &[u8]) -> Result<(), Failure> {
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
    built_in: bool,
    root: PathBuf,
    out_dir: PathBuf,
    settings: Vec<(String, String)>,
    env: Vec<(String, String)>,
    backend_args: Vec<String>,
}

/// Validates the input, extracts the repository, and writes the generated files.
fn prepare(cfg: &ExecutorConfig, ctx: &ExecContext) -> Result<Prepared, Failure> {
    let input: Input = ctx.input()?;
    let rt = &cfg.runtime;
    let ws = &ctx.workspace;

    // Backend.
    let kind = input.backend.kind.as_str();
    let built_in = kind == state::BACKEND_TYPE;
    if built_in && (!input.backend.config.is_empty() || input.backend.workspace.is_some()) {
        return Err(Failure::msg(
            "the built-in state takes no backend settings or workspace",
        ));
    }
    if kind.is_empty()
        || !kind
            .bytes()
            .all(|b| b.is_ascii_lowercase() || b.is_ascii_digit() || b == b'_')
    {
        return Err(Failure::msg(format!("invalid backend type {kind:?}")));
    }
    if kind == "local" {
        return Err(Failure::msg(
            "the local backend is not allowed: state would be lost with the attempt's workspace",
        ));
    }
    let mut backend_args = Vec::new();
    for (key, value) in &input.backend.config {
        if let Some(v) = backend_value(key, value)? {
            backend_args.push(format!("-backend-config={key}={v}"));
        }
    }
    if let Some(name) = &input.backend.workspace
        && (name.is_empty()
            || !name
                .bytes()
                .all(|b| b.is_ascii_alphanumeric() || b"._-".contains(&b)))
    {
        return Err(Failure::msg(format!("invalid backend workspace {name:?}")));
    }
    if rt.is_container() && input.image.trim().is_empty() {
        return Err(Failure::msg("the input names no OpenTofu image"));
    }

    // Environment: ours, then `env`, then `secretEnv`.
    let mk = |p: &Path| {
        fs::create_dir_all(p).map_err(|e| Failure::msg(format!("creating {}: {e}", p.display())))
    };
    let home = ws.join("home");
    let out_dir = ws.join("out");
    mk(&home)?;
    mk(&out_dir)?;
    let mut settings: Vec<(String, String)> = vec![
        ("TF_IN_AUTOMATION".into(), "1".into()),
        ("TF_INPUT".into(), "0".into()),
        (
            "TF_PLUGIN_CACHE_DIR".into(),
            rt.plugin_cache_path(&cfg.provider_cache),
        ),
        ("HOME".into(), rt.path(ws, &home)?),
    ];
    if !rt.is_container() {
        let tmp = ws.join("tmp");
        mk(&tmp)?;
        settings.push(("TMPDIR".into(), tmp.to_string_lossy().into_owned()));
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
    fs::create_dir_all(&cfg.provider_cache)
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
    let mut backends = Map::new();
    if built_in {
        state::restore(ctx)?;
        state::record_root(ws, &root)?;
        backends.insert(
            "local".to_owned(),
            json!({ "path": state::backend_path(ws, &root)? }),
        );
    } else {
        backends.insert(kind.to_owned(), json!({}));
    }
    let backend = json!({ "terraform": { "backend": backends } });
    write_fresh(&root.join(BACKEND_FILE), backend.to_string().as_bytes())?;

    ctx.log.agent(format!(
        "tool runtime {}; environment: TF_IN_AUTOMATION, TF_INPUT, TF_PLUGIN_CACHE_DIR, HOME{}{}",
        rt.kind.as_str(),
        if names.is_empty() { "" } else { ", " },
        names.join(", ")
    ));
    Ok(Prepared {
        input,
        built_in,
        root,
        out_dir,
        settings,
        env,
        backend_args,
    })
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
        let out = self
            .cfg
            .runtime
            .run(
                Invocation {
                    name,
                    image: &self.prep.input.image,
                    workspace: &self.ctx.workspace,
                    workdir: &self.prep.root,
                    plugin_cache: &self.cfg.provider_cache,
                    args,
                    settings: self.prep.settings.clone(),
                    env: self.prep.env.clone(),
                    capture_stdout: capture,
                },
                &self.ctx.log,
                &self.ctx.cancel,
            )
            .await?;
        if out.cancelled {
            return Err(Failure::Cancelled);
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
        args.extend(self.prep.backend_args.iter().cloned());
        self.run_ok("init", args, false).await?;
        if let Some(ws) = &self.prep.input.backend.workspace {
            let args = vec![
                "workspace".into(),
                "select".into(),
                "-or-create=true".into(),
                "-no-color".into(),
                ws.clone(),
            ];
            self.run_ok("workspace", args, false).await?;
        }
        Ok(())
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
    let tofu = Tofu {
        cfg,
        ctx,
        prep: &prep,
    };
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

    if has_changes || prep.input.retain_no_changes_plan {
        let plan_bin = fs::read(&plan_path)
            .map_err(|e| Failure::msg(format!("reading the saved plan: {e}")))?;
        let lock_path = prep.root.join(LOCK_FILE);
        let lock = fs::read(&lock_path).ok();
        let mut files: Vec<(&str, &[u8])> = vec![("plan.bin", &plan_bin)];
        match &lock {
            Some(l) => files.push((LOCK_FILE, l)),
            None => ctx.log.agent("no .terraform.lock.hcl after init (no providers); the artifact holds only plan.bin"),
        }
        let content = archive::pack(&files)?;
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
    if artifact.sha256.is_empty() {
        ctx.log.agent(format!(
            "{PLAN_ARTIFACT} has no sha256; its content hashes to {actual}"
        ));
    } else if !artifact.sha256.eq_ignore_ascii_case(&actual) {
        return Err(Failure::msg(format!(
            "{PLAN_ARTIFACT} is corrupt: sha256 {actual}, expected {}",
            artifact.sha256
        )));
    }

    let prep = prepare(cfg, ctx)?;
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
    if saved_lock.is_file() {
        let lock = fs::read(&saved_lock).map_err(anyhow::Error::from)?;
        write_fresh(&prep.root.join(LOCK_FILE), &lock)?;
        ctx.log
            .agent("restored .terraform.lock.hcl from the preview");
    }

    tofu.version().await?;
    tofu.init(saved_lock.is_file()).await?;
    let plan_arg = tofu.path(&plan_path)?;
    let watcher = prep.built_in.then(|| {
        tokio::spawn(state::watch(
            ctx.workspace.clone(),
            cfg.poll_interval,
            ctx.snapshots.clone(),
        ))
    });
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
    if let Some(watcher) = watcher {
        watcher.abort();
    }
    let applied = applied?;
    if applied.code != Some(0) {
        if prep.built_in && state::lost(&ctx.workspace, &applied.stderr_tail) {
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
        Ok(out) => plan::split_outputs(&out.stdout)?,
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

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn env_names() {
        assert!(check_env_name("PROXMOX_VE_API_TOKEN").is_ok());
        assert!(check_env_name("PG_CONN_STR").is_ok());
        for bad in [
            "",
            "1X",
            "A-B",
            "HOME",
            "TF_PLUGIN_CACHE_DIR",
            "TF_CLI_ARGS_plan",
            "CONTAINERS_CONF",
        ] {
            assert!(check_env_name(bad).is_err(), "{bad}");
        }
    }

    #[test]
    fn roots_and_backend_values() {
        assert_eq!(
            check_root("modules/opentofu/x").unwrap(),
            PathBuf::from("modules/opentofu/x")
        );
        assert_eq!(check_root("./a").unwrap(), PathBuf::from("a"));
        assert!(check_root("../a").is_err());
        assert!(check_root("/a").is_err());
        assert_eq!(
            backend_value("port", &json!(5432)).unwrap().as_deref(),
            Some("5432")
        );
        assert_eq!(
            backend_value("x", &json!(true)).unwrap().as_deref(),
            Some("true")
        );
        assert_eq!(backend_value("x", &Value::Null).unwrap(), None);
        assert!(backend_value("x", &json!({})).is_err());
        assert!(backend_value("a=b", &json!("v")).is_err());
    }
}
