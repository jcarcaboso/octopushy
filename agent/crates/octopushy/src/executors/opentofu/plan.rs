//! Reading `tofu show -json`, `tofu output -json`, and `tofu version -json`.
//!
//! Only the parts the contract needs are kept; the JSON plan itself can hold
//! configuration values and is never logged or returned.

use anyhow::{Context, Result};
use serde::Serialize;
use serde_json::{Map, Value};

/// The resource types of a Proxmox VM and container, whose identity a summary reports.
const GUEST_RESOURCE_TYPES: [&str; 2] = [
    "proxmox_virtual_environment_vm",
    "proxmox_virtual_environment_container",
];

/// One entry of `changes`.
#[derive(Debug, Clone, PartialEq, Eq, Serialize)]
pub struct Change {
    pub address: String,
    #[serde(rename = "type")]
    pub kind: String,
    pub name: String,
    pub actions: Vec<String>,
    /// Why OpenTofu chose this action, such as `replace_because_tainted`.
    #[serde(rename = "actionReason", skip_serializing_if = "Option::is_none")]
    pub action_reason: Option<String>,
    /// The VMID the state holds for a Proxmox VM or container. The controller lifts a guest's
    /// protection only for a change that names that guest's own VMID and node.
    #[serde(rename = "vmId", skip_serializing_if = "Option::is_none")]
    pub vm_id: Option<u64>,
    /// The node the state holds for a Proxmox VM or container.
    #[serde(skip_serializing_if = "Option::is_none")]
    pub node: Option<String>,
    /// Every VMID this change names, on any resource, before or after: a firewall rule of
    /// another guest names one too. The controller refuses a plan that names a reserved one.
    #[serde(rename = "vmIds", skip_serializing_if = "Vec::is_empty")]
    pub vm_ids: Vec<u64>,
}

/// The numbers under `vm_id` and `container_id`, at any depth. Nothing else is read, so this
/// takes nothing sensitive out of the plan.
fn named_vmids(value: &Value, found: &mut Vec<u64>) {
    match value {
        Value::Object(fields) => {
            for (key, field) in fields {
                if let ("vm_id" | "container_id", Some(vmid)) = (key.as_str(), field.as_u64())
                    && !found.contains(&vmid)
                {
                    found.push(vmid);
                }
                named_vmids(field, found);
            }
        }
        Value::Array(items) => items.iter().for_each(|item| named_vmids(item, found)),
        _ => {}
    }
}

#[derive(Debug, Clone, Copy, Default, PartialEq, Eq, Serialize)]
pub struct Counts {
    pub create: u64,
    pub update: u64,
    pub delete: u64,
    pub replace: u64,
}

#[derive(Debug, Clone, Default, PartialEq, Eq)]
pub struct PlanSummary {
    pub changes: Vec<Change>,
    pub counts: Counts,
}

/// Summarizes `resource_changes`, leaving out `no-op` and `read`. A replace
/// (`["delete","create"]` or `["create","delete"]`) counts as `replace`.
pub fn summarize(show_json: &[u8]) -> Result<PlanSummary> {
    let plan: Value =
        serde_json::from_slice(show_json).context("tofu show -json printed invalid JSON")?;
    let mut summary = PlanSummary::default();
    let Some(changes) = plan.get("resource_changes").and_then(Value::as_array) else {
        return Ok(summary);
    };
    for rc in changes {
        let actions: Vec<String> = rc
            .pointer("/change/actions")
            .and_then(Value::as_array)
            .map(|a| {
                a.iter()
                    .filter_map(|v| v.as_str().map(str::to_owned))
                    .collect()
            })
            .unwrap_or_default();
        let acts: Vec<&str> = actions.iter().map(String::as_str).collect();
        match acts.as_slice() {
            [] | ["no-op"] | ["read"] => continue,
            ["create"] => summary.counts.create += 1,
            ["update"] => summary.counts.update += 1,
            ["delete"] => summary.counts.delete += 1,
            ["delete", "create"] | ["create", "delete"] => summary.counts.replace += 1,
            _ => {} // e.g. "forget": listed, not counted
        }
        let text = |key: &str| rc.get(key).and_then(Value::as_str).unwrap_or("").to_owned();
        let kind = text("type");
        let before = rc
            .pointer("/change/before")
            .filter(|_| GUEST_RESOURCE_TYPES.contains(&kind.as_str()));
        let mut vm_ids = Vec::new();
        for side in ["/change/before", "/change/after"] {
            if let Some(value) = rc.pointer(side) {
                named_vmids(value, &mut vm_ids);
            }
        }
        summary.changes.push(Change {
            address: text("address"),
            name: text("name"),
            actions,
            action_reason: rc
                .get("action_reason")
                .and_then(Value::as_str)
                .map(str::to_owned),
            vm_id: before.and_then(|b| b.get("vm_id")).and_then(Value::as_u64),
            node: before
                .and_then(|b| b.get("node_name"))
                .and_then(Value::as_str)
                .map(str::to_owned),
            vm_ids,
            kind,
        });
    }
    Ok(summary)
}

/// Splits `tofu output -json` into the values of non-sensitive outputs and the
/// names of sensitive ones (sorted).
pub fn split_outputs(output_json: &[u8]) -> Result<(Map<String, Value>, Vec<String>)> {
    let all: Map<String, Value> =
        serde_json::from_slice(output_json).context("tofu output -json printed invalid JSON")?;
    let mut values = Map::new();
    let mut sensitive = Vec::new();
    for (name, output) in all {
        if output
            .get("sensitive")
            .and_then(Value::as_bool)
            .unwrap_or(false)
        {
            sensitive.push(name);
        } else {
            values.insert(name, output.get("value").cloned().unwrap_or(Value::Null));
        }
    }
    sensitive.sort();
    Ok((values, sensitive))
}

/// The version from `tofu version -json`.
pub fn tool_version(version_json: &[u8]) -> Option<String> {
    let v: Value = serde_json::from_slice(version_json).ok()?;
    ["terraform_version", "tofu_version", "version"]
        .iter()
        .find_map(|k| v.get(*k).and_then(Value::as_str).map(str::to_owned))
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn summarizes_and_counts_replaces() {
        let json = br#"{"format_version":"1.2","resource_changes":[
            {"address":"a.new","type":"a","name":"new","change":{"actions":["create"]}},
            {"address":"a.chg","type":"a","name":"chg","change":{"actions":["update"]}},
            {"address":"a.old","type":"a","name":"old","change":{"actions":["delete"]}},
            {"address":"a.r1","type":"a","name":"r1","change":{"actions":["delete","create"]}},
            {"address":"a.r2","type":"a","name":"r2","change":{"actions":["create","delete"]}},
            {"address":"a.same","type":"a","name":"same","change":{"actions":["no-op"]}},
            {"address":"data.x.y","type":"x","name":"y","change":{"actions":["read"]}}
        ],"configuration":{"secret":"do not leak"}}"#;
        let s = summarize(json).unwrap();
        assert_eq!(
            s.counts,
            Counts {
                create: 1,
                update: 1,
                delete: 1,
                replace: 2
            }
        );
        assert_eq!(s.changes.len(), 5);
        assert_eq!(s.changes[3].actions, ["delete", "create"]);
        assert_eq!(s.changes[0].kind, "a");
        assert!(summarize(b"{}").unwrap().changes.is_empty());
        assert!(summarize(b"nope").is_err());
    }

    #[test]
    fn reports_which_proxmox_vm_a_change_touches_and_why() {
        let json = br#"{"resource_changes":[
            {"address":"proxmox_virtual_environment_vm.guest","type":"proxmox_virtual_environment_vm","name":"guest",
             "action_reason":"replace_because_tainted",
             "change":{"actions":["delete","create"],
                       "before":{"vm_id":200,"node_name":"pve01","cloud_init":"secret user data"},
                       "after":{"vm_id":200,"node_name":"pve01"}}},
            {"address":"proxmox_virtual_environment_vm.new","type":"proxmox_virtual_environment_vm","name":"new",
             "change":{"actions":["create"],"before":null,"after":{"vm_id":201}}},
            {"address":"proxmox_virtual_environment_firewall_options.vm","type":"proxmox_virtual_environment_firewall_options","name":"vm",
             "change":{"actions":["delete","create"],"before":{"vm_id":200,"node_name":"pve01"}}}
        ]}"#;
        let s = summarize(json).unwrap();
        let vm = &s.changes[0];
        assert_eq!(vm.action_reason.as_deref(), Some("replace_because_tainted"));
        assert_eq!((vm.vm_id, vm.node.as_deref()), (Some(200), Some("pve01")));
        // A VM being created has no identity yet, and only a VM resource reports one.
        assert_eq!(
            (s.changes[1].vm_id, s.changes[1].action_reason.as_deref()),
            (None, None)
        );
        assert_eq!(
            (s.changes[2].vm_id, s.changes[2].node.as_deref()),
            (None, None)
        );

        let wire = serde_json::to_string(&s.changes).unwrap();
        assert!(
            wire.contains(r#""actionReason":"replace_because_tainted","vmId":200,"node":"pve01""#)
        );
        assert!(!wire.contains("secret user data"));
        // Absent values stay off the wire instead of appearing as nulls.
        assert!(!wire.contains("null"));
    }

    #[test]
    fn reports_which_proxmox_container_a_change_touches_and_why() {
        let json = br#"{"resource_changes":[
            {"address":"module.container.proxmox_virtual_environment_container.guest","type":"proxmox_virtual_environment_container","name":"guest",
             "action_reason":"replace_because_tainted",
             "change":{"actions":["delete","create"],
                       "before":{"vm_id":102,"node_name":"ziost","initialization":[{"user_account":[{"keys":["secret key"]}]}]},
                       "after":{"vm_id":102,"node_name":"ziost"}}},
            {"address":"proxmox_virtual_environment_container.new","type":"proxmox_virtual_environment_container","name":"new",
             "change":{"actions":["create"],"before":null,"after":{"vm_id":103}}},
            {"address":"proxmox_virtual_environment_firewall_rules.guest","type":"proxmox_virtual_environment_firewall_rules","name":"guest",
             "change":{"actions":["delete","create"],"before":{"container_id":102,"node_name":"ziost"}}}
        ]}"#;
        let s = summarize(json).unwrap();
        let container = &s.changes[0];
        assert_eq!(
            container.action_reason.as_deref(),
            Some("replace_because_tainted")
        );
        assert_eq!(
            (container.vm_id, container.node.as_deref()),
            (Some(102), Some("ziost"))
        );
        // A container being created has no identity yet, and only a guest resource reports one.
        assert_eq!(
            (s.changes[1].vm_id, s.changes[1].node.as_deref()),
            (None, None)
        );
        assert_eq!(
            (s.changes[2].vm_id, s.changes[2].node.as_deref()),
            (None, None)
        );

        let wire = serde_json::to_string(&s.changes).unwrap();
        assert!(wire.contains(r#""vmId":102,"node":"ziost""#));
        assert!(!wire.contains("secret key"));
    }

    #[test]
    fn reports_every_vmid_a_change_names_whatever_the_resource() {
        let json = br#"{"resource_changes":[
            {"address":"proxmox_virtual_environment_firewall_rules.other","type":"proxmox_virtual_environment_firewall_rules","name":"other",
             "change":{"actions":["update"],"before":{"vm_id":100,"node_name":"ziost"},"after":{"vm_id":100}}},
            {"address":"proxmox_virtual_environment_firewall_options.ct","type":"proxmox_virtual_environment_firewall_options","name":"ct",
             "change":{"actions":["create"],"before":null,"after":{"container_id":102}}},
            {"address":"proxmox_virtual_environment_vm.guest","type":"proxmox_virtual_environment_vm","name":"guest",
             "change":{"actions":["create"],"before":null,"after":{"vm_id":103,"clone":[{"vm_id":100}]}}}
        ]}"#;
        let s = summarize(json).unwrap();
        assert_eq!(s.changes[0].vm_ids, vec![100]);
        assert_eq!(s.changes[1].vm_ids, vec![102]);
        let mut both = s.changes[2].vm_ids.clone();
        both.sort_unstable();
        assert_eq!(both, vec![100, 103]);
        // Only a guest's own resource says which guest it is.
        assert_eq!(s.changes[0].vm_id, None);
    }

    #[test]
    fn withholds_sensitive_outputs() {
        let json = br#"{
            "vm_id":{"sensitive":false,"type":"number","value":210},
            "password":{"sensitive":true,"type":"string","value":"hunter2"},
            "ips":{"sensitive":false,"type":["list",["list","string"]],"value":[["10.1.0.21"]]}
        }"#;
        let (values, sensitive) = split_outputs(json).unwrap();
        assert_eq!(values["vm_id"], 210);
        assert_eq!(values["ips"][0][0], "10.1.0.21");
        assert!(!values.contains_key("password"));
        assert_eq!(sensitive, ["password"]);
    }

    #[test]
    fn reads_versions() {
        assert_eq!(
            tool_version(br#"{"terraform_version":"1.13.1","platform":"linux_amd64"}"#).as_deref(),
            Some("1.13.1")
        );
        assert_eq!(tool_version(b"OpenTofu v1.13.1"), None);
    }
}
