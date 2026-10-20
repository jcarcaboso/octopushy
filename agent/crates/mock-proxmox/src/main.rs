//! `mock-proxmox --listen 127.0.0.1:18006 --fixture fixture.json [--tls-self-signed]`

use std::net::SocketAddr;
use std::path::PathBuf;

use anyhow::Context;
use clap::Parser;
use mock_proxmox::{Fixture, Options};

/// A fake Proxmox VE API for tests and end-to-end runs.
#[derive(Debug, Parser)]
#[command(version)]
struct Args {
    /// Address to listen on.
    #[arg(long, default_value = "127.0.0.1:18006")]
    listen: SocketAddr,
    /// Fixture JSON file; the built-in example (fixtures/home.json) when omitted.
    #[arg(long)]
    fixture: Option<PathBuf>,
    /// Serve HTTPS with a freshly generated CA and server certificate.
    #[arg(long)]
    tls_self_signed: bool,
    /// Write the generated CA certificate (PEM) here, for `caCertificatePem`.
    #[arg(long, requires = "tls_self_signed")]
    ca_out: Option<PathBuf>,
}

#[tokio::main]
async fn main() -> anyhow::Result<()> {
    use std::io::IsTerminal;
    tracing_subscriber::fmt()
        .with_writer(std::io::stderr)
        .with_ansi(std::io::stderr().is_terminal())
        .with_env_filter(
            tracing_subscriber::EnvFilter::try_from_default_env().unwrap_or_else(|_| "info".into()),
        )
        .init();
    let args = Args::parse();

    let fixture = match &args.fixture {
        Some(path) => {
            let json = std::fs::read_to_string(path)
                .with_context(|| format!("reading {}", path.display()))?;
            Fixture::from_json(&json).with_context(|| format!("parsing {}", path.display()))?
        }
        None => Fixture::example(),
    };
    let token_id = fixture
        .token
        .split_once('=')
        .map(|(id, _)| id.to_owned())
        .unwrap_or_default();
    let (nodes, vms) = (fixture.nodes.len(), fixture.vms.len());

    let mock = mock_proxmox::start(
        fixture,
        Options {
            listen: args.listen,
            tls: args.tls_self_signed,
        },
    )
    .await?;

    if let (Some(path), Some(pem)) = (&args.ca_out, mock.ca_pem()) {
        std::fs::write(path, pem).with_context(|| format!("writing {}", path.display()))?;
        tracing::info!("wrote the CA certificate to {}", path.display());
    }
    tracing::info!(
        "mock-proxmox serving {nodes} nodes and {vms} VMs on {} (token id {token_id})",
        mock.endpoint()
    );

    tokio::select! {
        _ = mock.wait() => {}
        _ = tokio::signal::ctrl_c() => tracing::info!("stopping"),
    }
    Ok(())
}
