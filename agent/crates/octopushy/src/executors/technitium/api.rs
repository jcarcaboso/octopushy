//! One member's Technitium API, over HTTPS on its own address.

use std::time::Duration;

use reqwest::{Client, Response};
use serde::Deserialize;
use serde_json::Value;

use crate::executors::Failure;

/// Why a call did not work. Reaching a member is one thing and being refused by it another:
/// they lead to different recoveries.
#[derive(Debug)]
pub enum ApiError {
    Unreachable(String),
    Refused(String),
    Api(String),
}

impl std::fmt::Display for ApiError {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        match self {
            ApiError::Unreachable(e) | ApiError::Refused(e) | ApiError::Api(e) => f.write_str(e),
        }
    }
}

pub fn http_client(verify_tls: bool, timeout: Duration) -> Result<Client, Failure> {
    let tls = if verify_tls {
        octopushy_proxmox::Tls::Verify
    } else {
        octopushy_proxmox::Tls::Insecure
    };
    let config =
        octopushy_proxmox::tls_client_config(&tls).map_err(|e| Failure::msg(e.to_string()))?;
    Client::builder()
        .tls_backend_preconfigured(config)
        .timeout(timeout)
        .connect_timeout(timeout.min(Duration::from_secs(15)))
        .user_agent(concat!("octopushy-agent/", env!("CARGO_PKG_VERSION")))
        .redirect(reqwest::redirect::Policy::none())
        .retry(reqwest::retry::never())
        .build()
        .map_err(|e| Failure::msg(format!("cannot build the HTTP client: {e}")))
}

/// A member's `/api/admin/cluster/state`.
#[derive(Debug, Clone, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ClusterState {
    #[serde(default)]
    pub version: String,
    #[serde(default)]
    pub dns_server_domain: String,
    #[serde(default)]
    pub cluster_initialized: bool,
    #[serde(default)]
    pub cluster_domain: Option<String>,
    #[serde(default, alias = "nodes")]
    pub cluster_nodes: Vec<Peer>,
}

#[derive(Debug, Clone, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct Peer {
    pub id: i64,
    #[serde(default)]
    pub name: String,
    #[serde(default)]
    pub ip_addresses: Vec<String>,
    #[serde(rename = "type", default)]
    pub kind: String,
    #[serde(default)]
    pub state: String,
}

/// A signed-in session on one member.
pub struct Api {
    http: Client,
    endpoint: String,
    token: String,
}

impl Api {
    pub async fn login(http: &Client, endpoint: &str, password: &str) -> Result<Self, ApiError> {
        let endpoint = endpoint.trim_end_matches('/').to_owned();
        let response = http
            .post(format!("{endpoint}/api/user/login"))
            .form(&[("user", "admin"), ("pass", password)])
            .send()
            .await
            .map_err(|e| ApiError::Unreachable(transport(&e)))?;
        let body = decode(response).await?;
        if body["status"] != "ok" {
            return Err(ApiError::Refused(message(&body)));
        }
        let token = body["token"]
            .as_str()
            .ok_or_else(|| ApiError::Api("the login returned no token".into()))?
            .to_owned();
        Ok(Self {
            http: http.clone(),
            endpoint,
            token,
        })
    }

    pub async fn get(&self, path: &str) -> Result<Value, ApiError> {
        self.call(path, &[]).await
    }

    /// Calls `path` with a form. The server applies a change as soon as it has read it.
    pub async fn call(&self, path: &str, form: &[(&str, &str)]) -> Result<Value, ApiError> {
        let response = self
            .http
            .post(format!("{}{path}", self.endpoint))
            .bearer_auth(&self.token)
            .form(form)
            .send()
            .await
            .map_err(|e| ApiError::Unreachable(transport(&e)))?;
        let body = decode(response).await?;
        if body["status"] == "ok" {
            Ok(body["response"].clone())
        } else {
            Err(ApiError::Api(message(&body)))
        }
    }

    /// An export: one entry on each line.
    pub async fn lines(&self, path: &str) -> Result<Vec<String>, ApiError> {
        let response = self
            .http
            .get(format!("{}{path}", self.endpoint))
            .bearer_auth(&self.token)
            .send()
            .await
            .map_err(|e| ApiError::Unreachable(transport(&e)))?;
        if !response.status().is_success() {
            return Err(ApiError::Api(format!(
                "{path} returned {}",
                response.status()
            )));
        }
        let text = response
            .text()
            .await
            .map_err(|e| ApiError::Unreachable(transport(&e)))?;
        Ok(text
            .lines()
            .map(str::trim)
            .filter(|l| !l.is_empty() && !l.starts_with('#'))
            .map(str::to_owned)
            .collect())
    }

    pub async fn cluster_state(&self) -> Result<ClusterState, ApiError> {
        let value = self.get("/api/admin/cluster/state").await?;
        serde_json::from_value(value)
            .map_err(|e| ApiError::Api(format!("unexpected cluster state: {e}")))
    }

    /// Best effort: a session left behind expires by itself.
    pub async fn logout(self) {
        let _ = self.get("/api/user/logout").await;
    }
}

async fn decode(response: Response) -> Result<Value, ApiError> {
    let status = response.status();
    response
        .json::<Value>()
        .await
        .map_err(|e| ApiError::Api(format!("the server answered {status} without JSON: {e}")))
}

fn message(body: &Value) -> String {
    body["errorMessage"]
        .as_str()
        .unwrap_or("the server refused the request")
        .to_owned()
}

fn transport(e: &reqwest::Error) -> String {
    let mut text = e.to_string();
    let mut source = std::error::Error::source(e);
    while let Some(cause) = source {
        text = format!("{text}: {cause}");
        source = cause.source();
    }
    text
}
