//! The fake API: the Proxmox endpoints Octopushy uses, plus `/mock` admin routes.
//!
//! Responses imitate Proxmox closely where clients depend on it: the `{"data":
//! ...}` envelope, booleans as `0`/`1`, `;`-separated tags, and errors with the
//! message in the HTTP status line (for example `500 QEMU guest agent is not
//! running`). Containers (`kind: lxc`) answer on the `lxc` endpoints, VMs on `qemu`.

use std::collections::HashMap;
use std::sync::{Arc, Mutex};

use axum::extract::{Path, Query, Request, State};
use axum::http::{StatusCode, header};
use axum::middleware::{self, Next};
use axum::response::{IntoResponse, Response};
use axum::routing::{delete, get};
use axum::{Form, Json, Router};
use serde_json::{Value, json};

use crate::fixture::{Fixture, FixtureVm};

/// The mutable state of a running mock.
pub type SharedFixture = Arc<Mutex<Fixture>>;

/// Builds the router for `state`.
pub fn router(state: SharedFixture) -> Router {
    let api = Router::new()
        .route("/version", get(version))
        .route("/cluster/status", get(cluster_status))
        .route("/cluster/resources", get(cluster_resources))
        .route("/nodes", get(nodes))
        .route("/nodes/{node}/qemu/{vmid}/status/current", get(vm_status))
        .route(
            "/nodes/{node}/qemu/{vmid}/config",
            get(vm_config).put(put_vm_config),
        )
        .route(
            "/nodes/{node}/qemu/{vmid}/agent/network-get-interfaces",
            get(network_interfaces),
        )
        .route("/nodes/{node}/lxc/{vmid}/status/current", get(ct_status))
        .route(
            "/nodes/{node}/lxc/{vmid}/config",
            get(ct_config).put(put_ct_config),
        )
        .route("/nodes/{node}/lxc/{vmid}/interfaces", get(ct_interfaces))
        .route("/nodes/{node}/tasks", get(tasks))
        .route("/nodes/{node}/tasks/{upid}/log", get(task_log))
        .route("/access/permissions", get(permissions))
        .route("/access/acl", get(acl))
        .fallback(|| async { pve_error(StatusCode::NOT_IMPLEMENTED, "Method not implemented") })
        .route_layer(middleware::from_fn_with_state(state.clone(), require_token));

    let admin = Router::new()
        .route("/vms", get(list_vms).post(upsert_vm))
        .route("/vms/{vmid}", delete(remove_vm));

    Router::new()
        .nest("/api2/json", api)
        .nest("/mock", admin)
        .with_state(state)
}

/// A Proxmox-style error: the message goes in the status line and the body.
fn pve_error(status: StatusCode, message: &str) -> Response {
    let mut response = (
        status,
        Json(json!({ "data": null, "message": format!("{message}\n") })),
    )
        .into_response();
    let phrase: String = message
        .chars()
        .filter(|c| c.is_ascii_graphic() || *c == ' ')
        .collect();
    if let Ok(reason) = hyper::ext::ReasonPhrase::try_from(phrase.into_bytes()) {
        response.extensions_mut().insert(reason);
    }
    response
}

fn data(value: Value) -> Response {
    Json(json!({ "data": value })).into_response()
}

fn flag(b: bool) -> u8 {
    u8::from(b)
}

async fn require_token(State(state): State<SharedFixture>, req: Request, next: Next) -> Response {
    let given = req
        .headers()
        .get(header::AUTHORIZATION)
        .and_then(|v| v.to_str().ok())
        .and_then(|v| v.strip_prefix("PVEAPIToken="));
    let authorized = {
        let fixture = state.lock().unwrap();
        given.is_some_and(|token| {
            token == fixture.token || fixture.additional_tokens.iter().any(|t| t == token)
        })
    };
    if !authorized {
        return pve_error(StatusCode::UNAUTHORIZED, "authentication failure");
    }
    next.run(req).await
}

async fn version(State(state): State<SharedFixture>) -> Response {
    let f = state.lock().unwrap();
    let release = f.version.split('.').take(2).collect::<Vec<_>>().join(".");
    data(json!({ "version": f.version, "release": release, "repoid": "0123abcd" }))
}

async fn cluster_status(State(state): State<SharedFixture>) -> Response {
    let f = state.lock().unwrap();
    let mut entries = Vec::new();
    if let Some(cluster) = &f.cluster {
        entries.push(json!({
            "type": "cluster", "id": "cluster", "name": cluster.name,
            "quorate": flag(cluster.quorate), "nodes": f.nodes.len(), "version": 3,
        }));
    }
    for (i, node) in f.nodes.iter().enumerate() {
        entries.push(json!({
            "type": "node", "id": format!("node/{}", node.name), "name": node.name,
            "online": flag(node.online), "local": flag(i == 0), "nodeid": i + 1,
            "ip": format!("10.1.0.{}", 10 + i),
        }));
        if f.cluster.is_none() {
            break; // a standalone host only knows itself
        }
    }
    data(Value::Array(entries))
}

async fn nodes(State(state): State<SharedFixture>) -> Response {
    let f = state.lock().unwrap();
    let nodes: Vec<Value> = f
        .nodes
        .iter()
        .map(|n| {
            if n.online {
                json!({
                    "node": n.name, "id": format!("node/{}", n.name), "type": "node",
                    "status": "online", "cpu": n.cpu, "maxcpu": n.max_cpu,
                    "mem": n.memory_bytes, "maxmem": n.max_memory_bytes,
                    "uptime": n.uptime_seconds, "level": "",
                })
            } else {
                json!({ "node": n.name, "id": format!("node/{}", n.name), "type": "node", "status": "offline" })
            }
        })
        .collect();
    data(Value::Array(nodes))
}

fn node_online(f: &Fixture, node: &str) -> bool {
    f.nodes.iter().any(|n| n.name == node && n.online)
}

fn resource(f: &Fixture, vm: &FixtureVm) -> Value {
    let online = node_online(f, &vm.node);
    let running = online && vm.status == "running";
    let mut v = json!({
        "id": format!("{}/{}", vm.kind, vm.vmid), "type": vm.kind, "vmid": vm.vmid,
        "name": vm.name, "node": vm.node,
        "status": if online { vm.status.as_str() } else { "unknown" },
        "template": flag(vm.template), "maxcpu": vm.max_cpu, "maxmem": vm.max_memory_bytes,
        "uptime": if running { vm.uptime_seconds } else { 0 },
        "cpu": if running { 0.02 } else { 0.0 }, "mem": if running { vm.max_memory_bytes / 2 } else { 0 },
    });
    if !vm.tags.is_empty() {
        v["tags"] = Value::String(vm.tags.join(";"));
    }
    v
}

async fn cluster_resources(
    State(state): State<SharedFixture>,
    Query(query): Query<HashMap<String, String>>,
) -> Response {
    let f = state.lock().unwrap();
    match query.get("type").map(String::as_str) {
        None | Some("vm") => {}
        Some(_) => return data(json!([])),
    }
    data(Value::Array(
        f.vms.iter().map(|vm| resource(&f, vm)).collect(),
    ))
}

/// A Proxmox-style error as a value; it becomes a response via [`pve_error`].
struct PveError(StatusCode, String);

impl IntoResponse for PveError {
    fn into_response(self) -> Response {
        pve_error(self.0, &self.1)
    }
}

/// Finds a QEMU VM on `node`, answering like Proxmox when it is not there.
fn find_vm<'a>(f: &'a Fixture, node: &str, vmid: &str) -> Result<&'a FixtureVm, PveError> {
    find_guest(f, node, vmid, "qemu")
}

/// Finds a guest of `kind` (`qemu` or `lxc`) on `node`. Proxmox keeps each kind's configuration
/// in its own directory, so a VMID of the other kind is "not there" for these endpoints.
fn find_guest<'a>(
    f: &'a Fixture,
    node: &str,
    vmid: &str,
    kind: &str,
) -> Result<&'a FixtureVm, PveError> {
    if !f.nodes.iter().any(|n| n.name == node) {
        return Err(PveError(
            StatusCode::INTERNAL_SERVER_ERROR,
            format!(
                "hostname lookup '{node}' failed - failed to get address info for: {node}: Name or service not known"
            ),
        ));
    }
    if !node_online(f, node) {
        return Err(PveError(
            StatusCode::from_u16(595).unwrap(),
            format!("Connection refused (node {node} is offline)"),
        ));
    }
    let id: u32 = vmid.parse().map_err(|_| {
        PveError(
            StatusCode::BAD_REQUEST,
            "Parameter verification failed.".to_owned(),
        )
    })?;
    f.vms
        .iter()
        .find(|vm| vm.vmid == id && vm.node == node && vm.kind == kind)
        .ok_or_else(|| {
            let directory = if kind == "lxc" { "lxc" } else { "qemu-server" };
            PveError(
                StatusCode::INTERNAL_SERVER_ERROR,
                format!("Configuration file 'nodes/{node}/{directory}/{vmid}.conf' does not exist"),
            )
        })
}

async fn vm_status(
    State(state): State<SharedFixture>,
    Path((node, vmid)): Path<(String, String)>,
) -> Response {
    let f = state.lock().unwrap();
    match find_vm(&f, &node, &vmid) {
        Ok(vm) => {
            let running = vm.status == "running";
            let mut v = json!({
                "vmid": vm.vmid, "name": vm.name, "status": vm.status,
                "qmpstatus": vm.status, "uptime": if running { vm.uptime_seconds } else { 0 },
                "cpus": vm.max_cpu, "maxmem": vm.max_memory_bytes, "agent": 1,
            });
            if vm.template {
                v["template"] = json!(1);
            }
            data(v)
        }
        Err(e) => e.into_response(),
    }
}

async fn vm_config(
    State(state): State<SharedFixture>,
    Path((node, vmid)): Path<(String, String)>,
) -> Response {
    let f = state.lock().unwrap();
    match find_vm(&f, &node, &vmid) {
        Ok(vm) => {
            let mut v = json!({
                "name": vm.name, "cores": vm.max_cpu, "memory": (vm.max_memory_bytes / 1024 / 1024).to_string(),
                "agent": "1", "ostype": "l26", "digest": format!("{:040x}", vm.vmid),
            });
            if vm.protection {
                v["protection"] = json!(1);
            }
            if vm.template {
                v["template"] = json!(1);
            }
            if !vm.tags.is_empty() {
                v["tags"] = Value::String(vm.tags.join(";"));
            }
            if let Some(description) = &vm.description {
                v["description"] = Value::String(description.clone());
            }
            data(v)
        }
        Err(e) => e.into_response(),
    }
}

async fn put_vm_config(
    state: State<SharedFixture>,
    Path((node, vmid)): Path<(String, String)>,
    form: Form<HashMap<String, String>>,
) -> Response {
    put_config(state, &node, &vmid, "qemu", form)
}

async fn put_ct_config(
    state: State<SharedFixture>,
    Path((node, vmid)): Path<(String, String)>,
    form: Form<HashMap<String, String>>,
) -> Response {
    put_config(state, &node, &vmid, "lxc", form)
}

/// `protection` is the one option either kind's config endpoint changes here.
fn put_config(
    State(state): State<SharedFixture>,
    node: &str,
    vmid: &str,
    kind: &str,
    Form(form): Form<HashMap<String, String>>,
) -> Response {
    let mut f = state.lock().unwrap();
    let vmid_num = match find_guest(&f, node, vmid, kind) {
        Ok(vm) => vm.vmid,
        Err(e) => return e.into_response(),
    };
    let protection = match form.get("protection").map(String::as_str) {
        Some("1") | Some("true") => Some(true),
        Some("0") | Some("false") => Some(false),
        Some(_) => {
            return pve_error(StatusCode::BAD_REQUEST, "Parameter verification failed.");
        }
        None => None,
    };
    if let Some(p) = protection
        && let Some(vm) = f.vms.iter_mut().find(|v| v.vmid == vmid_num)
    {
        vm.protection = p;
    }
    data(Value::Null)
}

async fn network_interfaces(
    State(state): State<SharedFixture>,
    Path((node, vmid)): Path<(String, String)>,
) -> Response {
    let f = state.lock().unwrap();
    let vm = match find_vm(&f, &node, &vmid) {
        Ok(vm) => vm,
        Err(e) => return e.into_response(),
    };
    if vm.status != "running" {
        return pve_error(
            StatusCode::INTERNAL_SERVER_ERROR,
            &format!("VM {} is not running", vm.vmid),
        );
    }
    let Some(ipv4) = &vm.ipv4 else {
        return pve_error(
            StatusCode::INTERNAL_SERVER_ERROR,
            "QEMU guest agent is not running",
        );
    };
    let mut addresses: Vec<Value> = ipv4
        .iter()
        .map(|ip| json!({ "ip-address-type": "ipv4", "ip-address": ip, "prefix": 24 }))
        .collect();
    addresses.push(
        json!({ "ip-address-type": "ipv6", "ip-address": "fe80::be24:11ff:fe00:1", "prefix": 64 }),
    );
    data(json!({
        "result": [
            {
                "name": "lo", "hardware-address": "00:00:00:00:00:00",
                "ip-addresses": [
                    { "ip-address-type": "ipv4", "ip-address": "127.0.0.1", "prefix": 8 },
                    { "ip-address-type": "ipv6", "ip-address": "::1", "prefix": 128 }
                ],
                "statistics": { "rx-bytes": 0, "tx-bytes": 0 }
            },
            {
                "name": "ens18", "hardware-address": "bc:24:11:00:00:01",
                "ip-addresses": addresses,
                "statistics": { "rx-bytes": 1024, "tx-bytes": 2048 }
            }
        ]
    }))
}

async fn ct_status(
    State(state): State<SharedFixture>,
    Path((node, vmid)): Path<(String, String)>,
) -> Response {
    let f = state.lock().unwrap();
    match find_guest(&f, &node, &vmid, "lxc") {
        Ok(ct) => {
            let running = ct.status == "running";
            let mut v = json!({
                "vmid": ct.vmid, "name": ct.name, "status": ct.status, "type": "lxc",
                "uptime": if running { ct.uptime_seconds } else { 0 },
                "cpus": ct.max_cpu, "maxmem": ct.max_memory_bytes, "maxswap": 536_870_912,
            });
            if ct.template {
                v["template"] = json!(1);
            }
            data(v)
        }
        Err(e) => e.into_response(),
    }
}

async fn ct_config(
    State(state): State<SharedFixture>,
    Path((node, vmid)): Path<(String, String)>,
) -> Response {
    let f = state.lock().unwrap();
    match find_guest(&f, &node, &vmid, "lxc") {
        Ok(ct) => {
            let mut v = json!({
                "hostname": ct.name, "cores": ct.max_cpu, "memory": ct.max_memory_bytes / 1024 / 1024,
                "swap": 512, "ostype": "fedora", "arch": "amd64", "unprivileged": 1,
                "features": "nesting=1", "rootfs": format!("local-lvm:vm-{}-disk-0,size=8G", ct.vmid),
                "net0": "name=eth0,bridge=vmbr0,hwaddr=BC:24:11:00:00:02,ip=dhcp,type=veth",
                "digest": format!("{:040x}", ct.vmid),
            });
            if ct.protection {
                v["protection"] = json!(1);
            }
            if ct.template {
                v["template"] = json!(1);
            }
            if !ct.tags.is_empty() {
                v["tags"] = Value::String(ct.tags.join(";"));
            }
            if let Some(description) = &ct.description {
                v["description"] = Value::String(description.clone());
            }
            data(v)
        }
        Err(e) => e.into_response(),
    }
}

/// What Proxmox reads from a running container's network namespace, one address per family
/// and interface; `null` for a container that is not running.
async fn ct_interfaces(
    State(state): State<SharedFixture>,
    Path((node, vmid)): Path<(String, String)>,
) -> Response {
    let f = state.lock().unwrap();
    let ct = match find_guest(&f, &node, &vmid, "lxc") {
        Ok(ct) => ct,
        Err(e) => return e.into_response(),
    };
    if ct.status != "running" {
        return data(Value::Null);
    }
    let mut interfaces = vec![json!({
        "name": "lo", "hwaddr": "00:00:00:00:00:00", "inet": "127.0.0.1/8", "inet6": "::1/128",
    })];
    let mut addresses: Vec<Value> = ct
        .ipv4
        .iter()
        .flatten()
        .map(|ip| json!({ "ip-address": ip, "ip-address-type": "inet", "prefix": 24 }))
        .collect();
    addresses.push(
        json!({ "ip-address": "fe80::be24:11ff:fe00:2", "ip-address-type": "inet6", "prefix": 64 }),
    );
    // inet and inet6 hold the last address of each family, as Proxmox writes them.
    let mut eth0 = json!({
        "name": "eth0", "hwaddr": "bc:24:11:00:00:02", "hardware-address": "bc:24:11:00:00:02",
        "inet6": "fe80::be24:11ff:fe00:2/64", "ip-addresses": addresses,
    });
    if let Some(ip) = ct.ipv4.as_ref().and_then(|ips| ips.last()) {
        eth0["inet"] = Value::String(format!("{ip}/24"));
    }
    interfaces.push(eth0);
    data(Value::Array(interfaces))
}

/// The UPID of a container's create task in the mock: one per container that has a create log.
fn create_upid(ct: &FixtureVm) -> String {
    format!(
        "UPID:{}:0000A0B1:0012C3D4:6720A000:vzcreate:{}:root@pam:",
        ct.node, ct.vmid
    )
}

/// `GET /nodes/{node}/tasks`: the create task of each container with a create log, filtered by
/// `vmid` and `typefilter` as Proxmox filters them.
async fn tasks(
    State(state): State<SharedFixture>,
    Path(node): Path<String>,
    Query(query): Query<HashMap<String, String>>,
) -> Response {
    let f = state.lock().unwrap();
    let listed: Vec<Value> = f
        .vms
        .iter()
        .filter(|vm| vm.node == node && vm.kind == "lxc" && vm.create_log.is_some())
        .filter(|vm| {
            query
                .get("vmid")
                .is_none_or(|id| *id == vm.vmid.to_string())
        })
        .filter(|_| query.get("typefilter").is_none_or(|t| t == "vzcreate"))
        .map(|ct| {
            json!({
                "upid": create_upid(ct), "node": ct.node, "pid": 41137, "pstart": 1230804,
                "starttime": 1_730_191_360u64, "endtime": 1_730_191_372u64, "type": "vzcreate",
                "id": ct.vmid.to_string(), "user": "root@pam", "status": "OK",
            })
        })
        .collect();
    data(Value::Array(listed))
}

async fn task_log(
    State(state): State<SharedFixture>,
    Path((node, upid)): Path<(String, String)>,
) -> Response {
    let f = state.lock().unwrap();
    let Some(ct) = f
        .vms
        .iter()
        .find(|vm| vm.node == node && vm.create_log.is_some() && create_upid(vm) == upid)
    else {
        return pve_error(StatusCode::INTERNAL_SERVER_ERROR, "no such task");
    };
    let lines: Vec<Value> = ct
        .create_log
        .iter()
        .flatten()
        .enumerate()
        .map(|(i, t)| json!({ "n": i + 1, "t": t }))
        .collect();
    data(Value::Array(lines))
}

async fn permissions(
    State(state): State<SharedFixture>,
    Query(query): Query<HashMap<String, String>>,
) -> Response {
    let f = state.lock().unwrap();
    let flag = if f.privileges_propagate { 1 } else { 0 };
    let privs: serde_json::Map<String, Value> = f
        .privileges()
        .into_iter()
        .map(|p| (p, json!(flag)))
        .collect();
    let mut out = serde_json::Map::new();
    match query.get("path") {
        Some(path) => {
            out.insert(path.clone(), Value::Object(privs));
        }
        None => {
            out.insert("/".into(), Value::Object(privs.clone()));
            out.insert("/vms".into(), Value::Object(privs));
        }
    }
    data(Value::Object(out))
}

async fn acl(State(state): State<SharedFixture>) -> Response {
    let f = state.lock().unwrap();
    let entries = f.acl.clone().unwrap_or_else(|| {
        vec![json!({ "path": "/", "ugid": "octopushy@pve!agent", "type": "token", "roleid": "PVEAuditor", "propagate": 1 })]
    });
    data(Value::Array(entries))
}

async fn list_vms(State(state): State<SharedFixture>) -> Response {
    let f = state.lock().unwrap();
    Json(f.vms.clone()).into_response()
}

async fn upsert_vm(State(state): State<SharedFixture>, Json(vm): Json<FixtureVm>) -> Response {
    let mut f = state.lock().unwrap();
    if !f.nodes.iter().any(|n| n.name == vm.node) {
        return (StatusCode::BAD_REQUEST, format!("unknown node {}", vm.node)).into_response();
    }
    f.vms.retain(|v| v.vmid != vm.vmid);
    f.vms.push(vm.clone());
    f.vms.sort_by_key(|v| v.vmid);
    (StatusCode::CREATED, Json(vm)).into_response()
}

async fn remove_vm(State(state): State<SharedFixture>, Path(vmid): Path<u32>) -> Response {
    let mut f = state.lock().unwrap();
    let before = f.vms.len();
    f.vms.retain(|v| v.vmid != vmid);
    if f.vms.len() == before {
        StatusCode::NOT_FOUND.into_response()
    } else {
        StatusCode::NO_CONTENT.into_response()
    }
}
