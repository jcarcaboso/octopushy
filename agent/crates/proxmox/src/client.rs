//! The Proxmox API client.

use std::fmt;
use std::time::Duration;

use reqwest::header::{AUTHORIZATION, HeaderValue};
use reqwest::{Method, StatusCode};
use serde::de::DeserializeOwned;
use url::Url;

use crate::error::{Error, Result};
use crate::tls::{self, Tls};
use crate::types::*;

/// An API token, `USER@REALM!TOKENID=SECRET`. Its `Debug` output is redacted.
#[derive(Clone, PartialEq, Eq)]
pub struct ApiToken(String);

impl ApiToken {
    /// Wraps a token after checking its shape (`USER@REALM!TOKENID=SECRET`).
    pub fn new(token: impl Into<String>) -> Result<Self> {
        let token = token.into();
        let shape_ok = match token.split_once('=') {
            Some((id, secret)) => id.contains('@') && id.contains('!') && !secret.is_empty(),
            None => false,
        };
        if !shape_ok {
            return Err(Error::Config(
                "the API token must look like USER@REALM!TOKENID=SECRET".into(),
            ));
        }
        if token.chars().any(|c| c.is_control() || c == ' ') {
            return Err(Error::Config(
                "the API token contains whitespace or control characters".into(),
            ));
        }
        Ok(Self(token))
    }

    /// The token ID part (`USER@REALM!TOKENID`), which is not secret.
    pub fn id(&self) -> &str {
        self.0.split_once('=').map(|(id, _)| id).unwrap_or("")
    }

    /// The full token value. Handle with care.
    pub fn expose(&self) -> &str {
        &self.0
    }
}

impl fmt::Debug for ApiToken {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        write!(f, "ApiToken({}=***)", self.id())
    }
}

/// Settings for [`Client::new`].
#[derive(Clone)]
pub struct ClientConfig {
    /// For example `https://10.1.0.10:8006/`. A trailing `/api2/json` is accepted.
    pub endpoint: String,
    pub token: ApiToken,
    pub tls: Tls,
    /// Timeout of each request.
    pub timeout: Duration,
}

impl fmt::Debug for ClientConfig {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        f.debug_struct("ClientConfig")
            .field("endpoint", &self.endpoint)
            .field("token", &self.token)
            .field("tls", &self.tls)
            .field("timeout", &self.timeout)
            .finish()
    }
}

/// An async client for one Proxmox installation.
#[derive(Clone)]
pub struct Client {
    http: reqwest::Client,
    base: Url,
    auth: HeaderValue,
    token_id: String,
}

impl fmt::Debug for Client {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        f.debug_struct("Client")
            .field("base", &self.base.as_str())
            .field("token_id", &self.token_id)
            .finish_non_exhaustive()
    }
}

/// Status-line phrases Proxmox uses when the guest agent cannot answer.
const GUEST_AGENT_UNAVAILABLE: &[&str] = &[
    "guest agent is not running",
    "no qemu guest agent configured",
    "guest agent is not configured",
    // "VM 210 not running" when the VM itself is stopped.
    "not running",
    "got timeout",
];

impl Client {
    /// Creates a client. Fails on an unusable endpoint, token, or CA.
    pub fn new(config: ClientConfig) -> Result<Self> {
        let base = base_url(&config.endpoint)?;
        let tls_config = tls::client_config(&config.tls)?;
        let http = reqwest::Client::builder()
            .tls_backend_preconfigured(tls_config)
            .timeout(config.timeout)
            .connect_timeout(config.timeout.min(Duration::from_secs(15)))
            .user_agent(concat!("octopushy-proxmox/", env!("CARGO_PKG_VERSION")))
            .redirect(reqwest::redirect::Policy::none())
            .retry(reqwest::retry::never())
            .build()
            .map_err(|e| Error::Config(format!("cannot build the HTTP client: {e}")))?;
        let mut auth = HeaderValue::from_str(&format!("PVEAPIToken={}", config.token.expose()))
            .map_err(|_| Error::Config("the API token is not a valid header value".into()))?;
        auth.set_sensitive(true);
        Ok(Self {
            http,
            base,
            auth,
            token_id: config.token.id().to_owned(),
        })
    }

    /// The API base URL (`<endpoint>/api2/json/`).
    pub fn base_url(&self) -> &Url {
        &self.base
    }

    fn url(&self, segments: &[&str]) -> Url {
        let mut url = self.base.clone();
        {
            let mut path = url.path_segments_mut().expect("https URLs have a path");
            path.pop_if_empty();
            // Each segment is percent-encoded, so a node name cannot alter the path.
            path.extend(segments);
        }
        url
    }

    async fn request<T: DeserializeOwned>(
        &self,
        method: Method,
        segments: &[&str],
        query: &[(&str, &str)],
        form: Option<&[(&str, &str)]>,
        json: Option<&serde_json::Value>,
    ) -> Result<T> {
        let url = self.url(segments);
        let path = format!("/{}", segments.join("/"));
        tracing::debug!(%method, %path, "proxmox request");
        let mut req = self
            .http
            .request(method, url)
            .header(AUTHORIZATION, self.auth.clone());
        if !query.is_empty() {
            req = req.query(query);
        }
        if let Some(form) = form {
            req = req.form(form);
        }
        if let Some(json) = json {
            req = req.json(json);
        }
        let resp = req.send().await.map_err(Error::transport)?;
        let status = resp.status();
        let reason = resp
            .extensions()
            .get::<hyper::ext::ReasonPhrase>()
            .map(|r| String::from_utf8_lossy(r.as_bytes()).into_owned());
        let body = resp.bytes().await.map_err(Error::transport)?;

        if !status.is_success() {
            return Err(status_error(status, reason.as_deref(), &body));
        }

        #[derive(serde::Deserialize)]
        struct Envelope<T> {
            data: T,
        }
        serde_json::from_slice::<Envelope<T>>(&body)
            .map(|e| e.data)
            .map_err(|e| Error::Decode {
                path,
                message: e.to_string(),
            })
    }

    async fn get<T: DeserializeOwned>(
        &self,
        segments: &[&str],
        query: &[(&str, &str)],
    ) -> Result<T> {
        self.request(Method::GET, segments, query, None, None).await
    }

    /// `GET /version`.
    pub async fn version(&self) -> Result<Version> {
        self.get(&["version"], &[]).await
    }

    /// `GET /cluster/status`.
    pub async fn cluster_status(&self) -> Result<ClusterStatus> {
        let entries: Vec<ClusterStatusEntry> = self.get(&["cluster", "status"], &[]).await?;
        Ok(ClusterStatus { entries })
    }

    /// `GET /nodes`, sorted by node name.
    pub async fn nodes(&self) -> Result<Vec<Node>> {
        let mut nodes: Vec<Node> = self.get(&["nodes"], &[]).await?;
        nodes.sort_by(|a, b| a.node.cmp(&b.node));
        Ok(nodes)
    }

    /// `GET /cluster/resources?type=vm`, sorted by VMID. Works for standalone
    /// hosts and clusters alike. Proxmox only lists guests the token may audit.
    pub async fn vm_resources(&self) -> Result<Vec<VmResource>> {
        let mut vms: Vec<VmResource> = self
            .get(&["cluster", "resources"], &[("type", "vm")])
            .await?;
        vms.sort_by_key(|v| v.vmid);
        Ok(vms)
    }

    /// Finds a guest by VMID through [`Client::vm_resources`].
    pub async fn find_vm(&self, vmid: u32) -> Result<Option<VmResource>> {
        Ok(self
            .vm_resources()
            .await?
            .into_iter()
            .find(|v| v.vmid == vmid))
    }

    /// `GET /nodes/{node}/qemu/{vmid}/status/current`.
    pub async fn vm_status(&self, node: &str, vmid: u32) -> Result<VmStatus> {
        let id = vmid.to_string();
        self.get(&["nodes", node, "qemu", &id, "status", "current"], &[])
            .await
            .map_err(missing_vm)
    }

    /// `GET /nodes/{node}/qemu/{vmid}/config`.
    pub async fn vm_config(&self, node: &str, vmid: u32) -> Result<VmConfig> {
        let id = vmid.to_string();
        self.get(&["nodes", node, "qemu", &id, "config"], &[])
            .await
            .map_err(missing_vm)
    }

    /// Sets the VM's `protection` option (`PUT /nodes/{node}/qemu/{vmid}/config`).
    pub async fn set_vm_protection(&self, node: &str, vmid: u32, protection: bool) -> Result<()> {
        let id = vmid.to_string();
        let value = if protection { "1" } else { "0" };
        let _: serde_json::Value = self
            .request(
                Method::PUT,
                &["nodes", node, "qemu", &id, "config"],
                &[],
                Some(&[("protection", value)]),
                None,
            )
            .await
            .map_err(missing_vm)?;
        Ok(())
    }

    /// Executes an argument array through QEMU's guest agent; never retries a POST.
    /// Input is plain stdin: Proxmox handles base64 encoding for QGA. Never log it.
    pub async fn guest_exec(
        &self,
        node: &str,
        vmid: u32,
        command: &[String],
        input: Option<&str>,
    ) -> Result<u64> {
        if input.is_some_and(|input| input.len() > 64 * 1024) {
            return Err(Error::Config("guest exec input exceeds 64 KiB".into()));
        }
        #[derive(serde::Deserialize)]
        struct Started {
            pid: u64,
        }
        let id = vmid.to_string();
        let mut body = serde_json::json!({ "command": command });
        if let Some(input) = input {
            body["input-data"] = input.into();
        }
        let started: Started = self
            .request(
                Method::POST,
                &["nodes", node, "qemu", &id, "agent", "exec"],
                &[],
                None,
                Some(&body),
            )
            .await?;
        Ok(started.pid)
    }

    pub async fn guest_exec_status(
        &self,
        node: &str,
        vmid: u32,
        pid: u64,
    ) -> Result<GuestExecStatus> {
        self.get(
            &[
                "nodes",
                node,
                "qemu",
                &vmid.to_string(),
                "agent",
                "exec-status",
            ],
            &[("pid", &pid.to_string())],
        )
        .await
    }

    /// The guest agent's network interfaces
    /// (`GET /nodes/{node}/qemu/{vmid}/agent/network-get-interfaces`).
    ///
    /// Returns [`Error::GuestAgentUnavailable`] when the VM is stopped or its agent
    /// is not running or not configured.
    pub async fn guest_network_interfaces(
        &self,
        node: &str,
        vmid: u32,
    ) -> Result<Vec<GuestInterface>> {
        #[derive(serde::Deserialize)]
        struct Wrapper {
            #[serde(default)]
            result: Vec<GuestInterface>,
        }
        let id = vmid.to_string();
        let result: Result<Wrapper> = self
            .get(
                &[
                    "nodes",
                    node,
                    "qemu",
                    &id,
                    "agent",
                    "network-get-interfaces",
                ],
                &[],
            )
            .await;
        match result.map_err(missing_vm) {
            Ok(w) => Ok(w.result),
            Err(Error::Status { status, message })
                if status >= 500 && {
                    let lower = message.to_ascii_lowercase();
                    GUEST_AGENT_UNAVAILABLE.iter().any(|p| lower.contains(p))
                } =>
            {
                Err(Error::GuestAgentUnavailable(message))
            }
            Err(e) => Err(e),
        }
    }

    /// `GET /access/permissions[?path=...]`: the token's privileges.
    pub async fn permissions(&self, path: Option<&str>) -> Result<Permissions> {
        match path {
            Some(p) => self.get(&["access", "permissions"], &[("path", p)]).await,
            None => self.get(&["access", "permissions"], &[]).await,
        }
    }
}

fn base_url(endpoint: &str) -> Result<Url> {
    let mut url = Url::parse(endpoint.trim())
        .map_err(|e| Error::Config(format!("invalid endpoint {endpoint:?}: {e}")))?;
    if url.scheme() != "https" && url.scheme() != "http" {
        return Err(Error::Config(format!(
            "the endpoint must be an http(s) URL, got {endpoint:?}"
        )));
    }
    if url.cannot_be_a_base() || url.host().is_none() {
        return Err(Error::Config(format!("invalid endpoint {endpoint:?}")));
    }
    if !url.username().is_empty() || url.password().is_some() {
        return Err(Error::Config(
            "the endpoint must not contain credentials".into(),
        ));
    }
    url.set_query(None);
    url.set_fragment(None);
    let trimmed = url.path().trim_end_matches('/').to_owned();
    let path = if trimmed.ends_with("/api2/json") {
        format!("{trimmed}/")
    } else {
        format!("{trimmed}/api2/json/")
    };
    url.set_path(&path);
    Ok(url)
}

/// Builds the error for a non-success status. Proxmox puts the message in the
/// status line's reason phrase and sometimes in the body (`message` or
/// `errors`).
fn status_error(status: StatusCode, reason: Option<&str>, body: &[u8]) -> Error {
    let mut parts: Vec<String> = Vec::new();
    if let Some(reason) = reason.map(str::trim).filter(|r| !r.is_empty())
        && Some(reason) != status.canonical_reason()
    {
        parts.push(reason.to_owned());
    }
    if let Ok(json) = serde_json::from_slice::<serde_json::Value>(body) {
        if let Some(m) = json.get("message").and_then(|m| m.as_str()) {
            let m = m.trim();
            if !m.is_empty() && !parts.iter().any(|p| p == m) {
                parts.push(m.to_owned());
            }
        }
        if let Some(errors) = json.get("errors").filter(|e| !e.is_null()) {
            parts.push(format!("errors: {errors}"));
        }
    } else if !body.is_empty() {
        parts.push(String::from_utf8_lossy(body).into_owned());
    }
    let mut message = parts.join("; ");
    message.retain(|c| !c.is_control() || c == ' ');
    if message.chars().count() > 300 {
        message = message.chars().take(300).collect::<String>() + "...";
    }
    if message.is_empty() {
        message = status.canonical_reason().unwrap_or("error").to_owned();
    }
    match status {
        StatusCode::UNAUTHORIZED => Error::Unauthorized,
        StatusCode::FORBIDDEN => Error::Forbidden(message),
        StatusCode::NOT_FOUND => Error::NotFound(message),
        _ => Error::Status {
            status: status.as_u16(),
            message,
        },
    }
}

/// Proxmox answers requests for a missing VM with HTTP 500 "Configuration file
/// 'nodes/x/qemu-server/210.conf' does not exist"; report that as not found.
fn missing_vm(err: Error) -> Error {
    match err {
        Error::Status {
            status: 500,
            message,
        } if message.contains("does not exist") => Error::NotFound(message),
        other => other,
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn base_url_normalizes_endpoints() {
        for (input, expected) in [
            (
                "https://10.1.0.10:8006/",
                "https://10.1.0.10:8006/api2/json/",
            ),
            (
                "https://10.1.0.10:8006",
                "https://10.1.0.10:8006/api2/json/",
            ),
            (
                "https://pve.lan:8006/api2/json",
                "https://pve.lan:8006/api2/json/",
            ),
            ("https://pve.lan/proxy/", "https://pve.lan/proxy/api2/json/"),
        ] {
            assert_eq!(base_url(input).unwrap().as_str(), expected, "{input}");
        }
        assert!(base_url("ftp://x").is_err());
        assert!(base_url("https://user:pw@x/").is_err());
        assert!(base_url("not a url").is_err());
    }

    #[test]
    fn token_debug_is_redacted() {
        let token =
            ApiToken::new("octopushy@pve!agent=11111111-2222-3333-4444-555555555555").unwrap();
        let config = ClientConfig {
            endpoint: "https://x:8006/".into(),
            token: token.clone(),
            tls: Tls::CustomCa("-----BEGIN CERTIFICATE-----".into()),
            timeout: Duration::from_secs(1),
        };
        let debug = format!("{config:?} {token:?}");
        assert!(!debug.contains("11111111"), "{debug}");
        assert!(debug.contains("octopushy@pve!agent"), "{debug}");
        assert!(!debug.contains("BEGIN CERTIFICATE"), "{debug}");
    }

    #[test]
    fn token_shape_is_checked() {
        assert!(ApiToken::new("root@pam!x=secret").is_ok());
        assert!(ApiToken::new("secret").is_err());
        assert!(ApiToken::new("root@pam!x=").is_err());
        assert!(ApiToken::new("root@pam!x=se\ncret").is_err());
    }

    #[test]
    fn url_segments_are_encoded() {
        let client = Client::new(ClientConfig {
            endpoint: "https://pve:8006".into(),
            token: ApiToken::new("a@pve!t=s").unwrap(),
            tls: Tls::Insecure,
            timeout: Duration::from_secs(1),
        })
        .unwrap();
        let url = client.url(&["nodes", "../x", "qemu"]);
        assert_eq!(url.as_str(), "https://pve:8006/api2/json/nodes/..%2Fx/qemu");
    }
}
