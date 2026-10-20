//! `octopushy agent enroll`: exchanges a one-time token and a CSR for the
//! agent's certificate.

use std::path::PathBuf;

use anyhow::{Context, Result, bail};
use octopushy_proto::v1::{EnrollRequest, enrollment_client::EnrollmentClient};
use rcgen::{CertificateParams, DnType, KeyPair};

use crate::identity::{self, AgentRecord};
use crate::paths::StatePaths;
use crate::tls::{self, CertInfo};

/// A parsed enrollment token `opy1.<secret>.<fingerprint>`. `Debug` hides the
/// secret.
#[derive(Clone, PartialEq, Eq)]
pub struct EnrollmentToken {
    pub secret: String,
    /// Lowercase hex SHA-256 of the agent CA certificate (DER).
    pub fingerprint: String,
}

impl std::fmt::Debug for EnrollmentToken {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        write!(f, "EnrollmentToken(opy1.***.{})", self.fingerprint)
    }
}

impl EnrollmentToken {
    pub fn parse(token: &str) -> Result<Self> {
        let parts: Vec<&str> = token.trim().split('.').collect();
        let [prefix, secret, fingerprint] = parts.as_slice() else {
            bail!("an enrollment token looks like opy1.<secret>.<fingerprint>");
        };
        if *prefix != "opy1" {
            bail!("unsupported enrollment token version {prefix:?} (expected opy1)");
        }
        if secret.is_empty()
            || !secret
                .bytes()
                .all(|b| b.is_ascii_alphanumeric() || b == b'-' || b == b'_')
        {
            bail!("the token's secret must be base64url without padding");
        }
        let fingerprint = fingerprint.to_ascii_lowercase();
        if fingerprint.len() != 64 || !fingerprint.bytes().all(|b| b.is_ascii_hexdigit()) {
            bail!("the token's fingerprint must be 64 hex digits (SHA-256)");
        }
        Ok(Self {
            secret: (*secret).to_owned(),
            fingerprint,
        })
    }
}

/// Options of `octopushy agent enroll`.
#[derive(Debug, Clone)]
pub struct EnrollOptions {
    pub server: String,
    pub token: EnrollmentToken,
    pub name: Option<String>,
    pub state_dir: PathBuf,
    pub force: bool,
}

/// Generates an ECDSA P-256 key and a PKCS#10 request with `common_name`.
pub fn new_key_and_csr(common_name: &str) -> Result<(KeyPair, Vec<u8>)> {
    let key = KeyPair::generate_for(&rcgen::PKCS_ECDSA_P256_SHA256)?;
    let mut params = CertificateParams::new(Vec::<String>::new())?;
    params
        .distinguished_name
        .push(DnType::CommonName, common_name);
    let csr = params.serialize_request(&key)?;
    Ok((key, csr.der().to_vec()))
}

/// Checks that `cert_der` certifies `key` and is currently valid.
pub fn check_issued_cert(cert_der: &[u8], key: &KeyPair) -> Result<CertInfo> {
    let info = CertInfo::parse(cert_der).context("the server returned an invalid certificate")?;
    if info.public_key != key.public_key_raw() {
        bail!("the server returned a certificate for a different key");
    }
    if info.is_expired(std::time::SystemTime::now()) {
        bail!("the server returned an expired certificate");
    }
    Ok(info)
}

/// The machine's hostname, used as the default agent name.
pub fn hostname() -> String {
    std::fs::read_to_string("/proc/sys/kernel/hostname")
        .or_else(|_| std::fs::read_to_string("/etc/hostname"))
        .map(|s| s.trim().to_owned())
        .ok()
        .filter(|s| !s.is_empty())
        .unwrap_or_else(|| "octopushy-agent".to_owned())
}

/// Runs the enrollment and stores the result.
pub async fn enroll(opts: EnrollOptions) -> Result<AgentRecord> {
    let paths = StatePaths::new(&opts.state_dir);
    if identity::exists(&paths) && !opts.force {
        bail!(
            "{} already holds an enrollment; pass --force to replace it",
            paths.root.display()
        );
    }
    let server = tls::parse_server_url(&opts.server)?;
    let name = opts.name.clone().unwrap_or_else(hostname);

    // 1. Fetch the CA without verification and pin it by the token's fingerprint.
    tracing::info!("fetching the agent CA from {server}agent/ca.pem");
    let ca_pem = tls::fetch_ca_unverified(&server).await?;
    let ca_der = tls::single_cert_der(&ca_pem).context("the server's agent CA is unusable")?;
    let actual = tls::sha256_hex(&ca_der);
    if actual != opts.token.fingerprint {
        bail!(
            "the server's CA fingerprint {actual} does not match the token's {}; refusing to enroll",
            opts.token.fingerprint
        );
    }
    let ca_pem = tls::cert_pem(&ca_der);
    tracing::info!("the agent CA matches the token's fingerprint");

    // 2. Generate our own key and request a certificate, trusting only that CA.
    let (key, csr_der) = new_key_and_csr(&name)?;
    let channel = tls::endpoint(&server, &ca_pem, None)?
        .connect()
        .await
        .map_err(|e| {
            anyhow::anyhow!(
                "connecting to {server}: {}",
                tls::describe_connect_error(&e)
            )
        })?;
    let response = EnrollmentClient::new(channel)
        .enroll(EnrollRequest {
            token: opts.token.secret.clone(),
            name: name.clone(),
            csr_der,
            agent_version: crate::VERSION.to_owned(),
        })
        .await
        .map_err(|s| anyhow::anyhow!("the server refused the enrollment: {}", s.message()))?
        .into_inner();

    if !crate::paths::is_safe_id(&response.agent_id) {
        bail!(
            "the server returned an unusable agent ID {:?}",
            response.agent_id
        );
    }
    if !response.ca_certificate_der.is_empty() && response.ca_certificate_der != ca_der {
        bail!("the server returned a different CA than the one the token pins");
    }
    let info = check_issued_cert(&response.certificate_der, &key)?;
    if info.common_name.as_deref() != Some(response.agent_id.as_str()) {
        tracing::warn!(
            "the certificate's CN {:?} is not the agent ID {}",
            info.common_name,
            response.agent_id
        );
    }

    // 3. Store it.
    let record = AgentRecord {
        agent_id: response.agent_id,
        server_url: server.to_string(),
        name,
        enrolled_at_ms: identity::now_ms(),
    };
    identity::store_new(
        &paths,
        &record,
        &key.serialize_pem(),
        &tls::cert_pem(&response.certificate_der),
        &ca_pem,
    )?;
    Ok(record)
}

#[cfg(test)]
mod tests {
    use super::*;

    const FP: &str = "9f86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a08";

    #[test]
    fn parses_tokens() {
        let t = EnrollmentToken::parse(&format!("opy1.c2VjcmV0LXZhbHVl_-x.{}", FP.to_uppercase()))
            .unwrap();
        assert_eq!(t.secret, "c2VjcmV0LXZhbHVl_-x");
        assert_eq!(t.fingerprint, FP);
        assert!(!format!("{t:?}").contains("c2VjcmV0"));

        for bad in [
            "opy2.abc.".to_owned() + FP,
            "opy1.abc".to_owned(),
            format!("opy1..{FP}"),
            format!("opy1.ab=c.{FP}"),
            "opy1.abc.1234".to_owned(),
            format!("opy1.abc.{FP}.x"),
        ] {
            assert!(EnrollmentToken::parse(&bad).is_err(), "{bad}");
        }
    }

    #[test]
    fn csr_and_cert_checks() {
        let (key, csr) = new_key_and_csr("host-1").unwrap();
        assert!(!csr.is_empty());
        let mut params = CertificateParams::new(Vec::<String>::new()).unwrap();
        params
            .distinguished_name
            .push(DnType::CommonName, "agent-x");
        let cert = params.self_signed(&key).unwrap();
        let info = check_issued_cert(cert.der(), &key).unwrap();
        assert_eq!(info.common_name.as_deref(), Some("agent-x"));
        let other = KeyPair::generate_for(&rcgen::PKCS_ECDSA_P256_SHA256).unwrap();
        assert!(check_issued_cert(cert.der(), &other).is_err());
    }
}
