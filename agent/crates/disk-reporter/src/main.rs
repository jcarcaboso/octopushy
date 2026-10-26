//! A timer invokes this process once; there is no listener, sampling loop, or outbox.
#[cfg(not(target_os = "linux"))]
compile_error!("the disk reporter supports Linux guests only");

use std::{
    fs,
    path::{Path, PathBuf},
    time::Duration,
};

use anyhow::{Context, Result, bail, ensure};
use serde::{Deserialize, Serialize};

#[derive(Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct Config {
    endpoint: String,
    #[serde(default = "default_interval")]
    interval_seconds: u32,
    ca_certificate_file: Option<PathBuf>,
}

fn default_interval() -> u32 {
    3600
}

impl Config {
    fn read(path: &Path) -> Result<Self> {
        let config: Self = serde_json::from_slice(&fs::read(path).context("read config")?)
            .context("parse config")?;
        config.validate()?;
        Ok(config)
    }

    fn validate(&self) -> Result<()> {
        let url = reqwest::Url::parse(&self.endpoint).context("invalid endpoint")?;
        ensure!(
            url.scheme() == "https"
                && url.host_str().is_some()
                && url.username().is_empty()
                && url.password().is_none()
                && url.query().is_none()
                && url.fragment().is_none(),
            "endpoint must be HTTPS, without credentials, a query, or a fragment"
        );
        ensure!(
            (60..=604_800).contains(&self.interval_seconds),
            "intervalSeconds must be between 60 and 604800"
        );
        if let Some(path) = &self.ca_certificate_file {
            ensure!(
                path.is_absolute(),
                "caCertificateFile must be an absolute path"
            );
        }
        Ok(())
    }
}

#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
struct Report {
    used_bytes: u64,
}

fn used_bytes(blocks: u64, free: u64, fragment_size: u64) -> Result<u64> {
    ensure!(
        fragment_size > 0,
        "filesystem returned a zero fragment size"
    );
    blocks
        .checked_sub(free)
        .and_then(|used| used.checked_mul(fragment_size))
        .filter(|used| *used <= i64::MAX as u64)
        .context("filesystem returned invalid or overflowing block counts")
}

#[allow(clippy::unnecessary_cast)] // libc field widths differ between Linux architectures.
fn measure() -> Result<Report> {
    let mut stat = std::mem::MaybeUninit::<libc::statvfs>::uninit();
    // SAFETY: "/" is NUL-terminated, and stat points to enough writable storage.
    if unsafe { libc::statvfs(c"/".as_ptr(), stat.as_mut_ptr()) } != 0 {
        return Err(std::io::Error::last_os_error()).context("read root filesystem usage");
    }
    // SAFETY: statvfs initialized the structure on success.
    let stat = unsafe { stat.assume_init() };
    Ok(Report {
        // Free includes reserved blocks: those are not used data. We do not claim
        // that configured capacity minus this value is application-available space.
        used_bytes: used_bytes(
            stat.f_blocks as u64,
            stat.f_bfree as u64,
            stat.f_frsize as u64,
        )?,
    })
}

fn read_token(path: &Path) -> Result<String> {
    let token = fs::read_to_string(path).context("read reporting token")?;
    let token = token.trim();
    ensure!(
        token.len() == 64 && token.bytes().all(|b| b.is_ascii_hexdigit()),
        "reporting token must contain exactly 64 hexadecimal characters"
    );
    Ok(token.to_owned())
}

async fn upload(config: &Config, token: &str, report: &Report) -> Result<()> {
    config.validate()?;
    let _ = rustls::crypto::ring::default_provider().install_default();
    let mut client = reqwest::Client::builder()
        .https_only(true)
        .redirect(reqwest::redirect::Policy::none())
        .retry(reqwest::retry::never())
        .connect_timeout(Duration::from_secs(5))
        .timeout(Duration::from_secs(10))
        .user_agent(concat!(
            "octopushy-disk-reporter/",
            env!("CARGO_PKG_VERSION")
        ));
    if let Some(path) = &config.ca_certificate_file {
        client = client.add_root_certificate(
            reqwest::Certificate::from_pem(&fs::read(path).context("read CA certificate")?)
                .context("parse CA certificate")?,
        );
    }
    let response = client
        .build()?
        .post(&config.endpoint)
        .bearer_auth(token)
        .json(report)
        .send()
        .await
        .context("send disk usage")?;
    // Do not follow redirects or read arbitrary response bodies. Retry only at
    // the next timer activation, with a fresh measurement rather than a backlog.
    ensure!(
        response.status() == reqwest::StatusCode::NO_CONTENT,
        "server rejected disk usage (HTTP {})",
        response.status()
    );
    Ok(())
}

#[tokio::main(flavor = "current_thread")]
async fn main() -> Result<()> {
    let args: Vec<_> = std::env::args_os().skip(1).collect();
    match args.as_slice() {
        [command] if command == "measure" => {
            println!("{}", serde_json::to_string(&measure()?)?);
        }
        [command, path] if command == "interval" => {
            // The installer uses the same validated interval as the server's
            // downloaded config, instead of maintaining a second setting.
            println!("{}", Config::read(Path::new(path))?.interval_seconds);
        }
        [command, path] if command == "report" => {
            let path = Path::new(path);
            let config = Config::read(path)?;
            let token_path = std::env::var_os("CREDENTIALS_DIRECTORY")
                .map(|directory| PathBuf::from(directory).join("token"))
                .unwrap_or_else(|| path.with_file_name("token"));
            let token = read_token(&token_path)?;
            upload(&config, &token, &measure()?).await?;
        }
        _ => {
            bail!(
                "usage: octopushy-disk-reporter measure | interval CONFIG.json | report CONFIG.json"
            )
        }
    }
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn usage_counts_allocated_blocks_not_reserved_free_blocks() {
        assert_eq!(used_bytes(100, 40, 4096).unwrap(), 60 * 4096);
        assert_eq!(used_bytes(100, 100, 4096).unwrap(), 0);
        assert!(used_bytes(1, 2, 4096).is_err());
        assert!(used_bytes(u64::MAX, 0, 4096).is_err());
        assert!(used_bytes(100, 40, 0).is_err());
        let report = measure().unwrap();
        assert!(
            serde_json::to_string(&report)
                .unwrap()
                .starts_with("{\"usedBytes\":")
        );
    }

    #[test]
    fn configuration_and_credentials_fail_closed() {
        let directory = tempfile::tempdir().unwrap();
        let path = directory.path().join("config.json");
        fs::write(
            &path,
            r#"{"endpoint":"https://octopushy.test/api/guests/id/disk-usage"}"#,
        )
        .unwrap();
        assert_eq!(Config::read(&path).unwrap().interval_seconds, 3600);
        for invalid in [
            r#"{"endpoint":"http://octopushy.test"}"#,
            r#"{"endpoint":"https://secret@octopushy.test"}"#,
            r#"{"endpoint":"https://octopushy.test?token=secret"}"#,
            r#"{"endpoint":"https://octopushy.test","intervalSeconds":0}"#,
            r#"{"endpoint":"https://octopushy.test","intervalSeconds":604801}"#,
            r#"{"endpoint":"https://octopushy.test","intervalSecond":300}"#,
        ] {
            fs::write(&path, invalid).unwrap();
            assert!(Config::read(&path).is_err(), "{invalid}");
        }
        let token_path = directory.path().join("token");
        fs::write(&token_path, "secret\nheader").unwrap();
        assert!(read_token(&token_path).is_err());
        fs::write(&token_path, format!("{}\n", "a".repeat(64))).unwrap();
        assert_eq!(read_token(&token_path).unwrap(), "a".repeat(64));
    }

    #[tokio::test]
    async fn https_upload_is_authenticated_and_does_not_follow_redirects() {
        use std::sync::Arc;
        use tokio::io::{AsyncReadExt, AsyncWriteExt};

        let certificate = rcgen::generate_simple_self_signed(vec!["localhost".into()]).unwrap();
        let directory = tempfile::tempdir().unwrap();
        let ca = directory.path().join("ca.pem");
        fs::write(&ca, certificate.cert.pem()).unwrap();
        let _ = rustls::crypto::ring::default_provider().install_default();
        let tls = rustls::ServerConfig::builder()
            .with_no_client_auth()
            .with_single_cert(
                vec![certificate.cert.der().clone()],
                rustls::pki_types::PrivatePkcs8KeyDer::from(
                    certificate.signing_key.serialize_der(),
                )
                .into(),
            )
            .unwrap();
        let listener = tokio::net::TcpListener::bind("127.0.0.1:0").await.unwrap();
        let endpoint = format!(
            "https://localhost:{}/disk-usage",
            listener.local_addr().unwrap().port()
        );
        let server = tokio::spawn(async move {
            let acceptor = tokio_rustls::TlsAcceptor::from(Arc::new(tls));
            for status in ["204 No Content", "302 Found", "503 Service Unavailable"] {
                let (stream, _) = listener.accept().await.unwrap();
                let mut stream = acceptor.accept(stream).await.unwrap();
                let mut request = Vec::new();
                loop {
                    let mut buffer = [0; 1024];
                    let count = stream.read(&mut buffer).await.unwrap();
                    assert_ne!(count, 0);
                    request.extend_from_slice(&buffer[..count]);
                    if request.ends_with(b"{\"usedBytes\":4096}") {
                        break;
                    }
                }
                let request = String::from_utf8(request).unwrap();
                assert!(request.starts_with("POST /disk-usage HTTP/1.1"));
                assert!(
                    request
                        .to_lowercase()
                        .contains(&format!("authorization: bearer {}", "a".repeat(64)))
                );
                stream
                    .write_all(
                        format!(
                            "HTTP/1.1 {status}\r\nContent-Length: 0\r\nLocation: https://localhost/never-follow\r\nConnection: close\r\n\r\n"
                        )
                        .as_bytes(),
                    )
                    .await
                    .unwrap();
            }
            let (stream, _) = listener.accept().await.unwrap();
            assert!(acceptor.accept(stream).await.is_err());
        });
        let mut config = Config {
            endpoint,
            interval_seconds: 3600,
            ca_certificate_file: Some(ca),
        };
        let report = Report { used_bytes: 4096 };
        assert!(upload(&config, &"a".repeat(64), &report).await.is_ok());
        assert!(upload(&config, &"a".repeat(64), &report).await.is_err());
        assert!(upload(&config, &"a".repeat(64), &report).await.is_err());
        config.ca_certificate_file = None;
        assert!(upload(&config, &"a".repeat(64), &report).await.is_err());
        server.await.unwrap();
    }
}
