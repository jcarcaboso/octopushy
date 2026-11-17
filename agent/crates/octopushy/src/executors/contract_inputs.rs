//! The inputs in `contracts/agent/v1/examples`, which the backend's tests build, read into the types each executor reads
//! its input with.
//!
//! Unknown input fields are ignored, as the contract says, so a field the controller renamed would still parse, with the
//! old one defaulted. So every value of each example is changed in turn, and the change has to show in what the
//! executor parsed: a value it does not read fails the test, unless the action is listed as not using it.

use std::fmt::Debug;
use std::path::PathBuf;

use serde::de::DeserializeOwned;
use serde_json::{Value, json};

fn example(executor: &str, action: &str) -> Value {
    let path = PathBuf::from(env!("CARGO_MANIFEST_DIR"))
        .join("../../../contracts/agent/v1/examples")
        .join(executor)
        .join(format!("{action}.input.json"));
    let text = std::fs::read_to_string(&path)
        .unwrap_or_else(|e| panic!("reading {}: {e}", path.display()));
    serde_json::from_str(&text).unwrap_or_else(|e| panic!("{}: {e}", path.display()))
}

fn parse<T: DeserializeOwned + Debug>(value: &Value) -> Result<String, String> {
    serde_json::from_value::<T>(value.clone())
        .map(|parsed| format!("{parsed:?}"))
        .map_err(|e| e.to_string())
}

/// The JSON pointer of every value with nothing inside it.
fn leaves(value: &Value, path: String, out: &mut Vec<String>) {
    match value {
        Value::Object(map) if !map.is_empty() => {
            for (key, inner) in map {
                leaves(
                    inner,
                    format!("{path}/{}", key.replace('~', "~0").replace('/', "~1")),
                    out,
                );
            }
        }
        Value::Array(items) if !items.is_empty() => {
            for (i, inner) in items.iter().enumerate() {
                leaves(inner, format!("{path}/{i}"), out);
            }
        }
        _ => out.push(path),
    }
}

/// Another value of the same kind, or for null one of another kind.
fn changed(value: &Value) -> Value {
    match value {
        Value::Null => json!("probe"),
        Value::Bool(b) => json!(!b),
        Value::Number(n) => json!(n.as_u64().map_or(1, |n| n + 1)),
        Value::String(s) => json!(format!("{s}-probe")),
        Value::Array(_) => json!(["probe"]),
        Value::Object(_) => json!({ "probe": "probe" }),
    }
}

/// Checks that `T` reads every value of the example of `executor`/`action`, except under the top-level keys in `unused`,
/// which the controller sends to every action of the executor and this one does not read.
fn check<T: DeserializeOwned + Debug>(executor: &str, action: &str, unused: &[&str]) {
    let input = example(executor, action);
    let baseline = parse::<T>(&input)
        .unwrap_or_else(|e| panic!("{executor}/{action}: the example does not parse: {e}"));
    let mut paths = Vec::new();
    leaves(&input, String::new(), &mut paths);
    let mut unread = Vec::new();
    for path in &paths {
        let top = path.split('/').nth(1).unwrap_or("");
        let mut probe = input.clone();
        *probe.pointer_mut(path).unwrap() = changed(input.pointer(path).unwrap());
        // A value of another kind that fails to parse is read too.
        let read = parse::<T>(&probe).map_or(true, |parsed| parsed != baseline);
        match (read, unused.contains(&top)) {
            (false, false) => unread.push(path.clone()),
            (true, true) => {
                panic!("{executor}/{action} reads {path}; take {top} off its unused list")
            }
            _ => {}
        }
    }
    for key in unused {
        assert!(
            input.get(key).is_some(),
            "{executor}/{action}: {key} is listed as unused but the example has no such field"
        );
    }
    assert!(
        unread.is_empty(),
        "{executor}/{action}: the executor does not read {unread:?} of its example; the field may have been renamed on one side"
    );
}

/// What the controller sends to every `proxmox` action.
const PROXMOX_FIELDS: [&str; 8] = [
    "node",
    "vmid",
    "kind",
    "name",
    "expectRunning",
    "expectIpv4",
    "waitSeconds",
    "protection",
];

fn proxmox_unused(read: &[&str]) -> Vec<&'static str> {
    PROXMOX_FIELDS
        .into_iter()
        .filter(|f| !read.contains(f))
        .collect()
}

#[test]
fn opentofu_inputs() {
    check::<super::opentofu::Input>("opentofu", "preview", &[]);
    check::<super::opentofu::Input>("opentofu", "apply", &[]);
}

#[test]
fn ansible_inputs() {
    check::<super::ansible::Input>("ansible", "preview", &[]);
    check::<super::ansible::Input>("ansible", "run", &[]);
}

#[test]
fn proxmox_inputs() {
    use super::proxmox::*;
    check::<ObserveInput>("proxmox", "observe", &proxmox_unused(&[]));
    check::<VerifyGuestInput>(
        "proxmox",
        "verify-guest",
        &proxmox_unused(&[
            "node",
            "vmid",
            "kind",
            "name",
            "expectRunning",
            "expectIpv4",
            "waitSeconds",
        ]),
    );
    check::<VerifyAbsentInput>(
        "proxmox",
        "verify-absent",
        &proxmox_unused(&["vmid", "waitSeconds"]),
    );
    check::<SetProtectionInput>(
        "proxmox",
        "set-protection",
        &proxmox_unused(&["node", "vmid", "kind", "protection"]),
    );
}

#[test]
fn nix_inputs() {
    check::<super::nix::Input>("nix", "build", &[]);
}

#[test]
fn a_renamed_field_is_caught() {
    // What the check is for: a field the executor reads under another name parses, defaulted, and is caught.
    #[derive(Debug, serde::Deserialize)]
    #[allow(dead_code)]
    struct Renamed {
        #[serde(default)]
        timeout: Option<u64>,
        root: String,
    }
    let result = std::panic::catch_unwind(|| check::<Renamed>("opentofu", "preview", &[]));
    assert!(result.is_err());
}
