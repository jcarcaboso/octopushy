//! HTTPS for the mock: a generated CA (like `pve-root-ca`) and a server
//! certificate it signs, served with rustls and the `ring` provider.

use std::net::IpAddr;
use std::sync::Arc;

use axum::Router;
use hyper_util::rt::{TokioExecutor, TokioIo};
use hyper_util::server::conn::auto;
use hyper_util::service::TowerToHyperService;
use rcgen::{
    BasicConstraints, CertificateParams, DnType, ExtendedKeyUsagePurpose, IsCa, Issuer, KeyPair,
    KeyUsagePurpose, SanType,
};
use rustls_pki_types::{CertificateDer, PrivateKeyDer, PrivatePkcs8KeyDer};
use tokio::net::TcpListener;
use tokio_rustls::TlsAcceptor;

pub(crate) struct Identity {
    pub ca_pem: String,
    cert_der: CertificateDer<'static>,
    key_der: Vec<u8>,
}

/// Generates a CA and a server certificate valid for `localhost`, `127.0.0.1`,
/// `::1`, and `ip`.
pub(crate) fn generate(ip: IpAddr) -> anyhow::Result<Identity> {
    let ca_key = KeyPair::generate_for(&rcgen::PKCS_ECDSA_P256_SHA256)?;
    let mut ca_params = CertificateParams::new(Vec::<String>::new())?;
    ca_params
        .distinguished_name
        .push(DnType::CommonName, "mock-proxmox Root CA");
    ca_params.is_ca = IsCa::Ca(BasicConstraints::Unconstrained);
    ca_params.key_usages = vec![KeyUsagePurpose::KeyCertSign, KeyUsagePurpose::CrlSign];
    let ca_cert = ca_params.self_signed(&ca_key)?;
    let issuer = Issuer::new(ca_params, ca_key);

    let key = KeyPair::generate_for(&rcgen::PKCS_ECDSA_P256_SHA256)?;
    let mut params = CertificateParams::new(vec!["localhost".to_owned()])?;
    params
        .distinguished_name
        .push(DnType::CommonName, "mock-proxmox");
    let mut ips = vec![
        IpAddr::from([127, 0, 0, 1]),
        IpAddr::from([0u16, 0, 0, 0, 0, 0, 0, 1]),
    ];
    if !ip.is_unspecified() && !ips.contains(&ip) {
        ips.push(ip);
    }
    params
        .subject_alt_names
        .extend(ips.into_iter().map(SanType::IpAddress));
    params.extended_key_usages = vec![ExtendedKeyUsagePurpose::ServerAuth];
    let cert = params.signed_by(&key, &issuer)?;

    Ok(Identity {
        ca_pem: ca_cert.pem(),
        cert_der: cert.der().clone(),
        key_der: key.serialize_der(),
    })
}

pub(crate) async fn serve(
    listener: TcpListener,
    identity: Identity,
    app: Router,
) -> anyhow::Result<()> {
    let provider = Arc::new(rustls::crypto::ring::default_provider());
    let mut config = rustls::ServerConfig::builder_with_provider(provider)
        .with_safe_default_protocol_versions()?
        .with_no_client_auth()
        .with_single_cert(
            vec![identity.cert_der],
            PrivateKeyDer::Pkcs8(PrivatePkcs8KeyDer::from(identity.key_der)),
        )?;
    // Proxmox's pveproxy speaks HTTP/1.1 only.
    config.alpn_protocols = vec![b"http/1.1".to_vec()];
    let acceptor = TlsAcceptor::from(Arc::new(config));

    loop {
        let (stream, _) = listener.accept().await?;
        let acceptor = acceptor.clone();
        let app = app.clone();
        tokio::spawn(async move {
            let Ok(tls) = acceptor.accept(stream).await else {
                return;
            };
            let service = TowerToHyperService::new(app);
            let _ = auto::Builder::new(TokioExecutor::new())
                .serve_connection(TokioIo::new(tls), service)
                .await;
        });
    }
}
