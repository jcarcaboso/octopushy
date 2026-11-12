use std::path::PathBuf;

use rustls::pki_types::{CertificateDer, PrivateKeyDer, PrivatePkcs8KeyDer};
use tokio::io::AsyncWriteExt;
use tokio::net::TcpListener;
use tokio_rustls::TlsAcceptor;

use super::*;

fn prober(kind: RuntimeKind) -> Prober {
    Prober::new(Arc::new(ExecutorConfig {
        runtime: ToolRuntime::new(kind, None, "tofu".into(), Some("labnet".into())),
        provider_cache: PathBuf::from("/nonexistent"),
        poll_interval: Duration::from_secs(1),
        proxmox_timeout: Duration::from_secs(5),
        server: Default::default(),
        nix: Default::default(),
    }))
}

fn probe(protocol: &str, host: &str, port: u32, trust: &str, context: &str) -> ReachabilityProbe {
    ReachabilityProbe {
        probe_id: "p-1".into(),
        protocol: protocol.into(),
        host: host.into(),
        port,
        server_name: String::new(),
        tls_trust: trust.into(),
        context: context.into(),
        image: String::new(),
        timeout_seconds: 3,
        ..Default::default()
    }
}

fn http_probe(port: u32, path: &str, tls: bool, trust: &str) -> ReachabilityProbe {
    let mut p = probe("http", "127.0.0.1", port, trust, "agent");
    p.path = path.into();
    p.tls = tls;
    p
}

/// A plain HTTP listener that answers each request with `response`; returns its port and the
/// requests it received.
async fn http_listener(response: &'static [u8]) -> (u32, Arc<std::sync::Mutex<Vec<String>>>) {
    let listener = TcpListener::bind("127.0.0.1:0").await.unwrap();
    let port = u32::from(listener.local_addr().unwrap().port());
    let seen = Arc::new(std::sync::Mutex::new(Vec::new()));
    let log = seen.clone();
    tokio::spawn(async move {
        loop {
            let (mut stream, _) = listener.accept().await.unwrap();
            let log = log.clone();
            tokio::spawn(async move {
                let mut buf = vec![0u8; 2048];
                let n = stream.read(&mut buf).await.unwrap_or(0);
                log.lock()
                    .unwrap()
                    .push(String::from_utf8_lossy(&buf[..n]).into_owned());
                let _ = stream.write_all(response).await;
                let _ = stream.shutdown().await;
            });
        }
    });
    (port, seen)
}

async fn run(p: ReachabilityProbe) -> Outcome {
    prober(RuntimeKind::Local)
        .probe(&p, &CancellationToken::new())
        .await
}

/// A TLS listener with a self-signed certificate for `localhost`; returns its port and leaf.
async fn tls_listener() -> (u32, Vec<u8>) {
    let key = rcgen::KeyPair::generate().unwrap();
    let cert = rcgen::CertificateParams::new(vec!["localhost".to_owned()])
        .unwrap()
        .self_signed(&key)
        .unwrap();
    let leaf = cert.der().to_vec();
    let config = rustls::ServerConfig::builder_with_provider(octopushy_proxmox::tls_provider())
        .with_safe_default_protocol_versions()
        .unwrap()
        .with_no_client_auth()
        .with_single_cert(
            vec![CertificateDer::from(leaf.clone())],
            PrivateKeyDer::Pkcs8(PrivatePkcs8KeyDer::from(key.serialize_der())),
        )
        .unwrap();
    let acceptor = TlsAcceptor::from(Arc::new(config));
    let listener = TcpListener::bind("127.0.0.1:0").await.unwrap();
    let port = u32::from(listener.local_addr().unwrap().port());
    tokio::spawn(async move {
        loop {
            let (stream, _) = listener.accept().await.unwrap();
            let acceptor = acceptor.clone();
            tokio::spawn(async move {
                if let Ok(mut tls) = acceptor.accept(stream).await {
                    let _ = tls.shutdown().await;
                }
            });
        }
    });
    (port, leaf)
}

/// A listener that writes `text` to each connection, then holds it open for a while.
async fn talker(text: &'static [u8]) -> u32 {
    let listener = TcpListener::bind("127.0.0.1:0").await.unwrap();
    let port = u32::from(listener.local_addr().unwrap().port());
    tokio::spawn(async move {
        loop {
            let (mut stream, _) = listener.accept().await.unwrap();
            tokio::spawn(async move {
                let _ = stream.write_all(text).await;
                tokio::time::sleep(Duration::from_secs(30)).await;
            });
        }
    });
    port
}

#[tokio::test]
async fn tls_collect_completes_the_handshake_and_returns_the_chain() {
    let (port, leaf) = tls_listener().await;
    let mut p = probe("tls", "127.0.0.1", port, "collect", "tool");
    p.server_name = "localhost".into();
    let o = run(p).await;
    assert_eq!(o.status, "reachable", "{o:?}");
    assert_eq!(o.certificates, vec![leaf]);
}

#[tokio::test]
async fn tls_system_trust_refuses_an_unknown_certificate() {
    let (port, _) = tls_listener().await;
    let mut p = probe("tls", "127.0.0.1", port, "system", "agent");
    p.server_name = "localhost".into();
    let o = run(p).await;
    assert_eq!(o.status, "tls-untrusted", "{o:?}");
    assert!(o.certificates.is_empty());
}

#[tokio::test]
async fn tls_against_something_else_is_the_wrong_protocol() {
    let port = talker(b"SSH-2.0-OpenSSH_9.6\r\n").await;
    let o = run(probe("tls", "127.0.0.1", port, "collect", "tool")).await;
    assert_eq!(o.status, "protocol", "{o:?}");
}

#[tokio::test]
async fn ssh_banner_after_other_lines_is_reachable() {
    let port = talker(b"Welcome\r\nSSH-2.0-OpenSSH_9.6 Debian\r\n").await;
    let o = run(probe("ssh", "127.0.0.1", port, "", "tool")).await;
    assert_eq!(o.status, "reachable", "{o:?}");
    assert!(o.detail.contains("SSH-2.0 identification"), "{o:?}");
    assert!(
        !o.detail.contains("OpenSSH"),
        "peer software is not echoed: {o:?}"
    );
}

#[tokio::test]
async fn ssh_against_http_is_the_wrong_protocol() {
    let port = talker(b"HTTP/1.1 400 Bad Request\r\n\r\n").await;
    let o = run(probe("ssh", "127.0.0.1", port, "", "tool")).await;
    assert_eq!(o.status, "protocol", "{o:?}");
}

#[tokio::test]
async fn a_silent_service_times_out_within_the_probe_timeout() {
    let port = talker(b"").await;
    let started = Instant::now();
    let o = run(probe("ssh", "127.0.0.1", port, "", "tool")).await;
    assert_eq!(o.status, "timeout", "{o:?}");
    assert!(started.elapsed() < Duration::from_secs(6));
}

#[tokio::test]
async fn refused_and_dns() {
    let listener = TcpListener::bind("127.0.0.1:0").await.unwrap();
    let port = u32::from(listener.local_addr().unwrap().port());
    drop(listener);
    let o = run(probe("ssh", "127.0.0.1", port, "", "tool")).await;
    assert_eq!(o.status, "refused", "{o:?}");
    let o = run(probe(
        "tls",
        "no-such-host.invalid",
        8006,
        "system",
        "agent",
    ))
    .await;
    assert_eq!(o.status, "dns", "{o:?}");
}

#[tokio::test]
async fn ssh_from_the_agent_process_reads_the_banner() {
    let port = talker(b"SSH-2.0-OpenSSH_9.6\r\n").await;
    let o = run(probe("ssh", "127.0.0.1", port, "", "agent")).await;
    assert_eq!(o.status, "reachable", "{o:?}");
    assert_eq!(o.http_status, 0);
}

#[tokio::test]
async fn http_reports_the_status_code_and_sends_only_a_plain_get() {
    let (port, seen) =
        http_listener(b"HTTP/1.1 503 Service Unavailable\r\nContent-Length: 0\r\n\r\n").await;
    let o = run(http_probe(port, "/health?x=1", false, "")).await;
    assert_eq!(o.status, "reachable", "{o:?}");
    assert_eq!(o.http_status, 503);
    assert_eq!(o.detail, "HTTP 503");
    let request = seen.lock().unwrap()[0].clone();
    assert!(
        request.starts_with(&format!(
            "GET /health?x=1 HTTP/1.1\r\nHost: 127.0.0.1:{port}\r\n"
        )),
        "{request}"
    );
    assert!(!request.to_ascii_lowercase().contains("authorization"));
    assert!(!request.to_ascii_lowercase().contains("cookie"));
}

#[tokio::test]
async fn http_over_tls_checks_the_certificate_as_trusted() {
    let (port, _) = tls_listener().await;
    let mut p = http_probe(port, "/", true, "system");
    p.server_name = "localhost".into();
    let o = run(p).await;
    assert_eq!(o.status, "tls-untrusted", "{o:?}");
    assert_eq!(o.http_status, 0);
}

#[tokio::test]
async fn http_against_something_else_is_the_wrong_protocol() {
    let port = talker(b"SSH-2.0-OpenSSH_9.6\r\n").await;
    let o = run(http_probe(port, "/", false, "")).await;
    assert_eq!(o.status, "protocol", "{o:?}");
    let (port, _) = http_listener(b"").await;
    let o = run(http_probe(port, "/", false, "")).await;
    assert_eq!(o.status, "protocol", "{o:?}");
}

#[tokio::test]
async fn http_paths_that_could_change_the_request_are_errors() {
    for path in [
        "",
        "health",
        "/a b",
        "/a\r\nX-Evil: 1",
        &format!("/{}", "a".repeat(MAX_PATH)),
    ] {
        let o = run(http_probe(80, path, false, "")).await;
        assert_eq!(o.status, "error", "{path:?}: {o:?}");
    }
}

#[test]
fn status_lines() {
    assert_eq!(status_line(b"HTTP/1.1 200 OK\r").http_status, 200);
    assert_eq!(status_line(b"HTTP/1.0 404").http_status, 404);
    assert_eq!(status_line(b"HTTP/2 200").status, "protocol");
    assert_eq!(status_line(b"HTTP/1.1 abc").status, "protocol");
    assert_eq!(status_line(b"HTTP/1.1 999 Weird").status, "protocol");
}

#[tokio::test]
async fn malformed_probes_are_errors_and_never_reach_a_command_line() {
    for (host, port, protocol, context) in [
        ("-oProxyCommand=x", 22, "ssh", "tool"),
        ("a b", 22, "ssh", "tool"),
        ("host", 0, "ssh", "tool"),
        ("host", 70000, "ssh", "tool"),
        ("host", 22, "telnet", "tool"),
        ("host", 22, "ssh", "elsewhere"),
    ] {
        let o = run(probe(protocol, host, port, "", context)).await;
        assert_eq!(
            o.status, "error",
            "{host} {port} {protocol} {context}: {o:?}"
        );
    }
    let mut p = probe("ssh", "host", 22, "", "tool");
    p.image = "--privileged".into();
    let o = prober(RuntimeKind::Podman)
        .probe(&p, &CancellationToken::new())
        .await;
    assert_eq!(o.status, "unsupported", "{o:?}");
}

#[test]
fn reports_are_bounded_and_ids_checked() {
    let mut o = Outcome::new("reachable", "x".repeat(5000));
    o.certificates = vec![vec![1u8; 10]; 20];
    let r = report("p-1", o, 5);
    assert_eq!(r.certificates.len(), MAX_CERTIFICATES);
    assert!(r.detail.chars().count() <= MAX_DETAIL);
    let mut o = Outcome::new("reachable", "");
    o.certificates = vec![vec![0u8; MAX_CERTIFICATE_BYTES + 1]];
    let r = report("p-1", o, 5);
    assert_eq!(r.status, "error");
    assert!(r.certificates.is_empty());
    assert_eq!(report("bad id;", Outcome::new("error", ""), 0).probe_id, "");
}

#[test]
fn credentials_in_urls_are_redacted() {
    assert_eq!(
        redact("proxy http://user:pa55@proxy.lan:3128/ refused"),
        "proxy http://***@proxy.lan:3128/ refused"
    );
    assert_eq!(redact("https://registry.lan/x"), "https://registry.lan/x");
}

#[test]
fn relay_words_decide_over_what_the_protocol_saw() {
    let eof = || Outcome::new("protocol", "the connection closed during the TLS handshake");
    let lines = |l: &[&str]| l.iter().map(|s| s.to_string()).collect::<Vec<_>>();
    let cases: &[(&[&str], Option<i32>, &str)] = &[
        (
            &["octopushy-probe: proxy HTTPS_PROXY"],
            Some(97),
            "unsupported",
        ),
        (&["octopushy-probe: no-relay"], Some(98), "unsupported"),
        (&["octopushy-probe: error dns"], Some(99), "dns"),
        (&["octopushy-probe: error refused"], Some(99), "refused"),
        (&["octopushy-probe: error timeout"], Some(99), "timeout"),
        (
            &["octopushy-probe: error unreachable"],
            Some(99),
            "unreachable",
        ),
        (&["nc: bad address 'pve.lan'"], Some(1), "dns"),
        (
            &["nc: can't connect to remote host (10.0.0.1): Connection refused"],
            Some(1),
            "refused",
        ),
        (
            &["nc: can't connect to remote host (10.0.0.1): Connection timed out"],
            Some(1),
            "timeout",
        ),
        (
            &["nc: can't connect to remote host (10.0.0.1): No route to host"],
            Some(1),
            "unreachable",
        ),
        (
            &[
                "Error: crun: executable file `/bin/sh` not found in $PATH: No such file or directory",
            ],
            Some(127),
            "unsupported",
        ),
        (
            &["Error: initializing source docker://x: pinging registry"],
            Some(125),
            "error",
        ),
        // busybox nc refusing or timing out: no word, exit 1. Never mistaken for another protocol.
        (&[], Some(1), "error"),
        // Connected, then the peer spoke something else: the protocol's verdict stands.
        (&["octopushy-probe: connected"], Some(0), "protocol"),
    ];
    for (stderr, code, status) in cases {
        let o = classify_relay(eof(), *code, &lines(stderr), "img");
        assert_eq!(o.status, *status, "{stderr:?}: {o:?}");
    }
    let o = classify_relay(
        eof(),
        Some(125),
        &lines(&["Error: proxy http://u:secret@p:3128 failed"]),
        "img",
    );
    assert!(!o.detail.contains("secret"), "{o:?}");
    let ok = Outcome::new("reachable", "TLS handshake completed");
    assert_eq!(
        classify_relay(ok, Some(0), &lines(&["nc: whatever"]), "img").status,
        "reachable"
    );
}

#[tokio::test]
async fn relay_containers_get_the_tool_flags_and_nothing_of_the_agent() {
    let runtime = ToolRuntime::new(
        RuntimeKind::Podman,
        None,
        "tofu".into(),
        Some("labnet".into()),
    );
    let spec = spec(&ReachabilityProbe {
        image: "localhost/octopushy-ansible:1".into(),
        ..probe("ssh", "10.0.0.5", 22, "", "tool")
    })
    .unwrap();
    let cmd = relay_command(&runtime, &spec, "octopushy-probe-p-1").await;
    let args: Vec<String> = cmd
        .as_std()
        .get_args()
        .map(|a| a.to_string_lossy().into_owned())
        .collect();
    let joined = args.join(" ");
    for want in [
        "run --rm -i",
        "--cap-drop=all",
        "--security-opt=no-new-privileges",
        "--read-only",
        "--userns=keep-id",
        "--network labnet",
        "--log-driver=none",
        "--label io.octopushy.probe=true",
        "--entrypoint /bin/sh localhost/octopushy-ansible:1 -c",
    ] {
        assert!(joined.contains(want), "{want}: {joined}");
    }
    assert!(
        !args
            .iter()
            .any(|a| a == "-v" || a == "-e" || a.starts_with("--network=host")),
        "{joined}"
    );
    assert_eq!(&args[args.len() - 2..], ["10.0.0.5", "22"]);
}

/// A runtime whose `run` never answers: the probe still ends on time or on cancel, and the relay
/// container is killed by name, never left behind.
#[tokio::test]
async fn a_stuck_relay_is_killed_on_timeout_and_on_cancel() {
    use std::os::unix::fs::PermissionsExt;
    let dir = tempfile::tempdir().unwrap();
    let binary = dir.path().join("fake-runtime");
    let kills = dir.path().join("kills");
    std::fs::write(
        &binary,
        format!(
            "#!/bin/sh\nif [ \"$1\" = kill ]; then echo \"$2\" >>{}; exit 0; fi\nexec sleep 60\n",
            kills.display()
        ),
    )
    .unwrap();
    std::fs::set_permissions(&binary, std::fs::Permissions::from_mode(0o755)).unwrap();
    let mut runtime = ToolRuntime::new(RuntimeKind::Podman, None, "tofu".into(), None);
    runtime.container_binary = binary;
    runtime.kill_grace = Duration::from_secs(2);
    let prober = Prober::new(Arc::new(ExecutorConfig {
        runtime,
        provider_cache: PathBuf::from("/nonexistent"),
        poll_interval: Duration::from_secs(1),
        proxmox_timeout: Duration::from_secs(5),
        server: Default::default(),
        nix: Default::default(),
    }));
    let mut p = probe("ssh", "10.0.0.5", 22, "", "tool");
    p.image = "localhost/octopushy-ansible:1".into();
    p.timeout_seconds = 1;

    let started = Instant::now();
    let outcome = prober.probe(&p, &CancellationToken::new()).await;
    assert_eq!(outcome.status, "timeout", "{}", outcome.detail);
    assert!(started.elapsed() < Duration::from_secs(10));

    p.probe_id = "p-2".into();
    p.timeout_seconds = 60;
    let cancel = CancellationToken::new();
    let stop = cancel.clone();
    tokio::spawn(async move {
        tokio::time::sleep(Duration::from_millis(300)).await;
        stop.cancel();
    });
    let started = Instant::now();
    prober.probe(&p, &cancel).await;
    assert!(started.elapsed() < Duration::from_secs(10));

    assert_eq!(
        std::fs::read_to_string(&kills).unwrap(),
        "octopushy-probe-p-1\noctopushy-probe-p-2\n"
    );
}

/// A docker that does not answer `info`, asked before the relay starts: the probe still ends on
/// time and on cancel.
#[tokio::test]
async fn a_runtime_that_does_not_answer_before_the_relay_starts_holds_no_probe() {
    use std::os::unix::fs::PermissionsExt;
    let dir = tempfile::tempdir().unwrap();
    let binary = dir.path().join("fake-docker");
    let pids = dir.path().join("pids");
    std::fs::write(
        &binary,
        format!("#!/bin/sh\necho $$ >>{}\nexec sleep 30\n", pids.display()),
    )
    .unwrap();
    std::fs::set_permissions(&binary, std::fs::Permissions::from_mode(0o755)).unwrap();
    let mut runtime = ToolRuntime::new(RuntimeKind::Docker, None, "tofu".into(), None);
    runtime.container_binary = binary;
    let prober = Prober::new(Arc::new(ExecutorConfig {
        runtime,
        provider_cache: PathBuf::from("/nonexistent"),
        poll_interval: Duration::from_secs(1),
        proxmox_timeout: Duration::from_secs(5),
        server: Default::default(),
        nix: Default::default(),
    }));
    let mut p = probe("ssh", "10.0.0.5", 22, "", "tool");
    p.image = "localhost/octopushy-ansible:1".into();
    p.timeout_seconds = 1;

    let started = Instant::now();
    let outcome = prober.probe(&p, &CancellationToken::new()).await;
    assert_eq!(outcome.status, "timeout", "{}", outcome.detail);
    assert!(started.elapsed() < Duration::from_secs(5));

    p.timeout_seconds = 60;
    let cancel = CancellationToken::new();
    cancel.cancel();
    let started = Instant::now();
    prober.probe(&p, &cancel).await;
    assert!(started.elapsed() < Duration::from_secs(5));

    // Nothing it started is left running.
    tokio::time::sleep(Duration::from_millis(500)).await;
    let pids = std::fs::read_to_string(&pids).unwrap();
    assert!(!pids.is_empty());
    for pid in pids.lines() {
        let pid: libc::pid_t = pid.parse().unwrap();
        // SAFETY: signal 0 only checks that the process exists.
        let alive = unsafe { libc::kill(pid, 0) } == 0;
        assert!(!alive, "{pid} is still running");
    }
}
