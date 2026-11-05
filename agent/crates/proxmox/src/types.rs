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
    /// `qemu` or `lxc`, as Proxmox names them; anything else as given.
    pub fn parse(text: &str) -> Self {
        match text {
            "qemu" => GuestKind::Qemu,
            "lxc" => GuestKind::Lxc,
            other => GuestKind::Other(other.to_owned()),
        }
    }

    /// The guest as messages name it: `VM` for QEMU, `container` for LXC.
    pub fn noun(&self) -> &'static str {
        match self {
            GuestKind::Lxc => "container",
            _ => "VM",
        }
    }

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
        Ok(GuestKind::parse(&de::string_or_empty(d)?))
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

/// `GET /nodes/{node}/qemu/{vmid}/status/current`. A container's is [`CtStatus`].
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
/// stay available in `other`. A container's is [`CtConfig`].
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

/// `GET /nodes/{node}/lxc/{vmid}/status/current`.
#[derive(Debug, Clone, PartialEq, Deserialize, Serialize)]
pub struct CtStatus {
    #[serde(default, deserialize_with = "de::string_or_empty")]
    pub status: String,
    #[serde(default, deserialize_with = "de::opt_string")]
    pub name: Option<String>,
    #[serde(default, deserialize_with = "de::opt_u64")]
    pub uptime: Option<u64>,
    #[serde(default, deserialize_with = "de::bool_or_false")]
    pub template: bool,
    #[serde(default, deserialize_with = "de::opt_string")]
    pub lock: Option<String>,
}

/// `GET /nodes/{node}/lxc/{vmid}/config`. A container's name is its `hostname`. Only a few
/// keys are typed; the rest stay available in `other`.
#[derive(Debug, Clone, PartialEq, Deserialize, Serialize)]
pub struct CtConfig {
    #[serde(default, deserialize_with = "de::opt_string")]
    pub hostname: Option<String>,
    #[serde(default, deserialize_with = "de::opt_string")]
    pub description: Option<String>,
    #[serde(default, deserialize_with = "de::bool_or_false")]
    pub protection: bool,
    #[serde(default, deserialize_with = "de::bool_or_false")]
    pub template: bool,
    #[serde(default, deserialize_with = "de::bool_or_false")]
    pub unprivileged: bool,
    #[serde(default, deserialize_with = "de::tags")]
    pub tags: Vec<String>,
    #[serde(default, deserialize_with = "de::opt_string")]
    pub digest: Option<String>,
    #[serde(flatten)]
    pub other: BTreeMap<String, serde_json::Value>,
}

/// One interface of `GET /nodes/{node}/lxc/{vmid}/interfaces`, which Proxmox reads with
/// `ip --json a` in the container's network namespace (`PVE::LXC::get_interfaces`). `inet` and
/// `inet6` hold one address each, with its prefix, and are deprecated; since pve-container 5.2.6,
/// `ip-addresses` lists every address, its type being `ip`'s family, `inet` or `inet6`. Both are read.
#[derive(Debug, Clone, PartialEq, Deserialize, Serialize)]
pub struct CtInterface {
    #[serde(default, deserialize_with = "de::string_or_empty")]
    pub name: String,
    #[serde(default, deserialize_with = "de::opt_string")]
    pub hwaddr: Option<String>,
    #[serde(default, deserialize_with = "de::opt_string")]
    pub inet: Option<String>,
    #[serde(default, deserialize_with = "de::opt_string")]
    pub inet6: Option<String>,
    #[serde(rename = "ip-addresses", default)]
    pub ip_addresses: Vec<GuestIpAddress>,
}

/// The non-loopback IPv4 addresses of a container's `interfaces`, in order, without duplicates.
pub fn ct_ipv4_addresses(interfaces: &[CtInterface]) -> Vec<Ipv4Addr> {
    let mut out = Vec::new();
    for iface in interfaces {
        let listed = iface
            .ip_addresses
            .iter()
            .filter(|a| a.kind.eq_ignore_ascii_case("inet") || a.kind.eq_ignore_ascii_case("ipv4"))
            .map(|a| a.address.as_str());
        for text in iface.inet.as_deref().into_iter().chain(listed) {
            let text = text.split('/').next().unwrap_or(text).trim();
            if let Ok(ip) = text.parse::<Ipv4Addr>()
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

/// One entry of `GET /nodes/{node}/tasks`.
#[derive(Debug, Clone, PartialEq, Deserialize, Serialize)]
pub struct Task {
    #[serde(deserialize_with = "de::string_or_empty")]
    pub upid: String,
    /// `vzcreate`, `qmcreate`, `vzstart`, …
    #[serde(rename = "type", default, deserialize_with = "de::string_or_empty")]
    pub kind: String,
    /// The guest's VMID, as a string.
    #[serde(default, deserialize_with = "de::opt_string")]
    pub id: Option<String>,
    #[serde(default, deserialize_with = "de::opt_string")]
    pub user: Option<String>,
    #[serde(default, deserialize_with = "de::opt_u64")]
    pub starttime: Option<u64>,
    #[serde(default, deserialize_with = "de::opt_u64")]
    pub endtime: Option<u64>,
    /// `OK` for a task that succeeded, an error otherwise; absent while it runs.
    #[serde(default, deserialize_with = "de::opt_string")]
    pub status: Option<String>,
}

/// One line of `GET /nodes/{node}/tasks/{upid}/log`.
#[derive(Debug, Clone, PartialEq, Deserialize, Serialize)]
pub struct TaskLogLine {
    #[serde(default, deserialize_with = "de::opt_u64")]
    pub n: Option<u64>,
    #[serde(default, deserialize_with = "de::string_or_empty")]
    pub t: String,
}

/// One SSH host key Proxmox generated for a container when it created it, as the create task
/// logged it: the key's type (`rsa`, `ecdsa`, `ed25519`) and its SHA-256 fingerprint.
#[derive(Debug, Clone, PartialEq, Eq, Serialize)]
pub struct CreatedHostKey {
    pub kind: String,
    pub fingerprint: String,
}

/// The SSH host keys a container's create task logged. Proxmox (`PVE::LXC::Setup`,
/// `rewrite_ssh_host_keys`) generates them on the host and writes them into the container's
/// root file system before it first starts, whenever the template has `/etc/ssh`, and logs each
/// as `Creating SSH host key 'ssh_host_<type>_key' - …` followed by `done: SHA256:<43> <comment>`.
/// A key whose `done:` line has no fingerprint is left out.
pub fn created_host_keys(log: &[TaskLogLine]) -> Vec<CreatedHostKey> {
    let mut out = Vec::new();
    let mut pending: Option<String> = None;
    for line in log.iter().map(|l| l.t.trim()) {
        if let Some(rest) = line.strip_prefix("Creating SSH host key 'ssh_host_") {
            pending = rest
                .split_once("_key'")
                .map(|(kind, _)| kind.to_owned())
                .filter(|k| !k.is_empty() && k.bytes().all(|b| b.is_ascii_alphanumeric()));
            continue;
        }
        let Some(done) = line.strip_prefix("done: ") else {
            continue;
        };
        let Some(kind) = pending.take() else {
            continue;
        };
        let fingerprint = done.split_whitespace().next().unwrap_or("");
        if is_sha256_fingerprint(fingerprint)
            && !out.iter().any(|k: &CreatedHostKey| k.kind == kind)
        {
            out.push(CreatedHostKey {
                kind,
                fingerprint: fingerprint.to_owned(),
            });
        }
    }
    out
}

/// `SHA256:` and 43 characters of unpadded base64: an OpenSSH SHA-256 fingerprint.
pub fn is_sha256_fingerprint(text: &str) -> bool {
    text.strip_prefix("SHA256:").is_some_and(|b64| {
        b64.len() == 43
            && b64
                .bytes()
                .all(|b| b.is_ascii_alphanumeric() || b == b'+' || b == b'/')
    })
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
/// privilege counts as held on the path itself whenever it is listed
/// ([`Permissions::has`]); it reaches the paths below only when it propagates
/// ([`Permissions::propagates`]).
#[derive(Debug, Clone, Default, PartialEq, Eq, Serialize)]
pub struct Permissions {
    pub paths: BTreeMap<String, BTreeSet<String>>,
    /// The privileges of each path whose propagate flag is set.
    #[serde(skip)]
    pub propagating: BTreeMap<String, BTreeSet<String>>,
}

impl Permissions {
    /// Whether `privilege` is listed for `path`.
    pub fn has(&self, path: &str, privilege: &str) -> bool {
        self.paths
            .get(path)
            .is_some_and(|privs| privs.contains(privilege))
    }

    /// Whether `privilege` is held on `path` and propagates to the paths below it.
    pub fn propagates(&self, path: &str, privilege: &str) -> bool {
        self.propagating
            .get(path)
            .is_some_and(|privs| privs.contains(privilege))
    }
}

impl<'de> Deserialize<'de> for Permissions {
    fn deserialize<D: Deserializer<'de>>(d: D) -> Result<Self, D::Error> {
        let raw = BTreeMap::<String, BTreeMap<String, serde_json::Value>>::deserialize(d)?;
        let set = |v: &serde_json::Value| {
            v.as_u64() == Some(1) || v.as_bool() == Some(true) || v.as_str() == Some("1")
        };
        Ok(Permissions {
            propagating: raw
                .iter()
                .map(|(path, privs)| {
                    let held = privs
                        .iter()
                        .filter(|(_, flag)| set(flag))
                        .map(|(p, _)| p.clone());
                    (path.clone(), held.collect())
                })
                .collect(),
            paths: raw
                .into_iter()
                .map(|(path, privs)| (path, privs.into_keys().collect()))
                .collect(),
        })
    }
}

/// One entry of `GET /access/acl`: a role given to a user, group or token on a path.
#[derive(Debug, Clone, PartialEq, Eq, Deserialize, Serialize)]
pub struct AclEntry {
    pub path: String,
    #[serde(default)]
    pub ugid: String,
    #[serde(rename = "type", default)]
    pub kind: String,
    #[serde(default)]
    pub roleid: String,
}

#[cfg(test)]
mod host_key_tests {
    use super::{TaskLogLine, created_host_keys, is_sha256_fingerprint};

    fn log(lines: &[&str]) -> Vec<TaskLogLine> {
        lines
            .iter()
            .enumerate()
            .map(|(i, t)| TaskLogLine {
                n: Some(i as u64 + 1),
                t: (*t).to_owned(),
            })
            .collect()
    }

    const ED: &str = "SHA256:Zm9vYmFyYmF6cXV4cXV1eGNvcmdlZ3JhdWx0Z2FycA0";
    const RSA: &str = "SHA256:cnNhcnNhcnNhcnNhcnNhcnNhcnNhcnNhcnNhcnNhcnM";

    #[test]
    fn reads_the_fingerprints_a_create_task_logged() {
        let keys = created_host_keys(&log(&[
            "extracting archive '/var/lib/vz/template/cache/debian-13-standard_13.6-1_amd64.tar.zst'",
            "Total bytes read: 524288000 (500MiB, 120MiB/s)",
            "Detected container architecture: amd64",
            "Creating SSH host key 'ssh_host_rsa_key' - this may take some time ...",
            &format!("done: {RSA} root@ct-01"),
            "Creating SSH host key 'ssh_host_ed25519_key' - this may take some time ...",
            &format!("done: {ED} root@ct-01"),
            "TASK OK",
        ]));
        assert_eq!(keys.len(), 2);
        assert_eq!(
            (keys[0].kind.as_str(), keys[0].fingerprint.as_str()),
            ("rsa", RSA)
        );
        assert_eq!(
            (keys[1].kind.as_str(), keys[1].fingerprint.as_str()),
            ("ed25519", ED)
        );
    }

    #[test]
    fn a_line_out_of_place_or_without_a_fingerprint_counts_for_nothing() {
        let keys = created_host_keys(&log(&[
            &format!("done: {ED} root@ct-01"),
            "Creating SSH host key 'ssh_host_ecdsa_key' - this may take some time ...",
            "done: ",
            "Creating SSH host key 'ssh_host_../x_key' - this may take some time ...",
            &format!("done: {ED} root@ct-01"),
            "Creating SSH host key 'ssh_host_ed25519_key' - this may take some time ...",
            "done: aa:bb:cc root@ct-01",
        ]));
        assert!(keys.is_empty(), "{keys:?}");
        assert!(is_sha256_fingerprint(ED));
        assert!(!is_sha256_fingerprint("SHA256:short"));
        assert!(!is_sha256_fingerprint(&format!("{ED}'")));
    }
}

#[cfg(test)]
mod permission_tests {
    use super::Permissions;

    #[test]
    fn a_privilege_is_held_where_listed_and_reaches_below_only_when_it_propagates() {
        let p: Permissions =
            serde_json::from_str(r#"{"/vms":{"VM.Audit":0,"VM.Monitor":1}}"#).unwrap();
        assert!(p.has("/vms", "VM.Audit"));
        assert!(!p.propagates("/vms", "VM.Audit"));
        assert!(p.has("/vms", "VM.Monitor"));
        assert!(p.propagates("/vms", "VM.Monitor"));
        assert!(!p.has("/vms", "Sys.Audit"));
    }
}
