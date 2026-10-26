//! Typed views of the Proxmox responses this client uses.
//!
//! Fields not listed are ignored; every listed field is decoded leniently (see
//! [`crate::de`]).

use std::collections::{BTreeMap, BTreeSet};
use std::net::Ipv4Addr;

use serde::{Deserialize, Deserializer, Serialize};

use crate::de;

/// `GET /version`.
#[derive(Debug, Clone, PartialEq, Deserialize, Serialize)]
pub struct Version {
    #[serde(deserialize_with = "de::string_or_empty")]
    pub version: String,
    #[serde(default, deserialize_with = "de::opt_string")]
    pub release: Option<String>,
    #[serde(default, deserialize_with = "de::opt_string")]
    pub repoid: Option<String>,
}

/// One entry of `GET /cluster/status`: either the cluster itself (`type:
/// "cluster"`) or one of its nodes (`type: "node"`). A standalone host returns a
/// single node entry and no cluster entry.
#[derive(Debug, Clone, PartialEq, Deserialize, Serialize)]
pub struct ClusterStatusEntry {
    #[serde(rename = "type", deserialize_with = "de::string_or_empty")]
    pub kind: String,
    #[serde(default, deserialize_with = "de::string_or_empty")]
    pub name: String,
    #[serde(default, deserialize_with = "de::opt_string")]
    pub id: Option<String>,
    #[serde(default, deserialize_with = "de::opt_bool")]
    pub quorate: Option<bool>,
    #[serde(default, deserialize_with = "de::opt_bool")]
    pub online: Option<bool>,
    #[serde(default, deserialize_with = "de::opt_bool")]
    pub local: Option<bool>,
    #[serde(default, deserialize_with = "de::opt_u64")]
    pub nodes: Option<u64>,
    #[serde(default, deserialize_with = "de::opt_string")]
    pub ip: Option<String>,
}

/// Name and quorum of a cluster.
#[derive(Debug, Clone, PartialEq, Eq, Serialize)]
pub struct ClusterSummary {
    pub name: String,
    pub quorate: bool,
}

/// `GET /cluster/status`.
#[derive(Debug, Clone, PartialEq, Serialize)]
pub struct ClusterStatus {
    pub entries: Vec<ClusterStatusEntry>,
}

impl ClusterStatus {
    /// The cluster entry, or `None` for a standalone host.
    pub fn cluster(&self) -> Option<ClusterSummary> {
        self.entries
            .iter()
            .find(|e| e.kind == "cluster")
            .map(|e| ClusterSummary {
                name: e.name.clone(),
                quorate: e.quorate.unwrap_or(false),
            })
    }

    /// The node entries.
    pub fn nodes(&self) -> impl Iterator<Item = &ClusterStatusEntry> {
        self.entries.iter().filter(|e| e.kind == "node")
    }
}

/// One entry of `GET /nodes`. Offline nodes carry no figures.
#[derive(Debug, Clone, PartialEq, Deserialize, Serialize)]
pub struct Node {
    #[serde(deserialize_with = "de::string_or_empty")]
    pub node: String,
    /// `online`, `offline`, or `unknown`.
    #[serde(default, deserialize_with = "de::string_or_empty")]
    pub status: String,
    /// CPU load as a fraction of `maxcpu`.
    #[serde(default, deserialize_with = "de::opt_f64")]
    pub cpu: Option<f64>,
    #[serde(default, deserialize_with = "de::opt_u64")]
    pub maxcpu: Option<u64>,
    #[serde(default, deserialize_with = "de::opt_u64")]
    pub mem: Option<u64>,
    #[serde(default, deserialize_with = "de::opt_u64")]
    pub maxmem: Option<u64>,
    #[serde(default, deserialize_with = "de::opt_u64")]
    pub uptime: Option<u64>,
}

impl Node {
    pub fn is_online(&self) -> bool {
        self.status == "online"
    }
}

/// The kind of guest in `/cluster/resources`.
#[derive(Debug, Clone, PartialEq, Eq, Serialize)]
#[serde(rename_all = "lowercase")]
pub enum GuestKind {
    Qemu,
    Lxc,
    #[serde(untagged)]
    Other(String),
}

impl GuestKind {
    pub fn as_str(&self) -> &str {
        match self {
            GuestKind::Qemu => "qemu",
            GuestKind::Lxc => "lxc",
            GuestKind::Other(s) => s,
        }
    }
}

impl<'de> Deserialize<'de> for GuestKind {
    fn deserialize<D: Deserializer<'de>>(d: D) -> Result<Self, D::Error> {
        let s = de::string_or_empty(d)?;
        Ok(match s.as_str() {
            "qemu" => GuestKind::Qemu,
            "lxc" => GuestKind::Lxc,
            _ => GuestKind::Other(s),
        })
    }
}

/// One VM or container from `GET /cluster/resources?type=vm`.
#[derive(Debug, Clone, PartialEq, Deserialize, Serialize)]
pub struct VmResource {
    #[serde(deserialize_with = "de::vmid")]
    pub vmid: u32,
    #[serde(default, deserialize_with = "de::opt_string")]
    pub name: Option<String>,
    #[serde(deserialize_with = "de::string_or_empty")]
    pub node: String,
    /// `running`, `stopped`, `paused`, or `unknown` (for example when its node is
    /// offline).
    #[serde(default, deserialize_with = "de::string_or_empty")]
    pub status: String,
    #[serde(default, deserialize_with = "de::bool_or_false")]
    pub template: bool,
    #[serde(default, deserialize_with = "de::tags")]
    pub tags: Vec<String>,
    #[serde(default, deserialize_with = "de::opt_f64")]
    pub maxcpu: Option<f64>,
    #[serde(default, deserialize_with = "de::opt_u64")]
    pub maxmem: Option<u64>,
    #[serde(default, deserialize_with = "de::opt_u64")]
    pub uptime: Option<u64>,
    #[serde(rename = "type")]
    pub kind: GuestKind,
}

/// `GET /nodes/{node}/qemu/{vmid}/status/current`.
#[derive(Debug, Clone, PartialEq, Deserialize, Serialize)]
pub struct VmStatus {
    #[serde(default, deserialize_with = "de::string_or_empty")]
    pub status: String,
    #[serde(default, deserialize_with = "de::opt_string")]
    pub name: Option<String>,
    #[serde(default, deserialize_with = "de::opt_string")]
    pub qmpstatus: Option<String>,
    #[serde(default, deserialize_with = "de::opt_u64")]
    pub uptime: Option<u64>,
    #[serde(default, deserialize_with = "de::bool_or_false")]
    pub template: bool,
    /// Whether the QEMU guest agent is enabled in the VM configuration.
    #[serde(default, deserialize_with = "de::opt_bool")]
    pub agent: Option<bool>,
    #[serde(default, deserialize_with = "de::opt_string")]
    pub lock: Option<String>,
}

/// `GET /nodes/{node}/qemu/{vmid}/config`. Only a few keys are typed; the rest
/// stay available in `other`.
#[derive(Debug, Clone, PartialEq, Deserialize, Serialize)]
pub struct VmConfig {
    #[serde(default, deserialize_with = "de::opt_string")]
    pub name: Option<String>,
    #[serde(default, deserialize_with = "de::bool_or_false")]
    pub protection: bool,
    #[serde(default, deserialize_with = "de::bool_or_false")]
    pub template: bool,
    #[serde(default, deserialize_with = "de::tags")]
    pub tags: Vec<String>,
    #[serde(default, deserialize_with = "de::opt_string")]
    pub digest: Option<String>,
    #[serde(flatten)]
    pub other: BTreeMap<String, serde_json::Value>,
}

/// A polled QEMU guest execution result. No output is assumed until `exited`.
#[derive(Clone, Deserialize)]
pub struct GuestExecStatus {
    #[serde(default, deserialize_with = "de::bool_or_false")]
    pub exited: bool,
    pub exitcode: Option<i32>,
    pub signal: Option<i32>,
    #[serde(default, rename = "out-data")]
    pub stdout: String,
    #[serde(default, rename = "err-data")]
    pub stderr: String,
    #[serde(
        default,
        rename = "out-truncated",
        deserialize_with = "de::bool_or_false"
    )]
    pub stdout_truncated: bool,
    #[serde(
        default,
        rename = "err-truncated",
        deserialize_with = "de::bool_or_false"
    )]
    pub stderr_truncated: bool,
}

impl std::fmt::Debug for GuestExecStatus {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        // Guest commands may echo credentials or their encoded forms.
        f.debug_struct("GuestExecStatus")
            .field("exited", &self.exited)
            .field("exitcode", &self.exitcode)
            .field("signal", &self.signal)
            .finish_non_exhaustive()
    }
}

/// One interface from the guest agent's `network-get-interfaces`.
#[derive(Debug, Clone, PartialEq, Deserialize, Serialize)]
pub struct GuestInterface {
    #[serde(default, deserialize_with = "de::string_or_empty")]
    pub name: String,
    #[serde(
        rename = "hardware-address",
        default,
        deserialize_with = "de::opt_string"
    )]
    pub hardware_address: Option<String>,
    #[serde(rename = "ip-addresses", default)]
    pub ip_addresses: Vec<GuestIpAddress>,
}

/// One address of a [`GuestInterface`].
#[derive(Debug, Clone, PartialEq, Deserialize, Serialize)]
pub struct GuestIpAddress {
    /// `ipv4` or `ipv6`.
    #[serde(rename = "ip-address-type", deserialize_with = "de::string_or_empty")]
    pub kind: String,
    #[serde(rename = "ip-address", deserialize_with = "de::string_or_empty")]
    pub address: String,
    #[serde(default, deserialize_with = "de::opt_u64")]
    pub prefix: Option<u64>,
}

/// The non-loopback IPv4 addresses of `interfaces`, in order, without duplicates.
pub fn ipv4_addresses(interfaces: &[GuestInterface]) -> Vec<Ipv4Addr> {
    let mut out = Vec::new();
    for iface in interfaces {
        for addr in &iface.ip_addresses {
            if !addr.kind.eq_ignore_ascii_case("ipv4") {
                continue;
            }
            if let Ok(ip) = addr.address.trim().parse::<Ipv4Addr>()
                && !ip.is_loopback()
                && !ip.is_unspecified()
                && !out.contains(&ip)
            {
                out.push(ip);
            }
        }
    }
    out
}

/// `GET /access/permissions`: the privileges the token holds, by ACL path.
///
/// Proxmox lists each granted privilege with its propagate flag (`0`/`1`), so a
/// privilege counts as held whenever it is listed.
#[derive(Debug, Clone, Default, PartialEq, Eq, Serialize)]
pub struct Permissions {
    pub paths: BTreeMap<String, BTreeSet<String>>,
}

impl Permissions {
    /// Whether `privilege` is listed for `path`.
    pub fn has(&self, path: &str, privilege: &str) -> bool {
        self.paths
            .get(path)
            .is_some_and(|privs| privs.contains(privilege))
    }
}

impl<'de> Deserialize<'de> for Permissions {
    fn deserialize<D: Deserializer<'de>>(d: D) -> Result<Self, D::Error> {
        let raw = BTreeMap::<String, BTreeMap<String, serde_json::Value>>::deserialize(d)?;
        Ok(Permissions {
            paths: raw
                .into_iter()
                .map(|(path, privs)| (path, privs.into_keys().collect()))
                .collect(),
        })
    }
}
