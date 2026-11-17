//! Real OpenTofu in rootless podman. Ignored by default (needs podman and the
//! image); run with:
//!
//! ```sh
//! cargo test -p octopushy --test podman_smoke -- --ignored --nocapture
//! ```

use std::collections::HashMap;
use std::sync::Arc;
use std::time::Duration;

use octopushy::attempts::logs::{AttemptLogger, LogBuffer};
use octopushy::executors::opentofu::{archive, state};
use octopushy::executors::{self, ExecContext, ExecutorConfig, Secrets};
use octopushy::masking::Masker;
use octopushy::tools::{Invocation, RuntimeKind, Tool, ToolRuntime};
use serde_json::json;
use tokio::sync::Notify;
use tokio_util::sync::CancellationToken;

const IMAGE: &str = "ghcr.io/opentofu/opentofu:1.13.1";
const SECRET: &str = "s3cret-env-pa55";

fn logger(secrets: &[&str]) -> (AttemptLogger, Arc<LogBuffer>) {
    let buffer = Arc::new(LogBuffer::default());
    let masker = Arc::new(Masker::new(secrets.iter().copied()));
    (
        AttemptLogger::new(buffer.clone(), masker, Arc::new(Notify::new())),
        buffer,
    )
}

fn lines(buffer: &LogBuffer) -> Vec<String> {
    buffer
        .batch_from(0, usize::MAX)
        .map(|(_, l)| l.into_iter().map(|x| x.text).collect())
        .unwrap_or_default()
}

#[tokio::test]
#[ignore = "needs rootless podman and the OpenTofu image"]
async fn podman_tofu_version() {
    let dir = tempfile::tempdir().unwrap();
    let ws = dir.path().join("work/att-version");
    let cache = dir.path().join("cache");
    std::fs::create_dir_all(ws.join("home")).unwrap();
    std::fs::create_dir_all(&cache).unwrap();
    let rt = ToolRuntime::new(RuntimeKind::Podman, None, "tofu".into(), None);
    let (log, buffer) = logger(&["not-a-real-secret-value"]);
    let out = rt
        .run(
            Invocation {
                tool: Tool::OpenTofu,
                name: format!("octopushy-smoke-{}", fastrand::u32(..)),
                image: IMAGE,
                workspace: &ws,
                workdir: &ws,
                plugin_cache: Some(&cache),
                args: vec!["version".into(), "-json".into()],
                settings: vec![
                    ("HOME".into(), "/work/home".into()),
                    (
                        "TF_PLUGIN_CACHE_DIR".into(),
                        "/octopushy/plugin-cache".into(),
                    ),
                ],
                env: vec![(
                    "TF_VAR_some_secret".into(),
                    "not-a-real-secret-value".into(),
                )],
                stdin: None,
                capture_stdout: true,
                control_prefix: None,
                stderr_mark: None,
            },
            &log,
            &CancellationToken::new(),
        )
        .await
        .unwrap();
    println!("{:#?}", lines(&buffer));
    assert_eq!(out.code, Some(0), "stderr: {:?}", out.stderr_tail);
    let version = octopushy::executors::opentofu::plan::tool_version(&out.stdout);
    println!("tofu version via podman: {version:?}");
    assert_eq!(version.as_deref(), Some("1.13.1"));
}

#[tokio::test]
#[ignore = "needs rootless podman and the OpenTofu image"]
async fn podman_preview_and_apply() {
    let dir = tempfile::tempdir().unwrap();
    let cfg = ExecutorConfig {
        runtime: ToolRuntime::new(RuntimeKind::Podman, None, "tofu".into(), None),
        provider_cache: dir.path().join("cache"),
        poll_interval: Duration::from_secs(1),
        proxmox_timeout: Duration::from_secs(5),
        retry_delay: Duration::from_millis(1),
        server: Default::default(),
        nix: Default::default(),
    };
    let main_tf = br#"
variable "octopushy" { type = any }
variable "guest" { type = any }
resource "terraform_data" "vm" { input = var.guest.vmid }
output "vmid" { value = terraform_data.vm.output }
output "secret" {
  value     = "sensitive-output-value"
  sensitive = true
}
"#;
    let files = archive::pack(&[("modules/opentofu/test-vm/main.tf", main_tf)]).unwrap();
    let input = json!({
        "stack": "home.korriban.test-vm",
        "root": "modules/opentofu/test-vm",
        "image": IMAGE,
        "variables": { "octopushy": { "stack": "home.korriban.test-vm" }, "guest": { "vmid": 4242 } },
        "env": {},
        "secretEnv": { "TF_VAR_unused": "test.secret" },
        "destroy": false
    });
    let secrets = HashMap::from([("test.secret".to_owned(), SECRET.to_owned())]);

    let run = |id: &'static str, action: &'static str, artifacts| {
        let ws = dir.path().join("work").join(id);
        std::fs::create_dir_all(&ws).unwrap();
        octopushy::attempts::workspace::init(&ws, id, "opentofu", action, true).unwrap();
        octopushy::attempts::workspace::mark_running(&ws).unwrap();
        let (log, buffer) = logger(&[SECRET]);
        let ctx = ExecContext {
            attempt_id: id.into(),
            action: action.into(),
            input_json: input.to_string(),
            secrets: Secrets::new(secrets.clone()),
            files_tar_gz: files.clone(),
            artifacts,
            workspace: ws,
            log,
            cancel: CancellationToken::new(),
            mutating: true,
            snapshots: Default::default(),
        };
        let cfg = &cfg;
        async move {
            let result = executors::run(cfg, "opentofu", &ctx).await;
            let lines = lines(&buffer);
            (result, lines)
        }
    };

    let (preview, lines1) = run("smoke-preview", "preview", vec![]).await;
    for l in &lines1 {
        println!("preview | {l}");
    }
    let preview = preview.expect("preview succeeds");
    assert_eq!(preview.outputs["hasChanges"], true);
    assert_eq!(preview.outputs["toolVersion"], "1.13.1");
    assert_eq!(preview.outputs["counts"]["create"], 1);
    assert_eq!(preview.artifacts.len(), 1);
    assert!(lines1.iter().all(|l| !l.contains(SECRET)));

    // HOME points into the workspace, so nothing lands in the root itself.
    let root = dir
        .path()
        .join("work/smoke-preview/repo/modules/opentofu/test-vm");
    assert!(
        !root.join(".terraform.d").exists(),
        "HOME leaked into the root"
    );

    let (apply, lines2) = run("smoke-apply", "apply", preview.artifacts.clone()).await;
    for l in &lines2 {
        println!("apply | {l}");
    }
    let apply = apply.expect("apply succeeds");
    assert_eq!(apply.outputs["outputs"]["vmid"], 4242);
    assert_eq!(apply.outputs["sensitiveOutputs"], json!(["secret"]));
    let stored = state::read(&dir.path().join("work/smoke-apply")).expect("the built-in state");
    assert!(String::from_utf8_lossy(&stored.content).contains("terraform_data"));

    // Files written in the container belong to the agent's user (keep-id).
    use std::os::unix::fs::MetadataExt;
    let plan = dir.path().join("work/smoke-preview/out/plan.bin");
    // SAFETY: getuid has no preconditions.
    assert_eq!(std::fs::metadata(plan).unwrap().uid(), unsafe {
        libc::getuid()
    });
}

/// A real runtime (`OCTOPUSHY_SMOKE_RUNTIME`, podman by default) starts a container with the
/// limits, reads the environment from the in-memory env file, and at startup removes a
/// container this agent left and keeps another agent's.
#[tokio::test]
#[ignore = "needs rootless podman (or docker) and a busybox image"]
async fn real_runtime_limits_env_file_and_leftovers() {
    let kind = match std::env::var("OCTOPUSHY_SMOKE_RUNTIME").as_deref() {
        Ok("docker") => RuntimeKind::Docker,
        _ => RuntimeKind::Podman,
    };
    let image = "docker.io/library/busybox:1.37";
    let dir = tempfile::tempdir().unwrap();
    let ws = dir.path().join("att-smoke");
    std::fs::create_dir_all(&ws).unwrap();
    let mut rt = ToolRuntime::new(kind, None, "tofu".into(), None);
    let me = format!("smoke-{}", fastrand::u32(..));
    rt.owner = Some(me.clone());
    rt.pids_limit = Some(64);
    rt.memory_limit = Some("256m".into());
    let (log, buffer) = logger(&[SECRET]);
    let out = rt
        .run(
            Invocation {
                tool: Tool::OpenTofu,
                name: format!("octopushy-{me}-env"),
                image,
                workspace: &ws,
                workdir: &ws,
                plugin_cache: None,
                args: vec![
                    "sh".into(),
                    "-c".into(),
                    "echo \"one=$TF_VAR_one\"; echo \"multi=$TF_VAR_multi\" | head -1; \
                     cat /sys/fs/cgroup/pids.max /sys/fs/cgroup/memory.max"
                        .into(),
                ],
                settings: vec![],
                env: vec![
                    ("TF_VAR_one".into(), format!("{SECRET} with = and spaces ")),
                    ("TF_VAR_multi".into(), "first\nsecond".into()),
                ],
                stdin: None,
                capture_stdout: true,
                control_prefix: None,
                stderr_mark: None,
            },
            &log,
            &CancellationToken::new(),
        )
        .await
        .unwrap();
    let stdout = String::from_utf8_lossy(&out.stdout).into_owned();
    println!("{stdout}\n{:#?}", lines(&buffer));
    assert_eq!(out.code, Some(0), "stderr: {:?}", out.stderr_tail);
    assert!(
        stdout.contains(&format!("one={SECRET} with = and spaces \n")),
        "{stdout}"
    );
    assert!(stdout.contains("multi=first\n"), "{stdout}");
    assert!(stdout.contains("\n64\n"), "pids.max: {stdout}");
    assert!(stdout.contains("268435456"), "memory.max: {stdout}");

    // Left behind: one of this agent's, one of another's.
    let bin = kind.as_str();
    let start = |name: &str, owner: &str| {
        let status = std::process::Command::new(bin)
            .args(["run", "-d", "--name", name, "--label"])
            .arg(format!("io.octopushy.agent={owner}"))
            .args([
                "--label",
                "io.octopushy.attempt=att-left",
                image,
                "sleep",
                "600",
            ])
            .stdout(std::process::Stdio::null())
            .status()
            .unwrap();
        assert!(status.success());
    };
    let mine = format!("octopushy-{me}-mine");
    let theirs = format!("octopushy-{me}-theirs");
    start(&mine, &me);
    start(&theirs, &format!("{me}-other"));
    let exists = |name: &str| {
        std::process::Command::new(bin)
            .args(["container", "inspect", name])
            .stdout(std::process::Stdio::null())
            .stderr(std::process::Stdio::null())
            .status()
            .unwrap()
            .success()
    };
    let removed = rt.remove_leftovers().await.unwrap();
    let (mine_left, theirs_left) = (exists(&mine), exists(&theirs));
    let _ = std::process::Command::new(bin)
        .args(["rm", "-f", &theirs, &mine])
        .stdout(std::process::Stdio::null())
        .status();
    assert_eq!(removed, 1);
    assert!(!mine_left, "this agent's leftover is removed");
    assert!(theirs_left, "another agent's container stays");
}
