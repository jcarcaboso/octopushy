//! Fixed, generation-bound bootstrap. Only the public CSR/certificate crosses QGA.
use crate::{enrollment, fsutil, identity, paths::StatePaths, tls};
use anyhow::{Context, Result, bail};
use base64::{Engine, engine::general_purpose::STANDARD};
use serde::{Deserialize, Serialize};
use serde_json::{Value, json};
use std::{
    fs::{self, OpenOptions},
    os::{fd::AsRawFd, unix::fs::OpenOptionsExt},
    time::{SystemTime, UNIX_EPOCH},
};

#[derive(Serialize, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct Prepared {
    generation: String,
    key_pem: String,
    csr_der: String,
    install: Option<Payload>,
}
#[derive(Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct Payload {
    protocol_version: u32,
    generation: String,
    agent_id: String,
    name: String,
    server_url: String,
    certificate_pem: String,
    ca_certificate_pem: String,
}
pub fn valid_uuid(value: &str) -> bool {
    value.len() == 36
        && value.bytes().enumerate().all(|(i, b)| {
            if [8, 13, 18, 23].contains(&i) {
                b == b'-'
            } else {
                b.is_ascii_digit() || (b'a'..=b'f').contains(&b)
            }
        })
        && value != "00000000-0000-0000-0000-000000000000"
}
fn lock(paths: &StatePaths, generation: &str) -> Result<fs::File> {
    if !valid_uuid(generation) {
        bail!("generation must be a nonzero canonical UUID");
    }
    if fs::symlink_metadata(&paths.root).is_ok_and(|m| m.file_type().is_symlink()) {
        bail!("state directory must not be a symlink");
    }
    fsutil::ensure_private_dir(&paths.root)?;
    let file = OpenOptions::new()
        .read(true)
        .write(true)
        .create(true)
        .truncate(false)
        .mode(0o600)
        .custom_flags(libc::O_NOFOLLOW)
        .open(paths.root.join("managed-identity.lock"))?;
    if unsafe { libc::flock(file.as_raw_fd(), libc::LOCK_EX | libc::LOCK_NB) } != 0 {
        bail!("managed identity is busy; inspect before retry");
    }
    Ok(file)
}
fn load(paths: &StatePaths, generation: &str) -> Result<Prepared> {
    let prepared: Prepared =
        serde_json::from_slice(&fs::read(paths.root.join("managed-identity.json"))?)?;
    if prepared.generation != generation {
        bail!("state belongs to another worker generation");
    }
    Ok(prepared)
}
fn save(paths: &StatePaths, prepared: &Prepared) -> Result<()> {
    fsutil::write_atomic(
        &paths.root.join("managed-identity.json"),
        &serde_json::to_vec(prepared)?,
        0o600,
    )
}
pub fn prepare(paths: &StatePaths, generation: &str) -> Result<Value> {
    let _lock = lock(paths, generation)?;
    let path = paths.root.join("managed-identity.json");
    let prepared = if path.exists() {
        load(paths, generation)?
    } else {
        if identity::exists(paths) {
            bail!("existing identity cannot be claimed as a managed worker");
        }
        let (key, csr) = enrollment::new_key_and_csr("managed-worker")?;
        let prepared = Prepared {
            generation: generation.into(),
            key_pem: key.serialize_pem(),
            csr_der: STANDARD.encode(csr),
            install: None,
        };
        save(paths, &prepared)?;
        prepared
    };
    Ok(
        json!({"protocolVersion":1,"generation":generation,"csrDer":prepared.csr_der,"agentVersion":crate::VERSION}),
    )
}
pub fn install(paths: &StatePaths, generation: &str, payload: &str) -> Result<Value> {
    let _lock = lock(paths, generation)?;
    if payload.len() > 32768 {
        bail!("public identity payload is too large");
    }
    let payload: Payload =
        serde_json::from_str(payload).context("invalid public identity payload")?;
    if payload.protocol_version != 1
        || payload.generation != generation
        || !valid_uuid(&payload.agent_id)
        || payload.name.is_empty()
        || payload.name.len() > 128
    {
        bail!("invalid public identity fields");
    }
    let url = tls::parse_server_url(&payload.server_url)?;
    if url.fragment().is_some() {
        bail!("server URL must not contain a fragment");
    }
    let mut prepared = load(paths, generation)?;
    if prepared.install.as_ref().is_some_and(|old| old != &payload) {
        bail!("identity replacement is forbidden");
    }
    let key = rcgen::KeyPair::from_pem(&prepared.key_pem)?;
    let der = tls::single_cert_der(&payload.certificate_pem)?;
    let info = enrollment::check_issued_cert(&der, &key)?;
    if info.common_name.as_deref() != Some(&payload.agent_id) || info.not_before > SystemTime::now()
    {
        bail!("certificate does not identify this agent or is not yet valid");
    }
    let ca = tls::single_cert_der(&payload.ca_certificate_pem)?;
    let mut roots = rustls::RootCertStore::empty();
    roots.add(ca.into())?;
    let verifier = rustls::server::WebPkiClientVerifier::builder_with_provider(
        std::sync::Arc::new(roots),
        std::sync::Arc::new(rustls::crypto::ring::default_provider()),
    )
    .build()?;
    verifier
        .verify_client_cert(&der.into(), &[], rustls::pki_types::UnixTime::now())
        .context("certificate is not trusted for client authentication")?;
    if paths.agent_json().exists() {
        let existing = identity::load(paths)?;
        if existing.record.agent_id != payload.agent_id
            || existing.record.server_url != payload.server_url
            || existing.record.name != payload.name
            || existing.key_pem != prepared.key_pem
            || existing.cert_pem != payload.certificate_pem
            || existing.ca_pem != payload.ca_certificate_pem
        {
            bail!("existing identity replacement is forbidden");
        }
    } else {
        for (path, expected) in [
            (paths.key(), prepared.key_pem.as_str()),
            (paths.cert(), payload.certificate_pem.as_str()),
            (paths.ca(), payload.ca_certificate_pem.as_str()),
        ] {
            match fs::read_to_string(path) {
                Ok(existing) if existing != expected => {
                    bail!("partial identity replacement is forbidden")
                }
                Err(error) if error.kind() != std::io::ErrorKind::NotFound => {
                    return Err(error.into());
                }
                _ => {}
            }
        }
        // Persist the exact accepted public payload first; a crash can only resume it.
        prepared.install = Some(payload.clone());
        save(paths, &prepared)?;
        let record = identity::AgentRecord {
            agent_id: payload.agent_id.clone(),
            server_url: payload.server_url.clone(),
            name: payload.name,
            enrolled_at_ms: SystemTime::now().duration_since(UNIX_EPOCH)?.as_millis() as u64,
        };
        identity::store_new(
            paths,
            &record,
            &prepared.key_pem,
            &payload.certificate_pem,
            &payload.ca_certificate_pem,
        )?;
    }
    Ok(
        json!({"protocolVersion":1,"generation":generation,"agentId":payload.agent_id,"installed":true}),
    )
}
