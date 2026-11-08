//! Reading `tofu show -json`, `tofu output -json`, and `tofu version -json`.
//!
//! Only the parts the contract needs are kept; the JSON plan itself can hold
//! configuration values and is never logged or returned. The only values a change
//! carries out of it are the few VM attributes in [`VM_ATTRIBUTES`], and never one
//! the plan marks sensitive.

use anyhow::{Context, Result};
use serde::Serialize;
use serde_json::{Map, Value};

/// The resource type of a Proxmox VM, whose identity a summary reports.
const VM_RESOURCE_TYPE: &str = "proxmox_virtual_environment_vm";
const CONTAINER_RESOURCE_TYPE: &str = "proxmox_virtual_environment_container";

/// What every resource type of a Proxmox provider starts with: `bpg/proxmox` names its
/// resources `proxmox_virtual_environment_*` and, newer ones, `proxmox_*`.
const PROXMOX_TYPE_PREFIX: &str = "proxmox_";

/// Top-level attributes of a Proxmox resource that name the VM or container it acts on.
const VM_FIELDS: &[&str] = &["vm_id", "vmid", "container_id"];
/// Top-level attributes of a Proxmox resource that name the node it acts on.
const NODE_FIELDS: &[&str] = &["node_name", "node", "target_node"];
/// Top-level attributes that name a guest inside a string: `vm:100`, `ct:100`, `/vms/100`.
const REFERENCE_FIELDS: &[&str] = &["resource_id", "path"];

/// The attributes of a Proxmox VM whose old and new values a change carries, by name and
/// where `tofu show -json` holds them. bpg/proxmox holds each block as a list: `*` is a
/// `disk` entry, named by its `interface`, or a `network_device` entry, named by its index.
const VM_ATTRIBUTES: &[(&str, &str)] = &[
    ("cpu.cores", "/cpu/0/cores"),
    ("cpu.sockets", "/cpu/0/sockets"),
    ("memory.dedicated", "/memory/0/dedicated"),
    ("disk.*.size", "/disk/*/size"),
    ("tags", "/tags"),
    ("network_device.*.bridge", "/network_device/*/bridge"),
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
    /// The VMID the state holds for a Proxmox VM. The controller lifts a VM's
    /// protection only for a change that names that guest's own VMID and node.
    #[serde(rename = "vmId", skip_serializing_if = "Option::is_none")]
    pub vm_id: Option<u64>,
    /// The node the state holds for a Proxmox VM.
    #[serde(skip_serializing_if = "Option::is_none")]
    pub node: Option<String>,
    /// The change adopts an existing object into the state (`change.importing`).
    pub importing: bool,
    /// The resource belongs to a Proxmox provider.
    #[serde(skip_serializing_if = "std::ops::Not::not")]
    pub proxmox: bool,
    /// Every VM and node a Proxmox resource names, before and after the change.
    #[serde(skip_serializing_if = "Vec::is_empty")]
    pub targets: Vec<Target>,
    /// One of the attributes that name a VM or node is not known until apply.
    #[serde(rename = "targetUnknown", skip_serializing_if = "std::ops::Not::not")]
    pub target_unknown: bool,
    /// The [`VM_ATTRIBUTES`] of a Proxmox VM that the change sets to something else.
    #[serde(skip_serializing_if = "Vec::is_empty")]
    pub attributes: Vec<Attribute>,
}

/// An attribute of a Proxmox VM, before and after a change. A side that does not exist is
/// absent; a sensitive attribute carries neither side.
#[derive(Debug, Clone, PartialEq, Eq, Serialize)]
pub struct Attribute {
    pub name: String,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub before: Option<Value>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub after: Option<Value>,
    /// The new value is not known until apply.
    #[serde(skip_serializing_if = "std::ops::Not::not")]
    pub unknown: bool,
    #[serde(skip_serializing_if = "std::ops::Not::not")]
    pub sensitive: bool,
}

/// A VM or container, or a node, that a Proxmox resource names.
#[derive(Debug, Clone, PartialEq, Eq, PartialOrd, Ord, Serialize)]
pub struct Target {
    #[serde(rename = "vmId", skip_serializing_if = "Option::is_none")]
    pub vm_id: Option<u64>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub node: Option<String>,
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

/// A VMID given as a number or as a string of digits.
fn vmid(value: &Value) -> Option<u64> {
    value
        .as_u64()
        .or_else(|| value.as_str().and_then(|s| s.parse().ok()))
}

/// The VMID in `vm:100`, `ct:100`, `qemu/100`, `lxc/100` or `/vms/100`.
fn referenced_vmid(text: &str) -> Option<u64> {
    let rest = ["vm:", "ct:", "qemu/", "lxc/", "/vms/"]
        .iter()
        .find_map(|prefix| text.strip_prefix(prefix))?;
    rest.split('/').next()?.parse().ok()
}

/// What one side of a change, `before` or `after`, names.
fn targets_of(values: &Value, out: &mut Vec<Target>) {
    if !values.is_object() {
        return;
    }
    let node = NODE_FIELDS
        .iter()
        .find_map(|f| values.get(f).and_then(Value::as_str))
        .map(str::to_owned);
    let vmids: Vec<u64> = VM_FIELDS
        .iter()
        .filter_map(|f| values.get(f).and_then(vmid))
        .chain(REFERENCE_FIELDS.iter().filter_map(|f| {
            values
                .get(f)
                .and_then(Value::as_str)
                .and_then(referenced_vmid)
        }))
        .collect();
    if vmids.is_empty() {
        if node.is_some() {
            out.push(Target { vm_id: None, node });
        }
        return;
    }
    for vm_id in vmids {
        out.push(Target {
            vm_id: Some(vm_id),
            node: node.clone(),
        });
    }
}

/// The value at `pointer` on one side of a change, `before` or `after`, and whether
/// `<side>_sensitive` and `<side>_unknown` mark it, a parent of it, or anything inside it.
fn read<'a>(change: &'a Value, side: &str, pointer: &str) -> (Option<&'a Value>, bool, bool) {
    fn holds_true(marks: &Value) -> bool {
        match marks {
            Value::Bool(b) => *b,
            Value::Array(a) => a.iter().any(holds_true),
            Value::Object(o) => o.values().any(holds_true),
            _ => false,
        }
    }
    let marked = |marks: Option<&Value>| {
        marks.is_some_and(|marks| {
            pointer
                .match_indices('/')
                .any(|(i, _)| marks.pointer(&pointer[..i]) == Some(&Value::Bool(true)))
                || marks.pointer(pointer).is_some_and(holds_true)
        })
    };
    (
        change
            .get(side)
            .and_then(|v| v.pointer(pointer))
            .filter(|v| !v.is_null()),
        marked(change.get(format!("{side}_sensitive"))),
        marked(change.get(format!("{side}_unknown"))),
    )
}

/// The [`VM_ATTRIBUTES`] whose value differs between `before` and `after` of `change`.
fn vm_attributes(change: &Value) -> Vec<Attribute> {
    let mut out = Vec::new();
    for (name, pointer) in VM_ATTRIBUTES {
        // Each entry of a block, by its name, and its index on each side. `-` names no
        // entry, so a missing side reads as absent and only the block's own marks count.
        let mut entries = std::collections::BTreeMap::<String, [String; 2]>::new();
        if let Some((block, _)) = pointer.split_once("/*") {
            for (s, side) in ["before", "after"].into_iter().enumerate() {
                let Some(list) = change.get(side).and_then(|v| v.pointer(block)) else {
                    continue;
                };
                for i in 0..list.as_array().map_or(0, Vec::len) {
                    let key = if block == "/disk" {
                        // Only a plain interface such as `scsi0` names a disk.
                        let (Some(Value::String(k)), false, _) =
                            read(change, side, &format!("{block}/{i}/interface"))
                        else {
                            continue;
                        };
                        let letters = k.trim_end_matches(|c: char| c.is_ascii_digit());
                        if letters.is_empty()
                            || letters.len() == k.len()
                            || !letters.chars().all(|c| c.is_ascii_lowercase())
                        {
                            continue;
                        }
                        k.clone()
                    } else {
                        i.to_string()
                    };
                    entries
                        .entry(key)
                        .or_insert_with(|| ["-".into(), "-".into()])[s] = i.to_string();
                }
            }
        } else {
            entries.insert(String::new(), [String::new(), String::new()]);
        }
        for (key, [b, a]) in entries {
            let (before, before_sensitive, _) = read(change, "before", &pointer.replace('*', &b));
            let (after, after_sensitive, unknown) =
                read(change, "after", &pointer.replace('*', &a));
            let after = after.filter(|_| !unknown);
            let tags = *name == "tags";
            let plain = |v: &Value| match v {
                Value::Number(_) | Value::String(_) | Value::Bool(_) => true,
                Value::Array(a) => tags && a.iter().all(Value::is_string),
                _ => false,
            };
            if before.into_iter().chain(after).any(|v| !plain(v)) {
                continue;
            }
            let sorted = |v: &Value| {
                let mut v = v.clone();
                if let Value::Array(a) = &mut v {
                    a.sort_by(|x, y| x.as_str().cmp(&y.as_str()));
                }
                v
            };
            let (before, after) = (before.map(sorted), after.map(sorted));
            if !unknown && before == after {
                continue;
            }
            let sensitive = before_sensitive || after_sensitive;
            out.push(Attribute {
                name: name.replace('*', &key),
                before: before.filter(|_| !sensitive),
                after: after.filter(|_| !sensitive),
                unknown,
                sensitive,
            });
        }
    }
    out
}

/// Summarizes `resource_changes`, leaving out `no-op` and `read` unless the change
/// imports something. A replace (`["delete","create"]` or `["create","delete"]`)
/// counts as `replace`.
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
        let importing = rc
            .pointer("/change/importing")
            .is_some_and(|i| !i.is_null());
        let acts: Vec<&str> = actions.iter().map(String::as_str).collect();
        match acts.as_slice() {
            [] | ["no-op"] | ["read"] if !importing => continue,
            ["create"] => summary.counts.create += 1,
            ["update"] => summary.counts.update += 1,
            ["delete"] => summary.counts.delete += 1,
            ["delete", "create"] | ["create", "delete"] => summary.counts.replace += 1,
            _ => {} // e.g. "forget", or an import that changes nothing: listed, not counted
        }
        let text = |key: &str| rc.get(key).and_then(Value::as_str).unwrap_or("").to_owned();
        let kind = text("type");
        let machine = kind == VM_RESOURCE_TYPE || kind == CONTAINER_RESOURCE_TYPE;
        let proxmox = kind.starts_with(PROXMOX_TYPE_PREFIX)
            || rc
                .get("provider_name")
                .and_then(Value::as_str)
                .is_some_and(|p| p.ends_with("/proxmox"));
        let before = rc
            .pointer("/change/before")
            .filter(|b| machine && !b.is_null());
        // A machine that does not exist yet is named by what it would be. Only then: anything else
        // that changes is named by the state, not by what the config says.
        let creating = matches!(actions.as_slice(), [a] if a == "create");
        let identity =
            before.or_else(|| rc.pointer("/change/after").filter(|_| machine && creating));
        let mut targets = Vec::new();
        let mut target_unknown = false;
        if proxmox {
            for side in ["/change/before", "/change/after"] {
                if let Some(values) = rc.pointer(side) {
                    targets_of(values, &mut targets);
                }
            }
            targets.sort();
            targets.dedup();
            target_unknown = rc
                .pointer("/change/after_unknown")
                .and_then(Value::as_object)
                .is_some_and(|unknown| {
                    VM_FIELDS
                        .iter()
                        .chain(NODE_FIELDS)
                        .chain(REFERENCE_FIELDS)
                        .any(|f| unknown.get(*f).is_some_and(|u| u != &Value::Bool(false)))
                });
        }
        summary.changes.push(Change {
            address: text("address"),
            name: text("name"),
            actions,
            action_reason: rc
                .get("action_reason")
                .and_then(Value::as_str)
                .map(str::to_owned),
            // What exists, or for a pure create what would.
            vm_id: identity
                .and_then(|b| b.get("vm_id"))
                .and_then(Value::as_u64),
            node: identity
                .and_then(|b| b.get("node_name"))
                .and_then(Value::as_str)
                .map(str::to_owned),
            importing,
            proxmox,
            targets,
            target_unknown,
            attributes: match rc.get("change") {
                Some(change) if kind == VM_RESOURCE_TYPE => vm_attributes(change),
                _ => Vec::new(),
            },
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
    fn a_container_is_named_like_a_vm() {
        // What bpg/proxmox 0.117.0 plans for a container whose keys changed with no ignore_changes,
        // trimmed: user_account is ForceNew, so it is a replacement of the same VMID and node.
        let json = br#"{"resource_changes":[
            {"address":"proxmox_virtual_environment_container.guest","type":"proxmox_virtual_environment_container","name":"guest",
             "provider_name":"registry.opentofu.org/bpg/proxmox","action_reason":"replace_because_cannot_update",
             "change":{"actions":["delete","create"],
                       "before":{"vm_id":320,"node_name":"korriban","unprivileged":true},
                       "after":{"vm_id":320,"node_name":"korriban","unprivileged":true},
                       "after_unknown":{"id":true,"ipv4":true}}},
            {"address":"proxmox_virtual_environment_container.new","type":"proxmox_virtual_environment_container","name":"new",
             "provider_name":"registry.opentofu.org/bpg/proxmox",
             "change":{"actions":["create"],"before":null,"after":{"vm_id":321,"node_name":"korriban"}}}
        ]}"#;
        let s = summarize(json).unwrap();
        assert_eq!(
            s.counts,
            Counts {
                create: 1,
                replace: 1,
                ..Counts::default()
            }
        );
        let replaced = &s.changes[0];
        assert!(replaced.proxmox && !replaced.target_unknown);
        assert_eq!(
            (replaced.vm_id, replaced.node.as_deref()),
            (Some(320), Some("korriban"))
        );
        assert_eq!(
            replaced.targets,
            vec![Target {
                vm_id: Some(320),
                node: Some("korriban".into())
            }]
        );
        assert_eq!(
            (s.changes[1].vm_id, s.changes[1].node.as_deref()),
            (Some(321), Some("korriban"))
        );
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
        // A VM being created is named by what it would be, and only a machine reports one.
        assert_eq!(
            (s.changes[1].vm_id, s.changes[1].action_reason.as_deref()),
            (Some(201), None)
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
    fn reports_every_vm_and_node_a_proxmox_resource_names() {
        let json = br#"{"resource_changes":[
            {"address":"proxmox_virtual_environment_firewall_options.other","type":"proxmox_virtual_environment_firewall_options",
             "name":"other","provider_name":"registry.opentofu.org/bpg/proxmox",
             "change":{"actions":["create"],"before":null,"after":{"vm_id":100,"node_name":"pve01","enabled":true}}},
            {"address":"proxmox_virtual_environment_haresource.ha","type":"proxmox_virtual_environment_haresource","name":"ha",
             "change":{"actions":["update"],"before":{"resource_id":"vm:100"},"after":{"resource_id":"ct:101"}}},
            {"address":"proxmox_virtual_environment_vm.moved","type":"proxmox_virtual_environment_vm","name":"moved",
             "change":{"actions":["update"],"before":{"vm_id":310,"node_name":"pve01"},"after":{"vm_id":"311","node_name":"pve02"},
                       "after_unknown":{"vm_id":false,"ipv4_addresses":true}}},
            {"address":"proxmox_virtual_environment_network_linux_bridge.br","type":"proxmox_virtual_environment_network_linux_bridge",
             "name":"br","change":{"actions":["create"],"after":{"node_name":"pve01"},"after_unknown":{"node_name":true}}},
            {"address":"terraform_data.x","type":"terraform_data","name":"x",
             "change":{"actions":["create"],"after":{"vm_id":100}}}
        ]}"#;
        let s = summarize(json).unwrap();
        let target = |vm: Option<u64>, node: Option<&str>| Target {
            vm_id: vm,
            node: node.map(str::to_owned),
        };
        assert!(s.changes[0].proxmox);
        assert_eq!(s.changes[0].targets, [target(Some(100), Some("pve01"))]);
        assert_eq!(
            s.changes[1].targets,
            [target(Some(100), None), target(Some(101), None)]
        );
        assert_eq!(
            s.changes[2].targets,
            [
                target(Some(310), Some("pve01")),
                target(Some(311), Some("pve02"))
            ]
        );
        assert!(!s.changes[2].target_unknown);
        assert_eq!(s.changes[3].targets, [target(None, Some("pve01"))]);
        assert!(s.changes[3].target_unknown);
        // Only a Proxmox provider's resources are read for targets.
        assert!(!s.changes[4].proxmox);
        assert!(s.changes[4].targets.is_empty());

        let wire = serde_json::to_string(&s.changes[0]).unwrap();
        assert!(wire.contains(
            r#""importing":false,"proxmox":true,"targets":[{"vmId":100,"node":"pve01"}]"#
        ));
    }

    #[test]
    fn reports_imports_even_when_they_change_nothing() {
        let json = br#"{"resource_changes":[
            {"address":"proxmox_virtual_environment_vm.adopted","type":"proxmox_virtual_environment_vm","name":"adopted",
             "change":{"actions":["no-op"],"importing":{"id":"pve01/100"},
                       "before":{"vm_id":100,"node_name":"pve01"},"after":{"vm_id":100,"node_name":"pve01"}}},
            {"address":"terraform_data.same","type":"terraform_data","name":"same","change":{"actions":["no-op"],"importing":null}}
        ]}"#;
        let s = summarize(json).unwrap();
        assert_eq!(s.changes.len(), 1);
        assert!(s.changes[0].importing);
        assert_eq!(s.changes[0].actions, ["no-op"]);
        assert_eq!(s.counts, Counts::default());
        assert!(
            serde_json::to_string(&s.changes[0])
                .unwrap()
                .contains(r#""importing":true"#)
        );
    }

    #[test]
    fn reports_the_vm_attributes_an_update_changes() {
        // How bpg/proxmox shows a VM in `tofu show -json`, trimmed: every block is a list.
        let json = br#"{"resource_changes":[
            {"address":"proxmox_virtual_environment_vm.guest","type":"proxmox_virtual_environment_vm","name":"guest",
             "change":{"actions":["update"],
                       "before":{"vm_id":210,"node_name":"pve01","tags":["octopushy","old"],"description":"secret note",
                                 "cpu":[{"cores":2,"sockets":1,"type":"x86-64-v2-AES"}],"memory":[{"dedicated":4096}],
                                 "disk":[{"interface":"scsi0","size":32,"datastore_id":"local-lvm"}],
                                 "network_device":[{"bridge":"vmbr0","mac_address":"BC:24:11:00:00:01"}],
                                 "initialization":[{"user_account":[{"password":"hunter2"}]}]},
                       "after":{"vm_id":210,"node_name":"pve01","tags":["agents","octopushy"],"description":"secret note",
                                "cpu":[{"cores":2,"sockets":1,"type":"x86-64-v2-AES"}],"memory":[{"dedicated":8192}],
                                "disk":[{"interface":"scsi1","size":100,"datastore_id":"local-lvm"},
                                        {"interface":"scsi0","size":64,"datastore_id":"local-lvm"}],
                                "network_device":[{"bridge":"vmbr0","mac_address":"BC:24:11:00:00:02"}],
                                "initialization":[{"user_account":[{"password":"hunter3"}]}]}}}
        ]}"#;
        let s = summarize(json).unwrap();
        let wire = serde_json::to_value(&s.changes[0].attributes).unwrap();
        assert_eq!(
            wire,
            serde_json::json!([
                { "name": "memory.dedicated", "before": 4096, "after": 8192 },
                { "name": "disk.scsi0.size", "before": 32, "after": 64 },
                { "name": "disk.scsi1.size", "after": 100 },
                { "name": "tags", "before": ["octopushy", "old"], "after": ["agents", "octopushy"] }
            ])
        );
        let all = serde_json::to_string(&s.changes).unwrap();
        for withheld in ["secret note", "hunter", "BC:24", "local-lvm", "x86-64"] {
            assert!(!all.contains(withheld), "{withheld} left the agent");
        }
    }

    #[test]
    fn withholds_sensitive_vm_attributes() {
        let json = br#"{"resource_changes":[
            {"address":"proxmox_virtual_environment_vm.guest","type":"proxmox_virtual_environment_vm","name":"guest",
             "change":{"actions":["update"],
                       "before":{"tags":["hidden-a"],"memory":[{"dedicated":4096}],"cpu":[{"cores":2}]},
                       "after":{"tags":["hidden-b"],"memory":[{"dedicated":8192}],"cpu":[{"cores":2}]},
                       "before_sensitive":{"tags":[true]},
                       "after_sensitive":{"memory":true,"cpu":[{"cores":true}]}}}
        ]}"#;
        let s = summarize(json).unwrap();
        let wire = serde_json::to_value(&s.changes[0].attributes).unwrap();
        // An unchanged sensitive value is not reported at all.
        assert_eq!(
            wire,
            serde_json::json!([
                { "name": "memory.dedicated", "sensitive": true },
                { "name": "tags", "sensitive": true }
            ])
        );
        let all = serde_json::to_string(&s.changes).unwrap();
        assert!(!all.contains("hidden") && !all.contains("4096") && !all.contains("8192"));
    }

    #[test]
    fn marks_vm_attributes_not_known_until_apply() {
        let json = br#"{"resource_changes":[
            {"address":"proxmox_virtual_environment_vm.guest","type":"proxmox_virtual_environment_vm","name":"guest",
             "change":{"actions":["create"],"before":null,
                       "after":{"vm_id":211,"cpu":[{"cores":4}],"disk":[{"interface":"scsi0"}],"network_device":[{}]},
                       "after_unknown":{"disk":[{"size":true}],"network_device":true}}}
        ]}"#;
        let s = summarize(json).unwrap();
        let wire = serde_json::to_value(&s.changes[0].attributes).unwrap();
        assert_eq!(
            wire,
            serde_json::json!([
                { "name": "cpu.cores", "after": 4 },
                { "name": "disk.scsi0.size", "unknown": true },
                { "name": "network_device.0.bridge", "unknown": true }
            ])
        );
    }

    #[test]
    fn reports_no_attributes_for_other_resources() {
        let json = br#"{"resource_changes":[
            {"address":"proxmox_virtual_environment_container.ct","type":"proxmox_virtual_environment_container","name":"ct",
             "change":{"actions":["update"],"before":{"tags":["a"],"memory":[{"dedicated":512}]},
                       "after":{"tags":["b"],"memory":[{"dedicated":1024}]}}}
        ]}"#;
        let s = summarize(json).unwrap();
        assert!(s.changes[0].attributes.is_empty());
        assert!(
            !serde_json::to_string(&s.changes)
                .unwrap()
                .contains("attributes")
        );
    }

    #[test]
    fn drops_a_vm_attribute_that_is_not_a_plain_value() {
        let json = br#"{"resource_changes":[
            {"address":"proxmox_virtual_environment_vm.guest","type":"proxmox_virtual_environment_vm","name":"guest",
             "change":{"actions":["update"],
                       "before":{"cpu":[{"cores":{"inner":"a"},"sockets":1}],"tags":[{"t":1}],
                                 "disk":[{"interface":"../x","size":1}]},
                       "after":{"cpu":[{"cores":{"inner":"b"},"sockets":2}],"tags":[{"t":2}],
                                "disk":[{"interface":"../x","size":2}]}}}
        ]}"#;
        let s = summarize(json).unwrap();
        let wire = serde_json::to_value(&s.changes[0].attributes).unwrap();
        assert_eq!(
            wire,
            serde_json::json!([{ "name": "cpu.sockets", "before": 1, "after": 2 }])
        );
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
