//! Lenient deserializers for Proxmox responses.
//!
//! The Proxmox API is loosely typed: numbers arrive as JSON numbers or strings,
//! booleans as `0`/`1`, `true`/`false`, or strings, and optional fields are simply
//! missing. These helpers accept every form seen in practice and treat empty
//! strings as absent.

use serde::{Deserialize, Deserializer, de::Error as _};
use serde_json::Value;

fn number_from(value: &Value) -> Option<f64> {
    match value {
        Value::Number(n) => n.as_f64(),
        Value::String(s) => {
            let s = s.trim();
            if s.is_empty() {
                None
            } else {
                s.parse::<f64>().ok()
            }
        }
        Value::Bool(b) => Some(if *b { 1.0 } else { 0.0 }),
        _ => None,
    }
}

fn bool_from(value: &Value) -> Option<bool> {
    match value {
        Value::Bool(b) => Some(*b),
        Value::Number(n) => n.as_f64().map(|f| f != 0.0),
        Value::String(s) => match s.trim().to_ascii_lowercase().as_str() {
            "1" | "true" | "yes" | "on" => Some(true),
            "0" | "false" | "no" | "off" => Some(false),
            _ => None,
        },
        _ => None,
    }
}

/// `Option<f64>` from a number or numeric string.
pub fn opt_f64<'de, D: Deserializer<'de>>(d: D) -> Result<Option<f64>, D::Error> {
    let value = Option::<Value>::deserialize(d)?;
    match value {
        None | Some(Value::Null) => Ok(None),
        Some(Value::String(s)) if s.trim().is_empty() => Ok(None),
        Some(v) => number_from(&v)
            .map(Some)
            .ok_or_else(|| D::Error::custom(format!("expected a number, got {v}"))),
    }
}

/// `Option<u64>` from a number or numeric string; fractions are rounded.
pub fn opt_u64<'de, D: Deserializer<'de>>(d: D) -> Result<Option<u64>, D::Error> {
    let value = opt_f64(d)?;
    match value {
        None => Ok(None),
        Some(f) if f.is_finite() && f >= 0.0 => Ok(Some(f.round() as u64)),
        Some(f) => Err(D::Error::custom(format!(
            "expected a non-negative number, got {f}"
        ))),
    }
}

/// A required VMID from a number or numeric string.
pub fn vmid<'de, D: Deserializer<'de>>(d: D) -> Result<u32, D::Error> {
    let value = Value::deserialize(d)?;
    let n = number_from(&value)
        .ok_or_else(|| D::Error::custom(format!("expected a VMID, got {value}")))?;
    if n.fract() != 0.0 || !(0.0..=f64::from(u32::MAX)).contains(&n) {
        return Err(D::Error::custom(format!("invalid VMID {value}")));
    }
    Ok(n as u32)
}

/// `Option<bool>` from a boolean, `0`/`1`, or a string such as `"1"` or `"true"`.
pub fn opt_bool<'de, D: Deserializer<'de>>(d: D) -> Result<Option<bool>, D::Error> {
    let value = Option::<Value>::deserialize(d)?;
    match value {
        None | Some(Value::Null) => Ok(None),
        Some(Value::String(s)) if s.trim().is_empty() => Ok(None),
        Some(v) => bool_from(&v)
            .map(Some)
            .ok_or_else(|| D::Error::custom(format!("expected a boolean, got {v}"))),
    }
}

/// `bool` that defaults to `false` when missing or null.
pub fn bool_or_false<'de, D: Deserializer<'de>>(d: D) -> Result<bool, D::Error> {
    Ok(opt_bool(d)?.unwrap_or(false))
}

/// `Option<String>` that also accepts numbers (Proxmox sometimes sends names or
/// versions as numbers) and treats empty strings as absent.
pub fn opt_string<'de, D: Deserializer<'de>>(d: D) -> Result<Option<String>, D::Error> {
    let value = Option::<Value>::deserialize(d)?;
    match value {
        None | Some(Value::Null) => Ok(None),
        Some(Value::String(s)) if s.is_empty() => Ok(None),
        Some(Value::String(s)) => Ok(Some(s)),
        Some(Value::Number(n)) => Ok(Some(n.to_string())),
        Some(Value::Bool(b)) => Ok(Some(b.to_string())),
        Some(v) => Err(D::Error::custom(format!("expected a string, got {v}"))),
    }
}

/// `String` that defaults to empty when missing or null.
pub fn string_or_empty<'de, D: Deserializer<'de>>(d: D) -> Result<String, D::Error> {
    Ok(opt_string(d)?.unwrap_or_default())
}

/// Splits a Proxmox tag list. The API returns tags separated by `;`; the VM
/// configuration also accepts `,` and spaces.
pub fn split_tags(raw: &str) -> Vec<String> {
    raw.split([';', ',', ' '])
        .map(str::trim)
        .filter(|t| !t.is_empty())
        .map(str::to_owned)
        .collect()
}

/// Tags from a separated string or, leniently, a JSON array.
pub fn tags<'de, D: Deserializer<'de>>(d: D) -> Result<Vec<String>, D::Error> {
    let value = Option::<Value>::deserialize(d)?;
    match value {
        None | Some(Value::Null) => Ok(Vec::new()),
        Some(Value::String(s)) => Ok(split_tags(&s)),
        Some(Value::Array(items)) => Ok(items
            .into_iter()
            .filter_map(|v| match v {
                Value::String(s) if !s.is_empty() => Some(s),
                _ => None,
            })
            .collect()),
        Some(v) => Err(D::Error::custom(format!("expected tags, got {v}"))),
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use serde::Deserialize;

    #[derive(Deserialize)]
    struct Sample {
        #[serde(default, deserialize_with = "opt_u64")]
        n: Option<u64>,
        #[serde(default, deserialize_with = "opt_f64")]
        f: Option<f64>,
        #[serde(default, deserialize_with = "bool_or_false")]
        b: bool,
        #[serde(deserialize_with = "vmid")]
        id: u32,
        #[serde(default, deserialize_with = "tags")]
        t: Vec<String>,
    }

    fn parse(json: &str) -> Sample {
        serde_json::from_str(json).unwrap()
    }

    #[test]
    fn accepts_numbers_strings_and_flags() {
        let s = parse(r#"{"n":"42","f":"0.5","b":1,"id":"210","t":"a;b; c"}"#);
        assert_eq!(s.n, Some(42));
        assert_eq!(s.f, Some(0.5));
        assert!(s.b);
        assert_eq!(s.id, 210);
        assert_eq!(s.t, vec!["a", "b", "c"]);

        let s = parse(r#"{"n":42.4,"f":3,"b":"0","id":7}"#);
        assert_eq!(s.n, Some(42));
        assert_eq!(s.f, Some(3.0));
        assert!(!s.b);
        assert!(s.t.is_empty());

        let s = parse(r#"{"n":"","f":null,"b":"true","id":1,"t":["x"]}"#);
        assert_eq!(s.n, None);
        assert_eq!(s.f, None);
        assert!(s.b);
        assert_eq!(s.t, vec!["x"]);
    }

    #[test]
    fn rejects_garbage() {
        assert!(serde_json::from_str::<Sample>(r#"{"n":"many","id":1}"#).is_err());
        assert!(serde_json::from_str::<Sample>(r#"{"id":"1.5"}"#).is_err());
        assert!(serde_json::from_str::<Sample>(r#"{"id":1,"b":"maybe"}"#).is_err());
    }
}
