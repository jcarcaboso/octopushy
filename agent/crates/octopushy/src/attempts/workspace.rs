//! Attempt workspaces and their `attempt.json` marker.
//!
//! The marker is written when an assignment is accepted (`queued`) and updated
//! when it starts executing (`running`). An executor whose action is in
//! [`executors::MARKS_CHANGE`] updates it once more, right before the first thing
//! that changes anything (`changeStartedAtMs`). After a restart, a workspace with a
//! marker but no outbox result is an attempt the agent lost track of.
//!
//! Every write is atomic and durable, directory entry included, before the call
//! returns.
//!
//! A tool has the workspace mounted and can write the marker. So that a change
//! started is also recorded beside the workspace, where no tool reaches: that
//! record, not the marker, is what a restart believes.

use std::fs;
use std::path::{Path, PathBuf};

use anyhow::{Context, Result};
use serde::{Deserialize, Serialize};

use crate::executors;
use crate::fsutil;
use crate::identity::now_ms;

pub const MARKER: &str = "attempt.json";

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "lowercase")]
pub enum Phase {
    /// Accepted, waiting for capacity; nothing ran yet.
    Queued,
    /// The executor started.
    Running,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct Marker {
    pub attempt_id: String,
    pub executor: String,
    pub action: String,
    pub phase: Phase,
    pub accepted_at_ms: u64,
    #[serde(default)]
    pub started_at_ms: Option<u64>,
    /// Whether the attempt may change infrastructure, as decided when it was accepted. Absent
    /// in the markers of older agents: the agent's own table decides then.
    #[serde(default)]
    pub mutating: Option<bool>,
    /// Set with `running` when the executor records the start of its change. Absent, as in the
    /// markers of older agents, a missing `changeStartedAtMs` says nothing.
    #[serde(default)]
    pub marks_change: bool,
    /// When the executor was about to make its first change.
    #[serde(default)]
    pub change_started_at_ms: Option<u64>,
}

/// The workspace directory of `attempt_id`.
pub fn dir(work_dir: &Path, attempt_id: &str) -> PathBuf {
    work_dir.join(attempt_id)
}

/// Creates a fresh workspace with a `queued` marker.
pub fn create(
    work_dir: &Path,
    attempt_id: &str,
    executor: &str,
    action: &str,
    mutating: bool,
) -> Result<PathBuf> {
    let ws = dir(work_dir, attempt_id);
    // Whatever an earlier, unreported try of the same attempt left over.
    remove(&ws);
    fsutil::ensure_private_dir(&ws)?;
    init(&ws, attempt_id, executor, action, mutating)?;
    Ok(ws)
}

/// Writes a `queued` marker into the existing directory `ws`.
pub fn init(
    ws: &Path,
    attempt_id: &str,
    executor: &str,
    action: &str,
    mutating: bool,
) -> Result<()> {
    let marker = Marker {
        attempt_id: attempt_id.to_owned(),
        executor: executor.to_owned(),
        action: action.to_owned(),
        phase: Phase::Queued,
        accepted_at_ms: now_ms(),
        started_at_ms: None,
        mutating: Some(mutating),
        marks_change: false,
        change_started_at_ms: None,
    };
    write(ws, &marker)
}

fn write(ws: &Path, marker: &Marker) -> Result<()> {
    fsutil::write_atomic(&ws.join(MARKER), &serde_json::to_vec_pretty(marker)?, 0o600)
}

/// Records that the executor is starting, and whether it will record the start of its change.
pub fn mark_running(ws: &Path) -> Result<()> {
    let mut marker = read(ws)?.context("the attempt marker is missing")?;
    marker.phase = Phase::Running;
    marker.started_at_ms = Some(now_ms());
    marker.marks_change = executors::marks_change(&marker.executor, &marker.action);
    write(ws, &marker)
}

/// Where it is recorded that the attempt's change started: beside the workspace, not in it.
fn change_record(ws: &Path) -> Result<PathBuf> {
    let name = ws
        .file_name()
        .context("the workspace has no name")?
        .to_string_lossy();
    Ok(ws.with_file_name(format!("{name}.change-started")))
}

/// Whether a change of this attempt started, or that cannot be ruled out.
pub fn change_started(ws: &Path) -> bool {
    match change_record(ws).map(fs::symlink_metadata) {
        Ok(Err(e)) => e.kind() != std::io::ErrorKind::NotFound,
        _ => true,
    }
}

/// Records that the executor is about to make its first change. Durable when it returns; on an
/// error the change must not start. Keeps the time of the first call. For a change (`mutating`,
/// as the agent holds it in memory, not as the marker says) it is first recorded beside the
/// workspace.
pub fn mark_change_started(ws: &Path, mutating: bool) -> Result<()> {
    let mut marker = read(ws)?.context("the attempt marker is missing")?;
    if marker.phase != Phase::Running {
        anyhow::bail!("the attempt marker does not say the attempt is running");
    }
    if mutating {
        fsutil::write_atomic(&change_record(ws)?, b"", 0o600)?;
    }
    if marker.change_started_at_ms.is_some() {
        return Ok(());
    }
    marker.change_started_at_ms = Some(now_ms());
    write(ws, &marker)
}

/// Reads the marker, if there is one.
pub fn read(ws: &Path) -> Result<Option<Marker>> {
    match fs::read(ws.join(MARKER)) {
        Ok(bytes) => {
            Ok(Some(serde_json::from_slice(&bytes).with_context(|| {
                format!("parsing {}", ws.join(MARKER).display())
            })?))
        }
        Err(e) if e.kind() == std::io::ErrorKind::NotFound => Ok(None),
        Err(e) => Err(e.into()),
    }
}

/// Durably removes what a restart reports an attempt from: its marker and the record beside the
/// workspace. For an acknowledged attempt, before its stored result goes: a restart in between
/// then finds a workspace that says nothing and discards it.
pub fn forget(ws: &Path) -> Result<()> {
    fsutil::remove_durably(&change_record(ws)?)?;
    fsutil::remove_durably(&ws.join(MARKER))
}

/// Deletes a workspace and the record beside it, logging instead of failing.
pub fn remove(ws: &Path) {
    if let Ok(record) = change_record(ws) {
        let _ = fs::remove_file(record);
    }
    if let Err(e) = fs::remove_dir_all(ws)
        && e.kind() != std::io::ErrorKind::NotFound
    {
        tracing::warn!("could not remove workspace {}: {e}", ws.display());
    }
}
