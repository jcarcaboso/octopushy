//! Built templates' images: `UploadImage` from a `nix` build, `DownloadImage` for an OpenTofu
//! attempt whose input names `templateImage`.
//!
//! Images are too big for one message, so they travel in [`CHUNK_BYTES`] pieces on their own
//! RPCs, over the same mutual-TLS endpoint and with the same credentials as the agent channel.
//! Each transfer opens its own connection: a transfer of gigabytes then shares no flow control
//! with the agent channel, and survives that channel reconnecting.

use std::path::{Path, PathBuf};

use anyhow::{Context, Result, anyhow, bail};
use octopushy_proto::v1::{
    DownloadImageRequest, ImageChunk, agent_channel_client::AgentChannelClient,
};
use sha2::{Digest, Sha256};
use tokio::io::{AsyncReadExt, AsyncWriteExt};
use tokio::sync::mpsc;
use tokio_stream::wrappers::ReceiverStream;
use tonic::transport::Channel;
use url::Url;

use crate::connection::MAX_MESSAGE_BYTES;
use crate::paths::{StatePaths, is_safe_id};
use crate::{identity, tls};

/// The most data one chunk carries.
pub const CHUNK_BYTES: usize = 4 * 1024 * 1024;

/// How executors reach the server for images.
#[derive(Debug, Clone, Default)]
pub enum ServerLink {
    /// Not connected to a server (tests of executors that transfer no image).
    #[default]
    None,
    /// Connects with the enrolled identity in this state directory, as the agent channel does.
    Enrolled(StatePaths),
    /// A channel that is already set up.
    Channel(Channel),
}

impl ServerLink {
    async fn client(&self) -> Result<AgentChannelClient<Channel>> {
        let channel = match self {
            ServerLink::None => bail!("this agent has no server connection for images"),
            ServerLink::Channel(channel) => channel.clone(),
            ServerLink::Enrolled(paths) => {
                let identity = identity::load(paths)?;
                let server = Url::parse(&identity.record.server_url)
                    .context("invalid server URL in agent.json")?;
                tls::endpoint(
                    &server,
                    &identity.ca_pem,
                    Some((&identity.cert_pem, &identity.key_pem)),
                )?
                .connect()
                .await
                .map_err(|e| {
                    anyhow!(
                        "connecting to {server}: {}",
                        tls::describe_connect_error(&e)
                    )
                })?
            }
        };
        Ok(AgentChannelClient::new(channel)
            .max_decoding_message_size(MAX_MESSAGE_BYTES)
            .max_encoding_message_size(MAX_MESSAGE_BYTES))
    }
}

/// An image's size and lowercase hex SHA-256.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct ImageDigest {
    pub sha256: String,
    pub size: u64,
}

/// Whether `name` is a plain file name: letters, digits, `.`, `_` and `-`, not starting with a
/// dot, at most 128 characters.
pub fn check_file_name(name: &str) -> Result<()> {
    if !is_safe_id(name) {
        bail!(
            "{name:?} is not a plain file name (letters, digits, '.', '_' and '-', not starting with '.')"
        );
    }
    Ok(())
}

/// Checks that `sha256` is 64 hex digits and returns it in lowercase.
pub fn check_sha256(sha256: &str) -> Result<String> {
    if sha256.len() != 64 || !sha256.bytes().all(|b| b.is_ascii_hexdigit()) {
        bail!("{sha256:?} is not a SHA-256 (64 hex digits)");
    }
    Ok(sha256.to_ascii_lowercase())
}

/// Sends the image at `path` for `attempt_id` and checks that the server received exactly it.
pub async fn upload(
    link: &ServerLink,
    attempt_id: &str,
    path: &Path,
    file_name: &str,
) -> Result<ImageDigest> {
    check_file_name(file_name)?;
    let mut client = link.client().await?;
    let file = tokio::fs::File::open(path)
        .await
        .with_context(|| format!("opening {}", path.display()))?;
    let size = file.metadata().await?.len();
    if size == 0 {
        bail!("{} is empty", path.display());
    }
    let (tx, rx) = mpsc::channel(2);
    let producer = tokio::spawn(send_chunks(
        file,
        attempt_id.to_owned(),
        file_name.to_owned(),
        size,
        tx,
    ));
    let response = client.upload_image(ReceiverStream::new(rx)).await;
    // The reader's own error explains a stream that ended early better than the server can.
    let sent = producer.await.context("the image reader panicked")?;
    let (received, sent) = match (response, sent) {
        (_, Err(e)) => return Err(e.context(format!("reading {}", path.display()))),
        (Err(s), _) => bail!(
            "the server refused the image: {} ({:?})",
            s.message(),
            s.code()
        ),
        (Ok(_), Ok(None)) => bail!("the server answered before the whole image was sent"),
        (Ok(r), Ok(Some(sent))) => (r.into_inner(), sent),
    };
    if !received.sha256.eq_ignore_ascii_case(&sent.sha256) || received.size != sent.size {
        bail!(
            "the server received {} bytes with SHA-256 {}, but the agent sent {} bytes with SHA-256 {}",
            received.size,
            received.sha256,
            sent.size,
            sent.sha256
        );
    }
    Ok(sent)
}

/// Reads `file` into chunks: the first names the attempt, the file and its size, the last
/// carries the SHA-256 of the whole file. `None`: the upload ended before the last chunk.
async fn send_chunks(
    mut file: tokio::fs::File,
    attempt_id: String,
    file_name: String,
    size: u64,
    tx: mpsc::Sender<ImageChunk>,
) -> Result<Option<ImageDigest>> {
    let mut hasher = Sha256::new();
    let mut sent = 0u64;
    let mut first = true;
    loop {
        let mut data = Vec::with_capacity(CHUNK_BYTES);
        (&mut file)
            .take(CHUNK_BYTES as u64)
            .read_to_end(&mut data)
            .await?;
        if data.is_empty() {
            bail!("the file ended after {sent} of {size} bytes");
        }
        sent += data.len() as u64;
        if sent > size {
            bail!("the file grew past {size} bytes while it was sent");
        }
        hasher.update(&data);
        let last = sent == size;
        let sha256 = if last {
            hex::encode(hasher.finalize_reset())
        } else {
            String::new()
        };
        let chunk = ImageChunk {
            attempt_id: if first {
                attempt_id.clone()
            } else {
                String::new()
            },
            file_name: if first {
                file_name.clone()
            } else {
                String::new()
            },
            size: if first { size } else { 0 },
            data,
            sha256: sha256.clone(),
        };
        first = false;
        if tx.send(chunk).await.is_err() {
            return Ok(None);
        }
        if last {
            let mut extra = [0u8; 1];
            if file.read(&mut extra).await? != 0 {
                bail!("the file grew past {size} bytes while it was sent");
            }
            return Ok(Some(ImageDigest { sha256, size }));
        }
    }
}

/// Fetches the image `expected` names for `attempt_id` into `dir/<file_name>`, written under a
/// temporary name and renamed once its size and SHA-256 match.
pub async fn download(
    link: &ServerLink,
    attempt_id: &str,
    expected: &ImageDigest,
    file_name: &str,
    dir: &Path,
) -> Result<PathBuf> {
    check_file_name(file_name)?;
    let sha256 = check_sha256(&expected.sha256)?;
    let target = dir.join(file_name);
    let part = dir.join(format!(".{file_name}.part"));
    let result = receive(link, attempt_id, &sha256, expected.size, &part).await;
    match result {
        Ok(()) => {
            tokio::fs::rename(&part, &target)
                .await
                .with_context(|| format!("renaming {} to {}", part.display(), target.display()))?;
            Ok(target)
        }
        Err(e) => {
            let _ = tokio::fs::remove_file(&part).await;
            Err(e)
        }
    }
}

async fn receive(
    link: &ServerLink,
    attempt_id: &str,
    sha256: &str,
    size: u64,
    part: &Path,
) -> Result<()> {
    let mut client = link.client().await?;
    let mut stream = client
        .download_image(DownloadImageRequest {
            attempt_id: attempt_id.to_owned(),
            sha256: sha256.to_owned(),
        })
        .await
        .map_err(|s| {
            anyhow!(
                "the server refused the image: {} ({:?})",
                s.message(),
                s.code()
            )
        })?
        .into_inner();
    let _ = tokio::fs::remove_file(part).await;
    let mut file = tokio::fs::OpenOptions::new()
        .write(true)
        .create_new(true)
        .open(part)
        .await
        .with_context(|| format!("creating {}", part.display()))?;
    let mut hasher = Sha256::new();
    let mut received = 0u64;
    let mut announced: Option<String> = None;
    while let Some(chunk) = stream.message().await.map_err(|s| {
        anyhow!(
            "the image download broke off: {} ({:?})",
            s.message(),
            s.code()
        )
    })? {
        if chunk.size != 0 && chunk.size != size {
            bail!(
                "the server sends an image of {} bytes, but {size} were expected",
                chunk.size
            );
        }
        received += chunk.data.len() as u64;
        if received > size {
            bail!("the server sent more than the expected {size} bytes");
        }
        hasher.update(&chunk.data);
        file.write_all(&chunk.data)
            .await
            .with_context(|| format!("writing {}", part.display()))?;
        if !chunk.sha256.is_empty() {
            announced = Some(chunk.sha256);
        }
    }
    file.flush().await?;
    file.sync_all().await?;
    if received != size {
        bail!("the image has {received} bytes, but {size} were expected");
    }
    let actual = hex::encode(hasher.finalize());
    if actual != sha256 {
        bail!("the image's SHA-256 is {actual}, but {sha256} was expected");
    }
    if let Some(a) = announced
        && !a.eq_ignore_ascii_case(sha256)
    {
        bail!("the server announced SHA-256 {a}, but {sha256} was expected");
    }
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;
    use octopushy_proto::v1::{
        AgentMessage, ImageUploaded, RenewCertificateRequest, RenewCertificateResponse,
        ServerMessage,
        agent_channel_server::{AgentChannel, AgentChannelServer},
    };
    use tonic::{Request, Response, Status, Streaming};

    /// Serves `chunks` for any download, as they are, over plain HTTP/2.
    struct Raw(Vec<ImageChunk>);

    #[tonic::async_trait]
    impl AgentChannel for Raw {
        type ConnectStream = ReceiverStream<Result<ServerMessage, Status>>;
        async fn connect(
            &self,
            _: Request<Streaming<AgentMessage>>,
        ) -> Result<Response<Self::ConnectStream>, Status> {
            Err(Status::unimplemented("images only"))
        }
        async fn renew_certificate(
            &self,
            _: Request<RenewCertificateRequest>,
        ) -> Result<Response<RenewCertificateResponse>, Status> {
            Err(Status::unimplemented("images only"))
        }
        async fn upload_image(
            &self,
            _: Request<Streaming<ImageChunk>>,
        ) -> Result<Response<ImageUploaded>, Status> {
            Err(Status::unimplemented("downloads only"))
        }
        type DownloadImageStream = ReceiverStream<Result<ImageChunk, Status>>;
        async fn download_image(
            &self,
            _: Request<DownloadImageRequest>,
        ) -> Result<Response<Self::DownloadImageStream>, Status> {
            let (tx, rx) = mpsc::channel(self.0.len().max(1));
            for c in &self.0 {
                tx.try_send(Ok(c.clone())).unwrap();
            }
            Ok(Response::new(ReceiverStream::new(rx)))
        }
    }

    async fn serve(chunks: Vec<ImageChunk>) -> ServerLink {
        let listener = tokio::net::TcpListener::bind("127.0.0.1:0").await.unwrap();
        let addr = listener.local_addr().unwrap();
        tokio::spawn(
            tonic::transport::Server::builder()
                .add_service(AgentChannelServer::new(Raw(chunks)))
                .serve_with_incoming(tokio_stream::wrappers::TcpListenerStream::new(listener)),
        );
        let channel = tonic::transport::Endpoint::from_shared(format!("http://{addr}"))
            .unwrap()
            .connect_lazy();
        ServerLink::Channel(channel)
    }

    fn chunk(data: &[u8], size: u64, sha256: &str) -> ImageChunk {
        ImageChunk {
            data: data.to_vec(),
            size,
            sha256: sha256.into(),
            ..Default::default()
        }
    }

    #[tokio::test]
    async fn downloads_are_checked_chunk_by_chunk_and_whole() {
        let data = b"0123456789";
        let sha = hex::encode(Sha256::digest(data));
        let expected = ImageDigest {
            sha256: sha.clone(),
            size: 10,
        };
        let dir = tempfile::tempdir().unwrap();
        let cases: Vec<(Vec<ImageChunk>, Option<&str>)> = vec![
            // Size first, data in pieces, SHA-256 last.
            (
                vec![chunk(&data[..4], 10, ""), chunk(&data[4..], 0, &sha)],
                None,
            ),
            // Nothing announced: the content alone decides.
            (
                vec![chunk(&data[..3], 0, ""), chunk(&data[3..], 0, "")],
                None,
            ),
            (
                vec![chunk(data, 0, ""), chunk(b"x", 0, "")],
                Some("more than the expected 10 bytes"),
            ),
            (vec![chunk(&data[..9], 0, "")], Some("has 9 bytes, but 10")),
            (
                vec![chunk(b"9876543210", 0, "")],
                Some("the image's SHA-256 is"),
            ),
            (
                vec![chunk(data, 0, &"0".repeat(64))],
                Some("the server announced SHA-256 0000"),
            ),
            (vec![chunk(data, 11, "")], Some("an image of 11 bytes")),
        ];
        for (n, (chunks, error)) in cases.into_iter().enumerate() {
            let link = serve(chunks).await;
            let got = download(&link, "att", &expected, "img.raw", dir.path()).await;
            match error {
                None => {
                    let path = got.unwrap_or_else(|e| panic!("{n}: {e:#}"));
                    assert_eq!(path, dir.path().join("img.raw"));
                    assert_eq!(std::fs::read(&path).unwrap(), data);
                    std::fs::remove_file(path).unwrap();
                }
                Some(error) => {
                    let e = format!("{:#}", got.unwrap_err());
                    assert!(e.contains(error), "{n}: {e}");
                }
            }
            let left: Vec<_> = std::fs::read_dir(dir.path()).unwrap().collect();
            assert!(left.is_empty(), "{n}: no partial file stays");
        }
    }

    #[tokio::test]
    async fn without_a_server_there_is_no_transfer() {
        let dir = tempfile::tempdir().unwrap();
        let file = dir.path().join("x.raw");
        std::fs::write(&file, b"x").unwrap();
        let e = upload(&ServerLink::None, "att", &file, "x.raw")
            .await
            .unwrap_err();
        assert!(e.to_string().contains("no server connection"), "{e}");
    }

    #[test]
    fn file_names() {
        for good in [
            "nixos-host.qcow2",
            "3f2a9c1e04b7-nixos-host.qcow2",
            "a_b.raw",
            "x",
        ] {
            assert!(check_file_name(good).is_ok(), "{good}");
        }
        for bad in [
            "",
            ".hidden.qcow2",
            "..",
            "a/b.raw",
            "../x.raw",
            "a b.raw",
            "ä.raw",
            "a\nb",
            &"x".repeat(129),
        ] {
            assert!(check_file_name(bad).is_err(), "{bad:?}");
        }
    }

    #[test]
    fn sha256s() {
        let upper = "AB".repeat(32);
        assert_eq!(check_sha256(&upper).unwrap(), "ab".repeat(32));
        for bad in ["", "ab", &"g".repeat(64), &"a".repeat(65)] {
            assert!(check_sha256(bad).is_err(), "{bad:?}");
        }
    }
}
