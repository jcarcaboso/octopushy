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
    let dir = tempfile::tempdir().unwrap();
    let work = dir.path().join("work");
    let outbox_dir = dir.path().join("outbox");
    let exec = Arc::new(ExecutorConfig {
        runtime: ToolRuntime::new(RuntimeKind::Local, None, "false".into(), None),
        provider_cache: dir.path().join("cache"),
        poll_interval: Duration::from_millis(10),
        proxmox_timeout: Duration::from_secs(1),
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
    let h = harness();
    h.manager.set_concurrency(1);
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
    h.manager.accept(assignment("u1", "ansible", "run", "{}"));
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
async fn a_lost_stored_result_of_a_recovered_attempt_is_uncertain_not_failed() {
    let h = harness();
    let ws = workspace::create(&h.work, "apply-1", "opentofu", "apply").unwrap();
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
