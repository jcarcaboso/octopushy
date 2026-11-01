//! The technitium executor against fake members: one HTTP API and one UDP DNS server per
//! member, on loopback addresses of their own. The real server is in tests/technitium-cluster.
use std::collections::{HashMap, HashSet};
use std::sync::{Arc, Mutex};
use std::time::Duration;

use axum::Router;
use axum::body::{Body, to_bytes};
use axum::http::{Request, Response};
use octopushy::attempts::logs::{AttemptLogger, LogBuffer};
use octopushy::executors::{self, ExecContext, ExecutorConfig, Failure, Secrets, Success};
use octopushy::masking::Masker;
use octopushy::tools::{RuntimeKind, ToolRuntime};
use serde_json::{Value, json};
use tokio::sync::Notify;
use tokio_util::sync::CancellationToken;

const PASSWORD: &str = "a shared & admin = password";

#[derive(Default)]
struct Node {
    name: String,
    version: String,
    initialized: bool,
    domain: Option<String>,
    peers: Vec<Value>,
    settings: Value,
    blocked: Vec<String>,
    allowed: Vec<String>,
    zones: Vec<String>,
    password: String,
    /// Names the DNS server answers as blocked, and names it resolves.
    dns_blocked: HashSet<String>,
    dns_resolves: HashSet<String>,
    /// `settings` becomes this after this many reads, like sync lag.
    lagging: Option<(u32, Value)>,
    calls: Vec<String>,
    forms: Vec<HashMap<String, String>>,
    refuse: HashMap<String, String>,
}

type Shared = Arc<Mutex<Node>>;

fn ok(response: Value) -> Response<Body> {
    json_body(json!({ "status": "ok", "response": response, "token": "session" }))
}

fn json_body(value: Value) -> Response<Body> {
    Response::builder()
        .header("content-type", "application/json")
        .body(Body::from(value.to_string()))
        .unwrap()
}

async fn api(node: Shared, request: Request<Body>) -> Response<Body> {
    let path = request.uri().path().to_owned();
    let body = to_bytes(request.into_body(), 1 << 20).await.unwrap();
    let form: HashMap<String, String> = url::form_urlencoded::parse(&body).into_owned().collect();
    let mut n = node.lock().unwrap();
    n.calls.push(path.clone());
    n.forms.push(form.clone());
    if let Some(error) = n.refuse.get(&path) {
        return json_body(json!({ "status": "error", "errorMessage": error }));
    }
    match path.as_str() {
        "/api/user/login" => {
            if form.get("pass") == Some(&n.password) {
                ok(json!({}))
            } else {
                json_body(
                    json!({ "status": "error", "errorMessage": "Invalid username or password" }),
                )
            }
        }
        "/api/admin/cluster/state" => ok(json!({
            "version": n.version, "dnsServerDomain": n.name, "clusterInitialized": n.initialized,
            "clusterDomain": n.domain, "clusterNodes": n.peers,
        })),
        "/api/settings/get" => {
            if let Some((reads, next)) = n.lagging.take() {
                if reads == 0 {
                    n.settings = next;
                } else {
                    n.lagging = Some((reads - 1, next));
                }
            }
            ok(n.settings.clone())
        }
        "/api/blocked/export" => Response::new(Body::from(n.blocked.join("\n"))),
        "/api/allowed/export" => Response::new(Body::from(n.allowed.join("\n"))),
        "/api/zones/list" => {
            ok(json!({ "zones": n.zones.iter().map(|z| json!({ "name": z })).collect::<Vec<_>>() }))
        }
        "/api/blocked/delete" => {
            let domain = form["domain"].clone();
            n.blocked.retain(|e| e != &domain);
            ok(json!({}))
        }
        "/api/admin/cluster/primary/removeSecondary"
        | "/api/admin/cluster/primary/deleteSecondary" => {
            let id: i64 = form["secondaryNodeId"].parse().unwrap();
            n.peers.retain(|p| p["id"] != id);
            ok(json!({}))
        }
        "/api/admin/cluster/initJoin" => {
            n.initialized = true;
            ok(json!({}))
        }
        _ => ok(json!({})),
    }
}

/// Answers A questions: NXDOMAIN with EDE 15 for a blocked name, an address for a known one.
fn dns_answer(node: &Shared, query: &[u8]) -> Vec<u8> {
    let mut labels = Vec::new();
    let mut at = 12;
    while query[at] != 0 {
        let length = query[at] as usize;
        labels.push(String::from_utf8_lossy(&query[at + 1..at + 1 + length]).into_owned());
        at += length + 1;
    }
    let name = labels.join(".");
    let question = &query[12..at + 5];
    let n = node.lock().unwrap();
    let mut answer = query[..2].to_vec();
    if n.dns_blocked.contains(&name) {
        answer.extend_from_slice(&[0x85, 0x83, 0, 1, 0, 0, 0, 0, 0, 1]);
        answer.extend_from_slice(question);
        answer.extend_from_slice(&[0, 0, 41, 4, 0xd0, 0, 0, 0, 0, 0, 6, 0, 15, 0, 2, 0, 15]);
    } else if n.dns_resolves.contains(&name) {
        answer.extend_from_slice(&[0x85, 0x80, 0, 1, 0, 1, 0, 0, 0, 0]);
        answer.extend_from_slice(question);
        answer.extend_from_slice(&[0xc0, 0x0c, 0, 1, 0, 1, 0, 0, 0, 30, 0, 4, 192, 0, 2, 10]);
    } else {
        answer.extend_from_slice(&[0x85, 0x83, 0, 1, 0, 0, 0, 0, 0, 0]);
        answer.extend_from_slice(question);
    }
    answer
}

struct Fake {
    node: Shared,
    endpoint: String,
    address: String,
    dns_port: u16,
}

async fn start(address: &str, node: Node) -> Fake {
    let node: Shared = Arc::new(Mutex::new(node));
    let listener = tokio::net::TcpListener::bind((address, 0)).await.unwrap();
    let port = listener.local_addr().unwrap().port();
    let shared = node.clone();
    let app = Router::new().fallback(move |request: Request<Body>| api(shared.clone(), request));
    tokio::spawn(async move { axum::serve(listener, app).await });
    let socket = tokio::net::UdpSocket::bind((address, 0)).await.unwrap();
    let dns_port = socket.local_addr().unwrap().port();
    let shared = node.clone();
    tokio::spawn(async move {
        let mut buffer = [0u8; 512];
        while let Ok((size, from)) = socket.recv_from(&mut buffer).await {
            let answer = dns_answer(&shared, &buffer[..size]);
            let _ = socket.send_to(&answer, from).await;
        }
    });
    Fake {
        node,
        endpoint: format!("http://{address}:{port}"),
        address: address.into(),
        dns_port,
    }
}

const PRIMARY_NAME: &str = "krb-dns-01.cluster.home.arpa";
const SECONDARY_NAME: &str = "mrb-dns-01.cluster.home.arpa";

fn declared_settings() -> Value {
    json!({
        "enableBlocking": true, "blockingType": "NxDomain", "blockListUrls": [],
        "blockListUpdateIntervalHours": 24, "recursion": "UseSpecifiedNetworkACL", "recursionNetworkACL": ["10.1.0.0/24"],
    })
}

fn peer_list(
    primary: &str,
    secondary: &str,
    secondary_state: &str,
    primary_state: &str,
) -> Vec<Value> {
    vec![
        json!({ "id": 1, "name": PRIMARY_NAME, "ipAddresses": [primary], "type": "Primary", "state": primary_state }),
        json!({ "id": 22, "name": SECONDARY_NAME, "ipAddresses": [secondary], "type": "Secondary", "state": secondary_state }),
    ]
}

fn healthy(name: &str) -> Node {
    Node {
        name: name.into(),
        version: "15.6.2".into(),
        initialized: true,
        domain: Some("cluster.home.arpa".into()),
        settings: declared_settings(),
        blocked: vec!["bad.example.com".into()],
        allowed: vec!["fine.example.com".into()],
        zones: vec!["home.arpa".into()],
        password: PASSWORD.into(),
        dns_blocked: HashSet::from(["bad.example.com".to_owned()]),
        dns_resolves: HashSet::from([PRIMARY_NAME.to_owned()]),
        ..Default::default()
    }
}

/// An empty server, as a replaced container leaves it.
fn rebuilt(name: &str) -> Node {
    Node {
        name: name.into(),
        version: "15.6.2".into(),
        password: PASSWORD.into(),
        settings: json!({}),
        ..Default::default()
    }
}

const PRIMARY_ADDRESS: &str = "127.0.70.1";
const SECONDARY_ADDRESS: &str = "127.0.70.2";

async fn pair(primary: Node, secondary: Node) -> (Fake, Fake) {
    // Each test owns its loopback pair, through ports; the addresses identify the members.
    (
        start(PRIMARY_ADDRESS, primary).await,
        start(SECONDARY_ADDRESS, secondary).await,
    )
}

async fn healthy_pair() -> (Fake, Fake) {
    let mut primary = healthy(PRIMARY_NAME);
    primary.peers = peer_list(PRIMARY_ADDRESS, SECONDARY_ADDRESS, "Connected", "Self");
    let mut secondary = healthy(SECONDARY_NAME);
    secondary.peers = peer_list(PRIMARY_ADDRESS, SECONDARY_ADDRESS, "Self", "Connected");
    pair(primary, secondary).await
}

fn input(primary: &Fake, secondary: &Fake) -> Value {
    json!({
        "cluster": "home/clusters/technitium", "verifyTls": false, "clusterDomain": "cluster.home.arpa",
        "minimumVersion": "15.6.2", "syncWaitSeconds": 1,
        "members": [
            { "guest": "home/korriban/krb-dns-01", "role": "primary", "address": primary.address,
              "endpoint": primary.endpoint, "dnsPort": primary.dns_port },
            { "guest": "home/moraband/mrb-dns-01", "role": "secondary", "address": secondary.address,
              "endpoint": secondary.endpoint, "dnsPort": secondary.dns_port },
        ],
        "settings": declared_settings(),
        "blocked": ["bad.example.com"], "allowed": ["fine.example.com"], "forwardZones": ["home.arpa"],
    })
}

async fn exec(action: &str, input: Value) -> (Result<Success, Failure>, Vec<String>) {
    let dir = tempfile::tempdir().unwrap();
    let cfg = ExecutorConfig {
        runtime: ToolRuntime::new(RuntimeKind::Local, None, "false".into(), None),
        provider_cache: dir.path().join("cache"),
        poll_interval: Duration::from_millis(20),
        proxmox_timeout: Duration::from_secs(3),
    };
    let secrets = HashMap::from([("technitium.password".to_owned(), PASSWORD.to_owned())]);
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
    let result = executors::run(&cfg, "technitium", &ctx).await;
    let lines = buffer
        .batch_from(0, usize::MAX)
        .map(|(_, l)| l.into_iter().map(|l| l.text).collect())
        .unwrap_or_default();
    (result, lines)
}

fn failure(result: Result<Success, Failure>) -> (String, Value) {
    match result {
        Err(Failure::Failed { message, outputs }) => (message, outputs.unwrap_or(Value::Null)),
        Err(other) => panic!("expected a failure, got {other:?}"),
        Ok(success) => panic!("expected a failure, got {:?}", success.outputs),
    }
}

fn member<'a>(outputs: &'a Value, guest: &str) -> &'a Value {
    outputs["members"]
        .as_array()
        .unwrap()
        .iter()
        .find(|m| m["guest"] == guest)
        .unwrap()
}

#[tokio::test(flavor = "multi_thread")]
async fn verify_passes_on_a_healthy_cluster_and_connects_to_every_member() {
    let (primary, secondary) = healthy_pair().await;
    let (result, _) = exec("verify", input(&primary, &secondary)).await;
    let outputs = result.unwrap().outputs;
    assert_eq!(outputs["ok"], true);
    assert_eq!(outputs["members"].as_array().unwrap().len(), 2);
    for fake in [&primary, &secondary] {
        let calls = fake.node.lock().unwrap().calls.clone();
        assert!(calls.contains(&"/api/admin/cluster/state".to_owned()));
        assert!(
            calls.contains(&"/api/user/logout".to_owned()),
            "a session stays behind"
        );
    }
    let checks: Vec<&str> = member(&outputs, "home/moraband/mrb-dns-01")["checks"]
        .as_array()
        .unwrap()
        .iter()
        .map(|c| c["name"].as_str().unwrap())
        .collect();
    for expected in [
        "cluster",
        "version",
        "membership",
        "peers",
        "settings",
        "blocked",
        "allowed",
        "zones",
        "blocking",
        "control",
    ] {
        assert!(
            checks.contains(&expected),
            "{expected} was not checked: {checks:?}"
        );
    }
}

#[tokio::test(flavor = "multi_thread")]
async fn verify_fails_on_a_rebuilt_secondary_with_the_exact_recovery_and_removes_nothing() {
    let mut primary = healthy(PRIMARY_NAME);
    primary.peers = peer_list(PRIMARY_ADDRESS, SECONDARY_ADDRESS, "Unreachable", "Self");
    let (primary, secondary) = pair(primary, rebuilt("mrb-dns-01")).await;
    let (result, _) = exec("verify", input(&primary, &secondary)).await;
    let (message, outputs) = failure(result);
    assert!(message.contains("home/moraband/mrb-dns-01"), "{message}");
    assert!(message.contains("clusterInitialized=false"), "{message}");
    assert!(
        message.contains("deleteSecondary?secondaryNodeId=22"),
        "{message}"
    );
    assert!(
        !message.contains("home/korriban/krb-dns-01 ("),
        "the primary is not to blame: {message}"
    );
    assert_eq!(outputs["ok"], false);
    // The primary sees its peer Unreachable, which is the same fact, not a second failure.
    assert_eq!(member(&outputs, "home/moraband/mrb-dns-01")["ok"], false);
    assert_eq!(member(&outputs, "home/korriban/krb-dns-01")["ok"], true);
    for fake in [&primary, &secondary] {
        let calls = fake.node.lock().unwrap().calls.clone();
        assert!(
            !calls
                .iter()
                .any(|c| c.contains("deleteSecondary") || c.contains("removeSecondary")),
            "{calls:?}"
        );
    }
}

#[tokio::test(flavor = "multi_thread")]
async fn verify_waits_for_sync_lag_within_its_limit() {
    let mut secondary = healthy(SECONDARY_NAME);
    secondary.peers = peer_list(PRIMARY_ADDRESS, SECONDARY_ADDRESS, "Self", "Connected");
    secondary.settings = json!({ "enableBlocking": true, "blockingType": "AnyAddress" });
    secondary.lagging = Some((2, declared_settings()));
    let mut primary = healthy(PRIMARY_NAME);
    primary.peers = peer_list(PRIMARY_ADDRESS, SECONDARY_ADDRESS, "Connected", "Self");
    let (primary, secondary) = pair(primary, secondary).await;
    let (result, logs) = exec("verify", input(&primary, &secondary)).await;
    assert_eq!(result.unwrap().outputs["ok"], true);
    assert!(
        logs.iter()
            .any(|l| l.contains("waiting for home/moraband/mrb-dns-01")),
        "{logs:?}"
    );
}

#[tokio::test(flavor = "multi_thread")]
async fn verify_gives_up_on_drift_and_names_the_member_and_the_setting() {
    let mut secondary = healthy(SECONDARY_NAME);
    secondary.peers = peer_list(PRIMARY_ADDRESS, SECONDARY_ADDRESS, "Self", "Connected");
    secondary.settings["blockingType"] = json!("AnyAddress");
    let mut primary = healthy(PRIMARY_NAME);
    primary.peers = peer_list(PRIMARY_ADDRESS, SECONDARY_ADDRESS, "Connected", "Self");
    let (primary, secondary) = pair(primary, secondary).await;
    let mut request = input(&primary, &secondary);
    request["syncWaitSeconds"] = json!(0);
    let (result, _) = exec("verify", request).await;
    let (message, _) = failure(result);
    assert!(message.contains("home/moraband/mrb-dns-01"), "{message}");
    assert!(message.contains("blockingType"), "{message}");
    assert!(message.contains("hand edits are reverted"), "{message}");
}

#[tokio::test(flavor = "multi_thread")]
async fn verify_needs_each_member_to_block_and_resolve_on_its_own() {
    let (primary, secondary) = healthy_pair().await;
    secondary.node.lock().unwrap().dns_blocked.clear();
    let mut request = input(&primary, &secondary);
    request["syncWaitSeconds"] = json!(0);
    let (result, _) = exec("verify", request.clone()).await;
    let (message, outputs) = failure(result);
    assert!(
        message.contains("bad.example.com is not blocked here"),
        "{message}"
    );
    assert_eq!(member(&outputs, "home/korriban/krb-dns-01")["ok"], true);

    secondary
        .node
        .lock()
        .unwrap()
        .dns_blocked
        .insert("bad.example.com".into());
    secondary.node.lock().unwrap().dns_resolves.clear();
    let (result, _) = exec("verify", request).await;
    let (message, _) = failure(result);
    assert!(message.contains("control"), "{message}");
}

#[tokio::test(flavor = "multi_thread")]
async fn verify_finds_undeclared_entries_and_old_versions() {
    let (primary, secondary) = healthy_pair().await;
    secondary
        .node
        .lock()
        .unwrap()
        .blocked
        .push("hand.example.com".into());
    secondary.node.lock().unwrap().version = "15.6.0".into();
    let mut request = input(&primary, &secondary);
    request["syncWaitSeconds"] = json!(0);
    let (message, _) = failure(exec("verify", request).await.0);
    assert!(
        message.contains("not declared: hand.example.com"),
        "{message}"
    );
    assert!(message.contains("below the minimum 15.6.2"), "{message}");
}

#[tokio::test(flavor = "multi_thread")]
async fn verify_blames_only_the_member_that_is_down() {
    let (primary, secondary) = healthy_pair().await;
    let mut request = input(&primary, &secondary);
    request["members"][1]["endpoint"] = json!("http://127.0.70.2:9");
    request["syncWaitSeconds"] = json!(0);
    let (message, outputs) = failure(exec("verify", request).await.0);
    assert!(message.starts_with("home/moraband/mrb-dns-01"), "{message}");
    assert!(message.contains("port 53443"), "{message}");
    assert_eq!(
        member(&outputs, "home/korriban/krb-dns-01")["checks"]
            .as_array()
            .unwrap()
            .iter()
            .filter(|c| c["name"] == "cluster")
            .count(),
        1
    );
}

#[tokio::test(flavor = "multi_thread")]
async fn a_wrong_password_says_so() {
    let (primary, secondary) = healthy_pair().await;
    secondary.node.lock().unwrap().password = "another".into();
    let (message, _) = failure(exec("verify", input(&primary, &secondary)).await.0);
    assert!(message.contains("refused the admin password"), "{message}");
}

#[tokio::test(flavor = "multi_thread")]
async fn inspect_reports_each_secondary_and_undeclared_entries() {
    let (primary, secondary) = healthy_pair().await;
    primary
        .node
        .lock()
        .unwrap()
        .blocked
        .push("hand.example.com".into());
    let outputs = exec("inspect", input(&primary, &secondary))
        .await
        .0
        .unwrap()
        .outputs;
    assert_eq!(outputs["members"][0]["state"], "joined");
    assert_eq!(
        outputs["undeclared"]["blocked"],
        json!(["hand.example.com"])
    );
    assert_eq!(outputs["undeclared"]["allowed"], json!([]));

    // Rebuilt, and still listed.
    let mut listing = healthy(PRIMARY_NAME);
    listing.peers = peer_list(PRIMARY_ADDRESS, SECONDARY_ADDRESS, "Unreachable", "Self");
    let (primary, secondary) = pair(listing, rebuilt("mrb-dns-01")).await;
    let outputs = exec("inspect", input(&primary, &secondary))
        .await
        .0
        .unwrap()
        .outputs;
    assert_eq!(outputs["members"][0]["state"], "stale");

    // Empty, and not listed: it can be joined.
    let mut alone = healthy(PRIMARY_NAME);
    alone.peers = vec![
        json!({ "id": 1, "name": PRIMARY_NAME, "ipAddresses": [PRIMARY_ADDRESS], "type": "Primary", "state": "Self" }),
    ];
    let (primary, secondary) = pair(alone, rebuilt("mrb-dns-01")).await;
    let outputs = exec("inspect", input(&primary, &secondary))
        .await
        .0
        .unwrap()
        .outputs;
    assert_eq!(outputs["members"][0]["state"], "absent");

    // Nothing formed yet is ordinary, until the controller knows it was.
    let (primary, secondary) = pair(rebuilt(PRIMARY_NAME), rebuilt("mrb-dns-01")).await;
    let mut request = input(&primary, &secondary);
    let outputs = exec("inspect", request.clone()).await.0.unwrap().outputs;
    assert_eq!(outputs["formed"], false);
    assert_eq!(outputs["members"][0]["state"], "absent");
    request["formed"] = json!(true);
    let (message, _) = failure(exec("inspect", request).await.0);
    assert!(message.contains("rebuilt from empty"), "{message}");
}

#[tokio::test(flavor = "multi_thread")]
async fn a_primary_that_is_down_fails_inspection_and_says_what_still_works() {
    let (primary, secondary) = healthy_pair().await;
    let mut request = input(&primary, &secondary);
    request["members"][0]["endpoint"] = json!("http://127.0.70.1:9");
    let (message, _) = failure(exec("inspect", request).await.0);
    assert!(message.contains("home/korriban/krb-dns-01"), "{message}");
    assert!(message.contains("secondaries keep answering"), "{message}");
}

#[tokio::test(flavor = "multi_thread")]
async fn reconcile_joins_an_absent_member_with_the_password_in_the_form_only() {
    let mut alone = healthy(PRIMARY_NAME);
    alone.peers = vec![
        json!({ "id": 1, "name": PRIMARY_NAME, "ipAddresses": [PRIMARY_ADDRESS], "type": "Primary", "state": "Self" }),
    ];
    let (primary, secondary) = pair(alone, rebuilt("mrb-dns-01")).await;
    let mut request = input(&primary, &secondary);
    request["join"] = json!(["home/moraband/mrb-dns-01"]);
    let (result, logs) = exec("reconcile", request).await;
    assert_eq!(
        result.unwrap().outputs["joined"],
        json!(["home/moraband/mrb-dns-01"])
    );
    let n = secondary.node.lock().unwrap();
    let at = n
        .calls
        .iter()
        .position(|c| c == "/api/admin/cluster/initJoin")
        .unwrap();
    let form = &n.forms[at];
    assert_eq!(form["primaryNodePassword"], PASSWORD);
    assert_eq!(form["primaryNodeIpAddress"], PRIMARY_ADDRESS);
    assert_eq!(form["ignoreCertificateErrors"], "true");
    assert!(
        form["primaryNodeUrl"].contains(PRIMARY_NAME),
        "the primary's URL must carry its name"
    );
    assert!(!logs.join("\n").contains(PASSWORD));
}

#[tokio::test(flavor = "multi_thread")]
async fn reconcile_never_clears_a_stale_record_by_itself() {
    let mut primary = healthy(PRIMARY_NAME);
    primary.peers = peer_list(PRIMARY_ADDRESS, SECONDARY_ADDRESS, "Unreachable", "Self");
    let (primary, secondary) = pair(primary, rebuilt("mrb-dns-01")).await;
    let mut request = input(&primary, &secondary);
    request["join"] = json!(["home/moraband/mrb-dns-01"]);
    let (message, _) = failure(exec("reconcile", request).await.0);
    assert!(
        message.contains("deleteSecondary?secondaryNodeId=22"),
        "{message}"
    );
    assert!(
        !primary
            .node
            .lock()
            .unwrap()
            .calls
            .iter()
            .any(|c| c.contains("Secondary"))
    );
    assert!(
        !secondary
            .node
            .lock()
            .unwrap()
            .calls
            .iter()
            .any(|c| c.contains("initJoin"))
    );
}

#[tokio::test(flavor = "multi_thread")]
async fn reconcile_removes_what_the_plan_removed_and_no_more() {
    let (primary, secondary) = healthy_pair().await;
    let extra = json!({ "id": 33, "name": "other.cluster.home.arpa", "ipAddresses": ["127.0.70.9"], "type": "Secondary", "state": "Connected" });
    primary.node.lock().unwrap().peers.push(extra.clone());

    // Nothing approved: the undeclared peer stays. Verification is what reports it.
    exec("reconcile", input(&primary, &secondary))
        .await
        .0
        .unwrap();
    assert!(
        !primary
            .node
            .lock()
            .unwrap()
            .calls
            .iter()
            .any(|c| c.contains("removeSecondary"))
    );

    // One removal approved and two peers to remove: none goes.
    let other = json!({ "id": 44, "name": "another.cluster.home.arpa", "ipAddresses": ["127.0.70.10"], "type": "Secondary", "state": "Connected" });
    primary.node.lock().unwrap().peers.push(other);
    let mut request = input(&primary, &secondary);
    request["remove"] = json!(["home/umbrel/umb-dns-01"]);
    let (message, _) = failure(exec("reconcile", request).await.0);
    assert!(message.contains("undeclared peers"), "{message}");
    assert!(
        !primary
            .node
            .lock()
            .unwrap()
            .calls
            .iter()
            .any(|c| c.contains("removeSecondary"))
    );
    primary.node.lock().unwrap().peers.retain(|p| p["id"] != 44);

    // Approved, and the peer cannot answer: the primary's record goes instead.
    primary.node.lock().unwrap().refuse.insert(
        "/api/admin/cluster/primary/removeSecondary".into(),
        "connection refused".into(),
    );
    let mut request = input(&primary, &secondary);
    request["remove"] = json!(["home/umbrel/umb-dns-01"]);
    exec("reconcile", request).await.0.unwrap();
    let n = primary.node.lock().unwrap();
    assert!(n.calls.iter().any(|c| c.ends_with("/deleteSecondary")));
    assert_eq!(n.peers.len(), 2);
}

#[tokio::test(flavor = "multi_thread")]
async fn reconcile_prunes_undeclared_entries_on_the_primary_only_when_approved() {
    let (primary, secondary) = healthy_pair().await;
    primary
        .node
        .lock()
        .unwrap()
        .blocked
        .push("hand.example.com".into());
    let mut request = input(&primary, &secondary);
    exec("reconcile", request.clone()).await.0.unwrap();
    assert_eq!(primary.node.lock().unwrap().blocked.len(), 2);
    request["pruneEntries"] = json!(true);
    exec("reconcile", request).await.0.unwrap();
    assert_eq!(primary.node.lock().unwrap().blocked, ["bad.example.com"]);
}

#[tokio::test(flavor = "multi_thread")]
async fn one_member_that_cannot_join_does_not_stop_the_rest() {
    let mut alone = healthy(PRIMARY_NAME);
    alone.peers = vec![
        json!({ "id": 1, "name": PRIMARY_NAME, "ipAddresses": [PRIMARY_ADDRESS], "type": "Primary", "state": "Self" }),
    ];
    let third = start("127.0.70.3", rebuilt("umb-dns-01")).await;
    let (primary, secondary) = pair(alone, rebuilt("mrb-dns-01")).await;
    secondary.node.lock().unwrap().refuse.insert(
        "/api/admin/cluster/initJoin".into(),
        "the primary cannot be reached".into(),
    );
    let mut request = input(&primary, &secondary);
    request["members"].as_array_mut().unwrap().push(json!({
        "guest": "home/umbrel/umb-dns-01", "role": "secondary", "address": third.address, "endpoint": third.endpoint, "dnsPort": third.dns_port }));
    request["join"] = json!(["home/moraband/mrb-dns-01", "home/umbrel/umb-dns-01"]);
    let (message, _) = failure(exec("reconcile", request).await.0);
    assert!(message.contains("home/moraband/mrb-dns-01"), "{message}");
    assert!(!message.contains("umb-dns-01"), "{message}");
    assert!(third.node.lock().unwrap().initialized);
}

#[tokio::test(flavor = "multi_thread")]
async fn a_moved_member_tells_the_cluster_its_new_address() {
    let mut primary = healthy(PRIMARY_NAME);
    primary.peers = peer_list(PRIMARY_ADDRESS, "127.0.70.8", "Connected", "Self");
    let mut secondary = healthy(SECONDARY_NAME);
    secondary.peers = primary.peers.clone();
    let (primary, secondary) = pair(primary, secondary).await;
    let mut request = input(&primary, &secondary);
    request["join"] = json!(["home/moraband/mrb-dns-01"]);
    exec("reconcile", request).await.0.unwrap();
    let n = secondary.node.lock().unwrap();
    let at = n
        .calls
        .iter()
        .position(|c| c.ends_with("/updateIpAddress"))
        .unwrap();
    assert_eq!(n.forms[at]["ipAddresses"], SECONDARY_ADDRESS);
}

#[tokio::test(flavor = "multi_thread")]
async fn refreshing_lists_goes_to_the_primary_alone() {
    let (primary, secondary) = healthy_pair().await;
    exec("refresh-block-lists", input(&primary, &secondary))
        .await
        .0
        .unwrap();
    assert!(
        primary
            .node
            .lock()
            .unwrap()
            .calls
            .iter()
            .any(|c| c.ends_with("/forceUpdateBlockLists"))
    );
    assert!(secondary.node.lock().unwrap().calls.is_empty());
    assert!(executors::is_mutating("technitium", "refresh-block-lists"));
    assert!(executors::is_mutating("technitium", "reconcile"));
    assert!(!executors::is_mutating("technitium", "verify"));
    assert!(!executors::is_mutating("technitium", "inspect"));
}

#[tokio::test(flavor = "multi_thread")]
async fn an_unknown_action_is_refused() {
    let (primary, secondary) = healthy_pair().await;
    let (message, _) = failure(exec("promote", input(&primary, &secondary)).await.0);
    assert!(message.contains("unknown technitium action"), "{message}");
}
