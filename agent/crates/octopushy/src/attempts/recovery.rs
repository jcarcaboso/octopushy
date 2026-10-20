//! Restart recovery: turns workspaces the previous process left behind into
//! results.
//!
//! - A workspace with an outbox result is waiting for its acknowledgement.
//! - A `queued` marker without a result never started: failed.
//! - A `running` marker without a result died mid-attempt: uncertain for
//!   mutations (`opentofu/apply`, `proxmox/set-protection`), failed otherwise.
//! - A workspace without a marker never got past setup and is removed.

use std::fs;
use std::path::Path;

use anyhow::Result;
use octopushy_proto::v1::{AttemptResult, Outcome};

use super::workspace::{self, Marker, Phase};
use crate::executors;
use crate::identity::now_ms;
use crate::outbox::{Outbox, OutboxRecord};
use crate::paths::is_safe_id;

/// The outcome and message to report for an attempt the agent lost.
pub fn classify(marker: &Marker) -> (Outcome, String) {
    match marker.phase {
        Phase::Queued => (
            Outcome::Failed,
            "the agent restarted before the attempt started".to_owned(),
        ),
        Phase::Running if executors::is_mutating(&marker.executor, &marker.action) => (
            Outcome::Uncertain,
            format!(
                "the agent restarted during {}/{}; it cannot tell whether the change was made",
                marker.executor, marker.action
            ),
        ),
        Phase::Running => (
            Outcome::Failed,
            "agent restarted during the attempt".to_owned(),
        ),
    }
}

/// Scans `work_dir`, writes results for lost attempts into `outbox`, and
/// returns the IDs of every result awaiting acknowledgement.
pub fn recover(work_dir: &Path, outbox: &Outbox) -> Result<Vec<String>> {
    if work_dir.exists() {
        for entry in fs::read_dir(work_dir)? {
            let entry = entry?;
            if !entry.file_type()?.is_dir() {
                continue;
            }
            let id = entry.file_name().to_string_lossy().into_owned();
            if !is_safe_id(&id) {
                continue;
            }
            let ws = entry.path();
            if outbox.contains(&id) {
                continue; // reported already; cleaned up on acknowledgement
            }
            let marker = match workspace::read(&ws) {
                Ok(Some(m)) => m,
                Ok(None) => {
                    tracing::info!("removing incomplete workspace {}", ws.display());
                    workspace::remove(&ws);
                    continue;
                }
                Err(e) => {
                    tracing::warn!("unreadable marker in {}: {e:#}", ws.display());
                    Marker {
                        attempt_id: id.clone(),
                        executor: String::new(),
                        action: String::new(),
                        phase: Phase::Running,
                        accepted_at_ms: 0,
                        started_at_ms: None,
                    }
                }
            };
            let (outcome, error) = classify(&marker);
            tracing::warn!(
                attempt = %id, outcome = outcome.as_str_name(),
                "reporting an attempt interrupted by a restart"
            );
            let result = AttemptResult {
                attempt_id: id.clone(),
                outcome: outcome as i32,
                outputs_json: "{}".to_owned(),
                error,
                artifacts: Vec::new(),
            };
            outbox.put(&OutboxRecord::from_result(&result, now_ms()))?;
        }
    }
    outbox.list()
}

#[cfg(test)]
mod tests {
    use super::*;

    fn marker(executor: &str, action: &str, phase: Phase) -> Marker {
        Marker {
            attempt_id: "a".into(),
            executor: executor.into(),
            action: action.into(),
            phase,
            accepted_at_ms: 0,
            started_at_ms: None,
        }
    }

    #[test]
    fn classification() {
        use Outcome::*;
        let cases = [
            ("opentofu", "apply", Phase::Running, Uncertain),
            ("proxmox", "set-protection", Phase::Running, Uncertain),
            ("opentofu", "preview", Phase::Running, Failed),
            ("proxmox", "observe", Phase::Running, Failed),
            ("proxmox", "verify-guest", Phase::Running, Failed),
            ("opentofu", "apply", Phase::Queued, Failed),
            ("", "", Phase::Running, Failed),
        ];
        for (e, a, p, expected) in cases {
            assert_eq!(classify(&marker(e, a, p)).0, expected, "{e}/{a} {p:?}");
        }
        assert_eq!(
            classify(&marker("proxmox", "observe", Phase::Running)).1,
            "agent restarted during the attempt"
        );
    }

    #[test]
    fn recovers_lost_attempts() {
        let dir = tempfile::tempdir().unwrap();
        let work = dir.path().join("work");
        let outbox = Outbox::open(dir.path().join("outbox")).unwrap();

        let apply = workspace::create(&work, "apply-1", "opentofu", "apply").unwrap();
        workspace::mark_running(&apply).unwrap();
        let preview = workspace::create(&work, "preview-1", "opentofu", "preview").unwrap();
        workspace::mark_running(&preview).unwrap();
        workspace::create(&work, "queued-1", "proxmox", "set-protection").unwrap();
        fs::create_dir_all(work.join("nomarker-1")).unwrap();
        // Already reported: keep its result as it is.
        workspace::create(&work, "done-1", "proxmox", "observe").unwrap();
        let done = AttemptResult {
            attempt_id: "done-1".into(),
            outcome: Outcome::Succeeded as i32,
            outputs_json: "{\"version\":\"8\"}".into(),
            ..Default::default()
        };
        outbox.put(&OutboxRecord::from_result(&done, 1)).unwrap();

        let mut pending = recover(&work, &outbox).unwrap();
        pending.sort();
        assert_eq!(pending, ["apply-1", "done-1", "preview-1", "queued-1"]);

        let get = |id: &str| outbox.get(id).unwrap().unwrap().to_result().unwrap();
        assert_eq!(get("apply-1").outcome(), Outcome::Uncertain);
        assert_eq!(get("preview-1").outcome(), Outcome::Failed);
        assert_eq!(get("preview-1").error, "agent restarted during the attempt");
        assert_eq!(get("queued-1").outcome(), Outcome::Failed);
        assert_eq!(get("done-1"), done);
        assert!(!work.join("nomarker-1").exists());
        assert!(work.join("apply-1").exists(), "kept until acknowledged");

        // Running recovery again changes nothing.
        let mut again = recover(&work, &outbox).unwrap();
        again.sort();
        assert_eq!(again, pending);
    }
}
