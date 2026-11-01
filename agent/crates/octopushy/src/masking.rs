//! Replaces secret values with `***` in log lines and error messages.

use std::borrow::Cow;

/// The placeholder for a masked secret.
pub const MASK: &str = "***";

/// Values shorter than this are not masked as fragments (only whole values are).
const MIN_FRAGMENT_LEN: usize = 6;

/// Masks a fixed set of secret values.
#[derive(Clone, Default)]
pub struct Masker {
    /// Longest first, so a secret containing another is masked whole.
    patterns: Vec<String>,
}

impl std::fmt::Debug for Masker {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        write!(f, "Masker({} patterns)", self.patterns.len())
    }
}

impl Masker {
    /// Builds a masker for `secrets`. Empty values are skipped. Besides each
    /// whole value it masks fragments that tools are known to print on their own:
    /// each line of a multi-line value, and the part after the first `=` (the
    /// secret of a Proxmox token `USER@REALM!TOKENID=SECRET`).
    pub fn new<'a>(secrets: impl IntoIterator<Item = &'a str>) -> Self {
        let mut patterns: Vec<String> = Vec::new();
        let mut add = |p: &str| {
            if !p.is_empty() && p != MASK && !patterns.iter().any(|q| q == p) {
                patterns.push(p.to_owned());
            }
        };
        for secret in secrets {
            if secret.is_empty() {
                continue;
            }
            add(secret);
            if secret.contains('\n') {
                for line in secret.lines().map(|l| l.trim_end_matches('\r')) {
                    if line.trim().len() >= MIN_FRAGMENT_LEN {
                        add(line);
                    }
                }
            }
            if let Some((_, rest)) = secret.split_once('=')
                && rest.len() >= MIN_FRAGMENT_LEN
                && !rest.contains('\n')
            {
                add(rest);
            }
        }
        patterns.sort_by(|a, b| b.len().cmp(&a.len()).then_with(|| a.cmp(b)));
        Self { patterns }
    }

    /// Masks every string in a JSON value, keys included. Serialized text will not do: a value
    /// holding a quote or a backslash is escaped there, and no longer matches.
    pub fn mask_json(&self, value: serde_json::Value) -> serde_json::Value {
        use serde_json::Value;
        match value {
            Value::String(text) => Value::String(self.mask(&text).into_owned()),
            Value::Array(items) => {
                Value::Array(items.into_iter().map(|v| self.mask_json(v)).collect())
            }
            Value::Object(fields) => {
                let mut masked = serde_json::Map::new();
                for (key, value) in fields {
                    let base = self.mask(&key).into_owned();
                    // Two secrets as keys become one text; each keeps its value.
                    let mut unique = base.clone();
                    let mut count = 1;
                    while masked.contains_key(&unique) {
                        count += 1;
                        unique = format!("{base}#{count}");
                    }
                    masked.insert(unique, self.mask_json(value));
                }
                Value::Object(masked)
            }
            other => other,
        }
    }

    /// Whether there is nothing to mask.
    pub fn is_empty(&self) -> bool {
        self.patterns.is_empty()
    }

    /// Returns `text` with every secret replaced by `***`.
    pub fn mask<'t>(&self, text: &'t str) -> Cow<'t, str> {
        let mut out: Cow<'t, str> = Cow::Borrowed(text);
        for pattern in &self.patterns {
            if out.contains(pattern.as_str()) {
                out = Cow::Owned(out.replace(pattern.as_str(), MASK));
            }
        }
        out
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn json_is_masked_at_any_depth_even_where_text_would_be_escaped() {
        let m = Masker::new([r#"pass"word\here"#]);
        let value = serde_json::json!({
            "ok": false,
            "members": [{ "checks": [{ "detail": r#"refused pass"word\here for you"#, "n": 3 }] }],
            r#"pass"word\here"#: true,
        });
        let masked = m.mask_json(value);
        assert_eq!(
            masked["members"][0]["checks"][0]["detail"],
            "refused *** for you"
        );
        assert_eq!(masked["members"][0]["checks"][0]["n"], 3);
        assert_eq!(masked["***"], true);
        assert!(!masked.to_string().contains("word"));
    }

    #[test]
    fn two_keys_masked_to_the_same_text_keep_both_their_values() {
        let m = Masker::new(["first-secret", "second-secret"]);
        let masked = m.mask_json(serde_json::json!({ "first-secret": 1, "second-secret": 2 }));
        let fields = masked.as_object().unwrap();
        assert_eq!(fields.len(), 2, "{masked}");
        let mut values: Vec<i64> = fields.values().map(|v| v.as_i64().unwrap()).collect();
        values.sort_unstable();
        assert_eq!(values, [1, 2]);
        assert!(!masked.to_string().contains("secret"));
    }

    #[test]
    fn masks_every_occurrence_longest_first() {
        let m = Masker::new(["abc", "abcdef", ""]);
        assert_eq!(m.mask("x abcdef y abc z"), "x *** y *** z");
        assert_eq!(m.mask("nothing here"), "nothing here");
        assert!(matches!(m.mask("nothing here"), Cow::Borrowed(_)));
    }

    #[test]
    fn skips_empty_values() {
        let m = Masker::new(["", ""]);
        assert!(m.is_empty());
        assert_eq!(m.mask("text"), "text");
    }

    #[test]
    fn masks_the_secret_part_of_a_proxmox_token() {
        let token = "octopushy@pve!agent=11111111-2222-3333-4444-555555555555";
        let m = Masker::new([token]);
        assert_eq!(
            m.mask(&format!("Authorization: PVEAPIToken={token}")),
            "Authorization: PVEAPIToken=***"
        );
        assert_eq!(
            m.mask("secret 11111111-2222-3333-4444-555555555555 leaked"),
            "secret *** leaked"
        );
        // The token ID alone is not secret.
        assert_eq!(m.mask("octopushy@pve!agent"), "octopushy@pve!agent");
    }

    #[test]
    fn masks_lines_of_multi_line_secrets() {
        let pem =
            "-----BEGIN KEY-----\nMIIBVgIBADANBgkqhkiG9w0BAQEFAASCAUAwggE8\n-----END KEY-----";
        let m = Masker::new([pem]);
        assert_eq!(
            m.mask("line MIIBVgIBADANBgkqhkiG9w0BAQEFAASCAUAwggE8"),
            "line ***"
        );
    }

    #[test]
    fn overlapping_secrets() {
        let m = Masker::new(["password123", "word"]);
        assert_eq!(m.mask("password123 and word"), "*** and ***");
    }
}
