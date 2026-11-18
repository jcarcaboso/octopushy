//! The executor examples in `contracts/agent/v1/examples`. The agent writes the outputs there and the backend's tests read
//! them; see the README there. Set `OCTOPUSHY_WRITE_CONTRACT_EXAMPLES=1` to write what the executors produce instead of
//! comparing it.

#![allow(dead_code)]

use std::path::PathBuf;

use serde_json::Value;

pub fn examples() -> PathBuf {
    PathBuf::from(env!("CARGO_MANIFEST_DIR")).join("../../../contracts/agent/v1/examples")
}

/// A file of recorded tool output, in `tests/fixtures/contract`.
pub fn recorded(name: &str) -> Vec<u8> {
    let path = PathBuf::from(env!("CARGO_MANIFEST_DIR"))
        .join("tests/fixtures/contract")
        .join(name);
    std::fs::read(&path).unwrap_or_else(|e| panic!("reading {}: {e}", path.display()))
}

pub fn input(executor: &str, action: &str) -> Value {
    read(
        &examples()
            .join(executor)
            .join(format!("{action}.input.json")),
    )
}

fn read(path: &std::path::Path) -> Value {
    let text =
        std::fs::read_to_string(path).unwrap_or_else(|e| panic!("reading {}: {e}", path.display()));
    serde_json::from_str(&text).unwrap_or_else(|e| panic!("{}: {e}", path.display()))
}

/// Compares outputs an executor produced with the example, or writes them there. Keys must be the same at every level,
/// arrays as long, and each value of the same JSON kind. Values are equal too, except at `varying`: paths such as
/// `outPath`, with `*` for any array index or key, whose value depends on where the test ran.
pub fn check_output(
    executor: &str,
    action: &str,
    variant: Option<&str>,
    actual: &Value,
    varying: &[&str],
) {
    let name = match variant {
        Some(variant) => format!("{action}.output.{variant}.json"),
        None => format!("{action}.output.json"),
    };
    let path = examples().join(executor).join(&name);
    if std::env::var("OCTOPUSHY_WRITE_CONTRACT_EXAMPLES").as_deref() == Ok("1") {
        std::fs::create_dir_all(path.parent().unwrap()).unwrap();
        std::fs::write(&path, serde_json::to_string_pretty(actual).unwrap() + "\n").unwrap();
        return;
    }
    let mut differences = Vec::new();
    compare(&read(&path), actual, "", varying, &mut differences);
    assert!(
        differences.is_empty(),
        "{executor}/{name}: the outputs differ from the example:\n  {}",
        differences.join("\n  ")
    );
}

fn kind(value: &Value) -> &'static str {
    match value {
        Value::Null => "null",
        Value::Bool(_) => "boolean",
        Value::Number(_) => "number",
        Value::String(_) => "string",
        Value::Array(_) => "array",
        Value::Object(_) => "object",
    }
}

fn varies(path: &str, varying: &[&str]) -> bool {
    let parts: Vec<&str> = path.trim_start_matches('/').split('/').collect();
    varying.iter().any(|pattern| {
        let wanted: Vec<&str> = pattern.split('/').collect();
        wanted.len() == parts.len() && wanted.iter().zip(&parts).all(|(w, p)| *w == "*" || w == p)
    })
}

fn compare(expected: &Value, actual: &Value, path: &str, varying: &[&str], out: &mut Vec<String>) {
    let at = if path.is_empty() { "/" } else { path };
    if kind(expected) != kind(actual) {
        out.push(format!(
            "{at}: expected {}, got {}",
            kind(expected),
            kind(actual)
        ));
        return;
    }
    match (expected, actual) {
        (Value::Object(e), Value::Object(a)) => {
            for key in e.keys().filter(|k| !a.contains_key(*k)) {
                out.push(format!("{path}/{key}: missing"));
            }
            for key in a.keys().filter(|k| !e.contains_key(*k)) {
                out.push(format!("{path}/{key}: not in the example"));
            }
            for (key, value) in e {
                if let Some(other) = a.get(key) {
                    compare(value, other, &format!("{path}/{key}"), varying, out);
                }
            }
        }
        (Value::Array(e), Value::Array(a)) => {
            if e.len() != a.len() {
                out.push(format!("{at}: expected {} items, got {}", e.len(), a.len()));
            }
            for (i, (x, y)) in e.iter().zip(a).enumerate() {
                compare(x, y, &format!("{path}/{i}"), varying, out);
            }
        }
        _ => {
            if !varies(path, varying) && expected != actual {
                out.push(format!("{at}: expected {expected}, got {actual}"));
            }
        }
    }
}
