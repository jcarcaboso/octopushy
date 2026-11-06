//! JSON as the agent sends it: every number in plain decimal form, never with an exponent.
//!
//! The controller keeps outputs in PostgreSQL `jsonb`, which prints a number such as `1.2e-5` as `0.000012`. It checks what an
//! attempt returned for its secrets as it will be stored, so it withholds outputs with a number in any other form. serde_json
//! writes a float with an exponent when it is very small or very large, as a nearly idle host's `cpu` is, and so would any
//! float the agent passes through from Proxmox or OpenTofu. Every output is written here, so none is.

use std::io;

use serde::Serialize;
use serde_json::Value;
use serde_json::ser::{Formatter, Serializer};

/// `value` as compact JSON with every float written as a plain decimal.
pub fn to_string(value: &Value) -> String {
    let mut out = Vec::new();
    let mut serializer = Serializer::with_formatter(&mut out, PlainNumbers);
    value
        .serialize(&mut serializer)
        .expect("a JSON value serializes to memory");
    String::from_utf8(out).expect("serde_json writes UTF-8")
}

/// The decimal digits of `text`, as Rust prints a float without an exponent, kept a float: `3.0` stays `3.0`, and a negative
/// zero, which `jsonb` would print without its sign, is `0.0`.
fn plain(text: String) -> String {
    let text = if text == "-0" { "0".to_owned() } else { text };
    if text.contains('.') {
        text
    } else {
        text + ".0"
    }
}

/// Writes floats as plain decimals; everything else as serde_json writes it compactly, by the trait's defaults.
struct PlainNumbers;

impl Formatter for PlainNumbers {
    fn write_f64<W: ?Sized + io::Write>(&mut self, writer: &mut W, value: f64) -> io::Result<()> {
        // Display is the shortest text that reads back as the same value, and never uses an exponent. A JSON value holds no
        // NaN or infinity: serde_json writes those as null before it gets here.
        writer.write_all(plain(value.to_string()).as_bytes())
    }

    fn write_f32<W: ?Sized + io::Write>(&mut self, writer: &mut W, value: f32) -> io::Result<()> {
        writer.write_all(plain(value.to_string()).as_bytes())
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use serde_json::json;

    /// Whether a number token outside strings has an exponent: outside strings, an `e` follows a digit only in one.
    fn has_exponent(json: &str) -> bool {
        let (mut in_string, mut escaped, mut after_number) = (false, false, false);
        for c in json.chars() {
            if in_string {
                match (escaped, c) {
                    (true, _) => escaped = false,
                    (false, '\\') => escaped = true,
                    (false, '"') => in_string = false,
                    _ => {}
                }
                continue;
            }
            match c {
                '"' => {
                    in_string = true;
                    after_number = false;
                }
                'e' | 'E' if after_number => return true,
                '0'..='9' | '.' => after_number = true,
                _ => after_number = false,
            }
        }
        false
    }

    #[test]
    fn floats_are_written_without_an_exponent_and_read_back_as_the_same_values() {
        let value = json!({
            "cpu": 4.2e-7,
            "tiny": 5e-324,
            "big": 1e300,
            "ratio": 0.0123,
            "whole": 3.0,
            "negativeZero": -0.0,
            "negative": -2.5e-7,
            "int": 16,
            "nested": [{ "load": 4.2e-9 }, 7.5e21],
            "text": "1e-5 stays as written in a string",
        });
        assert!(has_exponent(&serde_json::to_string(&value).unwrap()));
        let text = to_string(&value);
        assert!(!has_exponent(&text), "{text}");
        assert!(text.contains(r#""cpu":0.00000042"#), "{text}");
        assert!(text.contains(r#""whole":3.0"#), "{text}");
        assert!(text.contains(r#""negativeZero":0.0"#), "{text}");
        assert!(text.contains(r#""int":16"#), "{text}");
        assert!(
            text.contains(r#""text":"1e-5 stays as written in a string""#),
            "{text}"
        );
        // The same values, read back, a negative zero as zero.
        let mut expected = value.clone();
        expected["negativeZero"] = json!(0.0);
        assert_eq!(serde_json::from_str::<Value>(&text).unwrap(), expected);
    }

    #[test]
    fn what_serde_json_writes_with_an_exponent_is_written_plain() {
        // A nearly idle host's cpu, as serde_json itself writes it.
        assert_eq!(serde_json::to_string(&json!(4.2e-7)).unwrap(), "4.2e-7");
        assert_eq!(to_string(&json!(4.2e-7)), "0.00000042");
        assert_eq!(to_string(&json!(1e21)), "1000000000000000000000.0");
    }
}
