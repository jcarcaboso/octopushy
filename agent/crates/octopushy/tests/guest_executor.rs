//! Guest installation orchestration, without executing anything inside a real VM.
use std::collections::HashMap;
use std::sync::{
    Arc,
    atomic::{AtomicUsize, Ordering},
};
use std::time::Duration;

use octopushy::attempts::logs::{AttemptLogger, LogBuffer};
use octopushy::executors::{self, ExecContext, ExecutorConfig, Failure, Secrets, Success};
use octopushy::masking::Masker;
use octopushy::tools::{RuntimeKind, ToolRuntime};
use serde_json::{Value, json};
use tokio::sync::Notify;
use tokio_util::sync::CancellationToken;
use wiremock::matchers::{method, path, query_param};
use wiremock::{Mock, MockServer, ResponseTemplate};

const EXEC: &str = "/api2/json/nodes/n1/qemu/310/agent/exec";
const STATUS: &str = "/api2/json/nodes/n1/qemu/310/agent/exec-status";

fn data(value: Value) -> ResponseTemplate {
    ResponseTemplate::new(200).set_body_json(json!({ "data": value }))
}

async fn server(node: &str, name: &str, template: bool, privilege: &str) -> MockServer {
    let server = MockServer::start().await;
    Mock::given(method("GET"))
        .and(path("/api2/json/cluster/resources"))
        .respond_with(data(json!([{
            "vmid": 310, "node": node, "name": name, "type": "qemu",
            "template": template, "status": "running"
        }])))
        .mount(&server)
        .await;
    Mock::given(method("GET"))
        .and(path("/api2/json/access/permissions"))
        .and(query_param("path", "/vms/310"))
        .respond_with(data(json!({ "/vms/310": { privilege: 1 } })))
        .mount(&server)
        .await;
    server
}

async fn exec(server: &MockServer) -> (Result<Success, Failure>, Vec<String>) {
    let dir = tempfile::tempdir().unwrap();
    let cfg = ExecutorConfig {
        runtime: ToolRuntime::new(RuntimeKind::Local, None, "false".into(), None),
        provider_cache: dir.path().join("cache"),
        poll_interval: Duration::from_millis(1),
        proxmox_timeout: Duration::from_secs(2),
    };
    let secrets = HashMap::from([
        ("proxmox.token".into(), "user@pve!agent=secret".into()),
        ("disk-reporter.token".into(), "b".repeat(64)),
    ]);
    let buffer = Arc::new(LogBuffer::default());
    let masker = Arc::new(Masker::new(secrets.values().map(String::as_str)));
    let ctx = ExecContext {
        attempt_id: "t".into(),
        action: "install-disk-reporter".into(),
        input_json: json!({
            "connection": { "endpoint": server.uri(), "verifyTls": false },
            "node": "n1", "vmid": 310, "name": "test-vm",
            "reporter": {
                "endpoint": "https://octopushy.test/api/guests/test/disk-usage",
                "binaryUrl": "https://releases.test/reporter",
                "binarySha256": "a".repeat(64),
                "intervalSeconds": 3600
            }
        })
        .to_string(),
        secrets: Secrets::new(secrets),
        files_tar_gz: vec![],
        artifacts: vec![],
        workspace: dir.path().to_path_buf(),
        log: AttemptLogger::new(buffer.clone(), masker, Arc::new(Notify::new())),
        cancel: CancellationToken::new(),
    };
    let result = executors::run(&cfg, "guest", &ctx).await;
    let lines = buffer
        .batch_from(0, usize::MAX)
        .map(|(_, lines)| lines.into_iter().map(|l| l.text).collect())
        .unwrap_or_default();
    (result, lines)
}

#[tokio::test]
async fn install_uses_bounded_stdin_command_waits_for_exit_and_discards_guest_output() {
    let server = server("n1", "test-vm", false, "VM.GuestAgent.Unrestricted").await;
    Mock::given(method("POST"))
        .and(path(EXEC))
        .respond_with(data(json!({ "pid": 42 })))
        .expect(1)
        .mount(&server)
        .await;
    let polls = AtomicUsize::new(0);
    Mock::given(method("GET"))
        .and(path(STATUS))
        .and(query_param("pid", "42"))
        .respond_with(move |_: &wiremock::Request| {
            if polls.fetch_add(1, Ordering::SeqCst) == 0 {
                data(json!({ "exited": 0 }))
            } else {
                data(json!({ "exited": 1, "exitcode": 0, "out-data": "private-base64-secret" }))
            }
        })
        .expect(2)
        .mount(&server)
        .await;
    let (result, logs) = exec(&server).await;
    assert_eq!(result.unwrap().outputs["firstReportSent"], true);
    assert!(!logs.join("\n").contains("private-base64-secret"));
    let requests = server.received_requests().await.unwrap();
    let request = requests.iter().find(|r| r.method == "POST").unwrap();
    let body: Value = request.body_json().unwrap();
    assert_eq!(
        body["command"],
        json!(["/usr/bin/timeout", "--kill-after=5s", "240s", "/bin/sh"])
    );
    let script = body["input-data"].as_str().unwrap();
    assert!(script.starts_with("set -eu\n"));
    assert!(script.contains("sha256sum --check --status"));
    assert!(script.contains("systemctl start octopushy-disk-reporter.service"));
    assert!(executors::is_mutating("guest", "install-disk-reporter"));
}

#[tokio::test]
async fn foreign_targets_and_missing_privileges_never_execute_guest_commands() {
    for (node, name, template, privilege) in [
        ("elsewhere", "test-vm", false, "VM.GuestAgent.Unrestricted"),
        ("n1", "another-vm", false, "VM.GuestAgent.Unrestricted"),
        ("n1", "test-vm", true, "VM.GuestAgent.Unrestricted"),
        ("n1", "test-vm", false, "VM.Audit"),
    ] {
        let server = server(node, name, template, privilege).await;
        assert!(matches!(exec(&server).await.0, Err(Failure::Failed { .. })));
        assert!(
            server
                .received_requests()
                .await
                .unwrap()
                .iter()
                .all(|r| r.method == "GET")
        );
    }
}

#[tokio::test]
async fn uncertain_dispatch_or_lost_status_is_not_retried_and_known_exit_is_failed() {
    for (dispatch_status, status, uncertain) in [
        (500, data(json!({})), true),
        (
            200,
            ResponseTemplate::new(500).set_body_string("private-base64-secret"),
            true,
        ),
        (
            200,
            data(json!({ "exited": 1, "exitcode": 1, "err-data": "private-base64-secret" })),
            false,
        ),
    ] {
        let server = server("n1", "test-vm", false, "VM.Monitor").await;
        Mock::given(method("POST"))
            .and(path(EXEC))
            .respond_with(
                ResponseTemplate::new(dispatch_status)
                    .set_body_json(json!({ "data": { "pid": 42 } })),
            )
            .expect(1)
            .mount(&server)
            .await;
        Mock::given(method("GET"))
            .and(path(STATUS))
            .respond_with(status)
            .mount(&server)
            .await;
        let (result, logs) = exec(&server).await;
        let error = result.unwrap_err();
        assert_eq!(
            matches!(error, Failure::Uncertain(_)),
            uncertain,
            "{error:?}"
        );
        assert!(!format!("{error:?}{logs:?}").contains("private-base64-secret"));
    }
}
