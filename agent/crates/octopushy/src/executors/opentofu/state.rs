//! The built-in state: the controller keeps it, and OpenTofu sees it as a `local`
//! backend file inside the attempt's workspace.
//!
//! The state arrives with the assignment and leaves with the result, so it needs
//! no route between the tool and anything else: not to the controller, and not
//! to a loopback address, which a bridged container cannot reach. The controller
//! owns locking and versions, and a preview has no way to send state back. While
//! an apply runs, changes also go out as snapshots, so a lost machine loses
//! little. They and the final state are never logged.

use std::fs;
use std::path::{Path, PathBuf};
use std::sync::{Arc, Mutex};
use std::time::Duration;

use octopushy_proto::v1::Artifact;
use serde_json::Value;

use super::{ExecContext, Failure, write_fresh};
use crate::tls::sha256_hex;

/// The `backend.type` that selects the built-in state.
pub const BACKEND_TYPE: &str = "octopushy";
/// What `Hello.capabilities` advertises.
pub const CAPABILITY: &str = "opentofu-builtin-state";
pub const ARTIFACT: &str = "terraform.tfstate";
/// Where OpenTofu saves the state, in its working directory, when it cannot write
/// the backend file. It is then the only copy of what the apply did.
const ERRORED: &str = "errored.tfstate";
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
pub fn backend_path(workspace: &Path, root: &Path) -> Result<String, Failure> {
    let workspace = workspace.canonicalize().map_err(anyhow::Error::from)?;
    let depth = root
        .strip_prefix(&workspace)
        .map_err(|_| Failure::msg("the root is outside the attempt's workspace"))?
        .components()
        .count();
    Ok(format!("{}state/{ARTIFACT}", "../".repeat(depth)))
}

/// Writes the state the assignment carries, if it carries one yet.
pub fn restore(ctx: &ExecContext) -> Result<(), Failure> {
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
pub fn record_root(workspace: &Path, root: &Path) -> Result<(), Failure> {
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

/// The newest complete state in the workspace: OpenTofu's emergency state if it
/// left one, or else the backend file.
pub fn read(workspace: &Path) -> Option<Artifact> {
    let emergency = fs::read_to_string(root_record(workspace))
        .ok()
        .and_then(|root| complete(&workspace.join(root).join(ERRORED)));
    emergency.or_else(|| complete(&file(workspace)))
}

/// The state at `path`, if it is complete. OpenTofu rewrites the backend file in
/// place, so a read during an apply can see part of it.
fn complete(path: &Path) -> Option<Artifact> {
    let content = fs::read(path).ok()?;
    if content.len() > MAX_BYTES {
        tracing::error!("the OpenTofu state is too large to send");
        return None;
    }
    let parsed: Value = serde_json::from_slice(&content).ok()?;
    parsed.get("lineage")?.as_str()?;
    Some(Artifact {
        name: ARTIFACT.to_owned(),
        sha256: sha256_hex(&content),
        content,
    })
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
