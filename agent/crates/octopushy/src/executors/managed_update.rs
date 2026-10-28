//! Controller-approved NixOS closure activation. No repository script is executed.
use super::{ExecContext, ExecResult, ExecutorConfig, Failure, Success, proxmox};
use crate::managed_identity::valid_uuid;
use base64::{Engine, engine::general_purpose::STANDARD};
use octopushy_proxmox::Error;
use serde::Deserialize;
use serde_json::Value;
use std::time::{Duration, Instant};

#[derive(Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct Release {
    agent_id: String,
    archive_url: String,
    archive_sha256: String,
    system_path: String,
    image_sha256: String,
    ca_certificate_pem: Option<String>,
}

#[derive(Deserialize)]
#[serde(rename_all = "camelCase")]
struct Input {
    connection: proxmox::Connection,
    node: String,
    vmid: u32,
    name: String,
    address: String,
    generation: String,
    wait_seconds: u64,
    update: Release,
}

fn hash(value: &str) -> bool {
    value.len() == 64
        && value
            .bytes()
            .all(|b| b.is_ascii_digit() || (b'a'..=b'f').contains(&b))
}

fn system_path(value: &str) -> bool {
    let Some(name) = value.strip_prefix("/nix/store/") else {
        return false;
    };
    let Some((hash, rest)) = name.split_once('-') else {
        return false;
    };
    hash.len() == 32
        && hash
            .bytes()
            .all(|b| b"0123456789abcdfghijklmnpqrsvwxyz".contains(&b))
        && rest.starts_with("nixos-system-")
        && rest.len() > 13
        && rest.len() <= 213
        && rest
            .bytes()
            .all(|b| b.is_ascii_alphanumeric() || b"._+-".contains(&b))
}

fn protected_program(script: &str) -> String {
    // Exit 75 is reserved for flock refusing the lock before starting a child.
    // An activation hook can return 75 too; that must remain an unknown mutation.
    [
        "set +e\n(\n",
        script,
        "\n)\nstatus=$?\n[ \"$status\" -ne 75 ] || exit 1\nexit \"$status\"\n",
    ]
    .concat()
}

pub async fn run(cfg: &ExecutorConfig, ctx: &ExecContext) -> ExecResult {
    let input: Input = ctx.input()?;
    let release = &input.update;
    let url = reqwest::Url::parse(&release.archive_url)
        .map_err(|_| Failure::msg("Invalid OS archive URL"))?;
    if !valid_uuid(&input.generation)
        || !valid_uuid(&release.agent_id)
        || input.name.is_empty()
        || input.address.is_empty()
        || !(1..=1860).contains(&input.wait_seconds)
        || !hash(&release.archive_sha256)
        || !hash(&release.image_sha256)
        || !system_path(&release.system_path)
        || url.scheme() != "https"
        || url.host_str().is_none()
        || !url.username().is_empty()
        || url.password().is_some()
        || url.query().is_some()
        || url.fragment().is_some()
        || release.archive_url.len() > 2048
        || release
            .ca_certificate_pem
            .as_ref()
            .is_some_and(|p| p.len() > 16384 || p.contains("PRIVATE KEY"))
    {
        return Err(Failure::msg("Invalid managed OS update expectations"));
    }
    let client = proxmox::client(cfg, ctx, &input.connection)?;
    let vm = client
        .find_vm(input.vmid)
        .await?
        .ok_or_else(|| Failure::msg("Worker VM is missing"))?;
    if vm.node != input.node
        || vm.name.as_deref() != Some(input.name.as_str())
        || vm.status != "running"
        || vm.kind != octopushy_proxmox::GuestKind::Qemu
    {
        return Err(Failure::msg(
            "The expected worker VM must be running on its bound host",
        ));
    }
    let vm = client.vm_config(&input.node, input.vmid).await?;
    let expected = format!(
        "Managed by Octopushy as {}.\nworker-generation: {}",
        input.address, input.generation
    );
    if vm.template
        || vm.name.as_deref() != Some(input.name.as_str())
        || !vm.tags.iter().any(|t| t == "octopushy")
        || vm.other.get("description").and_then(Value::as_str) != Some(expected.as_str())
    {
        return Err(Failure::msg(
            "Worker ownership/generation does not match; no OS command was sent",
        ));
    }
    let path = format!("/vms/{}", input.vmid);
    let permissions = client.permissions(Some(&path)).await?;
    if !permissions.has(&path, "VM.GuestAgent.Unrestricted")
        && !permissions.has(&path, "VM.Monitor")
    {
        return Err(Failure::msg(
            "Worker OS update requires execution-capable QGA permission",
        ));
    }
    let program = protected_program(include_str!("managed-update.sh"));
    let mut command: Vec<String> = [
        "/run/current-system/sw/bin/flock",
        "-n",
        "-E",
        "75",
        "/run/lock/octopushy-nixos.lock",
        "/run/current-system/sw/bin/timeout",
        "--kill-after=30s",
        if ctx.action == "update-os" {
            "1800s"
        } else {
            "60s"
        },
        "/run/current-system/sw/bin/bash",
        "-c",
        program.as_str(),
        "octopushy-worker-os",
    ]
    .into_iter()
    .map(String::from)
    .collect();
    command.extend([
        ctx.action.clone(),
        release.archive_url.clone(),
        release.archive_sha256.clone(),
        release.system_path.clone(),
        input.generation.clone(),
        release.agent_id.clone(),
        STANDARD.encode(release.ca_certificate_pem.as_deref().unwrap_or("")),
    ]);
    let failure = || {
        if ctx.action == "update-os" {
            Failure::Uncertain("Worker OS update outcome is unknown. Leave it drained and inspect before any retry; no automatic replay.".into())
        } else {
            Failure::msg(
                "Worker OS verification failed; no identity or state-preservation claim can be made",
            )
        }
    };
    ctx.check_cancelled()?;
    let pid = client
        .guest_exec(&input.node, input.vmid, &command, None)
        .await
        .map_err(|e| {
            if matches!(
                e,
                Error::Unauthorized | Error::Forbidden(_) | Error::NotFound(_)
            ) {
                Failure::msg("Proxmox refused the OS command before execution")
            } else {
                failure()
            }
        })?;
    let deadline = Instant::now() + Duration::from_secs(input.wait_seconds);
    let status = loop {
        let status = client
            .guest_exec_status(&input.node, input.vmid, pid)
            .await
            .map_err(|_| failure())?;
        if status.exited {
            break status;
        }
        if Instant::now() >= deadline {
            return Err(failure());
        }
        ctx.sleep(cfg.poll_interval).await.map_err(|_| failure())?;
    };
    if status.exitcode == Some(75) && status.signal.is_none() {
        return Err(Failure::msg(
            "The worker's OS lock is busy; no update started",
        ));
    }
    if status.exitcode != Some(0)
        || status.signal.is_some()
        || status.stdout_truncated
        || status.stderr_truncated
        || status.stdout.len() > 2048
        || status.stderr.len() > 8192
    {
        return Err(failure());
    }
    // Discard guest stderr and all stdout except this strict public evidence.
    let report: Value = serde_json::from_str(&status.stdout).map_err(|_| failure())?;
    if report.as_object().is_none_or(|o| o.len() != 6)
        || report["protocolVersion"] != 1
        || report["generation"] != input.generation
        || report["systemPath"] != release.system_path
        || report["archiveSha256"] != release.archive_sha256
        || report["identityPreserved"] != true
        || report["stateFilesystemPreserved"] != true
    {
        return Err(failure());
    }
    Ok(Success::outputs(report))
}

#[cfg(test)]
mod tests {
    #[test]
    fn only_the_outer_lock_can_return_the_known_busy_status() {
        for (child, expected) in [(0, 0), (1, 1), (75, 1)] {
            let status = std::process::Command::new("bash")
                .args(["-c", &super::protected_program(&format!("exit {child}"))])
                .status()
                .unwrap();
            assert_eq!(status.code(), Some(expected));
        }
    }
}
