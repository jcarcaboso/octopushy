//! Certificates and TLS for the server connection.
//!
//! The agent trusts exactly one root: the agent CA it fetched at enrollment and
//! checked against the token's fingerprint. Only that one fetch skips
//! certificate verification.

use std::sync::Arc;
use std::time::{Duration, SystemTime, UNIX_EPOCH};

use anyhow::{Context, Result, bail};
use rustls::pki_types::{CertificateDer, pem::PemObject};
use sha2::{Digest, Sha256};
use tonic::transport::{Certificate, ClientTlsConfig, Endpoint, Identity};
use url::Url;

/// Lowercase hex SHA-256 of `bytes`.
pub fn sha256_hex(bytes: &[u8]) -> String {
    hex::encode(Sha256::digest(bytes))
}

/// The DER of the only certificate in `pem`; fails on zero or several.
pub fn single_cert_der(pem: &str) -> Result<Vec<u8>> {
    let mut certs = CertificateDer::pem_slice_iter(pem.as_bytes());
    let first = certs
        .next()
        .context("the PEM contains no certificate")?
        .context("invalid certificate PEM")?;
    if certs.next().is_some() {
        bail!("the PEM contains more than one certificate");
    }
    Ok(first.to_vec())
}

/// PEM-encodes a DER certificate.
pub fn cert_pem(der: &[u8]) -> String {
    use base64::Engine;
    let b64 = base64::engine::general_purpose::STANDARD.encode(der);
    let mut out = String::from("-----BEGIN CERTIFICATE-----\n");
    for chunk in b64.as_bytes().chunks(64) {
        out.push_str(std::str::from_utf8(chunk).expect("base64 is ASCII"));
        out.push('\n');
    }
    out.push_str("-----END CERTIFICATE-----\n");
    out
}

/// What the agent needs to know about a certificate.
#[derive(Debug, Clone)]
pub struct CertInfo {
    pub not_before: SystemTime,
    pub not_after: SystemTime,
    pub common_name: Option<String>,
    /// The raw subject public key (for EC keys, the uncompressed point).
    pub public_key: Vec<u8>,
}

impl CertInfo {
    pub fn parse(der: &[u8]) -> Result<Self> {
        let (_, cert) = x509_parser::parse_x509_certificate(der)
            .map_err(|e| anyhow::anyhow!("invalid certificate: {e}"))?;
        let to_time = |t: x509_parser::time::ASN1Time| {
            let secs = t.timestamp();
            if secs >= 0 {
                UNIX_EPOCH + Duration::from_secs(secs as u64)
            } else {
                UNIX_EPOCH
            }
        };
        let common_name = cert
            .subject()
            .iter_common_name()
            .next()
            .and_then(|cn| cn.as_str().ok())
            .map(str::to_owned);
        Ok(Self {
            not_before: to_time(cert.validity().not_before),
            not_after: to_time(cert.validity().not_after),
            common_name,
            public_key: cert.public_key().subject_public_key.data.to_vec(),
        })
    }

    /// The point after which the certificate should be renewed: when less than a
    /// third of its validity remains.
    pub fn renew_after(&self) -> SystemTime {
        let lifetime = self
            .not_after
            .duration_since(self.not_before)
            .unwrap_or_default();
        self.not_after - lifetime / 3
    }

    pub fn needs_renewal(&self, now: SystemTime) -> bool {
        now >= self.renew_after()
    }

    pub fn is_expired(&self, now: SystemTime) -> bool {
        now >= self.not_after
    }
}

/// Checks that `server` is an `https://host[:port]` URL without a path.
pub fn parse_server_url(server: &str) -> Result<Url> {
    let url =
        Url::parse(server.trim()).with_context(|| format!("invalid server URL {server:?}"))?;
    if url.scheme() != "https" {
        bail!("the server URL must start with https://");
    }
    if url.host().is_none() {
        bail!("the server URL has no host");
    }
    if !(url.path() == "/" || url.path().is_empty()) || url.query().is_some() {
        bail!("the server URL must not have a path or query (gRPC needs the root)");
    }
    if !url.username().is_empty() || url.password().is_some() {
        bail!("the server URL must not contain credentials");
    }
    Ok(url)
}

/// Fetches `GET <server>/agent/ca.pem` without verifying the server certificate.
/// The caller must check the result against the token's fingerprint.
pub async fn fetch_ca_unverified(server: &Url) -> Result<String> {
    let provider = Arc::new(rustls::crypto::ring::default_provider());
    let mut config = rustls::ClientConfig::builder_with_provider(provider.clone())
        .with_safe_default_protocol_versions()?
        .dangerous()
        .with_custom_certificate_verifier(Arc::new(octopushy_proxmox::AcceptAnyCertificate::new(
            &provider,
        )))
        .with_no_client_auth();
    config.alpn_protocols = vec![b"h2".to_vec(), b"http/1.1".to_vec()];
    let client = reqwest::Client::builder()
        .tls_backend_preconfigured(config)
        .timeout(Duration::from_secs(30))
        .redirect(reqwest::redirect::Policy::none())
        .build()?;
    let url = server.join("agent/ca.pem")?;
    let resp = client
        .get(url.clone())
        .send()
        .await
        .with_context(|| format!("fetching {url}"))?;
    if !resp.status().is_success() {
        bail!("fetching {url}: HTTP {}", resp.status());
    }
    let body = resp.bytes().await?;
    if body.len() > 64 * 1024 {
        bail!("the CA certificate from {url} is implausibly large");
    }
    String::from_utf8(body.to_vec()).context("the CA certificate is not text")
}

/// A gRPC endpoint that trusts only `ca_pem`, optionally presenting a client
/// identity (mutual TLS).
pub fn endpoint(server: &Url, ca_pem: &str, identity: Option<(&str, &str)>) -> Result<Endpoint> {
    let mut tls = ClientTlsConfig::new().ca_certificate(Certificate::from_pem(ca_pem));
    if let Some((cert_pem, key_pem)) = identity {
        tls = tls.identity(Identity::from_pem(cert_pem, key_pem));
    }
    let endpoint = Endpoint::from_shared(server.as_str().trim_end_matches('/').to_owned())?
        .tls_config(tls)?
        .connect_timeout(Duration::from_secs(15))
        .tcp_keepalive(Some(Duration::from_secs(60)))
        .http2_keep_alive_interval(Duration::from_secs(30))
        .keep_alive_timeout(Duration::from_secs(20))
        .keep_alive_while_idle(true);
    Ok(endpoint)
}

/// Describes a connection error with its causes, adding a hint for the usual
/// certificate problems.
pub fn describe_connect_error(err: &(dyn std::error::Error + 'static)) -> String {
    let mut text = err.to_string();
    let mut source = err.source();
    while let Some(cause) = source {
        let c = cause.to_string();
        if !text.contains(&c) {
            text.push_str(": ");
            text.push_str(&c);
        }
        source = cause.source();
    }
    let lower = text.to_ascii_lowercase();
    if lower.contains("certificate expired") || lower.contains("expired") && lower.contains("cert")
    {
        text.push_str(
            " (if the agent's own certificate expired, enroll again with `octopushy agent enroll --force`)",
        );
    }
    text
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn server_urls() {
        assert!(parse_server_url("https://127.0.0.1:8443").is_ok());
        assert!(parse_server_url("https://octopushy.lan:8443/").is_ok());
        assert!(parse_server_url("http://x:8443").is_err());
        assert!(parse_server_url("https://x:8443/api").is_err());
        assert!(parse_server_url("https://u:p@x:8443").is_err());
    }

    #[test]
    fn pem_round_trip_and_info() {
        let key = rcgen::KeyPair::generate_for(&rcgen::PKCS_ECDSA_P256_SHA256).unwrap();
        let mut params = rcgen::CertificateParams::new(Vec::<String>::new()).unwrap();
        params
            .distinguished_name
            .push(rcgen::DnType::CommonName, "agent-1");
        let now = SystemTime::now();
        params.not_before = (now - Duration::from_secs(20 * 86400)).into();
        params.not_after = (now + Duration::from_secs(10 * 86400)).into();
        let cert = params.self_signed(&key).unwrap();
        let der = single_cert_der(&cert.pem()).unwrap();
        assert_eq!(der, cert.der().to_vec());
        assert_eq!(single_cert_der(&cert_pem(&der)).unwrap(), der);
        assert!(single_cert_der(&format!("{}{}", cert.pem(), cert.pem())).is_err());
        assert!(single_cert_der("nothing").is_err());

        let info = CertInfo::parse(&der).unwrap();
        assert_eq!(info.common_name.as_deref(), Some("agent-1"));
        assert_eq!(info.public_key, key.public_key_raw());
        // 20 of 30 days have passed: exactly the renewal point, give or take.
        assert!(info.needs_renewal(now + Duration::from_secs(60)));
        assert!(!info.needs_renewal(now - Duration::from_secs(86400)));
        assert!(!info.is_expired(now));
    }
}
