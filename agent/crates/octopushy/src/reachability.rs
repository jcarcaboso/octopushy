//! Reachability probes: whether this agent can open a connection to one endpoint, from the
//! context a step would connect from. The server asks, for endpoints of trusted definitions
//! and queued work only, and decides which agent gets the work; the agent only answers.
//!
//! A probe holds no credential. A TLS probe completes a handshake, still checking that the
//! handshake is signed by the presented key, and either verifies the certificate with the
//! agent's trust store (`system`) or hands the presented chain back for the server to judge
//! against the configured CA or for consistency (`collect`). An SSH probe reads the server's
//! identification line and nothing else: it does not check the host key, which the step's
//! own connection still pins.
//!
//! Contexts, matched to how the step connects:
//! - `agent`: the agent process, as the `proxmox` executor's reqwest client. If the process
//!   environment would send the endpoint through a proxy, the answer is `unsupported`.
//! - `tool` with the `local` runtime: tools run with a cleared environment in the agent's own
//!   network namespace, so the agent connects directly.
//! - `tool` with `podman` or `docker`: a container of the step's image, started with the tool
//!   containers' flags and network and nothing mounted, relays one TCP connection over its
//!   stdin and stdout (with `python3` or `nc` from the image); the agent speaks the protocol
//!   through it. A proxy variable in the container is `unsupported`.
//!
//! See docs/worker-routing.md.

use std::process::Stdio;
use std::sync::Arc;
use std::sync::atomic::{AtomicUsize, Ordering};
use std::time::{Duration, Instant};

use octopushy_proto::v1::{
    AgentMessage, ReachabilityProbe, ReachabilityReport, agent_message::Kind as Out,
};
use octopushy_proxmox::{AcceptAnyCertificate, Tls};
use rustls::ClientConfig;
use rustls::pki_types::ServerName;
use tokio::io::{AsyncRead, AsyncReadExt, AsyncWrite};
use tokio::net::TcpStream;
use tokio::sync::{Semaphore, mpsc};
use tokio_rustls::TlsConnector;
use tokio_util::sync::CancellationToken;

use crate::executors::ExecutorConfig;
use crate::tools::{RuntimeKind, ToolRuntime};

/// Announced in `Hello` by agents that answer probes.
pub const CAPABILITY: &str = "reachability-v1";

/// Probes running at once.
const CONCURRENT: usize = 4;
/// Probes running or waiting; more are answered `error` at once.
const QUEUED: usize = 32;
/// The longest a probe may take, whatever the server asks.
const MAX_TIMEOUT: Duration = Duration::from_secs(120);
const DEFAULT_TIMEOUT: Duration = Duration::from_secs(15);
/// How long a connected peer gets to answer.
const ANSWER_TIMEOUT: Duration = Duration::from_secs(10);
/// One connection attempt, per address.
const CONNECT_TIMEOUT: Duration = Duration::from_secs(10);
pub const MAX_CERTIFICATES: usize = 8;
pub const MAX_CERTIFICATE_BYTES: usize = 16 * 1024;
pub const MAX_DETAIL: usize = 300;
/// What is read looking for an SSH identification line.
const MAX_BANNER: usize = 2048;
/// What is kept of a relay container's stderr.
const MAX_STDERR_LINES: usize = 20;

/// The relay a probe container runs as `/bin/sh -c`: refuse when a proxy variable is set, then
/// connect with `python3`, or with `nc` where there is none. Arguments: the python relay, the
/// host and the port.
const RELAY_SCRIPT: &str = r#"for v in HTTPS_PROXY https_proxy HTTP_PROXY http_proxy ALL_PROXY all_proxy; do
  eval "x=\${$v-}"
  if [ -n "$x" ]; then echo "octopushy-probe: proxy $v" >&2; exit 97; fi
done
if command -v python3 >/dev/null 2>&1; then exec python3 -c "$1" "$2" "$3"; fi
if command -v nc >/dev/null 2>&1; then exec nc -w 10 "$2" "$3"; fi
echo "octopushy-probe: no-relay" >&2
exit 98
"#;

/// One TCP connection between stdin/stdout and the host, saying on stderr how connecting went.
const PYTHON_RELAY: &str = r#"
import errno, os, select, socket, sys
def say(m):
    sys.stderr.write("octopushy-probe: " + m + "\n"); sys.stderr.flush()
try:
    s = socket.create_connection((sys.argv[1], int(sys.argv[2])), timeout=10)
except socket.gaierror:
    say("error dns"); sys.exit(99)
except ConnectionRefusedError:
    say("error refused"); sys.exit(99)
except (socket.timeout, TimeoutError):
    say("error timeout"); sys.exit(99)
except OSError as e:
    say("error unreachable" if e.errno in (errno.ENETUNREACH, errno.EHOSTUNREACH) else "error connect"); sys.exit(99)
say("connected")
s.settimeout(None)
inp = True
while True:
    r, _, _ = select.select([0, s] if inp else [s], [], [])
    if 0 in r:
        d = os.read(0, 65536)
        if d: s.sendall(d)
        else:
            inp = False
            try: s.shutdown(socket.SHUT_WR)
            except OSError: pass
    if s in r:
        d = s.recv(65536)
        if not d: break
        os.write(1, d)
"#;

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
enum Protocol {
    Tls,
    Ssh,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
enum Trust {
    System,
    Collect,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
enum Context {
    Agent,
    Tool,
}

/// A checked probe.
#[derive(Debug)]
struct Spec {
    protocol: Protocol,
    host: String,
    port: u16,
    server_name: String,
    trust: Trust,
    context: Context,
    image: String,
    timeout: Duration,
}

/// What a probe found.
#[derive(Debug, PartialEq, Eq)]
pub struct Outcome {
    pub status: &'static str,
    pub detail: String,
    pub certificates: Vec<Vec<u8>>,
}

impl Outcome {
    fn new(status: &'static str, detail: impl Into<String>) -> Self {
        Self {
            status,
            detail: detail.into(),
            certificates: Vec::new(),
        }
    }
}

/// A probe ID as the server writes it; anything else is not echoed back.
fn valid_id(id: &str) -> bool {
    !id.is_empty() && id.len() <= 64 && id.bytes().all(|b| b.is_ascii_alphanumeric() || b == b'-')
}

/// A host name or address that cannot be read as an option or carry anything else.
fn valid_host(host: &str) -> bool {
    !host.is_empty()
        && host.len() <= 253
        && !host.starts_with('-')
        && host
            .bytes()
            .all(|b| b.is_ascii_alphanumeric() || matches!(b, b'.' | b'-' | b':' | b'_'))
}

/// An image reference as a positional argument: nothing that reads as an option.
fn valid_image(image: &str) -> bool {
    !image.is_empty()
        && image.len() <= 255
        && image.starts_with(|c: char| c.is_ascii_alphanumeric())
        && image
            .chars()
            .all(|c| c.is_ascii_graphic() && !matches!(c, '\'' | '"' | '\\' | '`' | '$'))
}

fn spec(p: &ReachabilityProbe) -> Result<Spec, String> {
    let protocol = match p.protocol.as_str() {
        "tls" => Protocol::Tls,
        "ssh" => Protocol::Ssh,
        other => return Err(format!("unknown protocol {:?}", truncate(other, 16))),
    };
    if !valid_host(&p.host) {
        return Err("the host is not a DNS name or an IP address".into());
    }
    let port = u16::try_from(p.port)
        .ok()
        .filter(|&p| p != 0)
        .ok_or("the port is not between 1 and 65535")?;
    let server_name = if p.server_name.is_empty() {
        p.host.clone()
    } else if valid_host(&p.server_name) {
        p.server_name.clone()
    } else {
        return Err("the TLS server name is not a DNS name or an IP address".into());
    };
    let trust = match (protocol, p.tls_trust.as_str()) {
        (Protocol::Ssh, _) | (_, "system") => Trust::System,
        (_, "collect") => Trust::Collect,
        (_, other) => return Err(format!("unknown TLS trust {:?}", truncate(other, 16))),
    };
    let context = match p.context.as_str() {
        "agent" => Context::Agent,
        "tool" => Context::Tool,
        other => return Err(format!("unknown context {:?}", truncate(other, 16))),
    };
    let timeout = match p.timeout_seconds {
        0 => DEFAULT_TIMEOUT,
        s => Duration::from_secs(u64::from(s)).min(MAX_TIMEOUT),
    };
    Ok(Spec {
        protocol,
        host: p.host.clone(),
        port,
        server_name,
        trust,
        context,
        image: p.image.clone(),
        timeout,
    })
}

/// Text for people: printable, one line, bounded.
fn truncate(text: &str, max: usize) -> String {
    let clean: String = text
        .chars()
        .map(|c| if c.is_control() { ' ' } else { c })
        .collect();
    let clean = clean.trim();
    if clean.chars().count() <= max {
        clean.to_owned()
    } else {
        let mut out: String = clean.chars().take(max.saturating_sub(1)).collect();
        out.push('…');
        out
    }
}

/// Takes the credentials out of anything that looks like a URL, such as a proxy's.
fn redact(text: &str) -> String {
    let mut out = String::with_capacity(text.len());
    let mut rest = text;
    while let Some(i) = rest.find("://") {
        let (head, tail) = rest.split_at(i + 3);
        out.push_str(head);
        let end = tail
            .find(|c: char| c.is_whitespace() || c == '/')
            .unwrap_or(tail.len());
        match tail[..end].rfind('@') {
            Some(at) => {
                out.push_str("***");
                out.push_str(&tail[at..end]);
            }
            None => out.push_str(&tail[..end]),
        }
        rest = &tail[end..];
    }
    out.push_str(rest);
    out
}

/// How a host appears in a URL.
fn url_host(host: &str) -> String {
    if host.contains(':') {
        format!("[{host}]")
    } else {
        host.to_owned()
    }
}

/// Answers probes for one agent.
pub struct Prober {
    exec: Arc<ExecutorConfig>,
    slots: Arc<Semaphore>,
    queued: Arc<AtomicUsize>,
}

impl Prober {
    /// Probes with the tool runtime the executors use, so a `tool` probe is made as a tool connects.
    pub fn new(exec: Arc<ExecutorConfig>) -> Self {
        Self {
            exec,
            slots: Arc::new(Semaphore::new(CONCURRENT)),
            queued: Arc::new(AtomicUsize::new(0)),
        }
    }

    /// Runs `probe` on its own task and sends the report on `tx`. Nothing waits for it, and
    /// `cancel` (the end of the connection) stops it: the report would have nowhere to go.
    pub fn spawn(
        self: &Arc<Self>,
        probe: ReachabilityProbe,
        tx: mpsc::Sender<AgentMessage>,
        cancel: CancellationToken,
    ) {
        let this = self.clone();
        tokio::spawn(async move {
            let report = if this.queued.fetch_add(1, Ordering::SeqCst) >= QUEUED {
                report(
                    &probe.probe_id,
                    Outcome::new("error", "too many probes at once"),
                    0,
                )
            } else {
                let started = Instant::now();
                // The probe itself is never dropped half way: it stops on `cancel`, removing a
                // relay container it started.
                let slot = tokio::select! {
                    s = this.slots.acquire() => Some(s),
                    _ = cancel.cancelled() => None,
                };
                let outcome = match slot {
                    Some(_slot) => Some(this.probe(&probe, &cancel).await),
                    None => None,
                };
                let Some(outcome) = outcome.filter(|_| !cancel.is_cancelled()) else {
                    this.queued.fetch_sub(1, Ordering::SeqCst);
                    return;
                };
                report(&probe.probe_id, outcome, started.elapsed().as_millis())
            };
            this.queued.fetch_sub(1, Ordering::SeqCst);
            let _ = tx
                .send(AgentMessage {
                    kind: Some(Out::Reachability(report)),
                })
                .await;
        });
    }

    /// Runs one probe.
    pub async fn probe(&self, probe: &ReachabilityProbe, cancel: &CancellationToken) -> Outcome {
        let spec = match spec(probe) {
            Ok(s) => s,
            Err(e) => return Outcome::new("error", e),
        };
        let runtime = &self.exec.runtime;
        if let (Context::Tool, RuntimeKind::Podman | RuntimeKind::Docker) =
            (spec.context, runtime.kind)
        {
            // It keeps its own deadline and cancellation, so that its container is stopped
            // before it ends; dropping it part way could leave the container running.
            return relay(runtime, &spec, &probe.probe_id, cancel).await;
        }
        let work = async {
            match spec.context {
                Context::Agent => agent_process(&spec).await,
                Context::Tool => direct(&spec).await,
            }
        };
        tokio::select! {
            o = tokio::time::timeout(spec.timeout, work) => o.unwrap_or_else(|_| timed_out(&spec)),
            _ = cancel.cancelled() => Outcome::new("error", "cancelled"),
        }
    }
}

fn timed_out(spec: &Spec) -> Outcome {
    Outcome::new(
        "timeout",
        format!("no answer within {}s", spec.timeout.as_secs()),
    )
}

fn report(id: &str, mut outcome: Outcome, elapsed_ms: u128) -> ReachabilityReport {
    outcome.certificates.truncate(MAX_CERTIFICATES);
    if outcome
        .certificates
        .iter()
        .any(|c| c.len() > MAX_CERTIFICATE_BYTES)
    {
        outcome = Outcome::new(
            "error",
            "the presented certificate chain is too large to report",
        );
    }
    ReachabilityReport {
        probe_id: if valid_id(id) {
            id.to_owned()
        } else {
            String::new()
        },
        status: outcome.status.to_owned(),
        detail: truncate(&redact(&outcome.detail), MAX_DETAIL),
        certificates: outcome.certificates,
        elapsed_ms: u32::try_from(elapsed_ms).unwrap_or(u32::MAX),
    }
}

/// The agent process: as the `proxmox` executor's client connects.
async fn agent_process(spec: &Spec) -> Outcome {
    if spec.protocol != Protocol::Tls {
        return Outcome::new(
            "unsupported",
            "nothing connects over SSH from the agent process",
        );
    }
    let endpoint = format!("https://{}:{}/", url_host(&spec.host), spec.port);
    match octopushy_proxmox::proxied(&endpoint) {
        Ok(false) => direct(spec).await,
        Ok(true) => Outcome::new(
            "unsupported",
            "the agent's environment sends this endpoint through a proxy, and probes do not go through proxies",
        ),
        Err(e) => Outcome::new("error", e.to_string()),
    }
}

/// A connection from the agent's own network namespace, without any proxy.
async fn direct(spec: &Spec) -> Outcome {
    let addrs: Vec<_> = match tokio::net::lookup_host((spec.host.as_str(), spec.port)).await {
        Ok(a) => a.take(4).collect(),
        Err(_) => return Outcome::new("dns", format!("cannot resolve {}", spec.host)),
    };
    if addrs.is_empty() {
        return Outcome::new("dns", format!("{} has no address", spec.host));
    }
    let mut last = Outcome::new("error", "no address was tried");
    for addr in addrs {
        match tokio::time::timeout(CONNECT_TIMEOUT, TcpStream::connect(addr)).await {
            Ok(Ok(stream)) => return speak(spec, stream).await,
            Ok(Err(e)) => last = connect_error(&e, &addr.to_string()),
            Err(_) => {
                last = Outcome::new(
                    "timeout",
                    format!(
                        "connecting to {addr} timed out after {}s",
                        CONNECT_TIMEOUT.as_secs()
                    ),
                )
            }
        }
    }
    last
}

fn connect_error(e: &std::io::Error, addr: &str) -> Outcome {
    use std::io::ErrorKind as K;
    match e.kind() {
        K::ConnectionRefused => Outcome::new("refused", format!("{addr} refused the connection")),
        K::TimedOut => Outcome::new("timeout", format!("connecting to {addr} timed out")),
        K::NetworkUnreachable | K::HostUnreachable => {
            Outcome::new("unreachable", format!("{addr}: {e}"))
        }
        _ => Outcome::new("error", format!("connecting to {addr}: {e}")),
    }
}

/// The protocol, over a connection made one way or another.
async fn speak<S: AsyncRead + AsyncWrite + Unpin>(spec: &Spec, stream: S) -> Outcome {
    match spec.protocol {
        Protocol::Tls => tls(spec, stream).await,
        Protocol::Ssh => ssh(stream).await,
    }
}

/// The configuration of a TLS probe: the executor's own verification, or any certificate with
/// the handshake signature still checked.
fn tls_config(trust: Trust) -> Result<ClientConfig, String> {
    match trust {
        Trust::System => {
            octopushy_proxmox::tls_client_config(&Tls::Verify).map_err(|e| e.to_string())
        }
        Trust::Collect => {
            let provider = octopushy_proxmox::tls_provider();
            let mut config = ClientConfig::builder_with_provider(provider.clone())
                .with_safe_default_protocol_versions()
                .map_err(|e| e.to_string())?
                .dangerous()
                .with_custom_certificate_verifier(Arc::new(AcceptAnyCertificate::new(&provider)))
                .with_no_client_auth();
            config.alpn_protocols = vec![b"h2".to_vec(), b"http/1.1".to_vec()];
            Ok(config)
        }
    }
}

async fn tls<S: AsyncRead + AsyncWrite + Unpin>(spec: &Spec, stream: S) -> Outcome {
    let config = match tls_config(spec.trust) {
        Ok(c) => c,
        Err(e) => return Outcome::new("error", e),
    };
    let Ok(name) = ServerName::try_from(spec.server_name.clone()) else {
        return Outcome::new("error", "the TLS server name is not usable");
    };
    let connector = TlsConnector::from(Arc::new(config));
    match tokio::time::timeout(ANSWER_TIMEOUT, connector.connect(name, stream)).await {
        Err(_) => Outcome::new(
            "timeout",
            format!(
                "connected, but the TLS handshake did not finish within {}s",
                ANSWER_TIMEOUT.as_secs()
            ),
        ),
        Ok(Ok(conn)) => {
            let certificates = match spec.trust {
                Trust::Collect => conn
                    .get_ref()
                    .1
                    .peer_certificates()
                    .unwrap_or_default()
                    .iter()
                    .map(|c| c.as_ref().to_vec())
                    .collect(),
                Trust::System => Vec::new(),
            };
            Outcome {
                status: "reachable",
                detail: match spec.trust {
                    Trust::System => {
                        "TLS handshake completed; the certificate is trusted by this agent".into()
                    }
                    Trust::Collect => "TLS handshake completed".into(),
                },
                certificates,
            }
        }
        Ok(Err(e)) => tls_error(&e),
    }
}

fn tls_error(e: &std::io::Error) -> Outcome {
    match e.get_ref().and_then(|i| i.downcast_ref::<rustls::Error>()) {
        Some(rustls::Error::InvalidCertificate(c)) => Outcome::new(
            "tls-untrusted",
            format!("the certificate was not accepted: {c:?}"),
        ),
        Some(other) => Outcome::new("protocol", format!("the TLS handshake failed: {other}")),
        None => match e.kind() {
            std::io::ErrorKind::UnexpectedEof
            | std::io::ErrorKind::ConnectionReset
            | std::io::ErrorKind::BrokenPipe => {
                Outcome::new("protocol", "the connection closed during the TLS handshake")
            }
            _ => Outcome::new("protocol", format!("the TLS handshake failed: {e}")),
        },
    }
}

/// Reads the server's identification line (RFC 4253 4.2: lines before it are allowed). Once
/// something other than one has arrived, a pause ends the wait: a server sends those lines and
/// its identification together.
async fn ssh<S: AsyncRead + Unpin>(mut stream: S) -> Outcome {
    const PAUSE: Duration = Duration::from_secs(2);
    let mut buf = Vec::with_capacity(256);
    let mut chunk = [0u8; 512];
    let other = || {
        Outcome::new(
            "protocol",
            "something answered, but not with an SSH identification",
        )
    };
    loop {
        let mut start = 0;
        while let Some(end) = buf[start..].iter().position(|&b| b == b'\n') {
            let line = &buf[start..start + end];
            if line.starts_with(b"SSH-") {
                let version: Vec<String> = line
                    .split(|&b| b == b'-')
                    .take(2)
                    .map(|p| String::from_utf8_lossy(p).into_owned())
                    .collect();
                return Outcome::new(
                    "reachable",
                    format!(
                        "answered with an {} identification; its host key is not checked here",
                        truncate(&version.join("-"), 16)
                    ),
                );
            }
            start += end + 1;
        }
        if buf.len() >= MAX_BANNER {
            return other();
        }
        let wait = if buf.is_empty() {
            ANSWER_TIMEOUT
        } else {
            PAUSE
        };
        match tokio::time::timeout(wait, stream.read(&mut chunk)).await {
            Err(_) if buf.is_empty() => {
                return Outcome::new(
                    "timeout",
                    format!(
                        "connected, but no SSH identification arrived within {}s",
                        ANSWER_TIMEOUT.as_secs()
                    ),
                );
            }
            Ok(Ok(0)) | Ok(Err(_)) if buf.is_empty() => {
                return Outcome::new(
                    "protocol",
                    "the connection closed without an SSH identification",
                );
            }
            Err(_) | Ok(Ok(0)) | Ok(Err(_)) => return other(),
            Ok(Ok(n)) => buf.extend_from_slice(&chunk[..n]),
        }
    }
}

/// The connection through a container of the step's image, as the tool's would be made.
async fn relay(
    runtime: &ToolRuntime,
    spec: &Spec,
    id: &str,
    cancel: &CancellationToken,
) -> Outcome {
    if !valid_image(&spec.image) {
        return Outcome::new(
            "unsupported",
            "the step names no usable container image to probe from",
        );
    }
    if !valid_id(id) {
        return Outcome::new("error", "the probe ID is not usable as a container name");
    }
    let name = format!("octopushy-probe-{id}");
    // Preparing the command may ask the runtime about itself first, which may not answer.
    let deadline = tokio::time::Instant::now() + spec.timeout;
    let mut cmd = tokio::select! {
        c = relay_command(runtime, spec, &name) => c,
        _ = tokio::time::sleep_until(deadline) => return timed_out(spec),
        _ = cancel.cancelled() => return Outcome::new("error", "cancelled"),
    };
    let mut child = match cmd.spawn() {
        Ok(c) => c,
        Err(e) => {
            return Outcome::new(
                "error",
                format!("cannot start {}: {e}", runtime.container_binary.display()),
            );
        }
    };
    let pgid = child.id().map(|pid| pid as libc::pid_t);
    let stdin = child.stdin.take().expect("stdin is piped");
    let stdout = child.stdout.take().expect("stdout is piped");
    let stderr = child.stderr.take().expect("stderr is piped");
    let errors = tokio::spawn(async move {
        let mut tail: std::collections::VecDeque<String> = Default::default();
        let mut buf = Vec::new();
        let _ = stderr.take(64 * 1024).read_to_end(&mut buf).await;
        for line in String::from_utf8_lossy(&buf).lines() {
            if tail.len() == MAX_STDERR_LINES {
                tail.pop_front();
            }
            tail.push_back(line.to_owned());
        }
        tail.into_iter().collect::<Vec<_>>()
    });

    let spoken = tokio::select! {
        o = speak(spec, tokio::io::join(stdout, stdin)) => Some(o),
        _ = tokio::time::sleep_until(deadline) => Some(timed_out(spec)),
        _ = cancel.cancelled() => None,
    };
    // The relay ends when its stdin closes, which dropping the stream did; one that does not is
    // stopped, container and all.
    let code = match tokio::time::timeout(Duration::from_secs(3), child.wait()).await {
        Ok(Ok(status)) => status.code(),
        _ => {
            runtime.kill_group(&name, pgid).await;
            let _ = child.wait().await;
            None
        }
    };
    let lines = match tokio::time::timeout(Duration::from_secs(3), errors).await {
        Ok(Ok(lines)) => lines,
        _ => Vec::new(),
    };
    match spoken {
        None => Outcome::new("error", "cancelled"),
        Some(o) => classify_relay(o, code, &lines, &spec.image),
    }
}

/// `<runtime> run` of the relay: the tool containers' flags and network, the step's image, no
/// mount and no environment of the agent's own.
async fn relay_command(runtime: &ToolRuntime, spec: &Spec, name: &str) -> tokio::process::Command {
    let mut cmd = runtime
        .container_run(name, "io.octopushy.probe=true", true)
        .await;
    cmd.args([
        "--entrypoint",
        "/bin/sh",
        spec.image.as_str(),
        "-c",
        RELAY_SCRIPT,
        "octopushy-probe",
    ])
    .args([PYTHON_RELAY, spec.host.as_str(), &spec.port.to_string()])
    .stdin(Stdio::piped())
    .stdout(Stdio::piped())
    .stderr(Stdio::piped())
    .kill_on_drop(true)
    .process_group(0);
    cmd
}

/// What a relay's own words and exit say, over what the protocol saw through it.
fn classify_relay(spoken: Outcome, code: Option<i32>, stderr: &[String], image: &str) -> Outcome {
    if spoken.status == "reachable" {
        return spoken;
    }
    let mut connected = false;
    for line in stderr.iter().rev() {
        let line = line.trim();
        if let Some(rest) = line.strip_prefix("octopushy-probe: ") {
            match rest.split_once(' ').unwrap_or((rest, "")) {
                ("proxy", name) => {
                    return Outcome::new(
                        "unsupported",
                        format!(
                            "the tool container's environment sets {}, and probes do not go through proxies",
                            truncate(name, 16)
                        ),
                    );
                }
                ("no-relay", _) => {
                    return Outcome::new(
                        "unsupported",
                        format!(
                            "the image {image} has neither python3 nor nc to make a probe's connection"
                        ),
                    );
                }
                ("error", "dns") => {
                    return Outcome::new("dns", "the tool container cannot resolve the host");
                }
                ("error", "refused") => {
                    return Outcome::new("refused", "the connection was refused");
                }
                ("error", "timeout") => return Outcome::new("timeout", "connecting timed out"),
                ("error", "unreachable") => {
                    return Outcome::new("unreachable", "no route from the tool container");
                }
                ("error", _) => {
                    return Outcome::new("error", "the tool container could not connect");
                }
                ("connected", _) => connected = true,
                _ => {}
            }
        } else if let Some(rest) = line.strip_prefix("nc: ") {
            if rest.starts_with("bad address") {
                return Outcome::new("dns", "the tool container cannot resolve the host");
            }
            if rest.contains("Connection refused") {
                return Outcome::new("refused", "the connection was refused");
            }
            if rest.contains("timed out") {
                return Outcome::new("timeout", "connecting timed out");
            }
            if rest.contains("unreachable") || rest.contains("No route") {
                return Outcome::new("unreachable", "no route from the tool container");
            }
            if rest.starts_with("can't connect") {
                return Outcome::new("error", "the tool container could not connect");
            }
        }
    }
    if connected {
        return spoken;
    }
    let last = stderr
        .iter()
        .rev()
        .map(|l| l.trim())
        .find(|l| !l.is_empty())
        .unwrap_or("");
    let missing_shell =
        last.contains("/bin/sh") && (last.contains("not found") || last.contains("no such file"));
    match code {
        Some(126 | 127) => Outcome::new(
            "unsupported",
            format!("the image {image} has no /bin/sh to make a probe's connection"),
        ),
        Some(125) | Some(_) if missing_shell => Outcome::new(
            "unsupported",
            format!("the image {image} has no /bin/sh to make a probe's connection"),
        ),
        // busybox nc exits 1 without a word when it cannot connect, refused or timed out alike;
        // it exits 0 when a peer that answered closes.
        Some(1) => Outcome::new(
            "error",
            "the tool container could not connect (its nc does not say why: refused, timed out or unreachable)",
        ),
        Some(125) => Outcome::new(
            "error",
            format!(
                "the container runtime could not start the probe: {}",
                truncate(&redact(last), 160)
            ),
        ),
        _ => spoken,
    }
}

#[cfg(test)]
mod tests;
