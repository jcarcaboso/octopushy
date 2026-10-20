//! Certificate renewal over the current mutual-TLS connection.

use anyhow::{Context, Result};
use octopushy_proto::v1::{RenewCertificateRequest, agent_channel_client::AgentChannelClient};
use tonic::transport::Channel;

use crate::enrollment::{check_issued_cert, new_key_and_csr};
use crate::identity::{self, Identity};
use crate::paths::StatePaths;
use crate::tls;

/// Requests a certificate for a new key and stores the pair. The caller then
/// reconnects so the new identity is used.
pub async fn renew(channel: Channel, paths: &StatePaths, current: &Identity) -> Result<()> {
    let (key, csr_der) = new_key_and_csr(&current.record.agent_id)?;
    let response = AgentChannelClient::new(channel)
        .renew_certificate(RenewCertificateRequest { csr_der })
        .await
        .map_err(|s| {
            anyhow::anyhow!(
                "the server refused to renew the certificate: {}",
                s.message()
            )
        })?
        .into_inner();
    let info = check_issued_cert(&response.certificate_der, &key)?;
    identity::replace_key_and_cert(
        paths,
        &key.serialize_pem(),
        &tls::cert_pem(&response.certificate_der),
    )
    .context("storing the renewed certificate")?;
    tracing::info!(
        not_after = ?humantime(info.not_after),
        "renewed the agent certificate"
    );
    Ok(())
}

/// Seconds since the epoch, for logs.
pub fn humantime(t: std::time::SystemTime) -> u64 {
    t.duration_since(std::time::UNIX_EPOCH)
        .map(|d| d.as_secs())
        .unwrap_or(0)
}
