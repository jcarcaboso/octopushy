//! Performance evals for the agent, with the resilience invariants a faster agent must keep.
//!
//! Each scenario runs the real agent (`octopushy::run::run`) against the fake server over mutual
//! TLS and mock-proxmox, or drives one hot path directly. Timings are the median of
//! `OCTOPUSHY_EVAL_RUNS` runs (3). Every run also checks invariants: one result for every
//! assignment, contiguous log sequences, no secret on the wire, a change never cancelled once
//! started, the outbox and workspaces emptied after acknowledgement.
//!
//! The report goes to `target/evals/agent-latest.json` and is compared with
//! `agent/evals/baseline.json`. The test fails when a check fails or a metric is worse than its
//! baseline by more than the metric's tolerance. `OCTOPUSHY_EVAL_SAVE_BASELINE=1` writes the
//! report as the new baseline. Run it with `just agent-evals`; see `agent/evals/README.md`.

mod common;

use std::collections::{BTreeMap, HashMap, HashSet};
use std::path::{Path, PathBuf};
use std::sync::Arc;
use std::time::{Duration, Instant};

use common::{Event, FakeServer, Options};
use mock_proxmox::{Fixture, Options as MockOptions, RunningMock};
use octopushy::attempts::logs::{AttemptLogger, LogBuffer};
use octopushy::enrollment::{EnrollOptions, EnrollmentToken, enroll};
use octopushy::executors::opentofu::archive;
use octopushy::masking::Masker;
use octopushy::outbox::{Outbox, OutboxRecord};
use octopushy::run::{RunOptions, run};
use octopushy::tools::RuntimeKind;
use octopushy_proto::v1::{
    Artifact, Assignment, AttemptResult, Outcome, agent_message::Kind as AgentKind,
    server_message::Kind as ServerKind,
};
use prost::Message;
use serde::{Deserialize, Serialize};
use serde_json::{Value, json};
use tokio::sync::Notify;
use tokio_util::sync::CancellationToken;

// ---------------------------------------------------------------------------
// The report

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "lowercase")]
enum Better {
    Lower,
    Higher,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
struct Metric {
    /// The median of `samples`.
    value: f64,
    unit: String,
    better: Better,
    /// The relative change allowed before it counts as a regression.
    tolerance: f64,
    /// An absolute change always allowed, so a tiny value does not fail on noise.
    slack: f64,
    samples: Vec<f64>,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
struct Check {
    passed: bool,
    /// The first failure, if any.
    detail: String,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
struct Host {
    cpus: usize,
    cpu_model: String,
    kernel: String,
    profile: String,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
struct Report {
    schema: u32,
    agent_version: String,
    git: String,
    runs: usize,
    host: Host,
    metrics: BTreeMap<String, Metric>,
    checks: BTreeMap<String, Check>,
}

#[derive(Default)]
struct Recorder {
    metrics: BTreeMap<String, Metric>,
    checks: BTreeMap<String, Check>,
}

/// How a metric is judged against its baseline.
#[derive(Clone, Copy)]
struct Judge {
    unit: &'static str,
    better: Better,
    tolerance: f64,
    slack: f64,
}

/// A wall-clock latency on the loopback: noisy, so a wide band.
const LATENCY_MS: Judge = Judge {
    unit: "ms",
    better: Better::Lower,
    tolerance: 0.30,
    slack: 2.0,
};
/// A reconnect waits a random half to whole backoff step, so a wider band still.
const JITTERED_MS: Judge = Judge {
    unit: "ms",
    better: Better::Lower,
    tolerance: 0.45,
    slack: 50.0,
};
const CPU_MS: Judge = Judge {
    unit: "ms",
    better: Better::Lower,
    tolerance: 0.30,
    slack: 2.0,
};
const RATE: Judge = Judge {
    unit: "per s",
    better: Better::Higher,
    tolerance: 0.25,
    slack: 0.0,
};
const COUNT: Judge = Judge {
    unit: "count",
    better: Better::Lower,
    tolerance: 0.15,
    slack: 1.0,
};
const BYTES: Judge = Judge {
    unit: "bytes",
    better: Better::Lower,
    tolerance: 0.15,
    slack: 256.0,
};
const MEMORY_MIB: Judge = Judge {
    unit: "MiB",
    better: Better::Lower,
    tolerance: 0.30,
    slack: 8.0,
};

impl Recorder {
    fn metric(&mut self, name: &str, judge: Judge, value: f64) {
        let m = self
            .metrics
            .entry(name.to_owned())
            .or_insert_with(|| Metric {
                value: 0.0,
                unit: judge.unit.to_owned(),
                better: judge.better,
                tolerance: judge.tolerance,
                slack: judge.slack,
                samples: Vec::new(),
            });
        m.samples.push(round(value));
        m.value = round(median(&m.samples));
    }

    /// Records an invariant. It passes only if it passes in every run.
    fn check(&mut self, name: &str, passed: bool, detail: impl FnOnce() -> String) {
        let c = self.checks.entry(name.to_owned()).or_insert(Check {
            passed: true,
            detail: String::new(),
        });
        if !passed && c.passed {
            c.passed = false;
            c.detail = detail();
        }
    }
}

fn round(v: f64) -> f64 {
    (v * 1000.0).round() / 1000.0
}

fn median(v: &[f64]) -> f64 {
    percentile(v, 0.5)
}

fn percentile(v: &[f64], p: f64) -> f64 {
    if v.is_empty() {
        return 0.0;
    }
    let mut s = v.to_vec();
    s.sort_by(f64::total_cmp);
    let rank = (p * (s.len() - 1) as f64).round() as usize;
    s[rank]
}

fn ms(d: Duration) -> f64 {
    d.as_secs_f64() * 1000.0
}

/// CPU time of the whole test process: agent, fake server and mock alike.
fn cpu_time() -> Duration {
    // SAFETY: getrusage writes into the zeroed struct it is given.
    let mut u: libc::rusage = unsafe { std::mem::zeroed() };
    unsafe { libc::getrusage(libc::RUSAGE_SELF, &mut u) };
    let tv = |t: libc::timeval| Duration::new(t.tv_sec as u64, t.tv_usec as u32 * 1000);
    tv(u.ru_utime) + tv(u.ru_stime)
}

/// A field of `/proc/self/status`, in MiB.
fn status_mib(field: &str) -> Option<f64> {
    let status = std::fs::read_to_string("/proc/self/status").ok()?;
    let line = status.lines().find(|l| l.starts_with(field))?;
    let kib: f64 = line.split_whitespace().nth(1)?.parse().ok()?;
    Some(kib / 1024.0)
}

/// Resets the process's peak resident set (Linux: `5` > clear_refs) and returns the resident set
/// now, so the memory a scenario adds can be told from what earlier ones left.
fn reset_peak_rss() -> Option<f64> {
    std::fs::write("/proc/self/clear_refs", "5").ok()?;
    status_mib("VmRSS:")
}

/// On the disk the target directory is on, not a tmpfs `/tmp`, where an fsync costs nothing.
fn scratch() -> tempfile::TempDir {
    tempfile::tempdir_in(env!("CARGO_TARGET_TMPDIR")).unwrap()
}

// ---------------------------------------------------------------------------
// Driving the agent

async fn mock() -> RunningMock {
    mock_proxmox::start(
        Fixture::example(),
        MockOptions {
            listen: "127.0.0.1:0".parse().unwrap(),
            tls: true,
        },
    )
    .await
    .unwrap()
}

async fn enroll_into(server: &FakeServer, dir: &Path) {
    enroll(EnrollOptions {
        server: server.url.clone(),
        token: EnrollmentToken::parse(&server.token()).unwrap(),
        name: Some("eval-agent".into()),
        state_dir: dir.to_path_buf(),
        force: false,
    })
    .await
    .unwrap();
}

/// The agent as deployed with a container runtime, which lets attempts run concurrently. The
/// runtime is unusable (`/bin/false`), so only `proxmox` is offered.
fn container_agent(dir: &Path) -> RunOptions {
    let mut opts = RunOptions::new(dir.to_path_buf());
    opts.tool_runtime = RuntimeKind::Podman;
    opts.container_binary = Some("/bin/false".into());
    opts.nix_binary = "/nonexistent/octopushy-eval-nix".into();
    opts
}

struct Running {
    shutdown: CancellationToken,
    task: tokio::task::JoinHandle<anyhow::Result<()>>,
}

impl Running {
    fn start(opts: RunOptions) -> Self {
        let shutdown = CancellationToken::new();
        let task = tokio::spawn(run(opts, shutdown.clone()));
        Self { shutdown, task }
    }

    async fn stop(self) -> Duration {
        let t = Instant::now();
        self.shutdown.cancel();
        tokio::time::timeout(Duration::from_secs(60), self.task)
            .await
            .expect("the agent stops within 60s")
            .unwrap()
            .unwrap();
        t.elapsed()
    }
}

fn connection(mock: &RunningMock) -> Value {
    json!({ "endpoint": mock.endpoint(), "verifyTls": true, "caCertificatePem": mock.ca_pem() })
}

fn observe(id: &str, mock: &RunningMock) -> Assignment {
    Assignment {
        attempt_id: id.into(),
        executor: "proxmox".into(),
        action: "observe".into(),
        input_json: json!({ "connection": connection(mock) }).to_string(),
        secrets: HashMap::from([("proxmox.token".to_owned(), mock.token())]),
        lease_seconds: 60,
        ..Default::default()
    }
}

fn set_protection(id: &str, mock: &RunningMock, on: bool) -> Assignment {
    Assignment {
        action: "set-protection".into(),
        input_json: json!({
            "connection": connection(mock), "node": "korriban", "vmid": 210,
            "kind": "qemu", "protection": on,
        })
        .to_string(),
        ..observe(id, mock)
    }
}

/// What the server saw, per attempt.
#[derive(Default)]
struct Seen {
    sent: HashMap<String, Instant>,
    started: HashMap<String, Instant>,
    results: HashMap<String, Vec<(Instant, AttemptResult)>>,
    logs: HashMap<String, BTreeMap<u64, String>>,
    /// A sequence number sent again with other text.
    log_conflicts: Vec<String>,
    messages: usize,
    bytes: usize,
    hellos: Vec<Instant>,
    disconnects: usize,
}

impl Seen {
    fn absorb(&mut self, event: Event) {
        let now = Instant::now();
        let m = match event {
            Event::Message(m) => m,
            Event::Disconnected => {
                self.disconnects += 1;
                return;
            }
            _ => return,
        };
        self.messages += 1;
        self.bytes += m.encoded_len();
        match m.kind {
            Some(AgentKind::Hello(_)) => self.hellos.push(now),
            Some(AgentKind::Started(s)) => {
                self.started.entry(s.attempt_id).or_insert(now);
            }
            Some(AgentKind::Log(l)) => {
                let lines = self.logs.entry(l.attempt_id.clone()).or_default();
                for (i, line) in l.lines.into_iter().enumerate() {
                    let seq = l.first_sequence + i as u64;
                    match lines.get(&seq) {
                        Some(text) if *text != line.text => self.log_conflicts.push(format!(
                            "{} #{seq}: {text:?} then {:?}",
                            l.attempt_id, line.text
                        )),
                        Some(_) => {}
                        None => {
                            lines.insert(seq, line.text);
                        }
                    }
                }
            }
            Some(AgentKind::Result(r)) => {
                self.results
                    .entry(r.attempt_id.clone())
                    .or_default()
                    .push((now, r));
            }
            _ => {}
        }
    }

    fn all_finished(&self, ids: &[String]) -> bool {
        ids.iter().all(|id| self.results.contains_key(id))
    }

    fn result(&self, id: &str) -> Option<&AttemptResult> {
        self.results.get(id).and_then(|r| r.first()).map(|(_, r)| r)
    }

    fn outcome(&self, id: &str) -> Option<Outcome> {
        self.result(id).map(|r| r.outcome())
    }

    /// Attempts whose lines do not run 0, 1, 2… without a gap.
    fn log_gaps(&self, ids: &[String]) -> Vec<String> {
        ids.iter()
            .filter(|id| {
                let lines = self.logs.get(*id);
                let n = lines.map_or(0, |l| l.len() as u64);
                n == 0 || lines.unwrap().keys().last() != Some(&(n - 1))
            })
            .cloned()
            .collect()
    }

    /// Attempts given different results on different sends.
    fn inconsistent_results(&self) -> Vec<String> {
        self.results
            .iter()
            .filter(|(_, rs)| {
                rs.windows(2).any(|w| {
                    (w[0].1.outcome, &w[0].1.outputs_json, &w[0].1.error)
                        != (w[1].1.outcome, &w[1].1.outputs_json, &w[1].1.error)
                })
            })
            .map(|(id, _)| id.clone())
            .collect()
    }

    fn leaked(&self, secrets: &[&str]) -> Vec<String> {
        let texts = self
            .logs
            .values()
            .flat_map(|l| l.values())
            .chain(self.results.values().flatten().map(|(_, r)| &r.error))
            .chain(
                self.results
                    .values()
                    .flatten()
                    .map(|(_, r)| &r.outputs_json),
            );
        texts
            .filter(|t| secrets.iter().any(|s| t.contains(s)))
            .take(3)
            .cloned()
            .collect()
    }

    fn duplicate_results(&self) -> usize {
        self.results
            .values()
            .map(|r| r.len().saturating_sub(1))
            .sum()
    }
}

/// Absorbs events until `done` holds; `step` may act on the server between events.
async fn pump(
    server: &mut FakeServer,
    seen: &mut Seen,
    mut step: impl FnMut(&FakeServer, &Seen),
    done: impl Fn(&Seen) -> bool,
) {
    while !done(seen) {
        let e = server.next_event().await;
        seen.absorb(e);
        step(server, seen);
    }
}

async fn wait_until(what: &str, mut f: impl FnMut() -> bool) -> bool {
    for _ in 0..200 {
        if f() {
            return true;
        }
        tokio::time::sleep(Duration::from_millis(25)).await;
    }
    eprintln!("timed out waiting until {what}");
    false
}

fn dir_is_empty(p: &Path) -> bool {
    std::fs::read_dir(p)
        .map(|mut d| d.next().is_none())
        .unwrap_or(true)
}

/// The proxmox token's secret part, which must never be sent.
fn token_secret(mock: &RunningMock) -> String {
    mock.token().split_once('=').unwrap().1.to_owned()
}

/// Checks every attempt's result arrived, logs are whole and nothing secret was sent.
fn common_checks(rec: &mut Recorder, prefix: &str, seen: &Seen, ids: &[String], secrets: &[&str]) {
    let gaps = seen.log_gaps(ids);
    rec.check(
        &format!("{prefix}.logs_contiguous"),
        gaps.is_empty(),
        || format!("attempts with a gap in their log sequence: {gaps:?}"),
    );
    rec.check(
        &format!("{prefix}.logs_consistent"),
        seen.log_conflicts.is_empty(),
        || {
            format!(
                "{:?}",
                &seen.log_conflicts[..seen.log_conflicts.len().min(3)]
            )
        },
    );
    let leaked = seen.leaked(secrets);
    rec.check(
        &format!("{prefix}.no_secret_sent"),
        leaked.is_empty(),
        || format!("{leaked:?}"),
    );
    let inconsistent = seen.inconsistent_results();
    rec.check(
        &format!("{prefix}.results_consistent"),
        inconsistent.is_empty(),
        || format!("attempts with different results on different sends: {inconsistent:?}"),
    );
    let unknown: Vec<_> = seen
        .results
        .keys()
        .filter(|id| !ids.contains(id))
        .cloned()
        .collect();
    rec.check(
        &format!("{prefix}.no_unknown_results"),
        unknown.is_empty(),
        || format!("{unknown:?}"),
    );
}

async fn check_cleaned_up(rec: &mut Recorder, prefix: &str, state: &Path) {
    let outbox = state.join("outbox");
    let work = state.join("work");
    let ok = wait_until("the outbox and workspaces are empty", || {
        dir_is_empty(&outbox) && dir_is_empty(&work)
    })
    .await;
    rec.check(&format!("{prefix}.cleaned_up_after_ack"), ok, || {
        format!(
            "outbox empty: {}, work empty: {}",
            dir_is_empty(&outbox),
            dir_is_empty(&work)
        )
    });
}

// ---------------------------------------------------------------------------
// Scenarios

/// Start to `Hello`, a server-side disconnect to the next `Hello`, an idle shutdown.
async fn startup_reconnect_shutdown(rec: &mut Recorder) {
    let mut server = FakeServer::start(Options {
        heartbeat_seconds: 5,
        ..Options::default()
    })
    .await;
    let state = scratch();
    enroll_into(&server, state.path()).await;
    let mut seen = Seen::default();

    let t = Instant::now();
    let agent = Running::start(container_agent(state.path()));
    pump(&mut server, &mut seen, |_, _| {}, |s| !s.hellos.is_empty()).await;
    rec.metric("startup.to_hello_ms", LATENCY_MS, ms(t.elapsed()));

    let t = Instant::now();
    server.disconnect();
    pump(&mut server, &mut seen, |_, _| {}, |s| s.hellos.len() >= 2).await;
    rec.metric("reconnect.to_hello_ms", JITTERED_MS, ms(t.elapsed()));

    let stopped = agent.stop().await;
    rec.metric("shutdown.idle_ms", LATENCY_MS, ms(stopped));
}

/// One observe at a time: what an operator waits for a single read.
async fn sequential_observes(rec: &mut Recorder, mock: &RunningMock, n: usize) {
    let mut server = FakeServer::start(Options {
        auto_ack: true,
        heartbeat_seconds: 5,
        max_concurrency: 4,
        ..Options::default()
    })
    .await;
    let state = scratch();
    enroll_into(&server, state.path()).await;
    let agent = Running::start(container_agent(state.path()));
    let mut seen = Seen::default();
    pump(&mut server, &mut seen, |_, _| {}, |s| !s.hellos.is_empty()).await;

    let mut ids = Vec::new();
    let (mut to_started, mut to_result) = (Vec::new(), Vec::new());
    for i in 0..n {
        let id = format!("seq-{i:03}");
        ids.push(id.clone());
        let t = Instant::now();
        seen.sent.insert(id.clone(), t);
        server
            .send(ServerKind::Assignment(observe(&id, mock)))
            .await;
        let want = [id.clone()];
        pump(&mut server, &mut seen, |_, _| {}, |s| s.all_finished(&want)).await;
        to_started.push(ms(seen.started.get(&id).map_or(t, |s| *s) - t));
        to_result.push(ms(seen.results[&id][0].0 - t));
    }
    rec.metric("observe.to_started_p50_ms", LATENCY_MS, median(&to_started));
    rec.metric("observe.to_result_p50_ms", LATENCY_MS, median(&to_result));
    rec.metric(
        "observe.to_result_p95_ms",
        LATENCY_MS,
        percentile(&to_result, 0.95),
    );
    let failed: Vec<_> = ids
        .iter()
        .filter(|id| seen.outcome(id) != Some(Outcome::Succeeded))
        .collect();
    rec.check("observe.all_succeeded", failed.is_empty(), || {
        format!(
            "{failed:?}: {:?}",
            failed.first().and_then(|id| seen.result(id))
        )
    });
    common_checks(rec, "observe", &seen, &ids, &[&token_secret(mock)]);
    check_cleaned_up(rec, "observe", state.path()).await;
    agent.stop().await;
}

/// Many observes at once, up to the server's concurrency limit, optionally with the server
/// dropping the channel while they run.
async fn burst(
    rec: &mut Recorder,
    mock: &RunningMock,
    name: &str,
    n: usize,
    concurrency: u32,
    disconnect_after: &[usize],
) {
    let mut server = FakeServer::start(Options {
        auto_ack: true,
        heartbeat_seconds: 5,
        max_concurrency: concurrency,
        ..Options::default()
    })
    .await;
    let state = scratch();
    enroll_into(&server, state.path()).await;
    let agent = Running::start(container_agent(state.path()));
    let mut seen = Seen::default();
    pump(&mut server, &mut seen, |_, _| {}, |s| !s.hellos.is_empty()).await;
    let (messages0, bytes0) = (seen.messages, seen.bytes);

    let ids: Vec<String> = (0..n).map(|i| format!("{name}-{i:03}")).collect();
    let rss0 = reset_peak_rss();
    let (cpu, t) = (cpu_time(), Instant::now());
    for id in &ids {
        seen.sent.insert(id.clone(), Instant::now());
        server.send(ServerKind::Assignment(observe(id, mock))).await;
    }
    // A disconnect waits for the agent to be back from the one before; the run ends once the
    // agent is back from the last, so results resent on the new connection are seen.
    let mut pending: Vec<usize> = disconnect_after.to_vec();
    let dropped = std::cell::Cell::new(0usize);
    pump(
        &mut server,
        &mut seen,
        |server, seen| {
            if pending
                .first()
                .is_some_and(|after| seen.results.len() >= *after)
                && seen.hellos.len() == dropped.get() + 1
            {
                pending.remove(0);
                dropped.set(dropped.get() + 1);
                server.disconnect();
            }
        },
        |s| s.all_finished(&ids) && s.hellos.len() == dropped.get() + 1,
    )
    .await;
    let wall = t.elapsed();
    let cpu = cpu_time() - cpu;

    // Reconnects wait a random part of a backoff step, so their bursts get a wider band.
    let (wall_judge, rate_judge) = if disconnect_after.is_empty() {
        (LATENCY_MS, RATE)
    } else {
        (
            JITTERED_MS,
            Judge {
                tolerance: 0.4,
                ..RATE
            },
        )
    };
    rec.metric(&format!("{name}.wall_ms"), wall_judge, ms(wall));
    rec.metric(
        &format!("{name}.attempts_per_s"),
        rate_judge,
        n as f64 / wall.as_secs_f64(),
    );
    rec.metric(
        &format!("{name}.cpu_ms_per_attempt"),
        CPU_MS,
        ms(cpu) / n as f64,
    );
    if let (Some(before), Some(peak)) = (rss0, status_mib("VmHWM:")) {
        rec.metric(
            &format!("{name}.peak_rss_growth_mib"),
            MEMORY_MIB,
            peak - before,
        );
    }
    if disconnect_after.is_empty() {
        rec.metric(
            &format!("{name}.messages_per_attempt"),
            COUNT,
            (seen.messages - messages0) as f64 / n as f64,
        );
        rec.metric(
            &format!("{name}.wire_bytes_per_attempt"),
            BYTES,
            (seen.bytes - bytes0) as f64 / n as f64,
        );
        rec.check(
            &format!("{name}.one_result_each"),
            seen.duplicate_results() == 0,
            || format!("{} results sent more than once", seen.duplicate_results()),
        );
    } else {
        rec.metric(
            &format!("{name}.duplicate_results"),
            COUNT,
            seen.duplicate_results() as f64,
        );
        rec.metric(
            &format!("{name}.disconnects_hit"),
            Judge {
                better: Better::Higher,
                ..COUNT
            },
            dropped.get() as f64,
        );
        rec.check(
            &format!("{name}.disconnected_mid_burst"),
            dropped.get() > 0,
            || "every result arrived before the first disconnect".into(),
        );
    }
    let failed: Vec<_> = ids
        .iter()
        .filter(|id| seen.outcome(id) != Some(Outcome::Succeeded))
        .collect();
    rec.check(&format!("{name}.all_succeeded"), failed.is_empty(), || {
        format!(
            "{failed:?}: {:?}",
            failed.first().and_then(|id| seen.result(id))
        )
    });
    common_checks(rec, name, &seen, &ids, &[&token_secret(mock)]);
    check_cleaned_up(rec, name, state.path()).await;
    agent.stop().await;
}

/// Shutdown while reads and changes run and wait: reads stop, changes that started finish,
/// every assignment gets a result, the agent exits.
async fn drain_under_load(rec: &mut Recorder, mock: &RunningMock) {
    let mut server = FakeServer::start(Options {
        auto_ack: true,
        heartbeat_seconds: 5,
        max_concurrency: 4,
        ..Options::default()
    })
    .await;
    let state = scratch();
    enroll_into(&server, state.path()).await;
    let agent = Running::start(container_agent(state.path()));
    let mut seen = Seen::default();
    pump(&mut server, &mut seen, |_, _| {}, |s| !s.hellos.is_empty()).await;

    let mut ids = Vec::new();
    let mut changes = HashSet::new();
    for i in 0..32 {
        let id = format!("drain-{i:03}");
        let a = if i % 4 == 1 {
            changes.insert(id.clone());
            set_protection(&id, mock, i % 8 == 1)
        } else {
            observe(&id, mock)
        };
        ids.push(id);
        server.send(ServerKind::Assignment(a)).await;
    }
    pump(&mut server, &mut seen, |_, _| {}, |s| s.results.len() >= 6).await;

    let t = Instant::now();
    agent.shutdown.cancel();
    let mut task = agent.task;
    let exited = loop {
        tokio::select! {
            r = &mut task => break r.map_err(|e| e.to_string()).and_then(|r| r.map_err(|e| format!("{e:#}"))),
            e = server.next_event() => seen.absorb(e),
        }
    };
    rec.metric("drain.shutdown_ms", LATENCY_MS, ms(t.elapsed()));
    rec.check("drain.agent_exited_cleanly", exited.is_ok(), || {
        format!("{exited:?}")
    });

    let missing: Vec<_> = ids.iter().filter(|id| seen.result(id).is_none()).collect();
    rec.check(
        "drain.every_assignment_answered",
        missing.is_empty(),
        || format!("no result for {missing:?}"),
    );
    // Normal cancellation never interrupts a change: one that started must finish.
    let interrupted: Vec<_> = changes
        .iter()
        .filter(|id| seen.started.contains_key(*id) && seen.outcome(id) != Some(Outcome::Succeeded))
        .map(|id| format!("{id}: {:?}", seen.result(id)))
        .collect();
    rec.check(
        "drain.started_changes_finish",
        interrupted.is_empty(),
        || format!("{interrupted:?}"),
    );
    // A read that ran ends succeeded or cancelled. One the agent had not accepted when it began
    // to drain is refused (failed, never started). Nothing is uncertain: no change was cut short.
    let bad: Vec<_> = ids
        .iter()
        .filter(|id| {
            !matches!(
                (seen.started.contains_key(*id), seen.outcome(id)),
                (_, None | Some(Outcome::Succeeded | Outcome::Cancelled))
                    | (false, Some(Outcome::Failed))
            )
        })
        .map(|id| format!("{id}: {:?}", seen.result(id)))
        .collect();
    rec.check("drain.outcomes_expected", bad.is_empty(), || {
        format!("{bad:?}")
    });
    common_checks(rec, "drain", &seen, &ids, &[&token_secret(mock)]);
}

const TOFU_SECRET: &str = "eval-t0ken-5ecret-value";

/// A `tofu` whose plan prints `@LINES@` lines, a few holding the token and terminal escapes.
const FAKE_TOFU: &str = r#"#!/bin/bash
case "$1" in
  version) echo '{"terraform_version":"1.13.1","platform":"linux_amd64","provider_selections":{}}' ;;
  init) [ -f .terraform.lock.hcl ] || echo '# lock' > .terraform.lock.hcl; mkdir -p .terraform
        echo "OpenTofu has been successfully initialized!" ;;
  workspace) echo "Switched to workspace." ;;
  plan)
    out=""; for a in "$@"; do case "$a" in -out=*) out="${a#-out=}" ;; esac; done
    echo "PLAN destroy=0" > "$out"
    awk -v n=@LINES@ -v tok="$PROXMOX_VE_API_TOKEN" 'BEGIN {
      for (i = 0; i < n; i++) {
        if (i % 500 == 0) printf "\033[1mmodule.guest[%d]\033[0m: debug token=%s\n", i, tok
        else printf "module.guest[%d]: Refreshing state... [id=%d] with some ordinary text after it\n", i, i
      }
    }'
    echo "Plan: 1 to add, 0 to change, 0 to destroy."
    exit 2 ;;
  show) echo '{"format_version":"1.2","resource_changes":[{"address":"terraform_data.a","type":"terraform_data","name":"a","change":{"actions":["create"]}}]}' ;;
  *) echo "unknown command $1" >&2; exit 1 ;;
esac
"#;

/// A tool's output from its process to the server: a plan printing many lines.
async fn tool_log_stream(rec: &mut Recorder, lines: usize) {
    let mut server = FakeServer::start(Options {
        auto_ack: true,
        heartbeat_seconds: 5,
        ..Options::default()
    })
    .await;
    let state = scratch();
    enroll_into(&server, state.path()).await;
    let tofu = state.path().join("bin/tofu");
    std::fs::create_dir_all(tofu.parent().unwrap()).unwrap();
    std::fs::write(&tofu, FAKE_TOFU.replace("@LINES@", &lines.to_string())).unwrap();
    use std::os::unix::fs::PermissionsExt;
    std::fs::set_permissions(&tofu, std::fs::Permissions::from_mode(0o755)).unwrap();

    let mut opts = RunOptions::new(state.path().to_path_buf());
    opts.tool_runtime = RuntimeKind::Local;
    opts.tofu_binary = tofu;
    opts.nix_binary = "/nonexistent/octopushy-eval-nix".into();
    let agent = Running::start(opts);
    let mut seen = Seen::default();
    pump(&mut server, &mut seen, |_, _| {}, |s| !s.hellos.is_empty()).await;

    let files = archive::pack(&[(
        "modules/opentofu/eval/main.tf",
        b"resource \"terraform_data\" \"a\" {}\n".as_slice(),
    )])
    .unwrap();
    // A tool's run time varies between two modes on some hosts: the median of a few runs.
    const PREVIEWS: usize = 5;
    let (messages0, cpu0) = (seen.messages, cpu_time());
    let mut walls = Vec::new();
    let mut ids = Vec::new();
    for i in 0..PREVIEWS {
        let id = format!("tofu-preview-{i:03}");
        let assignment = Assignment {
            attempt_id: id.clone(),
            executor: "opentofu".into(),
            action: "preview".into(),
            input_json: json!({
                "stack": "home.eval", "root": "modules/opentofu/eval",
                "image": "ghcr.io/opentofu/opentofu:1.13.1",
                "variables": {}, "env": {},
                "secretEnv": { "PROXMOX_VE_API_TOKEN": "proxmox.token" },
                "destroy": false,
            })
            .to_string(),
            secrets: HashMap::from([(
                "proxmox.token".to_owned(),
                format!("root@pam!eval={TOFU_SECRET}"),
            )]),
            files_tar_gz: files.clone(),
            lease_seconds: 60,
            ..Default::default()
        };
        let t = Instant::now();
        server.send(ServerKind::Assignment(assignment)).await;
        let want = [id.clone()];
        pump(&mut server, &mut seen, |_, _| {}, |s| s.all_finished(&want)).await;
        walls.push(t.elapsed());
        ids.push(id);
    }
    let cpu = cpu_time() - cpu0;
    let wall = walls.iter().map(|w| ms(*w)).collect::<Vec<_>>();

    rec.metric("tool_log.wall_ms", LATENCY_MS, median(&wall));
    rec.metric(
        "tool_log.lines_per_s",
        RATE,
        lines as f64 * 1000.0 / median(&wall),
    );
    rec.metric("tool_log.cpu_ms", CPU_MS, ms(cpu) / PREVIEWS as f64);
    rec.metric(
        "tool_log.messages",
        COUNT,
        (seen.messages - messages0) as f64 / PREVIEWS as f64,
    );
    let failed: Vec<_> = ids
        .iter()
        .filter(|id| seen.outcome(id) != Some(Outcome::Succeeded))
        .map(|id| format!("{id}: {:?}", seen.result(id)))
        .collect();
    rec.check("tool_log.preview_succeeded", failed.is_empty(), || {
        format!("{failed:?}")
    });
    let short: Vec<_> = ids
        .iter()
        .map(|id| {
            seen.logs.get(id).map_or(0, |l| {
                l.values()
                    .filter(|t| t.starts_with("module.guest["))
                    .count()
            })
        })
        .filter(|n| *n != lines)
        .collect();
    rec.check("tool_log.every_line_delivered", short.is_empty(), || {
        format!("tool lines delivered: {short:?} of {lines}")
    });
    let escapes = ids.iter().any(|id| {
        seen.logs
            .get(id)
            .is_some_and(|l| l.values().any(|t| t.contains('\u{1b}')))
    });
    rec.check("tool_log.escapes_removed", !escapes, || {
        "a line kept a terminal escape".into()
    });
    common_checks(rec, "tool_log", &seen, &ids, &[TOFU_SECRET]);
    check_cleaned_up(rec, "tool_log", state.path()).await;
    agent.stop().await;
}

/// Masking, cleaning and buffering of log lines, without a process or a connection.
fn log_pipeline(rec: &mut Recorder, n: usize) {
    let secrets: Vec<String> = (0..12)
        .map(|i| format!("user{i}@pve!tok{i}=5ecret-{i:04}-aaaa-bbbb-cccc"))
        .chain([
            "-----BEGIN KEY-----\nMIIEvQIBADANBgkqhkiG9w0BAQEFAASC\nBKcwggSjAgEAAoIBAQC7\n-----END KEY-----"
                .to_owned(),
        ])
        .collect();
    let buffer = Arc::new(LogBuffer::default());
    let masker = Arc::new(Masker::new(secrets.iter().map(String::as_str)));
    let log = AttemptLogger::new(buffer.clone(), masker, Arc::new(Notify::new()));
    let samples: Vec<String> = (0..64)
        .map(|i| match i % 16 {
            0 => format!(
                "\u{1b}[32mok\u{1b}[0m: [host{i}] => {{\"changed\": false, \"token\": \"{}\"}}",
                secrets[i % 12]
            ),
            1 => format!("TASK [role : step {i}] {}", "*".repeat(60)),
            _ => format!(
                "module.guest[{i}]: Still creating... [{i}s elapsed] lorem ipsum dolor sit amet"
            ),
        })
        .collect();

    let (cpu, t) = (cpu_time(), Instant::now());
    for i in 0..n {
        log.stdout(&samples[i % samples.len()]);
    }
    let wall = t.elapsed();
    let cpu = cpu_time() - cpu;
    rec.metric(
        "log_pipeline.lines_per_s",
        RATE,
        n as f64 / wall.as_secs_f64(),
    );
    rec.metric(
        "log_pipeline.cpu_us_per_line",
        Judge {
            unit: "us",
            slack: 0.05,
            ..CPU_MS
        },
        ms(cpu) * 1000.0 / n as f64,
    );

    let t = Instant::now();
    let mut next = 0;
    let mut leaked = 0;
    while let Some((first, lines)) = buffer.batch_from(next, 100) {
        next = first + lines.len() as u64;
        leaked += lines
            .iter()
            .filter(|l| {
                secrets
                    .iter()
                    .any(|s| l.text.contains(s.as_str()) || l.text.contains("5ecret-"))
            })
            .count();
    }
    rec.metric(
        "log_pipeline.drain_retained_ms",
        Judge {
            slack: 1.0,
            ..LATENCY_MS
        },
        ms(t.elapsed()),
    );
    rec.check("log_pipeline.no_secret_kept", leaked == 0, || {
        format!("{leaked} lines kept a secret")
    });
}

/// Results written durably, listed, read back and removed, as a burst of plans would.
fn outbox(rec: &mut Recorder, n: usize, artifact_kib: usize) {
    let dir = scratch();
    let outbox = Outbox::open(dir.path().join("outbox")).unwrap();
    let content: Vec<u8> = (0..artifact_kib * 1024)
        .map(|i| (i * 31 % 251) as u8)
        .collect();
    let records: Vec<OutboxRecord> = (0..n)
        .map(|i| {
            OutboxRecord::from_result(
                &AttemptResult {
                    attempt_id: format!("outbox-{i:04}"),
                    outcome: Outcome::Succeeded as i32,
                    outputs_json: json!({ "hasChanges": true, "summary": { "add": i } })
                        .to_string(),
                    error: String::new(),
                    artifacts: vec![Artifact {
                        name: "plan.tar.gz".into(),
                        content: content.clone(),
                        sha256: "0".repeat(64),
                    }],
                },
                i as u64,
            )
        })
        .collect();

    let t = Instant::now();
    for r in &records {
        outbox.put(r).unwrap();
    }
    rec.metric(
        "outbox.put_ms",
        Judge {
            slack: 0.5,
            ..LATENCY_MS
        },
        ms(t.elapsed()) / n as f64,
    );
    let t = Instant::now();
    let listed = outbox.list().unwrap();
    rec.metric(
        "outbox.list_ms",
        Judge {
            slack: 1.0,
            ..LATENCY_MS
        },
        ms(t.elapsed()),
    );
    let t = Instant::now();
    let mut intact = 0;
    for r in &records {
        let back = outbox.get(&r.attempt_id).unwrap().unwrap();
        if back == *r && back.to_result().is_ok() {
            intact += 1;
        }
    }
    rec.metric(
        "outbox.get_ms",
        Judge {
            slack: 0.5,
            ..LATENCY_MS
        },
        ms(t.elapsed()) / n as f64,
    );
    let t = Instant::now();
    for r in &records {
        outbox.remove(&r.attempt_id).unwrap();
    }
    rec.metric(
        "outbox.remove_ms",
        Judge {
            slack: 0.5,
            ..LATENCY_MS
        },
        ms(t.elapsed()) / n as f64,
    );
    rec.check(
        "outbox.round_trip_intact",
        intact == n && listed.len() == n,
        || format!("{intact} of {n} intact, {} listed", listed.len()),
    );
    rec.check(
        "outbox.empty_after_remove",
        outbox.list().unwrap().is_empty(),
        || "entries left".into(),
    );
}

// ---------------------------------------------------------------------------
// Comparison

#[derive(Debug, PartialEq)]
enum Verdict {
    Better,
    Same,
    Worse,
    New,
}

fn judge(now: &Metric, base: Option<&Metric>) -> (Verdict, f64) {
    let Some(base) = base else {
        return (Verdict::New, 0.0);
    };
    let delta = now.value - base.value;
    let change = if base.value == 0.0 {
        0.0
    } else {
        delta / base.value
    };
    let allowed = base.value.abs() * now.tolerance + now.slack;
    let worse = match now.better {
        Better::Lower => delta,
        Better::Higher => -delta,
    };
    let verdict = if worse > allowed {
        Verdict::Worse
    } else if -worse > allowed {
        Verdict::Better
    } else {
        Verdict::Same
    };
    (verdict, change)
}

fn compare(report: &Report, baseline: Option<&Report>, partial: bool) -> Vec<String> {
    let mut problems = Vec::new();
    if let Some(b) = baseline {
        if b.host.cpus != report.host.cpus || b.host.cpu_model != report.host.cpu_model {
            eprintln!(
                "warning: the baseline was taken on another host ({} × {}); timings may not compare",
                b.host.cpus, b.host.cpu_model
            );
        }
        if b.host.profile != report.host.profile {
            eprintln!(
                "warning: the baseline was built {}, this run {}",
                b.host.profile, report.host.profile
            );
        }
    }
    eprintln!(
        "\n{:<38} {:>12} {:>12} {:>8}  verdict",
        "metric", "baseline", "now", "change"
    );
    for (name, m) in &report.metrics {
        let base = baseline.and_then(|b| b.metrics.get(name));
        let (verdict, change) = judge(m, base);
        let shown = base.map_or("-".to_owned(), |b| format!("{:.3}", b.value));
        eprintln!(
            "{name:<38} {shown:>12} {:>12.3} {:>+7.1}%  {verdict:?} ({}, {} is better)",
            m.value,
            change * 100.0,
            m.unit,
            match m.better {
                Better::Lower => "lower",
                Better::Higher => "higher",
            }
        );
        if verdict == Verdict::Worse {
            problems.push(format!(
                "{name} regressed: {:.3} → {:.3} {} (allowed ±{:.0}% + {})",
                base.unwrap().value,
                m.value,
                m.unit,
                m.tolerance * 100.0,
                m.slack
            ));
        }
    }
    if let Some(b) = baseline.filter(|_| !partial) {
        for name in b
            .metrics
            .keys()
            .filter(|n| !report.metrics.contains_key(*n))
        {
            problems.push(format!("{name} is in the baseline but was not measured"));
        }
    }
    eprintln!();
    for (name, c) in &report.checks {
        eprintln!(
            "{} {name}{}",
            if c.passed { "ok  " } else { "FAIL" },
            if c.passed {
                String::new()
            } else {
                format!(": {}", c.detail)
            }
        );
        if !c.passed {
            problems.push(format!("check {name} failed: {}", c.detail));
        }
    }
    problems
}

fn host() -> Host {
    let cpu_model = std::fs::read_to_string("/proc/cpuinfo")
        .ok()
        .and_then(|s| {
            s.lines()
                .find(|l| l.starts_with("model name"))
                .and_then(|l| l.split_once(':'))
                .map(|(_, m)| m.trim().to_owned())
        })
        .unwrap_or_default();
    Host {
        cpus: std::thread::available_parallelism().map_or(1, |n| n.get()),
        cpu_model,
        kernel: std::fs::read_to_string("/proc/sys/kernel/osrelease")
            .unwrap_or_default()
            .trim()
            .to_owned(),
        profile: if cfg!(debug_assertions) {
            "debug"
        } else {
            "release"
        }
        .to_owned(),
    }
}

fn git_describe() -> String {
    let out = |args: &[&str]| {
        std::process::Command::new("git")
            .args(args)
            .current_dir(env!("CARGO_MANIFEST_DIR"))
            .output()
            .ok()
            .filter(|o| o.status.success())
            .map(|o| String::from_utf8_lossy(&o.stdout).trim().to_owned())
            .unwrap_or_default()
    };
    let sha = out(&["rev-parse", "--short", "HEAD"]);
    let dirty = !out(&["status", "--porcelain", "--", ".."]).is_empty();
    format!("{sha}{}", if dirty { "-dirty" } else { "" })
}

fn env_path(var: &str, default: PathBuf) -> PathBuf {
    std::env::var_os(var).map_or(default, PathBuf::from)
}

// ---------------------------------------------------------------------------

#[tokio::test(flavor = "multi_thread")]
#[ignore = "performance evals: run with `just agent-evals`"]
async fn agent_evals() {
    if std::env::var_os("OCTOPUSHY_EVAL_TRACE").is_some() {
        common::init_tracing();
    }
    let runs: usize = std::env::var("OCTOPUSHY_EVAL_RUNS")
        .ok()
        .and_then(|v| v.parse().ok())
        .unwrap_or(3);
    let mock = mock().await;
    let mut rec = Recorder::default();
    // `OCTOPUSHY_EVAL_ONLY=burst,outbox` runs only those scenarios, and compares only what they
    // measure.
    let only: Option<Vec<String>> = std::env::var("OCTOPUSHY_EVAL_ONLY")
        .ok()
        .map(|v| v.split(',').map(|s| s.trim().to_owned()).collect());
    let wanted = |name: &str| only.as_ref().is_none_or(|o| o.iter().any(|w| w == name));
    for run in 1..=runs {
        eprintln!("run {run} of {runs}");
        if wanted("startup") {
            startup_reconnect_shutdown(&mut rec).await;
        }
        if wanted("observe") {
            sequential_observes(&mut rec, &mock, 20).await;
        }
        if wanted("burst") {
            burst(&mut rec, &mock, "burst", 64, 8, &[]).await;
        }
        if wanted("burst_disconnects") {
            burst(&mut rec, &mock, "burst_disconnects", 64, 8, &[8, 32]).await;
        }
        if wanted("drain") {
            drain_under_load(&mut rec, &mock).await;
        }
        if wanted("tool_log") {
            tool_log_stream(&mut rec, 15_000).await;
        }
        if wanted("log_pipeline") {
            log_pipeline(&mut rec, 200_000);
        }
        if wanted("outbox") {
            outbox(&mut rec, 100, 256);
        }
    }

    let report = Report {
        schema: 1,
        agent_version: octopushy::VERSION.to_owned(),
        git: git_describe(),
        runs,
        host: host(),
        metrics: rec.metrics,
        checks: rec.checks,
    };
    let agent_dir = Path::new(env!("CARGO_MANIFEST_DIR"))
        .ancestors()
        .nth(2)
        .unwrap();
    let target = Path::new(env!("CARGO_TARGET_TMPDIR")).parent().unwrap();
    let latest = env_path(
        "OCTOPUSHY_EVAL_REPORT",
        target.join("evals/agent-latest.json"),
    );
    let baseline_path = env_path(
        "OCTOPUSHY_EVAL_BASELINE",
        agent_dir.join("evals/baseline.json"),
    );
    std::fs::create_dir_all(latest.parent().unwrap()).unwrap();
    let json = serde_json::to_string_pretty(&report).unwrap() + "\n";
    std::fs::write(&latest, &json).unwrap();
    eprintln!("report: {}", latest.display());

    let baseline: Option<Report> = std::fs::read(&baseline_path)
        .ok()
        .map(|b| serde_json::from_slice(&b).expect("the baseline is a report"));
    if baseline.is_none() {
        eprintln!("no baseline at {}", baseline_path.display());
    }
    let problems = compare(&report, baseline.as_ref(), only.is_some());

    if std::env::var_os("OCTOPUSHY_EVAL_SAVE_BASELINE").is_some() {
        assert!(only.is_none(), "a baseline measures every scenario");
        let failed: Vec<_> = report.checks.iter().filter(|(_, c)| !c.passed).collect();
        assert!(
            failed.is_empty(),
            "not saving a baseline whose checks fail: {failed:?}"
        );
        std::fs::write(&baseline_path, &json).unwrap();
        eprintln!("saved the baseline: {}", baseline_path.display());
        return;
    }
    assert!(problems.is_empty(), "\n{}", problems.join("\n"));
}
