//! The fake API: the Proxmox endpoints Octopushy uses, plus `/mock` admin routes.
//!
//! Responses imitate Proxmox closely where clients depend on it: the `{"data":
//! ...}` envelope, booleans as `0`/`1`, `;`-separated tags, and errors with the
//! message in the HTTP status line (for example `500 QEMU guest agent is not
//! running`).

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
        .route("/access/permissions", get(permissions))
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
    let expected = format!("PVEAPIToken={}", state.lock().unwrap().token);
    let given = req
        .headers()
        .get(header::AUTHORIZATION)
        .and_then(|v| v.to_str().ok());
    if given != Some(expected.as_str()) {
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
        .find(|vm| vm.vmid == id && vm.node == node && vm.kind == "qemu")
        .ok_or_else(|| {
            PveError(
                StatusCode::INTERNAL_SERVER_ERROR,
                format!("Configuration file 'nodes/{node}/qemu-server/{vmid}.conf' does not exist"),
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
            data(v)
        }
        Err(e) => e.into_response(),
    }
}

async fn put_vm_config(
    State(state): State<SharedFixture>,
    Path((node, vmid)): Path<(String, String)>,
    Form(form): Form<HashMap<String, String>>,
) -> Response {
    let mut f = state.lock().unwrap();
    let vmid_num = match find_vm(&f, &node, &vmid) {
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

async fn permissions(
    State(state): State<SharedFixture>,
    Query(query): Query<HashMap<String, String>>,
) -> Response {
    let f = state.lock().unwrap();
    let privs: serde_json::Map<String, Value> =
        f.privileges().into_iter().map(|p| (p, json!(1))).collect();
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
