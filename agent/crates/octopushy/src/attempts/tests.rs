//! Attempt manager tests with the built-in `test` executor.

use std::collections::HashMap;
use std::time::Duration;

use octopushy_proto::v1::{Assignment, LogStream};

use super::*;
use crate::tools::{RuntimeKind, ToolRuntime};

struct Harness {
    _dir: tempfile::TempDir,
    work: PathBuf,
    outbox_dir: PathBuf,
    manager: Arc<AttemptManager>,
}

fn harness() -> Harness {
    harness_runtime(RuntimeKind::Podman)
}

fn harness_runtime(kind: RuntimeKind) -> Harness {
    let dir = tempfile::tempdir().unwrap();
    let work = dir.path().join("work");
    let outbox_dir = dir.path().join("outbox");
    let exec = Arc::new(ExecutorConfig {
        runtime: ToolRuntime::new(kind, None, "false".into(), None),
        provider_cache: dir.path().join("cache"),
        poll_interval: Duration::from_millis(10),
        proxmox_timeout: Duration::from_secs(1),
        retry_delay: Duration::from_millis(1),
        server: Default::default(),
        nix: Default::default(),
    });
    let manager = AttemptManager::new(work.clone(), Outbox::open(&outbox_dir).unwrap(), exec);
    Harness {
        _dir: dir,
        work,
        outbox_dir,
        manager,
    }
}

fn assignment(id: &str, executor: &str, action: &str, input: &str) -> Assignment {
    Assignment {
        attempt_id: id.into(),
        executor: executor.into(),
        action: action.into(),
        input_json: input.into(),
        secrets: HashMap::from([("s".to_owned(), "topsecret".to_owned())]),
        ..Default::default()
    }
}

/// Collects everything a connection would send, remembering results.
#[derive(Default)]
struct Collector {
    cursor: SessionCursor,
    msgs: Vec<AgentMessage>,
}

impl Collector {
    fn poll(&mut self, m: &AttemptManager) -> Vec<AgentMessage> {
        let new = m.collect(&mut self.cursor);
        self.msgs.extend(new.iter().cloned());
        new
    }

    fn find(&self, id: &str) -> Option<AttemptResult> {
        self.msgs.iter().rev().find_map(|msg| match &msg.kind {
            Some(Kind::Result(r)) if r.attempt_id == id => Some(r.clone()),
            _ => None,
        })
    }

    /// Waits for a result of `id` that arrives after `already` results of it.
    async fn result_n(&mut self, m: &AttemptManager, id: &str, already: usize) -> AttemptResult {
        for _ in 0..500 {
            self.poll(m);
            let count = self
                .msgs
                .iter()
                .filter(|msg| matches!(&msg.kind, Some(Kind::Result(r)) if r.attempt_id == id))
                .count();
            if count > already {
                return self.find(id).unwrap();
            }
            tokio::time::sleep(Duration::from_millis(10)).await;
        }
        panic!("no result for {id}");
    }

    async fn result(&mut self, m: &AttemptManager, id: &str) -> AttemptResult {
        self.result_n(m, id, 0).await
    }
}

fn log_texts(msgs: &[AgentMessage], id: &str) -> Vec<String> {
    msgs.iter()
        .filter_map(|m| match &m.kind {
            Some(Kind::Log(l)) if l.attempt_id == id => {
                Some(l.lines.iter().map(|x| x.text.clone()).collect::<Vec<_>>())
            }
            _ => None,
        })
        .flatten()
        .collect()
}

#[tokio::test]
async fn runs_reports_resends_and_cleans_up_after_ack() {
    let h = harness();
    h.manager
        .accept(assignment("a1", "test", "echo", r#"{"hello":"world"}"#));
    let mut c = Collector::default();
    let result = c.result(&h.manager, "a1").await;
    let msgs = c.msgs.clone();
    assert_eq!(result.outcome(), Outcome::Succeeded);
    assert_eq!(result.outputs_json, r#"{"hello":"world"}"#);
    assert!(matches!(msgs[0].kind, Some(Kind::Started(_))), "{msgs:?}");
    let logs = log_texts(&msgs, "a1");
    assert!(
        logs.iter().any(|l| l.starts_with("starting test/echo")),
        "{logs:?}"
    );
    assert!(logs.iter().any(|l| l == r#"{"hello":"world"}"#));
    // Log sequence numbers start at 0 and are contiguous.
    let firsts: Vec<u64> = msgs
        .iter()
        .filter_map(|m| match &m.kind {
            Some(Kind::Log(l)) => Some(l.first_sequence),
            _ => None,
        })
        .collect();
    assert_eq!(firsts[0], 0);

    assert!(h.outbox_dir.join("a1.json").exists());
    assert!(h.work.join("a1/attempt.json").exists());
    // Nothing new for the same connection...
    assert!(c.poll(&h.manager).is_empty());
    // ...but a new connection gets the logs and the result again.
    let mut fresh = SessionCursor::default();
    let again = h.manager.collect(&mut fresh);
    assert!(
        again
            .iter()
            .any(|m| matches!(&m.kind, Some(Kind::Result(r)) if r.attempt_id == "a1"))
    );
    assert_eq!(log_texts(&again, "a1"), logs);

    h.manager.ack("a1");
    assert!(!h.outbox_dir.join("a1.json").exists());
    for _ in 0..100 {
        if !h.work.join("a1").exists() {
            break;
        }
        tokio::time::sleep(Duration::from_millis(10)).await;
    }
    assert!(!h.work.join("a1").exists());
    assert!(h.manager.collect(&mut SessionCursor::default()).is_empty());
    assert_eq!(h.manager.unacknowledged(), 0);
}

#[tokio::test]
async fn respects_concurrency() {
    let h = harness_runtime(RuntimeKind::Local);
    h.manager.set_concurrency(20);
    assert_eq!(h.manager.concurrency(), 1);
    h.manager
        .accept(assignment("s1", "test", "sleep", r#"{"ms":200}"#));
    h.manager
        .accept(assignment("s2", "test", "sleep", r#"{"ms":10}"#));
    tokio::time::sleep(Duration::from_millis(50)).await;
    assert_eq!(h.manager.active_ids(), ["s1", "s2"]);
    let mut c = Collector::default();
    let started: Vec<String> = c
        .poll(&h.manager)
        .into_iter()
        .filter_map(|m| match m.kind {
            Some(Kind::Started(s)) => Some(s.attempt_id),
            _ => None,
        })
        .collect();
    assert_eq!(started, ["s1"], "s2 waits for capacity");
    let r = c.result(&h.manager, "s2").await;
    assert_eq!(r.outcome(), Outcome::Succeeded);
    assert!(h.manager.is_idle());
}

#[tokio::test]
async fn cancels_reads_but_never_mutations() {
    let h = harness();
    h.manager.set_concurrency(2);
    h.manager
        .accept(assignment("read", "test", "sleep", r#"{"ms":60000}"#));
    h.manager
        .accept(assignment("mut", "test", "mutate", r#"{"ms":300}"#));
    h.manager
        .accept(assignment("queued", "test", "sleep", r#"{"ms":1}"#));
    tokio::time::sleep(Duration::from_millis(50)).await;

    h.manager.cancel("queued");
    h.manager.cancel("read");
    h.manager.cancel("mut");
    h.manager.cancel("unknown");

    let mut c = Collector::default();
    let r = c.result(&h.manager, "read").await;
    assert_eq!(r.outcome(), Outcome::Cancelled);
    let r = c.result(&h.manager, "queued").await;
    assert_eq!(r.outcome(), Outcome::Cancelled);
    assert_eq!(r.error, "cancelled before it started");
    let r = c.result(&h.manager, "mut").await;
    assert_eq!(
        r.outcome(),
        Outcome::Succeeded,
        "the mutation was not interrupted"
    );
    let logs = log_texts(&c.msgs, "mut");
    assert!(logs.iter().any(|l| l.contains("refused")), "{logs:?}");
}

#[tokio::test]
async fn failures_are_masked_and_unknown_executors_fail() {
    let h = harness();
    h.manager.accept(assignment("f1", "test", "fail", "{}"));
    h.manager.accept(assignment("u1", "salt", "run", "{}"));
    let mut c = Collector::default();
    let r = c.result(&h.manager, "f1").await;
    assert_eq!(r.outcome(), Outcome::Failed);
    assert_eq!(r.error, "failed with secret ***");
    assert_eq!(r.outputs_json, "{}");
    for text in log_texts(&c.msgs, "f1") {
        assert!(!text.contains("topsecret"), "{text}");
    }
    let stored = std::fs::read_to_string(h.outbox_dir.join("f1.json")).unwrap();
    assert!(!stored.contains("topsecret"));

    let r = c.result(&h.manager, "u1").await;
    assert_eq!(r.outcome(), Outcome::Failed);
    assert!(r.error.contains("unknown executor"), "{}", r.error);
}

#[tokio::test]
async fn duplicate_assignments_and_bad_ids() {
    let h = harness();
    h.manager.accept(assignment("d1", "test", "echo", "{}"));
    h.manager.accept(assignment("d1", "test", "echo", "{}"));
    let mut c = Collector::default();
    let r = c.result(&h.manager, "d1").await;
    assert_eq!(r.outcome(), Outcome::Succeeded);
    // The server sends it again (it lost the result): resend on this connection.
    h.manager.accept(assignment("d1", "test", "echo", "{}"));
    let again = c.result_n(&h.manager, "d1", 1).await;
    assert_eq!(again, r);

    h.manager
        .accept(assignment("../evil", "test", "echo", "{}"));
    let r = c.result(&h.manager, "../evil").await;
    assert_eq!(r.outcome(), Outcome::Failed);
    assert!(!h.work.parent().unwrap().join("evil").exists());
    h.manager.ack("../evil");
    assert_eq!(h.manager.unacknowledged(), 1);
}

#[tokio::test]
async fn drain_cancels_and_refuses_new_work() {
    let h = harness();
    h.manager.set_concurrency(4);
    h.manager
        .accept(assignment("long", "test", "sleep", r#"{"ms":60000}"#));
    tokio::time::sleep(Duration::from_millis(30)).await;
    h.manager.drain();
    h.manager.accept(assignment("late", "test", "echo", "{}"));
    let mut c = Collector::default();
    let r = c.result(&h.manager, "long").await;
    assert_eq!(r.outcome(), Outcome::Cancelled);
    let r = c.result(&h.manager, "late").await;
    let msgs = c.msgs.clone();
    assert_eq!(r.outcome(), Outcome::Failed);
    assert_eq!(r.error, "the agent is shutting down");
    let agent_lines = msgs.iter().any(|m| {
        matches!(&m.kind, Some(Kind::Log(l))
        if l.lines.iter().all(|x| x.stream == LogStream::Agent as i32))
    });
    assert!(agent_lines);
    assert!(h.manager.is_idle());
}

#[tokio::test]
async fn a_read_stopped_by_a_drain_is_cancelled_even_if_its_tool_cannot_tell() {
    let h = harness();
    h.manager.set_concurrency(4);
    // A playbook that only verifies: the server calls it a read, and a stopped playbook says
    // it cannot tell how far it got.
    let mut verify = assignment("verify", "test", "stopped", r#"{"ms":60000}"#);
    verify.mutating = Some(false);
    h.manager.accept(verify);
    // The same tool as a change is not stopped by a drain at all.
    let mut change = assignment("change", "test", "stopped", r#"{"ms":200}"#);
    change.mutating = Some(true);
    h.manager.accept(change);
    tokio::time::sleep(Duration::from_millis(30)).await;
    h.manager.drain();

    let mut c = Collector::default();
    assert_eq!(
        c.result(&h.manager, "verify").await.outcome(),
        Outcome::Cancelled
    );
    assert_eq!(
        c.result(&h.manager, "change").await.outcome(),
        Outcome::Succeeded
    );
}

#[tokio::test]
async fn a_lost_stored_result_of_a_recovered_attempt_is_uncertain_not_failed() {
    let h = harness();
    let ws = workspace::create(&h.work, "apply-1", "opentofu", "apply", true).unwrap();
    workspace::mark_running(&ws).unwrap();
    assert_eq!(h.manager.recover().unwrap(), 1);

    // The stored result becomes unreadable before it is sent: the agent no longer knows what the
    // attempt was, so it must not guess a failure for what may have been an apply.
    std::fs::write(h.outbox_dir.join("apply-1.json"), b"{ not json").unwrap();
    let mut c = Collector::default();
    c.poll(&h.manager);
    let result = c.find("apply-1").expect("a result is still reported");
    assert_eq!(result.outcome(), Outcome::Uncertain);
}

/// Writes a successful result of `id` to the outbox and makes it unreadable; returns whether a read now fails, which it
/// does not for root.
fn unreadable_success(h: &Harness, id: &str) -> bool {
    use std::os::unix::fs::PermissionsExt;
    let done = AttemptResult {
        attempt_id: id.into(),
        outcome: Outcome::Succeeded as i32,
        outputs_json: r#"{"done":true}"#.into(),
        ..Default::default()
    };
    Outbox::open(&h.outbox_dir)
        .unwrap()
        .put(&OutboxRecord::from_result(&done, 1))
        .unwrap();
    let path = h.outbox_dir.join(format!("{id}.json"));
    std::fs::set_permissions(&path, std::fs::Permissions::from_mode(0o000)).unwrap();
    std::fs::read(&path).is_err()
}

#[tokio::test]
async fn a_stored_result_that_cannot_be_read_for_a_moment_is_read_again_not_replaced() {
    use std::os::unix::fs::PermissionsExt;
    let h = harness();
    let ws = workspace::create(&h.work, "run-1", "ansible", "run", true).unwrap();
    workspace::mark_running(&ws).unwrap();
    assert_eq!(h.manager.recover().unwrap(), 1);
    unreadable_success(&h, "run-1");

    // Readable again well within the retries, as after a passing I/O failure.
    let path = h.outbox_dir.join("run-1.json");
    let restore = std::thread::spawn(move || {
        std::thread::sleep(Duration::from_millis(20));
        std::fs::set_permissions(&path, std::fs::Permissions::from_mode(0o600)).unwrap();
    });
    let mut c = Collector::default();
    c.poll(&h.manager);
    restore.join().unwrap();
    let result = c.find("run-1").expect("a result is reported");
    assert_eq!(result.outcome(), Outcome::Succeeded, "{}", result.error);
    assert_eq!(result.outputs_json, r#"{"done":true}"#);
}

#[tokio::test]
async fn a_stored_result_that_stays_unreadable_is_reported_uncertain_after_the_retries() {
    let h = harness();
    let ws = workspace::create(&h.work, "run-2", "ansible", "run", true).unwrap();
    workspace::mark_running(&ws).unwrap();
    assert_eq!(h.manager.recover().unwrap(), 1);
    if !unreadable_success(&h, "run-2") {
        // Root reads it whatever its mode: there is no failure to test.
        return;
    }
    let started = std::time::Instant::now();
    let mut c = Collector::default();
    c.poll(&h.manager);
    let result = c.find("run-2").expect("a result is still reported");
    assert_eq!(result.outcome(), Outcome::Uncertain);
    assert_eq!(result.error, "the agent lost the attempt's stored result");
    // It waited for the retries, and not much longer.
    assert!(
        started.elapsed() >= READ_BACKOFF * 15,
        "{:?}",
        started.elapsed()
    );
    assert!(started.elapsed() < Duration::from_secs(5));
}

#[tokio::test]
async fn an_apply_reports_its_state_even_when_it_fails() {
    use crate::executors::opentofu::{archive, state};

    let dir = tempfile::tempdir().unwrap();
    let tofu = dir.path().join("tofu");
    std::fs::write(
        &tofu,
        r#"#!/bin/sh
case "$1" in
  version) echo '{"terraform_version":"1.13.1"}' ;;
  apply) echo '{"version":4,"serial":5,"lineage":"l"}' > "$STATE_FILE"; exit 1 ;;
esac
"#,
    )
    .unwrap();
    std::fs::set_permissions(&tofu, std::os::unix::fs::PermissionsExt::from_mode(0o755)).unwrap();
    let exec = Arc::new(ExecutorConfig {
        runtime: ToolRuntime::new(RuntimeKind::Local, None, tofu, None),
        provider_cache: dir.path().join("cache"),
        poll_interval: Duration::from_secs(60),
        proxmox_timeout: Duration::from_secs(1),
        retry_delay: Duration::from_millis(1),
        server: Default::default(),
        nix: Default::default(),
    });
    std::fs::create_dir_all(dir.path().join("work")).unwrap();
    let work = dir.path().join("work").canonicalize().unwrap();
    let outbox = Outbox::open(dir.path().join("outbox")).unwrap();
    let manager = AttemptManager::new(work.clone(), outbox, exec);

    let plan = archive::pack(&[("plan.bin", b"PLAN".as_slice())]).unwrap();
    let input = serde_json::json!({
        "root": ".",
        "env": { "STATE_FILE": work.join("a1/state/terraform.tfstate") },
    });
    let mut apply = assignment("a1", "opentofu", "apply", &input.to_string());
    apply.files_tar_gz = archive::pack(&[("main.tf", b"".as_slice())]).unwrap();
    apply.artifacts = vec![octopushy_proto::v1::Artifact {
        name: "plan.tar.gz".into(),
        sha256: crate::tls::sha256_hex(&plan),
        content: plan,
    }];
    manager.accept(apply);

    let mut collector = Collector::default();
    let result = loop {
        collector.poll(&manager);
        if let Some(result) = collector.msgs.iter().find_map(|m| match &m.kind {
            Some(Kind::Result(r)) => Some(r.clone()),
            _ => None,
        }) {
            break result;
        }
        tokio::time::sleep(Duration::from_millis(20)).await;
    };
    assert_eq!(result.outcome(), Outcome::Failed);
    let saved = result
        .artifacts
        .iter()
        .find(|a| a.name == state::ARTIFACT)
        .unwrap();
    assert!(String::from_utf8_lossy(&saved.content).contains("\"serial\":5"));
}

#[tokio::test]
async fn an_attempt_that_never_ran_does_not_claim_to_have_started() {
    let h = harness();
    let queued = workspace::create(&h.work, "queued-1", "opentofu", "apply", true).unwrap();
    let running = workspace::create(&h.work, "running-1", "opentofu", "apply", true).unwrap();
    workspace::mark_running(&running).unwrap();
    assert!(queued.exists());
    h.manager.recover().unwrap();

    let mut collector = Collector::default();
    let started: Vec<String> = collector
        .poll(&h.manager)
        .into_iter()
        .filter_map(|m| match m.kind {
            Some(Kind::Started(s)) => Some(s.attempt_id),
            _ => None,
        })
        .collect();
    assert_eq!(started, ["running-1"]);
}

#[tokio::test]
async fn oversized_result_keeps_state_and_does_not_block_later_results() {
    use octopushy_proto::v1::Artifact;
    let h = harness();
    h.manager
        .accept(assignment("large", "test", "mutate", r#"{"ms":0}"#));
    let mut c = Collector::default();
    c.result(&h.manager, "large").await;
    let mut large = result("large", Outcome::Succeeded, "");
    large.outputs_json = "x".repeat(MAX_RESULT_BYTES);
    large.artifacts = vec![
        Artifact {
            name: state::ARTIFACT.into(),
            content: b"state".to_vec(),
            ..Default::default()
        },
        Artifact {
            name: "plan.tar.gz".into(),
            content: vec![1; 10],
            ..Default::default()
        },
    ];
    h.manager.finish("large", large);
    let stored = h
        .manager
        .outbox
        .get("large")
        .unwrap()
        .unwrap()
        .to_result()
        .unwrap();
    assert_eq!(stored.outcome(), Outcome::Uncertain);
    assert!(stored.error.contains("result too large"));
    assert_eq!(stored.artifacts.len(), 1);
    assert_eq!(stored.artifacts[0].content, b"state");
    assert!(stored.encoded_len() < crate::connection::MAX_MESSAGE_BYTES);
    h.manager.accept(assignment("later", "test", "echo", "{}"));
    let mut reconnected = Collector::default();
    assert_eq!(
        reconnected.result(&h.manager, "later").await.outcome(),
        Outcome::Succeeded
    );
    assert!(reconnected.find("large").is_some());
}

#[test]
fn oversized_state_is_dropped_and_reads_fail() {
    let mut r = result("read", Outcome::Succeeded, "");
    r.artifacts.push(octopushy_proto::v1::Artifact {
        name: state::ARTIFACT.into(),
        content: vec![0; MAX_RESULT_BYTES],
        ..Default::default()
    });
    bound_result(&mut r, false);
    assert_eq!(r.outcome(), Outcome::Failed);
    assert!(r.artifacts.is_empty());
    assert!(r.encoded_len() < MAX_RESULT_BYTES);
}

#[tokio::test]
async fn forced_mutations_remain_uncertain_even_if_the_executor_returns_success() {
    let h = harness();
    h.manager
        .accept(assignment("forced", "test", "mutate", r#"{"ms":100}"#));
    let mut c = Collector::default();
    for _ in 0..100 {
        c.poll(&h.manager);
        if c.msgs
            .iter()
            .any(|m| matches!(&m.kind, Some(Kind::Started(s)) if s.attempt_id == "forced"))
        {
            break;
        }
        tokio::time::sleep(Duration::from_millis(1)).await;
    }
    h.manager.cancel_requested("forced", true);
    h.manager.accept(assignment("after", "test", "echo", "{}"));
    assert_eq!(
        c.result(&h.manager, "forced").await.outcome(),
        Outcome::Uncertain
    );
    assert_eq!(
        c.result(&h.manager, "after").await.outcome(),
        Outcome::Succeeded
    );
    assert!(h.manager.is_idle());
}

/// Waits until `path` exists.
async fn wait_for(path: &std::path::Path) {
    for _ in 0..1000 {
        if path.exists() {
            return;
        }
        tokio::time::sleep(Duration::from_millis(10)).await;
    }
    panic!("{} never appeared", path.display());
}

/// What a restart now would report for `id`: recovery into an empty outbox.
fn recovered_now(work: &std::path::Path, id: &str) -> AttemptResult {
    let dir = tempfile::tempdir().unwrap();
    let outbox = Outbox::open(dir.path()).unwrap();
    recovery::recover(work, &outbox).unwrap();
    outbox.get(id).unwrap().unwrap().to_result().unwrap()
}

#[tokio::test]
async fn a_restart_during_init_failed_and_one_during_the_apply_is_uncertain() {
    use crate::executors::opentofu::archive;

    let dir = tempfile::tempdir().unwrap();
    let gates = dir.path().join("gates");
    std::fs::create_dir_all(&gates).unwrap();
    let tofu = dir.path().join("tofu");
    // `init` and `apply` say they started, then wait to be let go.
    std::fs::write(
        &tofu,
        r#"#!/bin/sh
case "$1" in
  version) echo '{"terraform_version":"1.13.1"}' ;;
  init|apply)
    touch "$GATES/$1"
    while [ ! -e "$GATES/go-$1" ]; do sleep 0.02; done
    [ "$1" = apply ] && echo '{"version":4,"serial":5,"lineage":"l"}' > "$STATE_FILE"
    ;;
esac
exit 0
"#,
    )
    .unwrap();
    std::fs::set_permissions(&tofu, std::os::unix::fs::PermissionsExt::from_mode(0o755)).unwrap();
    let exec = Arc::new(ExecutorConfig {
        runtime: ToolRuntime::new(RuntimeKind::Local, None, tofu, None),
        provider_cache: dir.path().join("cache"),
        poll_interval: Duration::from_secs(60),
        proxmox_timeout: Duration::from_secs(1),
        retry_delay: Duration::from_millis(1),
        server: Default::default(),
        nix: Default::default(),
    });
    std::fs::create_dir_all(dir.path().join("work")).unwrap();
    let work = dir.path().join("work").canonicalize().unwrap();
    let manager = AttemptManager::new(
        work.clone(),
        Outbox::open(dir.path().join("outbox")).unwrap(),
        exec,
    );

    let plan = archive::pack(&[("plan.bin", b"PLAN".as_slice())]).unwrap();
    let input = serde_json::json!({
        "root": ".",
        "env": { "STATE_FILE": work.join("a1/state/terraform.tfstate"), "GATES": gates },
    });
    let mut apply = assignment("a1", "opentofu", "apply", &input.to_string());
    apply.mutating = Some(true);
    apply.files_tar_gz = archive::pack(&[("main.tf", b"".as_slice())]).unwrap();
    apply.artifacts = vec![octopushy_proto::v1::Artifact {
        name: "plan.tar.gz".into(),
        sha256: crate::tls::sha256_hex(&plan),
        content: plan,
    }];
    manager.accept(apply);

    wait_for(&gates.join("init")).await;
    let during_init = recovered_now(&work, "a1");
    assert_eq!(
        during_init.outcome(),
        Outcome::Failed,
        "{}",
        during_init.error
    );
    assert!(
        during_init.error.contains("nothing ran"),
        "{}",
        during_init.error
    );

    std::fs::write(gates.join("go-init"), b"").unwrap();
    wait_for(&gates.join("apply")).await;
    let during_apply = recovered_now(&work, "a1");
    assert_eq!(
        during_apply.outcome(),
        Outcome::Uncertain,
        "{}",
        during_apply.error
    );

    std::fs::write(gates.join("go-apply"), b"").unwrap();
    let mut c = Collector::default();
    let done = c.result(&manager, "a1").await;
    assert_eq!(done.outcome(), Outcome::Succeeded, "{}", done.error);
}

#[tokio::test]
async fn an_attempt_whose_change_marker_cannot_be_written_changes_nothing() {
    let h = harness();
    h.manager.accept(assignment(
        "c1",
        "test",
        "change",
        r#"{"before":200,"ms":0}"#,
    ));
    let ws = workspace::dir(&h.work, "c1");
    for _ in 0..100 {
        if matches!(workspace::read(&ws), Ok(Some(m)) if m.phase == workspace::Phase::Running) {
            break;
        }
        tokio::time::sleep(Duration::from_millis(5)).await;
    }
    // The marker is gone before the change would start: it must not start.
    std::fs::remove_file(ws.join(workspace::MARKER)).unwrap();
    let mut c = Collector::default();
    let r = c.result(&h.manager, "c1").await;
    assert_eq!(r.outcome(), Outcome::Failed);
    assert!(r.error.contains("not started"), "{}", r.error);
}

#[test]
fn the_server_cannot_call_an_apply_or_a_protection_write_a_read() {
    for (executor, action, expected) in [
        ("opentofu", "apply", true),
        ("proxmox", "set-protection", true),
        // A playbook that only verifies is a read, so the server's word stands.
        ("ansible", "run", false),
        ("opentofu", "preview", false),
    ] {
        let mut a = assignment("a", executor, action, "{}");
        a.mutating = Some(false);
        assert_eq!(effective_mutating(&a), expected, "{executor}/{action}");
    }
}

#[tokio::test]
async fn the_servers_flag_decides_whether_an_attempt_is_a_change() {
    let h = harness();
    let mut c = Collector::default();
    let started = |c: &Collector, id: &str| {
        c.msgs
            .iter()
            .any(|m| matches!(&m.kind, Some(Kind::Started(s)) if s.attempt_id == id))
    };

    // The agent calls `test/sleep` a read; the server says it is a change.
    let mut change = assignment("change", "test", "sleep", r#"{"ms":60000}"#);
    change.mutating = Some(true);
    h.manager.accept(change);
    // The agent calls `test/mutate` a change; the server says it is a read.
    let mut read = assignment("read", "test", "mutate", r#"{"ms":0}"#);
    read.mutating = Some(false);
    h.manager.accept(read);
    // No flag, from an older server: the agent's table.
    h.manager
        .accept(assignment("old", "test", "mutate", r#"{"ms":0}"#));
    let marker = |id: &str| {
        workspace::read(&workspace::dir(&h.work, id))
            .unwrap()
            .unwrap()
            .mutating
    };
    assert_eq!(marker("change"), Some(true));
    assert_eq!(marker("read"), Some(false));
    assert_eq!(marker("old"), Some(true));

    for _ in 0..200 {
        c.poll(&h.manager);
        if started(&c, "change") {
            break;
        }
        tokio::time::sleep(Duration::from_millis(5)).await;
    }
    // A normal cancel never interrupts a change; Force stop does, and leaves it uncertain.
    h.manager.cancel("change");
    tokio::time::sleep(Duration::from_millis(50)).await;
    c.poll(&h.manager);
    assert!(
        c.find("change").is_none(),
        "a normal cancel stopped a change"
    );
    h.manager.cancel_requested("change", true);
    assert_eq!(
        c.result(&h.manager, "change").await.outcome(),
        Outcome::Uncertain
    );
}
