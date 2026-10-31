//! The built-in state with a real `tofu` on `PATH`, for behavior a fake cannot show.
//! Ignored by default:
//!
//! ```sh
//! cargo test -p octopushy --test opentofu_real -- --ignored --nocapture
//! ```

use std::collections::HashMap;
use std::os::unix::fs::PermissionsExt;
use std::path::Path;
use std::sync::Arc;
use std::time::Duration;

use octopushy::attempts::logs::{AttemptLogger, LogBuffer};
use octopushy::executors::opentofu::{archive, state};
use octopushy::executors::{self, ExecContext, ExecutorConfig, Secrets};
use octopushy::masking::Masker;
use octopushy::tools::{RuntimeKind, ToolRuntime};
use octopushy_proto::v1::Artifact;
use serde_json::{Value, json};
use tokio::sync::Notify;
use tokio_util::sync::CancellationToken;

const SEED: &[u8] = br#"{"version":4,"serial":0,"lineage":"seed","outputs":{},"resources":[]}"#;

/// Makes the backend's directory unwritable, so OpenTofu cannot save the state.
const ROOT: &[u8] = br#"
resource "terraform_data" "created" {
  provisioner "local-exec" {
    command = "chmod 500 ../state"
  }
}
"#;

/// Makes OpenTofu's working directory unwritable too, so it can save the state nowhere.
const ROOT_NOWHERE: &[u8] = br#"
resource "terraform_data" "created" {
  provisioner "local-exec" {
    command = "chmod 500 ../state ."
  }
}
"#;

/// As above, with more diagnostics after OpenTofu's than a stderr tail keeps.
const ROOT_NOWHERE_LONG: &[u8] = br#"
resource "terraform_data" "created" {
  provisioner "local-exec" {
    command = "chmod 500 ../state .; seq 1 30; exit 1"
  }
}
"#;

/// Fails with the words of a failed save in its output, after OpenTofu saved everything.
const ROOT_QUOTES: &[u8] = br#"
resource "terraform_data" "created" {
  provisioner "local-exec" {
    command = "echo Failed to persist state to backend; exit 1"
  }
}
"#;

async fn run(
    dir: &Path,
    id: &str,
    action: &str,
    root: &[u8],
    artifacts: Vec<Artifact>,
) -> (Result<Vec<Artifact>, String>, std::path::PathBuf) {
    let cfg = ExecutorConfig {
        runtime: ToolRuntime::new(RuntimeKind::Local, None, "tofu".into(), None),
        provider_cache: dir.join("cache"),
        poll_interval: Duration::from_secs(60),
        proxmox_timeout: Duration::from_secs(1),
    };
    let workspace = dir.join(id);
    std::fs::create_dir_all(&workspace).unwrap();
    let ctx = ExecContext {
        attempt_id: id.into(),
        action: action.into(),
        input_json: json!({ "root": ".", "backend": { "type": "octopushy" } }).to_string(),
        secrets: Secrets::new(HashMap::new()),
        files_tar_gz: archive::pack(&[("main.tf", root)]).unwrap(),
        artifacts,
        workspace: workspace.clone(),
        log: AttemptLogger::new(
            Arc::new(LogBuffer::default()),
            Arc::new(Masker::default()),
            Arc::new(Notify::new()),
        ),
        cancel: CancellationToken::new(),
        snapshots: Default::default(),
    };
    let result = executors::run(&cfg, "opentofu", &ctx).await;
    (
        result.map(|s| s.artifacts).map_err(|e| format!("{e:?}")),
        workspace,
    )
}

#[tokio::test]
#[ignore = "needs a real tofu"]
async fn the_state_of_an_apply_that_could_not_save_it_is_not_lost() {
    let dir = tempfile::tempdir().unwrap();
    let dir = dir.path().canonicalize().unwrap();
    let seed = Artifact {
        name: state::ARTIFACT.into(),
        sha256: octopushy::tls::sha256_hex(SEED),
        content: SEED.to_vec(),
    };

    let (preview, _) = run(&dir, "plan", "preview", ROOT, vec![seed.clone()]).await;
    let plan = preview.unwrap().remove(0);
    let (apply, workspace) = run(&dir, "apply", "apply", ROOT, vec![plan, seed]).await;
    let state_dir = workspace.join("state");
    std::fs::set_permissions(&state_dir, std::fs::Permissions::from_mode(0o700)).unwrap();

    assert!(apply.is_err(), "the apply cannot have saved its state");
    let left = state::read(&workspace).expect("the emergency state");
    let parsed: Value = serde_json::from_slice(&left.content).unwrap();
    assert_eq!(parsed["lineage"], "seed");
    assert_eq!(parsed["serial"], 1);
    assert_eq!(parsed["resources"][0]["name"], "created");
}

async fn apply(
    root: &[u8],
) -> (
    tempfile::TempDir,
    Result<Vec<Artifact>, String>,
    std::path::PathBuf,
) {
    let dir = tempfile::tempdir().unwrap();
    let path = dir.path().canonicalize().unwrap();
    let seed = Artifact {
        name: state::ARTIFACT.into(),
        sha256: octopushy::tls::sha256_hex(SEED),
        content: SEED.to_vec(),
    };
    let (preview, _) = run(&path, "plan", "preview", root, vec![seed.clone()]).await;
    let plan = preview.unwrap().remove(0);
    let (result, workspace) = run(&path, "apply", "apply", root, vec![plan, seed]).await;
    for directory in [workspace.join("state"), workspace.join("repo")] {
        std::fs::set_permissions(&directory, std::fs::Permissions::from_mode(0o700)).unwrap();
    }
    (dir, result, workspace)
}

#[tokio::test]
#[ignore = "needs a real tofu"]
async fn an_apply_that_could_save_its_state_nowhere_is_uncertain() {
    for root in [ROOT_NOWHERE, ROOT_NOWHERE_LONG] {
        let (_dir, result, _) = apply(root).await;
        let error = result.expect_err("the apply cannot have saved its state");
        assert!(error.starts_with("Uncertain"), "{error}");
    }
}

#[tokio::test]
#[ignore = "needs a real tofu"]
async fn output_that_quotes_a_failed_save_is_an_ordinary_failure() {
    let (_dir, result, workspace) = apply(ROOT_QUOTES).await;
    let error = result.expect_err("the provisioner fails");
    assert!(error.starts_with("Failed"), "{error}");
    let left = state::read(&workspace).expect("the saved state");
    let parsed: Value = serde_json::from_slice(&left.content).unwrap();
    assert_eq!(parsed["serial"], 1);
}
