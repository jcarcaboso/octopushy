//! Reachability probes through a real container runtime. Ignored by default (needs rootless
//! podman or docker and pulls small public images); run with:
//!
//! ```sh
//! cargo test -p octopushy --test reachability_containers -- --ignored --nocapture
//! PROBE_RUNTIME=docker cargo test -p octopushy --test reachability_containers -- --ignored --nocapture
//! ```
//!
//! A target answers with an SSH identification on a private container network, under a name only
//! that network resolves. The tool context of an agent on that network reaches it; the agent's
//! own namespace, and a container on the runtime's default network, do not. One test: it changes
//! the process environment, which the container runtime reads.

use std::path::PathBuf;
use std::process::Command;
use std::sync::Arc;
use std::time::Duration;

use octopushy::executors::ExecutorConfig;
use octopushy::reachability::Prober;
use octopushy::tools::{RuntimeKind, ToolRuntime};
use octopushy_proto::v1::ReachabilityProbe;
use tokio_util::sync::CancellationToken;

const PYTHON: &str = "docker.io/library/python:3-alpine";
const BUSYBOX_NC: &str = "ghcr.io/opentofu/opentofu:1.13.2";
const NO_RELAY: &str = "docker.io/library/debian:trixie-slim";
const SSH_TARGET: &str = r#"
import socket
s = socket.socket(); s.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
s.bind(("0.0.0.0", 2222)); s.listen(8)
while True:
    c, _ = s.accept(); c.sendall(b"SSH-2.0-OpenSSH_9.6\r\n"); c.close()
"#;

fn runtime_kind() -> (RuntimeKind, &'static str) {
    match std::env::var("PROBE_RUNTIME").as_deref() {
        Ok("docker") => (RuntimeKind::Docker, "docker"),
        _ => (RuntimeKind::Podman, "podman"),
    }
}

fn sh(bin: &str, args: &[&str]) -> bool {
    Command::new(bin)
        .args(args)
        .stdout(std::process::Stdio::null())
        .status()
        .map(|s| s.success())
        .unwrap_or(false)
}

fn prober(kind: RuntimeKind, network: Option<&str>) -> Prober {
    Prober::new(Arc::new(ExecutorConfig {
        runtime: ToolRuntime::new(kind, None, "tofu".into(), network.map(str::to_owned)),
        provider_cache: PathBuf::from("/nonexistent"),
        poll_interval: Duration::from_secs(1),
        proxmox_timeout: Duration::from_secs(5),
        server: Default::default(),
        nix: Default::default(),
    }))
}

fn ssh_probe(host: &str, image: &str) -> ReachabilityProbe {
    ReachabilityProbe {
        probe_id: format!("t-{}", fastrand::u32(..)),
        protocol: "ssh".into(),
        host: host.into(),
        port: 2222,
        server_name: String::new(),
        tls_trust: String::new(),
        context: "tool".into(),
        image: image.into(),
        timeout_seconds: 120,
    }
}

struct Cleanup(&'static str, String, String);

impl Drop for Cleanup {
    fn drop(&mut self) {
        sh(self.0, &["rm", "-f", &self.1]);
        sh(self.0, &["network", "rm", &self.2]);
    }
}

#[tokio::test]
#[ignore = "needs rootless podman or docker and pulls public images"]
async fn tool_container_context_is_probed_from_its_own_network() {
    let (kind, bin) = runtime_kind();
    let suffix = fastrand::u32(..);
    let network = format!("octopushy-probe-net-{suffix}");
    let target = format!("octopushy-probe-target-{suffix}");
    for image in [PYTHON, BUSYBOX_NC, NO_RELAY] {
        assert!(
            sh(bin, &["image", "inspect", image]) || sh(bin, &["pull", "-q", image]),
            "pulling {image}"
        );
    }
    assert!(sh(bin, &["network", "create", &network]));
    let _cleanup = Cleanup(bin, target.clone(), network.clone());
    assert!(sh(
        bin,
        &[
            "run",
            "-d",
            "--rm",
            "--network",
            &network,
            "--name",
            &target,
            PYTHON,
            "python3",
            "-c",
            SSH_TARGET
        ]
    ));

    let cancel = CancellationToken::new();
    let on_network = prober(kind, Some(&network));
    // The target needs a moment to listen.
    let mut first = None;
    for _ in 0..20 {
        let o = on_network.probe(&ssh_probe(&target, PYTHON), &cancel).await;
        if o.status == "reachable" {
            first = Some(o);
            break;
        }
        tokio::time::sleep(Duration::from_millis(500)).await;
    }
    let first = first.expect("the python relay reached the target");
    println!("python relay: {first:?}");

    let o = on_network
        .probe(&ssh_probe(&target, BUSYBOX_NC), &cancel)
        .await;
    assert_eq!(o.status, "reachable", "busybox nc relay: {o:?}");

    let o = on_network
        .probe(&ssh_probe(&target, NO_RELAY), &cancel)
        .await;
    assert_eq!(o.status, "unsupported", "an image with no relay: {o:?}");

    // Nothing else counts for this context: the agent's own namespace (the local runtime) and a
    // container on the runtime's default network cannot even resolve the name.
    let o = prober(RuntimeKind::Local, None)
        .probe(&ssh_probe(&target, PYTHON), &cancel)
        .await;
    assert_ne!(o.status, "reachable", "host namespace: {o:?}");
    let o = prober(kind, None)
        .probe(&ssh_probe(&target, PYTHON), &cancel)
        .await;
    assert_ne!(o.status, "reachable", "default network: {o:?}");

    // A wrong port on the right network is refused, not reachable.
    let mut wrong = ssh_probe(&target, PYTHON);
    wrong.port = 2223;
    let o = on_network.probe(&wrong, &cancel).await;
    assert_eq!(o.status, "refused", "{o:?}");
    // busybox nc says nothing when refused: never reachable, never taken for another protocol.
    let mut wrong = ssh_probe(&target, BUSYBOX_NC);
    wrong.port = 2223;
    let o = on_network.probe(&wrong, &cancel).await;
    assert_eq!(o.status, "error", "{o:?}");

    // Podman copies proxy variables from its own environment into the container: the relay
    // sees one and refuses, rather than vouch for a connection the tool would proxy.
    if kind == RuntimeKind::Podman {
        // SAFETY: this binary has one test, and nothing else reads the environment now.
        unsafe { std::env::set_var("HTTPS_PROXY", "http://user:secret@proxy.invalid:3128") };
        let o = on_network.probe(&ssh_probe(&target, PYTHON), &cancel).await;
        // SAFETY: as above.
        unsafe { std::env::remove_var("HTTPS_PROXY") };
        assert_eq!(o.status, "unsupported", "{o:?}");
        assert!(!o.detail.contains("secret"), "{o:?}");
    }
}
