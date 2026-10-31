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

async fn run(
    dir: &Path,
    id: &str,
    action: &str,
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
        files_tar_gz: archive::pack(&[("main.tf", ROOT)]).unwrap(),
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

    let (preview, _) = run(&dir, "plan", "preview", vec![seed.clone()]).await;
    let plan = preview.unwrap().remove(0);
    let (apply, workspace) = run(&dir, "apply", "apply", vec![plan, seed]).await;
    let state_dir = workspace.join("state");
    std::fs::set_permissions(&state_dir, std::fs::Permissions::from_mode(0o700)).unwrap();

    assert!(apply.is_err(), "the apply cannot have saved its state");
    let left = state::read(&workspace).expect("the emergency state");
    let parsed: Value = serde_json::from_slice(&left.content).unwrap();
    assert_eq!(parsed["lineage"], "seed");
    assert_eq!(parsed["serial"], 1);
    assert_eq!(parsed["resources"][0]["name"], "created");
}
