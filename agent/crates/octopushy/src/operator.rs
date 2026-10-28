//! Controller operator credentials are separate from execution-agent identities.
use crate::{cli::OperatorCommand, fsutil};
use anyhow::{Context, Result, bail, ensure};
use reqwest::{
    Client, Method, StatusCode,
    header::{AUTHORIZATION, HeaderValue},
};
use serde::{Deserialize, Serialize};
use serde_json::Value;
use std::{
    fs,
    io::Read,
    os::unix::fs::MetadataExt,
    path::{Path, PathBuf},
    time::Duration,
};

#[derive(Deserialize, Serialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct Credential {
    server: String,
    token: String,
    ca_certificate_pem: Option<String>,
}

fn origin(value: &str) -> Result<url::Url> {
    let url = url::Url::parse(value).context("invalid controller URL")?;
    ensure!(
        url.scheme() == "https"
            && url.host_str().is_some()
            && url.username().is_empty()
            && url.password().is_none()
            && url.path() == "/"
            && url.query().is_none()
            && url.fragment().is_none(),
        "controller must be an HTTPS origin without credentials, path, query or fragment"
    );
    Ok(url)
}

fn private(path: &Path, directory: bool) -> Result<()> {
    let meta = fs::symlink_metadata(path)?;
    ensure!(
        (if directory {
            meta.is_dir()
        } else {
            meta.is_file() && meta.nlink() == 1
        }) && meta.uid() == unsafe { libc::geteuid() }
            && meta.mode() & 0o077 == 0,
        "operator credential paths must be private, owned by this user, and not symlinks"
    );
    Ok(())
}

fn load(path: &Path) -> Result<Credential> {
    private(
        path.parent()
            .context("credential requires a private directory")?,
        true,
    )?;
    private(path, false)?;
    let bytes = fs::read(path)?;
    ensure!(bytes.len() <= 32768, "invalid operator credential file");
    serde_json::from_slice(&bytes).map_err(|_| anyhow::anyhow!("invalid operator credential file"))
}

fn client(credential: &Credential) -> Result<Client> {
    origin(&credential.server)?;
    let _ = rustls::crypto::ring::default_provider().install_default();
    ensure!(
        credential.token.len() == 104
            && credential.token.starts_with("opy_op_")
            && !credential.token.chars().any(char::is_whitespace),
        "invalid operator credential"
    );
    let mut auth = HeaderValue::from_str(&format!("Bearer {}", credential.token))
        .map_err(|_| anyhow::anyhow!("invalid operator credential"))?;
    auth.set_sensitive(true);
    let mut builder = Client::builder()
        .https_only(true)
        .redirect(reqwest::redirect::Policy::none())
        .retry(reqwest::retry::never())
        .connect_timeout(Duration::from_secs(5))
        .timeout(Duration::from_secs(30))
        .default_headers([(AUTHORIZATION, auth)].into_iter().collect());
    if let Some(pem) = &credential.ca_certificate_pem {
        ensure!(
            pem.len() <= 16384 && !pem.contains("PRIVATE KEY"),
            "invalid controller public CA"
        );
        builder = builder.add_root_certificate(
            reqwest::Certificate::from_pem(pem.as_bytes())
                .map_err(|_| anyhow::anyhow!("invalid controller public CA"))?,
        );
    }
    builder.build().context("create HTTPS operator client")
}

async fn request(
    credential: &Credential,
    method: &str,
    path: &str,
    body: Option<Value>,
) -> Result<(StatusCode, Vec<u8>)> {
    let base = origin(&credential.server)?;
    ensure!(
        path.starts_with("/api/") && !path.contains('#'),
        "use a controller-relative /api/ path"
    );
    let url = base.join(path)?;
    ensure!(
        url.origin() == base.origin() && url.path().starts_with("/api/"),
        "refusing an API request outside the configured controller"
    );
    let mut call = client(credential)?
        .request(Method::from_bytes(method.as_bytes())?, url)
        .header("Origin", base.origin().ascii_serialization());
    if let Some(body) = body {
        call = call.json(&body);
    }
    let mut response = call
        .send()
        .await
        .context("controller request failed; no action was retried")?;
    let status = response.status();
    let mut bytes = Vec::new();
    while let Some(chunk) = response.chunk().await? {
        ensure!(
            bytes.len() + chunk.len() <= 8 * 1024 * 1024,
            "controller reply too large"
        );
        bytes.extend_from_slice(&chunk);
    }
    Ok((status, bytes))
}

fn success(status: StatusCode) -> Result<()> {
    if status == StatusCode::UNAUTHORIZED {
        bail!(
            "operator credential expired, was revoked, or is invalid; authorize a new grant in the console"
        );
    }
    ensure!(
        status.is_success(),
        "controller returned HTTP {}; no action was retried",
        status.as_u16()
    );
    Ok(())
}

fn stdin(limit: usize) -> Result<Vec<u8>> {
    let mut data = Vec::new();
    std::io::stdin()
        .lock()
        .take(limit as u64 + 1)
        .read_to_end(&mut data)?;
    ensure!(data.len() <= limit, "stdin exceeds the limit");
    Ok(data)
}

fn output(bytes: &[u8]) -> Result<()> {
    if !bytes.is_empty() {
        let value: Value =
            serde_json::from_slice(bytes).context("controller did not return JSON")?;
        println!("{}", serde_json::to_string_pretty(&value)?);
    }
    Ok(())
}

pub async fn run(path: Option<PathBuf>, command: OperatorCommand) -> Result<()> {
    let path = match path {
        Some(path) => path,
        None => std::env::var_os("XDG_CONFIG_HOME")
            .map(PathBuf::from)
            .or_else(|| std::env::var_os("HOME").map(|home| PathBuf::from(home).join(".config")))
            .context("set HOME or XDG_CONFIG_HOME, or pass --config")?
            .join("octopushy/operator/credential.json"),
    };
    match command {
        OperatorCommand::Login {
            server,
            token_stdin: _,
            ca_file,
            replace,
        } => {
            let base = origin(&server)?;
            ensure!(
                replace || !path.exists(),
                "an operator credential already exists; logout first or explicitly use --replace"
            );
            let raw = stdin(4096)?;
            let token = std::str::from_utf8(&raw)
                .map_err(|_| anyhow::anyhow!("invalid operator credential"))?
                .trim()
                .to_owned();
            let credential = Credential {
                server: base.origin().ascii_serialization(),
                token,
                ca_certificate_pem: ca_file.map(fs::read_to_string).transpose()?,
            };
            let (status, data) = request(&credential, "GET", "/api/auth/state", None).await?;
            success(status)?;
            let auth: Value = serde_json::from_slice(&data)?;
            ensure!(
                auth["authenticated"] == true,
                "operator grant was not authenticated"
            );
            let directory = path
                .parent()
                .context("credential requires a private directory")?;
            fsutil::ensure_private_dir(directory)?;
            private(directory, true)?;
            if path.exists() {
                private(&path, false)?;
            }
            fsutil::write_atomic(&path, &serde_json::to_vec(&credential)?, 0o600)?;
            println!("Operator grant saved privately. No password was stored.");
            Ok(())
        }
        OperatorCommand::Logout => {
            let credential = load(&path).context("no usable operator login")?;
            let (status, _) = request(
                &credential,
                "DELETE",
                "/api/auth/operator-tokens/self",
                None,
            )
            .await?;
            if status != StatusCode::UNAUTHORIZED {
                success(status)?;
            }
            fsutil::remove_durably(&path)?;
            println!("Operator grant revoked or already invalid; local credential removed.");
            Ok(())
        }
        OperatorCommand::Whoami => {
            let credential = load(&path).context("no usable operator login")?;
            let (status, data) = request(&credential, "GET", "/api/auth/state", None).await?;
            success(status)?;
            let auth: Value = serde_json::from_slice(&data)?;
            ensure!(
                auth["authenticated"] == true,
                "operator grant is not authenticated"
            );
            output(&data)
        }
        OperatorCommand::Request {
            path: api_path,
            method,
            json_stdin,
        } => {
            let credential = load(&path).context("no usable operator login")?;
            let body = if json_stdin {
                Some(
                    serde_json::from_slice(&stdin(1024 * 1024)?)
                        .map_err(|_| anyhow::anyhow!("invalid JSON stdin"))?,
                )
            } else {
                None
            };
            let (status, data) = request(&credential, &method, &api_path, body).await?;
            success(status)?;
            output(&data)
        }
    }
}
