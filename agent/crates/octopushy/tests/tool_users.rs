//! Each attempt's tools as a user of their own, with the local runtime and the agent as root
//! (`tools::users`). These need root and the pool users `agent/packaging/tool-users.sh` makes, so
//! they are ignored by default: `tests/tool-users/run.sh` runs them as root in a container.

use std::collections::HashMap;
use std::os::unix::fs::{MetadataExt, PermissionsExt};
use std::path::{Path, PathBuf};
use std::sync::Arc;
use std::time::Duration;

use octopushy::attempts::{AttemptManager, SessionCursor};
use octopushy::executors::ExecutorConfig;
use octopushy::executors::nix::NixSettings;
use octopushy::executors::opentofu::archive;
use octopushy::outbox::Outbox;
use octopushy::tools::users::{self, UserPool};
use octopushy::tools::{RuntimeKind, ToolRuntime};
use octopushy_proto::v1::agent_message::Kind;
use octopushy_proto::v1::{Assignment, AttemptResult, Outcome};
use serde_json::json;

const NEEDS_ROOT: &str = "needs root and the tool users: tests/tool-users/run.sh";
const AGENT_KEY: &str = "AGENT-PRIVATE-KEY";
/// The store's owner in these tests, the image's octopushy-agent.
const NIX_UID: u32 = 10001;

/// A fake `tofu`. `plan` sleeps for `TF_VAR_wait` seconds, or, given `TF_VAR_neighbour`, tries to
/// reach what is not its own and reports each try, then leaves a process and a file behind.
const FAKE_TOFU: &str = r#"#!/bin/bash
# Whether a thread of the detacher this user can see is alive, whatever its thread group leader.
survivors() {
  alive=gone
  for t in /proc/[0-9]*/task/*; do
    [ "$(cat "$t/comm" 2>/dev/null)" = detach ] || continue
    st=$(awk '/^State:/ {print $2}' "$t/status" 2>/dev/null)
    case "$st" in ""|Z|X) ;; *) alive=alive ;; esac
  done
  echo "survivor $alive"
}
case "$1" in
  version)
    [ -z "$2" ] && { echo "OpenTofu uid=$(id -u)"; exit 0; }
    echo '{"terraform_version":"1.13.1"}' ;;
  init)
    if [ -n "${TF_VAR_stall:-}" ] && [ "$TF_VAR_stall" = init ]; then echo "stalling as $(id -u)"; sleep 3; fi
    if [ -n "${TF_VAR_detach:-}" ]; then "$TF_VAR_detach"; sleep 0.5; survivors; fi
    if [ -n "${TF_VAR_survivors:-}" ]; then survivors; echo "ids $(id -u)"; fi
    echo "initialized" ;;
  plan)
    if [ -n "${TF_VAR_detach:-}" ]; then survivors; "$TF_VAR_detach"; fi
    if [ -n "${TF_VAR_wait:-}" ]; then sleep "$TF_VAR_wait"; fi
    if [ -n "${TF_VAR_neighbour:-}" ]; then
      probe() { if "${@:2}" >/dev/null 2>&1; then echo "probe $1 REACHED"; else echo "probe $1 denied"; fi; }
      probe state-key cat "$TF_VAR_state/key.pem"
      probe state-list ls "$TF_VAR_state"
      probe outbox ls "$TF_VAR_state/outbox"
      probe work-list ls "$TF_VAR_state/work"
      probe provider-cache ls "$TF_VAR_state/cache/providers"
      probe neighbour-file cat "$TF_VAR_neighbour/repo/main.tf"
      probe neighbour-list ls "$TF_VAR_neighbour"
      probe neighbour-marker cat "$TF_VAR_neighbour/attempt.json"
      probe agent-environ cat "/proc/$PPID/environ"
      probe agent-fds ls "/proc/$PPID/fd"
      probe agent-signal kill -0 "$PPID"
      probe own-marker-write sh -c "echo x >> '$TF_VAR_self/attempt.json'"
      probe own-change-record touch "$TF_VAR_self.change-started"
      probe own-workspace-entry touch "$TF_VAR_self/planted"
      echo "ids $(id -u) $(id -g) [$(id -G)]"
      echo "umask $(umask)"
      echo "fds $(ls /proc/self/fd | tr '\n' ' ')"
      grep -E '^(NoNewPrivs|CapEff|CapPrm|CapAmb)' /proc/self/status | tr -s '\t' ' '
      # Things that must not outlive the attempt.
      setsid sleep 1000 </dev/null >/dev/null 2>&1 &
      echo left > "/tmp/octopushy-test-left-$(id -u)"
    fi
    exit 0 ;;
  apply)
    if [ "${TF_VAR_stall:-}" = apply ]; then echo "stalling as $(id -u)"; sleep 3; fi
    echo '{"version":4,"serial":1,"lineage":"l","resources":[]}' > "$(dirname "$PWD")/state/terraform.tfstate" 2>/dev/null
    echo "applied" ;;
  output) echo '{}' ;;
  *) echo "unknown $1" >&2; exit 1 ;;
esac
"#;

/// A fake `nix`: `build` prints the path its flake's `out-path` names.
const FAKE_NIX: &str = r#"#!/bin/bash
case "$1" in
  --version) echo "nix (fake) uid=$(id -u)" ;;
  build)
    flake="${@: -1}"; flake="${flake#path:}"; flake="${flake%%#*}"
    echo "building as uid $(id -u)" >&2
    cat "$flake/out-path" ;;
  store) exit 0 ;;
  *) exit 1 ;;
esac
"#;

struct Agent {
    _dir: tempfile::TempDir,
    state: PathBuf,
    work: PathBuf,
    store: PathBuf,
    manager: Arc<AttemptManager>,
    exec: Arc<ExecutorConfig>,
}

fn executable(path: &Path, text: &str) {
    std::fs::write(path, text).unwrap();
    std::fs::set_permissions(path, std::fs::Permissions::from_mode(0o755)).unwrap();
}

/// An agent's state directory, as `agent run` leaves it when it starts as root, with the fake
/// tools and a Nix store owned by [`NIX_UID`].
fn agent(max: u32) -> Agent {
    assert!(users::is_root(), "{NEEDS_ROOT}");
    let dir = tempfile::tempdir().unwrap();
    std::fs::set_permissions(dir.path(), std::fs::Permissions::from_mode(0o711)).unwrap();
    let state = dir.path().join("state");
    let work = state.join("work");
    std::fs::create_dir_all(state.join("outbox")).unwrap();
    std::fs::create_dir_all(state.join("cache/providers")).unwrap();
    std::fs::write(state.join("key.pem"), AGENT_KEY).unwrap();
    let bin = dir.path().join("bin");
    std::fs::create_dir(&bin).unwrap();
    std::fs::set_permissions(&bin, std::fs::Permissions::from_mode(0o755)).unwrap();
    executable(&bin.join("tofu"), FAKE_TOFU);
    executable(&bin.join("nix"), FAKE_NIX);
    let store = dir.path().join("nix");
    std::fs::create_dir_all(store.join("store")).unwrap();
    std::os::unix::fs::chown(store.join("store"), Some(NIX_UID), Some(NIX_UID)).unwrap();

    let pool = Arc::new(UserPool::discover(&store).unwrap());
    // As `agent run` does: the agent's own directories, here under /tmp, are never swept.
    pool.keep_out_of_sweeps([state.clone(), work.clone(), store.clone(), bin.clone()]);
    pool.set_kill_timeout(Duration::from_secs(2));
    assert_eq!(pool.nix_user().map(|u| u.uid), Some(NIX_UID));
    let nix_cache = state.join("cache/nix");
    users::secure_state(&state, &work, Some((&nix_cache, pool.nix_user().unwrap()))).unwrap();
    let mut runtime = ToolRuntime::new(RuntimeKind::Local, None, bin.join("tofu"), None);
    runtime.nix_binary = bin.join("nix");
    runtime.kill_grace = Duration::from_secs(1);
    runtime.users = Some(pool);
    let exec = Arc::new(ExecutorConfig {
        runtime,
        provider_cache: state.join("cache/providers"),
        poll_interval: Duration::from_millis(50),
        proxmox_timeout: Duration::from_secs(1),
        retry_delay: Duration::from_millis(1),
        server: Default::default(),
        nix: NixSettings::new(store.clone(), u64::MAX, Some(nix_cache)),
    });
    let manager = AttemptManager::new(
        work.clone(),
        Outbox::open(state.join("outbox")).unwrap(),
        exec.clone(),
    );
    manager.set_concurrency(max);
    Agent {
        _dir: dir,
        state,
        work,
        store,
        manager,
        exec,
    }
}

fn preview(id: &str, env: serde_json::Value) -> Assignment {
    Assignment {
        attempt_id: id.into(),
        executor: "opentofu".into(),
        action: "preview".into(),
        input_json: json!({ "root": ".", "env": env }).to_string(),
        files_tar_gz: archive::pack(&[("main.tf", b"# the neighbour's code\n".as_slice())])
            .unwrap(),
        secrets: HashMap::new(),
        ..Default::default()
    }
}

/// Collects what a connection would send until every attempt in `ids` has a result: the log
/// lines and the results, in the order they came.
async fn run_until_done(
    agent: &Agent,
    ids: &[&str],
) -> (HashMap<String, Vec<String>>, Vec<AttemptResult>) {
    let mut cursor = SessionCursor::default();
    let mut logs: HashMap<String, Vec<String>> = HashMap::new();
    let mut results = Vec::new();
    tokio::time::timeout(Duration::from_secs(60), async {
        while results.len() < ids.len() {
            for message in agent.manager.collect(&mut cursor) {
                match message.kind {
                    Some(Kind::Log(log)) => logs
                        .entry(log.attempt_id)
                        .or_default()
                        .extend(log.lines.into_iter().map(|l| l.text)),
                    // A new cursor sends unacknowledged results again: each counts once.
                    Some(Kind::Result(r))
                        if ids.contains(&r.attempt_id.as_str())
                            && !results
                                .iter()
                                .any(|s: &AttemptResult| s.attempt_id == r.attempt_id) =>
                    {
                        results.push(r)
                    }
                    _ => {}
                }
            }
            tokio::time::sleep(Duration::from_millis(20)).await;
        }
    })
    .await
    .unwrap_or_else(|_| panic!("no results within a minute; logs: {logs:#?}"));
    (logs, results)
}

/// Threads, not zombies, whose real, effective, saved or file system user is `uid`. Every thread
/// of every process: a thread group whose leader has exited shows a zombie leader while its
/// other threads run.
fn processes_of(uid: u32) -> Vec<String> {
    let mut found = Vec::new();
    for entry in std::fs::read_dir("/proc").unwrap().flatten() {
        let Ok(tasks) = std::fs::read_dir(entry.path().join("task")) else {
            continue;
        };
        for task in tasks.flatten() {
            let Ok(status) = std::fs::read_to_string(task.path().join("status")) else {
                continue;
            };
            let dead = status.lines().any(|l| {
                l.strip_prefix("State:")
                    .is_some_and(|s| matches!(s.trim_start().chars().next(), Some('Z' | 'X')))
            });
            let mine = status.lines().any(|l| {
                l.strip_prefix("Uid:")
                    .is_some_and(|ids| ids.split_whitespace().any(|id| id == uid.to_string()))
            });
            if mine && !dead {
                found.push(format!(
                    "{} {}",
                    task.path().display(),
                    status.lines().next().unwrap_or("")
                ));
            }
        }
    }
    found
}

fn pool_uids(agent: &Agent) -> Vec<u32> {
    agent
        .exec
        .runtime
        .users
        .as_ref()
        .unwrap()
        .all()
        .map(|u| u.uid)
        .collect()
}

#[tokio::test]
#[ignore = "needs root and the tool users: tests/tool-users/run.sh"]
async fn a_tool_reaches_neither_the_agent_nor_another_attempt_and_leaves_nothing() {
    let agent = agent(4);
    // The neighbour: a preview whose plan takes its time, so the two run together.
    agent
        .manager
        .accept(preview("b", json!({ "TF_VAR_wait": "10" })));
    let neighbour = agent.work.join("b");
    tokio::time::timeout(Duration::from_secs(20), async {
        // Its plan has started: it sleeps there. Who the tool runs as is checked below.
        while !neighbour.join("out").exists() || !neighbour.join("repo/main.tf").exists() {
            tokio::time::sleep(Duration::from_millis(20)).await;
        }
        tokio::time::sleep(Duration::from_millis(500)).await;
    })
    .await
    .expect("the neighbour's tool runs");
    agent.manager.accept(preview(
        "a",
        json!({
            "TF_VAR_neighbour": neighbour,
            "TF_VAR_state": agent.state,
            "TF_VAR_self": agent.work.join("a"),
        }),
    ));
    let (logs, results) = run_until_done(&agent, &["a", "b"]).await;
    let log = logs.get("a").cloned().unwrap_or_default();
    let text = log.join("\n");
    println!("{text}");

    // In parallel: the attempt that started second ended first, while the first still ran.
    let order: Vec<&str> = results.iter().map(|r| r.attempt_id.as_str()).collect();
    assert_eq!(order, ["a", "b"], "the attempts ran side by side");
    for r in &results {
        assert_eq!(r.outcome(), Outcome::Succeeded, "{r:?}");
    }

    let probes: Vec<&String> = log.iter().filter(|l| l.starts_with("probe ")).collect();
    assert_eq!(probes.len(), 14, "every probe ran: {text}");
    let reached: Vec<&&String> = probes.iter().filter(|l| l.ends_with("REACHED")).collect();
    assert!(reached.is_empty(), "the tool reached: {reached:#?}");

    // Its user, dropped completely.
    let ids = log.iter().find(|l| l.starts_with("ids ")).expect("ids");
    let fields: Vec<&str> = ids.split_whitespace().collect();
    let (uid, gid) = (fields[1], fields[2]);
    assert!(
        pool_uids(&agent).iter().any(|u| u.to_string() == uid),
        "{ids}"
    );
    assert_ne!(uid, "0");
    assert_eq!(ids, &format!("ids {uid} {gid} [{gid}]"), "no other groups");
    assert!(log.iter().any(|l| l == "umask 0077"), "{text}");
    assert!(log.iter().any(|l| l == "NoNewPrivs: 1"), "{text}");
    for caps in ["CapEff", "CapPrm", "CapAmb"] {
        assert!(
            log.iter()
                .any(|l| *l == format!("{caps}: 0000000000000000")),
            "{caps}: {text}"
        );
    }
    // Only stdin, stdout, stderr and the directory ls itself reads.
    assert!(log.iter().any(|l| l.trim_end() == "fds 0 1 2 3"), "{text}");

    // Nothing of its user's outlives the attempt.
    for uid in pool_uids(&agent) {
        assert!(
            processes_of(uid).is_empty(),
            "uid {uid}: {:?}",
            processes_of(uid)
        );
        assert!(!Path::new(&format!("/tmp/octopushy-test-left-{uid}")).exists());
    }
    // Its workspace is root's again, and the agent's files are as they were.
    for ws in ["a", "b"] {
        let meta = std::fs::metadata(agent.work.join(ws)).unwrap();
        assert_eq!((meta.uid(), meta.mode() & 0o777), (0, 0o700), "{ws}");
    }
    assert_eq!(
        std::fs::read_to_string(agent.state.join("key.pem")).unwrap(),
        AGENT_KEY
    );
    let marker = std::fs::read_to_string(agent.work.join("a/attempt.json")).unwrap();
    assert!(
        marker.starts_with('{') && !marker.contains("\nx"),
        "{marker}"
    );
    assert!(!agent.work.join("a.change-started").exists());
    // Each attempt its own provider cache, never the shared one.
    assert!(agent.work.join("a/plugin-cache").is_dir());
    assert_eq!(
        std::fs::read_dir(agent.state.join("cache/providers"))
            .unwrap()
            .count(),
        0
    );
}

fn processes_of_pool(agent: &Agent) -> Vec<String> {
    pool_uids(agent)
        .into_iter()
        .flat_map(processes_of)
        .collect()
}

#[tokio::test]
#[ignore = "needs root and the tool users: tests/tool-users/run.sh"]
async fn a_stopped_tool_takes_everything_of_its_user_with_it() {
    let agent = agent(2);
    // The plan sleeps, and the read is cancelled: everything its user runs is killed.
    agent
        .manager
        .accept(preview("c", json!({ "TF_VAR_wait": "600" })));
    tokio::time::timeout(Duration::from_secs(20), async {
        while processes_of_pool(&agent).is_empty() {
            tokio::time::sleep(Duration::from_millis(20)).await;
        }
    })
    .await
    .unwrap();
    agent.manager.cancel("c");
    let (_, results) = run_until_done(&agent, &["c"]).await;
    assert_eq!(results[0].outcome(), Outcome::Cancelled, "{:?}", results[0]);
    assert!(
        processes_of_pool(&agent).is_empty(),
        "{:?}",
        processes_of_pool(&agent)
    );
}

#[tokio::test]
#[ignore = "needs root and the tool users: tests/tool-users/run.sh"]
async fn probes_never_run_as_root() {
    let agent = agent(2);
    let tofu = agent.exec.runtime.probe().await.unwrap();
    assert_eq!(tofu, "OpenTofu uid=65534", "as nobody");
    let nix = agent.exec.runtime.probe_nix().await.unwrap();
    assert_eq!(
        nix,
        format!("nix (fake) uid={NIX_UID}"),
        "as the store's owner"
    );
}

fn build(id: &str, out_path: &Path) -> Assignment {
    Assignment {
        attempt_id: id.into(),
        executor: "nix".into(),
        action: "build".into(),
        input_json: json!({ "flake": "f", "output": "image", "system": "x86_64-linux" })
            .to_string(),
        files_tar_gz: archive::pack(&[
            ("f/flake.nix", b"{}".as_slice()),
            ("f/out-path", out_path.to_string_lossy().as_bytes()),
        ])
        .unwrap(),
        ..Default::default()
    }
}

#[tokio::test]
#[ignore = "needs root and the tool users: tests/tool-users/run.sh"]
async fn the_agent_uploads_only_what_the_build_made() {
    let agent = agent(2);
    let store = agent.store.join("store");
    // A path outside the store: the agent's key.
    let outside = build("n1", &agent.state.join("key.pem"));
    // A store path whose image is a second link to the agent's key, as a build could make where
    // the kernel allows links to files one does not own.
    let linked = store.join("linked");
    std::fs::create_dir(&linked).unwrap();
    std::fs::hard_link(agent.state.join("key.pem"), linked.join("disk.img")).unwrap();
    // A store path whose image is root's.
    let roots = store.join("roots");
    std::fs::create_dir(&roots).unwrap();
    std::fs::write(roots.join("disk.img"), "not the build's").unwrap();
    for ws in [&linked, &roots] {
        std::os::unix::fs::lchown(ws, Some(NIX_UID), Some(NIX_UID)).unwrap();
    }
    agent.manager.accept(outside);
    agent.manager.accept(build("n2", &linked));
    agent.manager.accept(build("n3", &roots));
    let (logs, results) = run_until_done(&agent, &["n1", "n2", "n3"]).await;
    let by_id: HashMap<&str, &AttemptResult> =
        results.iter().map(|r| (r.attempt_id.as_str(), r)).collect();
    for (id, expected) in [
        ("n1", "which is not a path in"),
        ("n2", "no other links"),
        ("n3", "is not the build's"),
    ] {
        let r = by_id[id];
        assert_eq!(r.outcome(), Outcome::Failed, "{r:?}");
        assert!(r.error.contains(expected), "{id}: {}", r.error);
        let log = logs[id].join("\n");
        assert!(
            log.contains(&format!("building as uid {NIX_UID}")),
            "{id}: {log}"
        );
    }
    assert!(processes_of(NIX_UID).is_empty());
}

/// A program that leaves a thread group whose leader has exited: it forks, the child detaches and
/// closes its pipes, starts a worker thread and ends its main thread with `pthread_exit`. The
/// leader then shows as a zombie while the worker runs as the user.
const DETACH_C: &str = r#"
#include <pthread.h>
#include <unistd.h>
static void *work(void *arg) { (void)arg; for (;;) sleep(1); return 0; }
int main(void) {
    if (fork() != 0) return 0;
    setsid();
    close(0); close(1); close(2);
    pthread_t t;
    pthread_create(&t, 0, work, 0);
    pthread_exit(0);
}
"#;

fn detacher(dir: &Path) -> PathBuf {
    let source = dir.join("detach.c");
    let program = dir.join("bin/detach");
    std::fs::write(&source, DETACH_C).unwrap();
    let built = std::process::Command::new("cc")
        .args(["-pthread", "-o"])
        .arg(&program)
        .arg(&source)
        .status()
        .unwrap();
    assert!(built.success(), "cc failed");
    std::fs::set_permissions(&program, std::fs::Permissions::from_mode(0o755)).unwrap();
    program
}

#[tokio::test]
#[ignore = "needs root and the tool users: tests/tool-users/run.sh"]
async fn a_thread_group_whose_leader_exited_dies_before_its_user_runs_again() {
    let agent = agent(1);
    let detach = detacher(agent.state.parent().unwrap());
    // Leaves one in init, which the cleanup after init must kill before plan looks, and one in
    // plan, which the release must kill.
    agent
        .manager
        .accept(preview("d1", json!({ "TF_VAR_detach": detach })));
    let (logs, results) = run_until_done(&agent, &["d1"]).await;
    let first = logs["d1"].join("\n");
    assert_eq!(results[0].outcome(), Outcome::Succeeded, "{:?}", results[0]);
    let seen: Vec<&str> = first
        .lines()
        .filter(|l| l.starts_with("survivor "))
        .collect();
    assert_eq!(
        seen.first(),
        Some(&"survivor alive"),
        "the shape was made: {first}"
    );
    assert_eq!(
        seen.get(1),
        Some(&"survivor gone"),
        "the thread left by init outlived the tool run: {first}"
    );
    let survivors = processes_of_pool(&agent);
    assert!(survivors.is_empty(), "outlived the attempt: {survivors:#?}");

    // The next attempt gets the same user, and finds nothing of the last one's running.
    agent
        .manager
        .accept(preview("d2", json!({ "TF_VAR_survivors": "1" })));
    let (logs, _) = run_until_done(&agent, &["d2"]).await;
    let second = logs["d2"].join("\n");
    let uid = |log: &str| {
        log.lines()
            .find_map(|l| l.strip_prefix("ids "))
            .map(str::to_owned)
    };
    let used = processes_of_pool(&agent);
    assert!(used.is_empty(), "{used:#?}");
    assert!(second.contains("survivor gone"), "{second}");
    let d1_uid = std::fs::metadata(agent.work.join("d1/out")).map(|m| m.uid().to_string());
    assert_eq!(uid(&second), d1_uid.ok(), "the same user ran both");
}

#[tokio::test]
#[ignore = "needs root and the tool users: tests/tool-users/run.sh"]
async fn a_user_whose_sweep_another_user_exhausts_is_not_leased_with_its_files() {
    assert!(users::is_root(), "{NEEDS_ROOT}");
    let dir = tempfile::tempdir().unwrap();
    let gone = dir.path().join("gone");
    let pool = Arc::new(UserPool::discover(Path::new("/nonexistent")).unwrap());
    pool.keep_out_of_sweeps([dir.path().to_path_buf()]);
    pool.set_sweep_limit(2_000);
    let hog = pool.lease("opentofu").await.unwrap().unwrap();
    let victim = pool.lease("opentofu").await.unwrap().unwrap();
    let (a, b) = (hog.user(), victim.user());

    // A, still running, keeps more entries than a sweep looks at, in a folder of its own that the
    // victim's sweep reaches before /tmp.
    let heap = PathBuf::from(format!("/dev/shm/octopushy-test-hog-{}", a.uid));
    std::fs::create_dir(&heap).unwrap();
    for n in 0..5_000 {
        std::fs::write(heap.join(n.to_string()), "").unwrap();
        std::os::unix::fs::lchown(heap.join(n.to_string()), Some(a.uid), Some(a.gid)).unwrap();
    }
    std::os::unix::fs::lchown(&heap, Some(a.uid), Some(a.gid)).unwrap();
    // B leaves a credential where the next attempt of its user would find it.
    let credential = PathBuf::from(format!("/tmp/octopushy-test-credential-{}", b.uid));
    std::fs::write(&credential, "B's secret").unwrap();
    std::os::unix::fs::lchown(&credential, Some(b.uid), Some(b.gid)).unwrap();
    std::fs::set_permissions(&credential, std::fs::Permissions::from_mode(0o600)).unwrap();

    victim.release(&gone).await;
    assert!(
        pool.quarantined().contains(&b.uid),
        "B's sweep ran out, yet B is free"
    );
    assert!(credential.exists());

    // Every other user can be leased; B cannot, while A keeps its heap.
    let mut others = Vec::new();
    while let Ok(lease) = tokio::time::timeout(Duration::from_secs(2), pool.lease("opentofu")).await
    {
        let lease = lease.unwrap().unwrap();
        assert_ne!(lease.user().uid, b.uid, "B was leased with its file left");
        others.push(lease);
    }
    assert_eq!(others.len(), pool.size() - 2);

    // Once A is gone with its heap, B's cleanup succeeds: B comes back without its file.
    hog.release(&gone).await;
    assert!(!heap.exists());
    let a_again = pool.lease("opentofu").await.unwrap().unwrap();
    assert_eq!(a_again.user(), a);
    let b_again = tokio::time::timeout(Duration::from_secs(10), pool.lease("opentofu"))
        .await
        .expect("B is clean again")
        .unwrap()
        .unwrap();
    assert_eq!(b_again.user(), b);
    assert!(
        !credential.exists(),
        "B was leased again with its file left"
    );
    for lease in others.into_iter().chain([a_again, b_again]) {
        lease.release(&gone).await;
    }
    assert!(pool.quarantined().is_empty());
}

/// Becomes a process `/proc` shows as the given user's that the user cannot signal: real and saved
/// user nobody, effective user the one given. The kill a cleanup makes as that user fails on it,
/// as on a process that cannot be killed.
const STUCK_C: &str = r#"
#define _GNU_SOURCE
#include <stdlib.h>
#include <unistd.h>
int main(int argc, char **argv) {
    uid_t uid = (uid_t)atoi(argv[1]);
    if (argc != 2 || setresgid(65534, 65534, 65534) || setresuid(65534, uid, 65534)) return 1;
    for (;;) pause();
}
"#;

fn compile(dir: &Path, name: &str, source: &str) -> PathBuf {
    let c = dir.join(format!("{name}.c"));
    let program = dir.join("bin").join(name);
    std::fs::write(&c, source).unwrap();
    let built = std::process::Command::new("cc")
        .args(["-pthread", "-o"])
        .arg(&program)
        .arg(&c)
        .status()
        .unwrap();
    assert!(built.success(), "cc {name} failed");
    std::fs::set_permissions(&program, std::fs::Permissions::from_mode(0o755)).unwrap();
    program
}

/// Waits for the attempt's tool to say which user it stalls as, then leaves a process of that
/// user's which the user cannot kill. Returns the user and the process.
async fn stall(
    agent: &Agent,
    stuck: &Path,
    cursor: &mut SessionCursor,
    id: &str,
) -> (u32, std::process::Child) {
    let uid = tokio::time::timeout(Duration::from_secs(30), async {
        loop {
            for message in agent.manager.collect(cursor) {
                if let Some(Kind::Log(log)) = message.kind
                    && log.attempt_id == id
                {
                    for line in log.lines {
                        if let Some(uid) = line.text.strip_prefix("stalling as ") {
                            return uid.trim().parse::<u32>().unwrap();
                        }
                    }
                }
            }
            tokio::time::sleep(Duration::from_millis(20)).await;
        }
    })
    .await
    .expect("the tool stalls");
    let child = std::process::Command::new(stuck)
        .arg(uid.to_string())
        .spawn()
        .unwrap();
    (uid, child)
}

#[tokio::test]
#[ignore = "needs root and the tool users: tests/tool-users/run.sh"]
async fn a_survivor_that_cannot_be_killed_stops_the_attempt() {
    let agent = agent(2);
    let stuck = compile(agent.state.parent().unwrap(), "stuck", STUCK_C);

    // A read: the survivor appears during init, so plan never runs, and the preview fails.
    agent
        .manager
        .accept(preview("s1", json!({ "TF_VAR_stall": "init" })));
    let mut cursor = SessionCursor::default();
    let (uid1, mut stuck1) = stall(&agent, &stuck, &mut cursor, "s1").await;
    let (logs, results) = run_until_done(&agent, &["s1"]).await;
    let log = logs.get("s1").map(|l| l.join("\n")).unwrap_or_default();
    let r = &results[0];
    assert_eq!(r.outcome(), Outcome::Failed, "{r:?}");
    assert!(r.error.contains("outlived a tool run"), "{}", r.error);
    assert!(
        !log.contains("$ tofu plan"),
        "a tool ran after the survivor: {log}"
    );
    assert!(
        agent
            .exec
            .runtime
            .users
            .as_ref()
            .unwrap()
            .quarantined()
            .contains(&uid1)
    );

    // A change: the survivor appears during apply, so output never runs, and the apply is
    // uncertain, with no state.
    let plan = archive::pack(&[("plan.bin", b"PLAN".as_slice())]).unwrap();
    let mut apply = preview("s2", json!({ "TF_VAR_stall": "apply" }));
    apply.action = "apply".into();
    apply.artifacts = vec![octopushy_proto::v1::Artifact {
        name: "plan.tar.gz".into(),
        sha256: octopushy::tls::sha256_hex(&plan),
        content: plan,
    }];
    agent.manager.accept(apply);
    let mut cursor = SessionCursor::default();
    let (uid2, mut stuck2) = stall(&agent, &stuck, &mut cursor, "s2").await;
    assert_ne!(uid2, uid1, "a quarantined user ran another attempt");
    let (logs, results) = run_until_done(&agent, &["s2"]).await;
    let log = logs.get("s2").map(|l| l.join("\n")).unwrap_or_default();
    let r = &results[0];
    assert_eq!(r.outcome(), Outcome::Uncertain, "{r:?}");
    assert!(
        r.artifacts.is_empty(),
        "state reported from an untrusted apply: {:?}",
        r.artifacts
    );
    assert!(
        !log.contains("$ tofu output"),
        "a tool ran after the survivor: {log}"
    );
    assert!(
        agent
            .exec
            .runtime
            .users
            .as_ref()
            .unwrap()
            .quarantined()
            .contains(&uid2)
    );

    for child in [&mut stuck1, &mut stuck2] {
        child.kill().unwrap();
        child.wait().unwrap();
    }
}
