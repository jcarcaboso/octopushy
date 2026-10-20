//! The fixture file describing the fake installation.

use serde::{Deserialize, Serialize};

/// Privileges granted on every path unless the fixture lists its own.
pub const DEFAULT_PRIVILEGES: &[&str] = &[
    "Sys.Audit",
    "VM.Audit",
    "VM.Config.Options",
    "VM.Monitor",
    "VM.GuestAgent.Audit",
    "VM.PowerMgmt",
];

/// A fake Proxmox installation.
#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct Fixture {
    /// The only accepted API token, `USER@REALM!TOKENID=SECRET`.
    pub token: String,
    #[serde(default = "default_version")]
    pub version: String,
    /// The cluster, or null for a standalone host.
    #[serde(default)]
    pub cluster: Option<FixtureCluster>,
    pub nodes: Vec<FixtureNode>,
    #[serde(default)]
    pub vms: Vec<FixtureVm>,
    /// Privileges the token holds on every path; defaults to
    /// [`DEFAULT_PRIVILEGES`].
    #[serde(default)]
    pub privileges: Option<Vec<String>>,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct FixtureCluster {
    pub name: String,
    #[serde(default = "yes")]
    pub quorate: bool,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct FixtureNode {
    pub name: String,
    #[serde(default = "yes")]
    pub online: bool,
    #[serde(default = "default_cpu")]
    pub cpu: f64,
    #[serde(default = "default_node_cpus")]
    pub max_cpu: u64,
    #[serde(default = "default_node_mem")]
    pub memory_bytes: u64,
    #[serde(default = "default_node_maxmem")]
    pub max_memory_bytes: u64,
    #[serde(default = "default_uptime")]
    pub uptime_seconds: u64,
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct FixtureVm {
    pub vmid: u32,
    pub name: String,
    pub node: String,
    /// `running`, `stopped`, or `paused`.
    #[serde(default = "default_status")]
    pub status: String,
    #[serde(default)]
    pub template: bool,
    #[serde(default)]
    pub tags: Vec<String>,
    #[serde(default)]
    pub protection: bool,
    /// Addresses the guest agent reports, or null when the agent is not running.
    #[serde(default)]
    pub ipv4: Option<Vec<String>>,
    /// `qemu` or `lxc`.
    #[serde(default = "default_kind")]
    pub kind: String,
    #[serde(default = "default_vm_cpus")]
    pub max_cpu: u64,
    #[serde(default = "default_vm_mem")]
    pub max_memory_bytes: u64,
    #[serde(default)]
    pub uptime_seconds: u64,
}

impl Fixture {
    /// Parses a fixture from JSON.
    pub fn from_json(json: &str) -> anyhow::Result<Self> {
        let fixture: Fixture = serde_json::from_str(json)?;
        fixture.validate()?;
        Ok(fixture)
    }

    /// The example fixture shipped with the crate (`fixtures/home.json`).
    pub fn example() -> Self {
        Self::from_json(include_str!("../fixtures/home.json"))
            .expect("the example fixture is valid")
    }

    /// Checks that VMIDs are unique and every VM is on a known node.
    pub fn validate(&self) -> anyhow::Result<()> {
        let mut seen = std::collections::BTreeSet::new();
        for vm in &self.vms {
            anyhow::ensure!(seen.insert(vm.vmid), "duplicate VMID {}", vm.vmid);
            anyhow::ensure!(
                self.nodes.iter().any(|n| n.name == vm.node),
                "VM {} is on unknown node {}",
                vm.vmid,
                vm.node
            );
        }
        Ok(())
    }

    pub fn privileges(&self) -> Vec<String> {
        self.privileges
            .clone()
            .unwrap_or_else(|| DEFAULT_PRIVILEGES.iter().map(|s| (*s).to_owned()).collect())
    }
}

fn yes() -> bool {
    true
}
fn default_version() -> String {
    "8.4.1".into()
}
fn default_cpu() -> f64 {
    0.05
}
fn default_node_cpus() -> u64 {
    16
}
fn default_node_mem() -> u64 {
    12 * 1024 * 1024 * 1024
}
fn default_node_maxmem() -> u64 {
    64 * 1024 * 1024 * 1024
}
fn default_uptime() -> u64 {
    86_400
}
fn default_status() -> String {
    "running".into()
}
fn default_kind() -> String {
    "qemu".into()
}
fn default_vm_cpus() -> u64 {
    2
}
fn default_vm_mem() -> u64 {
    2 * 1024 * 1024 * 1024
}
