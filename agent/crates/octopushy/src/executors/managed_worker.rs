//! Typed QGA bootstrap only: never runs repository commands or shell payloads.
use super::{ExecContext, ExecResult, ExecutorConfig, Failure, Success, proxmox};
use crate::managed_identity::valid_uuid;
use base64::{Engine, engine::general_purpose::STANDARD};
use serde::Deserialize;
use serde_json::Value;
use std::time::{Duration, Instant};

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
    identity: Option<Value>,
}
pub async fn run(cfg: &ExecutorConfig, ctx: &ExecContext) -> ExecResult {
    if !matches!(ctx.action.as_str(), "prepare" | "install") {
        return Err(Failure::msg(
            "managed-worker supports only prepare and install",
        ));
    }
    let input: Input = ctx.input()?;
    if !valid_uuid(&input.generation)
        || input.address.is_empty()
        || input.name.is_empty()
        || !(1..=300).contains(&input.wait_seconds)
    {
        return Err(Failure::msg("Invalid managed worker identity expectations"));
    }
    let mut command = vec![
        "/run/current-system/sw/bin/octopushy-worker-bootstrap".into(),
        ctx.action.clone(),
        "--generation".into(),
        input.generation.clone(),
    ];
    if ctx.action == "install" {
        let payload = input
            .identity
            .as_ref()
            .ok_or_else(|| Failure::msg("Missing public identity"))?;
        if payload.get("generation").and_then(Value::as_str) != Some(&input.generation) {
            return Err(Failure::msg("Identity generation mismatch"));
        }
        let encoded = payload.to_string();
        if encoded.len() > 32768 {
            return Err(Failure::msg("Public identity payload is too large"));
        }
        command.extend(["--payload-base64".into(), STANDARD.encode(encoded)]);
    } else if input.identity.is_some() {
        return Err(Failure::msg("Prepare does not accept an identity payload"));
    }
    let client = proxmox::client(cfg, ctx, &input.connection)?;
    let vm = client.vm_config(&input.node, input.vmid).await?;
    let expected = format!(
        "Managed by Octopushy as {}.\nworker-generation: {}",
        input.address, input.generation
    );
    if vm.name.as_deref() != Some(&input.name)
        || vm.template
        || !vm.tags.iter().any(|t| t == "octopushy")
        || vm.other.get("description").and_then(Value::as_str) != Some(&expected)
    {
        return Err(Failure::msg(
            "VM ownership/generation does not match; no guest command was sent",
        ));
    }
    ctx.check_cancelled()?;
    let unknown = || {
        Failure::Uncertain("Managed identity command outcome unknown; inspect before retrying (never automatically replay).".into())
    };
    let pid = client
        .guest_exec(&input.node, input.vmid, &command)
        .await
        .map_err(|_| unknown())?;
    let deadline = Instant::now() + Duration::from_secs(input.wait_seconds);
    let status = loop {
        let status = client
            .guest_exec_status(&input.node, input.vmid, pid)
            .await
            .map_err(|_| unknown())?;
        if status.exited {
            break status;
        }
        if Instant::now() >= deadline {
            return Err(unknown());
        }
        ctx.sleep(cfg.poll_interval).await.map_err(|_| unknown())?;
    };
    if status.stdout_truncated
        || status.stderr_truncated
        || status.stdout.len() > 32768
        || status.stderr.len() > 8192
        || status.signal.is_some()
    {
        return Err(unknown());
    }
    // Never echo arbitrary helper stderr/stdout into logs or result errors.
    if status.exitcode != Some(0) {
        return Err(Failure::msg(
            "Managed identity helper refused the operation; inspect guest state.",
        ));
    }
    let report: Value = serde_json::from_str(&status.stdout).map_err(|_| unknown())?;
    let keys: &[&str] = if ctx.action == "prepare" {
        &["protocolVersion", "generation", "csrDer", "agentVersion"]
    } else {
        &["protocolVersion", "generation", "agentId", "installed"]
    };
    let object = report.as_object().ok_or_else(unknown)?;
    if object.len() != keys.len()
        || !keys.iter().all(|k| object.contains_key(*k))
        || report["protocolVersion"] != 1
        || report["generation"] != input.generation
    {
        return Err(unknown());
    }
    if ctx.action == "prepare" {
        let csr = report["csrDer"].as_str().ok_or_else(unknown)?;
        if !(1..=16384).contains(&csr.len())
            || STANDARD.decode(csr).is_err()
            || report["agentVersion"]
                .as_str()
                .is_none_or(|v| v.is_empty() || v.len() > 128)
        {
            return Err(unknown());
        }
    } else if report["installed"] != true
        || report["agentId"] != input.identity.as_ref().unwrap()["agentId"]
    {
        return Err(unknown());
    }
    Ok(Success::outputs(report))
}
