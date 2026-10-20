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

fn client(cfg: &ExecutorConfig, ctx: &ExecContext, conn: &Connection) -> Result<Client, Failure> {
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
    ctx.log.agent(format!(
        "{} hosts ({} online), {} guests",
        hosts.len(),
        nodes.iter().filter(|n| n.is_online()).count(),
        guests.len()
    ));

    Ok(Success::outputs(json!({
        "version": version.version,
        "cluster": cluster.map(|c| json!({ "name": c.name, "quorate": c.quorate })),
        "hosts": hosts,
        "guests": guests,
    })))
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

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
struct VerifyGuestInput {
    connection: Connection,
    node: String,
    vmid: u32,
    #[serde(default)]
    name: Option<String>,
    #[serde(default = "yes")]
    expect_running: bool,
    #[serde(default)]
    expect_ipv4: Option<String>,
    #[serde(default = "default_guest_wait")]
    wait_seconds: u64,
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
    expect_ip: Option<Ipv4Addr>,
) -> Result<GuestReport, PveError> {
    let vmid = input.vmid;
    let mut report = GuestReport::default();
    let check = |name, ok, detail: String| Check { name, ok, detail };

    let Some(vm) = c.find_vm(vmid).await? else {
        report
            .checks
            .push(check("present", false, format!("VM {vmid} does not exist")));
        return Ok(report);
    };
    report.present = true;
    report.node = Some(vm.node.clone());
    report.name = vm.name.clone();
    report
        .checks
        .push(check("present", true, format!("VM {vmid} exists")));

    let placed = vm.node == input.node;
    report.checks.push(check(
        "placement",
        placed,
        if placed {
            format!("VM {vmid} is on {}", vm.node)
        } else {
            format!("VM {vmid} is on {}, not {}", vm.node, input.node)
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

    let is_qemu = vm.kind == GuestKind::Qemu;
    let status = if is_qemu {
        match c.vm_status(&vm.node, vmid).await {
            Ok(s) => s.status,
            // It vanished between the two requests; the next round sees that.
            Err(PveError::NotFound(_)) => vm.status.clone(),
            Err(e) => return Err(e),
        }
    } else {
        vm.status.clone()
    };
    report.status = Some(status.clone());
    let running = status == "running";
    if input.expect_running {
        report.checks.push(check(
            "running",
            running,
            format!(
                "VM {vmid} is {}",
                if status.is_empty() {
                    "in an unknown state"
                } else {
                    &status
                }
            ),
        ));
    }

    if !is_qemu {
        report.checks.push(check(
            "address",
            false,
            format!(
                "VM {vmid} is a {} guest; addresses come from the QEMU guest agent",
                vm.kind.as_str()
            ),
        ));
    } else if !running && !input.expect_running {
        report.checks.push(check(
            "address",
            true,
            "not checked: the VM is not running and expectRunning is false".to_owned(),
        ));
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
    let expect_ip = input.expect_ipv4.as_deref().map(parse_ipv4).transpose()?;
    let c = client(cfg, ctx, &input.connection)?;
    require_audit(&c, input.vmid).await?;
    ctx.log.agent(format!(
        "verifying VM {} on {} (up to {}s)",
        input.vmid, input.node, input.wait_seconds
    ));

    let deadline = Instant::now() + Duration::from_secs(input.wait_seconds);
    let mut last_report: Option<GuestReport> = None;
    let mut last_summary = String::new();
    loop {
        match evaluate_guest(&c, &input, expect_ip).await {
            Ok(report) if report.passed() => {
                for check in &report.checks {
                    ctx.log
                        .agent(format!("{}: ok ({})", check.name, check.detail));
                }
                return Ok(Success::outputs(report.to_json()));
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
                "VM {} did not pass verification within {}s: {last_summary}",
                input.vmid, input.wait_seconds
            );
            return Err(match last_report {
                Some(r) => Failure::with_outputs(message, r.to_json()),
                None => Failure::msg(message),
            });
        }
        ctx.sleep(cfg.poll_interval.min(deadline - now)).await?;
    }
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
        protection: bool,
    }
    let input: Input = ctx.input()?;
    let c = client(cfg, ctx, &input.connection)?;
    require_audit(&c, input.vmid).await?;

    let Some(vm) = c.find_vm(input.vmid).await? else {
        ctx.log.agent(format!(
            "VM {} does not exist; nothing to change",
            input.vmid
        ));
        return Ok(Success::outputs(
            json!({ "absent": true, "previous": null, "protection": null }),
        ));
    };
    if vm.node != input.node {
        return Err(Failure::msg(format!(
            "VM {} is on {}, not {}; refusing to change its protection",
            input.vmid, vm.node, input.node
        )));
    }
    if vm.kind != GuestKind::Qemu {
        return Err(Failure::msg(format!(
            "VM {} is a {} guest; only QEMU VMs are supported",
            input.vmid,
            vm.kind.as_str()
        )));
    }

    let previous = match c.vm_config(&vm.node, input.vmid).await {
        Ok(config) => config.protection,
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
            "protection of VM {} is already {}",
            input.vmid,
            word(previous)
        ));
    } else {
        ctx.log.agent(format!(
            "turning protection of VM {} {}",
            input.vmid,
            word(input.protection)
        ));
        c.set_vm_protection(&vm.node, input.vmid, input.protection)
            .await?;
    }
    let now = c.vm_config(&vm.node, input.vmid).await?.protection;
    if now != input.protection {
        return Err(Failure::with_outputs(
            format!(
                "Proxmox still reports protection {} for VM {}",
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
