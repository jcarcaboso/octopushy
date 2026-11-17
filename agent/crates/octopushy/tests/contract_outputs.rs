//! Each executor, run on the input of its example in `contracts/agent/v1/examples`, produces the outputs of its example.
//! The tools are fakes that print what the real ones printed for that input, recorded in `tests/fixtures/contract` (see
//! the README there), and Proxmox is mock-proxmox, so what is checked is the executors' own code: how they read the
//! tools and the API, and the outputs they build. `nix_build.rs` checks the `nix` executor's the same way.

mod contract;

use std::collections::HashMap;
use std::os::unix::fs::PermissionsExt;
use std::path::{Path, PathBuf};
use std::sync::Arc;
use std::time::Duration;

use contract::{check_output, input, recorded};
use mock_proxmox::{Fixture, FixtureVm, Options};
use octopushy::attempts::logs::{AttemptLogger, LogBuffer};
use octopushy::executors::opentofu::archive;
use octopushy::executors::{self, ExecContext, ExecutorConfig, Secrets, Success};
use octopushy::masking::Masker;
use octopushy::tools::{RuntimeKind, ToolRuntime};
use octopushy_proto::v1::Artifact;
use serde_json::{Value, json};
use tokio::sync::Notify;
use tokio_util::sync::CancellationToken;

/// Prints what `tofu` 1.13.1 printed for the example's plan of the example lab's `proxmox-vm`.
const FAKE_TOFU: &str = r#"#!/bin/bash
set -u
FIX="@FIX@"
last="${@: -1}"
case "$1" in
  version) cat "$FIX/tofu-version.json" ;;
  init) echo "OpenTofu has been successfully initialized!" ;;
  plan)
    for a in "$@"; do case "$a" in -out=*) echo "a saved plan" > "${a#-out=}" ;; esac; done
    exit 2 ;;
  show) cat "$FIX/tofu-show.json" ;;
  apply)
    state=$(sed -E 's/.*"path":"([^"]*)".*/\1/' octopushy_backend.tf.json)
    echo '{"version":4,"serial":1,"lineage":"contract","outputs":{},"resources":[]}' > "$state"
    echo "Apply complete! Resources: 2 added, 0 changed, 0 destroyed." ;;
  output) cat "$FIX/tofu-output.json" ;;
  *) echo "unknown command $1" >&2; exit 1 ;;
esac
"#;

/// Prints what `ansible-playbook` 2.21.5 printed for the example lab's `machine-tools`, and the recap its callback wrote.
const FAKE_ANSIBLE: &str = r#"#!/bin/bash
set -u
FIX="@FIX@"
if [ "$1" = "--version" ]; then cat "$FIX/ansible-version.txt"; exit 0; fi
for a in "$@"; do
  case "$a" in
    --syntax-check) exit 0 ;;
    --list-tasks) cat "$FIX/ansible-list-tasks.txt"; exit 0 ;;
  esac
done
if [ -n "${OCTOPUSHY_PREPARED_FILE:-}" ]; then cp "$FIX/prepared.json" "$OCTOPUSHY_PREPARED_FILE"; exit 0; fi
if [ -n "${OCTOPUSHY_STDIN:-}" ]; then cat > /dev/null; fi
echo "octopushy-recap: $(cat "@DIR@/recap.json")" >&2
"#;

fn fixtures() -> PathBuf {
    PathBuf::from(env!("CARGO_MANIFEST_DIR")).join("tests/fixtures/contract")
}

fn script(dir: &Path, name: &str, text: &str) -> PathBuf {
    let path = dir.join("bin").join(name);
    std::fs::create_dir_all(path.parent().unwrap()).unwrap();
    std::fs::write(
        &path,
        text.replace("@FIX@", &fixtures().to_string_lossy())
            .replace("@DIR@", &dir.to_string_lossy()),
    )
    .unwrap();
    std::fs::set_permissions(&path, std::fs::Permissions::from_mode(0o755)).unwrap();
    path
}

fn config(dir: &Path, runtime: ToolRuntime) -> ExecutorConfig {
    ExecutorConfig {
        runtime,
        provider_cache: dir.join("cache"),
        poll_interval: Duration::from_millis(10),
        proxmox_timeout: Duration::from_secs(5),
        retry_delay: Duration::from_millis(1),
        server: Default::default(),
        nix: Default::default(),
    }
}

/// The files of the example lab under `root`, as the repository snapshot of an assignment.
fn example_lab(root: &str) -> Vec<u8> {
    let lab = PathBuf::from(env!("CARGO_MANIFEST_DIR")).join("../../../examples/lab");
    let mut files: Vec<(String, Vec<u8>)> = Vec::new();
    let mut pending = vec![lab.join(root)];
    while let Some(dir) = pending.pop() {
        for entry in std::fs::read_dir(&dir).unwrap() {
            let path = entry.unwrap().path();
            if path.is_dir() {
                pending.push(path);
            } else {
                let name = path
                    .strip_prefix(&lab)
                    .unwrap()
                    .to_string_lossy()
                    .into_owned();
                files.push((name, std::fs::read(&path).unwrap()));
            }
        }
    }
    let refs: Vec<(&str, &[u8])> = files
        .iter()
        .map(|(n, c)| (n.as_str(), c.as_slice()))
        .collect();
    archive::pack(&refs).unwrap()
}

#[allow(clippy::too_many_arguments)]
async fn exec(
    cfg: &ExecutorConfig,
    dir: &Path,
    executor: &str,
    action: &str,
    input: &Value,
    secrets: HashMap<String, String>,
    files: Vec<u8>,
    artifacts: Vec<Artifact>,
) -> Success {
    let workspace = dir.join("work").join(action);
    std::fs::create_dir_all(&workspace).unwrap();
    // As the agent readies an attempt's workspace before its executor runs.
    let mutating = executors::is_mutating(executor, action);
    octopushy::attempts::workspace::init(
        &workspace,
        &format!("contract-{action}"),
        executor,
        action,
        mutating,
    )
    .unwrap();
    octopushy::attempts::workspace::mark_running(&workspace).unwrap();
    let buffer = Arc::new(LogBuffer::default());
    let masker = Arc::new(Masker::new(secrets.values().map(String::as_str)));
    let ctx = ExecContext {
        attempt_id: format!("contract-{action}"),
        action: action.into(),
        // As the controller marks it: apply, set-protection and a playbook run change things.
        mutating,
        input_json: input.to_string(),
        secrets: Secrets::new(secrets),
        files_tar_gz: files,
        artifacts,
        workspace,
        log: AttemptLogger::new(buffer.clone(), masker, Arc::new(Notify::new())),
        cancel: CancellationToken::new(),
        snapshots: Default::default(),
    };
    match executors::run(cfg, executor, &ctx).await {
        Ok(success) => success,
        Err(failure) => {
            let lines: Vec<String> = buffer
                .batch_from(0, usize::MAX)
                .map(|(_, l)| l.into_iter().map(|x| x.text).collect())
                .unwrap_or_default();
            panic!(
                "{executor}/{action} failed: {failure:?}\n{}",
                lines.join("\n")
            )
        }
    }
}

#[tokio::test]
async fn opentofu_outputs_are_the_examples() {
    let dir = tempfile::tempdir().unwrap();
    let tofu = script(dir.path(), "tofu", FAKE_TOFU);
    let cfg = config(
        dir.path(),
        ToolRuntime::new(RuntimeKind::Local, None, tofu, None),
    );
    let secrets = HashMap::from([(
        "proxmox.token".to_owned(),
        "octopushy@pve!agent=00000000-0000-0000-0000-000000000000".to_owned(),
    )]);
    let files = example_lab("modules/opentofu/proxmox-vm");

    let preview = input("opentofu", "preview");
    let planned = exec(
        &cfg,
        dir.path(),
        "opentofu",
        "preview",
        &preview,
        secrets.clone(),
        files.clone(),
        vec![],
    )
    .await;
    check_output("opentofu", "preview", None, &planned.outputs, &[]);

    let apply = input("opentofu", "apply");
    let applied = exec(
        &cfg,
        dir.path(),
        "opentofu",
        "apply",
        &apply,
        secrets,
        files,
        planned.artifacts,
    )
    .await;
    check_output("opentofu", "apply", None, &applied.outputs, &[]);
}

#[tokio::test]
async fn ansible_outputs_are_the_examples() {
    let dir = tempfile::tempdir().unwrap();
    let mut runtime = ToolRuntime::new(RuntimeKind::Local, None, "false".into(), None);
    runtime.ansible_binary = script(dir.path(), "octopushy-ansible", FAKE_ANSIBLE);
    let files = example_lab("modules/ansible/machine-tools");

    let cfg = config(dir.path(), runtime);
    let preview = input("ansible", "preview");
    let previewed = exec(
        &cfg,
        dir.path(),
        "ansible",
        "preview",
        &preview,
        HashMap::new(),
        files.clone(),
        vec![],
    )
    .await;
    check_output("ansible", "preview", None, &previewed.outputs, &[]);

    let secrets = HashMap::from([
        ("ansible.ssh-key".to_owned(), "-----BEGIN OPENSSH PRIVATE KEY-----\nb3BlbnNzaC1rZXk\n-----END OPENSSH PRIVATE KEY-----\n".to_owned()),
        ("ansible.var.machine_tools_token".to_owned(), "synthetic-machine-tools-token".to_owned()),
    ]);
    let run = input("ansible", "run");
    for (recap, variant) in [
        ("recap.json", None),
        ("recap-outcome.json", Some("outcome")),
    ] {
        std::fs::write(dir.path().join("recap.json"), recorded(recap)).unwrap();
        let ran = exec(
            &cfg,
            dir.path(),
            "ansible",
            "run",
            &run,
            secrets.clone(),
            files.clone(),
            previewed.artifacts.clone(),
        )
        .await;
        check_output("ansible", "run", variant, &ran.outputs, &[]);
        std::fs::remove_dir_all(dir.path().join("work/run")).unwrap();
    }
}

#[tokio::test]
async fn proxmox_outputs_are_the_examples() {
    // mock-proxmox's home, where VMID 310 is the example guest, made and verified by Octopushy, still protected.
    let mut fixture = Fixture::example();
    fixture.vms.retain(|vm| vm.vmid != 310);
    fixture.vms.push(FixtureVm {
        vmid: 310,
        name: "krb-app-01".into(),
        node: "korriban".into(),
        status: "running".into(),
        template: false,
        tags: vec!["octopushy".into(), "home".into()],
        protection: true,
        description: Some("Managed by Octopushy as home/korriban/krb-app-01.".into()),
        ipv4: Some(vec!["10.1.0.31".into()]),
        kind: "qemu".into(),
        max_cpu: 2,
        max_memory_bytes: 4 * 1024 * 1024 * 1024,
        uptime_seconds: 3600,
    });
    let mock = mock_proxmox::start(
        fixture,
        Options {
            listen: "127.0.0.1:0".parse().unwrap(),
            tls: true,
        },
    )
    .await
    .unwrap();
    let dir = tempfile::tempdir().unwrap();
    let cfg = config(
        dir.path(),
        ToolRuntime::new(RuntimeKind::Local, None, "false".into(), None),
    );
    let secrets = HashMap::from([("proxmox.token".to_owned(), mock.token())]);
    // The example's input, sent to the mock.
    let to_mock = |action: &str| {
        let mut input = input("proxmox", action);
        input["connection"] = json!({ "endpoint": mock.endpoint(), "verifyTls": false, "caCertificatePem": mock.ca_pem() });
        input
    };

    for action in ["observe", "verify-guest", "set-protection"] {
        let done = exec(
            &cfg,
            dir.path(),
            "proxmox",
            action,
            &to_mock(action),
            secrets.clone(),
            vec![],
            vec![],
        )
        .await;
        check_output("proxmox", action, None, &done.outputs, &[]);
    }
    assert!(mock.remove_vm(310));
    let gone = exec(
        &cfg,
        dir.path(),
        "proxmox",
        "verify-absent",
        &to_mock("verify-absent"),
        secrets,
        vec![],
        vec![],
    )
    .await;
    check_output("proxmox", "verify-absent", None, &gone.outputs, &[]);
}
