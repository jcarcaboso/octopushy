//! Bounded, idempotent reporter installation through QEMU guest exec.
//! The shell program travels on stdin, never in the guest's command arguments.

use std::time::Duration;

use base64::Engine;
use octopushy_proxmox::{Error as PveError, GuestKind};
use serde::Deserialize;
use serde_json::json;

use super::{ExecContext, ExecResult, ExecutorConfig, Failure, Success, proxmox};

const INSTALLER: &str = include_str!("../../../../packaging/disk-reporter/install.sh");
const SERVICE: &str =
    include_str!("../../../../packaging/disk-reporter/octopushy-disk-reporter.service");
const TIMER: &str =
    include_str!("../../../../packaging/disk-reporter/octopushy-disk-reporter.timer");
const TOKEN: &str = "disk-reporter.token";

#[derive(Deserialize)]
#[serde(rename_all = "camelCase")]
struct Input {
    connection: proxmox::Connection,
    node: String,
    vmid: u32,
    name: String,
    reporter: Reporter,
    address: Option<String>,
    generation: Option<String>,
}

#[derive(Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct Reporter {
    #[serde(default = "fhs_mode")]
    installation_mode: String,
    image_sha256: Option<String>,
    endpoint: String,
    #[serde(default)]
    binary_url: String,
    #[serde(default)]
    binary_sha256: String,
    interval_seconds: u32,
    ca_certificate_pem: Option<String>,
}

fn fhs_mode() -> String {
    "fhs-download-v1".into()
}

pub async fn run(cfg: &ExecutorConfig, ctx: &ExecContext) -> ExecResult {
    if ctx.action != "install-disk-reporter" {
        return Err(Failure::msg(
            "unknown guest action; expected install-disk-reporter",
        ));
    }
    let input: Input = ctx.input()?;
    let native = match input.reporter.installation_mode.as_str() {
        "managed-nixos-v1" => true,
        "fhs-download-v1" if input.generation.is_none() => false,
        _ => {
            return Err(Failure::msg(
                "unsupported reporter installation mode; no FHS fallback for managed workers",
            ));
        }
    };
    let (command, payload) = if native {
        if input
            .reporter
            .image_sha256
            .as_deref()
            .is_none_or(|v| !hex64(v))
            || input.address.as_deref().is_none_or(str::is_empty)
            || !input.reporter.binary_url.is_empty()
            || !input.reporter.binary_sha256.is_empty()
        {
            return Err(Failure::msg(
                "missing native reporter provenance/ownership expectations",
            ));
        }
        let config = crate::managed_reporting::Configuration {
            protocol_version: 1,
            generation: input.generation.clone().unwrap_or_default(),
            endpoint: input.reporter.endpoint.clone(),
            interval_seconds: input.reporter.interval_seconds,
            ca_certificate_pem: input.reporter.ca_certificate_pem.clone(),
            token: ctx.secrets.get(TOKEN)?.to_owned(),
        };
        config
            .validate()
            .map_err(|_| Failure::msg("invalid native reporter configuration"))?;
        let payload = serde_json::to_string(&config)
            .map_err(|_| Failure::msg("invalid native reporter configuration"))?;
        if payload.len() as u64 > crate::managed_reporting::LIMIT {
            return Err(Failure::msg(
                "native reporter configuration exceeds the stdin limit",
            ));
        }
        (
            vec!["/run/current-system/sw/bin/octopushy-worker-reporting".to_owned()],
            payload,
        )
    } else {
        (
            ["/usr/bin/timeout", "--kill-after=5s", "240s", "/bin/sh"]
                .map(String::from)
                .to_vec(),
            installation_script(&input.reporter, ctx.secrets.get(TOKEN)?)?,
        )
    };
    let client = proxmox::client(cfg, ctx, &input.connection)?;
    let vm = client
        .find_vm(input.vmid)
        .await?
        .ok_or_else(|| Failure::msg("the target VM was not found"))?;
    if vm.kind != GuestKind::Qemu
        || vm.template
        || vm.node != input.node
        || vm.name.as_deref() != Some(&input.name)
        || vm.status != "running"
    {
        return Err(Failure::msg(
            "refusing guest setup: VM identity, placement, or running state changed",
        ));
    }

    if native {
        let config = client.vm_config(&input.node, input.vmid).await?;
        let expected = format!(
            "Managed by Octopushy as {}.\nworker-generation: {}",
            input.address.as_deref().unwrap(),
            input.generation.as_deref().unwrap(),
        );
        if config.name.as_deref() != Some(&input.name)
            || config.template
            || !config.tags.iter().any(|tag| tag == "octopushy")
            || config
                .other
                .get("description")
                .and_then(serde_json::Value::as_str)
                != Some(&expected)
        {
            return Err(Failure::msg(
                "VM ownership/generation does not match; no guest command was sent",
            ));
        }
    }

    // Proxmox 9 uses the dedicated privilege; older releases require VM.Monitor.
    let path = format!("/vms/{}", input.vmid);
    let permissions = client.permissions(Some(&path)).await?;
    if !permissions.has(&path, "VM.GuestAgent.Unrestricted")
        && !permissions.has(&path, "VM.Monitor")
    {
        return Err(Failure::msg(
            "reporter installation requires VM.GuestAgent.Unrestricted (VM.Monitor on older Proxmox)",
        ));
    }
    ctx.check_cancelled()?;
    ctx.log
        .agent("configuring the disk reporter through the QEMU guest agent");
    // Even if the execution agent disappears, the guest command cannot run forever.
    let pid = client
        .guest_exec(&input.node, input.vmid, &command, Some(&payload))
        .await
        .map_err(|error| match error {
            PveError::Unauthorized | PveError::Forbidden(_) | PveError::NotFound(_) => {
                Failure::msg("Proxmox refused guest execution; check the VM and guest-agent privileges")
            }
            _ => Failure::Uncertain("guest execution could not be confirmed; installation may have started. Wait at least 245s before recovery.".into()),
        })?;
    let completion = async {
        loop {
            let status = client
                .guest_exec_status(&input.node, input.vmid, pid)
                .await
                .map_err(|_| Failure::Uncertain("lost guest execution status; installation may still be running. Wait at least 245s before recovery.".into()))?;
            if status.exited {
                if status.exitcode == Some(0) && status.signal.is_none() {
                    if native {
                        return Ok(Success::outputs(json!({
                            "installed": true, "firstReportSent": true,
                            "installationMode": input.reporter.installation_mode,
                            "imageSha256": input.reporter.image_sha256,
                            "intervalSeconds": input.reporter.interval_seconds,
                        })));
                    }
                    return Ok(Success::outputs(json!({
                        "installed": true,
                        "firstReportSent": true,
                        "binarySha256": input.reporter.binary_sha256,
                        "intervalSeconds": input.reporter.interval_seconds,
                    })));
                }
                if native
                    && (status.signal.is_some()
                        || status.exitcode.is_none()
                        || matches!(status.exitcode, Some(124 | 137)))
                {
                    return Err(Failure::Uncertain("native helper timed out or was interrupted; inspect the guest before recovery".into()));
                }
                // Never forward guest stdout/stderr: a failing command may echo a
                // secret or its encoded form, which the normal masker cannot cover.
                return Err(Failure::msg(format!(
                    "reporter installation failed (exit {:?}, signal {:?}); check guest prerequisites and journalctl -u octopushy-disk-reporter.service",
                    status.exitcode, status.signal
                )));
            }
            ctx.sleep(cfg.poll_interval).await.map_err(|_| {
                Failure::Uncertain(
                    "guest setup interrupted; inspect the guest before recovery".into(),
                )
            })?;
        }
    };
    tokio::time::timeout(Duration::from_secs(270), completion)
        .await
        .unwrap_or_else(|_| {
            Err(Failure::Uncertain(
                "timed out waiting for guest setup; inspect the guest before recovery".into(),
            ))
        })
}

fn installation_script(reporter: &Reporter, token: &str) -> Result<String, Failure> {
    for address in [&reporter.endpoint, &reporter.binary_url] {
        let url = url::Url::parse(address).map_err(|_| Failure::msg("invalid reporter URL"))?;
        if url.scheme() != "https"
            || url.host_str().is_none()
            || !url.username().is_empty()
            || url.password().is_some()
            || url.query().is_some()
            || url.fragment().is_some()
        {
            return Err(Failure::msg(
                "reporter URLs must use HTTPS without credentials, query, or fragment",
            ));
        }
    }
    if !(60..=604800).contains(&reporter.interval_seconds)
        || !hex64(&reporter.binary_sha256)
        || !hex64(token)
    {
        return Err(Failure::msg(
            "invalid reporter checksum, credential, or interval",
        ));
    }
    if reporter
        .ca_certificate_pem
        .as_ref()
        .is_some_and(|pem| pem.len() > 16384 || pem.contains("PRIVATE KEY"))
    {
        return Err(Failure::msg(
            "only a public CA certificate of at most 16 KiB is accepted",
        ));
    }
    let mut config = json!({
        "endpoint": reporter.endpoint,
        "intervalSeconds": reporter.interval_seconds,
    });
    if reporter.ca_certificate_pem.is_some() {
        config["caCertificateFile"] = "/etc/octopushy-disk-reporter/ca.pem".into();
    }
    let mut script = String::from(
        "set -eu\nexport PATH=/usr/sbin:/usr/bin:/sbin:/bin\nunset DESTDIR CREDENTIALS_DIRECTORY\numask 077\n\
         test \"$(id -u)\" = 0\ncommand -v curl >/dev/null\ncommand -v systemctl >/dev/null\n\
         exec 9>/run/octopushy-disk-reporter-install.lock\nflock -w 10 9\n\
         stage=$(mktemp -d /tmp/octopushy-disk-reporter.XXXXXXXX)\n\
         trap 'rm -rf \"$stage\"' EXIT HUP INT TERM\ncd \"$stage\"\n",
    );
    for (name, contents) in [
        ("install.sh", INSTALLER),
        ("octopushy-disk-reporter.service", SERVICE),
        ("octopushy-disk-reporter.timer", TIMER),
        ("config.json", &config.to_string()),
        ("token", token),
        ("binary-url", &reporter.binary_url),
    ] {
        write_file(&mut script, name, contents);
    }
    if let Some(ca) = &reporter.ca_certificate_pem {
        write_file(&mut script, "ca.pem", ca);
        script.push_str("export CURL_CA_BUNDLE=\"$stage/ca.pem\"\n");
    }
    script.push_str(
        "curl --disable --fail --silent --show-error --proto '=https' --connect-timeout 10 --max-time 60 \
         --max-filesize 16777216 --output octopushy-disk-reporter \"$(cat binary-url)\"\n",
    );
    // Digest is validated hex; no assignment-controlled text is interpolated as shell code.
    script.push_str(&format!(
        "printf '%s\\n' '{}  octopushy-disk-reporter' | sha256sum --check --status\n",
        reporter.binary_sha256
    ));
    script.push_str("chmod 0755 octopushy-disk-reporter\n");
    if reporter.ca_certificate_pem.is_some() {
        script.push_str(
            "install -d -m 0755 /etc/octopushy-disk-reporter\n\
             install -m 0644 ca.pem /etc/octopushy-disk-reporter/ca.pem\n",
        );
    }
    script.push_str(
        "sh ./install.sh ./octopushy-disk-reporter ./config.json ./token\n\
         systemctl start octopushy-disk-reporter.service\n",
    );
    if script.len() > 64 * 1024 {
        return Err(Failure::msg(
            "reporter installation input exceeds the Proxmox 64 KiB limit",
        ));
    }
    Ok(script)
}

fn hex64(value: &str) -> bool {
    value.len() == 64 && value.bytes().all(|b| b.is_ascii_hexdigit())
}

fn write_file(script: &mut String, name: &str, contents: &str) {
    // `name` is always one of the fixed literals above; data is never shell code.
    let data = base64::engine::general_purpose::STANDARD.encode(contents);
    script.push_str(&format!(
        "printf '%s' '{data}' | base64 --decode > '{name}'\n"
    ));
}

#[cfg(test)]
mod tests {
    use super::*;

    fn reporter() -> Reporter {
        Reporter {
            installation_mode: fhs_mode(),
            image_sha256: None,
            endpoint: "https://octopushy.test/api/guests/id/disk-usage".into(),
            binary_url: "https://releases.test/reporter".into(),
            binary_sha256: "a".repeat(64),
            interval_seconds: 3600,
            ca_certificate_pem: None,
        }
    }

    #[test]
    fn script_is_bounded_pinned_and_keeps_credentials_out_of_shell_arguments() {
        let mut config = reporter();
        let token = "b".repeat(64);
        let script = installation_script(&config, &token).unwrap();
        assert!(!script.contains(&token));
        assert!(script.len() < 64 * 1024);
        assert!(script.contains("sha256sum --check --status"));
        assert!(script.contains("--max-time 60"));
        assert!(script.find("sha256sum").unwrap() < script.find("sh ./install.sh").unwrap());
        assert!(script.ends_with("systemctl start octopushy-disk-reporter.service\n"));
        config.binary_url = "https://releases.test/$(touch%20/tmp/pwned)'".into();
        let script = installation_script(&config, &token).unwrap();
        assert!(!script.contains("$(touch"));
        let mut child = std::process::Command::new("sh")
            .arg("-n")
            .stdin(std::process::Stdio::piped())
            .spawn()
            .unwrap();
        use std::io::Write;
        child
            .stdin
            .take()
            .unwrap()
            .write_all(script.as_bytes())
            .unwrap();
        assert!(child.wait().unwrap().success());
    }

    #[test]
    fn unsafe_urls_credentials_digests_and_intervals_are_rejected() {
        let token = "b".repeat(64);
        for url in [
            "http://releases.test/reporter",
            "https://user:secret@test/reporter",
            "https://test/reporter?secret=x",
        ] {
            let mut config = reporter();
            config.binary_url = url.into();
            assert!(installation_script(&config, &token).is_err());
        }
        let mut config = reporter();
        config.binary_sha256 = "$(touch /tmp/pwned)".into();
        assert!(installation_script(&config, &token).is_err());
        let mut config = reporter();
        config.interval_seconds = 0;
        assert!(installation_script(&config, &token).is_err());
        assert!(installation_script(&reporter(), "not-a-token").is_err());
    }
}
