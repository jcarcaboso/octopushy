//! A minimal fake Octopushy server for integration tests: a test CA (rcgen), the
//! `Enrollment` and `AgentChannel` services over real mutual TLS, and
//! `GET /agent/ca.pem` on the same port.

#![allow(dead_code)]

use std::sync::{Arc, Mutex};
use std::time::{Duration, SystemTime};

use octopushy_proto::v1::{
    AgentMessage, EnrollRequest, EnrollResponse, RenewCertificateRequest, RenewCertificateResponse,
    ResultAck, ServerMessage, Welcome,
    agent_channel_server::{AgentChannel, AgentChannelServer},
    agent_message::Kind as AgentKind,
    enrollment_server::{Enrollment, EnrollmentServer},
    server_message::Kind as ServerKind,
};
use rcgen::{
    BasicConstraints, CertificateParams, CertificateSigningRequestParams, DnType,
    ExtendedKeyUsagePurpose, IsCa, Issuer, KeyPair, KeyUsagePurpose, SanType,
};
use sha2::{Digest, Sha256};
use tokio::sync::mpsc;
use tokio_stream::wrappers::{ReceiverStream, TcpListenerStream};
use tonic::service::Routes;
use tonic::transport::{Certificate, Identity, Server, ServerTlsConfig};
use tonic::{Request, Response, Status, Streaming};

pub const AGENT_ID: &str = "agent-0001";
pub const TOKEN_SECRET: &str = "dGVzdC1lbnJvbGxtZW50LXNlY3JldA";

pub struct TestCa {
    pub cert_pem: String,
    pub cert_der: Vec<u8>,
    issuer: Issuer<'static, KeyPair>,
}

impl TestCa {
    pub fn new() -> Self {
        let key = KeyPair::generate_for(&rcgen::PKCS_ECDSA_P256_SHA256).unwrap();
        let mut params = CertificateParams::new(Vec::<String>::new()).unwrap();
        params
            .distinguished_name
            .push(DnType::CommonName, "Octopushy test agent CA");
        params.is_ca = IsCa::Ca(BasicConstraints::Unconstrained);
        params.key_usages = vec![KeyUsagePurpose::KeyCertSign, KeyUsagePurpose::CrlSign];
        let cert = params.self_signed(&key).unwrap();
        Self {
            cert_pem: cert.pem(),
            cert_der: cert.der().to_vec(),
            issuer: Issuer::new(params, key),
        }
    }

    pub fn fingerprint(&self) -> String {
        hex::encode(Sha256::digest(&self.cert_der))
    }

    /// A server certificate for 127.0.0.1 and localhost.
    pub fn server_identity(&self) -> (String, String) {
        let key = KeyPair::generate_for(&rcgen::PKCS_ECDSA_P256_SHA256).unwrap();
        let mut params = CertificateParams::new(vec!["localhost".to_owned()]).unwrap();
        params
            .subject_alt_names
            .push(SanType::IpAddress("127.0.0.1".parse().unwrap()));
        params
            .distinguished_name
            .push(DnType::CommonName, "octopushy-test-server");
        params.extended_key_usages = vec![ExtendedKeyUsagePurpose::ServerAuth];
        let cert = params.signed_by(&key, &self.issuer).unwrap();
        (cert.pem(), key.serialize_pem())
    }

    /// Signs a CSR, ignoring its subject: CN = `cn`.
    pub fn sign(
        &self,
        csr_der: &[u8],
        cn: &str,
        not_before: SystemTime,
        not_after: SystemTime,
    ) -> Result<Vec<u8>, Status> {
        let mut csr = CertificateSigningRequestParams::from_der(&csr_der.to_vec().into())
            .map_err(|e| Status::invalid_argument(format!("bad CSR: {e}")))?;
        let mut dn = rcgen::DistinguishedName::new();
        dn.push(DnType::CommonName, cn);
        csr.params.distinguished_name = dn;
        csr.params.subject_alt_names.clear();
        csr.params.not_before = not_before.into();
        csr.params.not_after = not_after.into();
        csr.params.extended_key_usages = vec![ExtendedKeyUsagePurpose::ClientAuth];
        csr.params.serial_number = Some(rcgen::SerialNumber::from(fastrand::u64(..)));
        Ok(csr.signed_by(&self.issuer).unwrap().der().to_vec())
    }
}

/// What the agent did, as seen by the server.
#[derive(Debug, Clone)]
pub enum Event {
    Enrolled { name: String },
    Connected { cn: String, cert_der: Vec<u8> },
    Message(AgentMessage),
    Renewed,
    Disconnected,
}

#[derive(Clone)]
pub struct Options {
    /// Age of certificates issued at enrollment (to make renewal due).
    pub enrolled_cert_age: Duration,
    pub max_concurrency: u32,
    pub heartbeat_seconds: u32,
    /// Acknowledge every result as soon as it arrives.
    pub auto_ack: bool,
}

impl Default for Options {
    fn default() -> Self {
        Self {
            enrolled_cert_age: Duration::ZERO,
            max_concurrency: 2,
            heartbeat_seconds: 1,
            auto_ack: false,
        }
    }
}

type Outbound = mpsc::Sender<Result<ServerMessage, Status>>;

#[derive(Clone)]
struct Shared {
    ca: Arc<TestCa>,
    opts: Options,
    events: mpsc::UnboundedSender<Event>,
    current: Arc<Mutex<Option<Outbound>>>,
}

const DAY: Duration = Duration::from_secs(86_400);

#[tonic::async_trait]
impl Enrollment for Shared {
    async fn enroll(
        &self,
        req: Request<EnrollRequest>,
    ) -> Result<Response<EnrollResponse>, Status> {
        let r = req.into_inner();
        if r.token != TOKEN_SECRET {
            return Err(Status::permission_denied("invalid enrollment token"));
        }
        let start = SystemTime::now() - self.opts.enrolled_cert_age;
        let cert = self
            .ca
            .sign(&r.csr_der, AGENT_ID, start, start + 30 * DAY)?;
        let _ = self.events.send(Event::Enrolled { name: r.name });
        Ok(Response::new(EnrollResponse {
            agent_id: AGENT_ID.into(),
            certificate_der: cert,
            ca_certificate_der: self.ca.cert_der.clone(),
        }))
    }
}

fn peer_cn<T>(req: &Request<T>) -> Result<(String, Vec<u8>), Status> {
    let certs = req
        .peer_certs()
        .ok_or_else(|| Status::unauthenticated("a client certificate is required"))?;
    let der = certs
        .first()
        .ok_or_else(|| Status::unauthenticated("no certificate"))?
        .to_vec();
    let (_, cert) = x509_parser::parse_x509_certificate(&der)
        .map_err(|_| Status::unauthenticated("bad certificate"))?;
    let cn = cert
        .subject()
        .iter_common_name()
        .next()
        .and_then(|c| c.as_str().ok())
        .unwrap_or("")
        .to_owned();
    Ok((cn, der))
}

#[tonic::async_trait]
impl AgentChannel for Shared {
    type ConnectStream = ReceiverStream<Result<ServerMessage, Status>>;

    async fn connect(
        &self,
        req: Request<Streaming<AgentMessage>>,
    ) -> Result<Response<Self::ConnectStream>, Status> {
        let (cn, cert_der) = peer_cn(&req)?;
        let mut inbound = req.into_inner();
        let hello = match inbound.message().await? {
            Some(
                m @ AgentMessage {
                    kind: Some(AgentKind::Hello(_)),
                },
            ) => m,
            other => {
                return Err(Status::invalid_argument(format!(
                    "expected Hello, got {other:?}"
                )));
            }
        };
        let _ = self.events.send(Event::Connected {
            cn: cn.clone(),
            cert_der,
        });
        let _ = self.events.send(Event::Message(hello));

        let (tx, rx) = mpsc::channel(64);
        tx.send(Ok(ServerMessage {
            kind: Some(ServerKind::Welcome(Welcome {
                agent_id: cn,
                max_concurrency: self.opts.max_concurrency,
                heartbeat_seconds: self.opts.heartbeat_seconds,
            })),
        }))
        .await
        .unwrap();
        *self.current.lock().unwrap() = Some(tx);

        let shared = self.clone();
        tokio::spawn(async move {
            while let Ok(Some(msg)) = inbound.message().await {
                if shared.opts.auto_ack
                    && let Some(AgentKind::Result(r)) = &msg.kind
                {
                    shared.send_now(ServerKind::ResultAck(ResultAck {
                        attempt_id: r.attempt_id.clone(),
                    }));
                }
                let _ = shared.events.send(Event::Message(msg));
            }
            let _ = shared.events.send(Event::Disconnected);
        });
        Ok(Response::new(ReceiverStream::new(rx)))
    }

    async fn renew_certificate(
        &self,
        req: Request<RenewCertificateRequest>,
    ) -> Result<Response<RenewCertificateResponse>, Status> {
        let (cn, _) = peer_cn(&req)?;
        let now = SystemTime::now();
        let cert = self
            .ca
            .sign(&req.into_inner().csr_der, &cn, now, now + 30 * DAY)?;
        let _ = self.events.send(Event::Renewed);
        Ok(Response::new(RenewCertificateResponse {
            certificate_der: cert,
        }))
    }
}

impl Shared {
    fn send_now(&self, kind: ServerKind) {
        if let Some(tx) = self.current.lock().unwrap().as_ref() {
            let _ = tx.try_send(Ok(ServerMessage { kind: Some(kind) }));
        }
    }
}

pub struct FakeServer {
    pub url: String,
    pub ca: Arc<TestCa>,
    shared: Shared,
    events: mpsc::UnboundedReceiver<Event>,
    task: tokio::task::JoinHandle<()>,
}

impl Drop for FakeServer {
    fn drop(&mut self) {
        self.task.abort();
    }
}

impl FakeServer {
    pub async fn start(opts: Options) -> Self {
        let ca = Arc::new(TestCa::new());
        let (events_tx, events) = mpsc::unbounded_channel();
        let shared = Shared {
            ca: ca.clone(),
            opts,
            events: events_tx,
            current: Arc::new(Mutex::new(None)),
        };
        let (cert_pem, key_pem) = ca.server_identity();
        let tls = ServerTlsConfig::new()
            .identity(Identity::from_pem(cert_pem, key_pem))
            .client_ca_root(Certificate::from_pem(&ca.cert_pem))
            .client_auth_optional(true);

        let ca_pem = ca.cert_pem.clone();
        let router = Routes::new(EnrollmentServer::new(shared.clone()))
            .add_service(
                AgentChannelServer::new(shared.clone())
                    .max_decoding_message_size(64 * 1024 * 1024)
                    .max_encoding_message_size(64 * 1024 * 1024),
            )
            .into_axum_router()
            .route(
                "/agent/ca.pem",
                axum::routing::get(move || {
                    let pem = ca_pem.clone();
                    async move { pem }
                }),
            );

        let listener = tokio::net::TcpListener::bind("127.0.0.1:0").await.unwrap();
        let port = listener.local_addr().unwrap().port();
        let task = tokio::spawn(async move {
            Server::builder()
                .tls_config(tls)
                .unwrap()
                .add_routes(Routes::from(router))
                .serve_with_incoming(TcpListenerStream::new(listener))
                .await
                .unwrap();
        });
        Self {
            url: format!("https://127.0.0.1:{port}"),
            ca,
            shared,
            events,
            task,
        }
    }

    pub fn token(&self) -> String {
        format!("opy1.{TOKEN_SECRET}.{}", self.ca.fingerprint())
    }

    /// Sends a message on the current stream.
    pub async fn send(&self, kind: ServerKind) {
        let tx = self
            .shared
            .current
            .lock()
            .unwrap()
            .clone()
            .expect("an agent is connected");
        tx.send(Ok(ServerMessage { kind: Some(kind) }))
            .await
            .unwrap();
    }

    /// Ends the current stream from the server side.
    pub fn disconnect(&self) {
        self.shared.current.lock().unwrap().take();
    }

    pub async fn next_event(&mut self) -> Event {
        tokio::time::timeout(Duration::from_secs(30), self.events.recv())
            .await
            .expect("timed out waiting for the agent")
            .expect("event channel closed")
    }

    /// Skips events until `f` returns `Some`.
    pub async fn wait_for<T>(&mut self, mut f: impl FnMut(&Event) -> Option<T>) -> T {
        loop {
            let e = self.next_event().await;
            if let Some(t) = f(&e) {
                return t;
            }
        }
    }

    /// Waits for the result of `attempt_id`, returning it and every message seen
    /// on the way.
    pub async fn wait_result(
        &mut self,
        attempt_id: &str,
    ) -> (octopushy_proto::v1::AttemptResult, Vec<AgentMessage>) {
        let mut seen = Vec::new();
        loop {
            if let Event::Message(m) = self.next_event().await {
                if let Some(AgentKind::Result(r)) = &m.kind
                    && r.attempt_id == attempt_id
                {
                    return (r.clone(), seen);
                }
                seen.push(m);
            }
        }
    }
}

/// Log lines of `attempt_id` in `messages`, as (sequence, text).
pub fn log_lines(messages: &[AgentMessage], attempt_id: &str) -> Vec<(u64, String)> {
    let mut out = Vec::new();
    for m in messages {
        if let Some(AgentKind::Log(l)) = &m.kind
            && l.attempt_id == attempt_id
        {
            for (i, line) in l.lines.iter().enumerate() {
                out.push((l.first_sequence + i as u64, line.text.clone()));
            }
        }
    }
    out
}

pub fn init_tracing() {
    let _ = tracing_subscriber::fmt()
        .with_test_writer()
        .with_env_filter(
            tracing_subscriber::EnvFilter::try_from_default_env()
                .unwrap_or_else(|_| "octopushy=debug,info".into()),
        )
        .try_init();
}
