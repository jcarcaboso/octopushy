//! Real OpenTofu in rootless podman. Ignored by default (needs podman and the
//! image); run with:
//!
//! ```sh
//! cargo test -p octopushy --test podman_smoke -- --ignored --nocapture
//! ```

use std::collections::HashMap;
use std::sync::{Arc, Mutex};
use std::time::Duration;

use axum::body::Bytes;
use axum::extract::State;
use axum::http::{HeaderMap, StatusCode};
use axum::routing::get;
use base64::Engine;
use octopushy::attempts::logs::{AttemptLogger, LogBuffer};
use octopushy::executors::opentofu::archive;
use octopushy::executors::{self, ExecContext, ExecutorConfig, Secrets};
use octopushy::masking::Masker;
use octopushy::tools::{Invocation, RuntimeKind, ToolRuntime};
use serde_json::json;
use tokio::sync::Notify;
use tokio_util::sync::CancellationToken;

const IMAGE: &str = "ghcr.io/opentofu/opentofu:1.13.1";
const STATE_PASSWORD: &str = "st4te-backend-pa55";

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
                name: format!("octopushy-smoke-{}", fastrand::u32(..)),
                image: IMAGE,
                workspace: &ws,
                workdir: &ws,
                plugin_cache: &cache,
                args: vec!["version".into(), "-json".into()],
                settings: vec![
                    ("HOME".into(), "/work/home".into()),
                    (
                        "TF_PLUGIN_CACHE_DIR".into(),
                        "/octopushy/plugin-cache".into(),
                    ),
                ],
                env: vec![("SOME_SECRET".into(), "not-a-real-secret-value".into())],
                capture_stdout: true,
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

/// A tiny OpenTofu `http` state backend with basic auth.
async fn state_backend() -> (String, Arc<Mutex<Option<Vec<u8>>>>) {
    type S = Arc<Mutex<Option<Vec<u8>>>>;
    let state: S = Arc::new(Mutex::new(None));
    fn authorized(h: &HeaderMap) -> bool {
        let want = format!(
            "Basic {}",
            base64::engine::general_purpose::STANDARD.encode(format!("octopushy:{STATE_PASSWORD}"))
        );
        h.get("authorization").and_then(|v| v.to_str().ok()) == Some(want.as_str())
    }
    let app = axum::Router::new()
        .route(
            "/state",
            get(|State(s): State<S>, h: HeaderMap| async move {
                if !authorized(&h) {
                    return (StatusCode::UNAUTHORIZED, Vec::new());
                }
                match s.lock().unwrap().clone() {
                    Some(body) => (StatusCode::OK, body),
                    None => (StatusCode::NOT_FOUND, Vec::new()),
                }
            })
            .post(|State(s): State<S>, h: HeaderMap, body: Bytes| async move {
                if !authorized(&h) {
                    return StatusCode::UNAUTHORIZED;
                }
                *s.lock().unwrap() = Some(body.to_vec());
                StatusCode::OK
            }),
        )
        .with_state(state.clone());
    let listener = tokio::net::TcpListener::bind("127.0.0.1:0").await.unwrap();
    let url = format!(
        "http://127.0.0.1:{}/state",
        listener.local_addr().unwrap().port()
    );
    tokio::spawn(async move { axum::serve(listener, app).await.unwrap() });
    (url, state)
}

#[tokio::test]
#[ignore = "needs rootless podman and the OpenTofu image"]
async fn podman_preview_and_apply() {
    let (state_url, state) = state_backend().await;
    let dir = tempfile::tempdir().unwrap();
    let cfg = ExecutorConfig {
        // The container shares the host network so it reaches the test backend
        // on 127.0.0.1.
        runtime: ToolRuntime::new(
            RuntimeKind::Podman,
            None,
            "tofu".into(),
            Some("host".into()),
        ),
        provider_cache: dir.path().join("cache"),
        poll_interval: Duration::from_secs(1),
        proxmox_timeout: Duration::from_secs(5),
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
        "backend": { "type": "http", "config": { "address": state_url } },
        "variables": { "octopushy": { "stack": "home.korriban.test-vm" }, "guest": { "vmid": 4242 } },
        "env": { "TF_HTTP_USERNAME": "octopushy" },
        "secretEnv": { "TF_HTTP_PASSWORD": "backend.password" },
        "destroy": false
    });
    let secrets = HashMap::from([("backend.password".to_owned(), STATE_PASSWORD.to_owned())]);

    let run = |id: &'static str, action: &'static str, artifacts| {
        let ws = dir.path().join("work").join(id);
        std::fs::create_dir_all(&ws).unwrap();
        let (log, buffer) = logger(&[STATE_PASSWORD]);
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
    assert!(lines1.iter().all(|l| !l.contains(STATE_PASSWORD)));

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
    let stored = state
        .lock()
        .unwrap()
        .clone()
        .expect("state was written to the backend");
    assert!(String::from_utf8_lossy(&stored).contains("terraform_data"));

    // Files written in the container belong to the agent's user (keep-id).
    use std::os::unix::fs::MetadataExt;
    let plan = dir.path().join("work/smoke-preview/out/plan.bin");
    // SAFETY: getuid has no preconditions.
    assert_eq!(std::fs::metadata(plan).unwrap().uid(), unsafe {
        libc::getuid()
    });
}
