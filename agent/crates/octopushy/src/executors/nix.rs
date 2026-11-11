//! The `nix` executor: `build` builds a flake output, a built template's image, and sends
//! it to the server with `UploadImage`.
//!
//! Workspace layout of an attempt:
//!
//! ```text
//! <work>/<attempt>/
//!   attempt.json   marker (see attempts::workspace)
//!   repo/          the extracted run bundle; `flake` and `lock` are inside it
//!   home/          HOME for nix
//!   tmp/           TMPDIR: on the agent's state volume, never /tmp
//! ```
//!
//! Nix runs only with the `local` tool runtime, single-user, with the store at
//! [`NixSettings::store_dir`]. The store is a cache: after a build it is collected down to
//! [`NixSettings::store_max_bytes`].

use std::collections::HashSet;
use std::fs;
use std::os::unix::fs::MetadataExt;
use std::path::{Path, PathBuf};
use std::time::Instant;

use serde::Deserialize;
use serde_json::json;
use tokio::sync::RwLock;

use super::opentofu::{archive, check_root, write_fresh};
use super::{ExecContext, ExecResult, ExecutorConfig, Failure, Success};
use crate::images::{self, ImageDigest};
use crate::tools::{Invocation, Tool, ToolOutput};

/// Image file extensions a build output folder is searched for.
const IMAGE_EXTENSIONS: &[&str] = &["qcow2", "raw", "img"];
const GIB: u64 = 1024 * 1024 * 1024;

/// Settings of the `nix` executor.
#[derive(Debug)]
pub struct NixSettings {
    /// The folder holding the Nix store (`/nix`).
    pub store_dir: PathBuf,
    /// Above this, a finished build collects garbage (`OCTOPUSHY_NIX_STORE_MAX_GB`).
    pub store_max_bytes: u64,
    /// `XDG_CACHE_HOME` for nix (fetcher and evaluation caches), kept across builds. `None`:
    /// the attempt's own home.
    pub cache_dir: Option<PathBuf>,
    /// Held for reading by every build until its image is sent, and for writing by garbage
    /// collection: `--no-link` leaves a finished output unrooted.
    gc: RwLock<()>,
}

impl NixSettings {
    pub const DEFAULT_MIN_FREE_GB: u64 = 20;
    pub const DEFAULT_STORE_MAX_GB: u64 = 40;

    pub fn new(store_dir: PathBuf, store_max_bytes: u64, cache_dir: Option<PathBuf>) -> Self {
        Self {
            store_dir,
            store_max_bytes,
            cache_dir,
            gc: RwLock::new(()),
        }
    }
}

impl Default for NixSettings {
    fn default() -> Self {
        Self::new(
            PathBuf::from("/nix"),
            Self::DEFAULT_STORE_MAX_GB * GIB,
            None,
        )
    }
}

/// Bytes available to an unprivileged user on the file system holding `path`. An error when
/// this user cannot write there, such as a read-only container root without a volume at /nix:
/// a store Nix cannot write to builds nothing, however much room it has.
pub fn free_bytes(path: &Path) -> std::io::Result<u64> {
    use std::os::unix::ffi::OsStrExt;
    let c = std::ffi::CString::new(path.as_os_str().as_bytes())?;
    // SAFETY: `c` is a valid C string and `stat` a valid out-pointer for the call.
    let mut stat: libc::statvfs = unsafe { std::mem::zeroed() };
    if unsafe { libc::statvfs(c.as_ptr(), &mut stat) } != 0 {
        return Err(std::io::Error::last_os_error());
    }
    #[allow(clippy::unnecessary_cast)]
    if stat.f_flag as u64 & libc::ST_RDONLY as u64 != 0 {
        return Err(std::io::Error::other(format!(
            "{} is on a read-only file system",
            path.display()
        )));
    }
    // SAFETY: `c` is a valid C string.
    if unsafe { libc::access(c.as_ptr(), libc::W_OK) } != 0 {
        return Err(std::io::Error::other(format!(
            "{} is not writable: {}",
            path.display(),
            std::io::Error::last_os_error()
        )));
    }
    #[allow(clippy::unnecessary_cast)]
    let free = stat.f_bavail as u64 * stat.f_frsize as u64;
    Ok(free)
}

/// Whether to advertise `nix`: the runtime is local, `nix --version` works, and the store's
/// file system has at least `min_free` bytes free. `Err` says why not.
pub fn availability(
    local: bool,
    version: Result<String, String>,
    free: std::io::Result<u64>,
    min_free: u64,
) -> Result<String, String> {
    if !local {
        return Err("nix runs only with the local tool runtime".into());
    }
    let version = version?;
    let free = free.map_err(|e| format!("cannot tell the free space of the Nix store: {e}"))?;
    if free < min_free {
        return Err(format!(
            "the Nix store has {:.1} GiB free, less than the {:.1} GiB a build needs",
            free as f64 / GIB as f64,
            min_free as f64 / GIB as f64
        ));
    }
    Ok(format!(
        "{version}, {:.1} GiB free",
        free as f64 / GIB as f64
    ))
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
struct Input {
    #[serde(default)]
    key: String,
    flake: String,
    output: String,
    system: String,
    #[serde(default)]
    lock: Option<String>,
}

/// An attribute name part: letters, digits, `_` and `-`.
fn check_attr(what: &str, value: &str) -> Result<(), Failure> {
    let ok = !value.is_empty()
        && value.len() <= 128
        && value
            .bytes()
            .all(|b| b.is_ascii_alphanumeric() || b == b'_' || b == b'-');
    if ok {
        Ok(())
    } else {
        Err(Failure::msg(format!(
            "{what} {value:?} must be letters, digits, '_' and '-'"
        )))
    }
}

pub async fn run(cfg: &ExecutorConfig, ctx: &ExecContext) -> ExecResult {
    match ctx.action.as_str() {
        "build" => build(cfg, ctx).await,
        other => Err(Failure::msg(format!(
            "unknown nix action {other:?}; expected build"
        ))),
    }
}

/// A path inside `repo`, resolved, that must not leave it.
fn inside(repo: &Path, rel: &str, what: &str) -> Result<PathBuf, Failure> {
    let path = repo.join(check_root(rel).map_err(|_| {
        Failure::msg(format!(
            "{what} {rel:?} must be a relative path inside the bundle"
        ))
    })?);
    let canonical = path
        .canonicalize()
        .map_err(|_| Failure::msg(format!("{what} {rel:?} is not in the bundle")))?;
    let repo = repo.canonicalize().map_err(anyhow::Error::from)?;
    if !canonical.starts_with(&repo) {
        return Err(Failure::msg(format!("{what} {rel:?} leaves the bundle")));
    }
    Ok(canonical)
}

async fn build(cfg: &ExecutorConfig, ctx: &ExecContext) -> ExecResult {
    let input: Input = ctx.input()?;
    check_attr("output", &input.output)?;
    check_attr("system", &input.system)?;
    if cfg.runtime.is_container() {
        return Err(Failure::msg(
            "nix runs only with the local tool runtime; this agent should not have been sent a build",
        ));
    }

    let ws = &ctx.workspace;
    let mk = |p: &Path| {
        fs::create_dir_all(p).map_err(|e| Failure::msg(format!("creating {}: {e}", p.display())))
    };
    let home = ws.join("home");
    let tmp = ws.join("tmp");
    mk(&home)?;
    mk(&tmp)?;
    let repo = ws.join("repo");
    let stats = archive::extract_tar_gz(&ctx.files_tar_gz, &repo, archive::Limits::default())
        .map_err(|e| Failure::msg(format!("cannot extract the bundle: {e:#}")))?;
    ctx.log.agent(format!(
        "extracted {} files ({} KiB) for build key {}",
        stats.files,
        stats.bytes / 1024,
        if input.key.is_empty() {
            "?"
        } else {
            &input.key
        }
    ));

    let flake = inside(&repo, &input.flake, "flake")?;
    if !flake.is_dir() {
        return Err(Failure::msg(format!(
            "flake {:?} is not a folder in the bundle",
            input.flake
        )));
    }
    if let Some(lock) = &input.lock {
        let source = inside(&repo, lock, "lock")?;
        if !source.is_file() {
            return Err(Failure::msg(format!("lock {lock:?} is not a file")));
        }
        let bytes =
            fs::read(&source).map_err(|e| Failure::msg(format!("reading lock {lock:?}: {e}")))?;
        write_fresh(&flake.join("flake.lock"), &bytes)?;
        ctx.log
            .agent(format!("using {lock} as {}/flake.lock", input.flake));
    }

    let mut settings = vec![
        ("HOME".to_owned(), home.to_string_lossy().into_owned()),
        ("TMPDIR".to_owned(), tmp.to_string_lossy().into_owned()),
    ];
    if let Some(cache) = &cfg.nix.cache_dir {
        mk(cache)?;
        settings.push((
            "XDG_CACHE_HOME".to_owned(),
            cache.to_string_lossy().into_owned(),
        ));
    }
    // Where nix finds certificates and its configuration, if the agent was told.
    for name in ["NIX_SSL_CERT_FILE", "SSL_CERT_FILE", "NIX_CONF_DIR"] {
        if let Some(v) = std::env::var_os(name) {
            settings.push((name.to_owned(), v.to_string_lossy().into_owned()));
        }
    }

    let result = {
        let _building = cfg.nix.gc.read().await;
        build_and_upload(cfg, ctx, &input, &flake, &settings).await
    };
    if !ctx.cancel.is_cancelled() {
        collect_garbage(cfg, ctx, &settings).await;
    }
    result
}

async fn nix(
    cfg: &ExecutorConfig,
    ctx: &ExecContext,
    step: &str,
    args: Vec<String>,
    settings: &[(String, String)],
    capture: bool,
) -> Result<ToolOutput, Failure> {
    let mut name = format!("octopushy-{}-{step}", ctx.attempt_id);
    name.truncate(200);
    let out = cfg
        .runtime
        .run(
            Invocation {
                tool: Tool::Nix,
                name,
                image: "",
                workspace: &ctx.workspace,
                workdir: &ctx.workspace,
                plugin_cache: None,
                args,
                settings: settings.to_vec(),
                env: Vec::new(),
                stdin: None,
                capture_stdout: capture,
                stderr_mark: None,
                control_prefix: None,
            },
            &ctx.log,
            &ctx.cancel,
        )
        .await?;
    if out.cancelled {
        return Err(Failure::Cancelled);
    }
    Ok(out)
}

fn step_failed(step: &str, out: &ToolOutput) -> Failure {
    let code = out
        .code
        .map(|c| c.to_string())
        .unwrap_or_else(|| "a signal".into());
    let detail = out.stderr_summary(5);
    Failure::msg(if detail.is_empty() {
        format!("nix {step} failed (exit status {code})")
    } else {
        format!("nix {step} failed (exit status {code}): {detail}")
    })
}

async fn build_and_upload(
    cfg: &ExecutorConfig,
    ctx: &ExecContext,
    input: &Input,
    flake: &Path,
    settings: &[(String, String)],
) -> ExecResult {
    let installable = format!(
        "path:{}#packages.{}.{}",
        flake.display(),
        input.system,
        input.output
    );
    let started = Instant::now();
    let out = nix(
        cfg,
        ctx,
        "build",
        vec![
            "build".into(),
            "--no-update-lock-file".into(),
            "--no-link".into(),
            "--print-out-paths".into(),
            installable,
        ],
        settings,
        true,
    )
    .await?;
    let stdout = String::from_utf8_lossy(&out.stdout).into_owned();
    for line in stdout.lines() {
        ctx.log.stdout(line);
    }
    if out.code != Some(0) {
        return Err(step_failed("build", &out));
    }
    let paths: Vec<&str> = stdout
        .lines()
        .map(str::trim)
        .filter(|l| !l.is_empty())
        .collect();
    let out_path = match paths.as_slice() {
        [one] => PathBuf::from(one),
        [] => return Err(Failure::msg("nix build printed no output path")),
        many => {
            return Err(Failure::msg(format!(
                "nix build printed {} output paths; an image output has one",
                many.len()
            )));
        }
    };
    ctx.log.agent(format!(
        "built {} in {:.1}s",
        out_path.display(),
        started.elapsed().as_secs_f64()
    ));

    let image = resolve_image(&out_path)?;
    let file_name = image
        .file_name()
        .map(|n| n.to_string_lossy().into_owned())
        .unwrap_or_default();
    images::check_file_name(&file_name)
        .map_err(|e| Failure::msg(format!("the image's name: {e:#}")))?;
    let size = fs::metadata(&image).map(|m| m.len()).unwrap_or(0);
    ctx.log.agent(format!(
        "sending {} ({:.1} MiB) to the server",
        image.display(),
        size as f64 / (1024.0 * 1024.0)
    ));
    let started = Instant::now();
    let upload = images::upload(&cfg.server, &ctx.attempt_id, &image, &file_name);
    let ImageDigest { sha256, size } = tokio::select! {
        r = upload => r.map_err(|e| Failure::msg(format!("sending the image: {e:#}")))?,
        _ = ctx.cancel.cancelled() => return Err(Failure::Cancelled),
    };
    ctx.log.agent(format!(
        "the server received the image in {:.1}s: {size} bytes, sha256 {sha256}",
        started.elapsed().as_secs_f64()
    ));
    Ok(Success::outputs(json!({
        "sha256": sha256,
        "size": size,
        "fileName": file_name,
        "outPath": out_path.to_string_lossy(),
    })))
}

/// The image in a build output: the output itself if it is a regular file, or the one regular
/// file ending in `.qcow2`, `.raw` or `.img` directly in it.
pub fn resolve_image(out_path: &Path) -> Result<PathBuf, Failure> {
    let meta = fs::symlink_metadata(out_path)
        .map_err(|e| Failure::msg(format!("{}: {e}", out_path.display())))?;
    if meta.is_file() {
        return Ok(out_path.to_path_buf());
    }
    if !meta.is_dir() {
        return Err(Failure::msg(format!(
            "the build output {} is neither a file nor a folder",
            out_path.display()
        )));
    }
    let mut found = Vec::new();
    let entries = fs::read_dir(out_path)
        .map_err(|e| Failure::msg(format!("reading {}: {e}", out_path.display())))?;
    for entry in entries {
        let entry = entry.map_err(|e| Failure::msg(format!("{}: {e}", out_path.display())))?;
        // A symlink is not a regular file, even when it names one.
        if !entry.file_type().is_ok_and(|t| t.is_file()) {
            continue;
        }
        let path = entry.path();
        if path
            .extension()
            .and_then(|e| e.to_str())
            .is_some_and(|e| IMAGE_EXTENSIONS.contains(&e))
        {
            found.push(path);
        }
    }
    found.sort();
    match found.len() {
        1 => Ok(found.remove(0)),
        0 => Err(Failure::msg(format!(
            "the build output {} holds no .qcow2, .raw or .img file",
            out_path.display()
        ))),
        _ => Err(Failure::msg(format!(
            "the build output {} holds several images: {}",
            out_path.display(),
            found
                .iter()
                .filter_map(|p| p.file_name())
                .map(|n| n.to_string_lossy())
                .collect::<Vec<_>>()
                .join(", ")
        ))),
    }
}

/// Bytes the files under `dir` occupy, each hard-linked file counted once.
pub fn disk_usage(dir: &Path) -> std::io::Result<u64> {
    let mut total = 0u64;
    let mut seen = HashSet::new();
    let mut stack = vec![dir.to_path_buf()];
    while let Some(d) = stack.pop() {
        let entries = match fs::read_dir(&d) {
            Ok(e) => e,
            Err(e) if d != dir && e.kind() == std::io::ErrorKind::PermissionDenied => continue,
            Err(e) => return Err(e),
        };
        for entry in entries {
            let entry = entry?;
            let Ok(meta) = entry.metadata() else { continue };
            if meta.nlink() > 1 && !meta.is_dir() && !seen.insert((meta.dev(), meta.ino())) {
                continue;
            }
            total += meta.blocks() * 512;
            if meta.is_dir() {
                stack.push(entry.path());
            }
        }
    }
    Ok(total)
}

/// Collects garbage down to the store limit, if another build is not using the store. Best
/// effort: a failure is logged, never the attempt's.
async fn collect_garbage(cfg: &ExecutorConfig, ctx: &ExecContext, settings: &[(String, String)]) {
    let Ok(_gc) = cfg.nix.gc.try_write() else {
        ctx.log
            .agent("another build is using the Nix store; not collecting garbage now");
        return;
    };
    let store = cfg.nix.store_dir.join("store");
    let usage = tokio::task::spawn_blocking(move || disk_usage(&store)).await;
    let used = match usage {
        Ok(Ok(used)) => used,
        Ok(Err(e)) => {
            ctx.log
                .agent(format!("warning: cannot measure the Nix store: {e}"));
            return;
        }
        Err(e) => {
            ctx.log
                .agent(format!("warning: cannot measure the Nix store: {e}"));
            return;
        }
    };
    let max = cfg.nix.store_max_bytes;
    if used <= max {
        ctx.log.agent(format!(
            "the Nix store uses {:.1} GiB of {:.1} GiB",
            used as f64 / GIB as f64,
            max as f64 / GIB as f64
        ));
        return;
    }
    let excess = used - max;
    ctx.log.agent(format!(
        "the Nix store uses {:.1} GiB, above {:.1} GiB; collecting garbage",
        used as f64 / GIB as f64,
        max as f64 / GIB as f64
    ));
    let args = vec![
        "store".into(),
        "gc".into(),
        "--max".into(),
        excess.to_string(),
    ];
    match nix(cfg, ctx, "gc", args, settings, false).await {
        Ok(out) if out.code == Some(0) => {}
        Ok(out) => ctx.log.agent(format!(
            "warning: {}",
            step_failed("store gc", &out).message()
        )),
        Err(e) => ctx.log.agent(format!(
            "warning: collecting garbage failed: {}",
            e.message()
        )),
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::os::unix::fs::symlink;

    #[test]
    fn image_resolution() {
        let dir = tempfile::tempdir().unwrap();
        let d = dir.path();

        let file = d.join("abc-nixos.qcow2");
        fs::write(&file, b"img").unwrap();
        assert_eq!(resolve_image(&file).unwrap(), file);

        let out = d.join("out");
        fs::create_dir_all(out.join("nix-support")).unwrap();
        fs::write(out.join("repart-output.json"), b"{}").unwrap();
        assert!(
            resolve_image(&out)
                .unwrap_err()
                .message()
                .contains("holds no")
        );
        fs::write(out.join("nixos.raw"), b"img").unwrap();
        assert_eq!(resolve_image(&out).unwrap(), out.join("nixos.raw"));
        // A link is not a regular file, and neither is a folder with an image's name.
        symlink(out.join("nixos.raw"), out.join("link.img")).unwrap();
        fs::create_dir(out.join("dir.qcow2")).unwrap();
        assert_eq!(resolve_image(&out).unwrap(), out.join("nixos.raw"));
        fs::write(out.join("other.img"), b"img").unwrap();
        let err = resolve_image(&out).unwrap_err();
        assert!(
            err.message()
                .contains("several images: nixos.raw, other.img"),
            "{}",
            err.message()
        );

        let link = d.join("link");
        symlink(&file, &link).unwrap();
        assert!(resolve_image(&link).is_err());
        assert!(resolve_image(&d.join("missing")).is_err());
    }

    #[test]
    fn a_build_is_not_a_mutation() {
        assert!(!super::super::is_mutating("nix", "build"));
    }

    #[test]
    fn attributes() {
        assert!(check_attr("output", "base-image").is_ok());
        assert!(check_attr("system", "x86_64-linux").is_ok());
        for bad in ["", "a.b", "a b", "a\"b", "${x}", "a#b"] {
            assert!(check_attr("output", bad).is_err(), "{bad:?}");
        }
    }

    #[test]
    fn advertised_only_when_local_working_and_roomy() {
        let ok = || Ok::<_, String>("nix (Nix) 2.34.0".to_owned());
        let free = |gb: u64| Ok::<_, std::io::Error>(gb * GIB);
        assert!(availability(true, ok(), free(25), 20 * GIB).is_ok());
        assert!(availability(true, ok(), free(20), 20 * GIB).is_ok());
        assert!(availability(false, ok(), free(25), 20 * GIB).is_err());
        assert!(availability(true, Err("no nix".into()), free(25), 20 * GIB).is_err());
        let short = availability(true, ok(), free(19), 20 * GIB).unwrap_err();
        assert!(short.contains("19.0 GiB free"), "{short}");
        assert!(
            availability(
                true,
                ok(),
                Err(std::io::Error::from(std::io::ErrorKind::NotFound)),
                0
            )
            .is_err()
        );
    }

    #[test]
    fn free_space_and_usage() {
        let dir = tempfile::tempdir().unwrap();
        assert!(free_bytes(dir.path()).unwrap() > 0);
        assert!(free_bytes(&dir.path().join("missing")).is_err());
        let locked = dir.path().join("locked");
        fs::create_dir(&locked).unwrap();
        fs::set_permissions(&locked, std::os::unix::fs::PermissionsExt::from_mode(0o555)).unwrap();
        // root may write anywhere; everyone else is refused.
        if unsafe { libc::geteuid() } != 0 {
            let err = free_bytes(&locked).unwrap_err().to_string();
            assert!(err.contains("not writable"), "{err}");
        }
        fs::create_dir(dir.path().join("a")).unwrap();
        fs::write(dir.path().join("a/f"), vec![1u8; 100_000]).unwrap();
        fs::hard_link(dir.path().join("a/f"), dir.path().join("g")).unwrap();
        let used = disk_usage(dir.path()).unwrap();
        assert!((100_000..200_000).contains(&used), "{used}");
    }
}
