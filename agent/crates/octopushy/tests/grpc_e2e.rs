//! End to end over real mutual TLS: enrollment → connect → hello → assignment
//! (proxmox against mock-proxmox) → logs → result → ack → outbox emptied; plus
//! certificate renewal, restart recovery, cancellation, and reconnects.

mod common;

use std::collections::HashMap;
use std::os::unix::fs::PermissionsExt;
use std::path::Path;
use std::time::Duration;

use common::{AGENT_ID, Event, FakeServer, Options, log_lines};
use mock_proxmox::{Fixture, Options as MockOptions, RunningMock};
use octopushy::enrollment::{EnrollOptions, EnrollmentToken, enroll};
use octopushy::outbox::{Outbox, OutboxRecord};
use octopushy::run::{RunOptions, run};
use octopushy::tools::RuntimeKind;
use octopushy_proto::v1::{
    Assignment, AttemptResult, CancelAttempt, Outcome, ResultAck, agent_message::Kind as AgentKind,
    server_message::Kind as ServerKind,
};
use serde_json::{Value, json};
use tokio_util::sync::CancellationToken;

async fn mock() -> RunningMock {
    mock_proxmox::start(
        Fixture::example(),
        MockOptions {
            listen: "127.0.0.1:0".parse().unwrap(),
            tls: true,
        },
    )
    .await
    .unwrap()
}

async fn enroll_into(server: &FakeServer, dir: &Path) {
    enroll(EnrollOptions {
        server: server.url.clone(),
        token: EnrollmentToken::parse(&server.token()).unwrap(),
        name: Some("test-agent".into()),
        state_dir: dir.to_path_buf(),
        force: false,
    })
    .await
    .unwrap();
}

/// Starts the agent in the background; returns its shutdown token and task.
fn start_agent(
    dir: &Path,
) -> (
    CancellationToken,
    tokio::task::JoinHandle<anyhow::Result<()>>,
) {
    let mut opts = RunOptions::new(dir.to_path_buf());
    opts.tool_runtime = RuntimeKind::Local;
    opts.tofu_binary = "/bin/false".into();
    opts.poll_interval = Duration::from_millis(100);
    let shutdown = CancellationToken::new();
    let task = tokio::spawn(run(opts, shutdown.clone()));
    (shutdown, task)
}

fn connection(mock: &RunningMock) -> Value {
    json!({ "endpoint": mock.endpoint(), "verifyTls": true, "caCertificatePem": mock.ca_pem() })
}

fn proxmox_assignment(id: &str, action: &str, input: Value, mock: &RunningMock) -> Assignment {
    Assignment {
        attempt_id: id.into(),
        executor: "proxmox".into(),
        action: action.into(),
        input_json: input.to_string(),
        secrets: HashMap::from([("proxmox.token".to_owned(), mock.token())]),
        lease_seconds: 60,
        ..Default::default()
    }
}

async fn wait_until(what: &str, mut f: impl FnMut() -> bool) {
    for _ in 0..300 {
        if f() {
            return;
        }
        tokio::time::sleep(Duration::from_millis(50)).await;
    }
    panic!("timed out waiting until {what}");
}

fn dir_is_empty(p: &Path) -> bool {
    std::fs::read_dir(p)
        .map(|mut d| d.next().is_none())
        .unwrap_or(true)
}

#[tokio::test]
async fn enroll_connect_observe_ack() {
    common::init_tracing();
    let mock = mock().await;
    let mut server = FakeServer::start(Options::default()).await;
    let state = tempfile::tempdir().unwrap();

    // Enrollment, with the CA pinned by the token's fingerprint.
    enroll_into(&server, state.path()).await;
    let name = server
        .wait_for(|e| match e {
            Event::Enrolled { name } => Some(name.clone()),
            _ => None,
        })
        .await;
    assert_eq!(name, "test-agent");
    let agent_json: Value =
        serde_json::from_slice(&std::fs::read(state.path().join("agent.json")).unwrap()).unwrap();
    assert_eq!(agent_json["agentId"], AGENT_ID);
    let key_mode = std::fs::metadata(state.path().join("key.pem"))
        .unwrap()
        .permissions()
        .mode();
    assert_eq!(key_mode & 0o777, 0o600);
    assert_eq!(
        octopushy::tls::single_cert_der(
            &std::fs::read_to_string(state.path().join("ca.pem")).unwrap()
        )
        .unwrap(),
        server.ca.cert_der
    );

    // A second enrollment needs --force.
    let again = enroll(EnrollOptions {
        server: server.url.clone(),
        token: EnrollmentToken::parse(&server.token()).unwrap(),
        name: None,
        state_dir: state.path().to_path_buf(),
        force: false,
    })
    .await
    .unwrap_err();
    assert!(again.to_string().contains("--force"), "{again}");

    // Connect: Hello first, with mutual TLS (the server saw our CN).
    let (shutdown, task) = start_agent(state.path());
    let cn = server
        .wait_for(|e| match e {
            Event::Connected { cn, .. } => Some(cn.clone()),
            _ => None,
        })
        .await;
    assert_eq!(cn, AGENT_ID);
    let hello = server
        .wait_for(|e| match e {
            Event::Message(m) => match &m.kind {
                Some(AgentKind::Hello(h)) => Some(h.clone()),
                _ => None,
            },
            _ => None,
        })
        .await;
    assert_eq!(hello.executors, ["opentofu", "proxmox"]);
    assert_eq!(hello.tool_runtime, "local");
    assert!(hello.running_attempt_ids.is_empty());
    assert_eq!(hello.agent_version, octopushy::VERSION);

    // An observe assignment against mock-proxmox, trusting its CA only.
    server
        .send(ServerKind::Assignment(proxmox_assignment(
            "att-observe-1",
            "observe",
            json!({ "connection": connection(&mock), "futureField": 1 }),
            &mock,
        )))
        .await;
    let (result, seen) = server.wait_result("att-observe-1").await;
    assert_eq!(result.outcome(), Outcome::Succeeded, "{}", result.error);
    assert!(seen.iter().any(
        |m| matches!(&m.kind, Some(AgentKind::Started(s)) if s.attempt_id == "att-observe-1")
    ));
    let out: Value = serde_json::from_str(&result.outputs_json).unwrap();
    assert_eq!(out["version"], "8.4.1");
    assert_eq!(out["cluster"], json!({ "name": "home", "quorate": true }));
    assert_eq!(out["hosts"].as_array().unwrap().len(), 2);
    assert_eq!(out["hosts"][0]["name"], "korriban");
    assert_eq!(out["hosts"][0]["maxCpu"], 16);
    let guests = out["guests"].as_array().unwrap();
    assert_eq!(guests.len(), 5);
    let crt = guests.iter().find(|g| g["vmid"] == 210).unwrap();
    assert_eq!(crt["tags"], json!(["nixos", "opentofu"]));
    assert_eq!(crt["kind"], "qemu");
    assert_eq!(crt["host"], "korriban");
    assert!(guests.iter().any(|g| g["template"] == true));

    let lines = log_lines(&seen, "att-observe-1");
    assert_eq!(lines.first().map(|l| l.0), Some(0), "sequences start at 0");
    assert!(
        lines.windows(2).all(|w| w[1].0 == w[0].0 + 1),
        "contiguous: {lines:?}"
    );
    assert!(
        lines.iter().any(|(_, t)| t.contains("cluster home")),
        "{lines:?}"
    );
    let secret = mock.token().split_once('=').unwrap().1.to_owned();
    assert!(lines.iter().all(|(_, t)| !t.contains(&secret)));

    // The result stays in the outbox until acknowledged.
    let outbox = state.path().join("outbox");
    assert!(outbox.join("att-observe-1.json").exists());
    assert!(state.path().join("work/att-observe-1").exists());
    server
        .send(ServerKind::ResultAck(ResultAck {
            attempt_id: "att-observe-1".into(),
        }))
        .await;
    wait_until("the outbox is empty", || dir_is_empty(&outbox)).await;
    wait_until("the workspace is gone", || {
        !state.path().join("work/att-observe-1").exists()
    })
    .await;

    // Heartbeats flow.
    server
        .wait_for(|e| match e {
            Event::Message(m) => matches!(m.kind, Some(AgentKind::Heartbeat(_))).then_some(()),
            _ => None,
        })
        .await;

    shutdown.cancel();
    tokio::time::timeout(Duration::from_secs(15), task)
        .await
        .expect("the agent stops")
        .unwrap()
        .unwrap();
}

#[tokio::test]
async fn refuses_a_ca_with_the_wrong_fingerprint() {
    let server = FakeServer::start(Options::default()).await;
    let state = tempfile::tempdir().unwrap();
    let token = format!("opy1.{}.{}", common::TOKEN_SECRET, "0".repeat(64));
    let err = enroll(EnrollOptions {
        server: server.url.clone(),
        token: EnrollmentToken::parse(&token).unwrap(),
        name: None,
        state_dir: state.path().to_path_buf(),
        force: false,
    })
    .await
    .unwrap_err();
    assert!(err.to_string().contains("does not match"), "{err}");
    assert!(dir_is_empty(state.path()));

    // A wrong token secret is refused by the server.
    let token = format!("opy1.wrongsecret.{}", server.ca.fingerprint());
    let err = enroll(EnrollOptions {
        server: server.url.clone(),
        token: EnrollmentToken::parse(&token).unwrap(),
        name: None,
        state_dir: state.path().to_path_buf(),
        force: false,
    })
    .await
    .unwrap_err();
    assert!(err.to_string().contains("refused"), "{err}");
}

#[tokio::test]
async fn renews_the_certificate_and_reconnects() {
    common::init_tracing();
    let mut server = FakeServer::start(Options {
        // 25 of 30 days used: renewal is due right away.
        enrolled_cert_age: Duration::from_secs(25 * 86_400),
        ..Options::default()
    })
    .await;
    let state = tempfile::tempdir().unwrap();
    enroll_into(&server, state.path()).await;
    let old_cert = std::fs::read_to_string(state.path().join("cert.pem")).unwrap();
    let old_key = std::fs::read_to_string(state.path().join("key.pem")).unwrap();

    let (shutdown, task) = start_agent(state.path());
    let first = server
        .wait_for(|e| match e {
            Event::Connected { cert_der, .. } => Some(cert_der.clone()),
            _ => None,
        })
        .await;
    server
        .wait_for(|e| matches!(e, Event::Renewed).then_some(()))
        .await;
    let second = server
        .wait_for(|e| match e {
            Event::Connected { cert_der, .. } => Some(cert_der.clone()),
            _ => None,
        })
        .await;
    assert_ne!(
        first, second,
        "the new connection uses the renewed certificate"
    );

    let new_cert = std::fs::read_to_string(state.path().join("cert.pem")).unwrap();
    let new_key = std::fs::read_to_string(state.path().join("key.pem")).unwrap();
    assert_ne!(old_cert, new_cert);
    assert_ne!(old_key, new_key);
    assert_eq!(octopushy::tls::single_cert_der(&new_cert).unwrap(), second);
    let info = octopushy::tls::CertInfo::parse(&second).unwrap();
    assert!(!info.needs_renewal(std::time::SystemTime::now()));
    assert_eq!(info.common_name.as_deref(), Some(AGENT_ID));

    shutdown.cancel();
    tokio::time::timeout(Duration::from_secs(15), task)
        .await
        .unwrap()
        .unwrap()
        .unwrap();
}

#[tokio::test]
async fn reports_interrupted_attempts_and_resends_the_outbox() {
    common::init_tracing();
    let mut server = FakeServer::start(Options {
        auto_ack: true,
        ..Options::default()
    })
    .await;
    let state = tempfile::tempdir().unwrap();
    enroll_into(&server, state.path()).await;

    // What a previous process left behind: an apply that was running, a
    // preview that was running, and a result it never got acknowledged.
    let work = state.path().join("work");
    let marker = |id: &str, executor: &str, action: &str| {
        std::fs::create_dir_all(work.join(id)).unwrap();
        std::fs::write(
            work.join(id).join("attempt.json"),
            json!({ "attemptId": id, "executor": executor, "action": action,
                    "phase": "running", "acceptedAtMs": 1, "startedAtMs": 2 })
            .to_string(),
        )
        .unwrap();
    };
    marker("apply-1", "opentofu", "apply");
    marker("preview-1", "opentofu", "preview");
    let outbox = Outbox::open(state.path().join("outbox")).unwrap();
    let done = AttemptResult {
        attempt_id: "done-1".into(),
        outcome: Outcome::Succeeded as i32,
        outputs_json: r#"{"present":false}"#.into(),
        ..Default::default()
    };
    outbox.put(&OutboxRecord::from_result(&done, 1)).unwrap();

    let (shutdown, task) = start_agent(state.path());
    let mut results = HashMap::new();
    while results.len() < 3 {
        if let Event::Message(m) = server.next_event().await {
            match m.kind {
                Some(AgentKind::Hello(h)) => assert!(h.running_attempt_ids.is_empty()),
                Some(AgentKind::Result(r)) => {
                    results.insert(r.attempt_id.clone(), r);
                }
                _ => {}
            }
        }
    }
    assert_eq!(results["apply-1"].outcome(), Outcome::Uncertain);
    assert_eq!(results["preview-1"].outcome(), Outcome::Failed);
    assert_eq!(
        results["preview-1"].error,
        "agent restarted during the attempt"
    );
    assert_eq!(results["done-1"], done);

    // Acknowledged: outbox and workspaces are cleaned up.
    wait_until("the outbox is empty", || {
        dir_is_empty(&state.path().join("outbox"))
    })
    .await;
    wait_until("workspaces are gone", || dir_is_empty(&work)).await;

    shutdown.cancel();
    tokio::time::timeout(Duration::from_secs(15), task)
        .await
        .unwrap()
        .unwrap()
        .unwrap();
}

#[tokio::test]
async fn attempts_survive_reconnects_and_can_be_cancelled() {
    common::init_tracing();
    let mock = mock().await;
    let mut server = FakeServer::start(Options {
        auto_ack: true,
        ..Options::default()
    })
    .await;
    let state = tempfile::tempdir().unwrap();
    enroll_into(&server, state.path()).await;
    let (shutdown, task) = start_agent(state.path());
    server
        .wait_for(|e| matches!(e, Event::Connected { .. }).then_some(()))
        .await;

    // VM 211's guest agent is not running yet, so verification waits.
    server
        .send(ServerKind::Assignment(proxmox_assignment(
            "verify-1",
            "verify-guest",
            json!({ "connection": connection(&mock), "node": "korriban", "vmid": 211,
                    "name": "krb-svc-01", "expectRunning": true, "expectIpv4": "10.1.0.22",
                    "waitSeconds": 120 }),
            &mock,
        )))
        .await;
    server
        .wait_for(|e| match e {
            Event::Message(m) => {
                matches!(&m.kind, Some(AgentKind::Log(l)) if l.attempt_id == "verify-1"
            && l.lines.iter().any(|x| x.text.contains("waiting")))
                .then_some(())
            }
            _ => None,
        })
        .await;

    // The server drops the stream; the agent reconnects and still runs it.
    server.disconnect();
    let hello = server
        .wait_for(|e| match e {
            Event::Message(m) => match &m.kind {
                Some(AgentKind::Hello(h)) => Some(h.clone()),
                _ => None,
            },
            _ => None,
        })
        .await;
    assert_eq!(hello.running_attempt_ids, ["verify-1"]);

    // The guest agent comes up; verification passes on the new connection.
    let mut vm = mock.vm(211).unwrap();
    vm.ipv4 = Some(vec!["10.1.0.22".into()]);
    mock.upsert_vm(vm);
    let (result, seen) = server.wait_result("verify-1").await;
    assert_eq!(result.outcome(), Outcome::Succeeded, "{}", result.error);
    let out: Value = serde_json::from_str(&result.outputs_json).unwrap();
    assert_eq!(out["ipv4Addresses"], json!(["10.1.0.22"]));
    let names: Vec<&str> = out["checks"]
        .as_array()
        .unwrap()
        .iter()
        .map(|c| c["name"].as_str().unwrap())
        .collect();
    assert_eq!(
        names,
        ["present", "placement", "name", "running", "address"]
    );
    // Retained lines were resent from sequence 0 on the new connection.
    assert_eq!(log_lines(&seen, "verify-1").first().map(|l| l.0), Some(0));

    // Cancel a read-only attempt that would otherwise wait for minutes.
    server
        .send(ServerKind::Assignment(proxmox_assignment(
            "absent-1",
            "verify-absent",
            json!({ "connection": connection(&mock), "vmid": 210, "waitSeconds": 600 }),
            &mock,
        )))
        .await;
    server
        .wait_for(|e| match e {
            Event::Message(m) => {
                matches!(&m.kind, Some(AgentKind::Started(s)) if s.attempt_id == "absent-1")
                    .then_some(())
            }
            _ => None,
        })
        .await;
    server
        .send(ServerKind::Cancel(CancelAttempt {
            attempt_id: "absent-1".into(),
        }))
        .await;
    let (result, _) = server.wait_result("absent-1").await;
    assert_eq!(result.outcome(), Outcome::Cancelled);

    // Unknown executors fail.
    server
        .send(ServerKind::Assignment(Assignment {
            attempt_id: "nix-1".into(),
            executor: "nix".into(),
            action: "build".into(),
            input_json: "{}".into(),
            ..Default::default()
        }))
        .await;
    let (result, _) = server.wait_result("nix-1").await;
    assert_eq!(result.outcome(), Outcome::Failed);

    shutdown.cancel();
    tokio::time::timeout(Duration::from_secs(15), task)
        .await
        .unwrap()
        .unwrap()
        .unwrap();
    wait_until("the outbox is empty", || {
        dir_is_empty(&state.path().join("outbox"))
    })
    .await;
}
