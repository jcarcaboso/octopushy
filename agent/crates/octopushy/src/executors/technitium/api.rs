//! One member's Technitium API, over HTTPS on its own address, to one exact certificate.

use std::sync::{Arc, Mutex};
use std::time::Duration;

use reqwest::{Client, Response};
use rustls::DigitallySignedStruct;
use rustls::SignatureScheme;
use rustls::client::danger::{HandshakeSignatureValid, ServerCertVerified, ServerCertVerifier};
use rustls_pki_types::{CertificateDer, ServerName, UnixTime};
use serde::Deserialize;
use serde_json::Value;
use sha2::{Digest, Sha256};

use crate::executors::Failure;

/// Well beyond a real answer: settings and zone lists are small. A member is not trusted to
/// keep its answers to a size the agent can hold.
const MAX_JSON_BYTES: usize = 8 * 1024 * 1024;
/// A blocked or allowed list is a person's own entries, not a downloaded list.
const MAX_EXPORT_BYTES: usize = 16 * 1024 * 1024;
const MAX_EXPORT_ENTRIES: usize = 200_000;

/// Why a call did not work. Reaching a member is one thing and being refused by it another, and
/// a change whose answer never arrived is neither: only asking again can tell whether it happened.
#[derive(Debug)]
pub enum ApiError {
    Unreachable(String),
    Refused(String),
    /// The server said no.
    Api(String),
    /// The request may have been acted on, and what came back cannot be read.
    Lost(String),
}

impl std::fmt::Display for ApiError {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        match self {
            ApiError::Unreachable(e)
            | ApiError::Refused(e)
            | ApiError::Api(e)
            | ApiError::Lost(e) => f.write_str(e),
        }
    }
}

/// The certificate a connection was offered, whether or not it was accepted.
pub type Offered = Arc<Mutex<Option<Vec<u8>>>>;

pub fn sha256_hex(der: &[u8]) -> String {
    hex::encode(Sha256::digest(der))
}

/// Accepts exactly one certificate, whatever its name or issuer, because a member's is its own.
/// The handshake must still be signed by it. With no pin it accepts anything and only records
/// it: that is how a certificate is read for an operator, and such a client sends no credential.
#[derive(Debug)]
struct Pinned {
    sha256: Option<String>,
    offered: Offered,
    signatures: octopushy_proxmox::AcceptAnyCertificate,
}

impl ServerCertVerifier for Pinned {
    fn verify_server_cert(
        &self,
        end_entity: &CertificateDer<'_>,
        _intermediates: &[CertificateDer<'_>],
        _server_name: &ServerName<'_>,
        _ocsp_response: &[u8],
        _now: UnixTime,
    ) -> Result<ServerCertVerified, rustls::Error> {
        *self.offered.lock().unwrap_or_else(|e| e.into_inner()) = Some(end_entity.to_vec());
        match &self.sha256 {
            Some(pin) if !sha256_hex(end_entity).eq_ignore_ascii_case(pin) => {
                Err(rustls::Error::General("not the trusted certificate".into()))
            }
            _ => Ok(ServerCertVerified::assertion()),
        }
    }

    fn verify_tls12_signature(
        &self,
        message: &[u8],
        cert: &CertificateDer<'_>,
        dss: &DigitallySignedStruct,
    ) -> Result<HandshakeSignatureValid, rustls::Error> {
        self.signatures.verify_tls12_signature(message, cert, dss)
    }

    fn verify_tls13_signature(
        &self,
        message: &[u8],
        cert: &CertificateDer<'_>,
        dss: &DigitallySignedStruct,
    ) -> Result<HandshakeSignatureValid, rustls::Error> {
        self.signatures.verify_tls13_signature(message, cert, dss)
    }

    fn supported_verify_schemes(&self) -> Vec<SignatureScheme> {
        self.signatures.supported_verify_schemes()
    }
}

/// A client for one member, and what certificate it was offered.
pub fn http_client(pin: Option<&str>, timeout: Duration) -> Result<(Client, Offered), Failure> {
    let mut config = octopushy_proxmox::tls_client_config(&octopushy_proxmox::Tls::Insecure)
        .map_err(|e| Failure::msg(e.to_string()))?;
    let offered = Offered::default();
    config
        .dangerous()
        .set_certificate_verifier(Arc::new(Pinned {
            sha256: pin.map(str::to_owned),
            offered: offered.clone(),
            signatures: octopushy_proxmox::AcceptAnyCertificate::new(
                &rustls::crypto::ring::default_provider(),
            ),
        }));
    let client = Client::builder()
        .tls_backend_preconfigured(config)
        .timeout(timeout)
        .connect_timeout(timeout.min(Duration::from_secs(15)))
        .user_agent(concat!("octopushy-agent/", env!("CARGO_PKG_VERSION")))
        .redirect(reqwest::redirect::Policy::none())
        .retry(reqwest::retry::never())
        .build()
        .map_err(|e| Failure::msg(format!("cannot build the HTTP client: {e}")))?;
    Ok((client, offered))
}

/// A member's `/api/admin/cluster/state`.
#[derive(Debug, Clone, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ClusterState {
    #[serde(default)]
    pub version: String,
    #[serde(default)]
    pub dns_server_domain: String,
    #[serde(default)]
    pub cluster_initialized: bool,
    #[serde(default)]
    pub cluster_domain: Option<String>,
    #[serde(default, alias = "nodes")]
    pub cluster_nodes: Vec<Peer>,
}

#[derive(Debug, Clone, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct Peer {
    pub id: i64,
    #[serde(default)]
    pub name: String,
    #[serde(default)]
    pub ip_addresses: Vec<String>,
    #[serde(rename = "type", default)]
    pub kind: String,
    #[serde(default)]
    pub state: String,
}

/// A signed-in session on one member.
pub struct Api {
    http: Client,
    endpoint: String,
    token: String,
}

impl Api {
    pub async fn login(http: &Client, endpoint: &str, password: &str) -> Result<Self, ApiError> {
        let endpoint = endpoint.trim_end_matches('/').to_owned();
        let response = http
            .post(format!("{endpoint}/api/user/login"))
            .form(&[("user", "admin"), ("pass", password)])
            .send()
            .await
            .map_err(|e| ApiError::Unreachable(transport(&e)))?;
        let body = decode(response).await?;
        if body["status"] != "ok" {
            return Err(ApiError::Refused(message(&body)));
        }
        let token = body["token"]
            .as_str()
            .ok_or_else(|| ApiError::Api("the login returned no token".into()))?
            .to_owned();
        Ok(Self {
            http: http.clone(),
            endpoint,
            token,
        })
    }

    pub async fn get(&self, path: &str) -> Result<Value, ApiError> {
        self.call(path, &[]).await
    }

    /// Calls `path` with a form. The server applies a change as soon as it has read it.
    pub async fn call(&self, path: &str, form: &[(&str, &str)]) -> Result<Value, ApiError> {
        self.call_within(path, form, None).await
    }

    /// `call`, with a timeout of its own for what takes longer than an ordinary request.
    pub async fn call_within(
        &self,
        path: &str,
        form: &[(&str, &str)],
        timeout: Option<Duration>,
    ) -> Result<Value, ApiError> {
        let mut request = self
            .http
            .post(format!("{}{path}", self.endpoint))
            .bearer_auth(&self.token)
            .form(form);
        if let Some(timeout) = timeout {
            request = request.timeout(timeout);
        }
        let response = request
            .send()
            .await
            .map_err(|e| ApiError::Unreachable(transport(&e)))?;
        let body = decode(response).await?;
        if body["status"] == "ok" {
            Ok(body["response"].clone())
        } else {
            Err(ApiError::Api(message(&body)))
        }
    }

    /// An export: one entry on each line.
    pub async fn lines(&self, path: &str) -> Result<Vec<String>, ApiError> {
        let response = self
            .http
            .get(format!("{}{path}", self.endpoint))
            .bearer_auth(&self.token)
            .send()
            .await
            .map_err(|e| ApiError::Unreachable(transport(&e)))?;
        if !response.status().is_success() {
            return Err(ApiError::Api(format!(
                "{path} returned {}",
                response.status()
            )));
        }
        let body = read_limited(response, MAX_EXPORT_BYTES).await?;
        let text = String::from_utf8_lossy(&body);
        let lines: Vec<String> = text
            .lines()
            .map(str::trim)
            .filter(|l| !l.is_empty() && !l.starts_with('#'))
            .take(MAX_EXPORT_ENTRIES + 1)
            .map(str::to_owned)
            .collect();
        if lines.len() > MAX_EXPORT_ENTRIES {
            return Err(ApiError::Lost(format!(
                "{path} holds more than {MAX_EXPORT_ENTRIES} entries"
            )));
        }
        Ok(lines)
    }

    pub async fn cluster_state(&self) -> Result<ClusterState, ApiError> {
        let value = self.get("/api/admin/cluster/state").await?;
        serde_json::from_value(value)
            .map_err(|e| ApiError::Api(format!("unexpected cluster state: {e}")))
    }

    /// Best effort: a session left behind expires by itself.
    pub async fn logout(self) {
        let _ = self.get("/api/user/logout").await;
    }
}

/// Reads a response as it arrives and stops at `limit`, since a length is only a claim, and a
/// chunked response does not make one.
async fn read_limited(mut response: Response, limit: usize) -> Result<Vec<u8>, ApiError> {
    if response.content_length().is_some_and(|l| l > limit as u64) {
        return Err(ApiError::Lost(format!(
            "the answer is larger than {limit} bytes"
        )));
    }
    let mut body = Vec::new();
    while let Some(chunk) = response
        .chunk()
        .await
        .map_err(|e| ApiError::Lost(format!("the answer was cut off: {}", transport(&e))))?
    {
        if body.len() + chunk.len() > limit {
            return Err(ApiError::Lost(format!(
                "the answer is larger than {limit} bytes"
            )));
        }
        body.extend_from_slice(&chunk);
    }
    Ok(body)
}

async fn decode(response: Response) -> Result<Value, ApiError> {
    let status = response.status();
    let body = read_limited(response, MAX_JSON_BYTES).await?;
    serde_json::from_slice(&body)
        .map_err(|e| ApiError::Lost(format!("the server answered {status} without JSON: {e}")))
}

fn message(body: &Value) -> String {
    body["errorMessage"]
        .as_str()
        .unwrap_or("the server refused the request")
        .to_owned()
}

fn transport(e: &reqwest::Error) -> String {
    let mut text = e.to_string();
    let mut source = std::error::Error::source(e);
    while let Some(cause) = source {
        text = format!("{text}: {cause}");
        source = cause.source();
    }
    text
}
