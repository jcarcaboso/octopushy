//! Public executor contract against a scripted guest-agent API, not real disks.
use axum::{
    Json, Router,
    routing::{get, post},
};
use octopushy::{
    attempts::logs::{AttemptLogger, LogBuffer},
    executors::{self, ExecContext, ExecutorConfig, Failure, Secrets},
    masking::Masker,
    tools::{RuntimeKind, ToolRuntime},
};
use serde_json::{Value, json};
use std::{
    collections::HashMap,
    sync::{
        Arc,
        atomic::{AtomicUsize, Ordering},
    },
    time::Duration,
};
use tokio::sync::Notify;
use tokio_util::sync::CancellationToken;

async fn exercise(
    action: &str,
    description: &str,
    response: Value,
    expected_ok: bool,
) -> (Result<executors::Success, Failure>, usize) {
    exercise_vm(
        action,
        json!({"name":"opy-service-01","tags":"octopushy","description":description}),
        response,
        expected_ok,
    )
    .await
}

async fn exercise_vm(
    action: &str,
    vm: Value,
    response: Value,
    expected_ok: bool,
) -> (Result<executors::Success, Failure>, usize) {
    let posts = Arc::new(AtomicUsize::new(0));
    let counter = posts.clone();
    let app = Router::new()
        .route(
            "/api2/json/nodes/umbrel/qemu/9210/config",
            get(move || {
                let vm = vm.clone();
                async move { Json(json!({"data":vm})) }
            }),
        )
        .route(
            "/api2/json/nodes/umbrel/qemu/9210/agent/exec",
            post(move |Json(body): Json<Value>| {
                counter.fetch_add(1, Ordering::SeqCst);
                assert_eq!(
                    body["command"][0],
                    "/run/current-system/sw/bin/octopushy-worker-bootstrap"
                );
                assert_eq!(body["command"][2], "--generation");
                assert_eq!(body["command"][3], GENERATION);
                assert!(!body.to_string().contains("PRIVATE KEY"));
                if body["command"][1] == "install" {
                    use base64::Engine;
                    assert_eq!(body["command"][4], "--payload-base64");
                    let decoded = base64::engine::general_purpose::STANDARD
                        .decode(body["command"][5].as_str().unwrap())
                        .unwrap();
                    assert_eq!(
                        serde_json::from_slice::<Value>(&decoded).unwrap()["agentId"],
                        GENERATION
                    );
                    assert_eq!(body["command"].as_array().unwrap().len(), 6);
                } else {
                    assert_eq!(body["command"].as_array().unwrap().len(), 4);
                }
                async { Json(json!({"data":{"pid":42}})) }
            }),
        )
        .route(
            "/api2/json/nodes/umbrel/qemu/9210/agent/exec-status",
            get(move || {
                let response = response.clone();
                async move { Json(json!({"data":response})) }
            }),
        );
    let listener = tokio::net::TcpListener::bind("127.0.0.1:0").await.unwrap();
    let address = listener.local_addr().unwrap();
    let server = tokio::spawn(async move { axum::serve(listener, app).await.unwrap() });
    let work = tempfile::tempdir().unwrap();
    let cfg = ExecutorConfig {
        runtime: ToolRuntime::new(RuntimeKind::Local, None, "false".into(), None),
        provider_cache: work.path().join("cache"),
        poll_interval: Duration::from_millis(5),
        proxmox_timeout: Duration::from_secs(1),
    };
    let ctx = ExecContext {
        attempt_id: "rootfs-test".into(), action: action.into(),
        input_json: json!({"connection":{"endpoint":format!("http://{address}"),"verifyTls":true},
            "node":"umbrel","vmid":9210,"name":"opy-service-01","address":"home/umbrel/opy-service-01",
            "generation":GENERATION,"waitSeconds":1,
            "identity":if action == "install" { Some(json!({"generation":GENERATION,"agentId":GENERATION})) } else { None }}).to_string(),
        secrets: Secrets::new(HashMap::from([("proxmox.token".into(),"test@pve!test=secret".into())])),
        files_tar_gz: vec![], artifacts: vec![], workspace: work.path().into(),
        log: AttemptLogger::new(Arc::new(LogBuffer::default()), Arc::new(Masker::new(["secret"])), Arc::new(Notify::new())),
        cancel: CancellationToken::new(),
    };
    let result = executors::run(&cfg, "managed-worker", &ctx).await;
    server.abort();
    assert_eq!(result.is_ok(), expected_ok, "{result:?}");
    (result, posts.load(Ordering::SeqCst))
}

const GENERATION: &str = "884d09b4-0934-43a4-bffb-d2f7977bc01c";
const DESCRIPTION: &str = "Managed by Octopushy as home/umbrel/opy-service-01.\nworker-generation: 884d09b4-0934-43a4-bffb-d2f7977bc01c";
fn prepared() -> Value {
    json!({"exited":1,"exitcode":0,"out-data":json!({"protocolVersion":1,"generation":GENERATION,"csrDer":"Y3Ny","agentVersion":"test"}).to_string()})
}
#[tokio::test]
async fn fixed_public_protocol_and_mutation_classification() {
    let (result, count) = exercise("prepare", DESCRIPTION, prepared(), true).await;
    assert_eq!(count, 1);
    assert_eq!(result.unwrap().outputs["generation"], GENERATION);
    assert!(executors::is_mutating("managed-worker", "prepare"));
    assert!(executors::is_mutating("managed-worker", "install"));
}
#[tokio::test]
async fn refuses_wrong_vm_or_generation_before_qga() {
    for description in [
        "foreign",
        "Managed by Octopushy as home/umbrel/opy-service-01.",
        "Managed by Octopushy as home/umbrel/opy-service-01.\nworker-generation: 884d09b4-0934-43a4-bffb-d2f7977bc01d",
    ] {
        let (_, count) = exercise("prepare", description, prepared(), false).await;
        assert_eq!(count, 0);
    }
}
#[tokio::test]
async fn lost_status_stays_uncertain_and_never_resubmits() {
    let (result, count) = exercise("prepare", DESCRIPTION, json!({"exited":0}), false).await;
    assert!(matches!(result, Err(Failure::Uncertain(_))));
    assert_eq!(count, 1);
}
#[tokio::test]
async fn rejects_unexpected_output_instead_of_forwarding_private_material() {
    let mut response = prepared();
    let mut report: Value = serde_json::from_str(response["out-data"].as_str().unwrap()).unwrap();
    report["keyPem"] = json!("PRIVATE KEY");
    response["out-data"] = json!(report.to_string());
    let (result, count) = exercise("prepare", DESCRIPTION, response, false).await;
    assert!(matches!(result, Err(Failure::Uncertain(_))));
    assert!(!format!("{result:?}").contains("PRIVATE KEY"));
    assert_eq!(count, 1);
}

#[tokio::test]
async fn install_uses_only_the_fixed_command_and_public_base64_argument() {
    let response = json!({"exited":1,"exitcode":0,"out-data":json!({"protocolVersion":1,
        "generation":GENERATION,"agentId":GENERATION,"installed":true}).to_string()});
    let (_, count) = exercise("install", DESCRIPTION, response, true).await;
    assert_eq!(count, 1);
}

#[tokio::test]
async fn a_template_wrong_name_or_missing_managed_tag_never_runs_the_helper() {
    for config in [
        json!({"name":"opy-service-01","tags":"octopushy","description":DESCRIPTION,"template":1}),
        json!({"name":"foreign","tags":"octopushy","description":DESCRIPTION}),
        json!({"name":"opy-service-01","description":DESCRIPTION}),
    ] {
        let (_, count) = exercise_vm("prepare", config, prepared(), false).await;
        assert_eq!(count, 0);
    }
}
