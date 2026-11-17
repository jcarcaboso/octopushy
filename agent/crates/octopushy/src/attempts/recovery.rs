//! Restart recovery: turns workspaces the previous process left behind into
//! results.
//!
//! - A workspace with an outbox result is waiting for its acknowledgement.
//! - A `queued` marker without a result never started: failed.
//! - A `running` marker without a result died mid-attempt. For an attempt that
//!   changes nothing it failed. For a change it is uncertain, unless the marker
//!   says its executor records the start of its change and it had not: then
//!   nothing ran, as when the restart came during `tofu init` or an image pull,
//!   and it failed. Whether it is a change is the server's word, kept in the
//!   marker; a marker of an older agent falls back to the agent's own table.
//! - A workspace without a marker never got past setup and is removed.
//! - A marker that exists but cannot be read says nothing about what the
//!   attempt was doing: uncertain, never failed.
//!
//! An interrupted `opentofu/apply` also reports the built-in state it left in
//! its workspace, which records whatever it finished.

use std::fs;
use std::path::Path;

use anyhow::Result;
use octopushy_proto::v1::{AttemptResult, Outcome};

use super::workspace::{self, Marker, Phase};
use crate::executors::{self, ansible, opentofu::state};
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
        Phase::Running if !mutating(marker) => (
            Outcome::Failed,
            "agent restarted during the attempt".to_owned(),
        ),
        // Only a marker that says the executor records the start of its change, and that it
        // had not, shows nothing changed. Anything else cannot tell.
        Phase::Running if marker.marks_change && marker.change_started_at_ms.is_none() => (
            Outcome::Failed,
            format!(
                "the agent restarted during {}/{} before its change started; nothing ran",
                marker.executor, marker.action
            ),
        ),
        Phase::Running => (
            Outcome::Uncertain,
            format!(
                "the agent restarted during {}/{}; it cannot tell whether the change was made",
                marker.executor, marker.action
            ),
        ),
    }
}

/// Whether the lost attempt may have been a change: the server's flag the marker kept, or for
/// a marker without one the agent's own table.
fn mutating(marker: &Marker) -> bool {
    marker
        .mutating
        .unwrap_or_else(|| executors::is_mutating(&marker.executor, &marker.action))
}

/// What to report when the marker cannot be read: without the executor and
/// action the agent cannot rule out that a change was made.
fn unreadable_marker() -> (Outcome, String) {
    (
        Outcome::Uncertain,
        "the agent restarted and could not read this attempt's record; it cannot tell whether a change was made"
            .to_owned(),
    )
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
            ansible::remove_secret_dir(&id);
            let mut applying = false;
            let (outcome, error) = match workspace::read(&ws) {
                Ok(Some(marker)) => {
                    applying = marker.phase == Phase::Running
                        && (marker.executor.as_str(), marker.action.as_str())
                            == ("opentofu", "apply");
                    classify(&marker)
                }
                Ok(None) => {
                    tracing::info!("removing incomplete workspace {}", ws.display());
                    workspace::remove(&ws);
                    continue;
                }
                Err(e) => {
                    tracing::warn!("unreadable marker in {}: {e:#}", ws.display());
                    unreadable_marker()
                }
            };
            tracing::warn!(
                attempt = %id, outcome = outcome.as_str_name(),
                "reporting an attempt interrupted by a restart"
            );
            let result = AttemptResult {
                attempt_id: id.clone(),
                outcome: outcome as i32,
                outputs_json: "{}".to_owned(),
                error,
                artifacts: if applying {
                    state::read(&ws).into_iter().collect()
                } else {
                    Vec::new()
                },
            };
            outbox.put(&OutboxRecord::from_result(&result, now_ms()))?;
        }
    }
    outbox.list()
}

#[cfg(test)]
mod tests {
    use super::*;

    const STATE: &[u8] = br#"{"version":4,"serial":3,"lineage":"l"}"#;

    /// A marker as an agent before the change marker wrote it: no `mutating`, no `marksChange`.
    fn marker(executor: &str, action: &str, phase: Phase) -> Marker {
        Marker {
            attempt_id: "a".into(),
            executor: executor.into(),
            action: action.into(),
            phase,
            accepted_at_ms: 0,
            started_at_ms: None,
            mutating: None,
            marks_change: false,
            change_started_at_ms: None,
        }
    }

    /// A running marker as this agent writes it, before or after the change started.
    fn running(executor: &str, action: &str, mutating: bool, changed: bool) -> Marker {
        Marker {
            mutating: Some(mutating),
            marks_change: executors::marks_change(executor, action),
            change_started_at_ms: changed.then_some(1),
            ..marker(executor, action, Phase::Running)
        }
    }

    #[test]
    fn classification_of_markers_without_the_change_marker() {
        use Outcome::*;
        let cases = [
            ("opentofu", "apply", Phase::Running, Uncertain),
            ("proxmox", "set-protection", Phase::Running, Uncertain),
            ("ansible", "run", Phase::Running, Uncertain),
            ("ansible", "run", Phase::Queued, Failed),
            ("ansible", "preview", Phase::Running, Failed),
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
    fn a_change_that_had_not_started_failed_and_one_that_had_is_uncertain() {
        use Outcome::*;
        for (e, a) in [
            ("opentofu", "apply"),
            ("ansible", "run"),
            ("proxmox", "set-protection"),
        ] {
            let (outcome, error) = classify(&running(e, a, true, false));
            assert_eq!(outcome, Failed, "{e}/{a} before its change");
            assert!(error.contains("nothing ran"), "{error}");
            assert_eq!(
                classify(&running(e, a, true, true)).0,
                Uncertain,
                "{e}/{a} after its change started"
            );
        }
    }

    #[test]
    fn a_change_whose_executor_does_not_mark_its_start_stays_uncertain() {
        // The server says it is a change; the agent does not record when such a change starts.
        for (e, a) in [
            ("nix", "build"),
            ("proxmox", "observe"),
            ("opentofu", "preview"),
            ("future", "thing"),
            ("test", "mutate"),
        ] {
            assert!(!executors::marks_change(e, a));
            assert_eq!(
                classify(&running(e, a, true, false)).0,
                Outcome::Uncertain,
                "{e}/{a}"
            );
        }
        // Nor does a marker that does not say its executor marks the start, whatever the action.
        let mut old = marker("opentofu", "apply", Phase::Running);
        old.mutating = Some(true);
        assert_eq!(classify(&old).0, Outcome::Uncertain);
    }

    #[test]
    fn a_playbook_the_server_calls_a_read_is_never_uncertain() {
        for changed in [false, true] {
            assert_eq!(
                classify(&running("ansible", "run", false, changed)).0,
                Outcome::Failed,
                "changed={changed}"
            );
        }
        // The server's word also makes a change of what the agent would call a read.
        assert_eq!(
            classify(&running("proxmox", "verify-guest", true, false)).0,
            Outcome::Uncertain
        );
    }

    #[test]
    fn recovers_lost_attempts() {
        let dir = tempfile::tempdir().unwrap();
        let work = dir.path().join("work");
        let outbox = Outbox::open(dir.path().join("outbox")).unwrap();

        let apply = workspace::create(&work, "apply-1", "opentofu", "apply", true).unwrap();
        workspace::mark_running(&apply).unwrap();
        workspace::mark_change_started(&apply).unwrap();
        // Restarted during `tofu init`: the apply had not started.
        let init = workspace::create(&work, "init-1", "opentofu", "apply", true).unwrap();
        workspace::mark_running(&init).unwrap();
        fs::create_dir_all(init.join("state")).unwrap();
        fs::write(init.join("state/terraform.tfstate"), STATE).unwrap();
        // A playbook that only verifies, restarted after it reached the guest.
        let verify = workspace::create(&work, "verify-1", "ansible", "run", false).unwrap();
        workspace::mark_running(&verify).unwrap();
        workspace::mark_change_started(&verify).unwrap();
        // Written by an agent before the change marker, during an apply.
        let old = workspace::create(&work, "old-1", "opentofu", "apply", true).unwrap();
        fs::write(
            old.join(workspace::MARKER),
            br#"{"attemptId":"old-1","executor":"opentofu","action":"apply","phase":"running","acceptedAtMs":1,"startedAtMs":2}"#,
        )
        .unwrap();
        let preview = workspace::create(&work, "preview-1", "opentofu", "preview", false).unwrap();
        workspace::mark_running(&preview).unwrap();
        workspace::create(&work, "queued-1", "proxmox", "set-protection", true).unwrap();
        fs::create_dir_all(preview.join("state")).unwrap();
        fs::write(preview.join("state/terraform.tfstate"), STATE).unwrap();
        fs::create_dir_all(apply.join("state")).unwrap();
        fs::write(apply.join("state/terraform.tfstate"), STATE).unwrap();
        fs::create_dir_all(work.join("nomarker-1")).unwrap();
        let torn = workspace::create(&work, "torn-1", "opentofu", "apply", true).unwrap();
        fs::write(
            torn.join(workspace::MARKER),
            b"{\"attemptId\": \"torn-1\", \"exec",
        )
        .unwrap();
        // Already reported: keep its result as it is.
        workspace::create(&work, "done-1", "proxmox", "observe", false).unwrap();
        let done = AttemptResult {
            attempt_id: "done-1".into(),
            outcome: Outcome::Succeeded as i32,
            outputs_json: "{\"version\":\"8\"}".into(),
            ..Default::default()
        };
        outbox.put(&OutboxRecord::from_result(&done, 1)).unwrap();

        let mut pending = recover(&work, &outbox).unwrap();
        pending.sort();
        assert_eq!(
            pending,
            [
                "apply-1",
                "done-1",
                "init-1",
                "old-1",
                "preview-1",
                "queued-1",
                "torn-1",
                "verify-1"
            ]
        );

        let get = |id: &str| outbox.get(id).unwrap().unwrap().to_result().unwrap();
        assert_eq!(get("apply-1").outcome(), Outcome::Uncertain);
        assert_eq!(get("apply-1").artifacts.len(), 1);
        assert_eq!(get("apply-1").artifacts[0].content, STATE);
        assert_eq!(get("init-1").outcome(), Outcome::Failed);
        assert!(get("init-1").error.contains("nothing ran"));
        assert_eq!(
            get("init-1").artifacts.len(),
            1,
            "an apply still reports its state"
        );
        assert_eq!(get("verify-1").outcome(), Outcome::Failed);
        assert_eq!(get("old-1").outcome(), Outcome::Uncertain);
        assert!(get("preview-1").artifacts.is_empty());
        assert_eq!(get("preview-1").outcome(), Outcome::Failed);
        assert_eq!(get("preview-1").error, "agent restarted during the attempt");
        assert_eq!(get("queued-1").outcome(), Outcome::Failed);
        assert_eq!(
            get("torn-1").outcome(),
            Outcome::Uncertain,
            "an unreadable marker is never a known failure"
        );
        assert!(get("torn-1").error.contains("could not read"));
        assert_eq!(get("done-1"), done);
        assert!(!work.join("nomarker-1").exists());
        assert!(work.join("apply-1").exists(), "kept until acknowledged");

        // Running recovery again changes nothing, not even the stored results.
        let stored = |id: &str| serde_json::to_vec(&outbox.get(id).unwrap().unwrap()).unwrap();
        let before: Vec<_> = pending.iter().map(|id| stored(id)).collect();
        let mut again = recover(&work, &outbox).unwrap();
        again.sort();
        assert_eq!(again, pending);
        assert_eq!(
            before,
            pending.iter().map(|id| stored(id)).collect::<Vec<_>>()
        );
    }
}
