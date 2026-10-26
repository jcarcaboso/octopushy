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
    name: &str,
    response: Value,
    expected_ok: bool,
) -> (Result<executors::Success, Failure>, usize) {
    exercise_generation(action, name, response, expected_ok, None).await
}
async fn exercise_generation(
    action: &str,
    name: &str,
    response: Value,
    expected_ok: bool,
    generation: Option<&str>,
) -> (Result<executors::Success, Failure>, usize) {
    let description = if generation.is_some() {
        "Managed by Octopushy as home/umbrel/opy-service-01.\nworker-generation: 884d09b4-0934-43a4-bffb-d2f7977bc01c"
    } else {
        "Managed by Octopushy as home/umbrel/opy-service-01."
    };
    let posts = Arc::new(AtomicUsize::new(0));
    let counter = posts.clone();
    let name = name.to_string();
    let app = Router::new()
        .route(
            "/api2/json/nodes/umbrel/qemu/9210/config",
            get(move || {
                let name = name.clone();
                async move {
                    Json(json!({"data":{"name":name,"tags":"octopushy","description":description}}))
                }
            }),
        )
        .route(
            "/api2/json/nodes/umbrel/qemu/9210/agent/exec",
            post(move |Json(body): Json<Value>| {
                counter.fetch_add(1, Ordering::SeqCst);
                assert_eq!(body["command"][0], "/run/current-system/sw/bin/flock");
                assert_eq!(body["command"][4], "/run/lock/octopushy-nixos.lock");
                assert_eq!(
                    body["command"].as_array().unwrap().last().unwrap(),
                    "27917287424"
                );
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
            "diskBytes":27917287424u64,"waitSeconds":1,"workerGeneration":generation}).to_string(),
        secrets: Secrets::new(HashMap::from([("proxmox.token".into(),"test@pve!test=secret".into())])),
        files_tar_gz: vec![], artifacts: vec![], workspace: work.path().into(),
        log: AttemptLogger::new(Arc::new(LogBuffer::default()), Arc::new(Masker::new(["secret"])), Arc::new(Notify::new())),
        cancel: CancellationToken::new(),
    };
    let result = executors::run(&cfg, "nixos-rootfs", &ctx).await;
    server.abort();
    assert_eq!(result.is_ok(), expected_ok, "{result:?}");
    (result, posts.load(Ordering::SeqCst))
}

fn report(fs_bytes: u64) -> Value {
    json!({"exited":1,"exitcode":0,"out-data":json!({
        "diskBytes":27917287424u64,"partitionBytes":27647786496u64,
        "filesystemBytes":fs_bytes,"usableBytes":27102232576u64,
        "uuid":"f222513b-ded1-49fa-b591-20ce86a2fe7f","changed":false
    }).to_string()})
}

#[tokio::test]
async fn verifies_real_capacity_and_keeps_mutation_and_verification_distinct() {
    for action in ["grow", "verify"] {
        let (result, count) = exercise(action, "opy-service-01", report(27647782912), true).await;
        assert_eq!(count, 1);
        assert_eq!(result.unwrap().outputs["changed"], false);
    }
    assert!(executors::is_mutating("nixos-rootfs", "grow"));
    assert!(!executors::is_mutating("nixos-rootfs", "verify"));
}

#[tokio::test]
async fn refuses_the_wrong_vm_before_guest_execution() {
    let (_, count) = exercise("grow", "unrelated-vm", report(27647782912), false).await;
    assert_eq!(count, 0);
}

#[tokio::test]
async fn refuses_an_unexpanded_filesystem_and_nonzero_exit() {
    let _ = exercise("verify", "opy-service-01", report(16910364672), false).await;
    let (r, count) = exercise(
        "grow",
        "opy-service-01",
        json!({"exited":1,"exitcode":1,"err-data":"unsupported layout"}),
        false,
    )
    .await;
    assert!(matches!(r, Err(Failure::Failed { .. })));
    assert_eq!(count, 1);
}

#[tokio::test]
async fn lost_status_after_mutation_is_uncertain_and_never_resubmits() {
    let (r, count) = exercise("grow", "opy-service-01", json!({"exited":0}), false).await;
    assert!(matches!(r, Err(Failure::Uncertain(_))));
    assert_eq!(count, 1);
}

#[tokio::test]
async fn managed_root_growth_checks_the_exact_generation_marker() {
    let (_, count) = exercise_generation(
        "grow",
        "opy-service-01",
        report(27647782912),
        true,
        Some("884d09b4-0934-43a4-bffb-d2f7977bc01c"),
    )
    .await;
    assert_eq!(count, 1);
    let (_, count) = exercise_generation(
        "grow",
        "opy-service-01",
        report(27647782912),
        false,
        Some("884d09b4-0934-43a4-bffb-d2f7977bc01d"),
    )
    .await;
    assert_eq!(count, 0);
}
