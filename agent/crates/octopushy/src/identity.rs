//! The agent's stored enrollment: `agent.json`, `key.pem`, `cert.pem`, `ca.pem`.
//!
//! Renewal replaces the key and certificate as a pair. Both are first written as
//! `*.next` files, then renamed key first; if the agent dies in between, the next
//! load finishes the switch, so the key and certificate on disk always match.

use std::fs;
use std::time::{SystemTime, UNIX_EPOCH};

use anyhow::{Context, Result, bail};
use serde::{Deserialize, Serialize};

use crate::fsutil;
use crate::paths::StatePaths;
use crate::tls::CertInfo;

/// `agent.json`.
#[derive(Debug, Clone, Serialize, Deserialize, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub struct AgentRecord {
    pub agent_id: String,
    pub server_url: String,
    pub name: String,
    pub enrolled_at_ms: u64,
}

/// Everything needed to connect.
#[derive(Clone)]
pub struct Identity {
    pub record: AgentRecord,
    pub key_pem: String,
    pub cert_pem: String,
    pub ca_pem: String,
    pub cert: CertInfo,
}

impl std::fmt::Debug for Identity {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("Identity")
            .field("record", &self.record)
            .field("cert", &self.cert)
            .finish_non_exhaustive()
    }
}

/// Whether `paths` holds an enrollment (or the start of one).
pub fn exists(paths: &StatePaths) -> bool {
    paths.agent_json().exists() || paths.key().exists() || paths.cert().exists()
}

/// Stores a fresh enrollment. Writes the key, certificate and CA first and
/// `agent.json` last, so a half-written enrollment never looks complete.
pub fn store_new(
    paths: &StatePaths,
    record: &AgentRecord,
    key_pem: &str,
    cert_pem: &str,
    ca_pem: &str,
) -> Result<()> {
    fsutil::ensure_private_dir(&paths.root)?;
    let _ = fsutil::remove_durably(&paths.key_next());
    let _ = fsutil::remove_durably(&paths.cert_next());
    fsutil::write_atomic(&paths.key(), key_pem.as_bytes(), 0o600)?;
    fsutil::write_atomic(&paths.cert(), cert_pem.as_bytes(), 0o644)?;
    fsutil::write_atomic(&paths.ca(), ca_pem.as_bytes(), 0o644)?;
    let json = serde_json::to_vec_pretty(record)?;
    fsutil::write_atomic(&paths.agent_json(), &json, 0o600)?;
    Ok(())
}

/// Replaces the key and certificate after a renewal.
pub fn replace_key_and_cert(paths: &StatePaths, key_pem: &str, cert_pem: &str) -> Result<()> {
    fsutil::write_atomic(&paths.key_next(), key_pem.as_bytes(), 0o600)?;
    fsutil::write_atomic(&paths.cert_next(), cert_pem.as_bytes(), 0o644)?;
    finish_switch(paths)
}

/// Completes an interrupted key/certificate switch, if any.
fn finish_switch(paths: &StatePaths) -> Result<()> {
    let key_next = paths.key_next();
    let cert_next = paths.cert_next();
    if !cert_next.exists() {
        // Nothing to finish; a lone key.pem.next means the switch never started.
        let _ = fsutil::remove_durably(&key_next);
        return Ok(());
    }
    if key_next.exists() {
        // Only switch if the new pair is consistent.
        let key = fs::read_to_string(&key_next)?;
        let cert = fs::read_to_string(&cert_next)?;
        if !key_matches_cert(&key, &cert)? {
            tracing::warn!("discarding an inconsistent pending certificate switch");
            fsutil::remove_durably(&key_next)?;
            fsutil::remove_durably(&cert_next)?;
            return Ok(());
        }
        fs::rename(&key_next, paths.key())?;
    }
    fs::rename(&cert_next, paths.cert())?;
    fsutil::sync_dir(&paths.root)?;
    Ok(())
}

fn key_matches_cert(key_pem: &str, cert_pem: &str) -> Result<bool> {
    let key = rcgen::KeyPair::from_pem(key_pem).context("invalid private key")?;
    let der = crate::tls::single_cert_der(cert_pem)?;
    let info = CertInfo::parse(&der)?;
    Ok(info.public_key == key.public_key_raw())
}

/// Loads the enrollment, finishing an interrupted renewal first.
pub fn load(paths: &StatePaths) -> Result<Identity> {
    if !paths.agent_json().exists() {
        bail!(
            "this agent is not enrolled (no {}); run `octopushy agent enroll` first",
            paths.agent_json().display()
        );
    }
    finish_switch(paths)?;
    let record: AgentRecord = serde_json::from_slice(&fs::read(paths.agent_json())?)
        .with_context(|| format!("reading {}", paths.agent_json().display()))?;
    let read = |p: std::path::PathBuf| {
        fs::read_to_string(&p).with_context(|| format!("reading {}", p.display()))
    };
    let key_pem = read(paths.key())?;
    let cert_pem = read(paths.cert())?;
    let ca_pem = read(paths.ca())?;
    warn_if_readable_by_others(&paths.key());
    if !key_matches_cert(&key_pem, &cert_pem)? {
        bail!("key.pem does not match cert.pem; enroll again with --force");
    }
    let cert = CertInfo::parse(&crate::tls::single_cert_der(&cert_pem)?)?;
    Ok(Identity {
        record,
        key_pem,
        cert_pem,
        ca_pem,
        cert,
    })
}

fn warn_if_readable_by_others(path: &std::path::Path) {
    use std::os::unix::fs::PermissionsExt;
    if let Ok(meta) = fs::metadata(path)
        && meta.permissions().mode() & 0o077 != 0
    {
        tracing::warn!(
            "{} is accessible by other users; it should be mode 0600",
            path.display()
        );
    }
}

pub fn now_ms() -> u64 {
    SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .map(|d| d.as_millis() as u64)
        .unwrap_or(0)
}

#[cfg(test)]
mod tests {
    use super::*;

    fn pair(cn: &str) -> (String, String) {
        let key = rcgen::KeyPair::generate_for(&rcgen::PKCS_ECDSA_P256_SHA256).unwrap();
        let mut params = rcgen::CertificateParams::new(Vec::<String>::new()).unwrap();
        params
            .distinguished_name
            .push(rcgen::DnType::CommonName, cn);
        let cert = params.self_signed(&key).unwrap();
        (key.serialize_pem(), cert.pem())
    }

    fn record() -> AgentRecord {
        AgentRecord {
            agent_id: "a1".into(),
            server_url: "https://127.0.0.1:8443/".into(),
            name: "host".into(),
            enrolled_at_ms: 1,
        }
    }

    #[test]
    fn store_load_and_replace() {
        let dir = tempfile::tempdir().unwrap();
        let paths = StatePaths::new(dir.path().join("state"));
        assert!(!exists(&paths));
        let (key, cert) = pair("a1");
        let (_, ca) = pair("ca");
        store_new(&paths, &record(), &key, &cert, &ca).unwrap();
        assert!(exists(&paths));
        let id = load(&paths).unwrap();
        assert_eq!(id.record, record());
        assert_eq!(id.cert.common_name.as_deref(), Some("a1"));

        let (key2, cert2) = pair("a1");
        replace_key_and_cert(&paths, &key2, &cert2).unwrap();
        let id2 = load(&paths).unwrap();
        assert_eq!(id2.key_pem, key2);
        assert_eq!(id2.cert_pem, cert2);
        assert!(!paths.key_next().exists() && !paths.cert_next().exists());
    }

    #[test]
    fn finishes_an_interrupted_switch() {
        let dir = tempfile::tempdir().unwrap();
        let paths = StatePaths::new(dir.path());
        let (key, cert) = pair("a1");
        store_new(&paths, &record(), &key, &cert, &cert).unwrap();

        // Crash after the key was renamed but before the certificate was.
        let (key2, cert2) = pair("a1");
        fs::write(paths.key(), &key2).unwrap();
        fs::write(paths.cert_next(), &cert2).unwrap();
        let id = load(&paths).unwrap();
        assert_eq!(id.cert_pem, cert2);

        // Crash before either rename: both .next files exist.
        let (key3, cert3) = pair("a1");
        fs::write(paths.key_next(), &key3).unwrap();
        fs::write(paths.cert_next(), &cert3).unwrap();
        let id = load(&paths).unwrap();
        assert_eq!(id.key_pem, key3);

        // A lone key.pem.next is discarded.
        let (key4, _) = pair("a1");
        fs::write(paths.key_next(), &key4).unwrap();
        let id = load(&paths).unwrap();
        assert_eq!(id.key_pem, key3);
        assert!(!paths.key_next().exists());
    }

    #[test]
    fn refuses_mismatched_pair() {
        let dir = tempfile::tempdir().unwrap();
        let paths = StatePaths::new(dir.path());
        let (key, _) = pair("a1");
        let (_, cert) = pair("a1");
        store_new(&paths, &record(), &key, &cert, &cert).unwrap();
        assert!(load(&paths).is_err());
    }
}
