//! Reading `tofu show -json`, `tofu output -json`, and `tofu version -json`.
//!
//! Only the parts the contract needs are kept; the JSON plan itself can hold
//! configuration values and is never logged or returned.

use anyhow::{Context, Result};
use serde::Serialize;
use serde_json::{Map, Value};

/// One entry of `changes`.
#[derive(Debug, Clone, PartialEq, Eq, Serialize)]
pub struct Change {
    pub address: String,
    #[serde(rename = "type")]
    pub kind: String,
    pub name: String,
    pub actions: Vec<String>,
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
        summary.changes.push(Change {
            address: text("address"),
            kind: text("type"),
            name: text("name"),
            actions,
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
