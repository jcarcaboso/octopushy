//! The built-in state: the controller keeps it, and OpenTofu sees it as a `local`
//! backend file inside the attempt's workspace.
//!
//! The state arrives with the assignment and leaves with the result, so it needs
//! no route between the tool and anything else: not to the controller, and not
//! to a loopback address, which a bridged container cannot reach. The controller
//! owns locking and versions, and a preview has no way to send state back. While
//! an apply runs, changes also go out as snapshots, so a lost machine loses
//! little. They and the final state are never logged.

use crate::fsutil::read_tool_file;
use anyhow::{Context, Result, bail};
use std::fs;
use std::path::{Component, Path, PathBuf};
use std::sync::{Arc, Mutex};
use std::time::Duration;

use octopushy_proto::v1::Artifact;
use serde_json::Value;

use super::{ExecContext, Failure, write_fresh};
use crate::tls::sha256_hex;

/// What `Hello.capabilities` advertises.
pub const CAPABILITY: &str = "opentofu-builtin-state";
pub const ARTIFACT: &str = "terraform.tfstate";
/// Where OpenTofu saves the state, in its working directory, when it cannot write
/// the backend file. It is then the only copy of what the apply did.
const ERRORED: &str = "errored.tfstate";
/// The line OpenTofu prints whenever it cannot write the backend file. It is matched
/// whole, because a provider's or provisioner's output can quote the words. It can
/// also claim to have written `errored.tfstate` when it could not, so only the file
/// itself shows that it did.
pub const NOT_SAVED: &str = "Error: Failed to persist state to backend";
/// Larger states are not sent: a result shares the 64 MiB message limit.
const MAX_BYTES: usize = 32 * 1024 * 1024;

/// The newest state waiting to be sent. Only the latest matters, so it replaces
/// rather than queues, and the connection takes it when it next collects.
#[derive(Clone, Debug, Default)]
pub struct Snapshots(Arc<Mutex<Option<Artifact>>>);

impl Snapshots {
    fn put(&self, state: Artifact) {
        *self.0.lock().unwrap() = Some(state);
    }

    pub fn take(&self) -> Option<Artifact> {
        self.0.lock().unwrap().take()
    }
}

fn file(workspace: &Path) -> PathBuf {
    workspace.join("state").join(ARTIFACT)
}

/// Names the root, so its emergency state can be found after a restart too.
fn root_record(workspace: &Path) -> PathBuf {
    workspace.join("state").join("root")
}

/// The backend's `path`, from the root. It is relative because a saved plan keeps
/// it and applies from another workspace, and a container sees other absolute paths.
pub fn backend_path(workspace: &Path, root: &Path) -> std::result::Result<String, Failure> {
    let workspace = workspace.canonicalize().map_err(anyhow::Error::from)?;
    let depth = root
        .strip_prefix(&workspace)
        .map_err(|_| Failure::msg("the root is outside the attempt's workspace"))?
        .components()
        .count();
    Ok(format!("{}state/{ARTIFACT}", "../".repeat(depth)))
}

/// Writes the state the assignment carries, if it carries one yet.
pub fn restore(ctx: &ExecContext) -> std::result::Result<(), Failure> {
    let path = file(&ctx.workspace);
    let directory = path.parent().expect("the state file has a directory");
    fs::create_dir_all(directory)
        .map_err(|e| Failure::msg(format!("creating the state directory: {e}")))?;
    let Some(state) = ctx.artifacts.iter().find(|a| a.name == ARTIFACT) else {
        ctx.log.agent("no OpenTofu state yet");
        return Ok(());
    };
    if !state
        .sha256
        .eq_ignore_ascii_case(&sha256_hex(&state.content))
    {
        return Err(Failure::msg("the OpenTofu state is corrupt"));
    }
    write_fresh(&path, &state.content)
}

/// Records where OpenTofu runs. A file of that name from the repository goes first:
/// it is not this apply's emergency state.
pub fn record_root(workspace: &Path, root: &Path) -> std::result::Result<(), Failure> {
    let workspace = workspace.canonicalize().map_err(anyhow::Error::from)?;
    let relative = root
        .strip_prefix(&workspace)
        .map_err(|_| Failure::msg("the root is outside the attempt's workspace"))?;
    if let Err(e) = fs::remove_file(root.join(ERRORED))
        && e.kind() != std::io::ErrorKind::NotFound
    {
        return Err(Failure::msg(format!("removing {ERRORED}: {e}")));
    }
    write_fresh(
        &root_record(&workspace),
        relative.to_string_lossy().as_bytes(),
    )
}

/// OpenTofu's emergency state, if it left one, however complete.
fn optional_file(workspace: &Path, path: &Path, cap: usize) -> Result<Option<Vec<u8>>> {
    match read_tool_file(workspace, path, cap) {
        Ok(content) => Ok(Some(content)),
        Err(e)
            if e.downcast_ref::<std::io::Error>()
                .is_some_and(|e| e.kind() == std::io::ErrorKind::NotFound) =>
        {
            Ok(None)
        }
        Err(e) => Err(e),
    }
}

fn emergency(workspace: &Path) -> Result<Option<PathBuf>> {
    let Some(root) = optional_file(workspace, &root_record(workspace), 16 * 1024)? else {
        return Ok(None);
    };
    let root = String::from_utf8(root).context("invalid OpenTofu root record")?;
    if !Path::new(&root)
        .components()
        .all(|c| matches!(c, Component::Normal(_)))
    {
        bail!("OpenTofu root record leaves the workspace");
    }
    let path = workspace.join(root).join(ERRORED);
    Ok(path.symlink_metadata().is_ok().then_some(path))
}

/// Whether OpenTofu left an emergency state, complete or not.
pub fn has_emergency(workspace: &Path) -> bool {
    emergency(workspace).ok().flatten().is_some()
}

/// The newest complete state. Refuse unsafe files rather than sending their contents.
pub fn read(workspace: &Path) -> Option<Artifact> {
    match read_checked(workspace) {
        Ok(state) => state,
        Err(e) => {
            tracing::error!("cannot read OpenTofu state: {e:#}");
            None
        }
    }
}

pub fn read_checked(workspace: &Path) -> Result<Option<Artifact>> {
    let path = emergency(workspace)?.unwrap_or_else(|| file(workspace));
    let Some(content) = optional_file(workspace, &path, MAX_BYTES)? else {
        return Ok(None);
    };
    // A read during an apply can see a partial rewrite. Only complete states count.
    let parsed: Value = match serde_json::from_slice(&content) {
        Ok(value) => value,
        Err(_) => return Ok(None),
    };
    if parsed.get("lineage").and_then(Value::as_str).is_none() {
        return Ok(None);
    }
    Ok(Some(Artifact {
        name: ARTIFACT.to_owned(),
        sha256: sha256_hex(&content),
        content,
    }))
}

/// Offers each new state as a snapshot, until it is aborted.
pub async fn watch(workspace: PathBuf, every: Duration, snapshots: Snapshots) {
    let mut last = read(&workspace).map(|state| state.sha256);
    loop {
        tokio::time::sleep(every).await;
        if let Some(state) = read(&workspace)
            && last.as_deref() != Some(&state.sha256)
        {
            last = Some(state.sha256.clone());
            snapshots.put(state);
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn the_backend_path_climbs_out_of_the_root() {
        let dir = tempfile::tempdir().unwrap();
        let ws = dir.path().canonicalize().unwrap();
        let root = ws.join("repo/modules/opentofu/vm");
        assert_eq!(
            backend_path(&ws, &root).unwrap(),
            "../../../../state/terraform.tfstate"
        );
        assert!(backend_path(&ws, Path::new("/elsewhere")).is_err());
    }

    #[test]
    fn tool_written_state_and_root_links_are_rejected() {
        use std::os::unix::fs::symlink;
        for target in [ARTIFACT, "root"] {
            let dir = tempfile::tempdir().unwrap();
            fs::create_dir(dir.path().join("state")).unwrap();
            symlink("/etc/passwd", dir.path().join("state").join(target)).unwrap();
            let error = read_checked(dir.path()).unwrap_err();
            assert!(error.to_string().contains("without following links"));
        }
        let dir = tempfile::tempdir().unwrap();
        fs::create_dir(dir.path().join("state")).unwrap();
        fs::create_dir(dir.path().join("repo")).unwrap();
        record_root(dir.path(), &dir.path().join("repo")).unwrap();
        symlink("/etc/passwd", dir.path().join("repo").join(ERRORED)).unwrap();
        assert!(read_checked(dir.path()).is_err());
    }

    #[test]
    fn only_a_complete_state_is_read() {
        let dir = tempfile::tempdir().unwrap();
        assert!(read(dir.path()).is_none());
        fs::create_dir(dir.path().join("state")).unwrap();
        fs::write(file(dir.path()), br#"{"version":4,"lin"#).unwrap();
        assert!(read(dir.path()).is_none(), "a partial write");
        fs::write(file(dir.path()), br#"{"version":4}"#).unwrap();
        assert!(read(dir.path()).is_none(), "not a state");
        let state = br#"{"version":4,"serial":1,"lineage":"l"}"#;
        fs::write(file(dir.path()), state).unwrap();
        let read = read(dir.path()).unwrap();
        assert_eq!(read.content, state);
        assert_eq!(read.sha256, sha256_hex(state));
    }

    #[test]
    fn the_emergency_state_wins_but_only_from_this_apply() {
        let dir = tempfile::tempdir().unwrap();
        let ws = dir.path().canonicalize().unwrap();
        let root = ws.join("repo/vm");
        fs::create_dir_all(&root).unwrap();
        fs::create_dir(ws.join("state")).unwrap();
        fs::write(file(&ws), br#"{"serial":0,"lineage":"l"}"#).unwrap();

        // Committed to the repository: not OpenTofu's, so it must not count.
        fs::write(root.join(ERRORED), br#"{"serial":9,"lineage":"evil"}"#).unwrap();
        record_root(&ws, &root).unwrap();
        assert!(!root.join(ERRORED).exists());
        assert!(String::from_utf8_lossy(&read(&ws).unwrap().content).contains(r#""serial":0"#));

        fs::write(root.join(ERRORED), br#"{"serial":1,"lineage":"l"}"#).unwrap();
        assert!(String::from_utf8_lossy(&read(&ws).unwrap().content).contains(r#""serial":1"#));

        // A partial one must not read as if the backend file were still current.
        fs::write(root.join(ERRORED), br#"{"serial":1,"lin"#).unwrap();
        assert!(read(&ws).is_none());
    }

    #[tokio::test]
    async fn watching_offers_only_changes() {
        let dir = tempfile::tempdir().unwrap();
        fs::create_dir(dir.path().join("state")).unwrap();
        fs::write(file(dir.path()), br#"{"serial":1,"lineage":"l"}"#).unwrap();
        let snapshots = Snapshots::default();
        let watcher = tokio::spawn(watch(
            dir.path().to_owned(),
            Duration::from_millis(5),
            snapshots.clone(),
        ));
        tokio::time::sleep(Duration::from_millis(40)).await;
        assert!(snapshots.take().is_none(), "the restored state is not new");
        fs::write(file(dir.path()), br#"{"serial":2,"lineage":"l"}"#).unwrap();
        tokio::time::sleep(Duration::from_millis(40)).await;
        let state = snapshots.take().expect("a snapshot of the change");
        assert!(String::from_utf8_lossy(&state.content).contains(r#""serial":2"#));
        watcher.abort();
    }
}
