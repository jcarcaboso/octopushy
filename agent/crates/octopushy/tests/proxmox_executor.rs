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
use wiremock::matchers::{method, path};
use wiremock::{Mock, MockServer, ResponseTemplate};

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
        server: Default::default(),
        nix: Default::default(),
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

async fn visibility(fixture: Fixture) -> (Value, Vec<String>) {
    let m = mock(fixture).await;
    let (r, lines) = exec(
        "observe",
        json!({ "connection": connection(&m) }),
        &m.token(),
    )
    .await;
    (r.unwrap().outputs["vmVisibility"].clone(), lines)
}

#[tokio::test]
async fn observe_proves_the_token_sees_every_vm_or_says_why_not() {
    // A propagating VM.Audit on /vms, Sys.Audit on /access, and no ACL below /vms or on a pool.
    let (seen, _) = visibility(Fixture::example()).await;
    assert_eq!(seen, json!({ "complete": true }));

    let incomplete = |fixture: fn(&mut Fixture), reason: &'static str| async move {
        let mut f = Fixture::example();
        fixture(&mut f);
        let (seen, lines) = visibility(f).await;
        assert_eq!(seen["complete"], false, "{reason}");
        assert!(seen["reason"].as_str().unwrap().contains(reason), "{seen}");
        assert!(lines.iter().any(|l| l.contains(reason)));
    };
    // Proxmox leaves out the guests a token may not audit.
    incomplete(
        |f| f.privileges = Some(vec!["Sys.Audit".into()]),
        "lacks VM.Audit on /vms",
    )
    .await;
    // Held on /vms itself only: it reaches no VM.
    incomplete(|f| f.privileges_propagate = false, "does not propagate").await;
    // A more specific ACL can take it away from a VM.
    incomplete(
        |f| f.acl = Some(vec![json!({ "path": "/vms/150", "ugid": "octopushy@pve!agent", "type": "token", "roleid": "NoAccess" })]),
        "ACLs on /vms/150",
    )
    .await;
    incomplete(
        |f| f.acl = Some(vec![json!({ "path": "/pool/lab", "ugid": "ops", "type": "group", "roleid": "NoAccess" })]),
        "ACLs on /pool/lab",
    )
    .await;
    // Without Sys.Audit on /access, the ACLs it reads may leave one out.
    incomplete(
        |f| f.privileges = Some(vec!["VM.Audit".into()]),
        "Sys.Audit on /access",
    )
    .await;
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
    // What the controller tells a VM its guest's module made by.
    assert_eq!(out["tags"], json!(["nixos", "opentofu"]));
    assert_eq!(
        out["description"],
        "Managed by Octopushy as home/korriban/krb-crt-01."
    );
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

/// A Proxmox that reports `protection` once, then fails every later read of the VM's config.
async fn config_read_fails_after_the_first(protection: u8) -> MockServer {
    let server = MockServer::start().await;
    let data = |value: Value| ResponseTemplate::new(200).set_body_json(json!({ "data": value }));
    Mock::given(method("GET"))
        .and(path("/api2/json/access/permissions"))
        .respond_with(data(json!({ "/vms/210": { "VM.Audit": 1 } })))
        .mount(&server)
        .await;
    Mock::given(method("GET"))
        .and(path("/api2/json/cluster/resources"))
        .respond_with(data(
            json!([{ "vmid": 210, "name": "vm", "node": "n1", "type": "qemu", "status": "running" }]),
        ))
        .mount(&server)
        .await;
    Mock::given(method("GET"))
        .and(path("/api2/json/nodes/n1/qemu/210/config"))
        .respond_with(data(json!({ "protection": protection })))
        .up_to_n_times(1)
        .with_priority(1)
        .mount(&server)
        .await;
    Mock::given(method("GET"))
        .and(path("/api2/json/nodes/n1/qemu/210/config"))
        .respond_with(ResponseTemplate::new(500))
        .with_priority(2)
        .mount(&server)
        .await;
    server
}

#[tokio::test]
async fn a_change_proxmox_accepted_but_could_not_confirm_is_uncertain() {
    let server = config_read_fails_after_the_first(1).await;
    Mock::given(method("PUT"))
        .and(path("/api2/json/nodes/n1/qemu/210/config"))
        .respond_with(ResponseTemplate::new(200).set_body_json(json!({ "data": null })))
        .expect(1)
        .mount(&server)
        .await;
    let input = json!({
        "connection": { "endpoint": server.uri(), "verifyTls": false },
        "node": "n1", "vmid": 210, "protection": false,
    });

    let (r, _) = exec("set-protection", input, "u@pve!t=s").await;

    match r {
        Err(Failure::Uncertain(message)) => assert!(message.contains("could not be confirmed")),
        Err(Failure::Failed { message, .. }) => panic!("a plain failure: {message}"),
        _ => panic!("expected an uncertain outcome"),
    }
}

#[tokio::test]
async fn a_failed_read_after_no_change_is_a_plain_failure() {
    let server = config_read_fails_after_the_first(0).await;
    let input = json!({
        "connection": { "endpoint": server.uri(), "verifyTls": false },
        "node": "n1", "vmid": 210, "protection": false,
    });

    let (r, _) = exec("set-protection", input, "u@pve!t=s").await;

    assert!(matches!(r, Err(Failure::Failed { .. })));
}

const ED25519: &str = "SHA256:Zm9vYmFyYmF6cXV4cXV1eGNvcmdlZ3JhdWx0Z2FycA0";
const RSA: &str = "SHA256:cnNhcnNhcnNhcnNhcnNhcnNhcnNhcnNhcnNhcnNhcnM";

/// The node's clock in the fixtures below.
const NOW: u64 = 1_800_000_000;
/// The token that applies, as the controller names it.
const WRITER: &str = "octopushy@pve!writer";

/// A create task's log, as Proxmox writes it, with the keys it generated.
fn create_log(name: &str, keys: &[(&str, &str)]) -> Vec<String> {
    let mut log = vec![
        "extracting archive '/var/lib/vz/template/cache/debian-13-standard_13.6-1_amd64.tar.zst'"
            .to_owned(),
        "Detected container architecture: amd64".to_owned(),
    ];
    for (kind, fingerprint) in keys {
        log.push(format!(
            "Creating SSH host key 'ssh_host_{kind}_key' - this may take some time ..."
        ));
        log.push(format!("done: {fingerprint} root@{name}"));
    }
    log.push("TASK OK".to_owned());
    log
}

fn task(
    kind: &str,
    vmid: u32,
    user: &str,
    starttime: u64,
    log: Vec<String>,
) -> mock_proxmox::FixtureTask {
    mock_proxmox::FixtureTask {
        node: "korriban".into(),
        kind: kind.into(),
        id: vmid.to_string(),
        user: user.into(),
        starttime,
        endtime: Some(starttime + 12),
        status: Some("OK".into()),
        log,
        active: false,
        // Unique, as a real node's are.
        pid: NEXT_PID.fetch_add(1, std::sync::atomic::Ordering::Relaxed),
    }
}

static NEXT_PID: std::sync::atomic::AtomicU32 = std::sync::atomic::AtomicU32::new(0x1000);

/// The example installation with two containers on korriban, each made by the writer 200 seconds
/// ago: 320 from a template that generated host keys, 321 from one without `/etc/ssh`.
fn with_containers() -> Fixture {
    let mut fixture = Fixture::example();
    fixture.time = Some(NOW);
    let container = |vmid: u32, name: &str, ip: &str| {
        serde_json::from_value::<mock_proxmox::FixtureVm>(json!({
            "vmid": vmid, "name": name, "node": "korriban", "status": "running", "kind": "lxc",
            "tags": ["octopushy", "home"], "protection": true, "ipv4": [ip],
            "description": format!("Managed by Octopushy as home/korriban/{name}."),
        }))
        .unwrap()
    };
    fixture.vms.push(container(320, "krb-ct-01", "10.1.0.40"));
    fixture.vms.push(container(321, "krb-ct-02", "10.1.0.41"));
    fixture.tasks.push(task(
        "vzcreate",
        320,
        WRITER,
        NOW - 200,
        create_log("krb-ct-01", &[("rsa", RSA), ("ed25519", ED25519)]),
    ));
    fixture
        .tasks
        .push(task("vzstart", 320, WRITER, NOW - 180, vec![]));
    fixture.tasks.push(task(
        "vzcreate",
        321,
        WRITER,
        NOW - 200,
        create_log("krb-ct-02", &[]),
    ));
    fixture
}

/// What the controller sends for an apply that ran from 300 to 60 seconds ago.
fn host_keys_request() -> Value {
    json!({ "createdBy": WRITER, "applyStartedSecondsAgo": 300, "applyFinishedSecondsAgo": 60 })
}

#[tokio::test]
async fn verify_guest_checks_a_container_through_the_lxc_endpoints() {
    let m = mock(with_containers()).await;
    let t = m.token();
    let input = |vmid: u32, kind: &str, ip: Value, host_keys: bool| {
        json!({
            "connection": connection(&m), "node": "korriban", "vmid": vmid, "kind": kind,
            "expectRunning": true, "expectIpv4": ip, "waitSeconds": 0,
            "hostKeys": if host_keys { host_keys_request() } else { Value::Null }
        })
    };

    let (r, lines) = exec(
        "verify-guest",
        input(320, "lxc", json!("10.1.0.40"), true),
        &t,
    )
    .await;
    let out = r.unwrap().outputs;
    assert_eq!(out["status"], "running");
    assert_eq!(out["ipv4Addresses"], json!(["10.1.0.40"]));
    assert_eq!(
        out["description"],
        "Managed by Octopushy as home/korriban/krb-ct-01."
    );
    assert_eq!(out["checks"][0]["detail"], "container 320 exists");
    assert!(
        out["checks"]
            .as_array()
            .unwrap()
            .iter()
            .any(|c| c["detail"] == "Proxmox reports 10.1.0.40"),
        "{out}"
    );
    assert_eq!(
        out["hostKeys"],
        json!([format!("rsa {RSA}"), format!("ed25519 {ED25519}")])
    );
    assert!(
        out["hostKeysTask"]
            .as_str()
            .unwrap()
            .contains(":vzcreate:320:")
    );
    assert!(lines.iter().any(|l| l.contains("host key: ed25519")));

    // Without asking, no task is read.
    let (r, _) = exec("verify-guest", input(320, "lxc", Value::Null, false), &t).await;
    assert!(r.unwrap().outputs.get("hostKeys").is_none());

    // A create task that generated no key: the pin cannot be made, so verification fails.
    let (r, _) = exec("verify-guest", input(321, "lxc", Value::Null, true), &t).await;
    let (message, outputs) = failed(r);
    assert!(message.contains("logged no SSH host key"), "{message}");
    assert_eq!(outputs.unwrap()["present"], true);

    // A VMID of the other kind is not the guest's machine, either way round.
    let (r, _) = exec("verify-guest", input(210, "lxc", Value::Null, false), &t).await;
    let (message, outputs) = failed(r);
    assert!(
        message.contains("VMID 210 is a qemu guest, not lxc"),
        "{message}"
    );
    assert!(
        outputs.unwrap()["description"].is_null(),
        "nothing of the other machine is read"
    );
    let (r, _) = exec("verify-guest", input(320, "qemu", Value::Null, false), &t).await;
    assert!(failed(r).0.contains("VMID 320 is a lxc guest, not qemu"));

    // A stopped container has no interfaces to read.
    m.update(|f| f.vms.iter_mut().find(|v| v.vmid == 320).unwrap().status = "stopped".into());
    let (r, _) = exec("verify-guest", input(320, "lxc", Value::Null, false), &t).await;
    let (message, _) = failed(r);
    assert!(message.contains("container 320 is stopped"), "{message}");
}

/// Verifies container 320 asking for its host keys, after `change` to the fixture.
async fn host_keys_after(change: impl FnOnce(&mut Fixture)) -> Result<Success, Failure> {
    let mut fixture = with_containers();
    change(&mut fixture);
    let m = mock(fixture).await;
    let (r, _) = exec(
        "verify-guest",
        json!({
            "connection": connection(&m), "node": "korriban", "vmid": 320, "kind": "lxc",
            "waitSeconds": 0, "hostKeys": host_keys_request()
        }),
        &m.token(),
    )
    .await;
    r
}

#[tokio::test]
async fn host_keys_come_only_from_the_create_this_apply_made() {
    // The create of 320 inside the apply, by the writer, and only start and stop after it.
    let r = host_keys_after(|f| {
        f.tasks
            .push(task("vzshutdown", 320, WRITER, NOW - 30, vec![]))
    })
    .await;
    assert_eq!(
        r.unwrap().outputs["hostKeys"],
        json!([format!("rsa {RSA}"), format!("ed25519 {ED25519}")])
    );

    // Container A at 320 was made long ago, and its keys copied by someone with root in it. A is
    // destroyed, and B is restored at 320 during this apply: A's create must not vouch for B.
    let (message, outputs) = failed(
        host_keys_after(|f| {
            f.tasks.retain(|t| t.id != "320");
            f.tasks.push(task(
                "vzcreate",
                320,
                WRITER,
                NOW - 90 * 86_400,
                create_log("a", &[("ed25519", ED25519)]),
            ));
            f.tasks
                .push(task("vzdestroy", 320, WRITER, NOW - 86_400, vec![]));
            f.tasks
                .push(task("vzrestore", 320, WRITER, NOW - 200, vec![]));
        })
        .await,
    );
    assert!(
        message.contains("no successful create task (vzcreate)"),
        "{message}"
    );
    assert!(message.contains("restored or cloned"), "{message}");
    assert!(outputs.unwrap().get("hostKeys").is_none());

    // A create in the window by anyone but the token that applied is not this apply's.
    let (message, _) = failed(
        host_keys_after(|f| {
            f.tasks.retain(|t| t.id != "320");
            f.tasks.push(task(
                "vzcreate",
                320,
                "root@pam",
                NOW - 200,
                create_log("x", &[("ed25519", ED25519)]),
            ));
        })
        .await,
    );
    assert!(
        message.contains("by octopushy@pve!writer during this apply"),
        "{message}"
    );

    // The create came before the apply, even by the right token.
    let (message, _) = failed(
        host_keys_after(|f| {
            f.tasks.retain(|t| t.id != "320");
            f.tasks.push(task(
                "vzcreate",
                320,
                WRITER,
                NOW - 3_600,
                create_log("x", &[("ed25519", ED25519)]),
            ));
        })
        .await,
    );
    assert!(message.contains("during this apply"), "{message}");

    // The VMID changed hands after the create: a destroy and a restore, or a second create.
    for later in ["vzdestroy", "vzrestore", "vzmigrate", "vzcreate"] {
        let (message, _) = failed(
            host_keys_after(|f| {
                f.tasks
                    .push(task(later, 320, "root@pam", NOW - 100, vec![]))
            })
            .await,
        );
        assert!(
            message.contains(&format!("Proxmox lists {later}")),
            "{later}: {message}"
        );
    }
    // A destroy still running counts too.
    let (message, _) = failed(
        host_keys_after(|f| {
            let mut running = task("vzdestroy", 320, "root@pam", NOW - 10, vec![]);
            running.endtime = None;
            running.status = None;
            f.tasks.push(running);
        })
        .await,
    );
    assert!(message.contains("Proxmox lists vzdestroy"), "{message}");

    // Two creates by the writer in the window: which made it is not known.
    let (message, _) = failed(
        host_keys_after(|f| {
            f.tasks.push(task(
                "vzcreate",
                320,
                WRITER,
                NOW - 250,
                create_log("y", &[("rsa", RSA)]),
            ))
        })
        .await,
    );
    assert!(message.contains("2 create tasks"), "{message}");

    // A create that failed is no creation.
    let (message, _) = failed(
        host_keys_after(|f| {
            f.tasks
                .iter_mut()
                .find(|t| t.id == "320" && t.kind == "vzcreate")
                .unwrap()
                .status = Some("unable to create".into());
        })
        .await,
    );
    assert!(message.contains("no successful create task"), "{message}");

    // No history at all, as for a token that may not see another's tasks.
    let (message, _) = failed(host_keys_after(|f| f.tasks.clear()).await);
    assert!(
        message.contains("Sys.Audit on /nodes/korriban"),
        "{message}"
    );
}

#[tokio::test]
async fn host_keys_need_the_window_of_the_apply() {
    let m = mock(with_containers()).await;
    let (r, _) = exec(
        "verify-guest",
        json!({
            "connection": connection(&m), "node": "korriban", "vmid": 320, "kind": "lxc",
            "waitSeconds": 0, "hostKeys": true
        }),
        &m.token(),
    )
    .await;
    let (message, _) = failed(r);
    assert!(
        message.contains("expected struct HostKeysRequest"),
        "a bare flag names no creation: {message}"
    );
}

#[tokio::test]
async fn set_protection_of_a_container() {
    let m = mock(with_containers()).await;
    let t = m.token();
    let input = |vmid: u32, kind: Option<&str>, p: bool| {
        let mut v = json!({ "connection": connection(&m), "node": "korriban", "vmid": vmid, "protection": p });
        if let Some(kind) = kind {
            v["kind"] = json!(kind);
        }
        v
    };

    let (r, _) = exec("set-protection", input(320, Some("lxc"), false), &t).await;
    assert_eq!(
        r.unwrap().outputs,
        json!({ "absent": false, "previous": true, "protection": false })
    );
    assert!(!m.vm(320).unwrap().protection);
    let (r, lines) = exec("set-protection", input(320, Some("lxc"), true), &t).await;
    assert_eq!(r.unwrap().outputs["protection"], true);
    assert!(
        lines
            .iter()
            .any(|l| l.contains("protection of container 320 on"))
    );
    assert!(m.vm(320).unwrap().protection);

    // Without a kind it is a VM, as a controller before containers meant: a container is refused.
    let (r, _) = exec("set-protection", input(320, None, false), &t).await;
    assert!(failed(r).0.contains("VMID 320 is a lxc guest, not qemu"));
    let (r, _) = exec("set-protection", input(210, Some("lxc"), false), &t).await;
    assert!(failed(r).0.contains("VMID 210 is a qemu guest, not lxc"));
    assert!(m.vm(320).unwrap().protection && m.vm(210).unwrap().protection);
    let (r, _) = exec("set-protection", input(320, Some("vm"), false), &t).await;
    assert!(failed(r).0.contains("expected qemu or lxc"));

    // An absent container succeeds, as an absent VM does.
    let (r, _) = exec("set-protection", input(404, Some("lxc"), false), &t).await;
    assert_eq!(r.unwrap().outputs["absent"], true);
}

#[tokio::test]
async fn verify_absent_counts_containers_in_the_one_vmid_namespace() {
    let m = mock(with_containers()).await;
    let (r, _) = exec(
        "verify-absent",
        json!({ "connection": connection(&m), "vmid": 320, "waitSeconds": 0 }),
        &m.token(),
    )
    .await;
    assert!(failed(r).0.contains("320 still exists on korriban"));
    assert!(m.remove_vm(320));
    let (r, _) = exec(
        "verify-absent",
        json!({ "connection": connection(&m), "vmid": 320, "waitSeconds": 0 }),
        &m.token(),
    )
    .await;
    assert_eq!(r.unwrap().outputs, json!({ "present": false }));
}

#[tokio::test]
async fn a_tokens_task_matches_in_either_form_proxmox_lists_it_and_no_other_principal_does() {
    // Archived, as the fixture's tasks are listed: user and tokenid apart.
    assert!(host_keys_after(|_| {}).await.is_ok());
    // Still on the active list, finished: the whole principal in user.
    assert!(
        host_keys_after(|f| f.tasks.iter_mut().for_each(|t| t.active = true))
            .await
            .is_ok()
    );
    // Another token of the same user, and the user itself, are other principals.
    for other in ["octopushy@pve!other", "octopushy@pve"] {
        let (message, _) = failed(
            host_keys_after(|f| {
                f.tasks
                    .iter_mut()
                    .filter(|t| t.id == "320")
                    .for_each(|t| t.user = other.into());
            })
            .await,
        );
        assert!(
            message.contains("no successful create task"),
            "{other}: {message}"
        );
    }
}

#[tokio::test]
async fn host_keys_read_the_whole_history_past_its_first_page() {
    // Later tasks that change nothing push the create far down the newest-first history.
    let r = host_keys_after(|f| {
        for i in 0..1_200 {
            f.tasks
                .push(task("vzstart", 320, WRITER, NOW - 150 + i % 100, vec![]));
        }
    })
    .await;
    assert!(r.is_ok(), "{r:?}");
    // And a destroy among them is seen, wherever it falls.
    let (message, _) = failed(
        host_keys_after(|f| {
            for i in 0..1_200 {
                f.tasks
                    .push(task("vzstart", 320, WRITER, NOW - 150 + i % 100, vec![]));
            }
            f.tasks
                .push(task("vzdestroy", 320, "root@pam", NOW - 190, vec![]));
        })
        .await,
    );
    assert!(message.contains("Proxmox lists vzdestroy"), "{message}");
}

#[tokio::test]
async fn review_clock_skew_over_120_seconds_uses_node_relative_time() {
    let r = host_keys_after(|f| {
        f.time = Some(NOW + 86_400);
        for t in &mut f.tasks {
            t.starttime += 86_400;
            t.endtime = t.endtime.map(|s| s + 86_400);
        }
    })
    .await;
    assert!(
        r.is_ok(),
        "fixed one-day skew must not reject this creation: {r:?}"
    );
}

#[tokio::test]
async fn review_a_running_create_cannot_vouch_for_keys() {
    let r = host_keys_after(|f| {
        for t in f
            .tasks
            .iter_mut()
            .filter(|t| t.id == "320" && t.kind == "vzcreate")
        {
            t.status = None;
            t.endtime = None;
        }
    })
    .await;
    let (message, _) = failed(r);
    assert!(message.contains("no successful create task"), "{message}");
}

#[tokio::test]
async fn review_another_guest_at_the_vmid_fails_the_expected_name_check() {
    let mut f = with_containers();
    f.vms.iter_mut().find(|v| v.vmid == 320).unwrap().name = "other-guest".into();
    let m = mock(f).await;
    let (r, _) = exec(
        "verify-guest",
        json!({
            "connection": connection(&m), "node": "korriban", "vmid": 320, "kind": "lxc",
            "name": "krb-ct-01", "waitSeconds": 0, "hostKeys": host_keys_request()
        }),
        &m.token(),
    )
    .await;
    let (message, outputs) = failed(r);
    assert!(message.contains("other-guest"), "{message}");
    assert!(outputs.unwrap().get("hostKeys").is_none());
}

#[tokio::test]
async fn a_retried_read_that_cannot_vouch_reports_why_and_passes() {
    let mut fixture = with_containers();
    fixture
        .tasks
        .push(task("vzrestore", 320, "root@pam", NOW - 100, vec![]));
    let m = mock(fixture).await;
    let mut request = host_keys_request();
    request["optional"] = json!(true);
    let (r, _) = exec(
        "verify-guest",
        json!({
            "connection": connection(&m), "node": "korriban", "vmid": 320, "kind": "lxc",
            "waitSeconds": 0, "hostKeys": request
        }),
        &m.token(),
    )
    .await;
    let outputs = r.unwrap().outputs;
    assert!(outputs.get("hostKeys").is_none());
    let error = outputs["hostKeysError"].as_str().unwrap();
    assert!(error.contains("Proxmox lists vzrestore"), "{error}");
    assert!(
        error.contains("Planning again will not change that"),
        "{error}"
    );
}

#[tokio::test]
async fn probe_restore_between_pages_must_prevent_pinning() {
    use wiremock::matchers::query_param;
    let server = MockServer::start().await;
    let data = |v: Value| ResponseTemplate::new(200).set_body_json(json!({"data":v}));
    for (url, value) in [
        (
            "/api2/json/access/permissions",
            json!({"/vms/320":{"VM.Audit":1}}),
        ),
        (
            "/api2/json/cluster/resources",
            json!([{"vmid":320,"name":"krb-ct-01","node":"korriban","type":"lxc","status":"running"}]),
        ),
        (
            "/api2/json/nodes/korriban/lxc/320/status/current",
            json!({"status":"running"}),
        ),
        (
            "/api2/json/nodes/korriban/lxc/320/interfaces",
            json!([{"name":"eth0","inet":"10.1.0.40/24"}]),
        ),
        ("/api2/json/nodes/korriban/time", json!({"time":NOW})),
    ] {
        Mock::given(path(url))
            .respond_with(data(value))
            .mount(&server)
            .await;
    }
    let mut tasks:Vec<_>=(0..501).map(|i|json!({"upid":format!("start-{i}"),"type":"vzstart","id":"320","starttime":NOW-100,"status":"OK"})).collect();
    tasks[500] = json!({"upid":"owned-create","type":"vzcreate","id":"320","user":"octopushy@pve","tokenid":"writer","starttime":NOW-200,"status":"OK"});
    Mock::given(path("/api2/json/nodes/korriban/tasks"))
        .and(query_param("start", "0"))
        .respond_with(
            ResponseTemplate::new(200).set_body_json(json!({"data":tasks[..500],"total":501})),
        )
        .mount(&server)
        .await;
    let mut changed = vec![
        json!({"upid":"later-restore","type":"vzrestore","id":"320","starttime":NOW,"status":"RUNNING"}),
    ];
    changed.extend(tasks);
    Mock::given(path("/api2/json/nodes/korriban/tasks"))
        .and(query_param("start", "500"))
        .respond_with(
            ResponseTemplate::new(200).set_body_json(json!({"data":changed[500..],"total":502})),
        )
        .mount(&server)
        .await;
    let log: Vec<_> = create_log("krb-ct-01", &[("ed25519", ED25519)])
        .into_iter()
        .enumerate()
        .map(|(n, t)| json!({"n":n+1,"t":t}))
        .collect();
    Mock::given(path("/api2/json/nodes/korriban/tasks/owned-create/log"))
        .respond_with(data(json!(log)))
        .mount(&server)
        .await;
    let (result,_) = exec("verify-guest",json!({"connection":{"endpoint":server.uri(),"verifyTls":false},"kind":"lxc","node":"korriban","vmid":320,"name":"krb-ct-01","waitSeconds":0,"hostKeys":host_keys_request()}),"octopushy@pve!reader=test").await;
    eprintln!("verify result: {result:?}");
    assert!(
        result.is_err(),
        "a restore appeared during the scan, so the previous creation must not vouch for this VMID"
    );
}

#[tokio::test]
async fn probe_retry_never_switches_to_later_creation_by_same_token() {
    for later in [
        "vzcreate",
        "vzrestore",
        "vzdestroy",
        "vzmigrate",
        "qmcreate",
        "qmrestore",
        "qmdestroy",
        "qmigrate",
    ] {
        let mut f = with_containers();
        f.tasks.retain(|t| t.id != "320");
        f.tasks.push(task(
            "vzcreate",
            320,
            WRITER,
            NOW - 10000,
            create_log("krb-ct-01", &[("ed25519", ED25519)]),
        ));
        f.tasks.push(task(
            later,
            320,
            WRITER,
            NOW - 1000,
            create_log("krb-ct-01", &[("rsa", RSA)]),
        ));
        let m = mock(f).await;
        let (result,_)=exec("verify-guest",json!({"connection":connection(&m),"node":"korriban","vmid":320,"kind":"lxc","waitSeconds":0,"hostKeys":{"createdBy":WRITER,"applyStartedSecondsAgo":10100,"applyFinishedSecondsAgo":9900,"optional":true}}),&m.token()).await;
        let outputs = result.unwrap().outputs;
        assert!(outputs.get("hostKeys").is_none(), "{later}: {outputs}");
        assert!(
            outputs["hostKeysError"]
                .as_str()
                .unwrap()
                .contains(&format!("Proxmox lists {later}")),
            "{outputs}"
        );
    }
}

/// The fixture's history padded past one page with tasks that change nothing, and one more task that
/// starts once the history has been read once: in the middle of the first read.
fn moving_history(later: &str) -> Fixture {
    let mut f = with_containers();
    for i in 0..600 {
        f.tasks
            .push(task("vzstart", 320, WRITER, NOW - 150 + i % 100, vec![]));
    }
    f.tasks_later
        .push((1, task(later, 320, "root@pam", NOW - 5, vec![])));
    f
}

#[tokio::test]
async fn a_restore_that_starts_while_the_history_is_read_is_seen() {
    let m = mock(moving_history("vzrestore")).await;
    let (r, _) = exec(
        "verify-guest",
        json!({
            "connection": connection(&m), "node": "korriban", "vmid": 320, "kind": "lxc",
            "waitSeconds": 0, "hostKeys": host_keys_request()
        }),
        &m.token(),
    )
    .await;
    let (message, outputs) = failed(r);
    assert!(message.contains("Proxmox lists vzrestore"), "{message}");
    assert!(outputs.unwrap().get("hostKeys").is_none());
}

#[tokio::test]
async fn a_task_that_changes_nothing_starting_mid_read_only_means_reading_again() {
    let m = mock(moving_history("vzstart")).await;
    let (r, _) = exec(
        "verify-guest",
        json!({
            "connection": connection(&m), "node": "korriban", "vmid": 320, "kind": "lxc",
            "waitSeconds": 0, "hostKeys": host_keys_request()
        }),
        &m.token(),
    )
    .await;
    assert_eq!(
        r.unwrap().outputs["hostKeys"],
        json!([format!("rsa {RSA}"), format!("ed25519 {ED25519}")])
    );
}
