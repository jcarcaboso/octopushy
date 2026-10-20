//! TLS settings for Proxmox connections, built on rustls with the `ring` provider.

use std::sync::Arc;

use rustls::client::danger::{HandshakeSignatureValid, ServerCertVerified, ServerCertVerifier};
use rustls::crypto::{CryptoProvider, WebPkiSupportedAlgorithms};
use rustls::pki_types::{CertificateDer, ServerName, UnixTime, pem::PemObject};
use rustls::{ClientConfig, DigitallySignedStruct, RootCertStore, SignatureScheme};

use crate::error::Error;

/// How the client checks the Proxmox server certificate.
#[derive(Clone, PartialEq, Eq)]
pub enum Tls {
    /// Verify against the operating system's trust store.
    Verify,
    /// Accept any certificate. Only for installations with self-signed
    /// certificates where the user explicitly turned verification off.
    Insecure,
    /// Verify against this PEM bundle only (one or more certificates).
    CustomCa(String),
}

impl std::fmt::Debug for Tls {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        match self {
            Tls::Verify => f.write_str("Verify"),
            Tls::Insecure => f.write_str("Insecure"),
            Tls::CustomCa(_) => f.write_str("CustomCa(..)"),
        }
    }
}

pub(crate) fn provider() -> Arc<CryptoProvider> {
    Arc::new(rustls::crypto::ring::default_provider())
}

/// Builds the rustls configuration for `tls`.
pub(crate) fn client_config(tls: &Tls) -> Result<ClientConfig, Error> {
    let provider = provider();
    let builder = ClientConfig::builder_with_provider(provider.clone())
        .with_safe_default_protocol_versions()
        .map_err(|e| Error::Config(format!("TLS setup failed: {e}")))?;

    let mut config = match tls {
        Tls::Verify => {
            use rustls_platform_verifier::BuilderVerifierExt;
            builder
                .with_platform_verifier()
                .map_err(|e| Error::Config(format!("cannot load the system trust store: {e}")))?
                .with_no_client_auth()
        }
        Tls::Insecure => builder
            .dangerous()
            .with_custom_certificate_verifier(Arc::new(AcceptAnyCertificate::new(&provider)))
            .with_no_client_auth(),
        Tls::CustomCa(pem) => {
            let mut roots = RootCertStore::empty();
            let mut count = 0usize;
            for cert in CertificateDer::pem_slice_iter(pem.as_bytes()) {
                let cert =
                    cert.map_err(|e| Error::Config(format!("invalid CA certificate PEM: {e}")))?;
                roots
                    .add(cert)
                    .map_err(|e| Error::Config(format!("unusable CA certificate: {e}")))?;
                count += 1;
            }
            if count == 0 {
                return Err(Error::Config(
                    "the CA certificate PEM contains no certificate".into(),
                ));
            }
            builder.with_root_certificates(roots).with_no_client_auth()
        }
    };
    config.alpn_protocols = vec![b"h2".to_vec(), b"http/1.1".to_vec()];
    Ok(config)
}

/// A verifier that accepts any server certificate but still checks that the
/// handshake is signed by the presented key, so the connection is encrypted to
/// whoever holds that key. Used only when verification is explicitly disabled.
#[derive(Debug)]
pub struct AcceptAnyCertificate {
    algorithms: WebPkiSupportedAlgorithms,
}

impl AcceptAnyCertificate {
    /// Creates the verifier for the signature algorithms of `provider`.
    pub fn new(provider: &CryptoProvider) -> Self {
        Self {
            algorithms: provider.signature_verification_algorithms,
        }
    }
}

impl ServerCertVerifier for AcceptAnyCertificate {
    fn verify_server_cert(
        &self,
        _end_entity: &CertificateDer<'_>,
        _intermediates: &[CertificateDer<'_>],
        _server_name: &ServerName<'_>,
        _ocsp_response: &[u8],
        _now: UnixTime,
    ) -> Result<ServerCertVerified, rustls::Error> {
        Ok(ServerCertVerified::assertion())
    }

    fn verify_tls12_signature(
        &self,
        message: &[u8],
        cert: &CertificateDer<'_>,
        dss: &DigitallySignedStruct,
    ) -> Result<HandshakeSignatureValid, rustls::Error> {
        rustls::crypto::verify_tls12_signature(message, cert, dss, &self.algorithms)
    }

    fn verify_tls13_signature(
        &self,
        message: &[u8],
        cert: &CertificateDer<'_>,
        dss: &DigitallySignedStruct,
    ) -> Result<HandshakeSignatureValid, rustls::Error> {
        rustls::crypto::verify_tls13_signature(message, cert, dss, &self.algorithms)
    }

    fn supported_verify_schemes(&self) -> Vec<SignatureScheme> {
        self.algorithms.supported_schemes()
    }
}
