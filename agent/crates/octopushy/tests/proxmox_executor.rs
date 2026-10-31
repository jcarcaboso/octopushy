//! The proxmox executor's four actions against mock-proxmox.

use std::collections::HashMap;
use std::sync::Arc;
use std::time::Duration;

use mock_proxmox::{Fixture, Options, RunningMock};
use octopushy::attempts::logs::{AttemptLogger, LogBuffer};
use octopushy::executors::{self, ExecContext, ExecutorConfig, Failure, Secrets, Success};
use octopushy::masking::Masker;
use octopushy::tools::{RuntimeKind, ToolRuntime};
use serde_json::{Value, json};
use tokio::sync::Notify;
use tokio_util::sync::CancellationToken;

async fn mock(fixture: Fixture) -> RunningMock {
    mock_proxmox::start(
        fixture,
        Options {
            listen: "127.0.0.1:0".parse().unwrap(),
            tls: true,
        },
    )
    .await
    .unwrap()
}

fn connection(m: &RunningMock) -> Value {
    json!({ "endpoint": m.endpoint(), "verifyTls": false, "caCertificatePem": m.ca_pem() })
}

async fn exec(action: &str, input: Value, token: &str) -> (Result<Success, Failure>, Vec<String>) {
    let dir = tempfile::tempdir().unwrap();
    let cfg = ExecutorConfig {
        runtime: ToolRuntime::new(RuntimeKind::Local, None, "false".into(), None),
        provider_cache: dir.path().join("cache"),
        poll_interval: Duration::from_millis(20),
        proxmox_timeout: Duration::from_secs(5),
    };
    let secrets = HashMap::from([("proxmox.token".to_owned(), token.to_owned())]);
    let buffer = Arc::new(LogBuffer::default());
    let masker = Arc::new(Masker::new(secrets.values().map(String::as_str)));
    let ctx = ExecContext {
        attempt_id: "t".into(),
        action: action.into(),
        input_json: input.to_string(),
        secrets: Secrets::new(secrets),
        files_tar_gz: vec![],
        artifacts: vec![],
        workspace: dir.path().to_path_buf(),
        log: AttemptLogger::new(buffer.clone(), masker, Arc::new(Notify::new())),
        cancel: CancellationToken::new(),
        snapshots: Default::default(),
    };
    let result = executors::run(&cfg, "proxmox", &ctx).await;
    let lines = buffer
        .batch_from(0, usize::MAX)
        .map(|(_, l)| l.into_iter().map(|x| x.text).collect())
        .unwrap_or_default();
    (result, lines)
}

fn failed(r: Result<Success, Failure>) -> (String, Option<Value>) {
    match r {
        Err(Failure::Failed { message, outputs }) => (message, outputs),
        other => panic!("expected a failure, got {other:?}"),
    }
}

#[tokio::test]
async fn observe_standalone_and_offline_hosts() {
    let mut fixture = Fixture::example();
    fixture.cluster = None;
    fixture.nodes[1].online = false;
    let m = mock(fixture).await;
    let (r, lines) = exec(
        "observe",
        json!({ "connection": connection(&m) }),
        &m.token(),
    )
    .await;
    let out = r.unwrap().outputs;
    assert_eq!(out["cluster"], Value::Null);
    assert_eq!(out["hosts"][0]["online"], true);
    assert_eq!(out["hosts"][0]["memoryBytes"], 12_884_901_888u64);
    assert_eq!(
        out["hosts"][1],
        json!({
            "name": "moraband", "online": false, "cpu": null, "maxCpu": null,
            "memoryBytes": null, "maxMemoryBytes": null, "uptimeSeconds": null
        })
    );
    let g310 = out["guests"]
        .as_array()
        .unwrap()
        .iter()
        .find(|g| g["vmid"] == 310)
        .unwrap()
        .clone();
    assert_eq!(g310["status"], "unknown", "guests of an offline host");
    assert!(lines.iter().any(|l| l.contains("standalone host")));
}

#[tokio::test]
async fn wrong_token_and_unreachable_api_fail() {
    let m = mock(Fixture::example()).await;
    let bad = "octopushy@pve!agent=wrong-wrong-wrong";
    let (r, lines) = exec("observe", json!({ "connection": connection(&m) }), bad).await;
    let (message, _) = failed(r);
    assert!(message.contains("401"), "{message}");
    assert!(lines.iter().all(|l| !l.contains("wrong-wrong")));

    let (r, _) = exec(
        "observe",
        json!({ "connection": { "endpoint": "https://127.0.0.1:1/" , "verifyTls": false } }),
        &m.token(),
    )
    .await;
    let (message, _) = failed(r);
    assert!(message.contains("could not reach"), "{message}");

    let (r, _) = exec(
        "observe",
        json!({ "connection": connection(&m) }),
        "not-a-token",
    )
    .await;
    assert!(failed(r).0.contains("USER@REALM"));

    let (r, _) = exec("explode", json!({}), &m.token()).await;
    assert!(failed(r).0.contains("unknown proxmox action"));
}

#[tokio::test]
async fn verify_guest_checks() {
    let m = mock(Fixture::example()).await;
    let t = m.token();
    let input = |vmid: u32, node: &str, name: &str, ip: Value, wait: u64| {
        json!({
            "connection": connection(&m), "node": node, "vmid": vmid, "name": name,
            "expectRunning": true, "expectIpv4": ip, "waitSeconds": wait
        })
    };

    // Everything matches.
    let (r, _) = exec(
        "verify-guest",
        input(210, "korriban", "krb-crt-01", json!("10.1.0.21"), 5),
        &t,
    )
    .await;
    let out = r.unwrap().outputs;
    assert_eq!(out["present"], true);
    assert_eq!(out["node"], "korriban");
    assert_eq!(out["status"], "running");
    assert_eq!(out["ipv4Addresses"], json!(["10.1.0.21"]));
    assert_eq!(
        out["checks"][0],
        json!({ "name": "present", "ok": true, "detail": "VM 210 exists" })
    );
    assert_eq!(out["checks"][4]["detail"], "guest agent reports 10.1.0.21");

    // DHCP: any address will do.
    let (r, _) = exec(
        "verify-guest",
        input(310, "moraband", "mrb-ai-01", Value::Null, 5),
        &t,
    )
    .await;
    assert!(r.is_ok());

    // Wrong address, wrong node, wrong name: fails with outputs.
    let (r, _) = exec(
        "verify-guest",
        input(210, "moraband", "other", json!("10.1.0.99"), 0),
        &t,
    )
    .await;
    let (message, outputs) = failed(r);
    let outputs = outputs.unwrap();
    let failing: Vec<&str> = outputs["checks"]
        .as_array()
        .unwrap()
        .iter()
        .filter(|c| c["ok"] == false)
        .map(|c| c["name"].as_str().unwrap())
        .collect();
    assert_eq!(failing, ["placement", "name", "address"]);
    assert!(message.contains("not 10.1.0.99"), "{message}");

    // Guest agent not running.
    let (r, _) = exec(
        "verify-guest",
        input(211, "korriban", "krb-svc-01", Value::Null, 0),
        &t,
    )
    .await;
    let (message, outputs) = failed(r);
    assert!(
        message.contains("guest agent is not available"),
        "{message}"
    );
    assert_eq!(outputs.unwrap()["present"], true);

    // Stopped VM.
    let (r, _) = exec(
        "verify-guest",
        input(150, "moraband", "legacy-win11", Value::Null, 0),
        &t,
    )
    .await;
    let (_, outputs) = failed(r);
    assert_eq!(outputs.unwrap()["status"], "stopped");

    // Missing VM.
    let (r, _) = exec(
        "verify-guest",
        input(404, "korriban", "x", Value::Null, 0),
        &t,
    )
    .await;
    let (_, outputs) = failed(r);
    assert_eq!(
        outputs.unwrap()["checks"],
        json!([{ "name": "present", "ok": false, "detail": "VM 404 does not exist" }])
    );

    // Bad input.
    let (r, _) = exec(
        "verify-guest",
        input(210, "korriban", "krb-crt-01", json!("not-an-ip"), 0),
        &t,
    )
    .await;
    assert!(failed(r).0.contains("expectIpv4"));
}

#[tokio::test]
async fn verify_guest_waits_for_the_guest_agent() {
    let m = Arc::new(mock(Fixture::example()).await);
    let m2 = m.clone();
    tokio::spawn(async move {
        tokio::time::sleep(Duration::from_millis(200)).await;
        let mut vm = m2.vm(211).unwrap();
        vm.ipv4 = Some(vec!["10.1.0.22".into()]);
        m2.upsert_vm(vm);
    });
    let (r, lines) = exec("verify-guest", json!({
        "connection": connection(&m), "node": "korriban", "vmid": 211, "expectIpv4": "10.1.0.22", "waitSeconds": 30
    }), &m.token()).await;
    assert!(r.is_ok(), "{r:?}");
    assert!(
        lines.iter().any(|l| l.starts_with("waiting: address")),
        "{lines:?}"
    );
}

#[tokio::test]
async fn verify_absent_and_privileges() {
    let m = mock(Fixture::example()).await;
    let t = m.token();
    let (r, _) = exec(
        "verify-absent",
        json!({ "connection": connection(&m), "vmid": 404, "waitSeconds": 0 }),
        &t,
    )
    .await;
    assert_eq!(r.unwrap().outputs, json!({ "present": false }));

    let (r, _) = exec(
        "verify-absent",
        json!({ "connection": connection(&m), "vmid": 210, "waitSeconds": 0 }),
        &t,
    )
    .await;
    let (message, outputs) = failed(r);
    assert_eq!(outputs, Some(json!({ "present": true })));
    assert!(message.contains("still exists on korriban"), "{message}");

    // Removed while waiting.
    let m = Arc::new(m);
    let m2 = m.clone();
    tokio::spawn(async move {
        tokio::time::sleep(Duration::from_millis(150)).await;
        m2.remove_vm(210);
    });
    let (r, _) = exec(
        "verify-absent",
        json!({ "connection": connection(&m), "vmid": 210, "waitSeconds": 30 }),
        &t,
    )
    .await;
    assert!(r.is_ok());

    // Without VM.Audit, "not listed" proves nothing.
    let mut fixture = Fixture::example();
    fixture.privileges = Some(vec!["Sys.Audit".into()]);
    let m = mock(fixture).await;
    let (r, _) = exec(
        "verify-absent",
        json!({ "connection": connection(&m), "vmid": 404 }),
        &m.token(),
    )
    .await;
    assert!(failed(r).0.contains("lacks VM.Audit"));
}

#[tokio::test]
async fn set_protection() {
    let m = mock(Fixture::example()).await;
    let t = m.token();
    let input = |vmid: u32, node: &str, p: bool| json!({ "connection": connection(&m), "node": node, "vmid": vmid, "protection": p });

    let (r, _) = exec("set-protection", input(210, "korriban", false), &t).await;
    assert_eq!(
        r.unwrap().outputs,
        json!({ "absent": false, "previous": true, "protection": false })
    );
    assert!(!m.vm(210).unwrap().protection);

    // Idempotent.
    let (r, lines) = exec("set-protection", input(210, "korriban", false), &t).await;
    assert_eq!(
        r.unwrap().outputs,
        json!({ "absent": false, "previous": false, "protection": false })
    );
    assert!(lines.iter().any(|l| l.contains("already off")));

    let (r, _) = exec("set-protection", input(210, "korriban", true), &t).await;
    assert_eq!(r.unwrap().outputs["protection"], true);
    assert!(m.vm(210).unwrap().protection);

    // Absent VM succeeds.
    let (r, _) = exec("set-protection", input(404, "korriban", false), &t).await;
    assert_eq!(
        r.unwrap().outputs,
        json!({ "absent": true, "previous": null, "protection": null })
    );

    // On another node than expected: refused, nothing changed.
    let (r, _) = exec("set-protection", input(310, "korriban", false), &t).await;
    assert!(failed(r).0.contains("is on moraband"));
    assert!(m.vm(310).unwrap().protection);
}
