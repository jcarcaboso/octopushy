//! The `proxmox` executor: `observe`, `verify-guest`, `verify-absent`, and
//! `set-protection`, using Octopushy's Proxmox library and the secret
//! `proxmox.token`.

use std::net::Ipv4Addr;
use std::time::{Duration, Instant};

use octopushy_proxmox::{
    ApiToken, Client, ClientConfig, Error as PveError, GuestKind, Tls, VmResource, ipv4_addresses,
};
use serde::Deserialize;
use serde_json::{Value, json};

use super::{ExecContext, ExecResult, ExecutorConfig, Failure, Success};

pub const TOKEN_SECRET: &str = "proxmox.token";

/// `connection` in every input.
#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct Connection {
    pub endpoint: String,
    #[serde(default = "yes")]
    pub verify_tls: bool,
    #[serde(default)]
    pub ca_certificate_pem: Option<String>,
}

fn yes() -> bool {
    true
}

impl Connection {
    /// `caCertificatePem`, when set, is the only trust root, whatever
    /// `verifyTls` says.
    fn tls(&self) -> Tls {
        match &self.ca_certificate_pem {
            Some(pem) if !pem.trim().is_empty() => Tls::CustomCa(pem.clone()),
            _ if self.verify_tls => Tls::Verify,
            _ => Tls::Insecure,
        }
    }
}

pub(super) fn client(
    cfg: &ExecutorConfig,
    ctx: &ExecContext,
    conn: &Connection,
) -> Result<Client, Failure> {
    let token = ApiToken::new(ctx.secrets.get(TOKEN_SECRET)?)?;
    let tls = conn.tls();
    ctx.log.agent(format!(
        "connecting to {} as {} (TLS: {})",
        conn.endpoint,
        token.id(),
        match tls {
            Tls::Verify => "system trust store",
            Tls::Insecure => "not verified",
            Tls::CustomCa(_) => "custom CA",
        }
    ));
    Ok(Client::new(ClientConfig {
        endpoint: conn.endpoint.clone(),
        token,
        tls,
        timeout: cfg.proxmox_timeout,
    })?)
}

pub async fn run(cfg: &ExecutorConfig, ctx: &ExecContext) -> ExecResult {
    match ctx.action.as_str() {
        "observe" => observe(cfg, ctx).await,
        "verify-guest" => verify_guest(cfg, ctx).await,
        "verify-absent" => verify_absent(cfg, ctx).await,
        "set-protection" => set_protection(cfg, ctx).await,
        other => Err(Failure::msg(format!(
            "unknown proxmox action {other:?}; expected observe, verify-guest, verify-absent, or set-protection"
        ))),
    }
}

/// A JSON number that is an integer when `f` is whole.
fn number(f: f64) -> Value {
    if f.fract() == 0.0 && f.abs() < 9.0e15 {
        json!(f as i64)
    } else {
        json!(f)
    }
}

// ---------------------------------------------------------------------------
// observe

async fn observe(cfg: &ExecutorConfig, ctx: &ExecContext) -> ExecResult {
    #[derive(Deserialize)]
    struct Input {
        connection: Connection,
    }
    let input: Input = ctx.input()?;
    let c = client(cfg, ctx, &input.connection)?;

    let version = c.version().await?;
    ctx.check_cancelled()?;
    let status = c.cluster_status().await?;
    let nodes = c.nodes().await?;
    ctx.check_cancelled()?;
    let vms = c.vm_resources().await?;

    let cluster = status.cluster();
    match &cluster {
        Some(cl) => ctx.log.agent(format!(
            "Proxmox VE {}, cluster {} ({})",
            version.version,
            cl.name,
            if cl.quorate { "quorate" } else { "not quorate" }
        )),
        None => ctx
            .log
            .agent(format!("Proxmox VE {}, standalone host", version.version)),
    }
    let hosts: Vec<Value> = nodes
        .iter()
        .map(|n| {
            let online = n.is_online();
            let figure = |v: Option<Value>| if online { v } else { None };
            json!({
                "name": n.node,
                "online": online,
                "cpu": figure(n.cpu.map(|v| json!(v))),
                "maxCpu": figure(n.maxcpu.map(|v| json!(v))),
                "memoryBytes": figure(n.mem.map(|v| json!(v))),
                "maxMemoryBytes": figure(n.maxmem.map(|v| json!(v))),
                "uptimeSeconds": figure(n.uptime.map(|v| json!(v))),
            })
        })
        .collect();
    let guests: Vec<Value> = vms.iter().map(guest_json).collect();
    // `/cluster/resources` silently leaves out guests the token may not audit, so whether the list
    // is every VM has to be proven: unproven counts as incomplete.
    let visibility = vm_visibility(&c).await;
    if let Err(reason) = &visibility {
        ctx.log
            .agent(format!("the guests listed may not be every VM: {reason}"));
    }
    Ok(Success::outputs(json!({
        "version": version.version,
        "cluster": cluster.map(|c| json!({ "name": c.name, "quorate": c.quorate })),
        "hosts": hosts,
        "guests": guests,
        "vmVisibility": match &visibility {
            Ok(()) => json!({ "complete": true }),
            Err(reason) => json!({ "complete": false, "reason": reason }),
        },
    })))
}

/// Whether the token sees every VM: `VM.Audit` on `/vms` that propagates, and no ACL below
/// `/vms` or on a pool that could narrow it. The ACL list is only complete with `Sys.Audit` on
/// `/access`, since Proxmox leaves out entries a token may not audit. Anything not shown is a
/// reason it is not proven.
async fn vm_visibility(c: &octopushy_proxmox::Client) -> Result<(), String> {
    let vms = c
        .permissions(Some("/vms"))
        .await
        .map_err(|e| format!("the token's privileges on /vms could not be read: {e}"))?;
    if !vms.has("/vms", "VM.Audit") {
        return Err("the token lacks VM.Audit on /vms".into());
    }
    if !vms.propagates("/vms", "VM.Audit") {
        return Err("the token's VM.Audit on /vms does not propagate to the VMs below it".into());
    }
    let access = c
        .permissions(Some("/access"))
        .await
        .map_err(|e| format!("the token's privileges on /access could not be read: {e}"))?;
    if !access.has("/access", "Sys.Audit") {
        return Err(
            "the token lacks Sys.Audit on /access, so the ACLs it can read may leave out one that narrows what it sees"
                .into(),
        );
    }
    let acl = c
        .acl()
        .await
        .map_err(|e| format!("the ACLs could not be read: {e}"))?;
    let narrowing: Vec<&str> = acl
        .iter()
        .map(|entry| entry.path.as_str())
        .filter(|path| path.starts_with("/vms/") || *path == "/pool" || path.starts_with("/pool/"))
        .collect();
    if !narrowing.is_empty() {
        return Err(format!(
            "ACLs on {} may narrow what the token sees below /vms",
            narrowing.join(", ")
        ));
    }
    Ok(())
}

fn guest_json(vm: &VmResource) -> Value {
    json!({
        "vmid": vm.vmid,
        "name": vm.name,
        "host": vm.node,
        "kind": vm.kind.as_str(),
        "status": vm.status,
        "template": vm.template,
        "tags": vm.tags,
        "maxCpu": vm.maxcpu.map(number),
        "maxMemoryBytes": vm.maxmem,
        "uptimeSeconds": vm.uptime,
    })
}

/// Proxmox lists only the guests a token may audit, so "not listed" means
/// "absent" only if the token holds `VM.Audit` on the VMID.
async fn require_audit(c: &Client, vmid: u32) -> Result<(), Failure> {
    let path = format!("/vms/{vmid}");
    let perms = c.permissions(Some(&path)).await?;
    if perms.has(&path, "VM.Audit") {
        Ok(())
    } else {
        Err(Failure::msg(format!(
            "the Proxmox token lacks VM.Audit on {path}, so it cannot tell whether VM {vmid} exists"
        )))
    }
}

// ---------------------------------------------------------------------------
// verify-guest

/// The kind of machine the controller expects at the VMID: `qemu` (the default, and what a
/// controller before containers meant) or `lxc`. Verify and protection use its endpoints, and a
/// machine of another kind at that VMID fails them: it is not the guest's.
fn expected_kind(kind: &Option<String>) -> Result<GuestKind, Failure> {
    match kind.as_deref().unwrap_or("qemu") {
        "qemu" => Ok(GuestKind::Qemu),
        "lxc" => Ok(GuestKind::Lxc),
        other => Err(Failure::msg(format!(
            "kind {other:?} is not a machine Octopushy manages; expected qemu or lxc"
        ))),
    }
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
struct VerifyGuestInput {
    connection: Connection,
    node: String,
    vmid: u32,
    #[serde(default)]
    kind: Option<String>,
    #[serde(default)]
    name: Option<String>,
    #[serde(default = "yes")]
    expect_running: bool,
    #[serde(default)]
    expect_ipv4: Option<String>,
    #[serde(default = "default_guest_wait")]
    wait_seconds: u64,
    /// Read the SSH host keys Proxmox generated for a container, from its create task, once every
    /// other check passes. Only for `lxc`.
    #[serde(default)]
    host_keys: bool,
}

fn default_guest_wait() -> u64 {
    180
}

#[derive(Debug, Clone)]
struct Check {
    name: &'static str,
    ok: bool,
    detail: String,
}

#[derive(Debug, Clone, Default)]
struct GuestReport {
    present: bool,
    node: Option<String>,
    status: Option<String>,
    name: Option<String>,
    /// The VM's tags and its description, by which the controller tells a VM its
    /// guest's module made from any other VM at that VMID.
    tags: Vec<String>,
    description: Option<String>,
    ipv4: Vec<Ipv4Addr>,
    checks: Vec<Check>,
}

impl GuestReport {
    fn passed(&self) -> bool {
        self.checks.iter().all(|c| c.ok)
    }

    fn failures(&self) -> String {
        self.checks
            .iter()
            .filter(|c| !c.ok)
            .map(|c| format!("{}: {}", c.name, c.detail))
            .collect::<Vec<_>>()
            .join("; ")
    }

    fn to_json(&self) -> Value {
        json!({
            "present": self.present,
            "node": self.node,
            "status": self.status,
            "name": self.name,
            "tags": self.tags,
            "description": self.description,
            "ipv4Addresses": self.ipv4.iter().map(|ip| ip.to_string()).collect::<Vec<_>>(),
            "checks": self.checks.iter().map(|c| json!({
                "name": c.name, "ok": c.ok, "detail": c.detail,
            })).collect::<Vec<_>>(),
        })
    }
}

fn parse_ipv4(s: &str) -> Result<Ipv4Addr, Failure> {
    let addr = s.split('/').next().unwrap_or(s).trim();
    addr.parse()
        .map_err(|_| Failure::msg(format!("expectIpv4 {s:?} is not an IPv4 address")))
}

async fn evaluate_guest(
    c: &Client,
    input: &VerifyGuestInput,
    kind: &GuestKind,
    expect_ip: Option<Ipv4Addr>,
) -> Result<GuestReport, PveError> {
    let vmid = input.vmid;
    let noun = kind.noun();
    let mut report = GuestReport::default();
    let check = |name, ok, detail: String| Check { name, ok, detail };

    let Some(vm) = c.find_vm(vmid).await? else {
        report.checks.push(check(
            "present",
            false,
            format!("{noun} {vmid} does not exist"),
        ));
        return Ok(report);
    };
    report.present = true;
    report.node = Some(vm.node.clone());
    report.name = vm.name.clone();
    report.tags = vm.tags.clone();
    report
        .checks
        .push(check("present", true, format!("{noun} {vmid} exists")));
    // VMIDs are one namespace across VMs and containers: a machine of the other kind is not this one.
    if &vm.kind != kind {
        report.checks.push(check(
            "kind",
            false,
            format!(
                "VMID {vmid} is a {} guest, not {}",
                vm.kind.as_str(),
                kind.as_str()
            ),
        ));
        return Ok(report);
    }
    let description = match kind {
        GuestKind::Lxc => c.ct_config(&vm.node, vmid).await.map(|c| c.description),
        _ => c.vm_config(&vm.node, vmid).await.map(|config| {
            config
                .other
                .get("description")
                .and_then(Value::as_str)
                .map(str::to_owned)
        }),
    };
    match description {
        Ok(description) => report.description = description,
        // It vanished between the two requests; the next round sees that.
        Err(PveError::NotFound(_)) => {}
        Err(e) => return Err(e),
    }

    let placed = vm.node == input.node;
    report.checks.push(check(
        "placement",
        placed,
        if placed {
            format!("{noun} {vmid} is on {}", vm.node)
        } else {
            format!("{noun} {vmid} is on {}, not {}", vm.node, input.node)
        },
    ));

    if let Some(expected) = &input.name {
        let actual = vm.name.as_deref().unwrap_or("");
        report.checks.push(check(
            "name",
            actual == expected,
            if actual == expected {
                format!("name is {actual}")
            } else {
                format!("name is {actual:?}, expected {expected:?}")
            },
        ));
    }

    let current = match kind {
        GuestKind::Lxc => c.ct_status(&vm.node, vmid).await.map(|s| s.status),
        _ => c.vm_status(&vm.node, vmid).await.map(|s| s.status),
    };
    let status = match current {
        Ok(status) => status,
        // It vanished between the two requests; the next round sees that.
        Err(PveError::NotFound(_)) => vm.status.clone(),
        Err(e) => return Err(e),
    };
    report.status = Some(status.clone());
    let running = status == "running";
    if input.expect_running {
        report.checks.push(check(
            "running",
            running,
            format!(
                "{noun} {vmid} is {}",
                if status.is_empty() {
                    "in an unknown state"
                } else {
                    &status
                }
            ),
        ));
    }

    if !running && !input.expect_running {
        report.checks.push(check(
            "address",
            true,
            format!("not checked: the {noun} is not running and expectRunning is false"),
        ));
    } else if kind == &GuestKind::Lxc {
        // Proxmox reads a container's addresses from its network namespace: no agent inside is needed.
        match c.ct_interfaces(&vm.node, vmid).await {
            Ok(interfaces) => {
                report.ipv4 = interfaces
                    .as_deref()
                    .map(octopushy_proxmox::ct_ipv4_addresses)
                    .unwrap_or_default();
                let list = report
                    .ipv4
                    .iter()
                    .map(|ip| ip.to_string())
                    .collect::<Vec<_>>()
                    .join(", ");
                let (ok, detail) = match expect_ip {
                    _ if interfaces.is_none() => (
                        false,
                        "Proxmox reports no interfaces: the container is not running".to_owned(),
                    ),
                    _ if report.ipv4.is_empty() => (
                        false,
                        "Proxmox reports no IPv4 address for the container".to_owned(),
                    ),
                    Some(want) if !report.ipv4.contains(&want) => {
                        (false, format!("Proxmox reports {list}, not {want}"))
                    }
                    _ => (true, format!("Proxmox reports {list}")),
                };
                report.checks.push(check("address", ok, detail));
            }
            Err(PveError::NotFound(_)) => {
                report.checks.push(check(
                    "address",
                    false,
                    format!("{noun} {vmid} disappeared"),
                ));
            }
            Err(e) => return Err(e),
        }
    } else {
        match c.guest_network_interfaces(&vm.node, vmid).await {
            Ok(ifaces) => {
                report.ipv4 = ipv4_addresses(&ifaces);
                let list = report
                    .ipv4
                    .iter()
                    .map(|ip| ip.to_string())
                    .collect::<Vec<_>>()
                    .join(", ");
                let (ok, detail) = match expect_ip {
                    _ if report.ipv4.is_empty() => {
                        (false, "the guest agent reports no IPv4 address".to_owned())
                    }
                    Some(want) if !report.ipv4.contains(&want) => {
                        (false, format!("guest agent reports {list}, not {want}"))
                    }
                    _ => (true, format!("guest agent reports {list}")),
                };
                report.checks.push(check("address", ok, detail));
            }
            Err(PveError::GuestAgentUnavailable(m)) => {
                report.checks.push(check(
                    "address",
                    false,
                    format!("the guest agent is not available ({})", m.trim()),
                ));
            }
            Err(PveError::NotFound(_)) => {
                report
                    .checks
                    .push(check("address", false, format!("VM {vmid} disappeared")));
            }
            Err(e) => return Err(e),
        }
    }
    Ok(report)
}

async fn verify_guest(cfg: &ExecutorConfig, ctx: &ExecContext) -> ExecResult {
    let input: VerifyGuestInput = ctx.input()?;
    let kind = expected_kind(&input.kind)?;
    let expect_ip = input.expect_ipv4.as_deref().map(parse_ipv4).transpose()?;
    let c = client(cfg, ctx, &input.connection)?;
    require_audit(&c, input.vmid).await?;
    ctx.log.agent(format!(
        "verifying {} {} on {} (up to {}s)",
        kind.noun(),
        input.vmid,
        input.node,
        input.wait_seconds
    ));

    let deadline = Instant::now() + Duration::from_secs(input.wait_seconds);
    let mut last_report: Option<GuestReport> = None;
    let mut last_summary = String::new();
    loop {
        match evaluate_guest(&c, &input, &kind, expect_ip).await {
            Ok(report) if report.passed() => {
                for check in &report.checks {
                    ctx.log
                        .agent(format!("{}: ok ({})", check.name, check.detail));
                }
                let mut outputs = report.to_json();
                if input.host_keys {
                    let keys = created_host_keys(&c, &kind, &input.node, input.vmid)
                        .await
                        .map_err(|e| Failure::with_outputs(e, report.to_json()))?;
                    for key in &keys.keys {
                        ctx.log.agent(format!(
                            "host key: {} {} (from {})",
                            key.kind, key.fingerprint, keys.task
                        ));
                    }
                    outputs["hostKeys"] = json!(
                        keys.keys
                            .iter()
                            .map(|k| format!("{} {}", k.kind, k.fingerprint))
                            .collect::<Vec<_>>()
                    );
                    outputs["hostKeysTask"] = json!(keys.task);
                }
                return Ok(Success::outputs(outputs));
            }
            Ok(report) => {
                let summary = report.failures();
                if summary != last_summary {
                    ctx.log.agent(format!("waiting: {summary}"));
                    last_summary = summary;
                }
                last_report = Some(report);
            }
            Err(e) if e.is_transient() => {
                let summary = format!("Proxmox API error, retrying: {e}");
                if summary != last_summary {
                    ctx.log.agent(&summary);
                    last_summary = summary;
                }
            }
            Err(e) => {
                return Err(match last_report {
                    Some(r) => Failure::with_outputs(e.to_string(), r.to_json()),
                    None => e.into(),
                });
            }
        }
        let now = Instant::now();
        if now >= deadline {
            let message = format!(
                "{} {} did not pass verification within {}s: {last_summary}",
                kind.noun(),
                input.vmid,
                input.wait_seconds
            );
            return Err(match last_report {
                Some(r) => Failure::with_outputs(message, r.to_json()),
                None => Failure::msg(message),
            });
        }
        ctx.sleep(cfg.poll_interval.min(deadline - now)).await?;
    }
}

/// The host keys of a container, and the task they come from.
struct CreatedKeys {
    task: String,
    keys: Vec<octopushy_proxmox::CreatedHostKey>,
}

/// The SSH host keys Proxmox generated for a container when it created it, read from the log of
/// the newest successful create task (`vzcreate`) of its VMID on its node. Proxmox writes them
/// into the container's root file system before it first starts, so what the log shows is the
/// container's own identity, through the API the installation is trusted for. Anything that keeps
/// them from being read is a failure: nothing here falls back to asking the container itself.
async fn created_host_keys(
    c: &Client,
    kind: &GuestKind,
    node: &str,
    vmid: u32,
) -> Result<CreatedKeys, String> {
    if kind != &GuestKind::Lxc {
        return Err(
            "host keys are read from a container's create task; this is not a container".to_owned(),
        );
    }
    let tasks = c.guest_tasks(node, vmid, "vzcreate").await.map_err(|e| {
        format!(
            "the create task of container {vmid} could not be listed: {e}. Reading a task another token started needs Sys.Audit on /nodes/{node}"
        )
    })?;
    let id = vmid.to_string();
    let Some(task) = tasks
        .iter()
        .filter(|t| t.kind == "vzcreate" && t.id.as_deref() == Some(id.as_str()))
        .filter(|t| t.status.as_deref() == Some("OK"))
        .max_by_key(|t| (t.starttime.unwrap_or(0), t.upid.clone()))
    else {
        return Err(format!(
            "Proxmox lists no successful create task (vzcreate) of container {vmid} on {node}, so its host keys cannot be read. \
             A token sees another token's tasks only with Sys.Audit on /nodes/{node}"
        ));
    };
    let log = c.task_log(node, &task.upid).await.map_err(|e| {
        format!(
            "the log of {} could not be read: {e}. Reading a task another token started needs Sys.Audit on /nodes/{node}",
            task.upid
        )
    })?;
    let keys = octopushy_proxmox::created_host_keys(&log);
    if keys.is_empty() {
        return Err(format!(
            "the create task {} of container {vmid} logged no SSH host key: Proxmox makes them only for a template with /etc/ssh. \
             Use a template with OpenSSH, or set access.hostKey",
            task.upid
        ));
    }
    Ok(CreatedKeys {
        task: task.upid.clone(),
        keys,
    })
}

// ---------------------------------------------------------------------------
// verify-absent

async fn verify_absent(cfg: &ExecutorConfig, ctx: &ExecContext) -> ExecResult {
    #[derive(Deserialize)]
    #[serde(rename_all = "camelCase")]
    struct Input {
        connection: Connection,
        vmid: u32,
        #[serde(default = "default_absent_wait")]
        wait_seconds: u64,
    }
    fn default_absent_wait() -> u64 {
        60
    }
    let input: Input = ctx.input()?;
    let c = client(cfg, ctx, &input.connection)?;
    require_audit(&c, input.vmid).await?;

    let deadline = Instant::now() + Duration::from_secs(input.wait_seconds);
    let mut last = String::new();
    loop {
        match c.find_vm(input.vmid).await {
            Ok(None) => {
                ctx.log.agent(format!("VM {} is absent", input.vmid));
                return Ok(Success::outputs(json!({ "present": false })));
            }
            Ok(Some(vm)) => {
                let summary = format!("VM {} still exists on {} ({})", vm.vmid, vm.node, vm.status);
                if summary != last {
                    ctx.log.agent(format!("waiting: {summary}"));
                    last = summary;
                }
            }
            Err(e) if e.is_transient() => {
                last = format!("Proxmox API error: {e}");
                ctx.log.agent(format!("{last}; retrying"));
            }
            Err(e) => return Err(e.into()),
        }
        let now = Instant::now();
        if now >= deadline {
            return Err(Failure::with_outputs(
                format!("{last} after {} seconds", input.wait_seconds),
                json!({ "present": true }),
            ));
        }
        ctx.sleep(cfg.poll_interval.min(deadline - now)).await?;
    }
}

// ---------------------------------------------------------------------------
// set-protection

async fn set_protection(cfg: &ExecutorConfig, ctx: &ExecContext) -> ExecResult {
    #[derive(Deserialize)]
    #[serde(rename_all = "camelCase")]
    struct Input {
        connection: Connection,
        node: String,
        vmid: u32,
        #[serde(default)]
        kind: Option<String>,
        protection: bool,
    }
    let input: Input = ctx.input()?;
    let kind = expected_kind(&input.kind)?;
    let noun = kind.noun();
    let c = client(cfg, ctx, &input.connection)?;
    require_audit(&c, input.vmid).await?;

    let Some(vm) = c.find_vm(input.vmid).await? else {
        ctx.log.agent(format!(
            "{noun} {} does not exist; nothing to change",
            input.vmid
        ));
        return Ok(Success::outputs(
            json!({ "absent": true, "previous": null, "protection": null }),
        ));
    };
    if vm.node != input.node {
        return Err(Failure::msg(format!(
            "{noun} {} is on {}, not {}; refusing to change its protection",
            input.vmid, vm.node, input.node
        )));
    }
    if vm.kind != kind {
        return Err(Failure::msg(format!(
            "VMID {} is a {} guest, not {}; refusing to change its protection",
            input.vmid,
            vm.kind.as_str(),
            kind.as_str()
        )));
    }

    let previous = match protection_of(&c, &kind, &vm.node, input.vmid).await {
        Ok(protection) => protection,
        Err(PveError::NotFound(_)) => {
            return Ok(Success::outputs(
                json!({ "absent": true, "previous": null, "protection": null }),
            ));
        }
        Err(e) => return Err(e.into()),
    };
    let word = |b: bool| if b { "on" } else { "off" };
    if previous == input.protection {
        ctx.log.agent(format!(
            "protection of {noun} {} is already {}",
            input.vmid,
            word(previous)
        ));
    } else {
        ctx.log.agent(format!(
            "turning protection of {noun} {} {}",
            input.vmid,
            word(input.protection)
        ));
        let written = match kind {
            GuestKind::Lxc => {
                c.set_ct_protection(&vm.node, input.vmid, input.protection)
                    .await
            }
            _ => {
                c.set_vm_protection(&vm.node, input.vmid, input.protection)
                    .await
            }
        };
        written.map_err(|e| protection_failure(e, noun, input.vmid))?;
    }
    let now = match protection_of(&c, &kind, &vm.node, input.vmid).await {
        Ok(protection) => protection,
        // Proxmox accepted the write, so a failed read-back cannot mean nothing changed.
        Err(e) if previous != input.protection => {
            return Err(Failure::Uncertain(format!(
                "{e}; Proxmox accepted the change to the protection of {noun} {} but it could not be confirmed",
                input.vmid
            )));
        }
        Err(e) => return Err(e.into()),
    };
    if now != input.protection {
        return Err(Failure::with_outputs(
            format!(
                "Proxmox still reports protection {} for {noun} {}",
                word(now),
                input.vmid
            ),
            json!({ "absent": false, "previous": previous, "protection": now }),
        ));
    }
    Ok(Success::outputs(
        json!({ "absent": false, "previous": previous, "protection": now }),
    ))
}

/// The `protection` option of a VM's or a container's configuration.
async fn protection_of(
    c: &Client,
    kind: &GuestKind,
    node: &str,
    vmid: u32,
) -> Result<bool, PveError> {
    match kind {
        GuestKind::Lxc => c.ct_config(node, vmid).await.map(|c| c.protection),
        _ => c.vm_config(node, vmid).await.map(|c| c.protection),
    }
}

/// A failed `set-protection` request. A rejection (bad token, missing privilege, a client error)
/// changed nothing. When the request may have reached Proxmox but the answer was lost or was a
/// server-side error, the agent cannot tell whether the change was made.
fn protection_failure(error: PveError, noun: &str, vmid: u32) -> Failure {
    let may_have_applied = matches!(error, PveError::Transport(_) | PveError::Decode { .. })
        || matches!(error, PveError::Status { status, .. } if status >= 500);
    if may_have_applied {
        Failure::Uncertain(format!(
            "{error}; Proxmox may have changed the protection of {noun} {vmid}"
        ))
    } else {
        error.into()
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn a_protection_request_that_may_have_been_applied_is_uncertain() {
        let status = |status| PveError::Status {
            status,
            message: "x".into(),
        };
        let uncertain = [
            PveError::Transport("connection reset".into()),
            PveError::Decode {
                path: "/nodes/n/qemu/1/config".into(),
                message: "not json".into(),
            },
            status(500),
            status(502),
            status(599),
        ];
        for e in uncertain {
            assert!(
                matches!(protection_failure(e, "VM", 210), Failure::Uncertain(m) if m.contains("VM 210")),
            );
        }
        let rejected = [
            PveError::Unauthorized,
            PveError::Forbidden("VM.Config.Options".into()),
            PveError::NotFound("vm".into()),
            PveError::Config("bad".into()),
            status(400),
            status(499),
        ];
        for e in rejected {
            assert!(matches!(
                protection_failure(e, "container", 210),
                Failure::Failed { .. }
            ));
        }
    }
}
