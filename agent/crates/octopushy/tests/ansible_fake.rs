//! The ansible executor with the `local` runtime and a fake `octopushy-ansible` that records
//! what it was given and writes the recap a real run would.

use std::collections::HashMap;
use std::path::PathBuf;
use std::sync::Arc;
use std::time::Duration;

use octopushy::attempts::logs::{AttemptLogger, LogBuffer};
use octopushy::executors::opentofu::archive;
use octopushy::executors::opentofu::state::Snapshots;
use octopushy::executors::{self, ExecContext, ExecutorConfig, Failure, Secrets, Success};
use octopushy::masking::Masker;
use octopushy::tools::{RuntimeKind, ToolRuntime};
use octopushy_proto::v1::Artifact;
use serde_json::{Value, json};
use tokio::sync::Notify;
use tokio_util::sync::CancellationToken;

const SSH_KEY: &str = "-----BEGIN OPENSSH PRIVATE KEY-----\nb3BlbnNzaC1rZXktdjEAAAAA\n-----END OPENSSH PRIVATE KEY-----\n";
const PASSWORD: &str = "tail\"scale\\secret";
/// Values that test how a recap is read: one with the name of a field in it, and one that
/// JSON escapes differently from how it was given.
const FIELD_NAMES: [&str; 2] = ["unreachable", "tasks"];
const UNICODE: &str = "pässwörd";
const HOST_KEY: &str =
    "ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIL4yMxTo2ZC6CYBxvdtnHHtR5HBTjQeL+w9jI3rj+PyY";

const FAKE: &str = r#"#!/bin/bash
set -u
REC="@REC@"
printf '%s\n' "$*" >> "$REC/args.log"
{ echo "=== $*"; env | sort; } >> "$REC/env.log"
if [ "$1" = "--version" ]; then echo "ansible-playbook [core 2.21.5]"; exit 0; fi
for a in "$@"; do
  case "$a" in
    --syntax-check) [ -f "$REC/syntax-fails" ] && { echo "ERROR! bad yaml" >&2; exit 4; }; exit 0 ;;
    --list-tasks) printf 'playbook: site.yml\n\n  play #1 (all): Configure\n    tasks:\n      Install tools\n'; exit 0 ;;
  esac
done
if [ -n "${OCTOPUSHY_PREPARED_FILE:-}" ]; then
  cp "$REC/prepared.json" "$OCTOPUSHY_PREPARED_FILE"
  exit 0
fi
if [ -n "${OCTOPUSHY_STDIN:-}" ]; then cat > "$REC/stdin.json"; fi
if [ -f "$REC/sleep" ]; then sleep "$(cat "$REC/sleep")"; fi
if [ -f "$REC/kill" ]; then kill -9 $$; fi
echo "output with $(cat "$REC/echo" 2>/dev/null)"
if [ -f "$REC/recap.json" ]; then echo "octopushy-recap: $(cat "$REC/recap.json")" >&2; fi
exit "$(cat "$REC/exit" 2>/dev/null || echo 0)"
"#;

struct Fixture {
    dir: tempfile::TempDir,
    rec: PathBuf,
    cfg: ExecutorConfig,
}

impl Fixture {
    fn new() -> Self {
        let dir = tempfile::tempdir().unwrap();
        let rec = dir.path().join("rec");
        std::fs::create_dir_all(&rec).unwrap();
        let program = dir.path().join("bin/octopushy-ansible");
        std::fs::create_dir_all(program.parent().unwrap()).unwrap();
        std::fs::write(&program, FAKE.replace("@REC@", &rec.to_string_lossy())).unwrap();
        use std::os::unix::fs::PermissionsExt;
        std::fs::set_permissions(&program, std::fs::Permissions::from_mode(0o755)).unwrap();
        let mut runtime = ToolRuntime::new(RuntimeKind::Local, None, "false".into(), None);
        runtime.ansible_binary = program;
        runtime.kill_grace = Duration::from_secs(1);
        let cfg = ExecutorConfig {
            runtime,
            provider_cache: dir.path().join("cache"),
            poll_interval: Duration::from_millis(10),
            proxmox_timeout: Duration::from_secs(1),
        };
        Self { dir, rec, cfg }
    }

    fn set(&self, name: &str, content: &str) {
        std::fs::write(self.rec.join(name), content).unwrap();
    }

    fn read(&self, name: &str) -> String {
        std::fs::read_to_string(self.rec.join(name)).unwrap_or_default()
    }
}

static LONG: std::sync::LazyLock<String> = std::sync::LazyLock::new(|| "L0ng".repeat(180));

fn repo() -> Vec<u8> {
    archive::pack(&[
        ("modules/ansible/x/site.yml", b"- hosts: all\n" as &[u8]),
        ("modules/ansible/x/prepare.yml", b"- hosts: localhost\n"),
        ("labs/home/proxmox/p/guests/g/note.txt", b"guest file\n"),
    ])
    .unwrap()
}

fn input() -> Value {
    json!({
        "root": "modules/ansible/x",
        "action": "configure",
        "playbook": "site.yml",
        "host": { "name": "g", "address": "10.0.0.5", "port": 22, "user": "ops", "hostKey": HOST_KEY },
        "vars": { "greeting": "hello" },
        "secretVars": { "tailscale_key": "ansible.var.tailscale_key" },
        "secretFiles": { "git_key_src": "ansible.file.git_key_src" },
        "sshKey": "ansible.ssh-key",
        "context": { "address": "home/p/g", "commit": "abc", "action": "configure" },
        "guestDirectory": "labs/home/proxmox/p/guests/g",
        "outcomes": ["deferred"],
        "approvedVars": {},
        "manifest": { "commit": "abc" },
        "someFutureField": true,
    })
}

fn secrets() -> Secrets {
    Secrets::new(HashMap::from([
        ("ansible.ssh-key".to_owned(), SSH_KEY.to_owned()),
        ("ansible.var.tailscale_key".to_owned(), PASSWORD.to_owned()),
        ("ansible.file.git_key_src".to_owned(), "git-key".to_owned()),
    ]))
}

fn preview_artifact(prepared: Value) -> Artifact {
    let content = serde_json::to_vec(&json!({ "schemaVersion": 1, "prepared": prepared })).unwrap();
    let sha256 = octopushy::tls::sha256_hex(&content);
    Artifact {
        name: "preview.json".into(),
        content,
        sha256,
    }
}

struct Finished {
    result: Result<Success, Failure>,
    log: String,
    workspace: PathBuf,
}

async fn exec(
    fx: &Fixture,
    action: &str,
    input: Value,
    secrets: Secrets,
    artifacts: Vec<Artifact>,
) -> Finished {
    let workspace = fx
        .dir
        .path()
        .join("work")
        .join(fastrand::u32(..).to_string());
    std::fs::create_dir_all(&workspace).unwrap();
    let buffer = Arc::new(LogBuffer::default());
    let log = AttemptLogger::new(
        buffer.clone(),
        Arc::new(Masker::new(
            [SSH_KEY, PASSWORD, UNICODE, LONG.as_str()]
                .into_iter()
                .chain(FIELD_NAMES),
        )),
        Arc::new(Notify::new()),
    );
    let ctx = ExecContext {
        attempt_id: format!("attempt-{}", fastrand::u32(..)),
        action: action.into(),
        input_json: input.to_string(),
        secrets,
        files_tar_gz: repo(),
        artifacts,
        workspace: workspace.clone(),
        log,
        cancel: CancellationToken::new(),
        snapshots: Snapshots::default(),
    };
    let result = executors::run(&fx.cfg, "ansible", &ctx).await;
    let mut log = String::new();
    let mut next = 0;
    while let Some((first, lines)) = buffer.batch_from(next, 100) {
        next = first + lines.len() as u64;
        for line in lines {
            log.push_str(&line.text);
            log.push('\n');
        }
    }
    Finished {
        result,
        log,
        workspace,
    }
}

async fn run(fx: &Fixture) -> Finished {
    exec(
        fx,
        "run",
        input(),
        secrets(),
        vec![preview_artifact(Value::Null)],
    )
    .await
}

fn recap(reached: bool) -> Value {
    json!({
        "reached": reached, "tasks": 3,
        "stats": { "ok": 2, "changed": 1, "unreachable": 0, "failures": 0, "skipped": 0, "rescued": 0, "ignored": 0 },
    })
}

fn message(result: &Result<Success, Failure>) -> String {
    match result {
        Ok(_) => panic!("expected a failure"),
        Err(failure) => failure.message().to_owned(),
    }
}

#[tokio::test]
async fn preview_has_no_secrets_and_reports_what_will_run() {
    let fx = Fixture::new();
    let mut with_prepare = input();
    with_prepare["prepare"] = json!("prepare.yml");
    fx.set(
        "prepared.json",
        r#"{"tools":[{"name":"t3","version":"1.2.3"}]}"#,
    );
    let done = exec(
        &fx,
        "preview",
        with_prepare,
        Secrets::new(HashMap::new()),
        Vec::new(),
    )
    .await;
    let success = done.result.unwrap();
    assert_eq!(success.outputs["action"], "configure");
    assert_eq!(success.outputs["prepared"]["tools"][0]["version"], "1.2.3");
    assert_eq!(
        success.outputs["toolVersion"],
        "ansible-playbook [core 2.21.5]"
    );
    assert!(
        success.outputs["hostKeyFingerprints"][0]
            .as_str()
            .unwrap()
            .starts_with("ssh-ed25519 SHA256:")
    );
    let tasks = success.outputs["tasks"].to_string();
    assert!(tasks.contains("Install tools"), "{tasks}");
    assert_eq!(success.artifacts.len(), 1);
    assert_eq!(success.artifacts[0].name, "preview.json");
    let args = fx.read("args.log");
    assert!(args.contains("--syntax-check"), "{args}");
    assert!(args.contains("--list-tasks"), "{args}");
    assert!(args.contains("-c local"), "{args}");
    assert!(
        !args.contains("--check"),
        "check mode is not a preview: {args}"
    );
    assert!(
        fx.read("stdin.json").is_empty(),
        "a preview gets no secrets"
    );
    assert!(!fx.read("env.log").contains("OCTOPUSHY_STDIN"));
}

#[tokio::test]
async fn a_prepare_step_cannot_set_what_pins_the_connection() {
    let fx = Fixture::new();
    let mut with_prepare = input();
    with_prepare["prepare"] = json!("prepare.yml");
    fx.set(
        "prepared.json",
        r#"{"ansible_ssh_args":"-o StrictHostKeyChecking=no"}"#,
    );
    let done = exec(&fx, "preview", with_prepare, secrets(), Vec::new()).await;
    assert!(message(&done.result).contains("ansible_ssh_args"));
}

#[tokio::test]
async fn a_snapshot_is_reused_without_preparing() {
    let fx = Fixture::new();
    let mut reused = input();
    reused["prepared"] = json!({ "tools": ["from-last-run"] });
    let done = exec(
        &fx,
        "preview",
        reused,
        Secrets::new(HashMap::new()),
        Vec::new(),
    )
    .await;
    assert_eq!(
        done.result.unwrap().outputs["prepared"]["tools"][0],
        "from-last-run"
    );
}

#[tokio::test]
async fn a_broken_playbook_fails_the_preview() {
    let fx = Fixture::new();
    fx.set("syntax-fails", "");
    let done = exec(
        &fx,
        "preview",
        input(),
        Secrets::new(HashMap::new()),
        Vec::new(),
    )
    .await;
    assert!(message(&done.result).contains("bad yaml"));
}

#[tokio::test]
async fn secrets_reach_the_tool_on_stdin_and_nowhere_else() {
    let fx = Fixture::new();
    fx.set("recap.json", &recap(true).to_string());
    fx.set("echo", PASSWORD);
    let done = run(&fx).await;
    let success = done.result.unwrap();
    assert_eq!(success.outputs["recap"]["changed"], 1);

    let stdin: Value = serde_json::from_str(&fx.read("stdin.json")).unwrap();
    assert_eq!(stdin["sshKey"], SSH_KEY);
    assert_eq!(stdin["secretVars"]["tailscale_key"], PASSWORD);
    assert_eq!(stdin["secretFiles"]["git_key_src"], "git-key");

    let args = fx.read("args.log");
    let env = fx.read("env.log");
    for leak in ["b3BlbnNzaC1rZXk", "scale", "git-key"] {
        assert!(!args.contains(leak), "argv: {args}");
        assert!(!env.contains(leak), "env: {env}");
    }
    assert!(!done.log.contains("scale"), "{}", done.log);
    assert!(done.log.contains("output with ***"), "{}", done.log);

    // Nothing written to the workspace holds one.
    let mut stack = vec![done.workspace.clone()];
    while let Some(dir) = stack.pop() {
        for entry in std::fs::read_dir(dir).unwrap() {
            let path = entry.unwrap().path();
            if path.is_dir() {
                stack.push(path);
            } else if let Ok(text) = std::fs::read_to_string(&path) {
                assert!(
                    !text.contains("scale") && !text.contains("b3BlbnNzaC1rZXk"),
                    "{path:?}"
                );
            }
        }
    }
}

#[tokio::test]
async fn the_connection_is_pinned_and_comes_last() {
    let fx = Fixture::new();
    fx.set("recap.json", &recap(true).to_string());
    let done = run(&fx).await;
    done.result.unwrap();
    let args = fx.read("args.log");
    let vars = args.find("vars.json").unwrap();
    let connection = args.find("connection.json").unwrap();
    assert!(vars < connection, "{args}");
    let generated = done.workspace.join("ansible");
    let connection: Value =
        serde_json::from_str(&std::fs::read_to_string(generated.join("connection.json")).unwrap())
            .unwrap();
    assert_eq!(connection["ansible_host"], "10.0.0.5");
    assert_eq!(connection["ansible_user"], "ops");
    assert_eq!(connection["ansible_host_key_checking"], true);
    assert_eq!(connection["ansible_ssh_host_key_checking"], true);
    for (var, path) in [
        ("ansible_ssh_executable", "/usr/bin/ssh"),
        ("ansible_scp_executable", "/usr/bin/scp"),
        ("ansible_sftp_executable", "/usr/bin/sftp"),
    ] {
        assert_eq!(connection[var], path);
    }
    let ssh = connection["ansible_ssh_args"].as_str().unwrap();
    assert!(ssh.contains("StrictHostKeyChecking=yes"), "{ssh}");
    assert!(ssh.contains("GlobalKnownHostsFile=/dev/null"), "{ssh}");
    assert!(ssh.contains("ControlMaster=no"), "{ssh}");
    let known = std::fs::read_to_string(generated.join("known_hosts")).unwrap();
    assert!(known.starts_with("10.0.0.5 ssh-ed25519 AAAA"), "{known}");
    assert!(
        connection["octopushy"]["guest_directory"]
            .as_str()
            .unwrap()
            .ends_with("labs/home/proxmox/p/guests/g")
    );
}

#[tokio::test]
async fn the_approved_values_are_what_the_run_receives() {
    let fx = Fixture::new();
    fx.set("recap.json", &recap(true).to_string());
    let mut approved = input();
    approved["approvedVars"] = json!({ "allow_busy": true });
    let done = exec(
        &fx,
        "run",
        approved,
        secrets(),
        vec![preview_artifact(json!({ "tools": ["1.0"] }))],
    )
    .await;
    done.result.unwrap();
    let generated = done.workspace.join("ansible");
    assert!(
        std::fs::read_to_string(generated.join("prepared.json"))
            .unwrap()
            .contains("1.0")
    );
    assert!(
        std::fs::read_to_string(generated.join("approved.json"))
            .unwrap()
            .contains("allow_busy")
    );
}

#[tokio::test]
async fn a_changed_preview_is_refused() {
    let fx = Fixture::new();
    let mut artifact = preview_artifact(Value::Null);
    artifact.sha256 = "0".repeat(64);
    let done = exec(&fx, "run", input(), secrets(), vec![artifact]).await;
    assert!(message(&done.result).contains("corrupt"));
    assert!(fx.read("args.log").is_empty(), "nothing ran");
}

#[tokio::test]
async fn a_run_without_a_preview_is_refused() {
    let fx = Fixture::new();
    let done = exec(&fx, "run", input(), secrets(), Vec::new()).await;
    assert!(message(&done.result).contains("preview.json"));
    assert!(fx.read("args.log").is_empty(), "nothing ran");
}

#[tokio::test]
async fn a_failed_task_is_a_known_failure() {
    let fx = Fixture::new();
    let mut failed = recap(true);
    failed["failedTask"] = json!("Install tools");
    fx.set("recap.json", &failed.to_string());
    fx.set("exit", "2");
    let done = run(&fx).await;
    match done.result {
        Err(Failure::Failed { message, outputs }) => {
            assert!(message.contains("Install tools"), "{message}");
            assert_eq!(outputs.unwrap()["failedTask"], "Install tools");
        }
        other => panic!("{other:?}"),
    }
}

#[tokio::test]
async fn a_tool_that_never_started_is_a_known_failure() {
    for code in ["78", "125", "126", "127"] {
        let fx = Fixture::new();
        fx.set("exit", code);
        let done = run(&fx).await;
        assert!(matches!(done.result, Err(Failure::Failed { .. })), "{code}");
    }
}

#[tokio::test]
async fn a_run_that_left_no_recap_is_uncertain() {
    let fx = Fixture::new();
    fx.set("exit", "1");
    let done = run(&fx).await;
    assert!(matches!(done.result, Err(Failure::Uncertain(_))));
}

#[tokio::test]
async fn a_run_that_exits_zero_with_no_recap_is_uncertain() {
    let fx = Fixture::new();
    let done = run(&fx).await;
    assert!(
        matches!(done.result, Err(Failure::Uncertain(_))),
        "{:?}",
        done.result
    );
}

#[tokio::test]
async fn a_secret_named_like_a_field_cannot_change_what_a_recap_says() {
    let fx = Fixture::new();
    let mut lost = recap(true);
    lost["outcome"] = json!("deferred");
    lost["unreachable"] = json!("Shared connection closed");
    fx.set("recap.json", &lost.to_string());
    fx.set("exit", "4");
    let done = run(&fx).await;
    assert!(
        matches!(done.result, Err(Failure::Uncertain(_))),
        "{:?}",
        done.result
    );
    assert!(!done.log.contains("octopushy-recap"), "{}", done.log);
}

#[tokio::test]
async fn a_long_secret_in_a_task_name_is_masked_whole_before_it_is_cut() {
    let fx = Fixture::new();
    let mut failed = recap(true);
    failed["failedTask"] = json!(format!("Connect with {}", *LONG));
    fx.set("recap.json", &failed.to_string());
    fx.set("exit", "2");
    let done = run(&fx).await;
    match done.result {
        Err(Failure::Failed { message, outputs }) => {
            assert!(!message.contains("L0ng"), "{message}");
            assert_eq!(outputs.unwrap()["failedTask"], "Connect with ***");
        }
        other => panic!("{other:?}"),
    }
    assert!(!done.log.contains("L0ng"), "{}", done.log);
}

#[tokio::test]
async fn a_secret_json_escaped_in_a_recap_is_masked_as_itself() {
    let fx = Fixture::new();
    fx.set(
        "recap.json",
        r#"{"reached":true,"tasks":1,"failedTask":"Install p\u00e4ssw\u00f6rd"}"#,
    );
    fx.set("exit", "2");
    let done = run(&fx).await;
    match done.result {
        Err(Failure::Failed { outputs, .. }) => {
            assert_eq!(outputs.unwrap()["failedTask"], "Install ***");
        }
        other => panic!("{other:?}"),
    }
    assert!(!done.log.contains("00e4"), "{}", done.log);
}

#[tokio::test]
async fn a_guest_lost_in_a_task_the_play_ignored_leaves_an_outcome_uncertain() {
    let fx = Fixture::new();
    let mut lost = recap(true);
    lost["outcome"] = json!("deferred");
    lost["unreachable"] = json!("Shared connection closed");
    fx.set("recap.json", &lost.to_string());
    let done = run(&fx).await;
    assert!(
        matches!(done.result, Err(Failure::Uncertain(_))),
        "{:?}",
        done.result
    );
}

#[tokio::test]
async fn a_guest_lost_in_a_task_the_play_ignored_is_uncertain_with_no_outcome_either() {
    let fx = Fixture::new();
    let mut lost = recap(true);
    lost["unreachable"] = json!("Shared connection closed");
    fx.set("recap.json", &lost.to_string());
    let done = run(&fx).await;
    assert!(
        matches!(done.result, Err(Failure::Uncertain(_))),
        "{:?}",
        done.result
    );
}

#[tokio::test]
async fn an_undeclared_outcome_is_never_repeated() {
    let fx = Fixture::new();
    let mut surprise = recap(true);
    surprise["outcome"] = json!("hidden\u{200b}password");
    fx.set("recap.json", &surprise.to_string());
    let done = run(&fx).await;
    let text = format!("{:?} {}", done.result, done.log);
    assert!(!text.contains("hidden"), "{text}");
    assert!(!text.contains("200b"), "{text}");
    assert!(message(&done.result).contains("does not declare"));
}

#[tokio::test]
async fn ssh_text_is_masked_before_it_is_cut() {
    let fx = Fixture::new();
    let mut refused = recap(false);
    refused["unreachable"] = json!(format!("{} Permission denied (publickey).", *LONG));
    fx.set("recap.json", &refused.to_string());
    fx.set("exit", "4");
    let done = run(&fx).await;
    match done.result {
        Err(Failure::Failed { message, .. }) => {
            assert!(!message.contains("L0ng"), "{message}");
            assert!(
                message.ends_with("*** Permission denied (publickey)."),
                "{message}"
            );
        }
        other => panic!("{other:?}"),
    }
}

#[tokio::test]
async fn a_guest_lost_after_an_outcome_was_reported_is_uncertain() {
    let fx = Fixture::new();
    let mut lost = recap(true);
    lost["outcome"] = json!("deferred");
    lost["unreachable"] = json!("Shared connection to 10.0.0.5 closed.");
    lost["unreachableTask"] = json!("Later task");
    fx.set("recap.json", &lost.to_string());
    fx.set("exit", "4");
    let done = run(&fx).await;
    assert!(
        matches!(done.result, Err(Failure::Uncertain(_))),
        "{:?}",
        done.result
    );
}

#[tokio::test]
async fn a_secret_in_a_task_name_is_masked_in_what_is_kept() {
    let fx = Fixture::new();
    let mut failed = recap(true);
    failed["failedTask"] = json!(format!("Connect with {PASSWORD}"));
    fx.set("recap.json", &failed.to_string());
    fx.set("exit", "2");
    let done = run(&fx).await;
    match done.result {
        Err(Failure::Failed { outputs, .. }) => {
            let outputs = outputs.unwrap().to_string();
            assert!(!outputs.contains("scale"), "{outputs}");
        }
        other => panic!("{other:?}"),
    }
    assert!(!done.log.contains("scale"), "{}", done.log);
    let mut stack = vec![done.workspace.clone()];
    while let Some(dir) = stack.pop() {
        for entry in std::fs::read_dir(dir).unwrap() {
            let path = entry.unwrap().path();
            if path.is_dir() {
                stack.push(path);
            } else if let Ok(text) = std::fs::read_to_string(&path) {
                assert!(!text.contains("scale"), "{path:?}");
            }
        }
    }
}

#[tokio::test]
async fn a_secret_in_the_task_a_guest_was_lost_on_is_masked() {
    let fx = Fixture::new();
    let mut lost = recap(false);
    lost["unreachable"] = json!("Connection refused");
    lost["unreachableTask"] = json!(format!("Connect with {PASSWORD}"));
    fx.set("recap.json", &lost.to_string());
    fx.set("exit", "4");
    let done = run(&fx).await;
    match done.result {
        Err(Failure::Failed { outputs, .. }) => {
            let outputs = outputs.unwrap().to_string();
            assert!(!outputs.contains("scale"), "{outputs}");
        }
        other => panic!("{other:?}"),
    }
}

#[tokio::test]
async fn a_killed_run_is_uncertain() {
    let fx = Fixture::new();
    fx.set("recap.json", &recap(true).to_string());
    fx.set("kill", "");
    let done = run(&fx).await;
    match done.result {
        Err(Failure::Uncertain(message)) => assert!(message.contains("any lock"), "{message}"),
        other => panic!("{other:?}"),
    }
}

#[tokio::test]
async fn a_guest_lost_during_a_task_is_uncertain() {
    let fx = Fixture::new();
    let mut lost = recap(true);
    lost["unreachable"] = json!("Shared connection to 10.0.0.5 closed.");
    lost["unreachableTask"] = json!("Install tools");
    fx.set("recap.json", &lost.to_string());
    fx.set("exit", "4");
    let done = run(&fx).await;
    assert!(matches!(done.result, Err(Failure::Uncertain(_))));
}

#[tokio::test]
async fn a_refused_host_key_is_a_failure_that_says_so() {
    let fx = Fixture::new();
    let mut refused = recap(false);
    refused["unreachable"] = json!("Host key verification failed.");
    fx.set("recap.json", &refused.to_string());
    fx.set("exit", "4");
    let done = run(&fx).await;
    match done.result {
        Err(Failure::Failed { message, .. }) => {
            assert!(message.contains("not the pinned one"), "{message}");
            assert!(message.contains("SHA256:"), "{message}");
        }
        other => panic!("{other:?}"),
    }
}

#[tokio::test]
async fn a_failed_login_gives_ssh_s_reason() {
    let fx = Fixture::new();
    let mut refused = recap(false);
    refused["unreachable"] =
        json!("a banner\r\nmore banner\r\nops@g: Permission denied (publickey).\r\n");
    fx.set("recap.json", &refused.to_string());
    fx.set("exit", "4");
    let done = run(&fx).await;
    match done.result {
        Err(Failure::Failed { message, .. }) => {
            assert!(
                message.ends_with("Permission denied (publickey)."),
                "{message}"
            );
            assert!(!message.contains("banner"), "{message}");
        }
        other => panic!("{other:?}"),
    }
}

#[tokio::test]
async fn a_declared_outcome_is_not_a_failure() {
    let fx = Fixture::new();
    let mut deferred = recap(true);
    deferred["outcome"] = json!("deferred");
    deferred["detail"] = json!(format!("Three threads, {PASSWORD}"));
    fx.set("recap.json", &deferred.to_string());
    fx.set("exit", "2");
    let done = run(&fx).await;
    let success = done.result.unwrap();
    assert_eq!(success.outputs["outcome"], "deferred");
    assert_eq!(success.outputs["detail"], "Three threads, ***");
}

#[tokio::test]
async fn an_undeclared_outcome_is_a_failure() {
    let fx = Fixture::new();
    let mut surprise = recap(true);
    surprise["outcome"] = json!("surprise");
    fx.set("recap.json", &surprise.to_string());
    let done = run(&fx).await;
    assert!(message(&done.result).contains("does not declare"));
}

#[tokio::test]
async fn a_run_past_its_limit_is_stopped_and_uncertain() {
    let fx = Fixture::new();
    let mut quick = input();
    quick["timeoutMinutes"] = json!(0);
    fx.set("sleep", "30");
    let done = exec(
        &fx,
        "run",
        quick,
        secrets(),
        vec![preview_artifact(Value::Null)],
    )
    .await;
    match done.result {
        Err(Failure::Uncertain(message)) => assert!(message.contains("limit"), "{message}"),
        other => panic!("{other:?}"),
    }
}

#[tokio::test]
async fn reserved_names_and_bad_pins_are_refused_before_anything_runs() {
    let fx = Fixture::new();
    for (pointer, value) in [
        ("/vars", json!({ "ansible_ssh_common_args": "x" })),
        (
            "/secretVars",
            json!({ "ansible_password": "ansible.ssh-key" }),
        ),
        ("/approvedVars", json!({ "octopushy": 1 })),
        ("/host/hostKey", json!("not a key")),
        ("/root", json!("../outside")),
        ("/playbook", json!("../../../etc/passwd")),
    ] {
        let mut bad = input();
        *bad.pointer_mut(pointer)
            .unwrap_or_else(|| panic!("{pointer}")) = value;
        let done = exec(
            &fx,
            "run",
            bad,
            secrets(),
            vec![preview_artifact(Value::Null)],
        )
        .await;
        assert!(done.result.is_err(), "{pointer}");
    }
    assert!(fx.read("args.log").is_empty(), "nothing ran");
}
