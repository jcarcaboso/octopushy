//! Approved OS-update dispatch against a synthetic QGA API. No guest command runs.
use octopushy::{
    attempts::logs::{AttemptLogger, LogBuffer},
    executors::{self, ExecContext, ExecutorConfig, Failure, Secrets},
    masking::Masker,
    tools::{RuntimeKind, ToolRuntime},
};
use serde_json::{Value, json};
use std::{collections::HashMap, sync::Arc, time::Duration};
use tokio::sync::Notify;
use tokio_util::sync::CancellationToken;
use wiremock::{
    Mock, MockServer, ResponseTemplate,
    matchers::{method, path},
};

const GENERATION: &str = "884d09b4-0934-43a4-bffb-d2f7977bc01c";
const SYSTEM: &str = "/nix/store/00000000000000000000000000000000-nixos-system-worker-test";
const EXEC: &str = "/api2/json/nodes/n1/qemu/310/agent/exec";
const STATUS: &str = "/api2/json/nodes/n1/qemu/310/agent/exec-status";
fn data(value: Value) -> ResponseTemplate {
    ResponseTemplate::new(200).set_body_json(json!({"data":value}))
}
fn evidence() -> Value {
    json!({"protocolVersion":1,"generation":GENERATION,"systemPath":SYSTEM,"archiveSha256":"c".repeat(64),
        "identityPreserved":true,"stateFilesystemPreserved":true})
}
async fn fixture(description: &str) -> MockServer {
    let server = MockServer::start().await;
    Mock::given(method("GET"))
        .and(path("/api2/json/cluster/resources"))
        .respond_with(data(json!([
            {"vmid":310,"node":"n1","name":"worker","type":"qemu","status":"running"}
        ])))
        .mount(&server)
        .await;
    Mock::given(method("GET"))
        .and(path("/api2/json/nodes/n1/qemu/310/config"))
        .respond_with(data(
            json!({"name":"worker","tags":"octopushy","description":description}),
        ))
        .mount(&server)
        .await;
    Mock::given(method("GET"))
        .and(path("/api2/json/access/permissions"))
        .respond_with(data(json!({"/vms/310":{"VM.GuestAgent.Unrestricted":1}})))
        .mount(&server)
        .await;
    server
}
async fn execute(server: &MockServer, action: &str) -> Result<executors::Success, Failure> {
    execute_release(server, action, None).await
}
async fn execute_release(
    server: &MockServer,
    action: &str,
    edit: Option<(&str, Value)>,
) -> Result<executors::Success, Failure> {
    let directory = tempfile::tempdir().unwrap();
    let cfg = ExecutorConfig {
        runtime: ToolRuntime::new(RuntimeKind::Local, None, "false".into(), None),
        provider_cache: directory.path().join("cache"),
        poll_interval: Duration::from_millis(1),
        proxmox_timeout: Duration::from_secs(2),
    };
    let secrets = HashMap::from([("proxmox.token".into(), "user@pve!token=synthetic".into())]);
    let mut input = json!({
        "connection":{"endpoint":server.uri(),"verifyTls":false},
        "node":"n1","vmid":310,"name":"worker","address":"home/pve/worker","generation":GENERATION,"waitSeconds":1860,
        "update":{"agentId":GENERATION,"archiveUrl":"https://releases.test/worker.nar.xz",
            "archiveSha256":"c".repeat(64),"systemPath":SYSTEM,"imageSha256":"a".repeat(64)}
    });
    if let Some((field, value)) = edit {
        input["update"][field] = value;
    }
    let ctx = ExecContext {
        attempt_id: "update".into(),
        action: action.into(),
        input_json: input.to_string(),
        secrets: Secrets::new(secrets),
        files_tar_gz: vec![],
        artifacts: vec![],
        workspace: directory.path().to_path_buf(),
        log: AttemptLogger::new(
            Arc::new(LogBuffer::default()),
            Arc::new(Masker::new(["synthetic"])),
            Arc::new(Notify::new()),
        ),
        cancel: CancellationToken::new(),
    };
    executors::run(&cfg, "managed-worker", &ctx).await
}
#[tokio::test]
async fn update_uses_a_fixed_native_program_and_requires_exact_public_preservation_evidence() {
    let server = fixture(&format!(
        "Managed by Octopushy as home/pve/worker.\nworker-generation: {GENERATION}"
    ))
    .await;
    Mock::given(method("POST"))
        .and(path(EXEC))
        .respond_with(data(json!({"pid":42})))
        .expect(1)
        .mount(&server)
        .await;
    Mock::given(method("GET"))
        .and(path(STATUS))
        .respond_with(data(
            json!({"exited":1,"exitcode":0,"out-data":evidence().to_string()}),
        ))
        .mount(&server)
        .await;
    assert!(executors::is_mutating("managed-worker", "update-os"));
    assert!(!executors::is_mutating("managed-worker", "verify-os"));
    assert_eq!(
        execute(&server, "update-os").await.unwrap().outputs,
        evidence()
    );
    let requests = server.received_requests().await.unwrap();
    let body: Value = requests
        .iter()
        .find(|r| r.method == "POST")
        .unwrap()
        .body_json()
        .unwrap();
    assert_eq!(body["command"][0], "/run/current-system/sw/bin/flock");
    assert!(body.to_string().contains("nix-prefetch-url"));
    assert!(!body.to_string().contains("user@pve!token=synthetic"));
    assert!(body.to_string().contains("switch-to-configuration"));
}
#[tokio::test]
async fn foreign_worker_never_receives_an_os_command() {
    let server = fixture("foreign").await;
    assert!(execute(&server, "update-os").await.is_err());
    assert!(
        server
            .received_requests()
            .await
            .unwrap()
            .iter()
            .all(|r| r.method != "POST")
    );
}
#[tokio::test]
async fn an_unconfirmed_update_or_missing_preservation_evidence_is_uncertain_and_never_replayed() {
    let mut extra = evidence();
    extra["privateKey"] = json!("private-output");
    let mut not_preserved = evidence();
    not_preserved["stateFilesystemPreserved"] = json!(false);
    for result in [
        None,
        Some(json!({"exited":1,"exitcode":0,"out-data":"private-output"})),
        Some(json!({"exited":1,"exitcode":1,"err-data":"private-output"})),
        Some(json!({"exited":1,"exitcode":0,"out-data":extra.to_string()})),
        Some(json!({"exited":1,"exitcode":0,"out-data":not_preserved.to_string()})),
    ] {
        let server = fixture(&format!(
            "Managed by Octopushy as home/pve/worker.\nworker-generation: {GENERATION}"
        ))
        .await;
        Mock::given(method("POST"))
            .and(path(EXEC))
            .respond_with(if result.is_some() {
                data(json!({"pid":42}))
            } else {
                ResponseTemplate::new(500)
            })
            .expect(1)
            .mount(&server)
            .await;
        if let Some(result) = result {
            Mock::given(method("GET"))
                .and(path(STATUS))
                .respond_with(data(result))
                .mount(&server)
                .await;
        }
        let failure = execute(&server, "update-os").await.unwrap_err();
        assert!(matches!(failure, Failure::Uncertain(_)));
        assert!(!format!("{failure:?}").contains("private-output"));
    }
}

#[tokio::test]
async fn invalid_release_metadata_never_reaches_proxmox() {
    for (field, value) in [
        ("archiveUrl", "http://release.test/worker.nar.xz"),
        (
            "archiveUrl",
            "https://user:password@release.test/worker.nar.xz",
        ),
        (
            "archiveUrl",
            "https://release.test/worker.nar.xz?token=secret",
        ),
        ("archiveSha256", "not-a-hash"),
        (
            "systemPath",
            "/nix/store/00000000000000000000000000000000-nixos-system-worker;sh",
        ),
        (
            "systemPath",
            "/nix/store/00000000000000000000000000000000-nixos-system-worker/../foreign",
        ),
        ("agentId", "not-a-uuid"),
        ("caCertificatePem", "-----BEGIN PRIVATE KEY-----"),
    ] {
        let server = fixture("unused").await;
        assert!(matches!(
            execute_release(&server, "update-os", Some((field, json!(value)))).await,
            Err(Failure::Failed { .. })
        ));
        assert!(server.received_requests().await.unwrap().is_empty());
    }
}

#[tokio::test]
async fn a_busy_lock_or_confirmed_permission_denial_is_not_an_unknown_mutation() {
    for forbidden in [true, false] {
        let server = fixture(&format!(
            "Managed by Octopushy as home/pve/worker.\nworker-generation: {GENERATION}"
        ))
        .await;
        Mock::given(method("POST"))
            .and(path(EXEC))
            .respond_with(if forbidden {
                ResponseTemplate::new(403)
            } else {
                data(json!({"pid":42}))
            })
            .expect(1)
            .mount(&server)
            .await;
        if !forbidden {
            Mock::given(method("GET"))
                .and(path(STATUS))
                .respond_with(data(
                    json!({"exited":1,"exitcode":75,"err-data":"private-output"}),
                ))
                .mount(&server)
                .await;
        }
        let result = execute(&server, "update-os").await;
        assert!(matches!(result, Err(Failure::Failed { .. })));
        assert!(!format!("{result:?}").contains("private-output"));
    }
}

#[tokio::test]
async fn verification_returns_the_same_evidence_but_never_marks_a_read_failure_as_a_mutation() {
    let server = fixture(&format!(
        "Managed by Octopushy as home/pve/worker.\nworker-generation: {GENERATION}"
    ))
    .await;
    Mock::given(method("POST"))
        .and(path(EXEC))
        .respond_with(data(json!({"pid":42})))
        .expect(1)
        .mount(&server)
        .await;
    Mock::given(method("GET"))
        .and(path(STATUS))
        .respond_with(data(
            json!({"exited":1,"exitcode":0,"out-data":evidence().to_string()}),
        ))
        .mount(&server)
        .await;
    assert_eq!(
        execute(&server, "verify-os").await.unwrap().outputs,
        evidence()
    );
    let server = fixture(&format!(
        "Managed by Octopushy as home/pve/worker.\nworker-generation: {GENERATION}"
    ))
    .await;
    Mock::given(method("POST"))
        .and(path(EXEC))
        .respond_with(ResponseTemplate::new(500))
        .expect(1)
        .mount(&server)
        .await;
    assert!(matches!(
        execute(&server, "verify-os").await,
        Err(Failure::Failed { .. })
    ));
}
