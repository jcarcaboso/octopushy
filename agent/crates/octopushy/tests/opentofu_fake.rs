//! The opentofu executor with the `local` runtime and a fake `tofu` script that
//! records its arguments and environment.

use std::collections::HashMap;
use std::path::{Path, PathBuf};
use std::sync::Arc;
use std::time::{Duration, Instant};

use octopushy::attempts::logs::{AttemptLogger, LogBuffer};
use octopushy::executors::opentofu::archive;
use octopushy::executors::opentofu::state::{self, Snapshots};
use octopushy::executors::{self, ExecContext, ExecutorConfig, Failure, Secrets, Success};
use octopushy::masking::Masker;
use octopushy::tools::{RuntimeKind, ToolRuntime};
use octopushy_proto::v1::{Artifact, LogStream};
use serde_json::{Value, json};
use tokio::sync::Notify;
use tokio_util::sync::CancellationToken;

const PVE_TOKEN: &str = "octopushy@pve!agent=5ec2e7-70ke-n000-aaaa-bbbbbbbbbbbb";
const PG_CONN: &str = "postgres://tofu:pg-pa55word@db.lan/state";

const FAKE_TOFU: &str = r#"#!/bin/bash
set -u
REC="@REC@"
printf '%s\n' "$*" >> "$REC/args.log"
printf '%s\n' "$PWD" >> "$REC/cwd.log"
state=""
if [ -f octopushy_backend.tf.json ] && grep -q '"local"' octopushy_backend.tf.json; then
  state=$(sed -E 's/.*"path":"([^"]*)".*/\1/' octopushy_backend.tf.json)
fi
{ echo "=== $1"; env | sort; } >> "$REC/env.log"
last="${@: -1}"
case "$1" in
  version)
    echo '{"terraform_version":"1.13.1","platform":"linux_amd64","provider_selections":{}}' ;;
  init)
    [ -f octopushy_backend.tf.json ] || { echo "no backend file" >&2; exit 1; }
    cp octopushy_backend.tf.json "$REC/backend.json"
    cp octopushy.auto.tfvars.json "$REC/tfvars.json"
    if [ -f .terraform.lock.hcl ]; then cp .terraform.lock.hcl "$REC/lock-at-init.hcl"; fi
    [ -f .terraform.lock.hcl ] || echo '# lock written by init' > .terraform.lock.hcl
    mkdir -p .terraform
    echo "Initializing the backend..."
    echo "OpenTofu has been successfully initialized!" ;;
  workspace)
    echo "Switched to workspace \"$last\"." ;;
  plan)
    out=""; destroy=0
    for a in "$@"; do case "$a" in -out=*) out="${a#-out=}" ;; -destroy) destroy=1 ;; esac; done
    echo "PLAN destroy=$destroy" > "$out"
    if [ -n "$state" ] && [ -f "$state" ]; then cp "$state" "$REC/state-at-plan.json"; fi
    if [ -n "${FAKE_PLAN_SLEEP:-}" ]; then sleep "$FAKE_PLAN_SLEEP"; fi
    echo "Plan: 2 to add, 1 to change, 1 to destroy."
    echo "debug: provider token $PROXMOX_VE_API_TOKEN" >&2
    exit "${FAKE_PLAN_EXIT:-2}" ;;
  show)
    if grep -q 'destroy=1' "$last"; then
      echo '{"resource_changes":[{"address":"proxmox_virtual_environment_vm.guest","type":"proxmox_virtual_environment_vm","name":"guest","change":{"actions":["delete"]}}]}'
    else
      cat <<'JSON'
{"format_version":"1.2","resource_changes":[
 {"address":"terraform_data.a","type":"terraform_data","name":"a","change":{"actions":["create"]}},
 {"address":"terraform_data.b","type":"terraform_data","name":"b","change":{"actions":["update"]}},
 {"address":"terraform_data.c","type":"terraform_data","name":"c","change":{"actions":["delete","create"]}},
 {"address":"terraform_data.d","type":"terraform_data","name":"d","change":{"actions":["no-op"]}},
 {"address":"data.x.y","type":"x","name":"y","change":{"actions":["read"]}}
],"configuration":{"note":"PLAN-JSON-MUST-NOT-BE-LOGGED"}}
JSON
    fi ;;
  apply)
    grep -q PLAN "$last" || { echo "not a plan: $last" >&2; exit 1; }
    if [ -n "$state" ]; then
      [ -f "$state" ] && cp "$state" "$REC/state-at-apply.json"
      echo '{"version":4,"serial":8,"lineage":"l"}' > "$state"
    fi
    if [ -n "${FAKE_APPLY_SLEEP:-}" ]; then sleep "$FAKE_APPLY_SLEEP"; fi
    if [ -n "${FAKE_ERRORED:-}" ]; then echo '{"version":4,"serial":9,"lineage":"l"}' > errored.tfstate; fi
    echo "Apply complete! Resources: 2 added. token=$PROXMOX_VE_API_TOKEN"
    exit "${FAKE_APPLY_EXIT:-0}" ;;
  output)
    echo '{"vm_id":{"sensitive":false,"type":"number","value":210},"ipv4_addresses":{"sensitive":false,"type":"tuple","value":[["10.1.0.21"]]},"root_password":{"sensitive":true,"type":"string","value":"hunter2"}}' ;;
  *) echo "unknown command $1" >&2; exit 1 ;;
esac
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
        let tofu = dir.path().join("bin/tofu");
        std::fs::create_dir_all(tofu.parent().unwrap()).unwrap();
        std::fs::write(&tofu, FAKE_TOFU.replace("@REC@", &rec.to_string_lossy())).unwrap();
        use std::os::unix::fs::PermissionsExt;
        std::fs::set_permissions(&tofu, std::fs::Permissions::from_mode(0o755)).unwrap();
        let mut runtime = ToolRuntime::new(RuntimeKind::Local, None, tofu, None);
        runtime.kill_grace = Duration::from_secs(1);
        let cfg = ExecutorConfig {
            runtime,
            provider_cache: dir.path().join("cache"),
            poll_interval: Duration::from_millis(10),
            proxmox_timeout: Duration::from_secs(1),
        };
        Self { dir, rec, cfg }
    }

    fn read(&self, name: &str) -> String {
        std::fs::read_to_string(self.rec.join(name)).unwrap_or_default()
    }

    fn reset(&self) {
        std::fs::remove_dir_all(&self.rec).unwrap();
        std::fs::create_dir_all(&self.rec).unwrap();
    }
}

fn repo_tar(lock: Option<&str>) -> Vec<u8> {
    let mut files: Vec<(&str, &[u8])> = vec![
        (
            "modules/opentofu/test-vm/main.tf",
            b"resource \"terraform_data\" \"a\" {}\n",
        ),
        ("README.md", b"lab repo\n"),
    ];
    let lock_path = "modules/opentofu/test-vm/.terraform.lock.hcl";
    if let Some(l) = lock {
        files.push((lock_path, l.as_bytes()));
    }
    archive::pack(&files).unwrap()
}

fn input(destroy: bool, extra_env: Value) -> Value {
    let mut env =
        json!({ "PROXMOX_VE_ENDPOINT": "https://10.1.0.10:8006/", "PROXMOX_VE_INSECURE": "true" });
    for (k, v) in extra_env.as_object().unwrap() {
        env[k] = v.clone();
    }
    json!({
        "stack": "home.korriban.krb-crt-01",
        "root": "modules/opentofu/test-vm",
        "image": "ghcr.io/opentofu/opentofu:1.13.1",
        "backend": {
            "type": "pg",
            "config": { "schema_name": "octopushy_state", "skip_schema_creation": false },
            "workspace": "home.korriban.krb-crt-01"
        },
        "variables": { "octopushy": { "stack": "home.korriban.krb-crt-01" }, "guest": { "vmid": 210 } },
        "env": env,
        "secretEnv": { "PROXMOX_VE_API_TOKEN": "proxmox.token", "PG_CONN_STR": "backend.PG_CONN_STR" },
        "destroy": destroy,
        "someFutureField": true
    })
}

struct Run {
    result: Result<Success, Failure>,
    lines: Vec<(i32, String)>,
    workspace: PathBuf,
    snapshots: Snapshots,
}

async fn run(
    fx: &Fixture,
    id: &str,
    action: &str,
    input: &Value,
    files: Vec<u8>,
    artifacts: Vec<Artifact>,
    cancel: CancellationToken,
) -> Run {
    let secrets = HashMap::from([
        ("proxmox.token".to_owned(), PVE_TOKEN.to_owned()),
        ("backend.PG_CONN_STR".to_owned(), PG_CONN.to_owned()),
    ]);
    let buffer = Arc::new(LogBuffer::default());
    let masker = Arc::new(Masker::new(secrets.values().map(String::as_str)));
    let workspace = fx.dir.path().join("work").join(id);
    std::fs::create_dir_all(&workspace).unwrap();
    let ctx = ExecContext {
        attempt_id: id.into(),
        action: action.into(),
        input_json: input.to_string(),
        secrets: Secrets::new(secrets),
        files_tar_gz: files,
        artifacts,
        workspace: workspace.clone(),
        log: AttemptLogger::new(buffer.clone(), masker, Arc::new(Notify::new())),
        cancel,
        snapshots: Default::default(),
    };
    let result = executors::run(&fx.cfg, "opentofu", &ctx).await;
    let lines = buffer
        .batch_from(0, usize::MAX)
        .map(|(_, l)| l.into_iter().map(|x| (x.stream, x.text)).collect())
        .unwrap_or_default();
    Run {
        result,
        lines,
        workspace,
        snapshots: ctx.snapshots,
    }
}

fn assert_no_secrets(fx: &Fixture, r: &Run) {
    for (_, text) in &r.lines {
        assert!(
            !text.contains("pg-pa55word") && !text.contains("5ec2e7-70ke"),
            "secret in log: {text}"
        );
        assert!(
            !text.contains("PLAN-JSON-MUST-NOT-BE-LOGGED"),
            "plan JSON in log: {text}"
        );
    }
    // Secrets never appear on a command line.
    let args = fx.read("args.log");
    assert!(
        !args.contains("pg-pa55word") && !args.contains("5ec2e7"),
        "{args}"
    );
}

fn env_of(fx: &Fixture, step: &str) -> HashMap<String, String> {
    let all = fx.read("env.log");
    let section = all
        .split("=== ")
        .find(|s| s.starts_with(&format!("{step}\n")))
        .unwrap_or_else(|| panic!("no env for {step}"));
    section
        .lines()
        .skip(1)
        .filter_map(|l| l.split_once('='))
        .map(|(k, v)| (k.to_owned(), v.to_owned()))
        .collect()
}

fn built_in(extra_env: Value) -> Value {
    let mut input = input(false, extra_env);
    input["backend"] = json!({ "type": "octopushy", "config": {}, "workspace": null });
    input["secretEnv"] = json!({ "PROXMOX_VE_API_TOKEN": "proxmox.token" });
    input
}

#[tokio::test]
async fn built_in_state_lives_in_the_workspace_as_a_local_backend() {
    let fx = Fixture::new();
    let state_v7 = b"{\"version\":4,\"serial\":7,\"lineage\":\"l\"}\n".to_vec();
    let state = Artifact {
        name: state::ARTIFACT.into(),
        sha256: octopushy::tls::sha256_hex(&state_v7),
        content: state_v7.clone(),
    };

    let preview = run(
        &fx,
        "p1",
        "preview",
        &built_in(json!({})),
        repo_tar(None),
        vec![state.clone()],
        CancellationToken::new(),
    )
    .await;
    let success = preview.result.as_ref().unwrap();
    assert_eq!(success.artifacts.len(), 1, "the plan, and no state");
    assert_eq!(success.artifacts[0].name, "plan.tar.gz");
    assert!(
        preview.snapshots.take().is_none(),
        "a preview writes nothing"
    );
    assert_eq!(fx.read("state-at-plan.json").into_bytes(), state_v7);
    let backend: Value = serde_json::from_str(&fx.read("backend.json")).unwrap();
    assert_eq!(
        backend,
        json!({ "terraform": { "backend": { "local": {
            "path": "../../../../state/terraform.tfstate"
        } } } })
    );
    let args = fx.read("args.log");
    assert!(
        !args.contains("workspace") && !args.contains("-backend-config"),
        "{args}"
    );

    let plan = success.artifacts[0].clone();
    let apply = run(
        &fx,
        "a1",
        "apply",
        &built_in(json!({ "FAKE_APPLY_SLEEP": "0.2" })),
        repo_tar(None),
        vec![plan, state.clone()],
        CancellationToken::new(),
    )
    .await;
    assert!(apply.result.is_ok(), "{:?}", apply.result);
    assert_eq!(fx.read("state-at-apply.json").into_bytes(), state_v7);
    let snapshot = apply.snapshots.take().expect("a snapshot during the apply");
    assert!(String::from_utf8_lossy(&snapshot.content).contains("\"serial\":8"));
    let left = state::read(&apply.workspace).expect("the state for the result");
    assert_eq!(left.sha256, snapshot.sha256);
    assert_no_secrets(&fx, &apply);
}

#[tokio::test]
async fn an_apply_that_could_not_write_its_backend_reports_the_emergency_state() {
    let fx = Fixture::new();
    let preview = run(
        &fx,
        "p1",
        "preview",
        &built_in(json!({})),
        repo_tar(None),
        vec![],
        CancellationToken::new(),
    )
    .await;
    let plan = preview.result.unwrap().artifacts.remove(0);
    let apply = run(
        &fx,
        "a1",
        "apply",
        &built_in(json!({ "FAKE_ERRORED": "1", "FAKE_APPLY_EXIT": "1" })),
        repo_tar(None),
        vec![plan],
        CancellationToken::new(),
    )
    .await;
    assert!(apply.result.is_err());
    let left = state::read(&apply.workspace).expect("the emergency state");
    assert!(String::from_utf8_lossy(&left.content).contains("\"serial\":9"));
}

#[tokio::test]
async fn built_in_state_refuses_settings_and_a_corrupt_state() {
    let fx = Fixture::new();
    let mut bad = built_in(json!({}));
    bad["backend"]["workspace"] = json!("somewhere");
    let refused = run(
        &fx,
        "s1",
        "preview",
        &bad,
        repo_tar(None),
        vec![],
        CancellationToken::new(),
    )
    .await;
    assert!(
        matches!(&refused.result, Err(Failure::Failed { message, .. }) if message.contains("no backend settings")),
        "{:?}",
        refused.result
    );

    let corrupt = Artifact {
        name: state::ARTIFACT.into(),
        sha256: "0".repeat(64),
        content: b"{}".to_vec(),
    };
    let refused = run(
        &fx,
        "s2",
        "preview",
        &built_in(json!({})),
        repo_tar(None),
        vec![corrupt],
        CancellationToken::new(),
    )
    .await;
    assert!(
        matches!(&refused.result, Err(Failure::Failed { message, .. }) if message.contains("corrupt")),
        "{:?}",
        refused.result
    );
}

#[tokio::test]
async fn preview_with_changes_then_apply() {
    let fx = Fixture::new();
    let r = run(
        &fx,
        "prev-1",
        "preview",
        &input(false, json!({})),
        repo_tar(None),
        vec![],
        CancellationToken::new(),
    )
    .await;
    let success = r
        .result
        .as_ref()
        .unwrap_or_else(|e| panic!("{e:?}\n{:#?}", r.lines));
    assert_no_secrets(&fx, &r);

    // Outputs.
    let out = &success.outputs;
    assert_eq!(out["hasChanges"], true);
    assert_eq!(out["toolVersion"], "1.13.1");
    assert_eq!(
        out["counts"],
        json!({ "create": 1, "update": 1, "delete": 0, "replace": 1 })
    );
    let addresses: Vec<&str> = out["changes"]
        .as_array()
        .unwrap()
        .iter()
        .map(|c| c["address"].as_str().unwrap())
        .collect();
    assert_eq!(
        addresses,
        ["terraform_data.a", "terraform_data.b", "terraform_data.c"]
    );
    assert_eq!(out["changes"][2]["actions"], json!(["delete", "create"]));
    assert_eq!(out["changes"][0]["type"], "terraform_data");
    assert_eq!(out["changes"][0]["name"], "a");

    // Commands and arguments.
    let args: Vec<String> = fx.read("args.log").lines().map(str::to_owned).collect();
    let plan_path = r.workspace.join("out/plan.bin");
    assert_eq!(
        args,
        [
            "version -json".to_owned(),
            "init -input=false -no-color -backend-config=schema_name=octopushy_state -backend-config=skip_schema_creation=false".to_owned(),
            "workspace select -or-create=true -no-color home.korriban.krb-crt-01".to_owned(),
            format!("plan -input=false -no-color -detailed-exitcode -out={}", plan_path.display()),
            format!("show -json -no-color {}", plan_path.display()),
        ]
    );
    let root = r
        .workspace
        .join("repo/modules/opentofu/test-vm")
        .canonicalize()
        .unwrap();
    for cwd in fx.read("cwd.log").lines() {
        assert_eq!(Path::new(cwd), root);
    }

    // Environment: secrets and settings arrive in the environment.
    let env = env_of(&fx, "plan");
    assert_eq!(env["PROXMOX_VE_API_TOKEN"], PVE_TOKEN);
    assert_eq!(env["PG_CONN_STR"], PG_CONN);
    assert_eq!(env["PROXMOX_VE_ENDPOINT"], "https://10.1.0.10:8006/");
    assert_eq!(env["TF_IN_AUTOMATION"], "1");
    assert_eq!(env["TF_INPUT"], "0");
    assert_eq!(
        Path::new(&env["TF_PLUGIN_CACHE_DIR"]),
        fx.cfg.provider_cache
    );
    assert_eq!(Path::new(&env["HOME"]), r.workspace.join("home"));
    assert!(
        !env.contains_key("CARGO"),
        "the agent's own environment is not passed on"
    );

    // Generated files.
    let backend: Value = serde_json::from_str(&fx.read("backend.json")).unwrap();
    assert_eq!(backend, json!({ "terraform": { "backend": { "pg": {} } } }));
    let tfvars: Value = serde_json::from_str(&fx.read("tfvars.json")).unwrap();
    assert_eq!(tfvars["guest"]["vmid"], 210);

    // Logs: agent lines, tool output, masked stderr.
    assert!(
        r.lines
            .iter()
            .any(|(s, t)| *s == LogStream::Agent as i32 && t.starts_with("$ tofu init"))
    );
    assert!(
        r.lines
            .iter()
            .any(|(s, t)| *s == LogStream::Stdout as i32 && t.contains("successfully initialized"))
    );
    assert!(
        r.lines
            .iter()
            .any(|(s, t)| *s == LogStream::Stderr as i32 && t == "debug: provider token ***")
    );

    // The artifact: plan.bin and the lock file after init.
    assert_eq!(success.artifacts.len(), 1);
    let artifact = &success.artifacts[0];
    assert_eq!(artifact.name, "plan.tar.gz");
    assert_eq!(
        artifact.sha256,
        octopushy::tls::sha256_hex(&artifact.content)
    );
    let unpacked = fx.dir.path().join("unpacked");
    archive::extract_tar_gz(&artifact.content, &unpacked, Default::default()).unwrap();
    assert_eq!(
        std::fs::read_to_string(unpacked.join("plan.bin")).unwrap(),
        "PLAN destroy=0\n"
    );
    assert_eq!(
        std::fs::read_to_string(unpacked.join(".terraform.lock.hcl")).unwrap(),
        "# lock written by init\n"
    );

    // Apply that plan, from a repository whose lock file differs.
    fx.reset();
    let r = run(
        &fx,
        "apply-1",
        "apply",
        &input(false, json!({})),
        repo_tar(Some("# stale lock from the repository\n")),
        vec![artifact.clone()],
        CancellationToken::new(),
    )
    .await;
    let success = r
        .result
        .as_ref()
        .unwrap_or_else(|e| panic!("{e:?}\n{:#?}", r.lines));
    assert_no_secrets(&fx, &r);
    assert_eq!(
        fx.read("lock-at-init.hcl"),
        "# lock written by init\n",
        "the preview's lock file is restored before init"
    );
    let args: Vec<String> = fx.read("args.log").lines().map(str::to_owned).collect();
    let plan_path = r.workspace.join("out/plan.bin");
    assert_eq!(
        args,
        [
            "version -json".to_owned(),
            "init -input=false -no-color -lockfile=readonly -backend-config=schema_name=octopushy_state -backend-config=skip_schema_creation=false".to_owned(),
            "workspace select -or-create=true -no-color home.korriban.krb-crt-01".to_owned(),
            format!("apply -input=false -no-color {}", plan_path.display()),
            "output -json -no-color".to_owned(),
        ]
    );
    assert_eq!(
        success.outputs,
        json!({ "outputs": { "vm_id": 210, "ipv4_addresses": [["10.1.0.21"]] }, "sensitiveOutputs": ["root_password"] })
    );
    assert!(
        r.lines
            .iter()
            .any(|(_, t)| t == "Apply complete! Resources: 2 added. token=***")
    );
    assert!(r.lines.iter().all(|(_, t)| !t.contains("hunter2")));
}

#[tokio::test]
async fn no_changes_plan_can_be_retained_for_later_privileged_guest_approval() {
    let fx = Fixture::new();
    let mut request = input(false, json!({ "FAKE_PLAN_EXIT": "0" }));
    request["retainNoChangesPlan"] = json!(true);
    let r = run(
        &fx,
        "retain-no-change",
        "preview",
        &request,
        repo_tar(Some("# reviewed lock")),
        vec![],
        CancellationToken::new(),
    )
    .await;
    let success = r.result.as_ref().unwrap();
    assert_eq!(success.outputs["hasChanges"], false);
    assert_eq!(success.outputs["changes"], json!([]));
    assert_eq!(success.artifacts.len(), 1);
    let artifact = &success.artifacts[0];
    assert_eq!(artifact.name, "plan.tar.gz");
    assert_eq!(
        artifact.sha256,
        octopushy::tls::sha256_hex(&artifact.content)
    );
    let extracted = fx.dir.path().join("retained");
    std::fs::create_dir_all(&extracted).unwrap();
    archive::extract_tar_gz(&artifact.content, &extracted, archive::Limits::default()).unwrap();
    assert_eq!(
        std::fs::read(extracted.join("plan.bin")).unwrap(),
        b"PLAN destroy=0\n"
    );
    assert_eq!(
        std::fs::read(extracted.join(".terraform.lock.hcl")).unwrap(),
        b"# reviewed lock"
    );
    assert!(!fx.read("args.log").contains("show"));
    assert!(!fx.read("args.log").contains("apply"));
    assert_no_secrets(&fx, &r);
}

#[tokio::test]
async fn destroy_no_changes_and_errors() {
    let fx = Fixture::new();

    // Destroy plan.
    let r = run(
        &fx,
        "d-1",
        "preview",
        &input(true, json!({})),
        repo_tar(None),
        vec![],
        CancellationToken::new(),
    )
    .await;
    let out = &r.result.as_ref().unwrap().outputs;
    assert!(
        fx.read("args.log")
            .lines()
            .any(|l| l.starts_with("plan ") && l.ends_with(" -destroy"))
    );
    assert_eq!(
        out["counts"],
        json!({ "create": 0, "update": 0, "delete": 1, "replace": 0 })
    );
    assert_eq!(out["changes"][0]["actions"], json!(["delete"]));

    // No changes: no show, no artifact.
    fx.reset();
    let r = run(
        &fx,
        "n-1",
        "preview",
        &input(false, json!({ "FAKE_PLAN_EXIT": "0" })),
        repo_tar(None),
        vec![],
        CancellationToken::new(),
    )
    .await;
    let success = r.result.unwrap();
    assert_eq!(success.outputs["hasChanges"], false);
    assert_eq!(success.outputs["changes"], json!([]));
    assert!(success.artifacts.is_empty());
    assert!(!fx.read("args.log").contains("show"));

    // A failing plan fails the attempt, with masked stderr in the error.
    fx.reset();
    let r = run(
        &fx,
        "e-1",
        "preview",
        &input(false, json!({ "FAKE_PLAN_EXIT": "1" })),
        repo_tar(None),
        vec![],
        CancellationToken::new(),
    )
    .await;
    match r.result {
        Err(Failure::Failed { message, .. }) => {
            assert!(
                message.starts_with("tofu plan failed (exit status 1)"),
                "{message}"
            );
            assert!(message.contains("debug: provider token"), "{message}");
        }
        other => panic!("{other:?}"),
    }

    // Missing secret, escaping root, reserved env, local backend.
    let mut bad = input(false, json!({}));
    bad["secretEnv"]["X_TOKEN"] = json!("not.provided");
    let r = run(
        &fx,
        "e-2",
        "preview",
        &bad,
        repo_tar(None),
        vec![],
        CancellationToken::new(),
    )
    .await;
    assert!(
        matches!(&r.result, Err(Failure::Failed { message, .. }) if message.contains("not.provided")),
        "{:?}",
        r.result
    );

    let mut bad = input(false, json!({}));
    bad["root"] = json!("../../etc");
    let r = run(
        &fx,
        "e-3",
        "preview",
        &bad,
        repo_tar(None),
        vec![],
        CancellationToken::new(),
    )
    .await;
    assert!(
        matches!(&r.result, Err(Failure::Failed { message, .. }) if message.contains("relative path")),
        "{:?}",
        r.result
    );

    let r = run(
        &fx,
        "e-4",
        "preview",
        &input(false, json!({ "TF_PLUGIN_CACHE_DIR": "/tmp" })),
        repo_tar(None),
        vec![],
        CancellationToken::new(),
    )
    .await;
    assert!(
        matches!(&r.result, Err(Failure::Failed { message, .. }) if message.contains("reserved")),
        "{:?}",
        r.result
    );

    let mut bad = input(false, json!({}));
    bad["backend"]["type"] = json!("local");
    let r = run(
        &fx,
        "e-5",
        "preview",
        &bad,
        repo_tar(None),
        vec![],
        CancellationToken::new(),
    )
    .await;
    assert!(
        matches!(&r.result, Err(Failure::Failed { message, .. }) if message.contains("local backend")),
        "{:?}",
        r.result
    );

    let r = run(
        &fx,
        "e-6",
        "preview",
        &json!({ "root": 5 }),
        repo_tar(None),
        vec![],
        CancellationToken::new(),
    )
    .await;
    assert!(
        matches!(&r.result, Err(Failure::Failed { message, .. }) if message.contains("invalid input")),
        "{:?}",
        r.result
    );
}

#[tokio::test]
async fn apply_refuses_a_corrupt_or_missing_plan() {
    let fx = Fixture::new();
    let content = archive::pack(&[("plan.bin", b"PLAN destroy=0\n")]).unwrap();
    let corrupt = Artifact {
        name: "plan.tar.gz".into(),
        sha256: "0".repeat(64),
        content: content.clone(),
    };
    let r = run(
        &fx,
        "a-1",
        "apply",
        &input(false, json!({})),
        repo_tar(None),
        vec![corrupt],
        CancellationToken::new(),
    )
    .await;
    assert!(
        matches!(&r.result, Err(Failure::Failed { message, .. }) if message.contains("corrupt")),
        "{:?}",
        r.result
    );
    assert!(fx.read("args.log").is_empty(), "nothing ran");

    let r = run(
        &fx,
        "a-2",
        "apply",
        &input(false, json!({})),
        repo_tar(None),
        vec![],
        CancellationToken::new(),
    )
    .await;
    assert!(
        matches!(&r.result, Err(Failure::Failed { message, .. }) if message.contains("no plan.tar.gz")),
        "{:?}",
        r.result
    );

    // An artifact with unexpected files is refused.
    let odd = archive::pack(&[("plan.bin", b"PLAN"), ("evil.sh", b"#!/bin/sh")]).unwrap();
    let odd = Artifact {
        name: "plan.tar.gz".into(),
        sha256: octopushy::tls::sha256_hex(&odd),
        content: odd,
    };
    let r = run(
        &fx,
        "a-3",
        "apply",
        &input(false, json!({})),
        repo_tar(None),
        vec![odd],
        CancellationToken::new(),
    )
    .await;
    assert!(
        matches!(&r.result, Err(Failure::Failed { message, .. }) if message.contains("unexpected entry")),
        "{:?}",
        r.result
    );

    // A failing apply fails the attempt.
    let ok = Artifact {
        name: "plan.tar.gz".into(),
        sha256: octopushy::tls::sha256_hex(&content),
        content,
    };
    let r = run(
        &fx,
        "a-4",
        "apply",
        &input(false, json!({ "FAKE_APPLY_EXIT": "1" })),
        repo_tar(None),
        vec![ok],
        CancellationToken::new(),
    )
    .await;
    assert!(
        matches!(&r.result, Err(Failure::Failed { message, .. }) if message.starts_with("tofu apply failed")),
        "{:?}",
        r.result
    );
}

#[tokio::test]
async fn cancel_stops_a_running_plan() {
    let fx = Fixture::new();
    let cancel = CancellationToken::new();
    let c = cancel.clone();
    tokio::spawn(async move {
        tokio::time::sleep(Duration::from_millis(700)).await;
        c.cancel();
    });
    let started = Instant::now();
    let r = run(
        &fx,
        "c-1",
        "preview",
        &input(false, json!({ "FAKE_PLAN_SLEEP": "60" })),
        repo_tar(None),
        vec![],
        cancel,
    )
    .await;
    assert!(
        matches!(r.result, Err(Failure::Cancelled)),
        "{:?}",
        r.result
    );
    assert!(
        started.elapsed() < Duration::from_secs(10),
        "took {:?}",
        started.elapsed()
    );
    assert!(r.lines.iter().any(|(_, t)| t == "stopping the tool"));
}
