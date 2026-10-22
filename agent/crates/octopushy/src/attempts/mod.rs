//! Attempt bookkeeping: accepting assignments, running them concurrently up to
//! the server's limit, cancelling, and handing logs and results to whichever
//! connection is current.
//!
//! Attempts are independent of connections: they keep running while the agent
//! reconnects. Each connection keeps a [`SessionCursor`] of what it has sent, so
//! a new connection starts from scratch and resends retained log lines and every
//! unacknowledged result.

pub mod limiter;
pub mod logs;
pub mod recovery;
pub mod workspace;

use std::collections::{BTreeMap, HashMap, HashSet};
use std::panic::AssertUnwindSafe;
use std::path::PathBuf;
use std::sync::atomic::{AtomicBool, AtomicU64, Ordering};
use std::sync::{Arc, Mutex};
use std::time::Instant;

use anyhow::Result;
use futures::FutureExt;
use octopushy_proto::v1::{
    AgentMessage, Assignment, AttemptLog, AttemptResult, AttemptStarted, Outcome,
    agent_message::Kind,
};
use tokio::sync::Notify;
use tokio_util::sync::CancellationToken;

use crate::executors::{self, ExecContext, ExecutorConfig, Failure, Secrets};
use crate::identity::now_ms;
use crate::masking::Masker;
use crate::outbox::{Outbox, OutboxRecord};
use crate::paths::is_safe_id;
use limiter::Limiter;
use logs::{AttemptLogger, LogBuffer, MAX_BATCH_LINES};

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
enum State {
    Queued,
    Running,
    /// The result is in the outbox, waiting for `ResultAck`.
    Finished,
}

struct Entry {
    order: u64,
    executor: String,
    action: String,
    state: State,
    mutating: bool,
    cancel: CancellationToken,
    logs: Arc<LogBuffer>,
    logger: AttemptLogger,
    /// The server sent the assignment again; resend the result even if this
    /// connection already sent it.
    resend_result: bool,
    /// Kept in memory only when it could not be written to the outbox.
    memory_result: Option<AttemptResult>,
    /// Whether the attempt has an outbox file and workspace to clean up.
    on_disk: bool,
}

/// What one connection has already sent.
#[derive(Debug, Default)]
pub struct SessionCursor {
    log_next: HashMap<String, u64>,
    started_sent: HashSet<String>,
    results_sent: HashSet<String>,
}

/// Runs and tracks attempts.
pub struct AttemptManager {
    entries: Mutex<BTreeMap<String, Entry>>,
    next_order: AtomicU64,
    limiter: Arc<Limiter>,
    outbox: Outbox,
    work_dir: PathBuf,
    exec: Arc<ExecutorConfig>,
    wake: Arc<Notify>,
    draining: AtomicBool,
}

impl AttemptManager {
    pub fn new(work_dir: PathBuf, outbox: Outbox, exec: Arc<ExecutorConfig>) -> Arc<Self> {
        Arc::new(Self {
            entries: Mutex::new(BTreeMap::new()),
            next_order: AtomicU64::new(0),
            limiter: Limiter::new(1),
            outbox,
            work_dir,
            exec,
            wake: Arc::new(Notify::new()),
            draining: AtomicBool::new(false),
        })
    }

    /// Reports attempts the previous process left behind and queues every
    /// unacknowledged result for sending.
    pub fn recover(&self) -> Result<usize> {
        let ids = recovery::recover(&self.work_dir, &self.outbox)?;
        let count = ids.len();
        let mut entries = self.entries.lock().unwrap();
        for id in ids {
            let logs = Arc::new(LogBuffer::default());
            let logger =
                AttemptLogger::new(logs.clone(), Arc::new(Masker::default()), self.wake.clone());
            entries.insert(
                id,
                Entry {
                    order: self.next_order.fetch_add(1, Ordering::Relaxed),
                    executor: String::new(),
                    action: String::new(),
                    state: State::Finished,
                    // The executor and action are unknown after a restart. If the stored result
                    // is later unreadable, that must read as "cannot tell", never as a failure.
                    mutating: true,
                    cancel: CancellationToken::new(),
                    logs,
                    logger,
                    resend_result: false,
                    memory_result: None,
                    on_disk: true,
                },
            );
        }
        Ok(count)
    }

    /// Signalled when there is something new to send.
    pub fn wake_handle(&self) -> Arc<Notify> {
        self.wake.clone()
    }

    pub fn set_concurrency(&self, max: u32) {
        self.limiter.set_limit(max as usize);
    }

    pub fn concurrency(&self) -> usize {
        self.limiter.limit()
    }

    /// Attempts accepted and not finished, for `Hello` and `Heartbeat`.
    pub fn active_ids(&self) -> Vec<String> {
        let entries = self.entries.lock().unwrap();
        let mut active: Vec<(&u64, &String)> = entries
            .iter()
            .filter(|(_, e)| e.state != State::Finished)
            .map(|(id, e)| (&e.order, id))
            .collect();
        active.sort();
        active.into_iter().map(|(_, id)| id.clone()).collect()
    }

    /// Whether no attempt is queued or running.
    pub fn is_idle(&self) -> bool {
        self.entries
            .lock()
            .unwrap()
            .values()
            .all(|e| e.state == State::Finished)
    }

    /// Results waiting for acknowledgement.
    pub fn unacknowledged(&self) -> usize {
        self.entries
            .lock()
            .unwrap()
            .values()
            .filter(|e| e.state == State::Finished)
            .count()
    }

    /// Accepts an assignment and starts it in the background.
    pub fn accept(self: &Arc<Self>, assignment: Assignment) {
        let id = assignment.attempt_id.clone();
        let masker = Arc::new(Masker::new(assignment.secrets.values().map(String::as_str)));
        let logs = Arc::new(LogBuffer::default());
        let logger = AttemptLogger::new(logs.clone(), masker, self.wake.clone());
        let cancel = CancellationToken::new();
        let safe = is_safe_id(&id);
        {
            let mut entries = self.entries.lock().unwrap();
            if let Some(existing) = entries.get_mut(&id) {
                if existing.state == State::Finished {
                    tracing::info!(attempt = %id, "assignment received again; resending its result");
                    existing.resend_result = true;
                    self.wake.notify_one();
                } else {
                    tracing::info!(attempt = %id, "ignoring a duplicate assignment");
                }
                return;
            }
            entries.insert(
                id.clone(),
                Entry {
                    order: self.next_order.fetch_add(1, Ordering::Relaxed),
                    executor: assignment.executor.clone(),
                    action: assignment.action.clone(),
                    state: State::Queued,
                    mutating: executors::is_mutating(&assignment.executor, &assignment.action),
                    cancel: cancel.clone(),
                    logs,
                    logger: logger.clone(),
                    resend_result: false,
                    memory_result: None,
                    on_disk: safe,
                },
            );
        }
        tracing::info!(
            attempt = %id, executor = %assignment.executor, action = %assignment.action,
            "accepted assignment"
        );

        if !safe {
            self.finish(
                &id,
                failed(&id, "the attempt ID is not usable as a file name"),
            );
            return;
        }
        if self.draining.load(Ordering::SeqCst) {
            logger.agent("the agent is shutting down; not starting this attempt");
            self.finish(&id, failed(&id, "the agent is shutting down"));
            return;
        }
        let ws = match workspace::create(
            &self.work_dir,
            &id,
            &assignment.executor,
            &assignment.action,
        ) {
            Ok(ws) => ws,
            Err(e) => {
                let msg = format!("could not create the attempt workspace: {e:#}");
                self.finish(&id, failed(&id, &logger.mask(&msg)));
                return;
            }
        };
        let manager = self.clone();
        tokio::spawn(async move {
            manager.run_attempt(assignment, ws, logger, cancel).await;
        });
    }

    async fn run_attempt(
        self: Arc<Self>,
        assignment: Assignment,
        ws: PathBuf,
        log: AttemptLogger,
        cancel: CancellationToken,
    ) {
        let id = assignment.attempt_id.clone();
        let permit = tokio::select! {
            biased;
            _ = cancel.cancelled() => {
                log.agent("cancelled before it started");
                self.finish(&id, result(&id, Outcome::Cancelled, "cancelled before it started"));
                return;
            }
            p = self.limiter.acquire() => p,
        };
        if let Err(e) = workspace::mark_running(&ws) {
            self.finish(
                &id,
                failed(&id, &format!("could not update the attempt marker: {e:#}")),
            );
            return;
        }
        let mutating = {
            let mut entries = self.entries.lock().unwrap();
            let entry = entries
                .get_mut(&id)
                .expect("attempt entry exists while running");
            entry.state = State::Running;
            entry.mutating
        };
        self.wake.notify_one();

        let executor = assignment.executor.clone();
        let action = assignment.action.clone();
        log.agent(format!(
            "starting {executor}/{action} (octopushy {})",
            crate::VERSION
        ));
        let ctx = ExecContext {
            attempt_id: id.clone(),
            action: assignment.action,
            input_json: assignment.input_json,
            secrets: Secrets::new(assignment.secrets),
            files_tar_gz: assignment.files_tar_gz,
            artifacts: assignment.artifacts,
            workspace: ws,
            log: log.clone(),
            cancel: cancel.clone(),
        };
        let started = Instant::now();
        let run = executors::run(&self.exec, &executor, &ctx);
        let outcome = AssertUnwindSafe(run).catch_unwind().await;
        let mut res = match outcome {
            Ok(Ok(success)) => AttemptResult {
                attempt_id: id.clone(),
                outcome: Outcome::Succeeded as i32,
                outputs_json: success.outputs.to_string(),
                error: String::new(),
                artifacts: success.artifacts,
            },
            Ok(Err(Failure::Uncertain(message))) => result(&id, Outcome::Uncertain, &message),
            Ok(Err(Failure::Cancelled)) => {
                result(&id, Outcome::Cancelled, "cancelled by the server")
            }
            Ok(Err(Failure::Failed { .. })) if cancel.is_cancelled() && !mutating => {
                result(&id, Outcome::Cancelled, "cancelled by the server")
            }
            Ok(Err(Failure::Failed { message, outputs })) => AttemptResult {
                attempt_id: id.clone(),
                outcome: Outcome::Failed as i32,
                outputs_json: outputs
                    .map(|o| o.to_string())
                    .unwrap_or_else(|| "{}".into()),
                error: message,
                artifacts: Vec::new(),
            },
            Err(_) if mutating => result(&id, Outcome::Uncertain, "the mutating executor panicked; inspect the target before retrying"),
            Err(_) => failed(&id, "internal error: the executor panicked"),
        };
        res.error = log.mask(&res.error);
        let secs = started.elapsed().as_secs_f64();
        match res.outcome() {
            Outcome::Succeeded => log.agent(format!("succeeded in {secs:.1}s")),
            other => log.agent(format!(
                "{} after {secs:.1}s: {}",
                outcome_word(other),
                res.error
            )),
        }
        drop(permit);
        self.finish(&id, res);
    }

    /// Stores the result and marks the attempt finished.
    fn finish(&self, id: &str, result: AttemptResult) {
        let on_disk = self
            .entries
            .lock()
            .unwrap()
            .get(id)
            .is_some_and(|e| e.on_disk);
        let mut memory = None;
        if on_disk {
            if let Err(e) = self
                .outbox
                .put(&OutboxRecord::from_result(&result, now_ms()))
            {
                tracing::error!(attempt = %id, "{e:#}; keeping the result in memory only");
                memory = Some(result.clone());
            }
        } else {
            memory = Some(result.clone());
        }
        tracing::info!(
            attempt = %id, outcome = result.outcome().as_str_name(),
            "attempt finished"
        );
        if let Some(entry) = self.entries.lock().unwrap().get_mut(id) {
            entry.state = State::Finished;
            entry.memory_result = memory;
        }
        self.wake.notify_one();
    }

    /// Handles `CancelAttempt`. Mutations are never interrupted.
    pub fn cancel(&self, id: &str) {
        let entries = self.entries.lock().unwrap();
        let Some(entry) = entries.get(id) else {
            tracing::debug!(attempt = %id, "cancel for an unknown attempt");
            return;
        };
        match entry.state {
            State::Finished => {}
            State::Queued => entry.cancel.cancel(),
            State::Running if entry.mutating => {
                entry.logger.agent(format!(
                    "cancel requested; refused, because {}/{} must not be interrupted",
                    entry.executor, entry.action
                ));
                tracing::warn!(attempt = %id, "refusing to cancel a running {}/{}", entry.executor, entry.action);
            }
            State::Running => {
                entry
                    .logger
                    .agent("cancel requested by the server; stopping");
                entry.cancel.cancel();
            }
        }
    }

    /// Handles `ResultAck`: forgets the attempt and deletes its files.
    pub fn ack(&self, id: &str) {
        let removed = {
            let mut entries = self.entries.lock().unwrap();
            match entries.get(id) {
                Some(e) if e.state != State::Finished => {
                    tracing::warn!(attempt = %id, "acknowledgement for an attempt that has not finished");
                    return;
                }
                Some(_) => entries.remove(id),
                None => None,
            }
        };
        let on_disk = removed.map(|e| e.on_disk).unwrap_or_else(|| is_safe_id(id));
        if !on_disk {
            return;
        }
        // The marker goes first: a workspace without a marker is discarded at
        // startup, while a marker without an outbox entry would be reported as
        // an interrupted attempt.
        let ws = workspace::dir(&self.work_dir, id);
        if let Err(e) = crate::fsutil::remove_durably(&ws.join(workspace::MARKER)) {
            tracing::error!(attempt = %id, "{e:#}");
        }
        if let Err(e) = self.outbox.remove(id) {
            tracing::error!(attempt = %id, "{e:#}");
        }
        tracing::debug!(attempt = %id, "result acknowledged; removing the workspace");
        tokio::task::spawn_blocking(move || workspace::remove(&ws));
    }

    /// Stops accepting work and cancels everything that may be cancelled.
    pub fn drain(&self) {
        self.draining.store(true, Ordering::SeqCst);
        let entries = self.entries.lock().unwrap();
        for entry in entries.values() {
            match entry.state {
                State::Queued => entry.cancel.cancel(),
                State::Running if !entry.mutating => {
                    entry.logger.agent("the agent is shutting down; stopping");
                    entry.cancel.cancel();
                }
                _ => {}
            }
        }
    }

    /// Everything a connection has not sent yet: `AttemptStarted`, log batches,
    /// then results, per attempt in the order they were accepted.
    pub fn collect(&self, cursor: &mut SessionCursor) -> Vec<AgentMessage> {
        struct Pending {
            id: String,
            started: bool,
            logs: Arc<LogBuffer>,
            result: Option<Option<AttemptResult>>,
            mutating: bool,
        }
        let mut pending: Vec<(u64, Pending)> = {
            let mut entries = self.entries.lock().unwrap();
            entries
                .iter_mut()
                .map(|(id, e)| {
                    // Once per connection for every attempt that started, so a
                    // Started lost with a dropped connection is repaired.
                    let started =
                        e.state != State::Queued && cursor.started_sent.insert(id.clone());
                    let want_result = e.state == State::Finished
                        && (!cursor.results_sent.contains(id)
                            || std::mem::take(&mut e.resend_result));
                    (
                        e.order,
                        Pending {
                            id: id.clone(),
                            started,
                            logs: e.logs.clone(),
                            result: want_result.then(|| e.memory_result.clone()),
                            mutating: e.mutating,
                        },
                    )
                })
                .collect()
        };
        pending.sort_by_key(|(order, _)| *order);

        let mut out = Vec::new();
        for (_, p) in pending {
            if p.started {
                out.push(AgentMessage {
                    kind: Some(Kind::Started(AttemptStarted {
                        attempt_id: p.id.clone(),
                    })),
                });
            }
            let mut next = cursor.log_next.get(&p.id).copied().unwrap_or(0);
            while let Some((first, lines)) = p.logs.batch_from(next, MAX_BATCH_LINES) {
                next = first + lines.len() as u64;
                out.push(AgentMessage {
                    kind: Some(Kind::Log(AttemptLog {
                        attempt_id: p.id.clone(),
                        first_sequence: first,
                        lines,
                    })),
                });
            }
            cursor.log_next.insert(p.id.clone(), next);

            if let Some(memory) = p.result {
                let result = match memory {
                    Some(r) => Some(r),
                    None => self.load_result(&p.id, p.mutating),
                };
                if let Some(r) = result {
                    out.push(AgentMessage {
                        kind: Some(Kind::Result(r)),
                    });
                }
                cursor.results_sent.insert(p.id);
            }
        }
        out
    }

    fn load_result(&self, id: &str, mutating: bool) -> Option<AttemptResult> {
        match self.outbox.get(id).map(|r| r.map(|r| r.to_result())) {
            Ok(Some(Ok(r))) => Some(r),
            other => {
                tracing::error!(attempt = %id, "the stored result is unreadable ({other:?})");
                let outcome = if mutating {
                    Outcome::Uncertain
                } else {
                    Outcome::Failed
                };
                Some(result(
                    id,
                    outcome,
                    "the agent lost the attempt's stored result",
                ))
            }
        }
    }
}

fn result(id: &str, outcome: Outcome, error: &str) -> AttemptResult {
    AttemptResult {
        attempt_id: id.to_owned(),
        outcome: outcome as i32,
        outputs_json: "{}".to_owned(),
        error: error.to_owned(),
        artifacts: Vec::new(),
    }
}

fn failed(id: &str, error: &str) -> AttemptResult {
    result(id, Outcome::Failed, error)
}

fn outcome_word(outcome: Outcome) -> &'static str {
    match outcome {
        Outcome::Succeeded => "succeeded",
        Outcome::Failed => "failed",
        Outcome::Uncertain => "uncertain",
        Outcome::Cancelled => "cancelled",
        Outcome::Unspecified => "ended",
    }
}

#[cfg(test)]
mod tests;
