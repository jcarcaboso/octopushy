//! Explicit, bounded native root growth; not an arbitrary guest script runner.
use super::{ExecContext, ExecResult, ExecutorConfig, Failure, Success, proxmox};
use serde::Deserialize;
use serde_json::json;
use std::time::{Duration, Instant};

#[derive(Deserialize)]
#[serde(rename_all = "camelCase")]
struct Input {
    connection: proxmox::Connection,
    node: String,
    vmid: u32,
    name: String,
    address: String,
    disk_bytes: u64,
    worker_generation: Option<String>,
    wait_seconds: u64,
}

#[derive(Deserialize, serde::Serialize)]
#[serde(rename_all = "camelCase")]
struct Report {
    disk_bytes: u64,
    worker_generation: Option<String>,
    partition_bytes: u64,
    filesystem_bytes: u64,
    usable_bytes: u64,
    uuid: String,
    changed: bool,
}

pub async fn run(cfg: &ExecutorConfig, ctx: &ExecContext) -> ExecResult {
    if !matches!(ctx.action.as_str(), "grow" | "verify") {
        return Err(Failure::msg("nixos-rootfs supports only grow and verify"));
    }
    let input: Input = ctx.input()?;
    if !(2 * 1024u64.pow(3)..=65536 * 1024u64.pow(3)).contains(&input.disk_bytes)
        || !input.disk_bytes.is_multiple_of(1024u64.pow(3))
        || input.address.is_empty()
        || input.name.is_empty()
        || !(1..=300).contains(&input.wait_seconds)
    {
        return Err(Failure::msg("Invalid root filesystem expectations"));
    }
    let c = proxmox::client(cfg, ctx, &input.connection)?;
    let vm = c.vm_config(&input.node, input.vmid).await?;
    let mut description = format!("Managed by Octopushy as {}.", input.address);
    if let Some(generation) = &input.worker_generation {
        if !crate::managed_identity::valid_uuid(generation) {
            return Err(Failure::msg("Invalid worker generation"));
        }
        description.push_str(&format!("\nworker-generation: {generation}"));
    }
    if vm.name.as_deref() != Some(&input.name)
        || vm.template
        || !vm.tags.iter().any(|t| t == "octopushy")
        || vm.other.get("description").and_then(|v| v.as_str()) != Some(&description)
    {
        return Err(Failure::msg(
            "VM ownership/identity does not match; no guest command was sent",
        ));
    }
    ctx.check_cancelled()?;
    let command = vec![
        "/run/current-system/sw/bin/flock".into(),
        "-n".into(),
        "-E".into(),
        "75".into(),
        "/run/lock/octopushy-nixos.lock".into(),
        "/run/current-system/sw/bin/bash".into(),
        "-c".into(),
        include_str!("nixos-rootfs.sh").into(),
        "octopushy-rootfs".into(),
        ctx.action.clone(),
        input.disk_bytes.to_string(),
    ];
    // A lost POST/status response must never become an automatic replay of a mutation.
    let unknown = |detail: String| {
        if ctx.action == "grow" {
            Failure::Uncertain(detail)
        } else {
            Failure::msg(detail)
        }
    };
    let pid = c
        .guest_exec(&input.node, input.vmid, &command, None)
        .await
        .map_err(|e| {
            unknown(format!(
                "Guest command submission outcome unknown: {e}. Inspect before retrying."
            ))
        })?;
    ctx.log.agent(format!(
        "guest root {} started as PID {pid}; expected disk {} bytes",
        ctx.action, input.disk_bytes
    ));
    let deadline = Instant::now() + Duration::from_secs(input.wait_seconds);
    let status = loop {
        let status = c
            .guest_exec_status(&input.node, input.vmid, pid)
            .await
            .map_err(|e| {
                unknown(format!(
                    "Guest PID {pid} outcome unknown: {e}. Inspect before retrying."
                ))
            })?;
        if status.exited {
            break status;
        }
        if Instant::now() >= deadline {
            return Err(unknown(format!(
                "Guest PID {pid} did not finish before the deadline. Inspect before retrying."
            )));
        }
        ctx.sleep(cfg.poll_interval).await?;
    };
    if status.stdout_truncated
        || status.stderr_truncated
        || status.stdout.len() > 16384
        || status.stderr.len() > 16384
    {
        return Err(Failure::msg(
            "Guest output was truncated or oversized; capacity is not verified",
        ));
    }
    if status.signal.is_some() || status.exitcode != Some(0) {
        // A signal may leave a native systemd job running after the command process dies.
        if status.signal.is_some() {
            return Err(unknown(format!(
                "Guest PID {pid} was signalled; inspect native growth jobs before retrying."
            )));
        }
        if status.exitcode == Some(75) {
            return Err(Failure::msg(
                "The native NixOS operation lock is held; retry this guest step after the other operation finishes.",
            ));
        }
        return Err(Failure::msg(format!(
            "Guest root {} failed (exit {:?}): {}",
            ctx.action,
            status.exitcode,
            status.stderr.trim()
        )));
    }
    let report: Report = serde_json::from_str(&status.stdout)
        .map_err(|_| Failure::msg("Guest did not return a valid capacity report"))?;
    if report.disk_bytes != input.disk_bytes
        || report.partition_bytes > report.disk_bytes
        || report.disk_bytes - report.partition_bytes > 1024u64.pow(3) + 1048576
        || report.filesystem_bytes > report.partition_bytes
        || report.partition_bytes - report.filesystem_bytes >= 65536
        || report.usable_bytes == 0
        || report.usable_bytes > report.filesystem_bytes
        || report.uuid.len() != 36
        || !report
            .uuid
            .bytes()
            .all(|c| c.is_ascii_hexdigit() || c == b'-')
        || (ctx.action == "verify" && report.changed)
    {
        return Err(Failure::msg(
            "Guest capacity report does not meet the declared root filesystem requirements",
        ));
    }
    ctx.log.agent(format!(
        "root filesystem {}: {} bytes, usable {} bytes; changed={}",
        report.uuid, report.filesystem_bytes, report.usable_bytes, report.changed
    ));
    Ok(Success::outputs(json!(report)))
}
