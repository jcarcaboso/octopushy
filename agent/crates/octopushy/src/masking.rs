//! Replaces secret values with `***` in log lines and error messages.

use std::borrow::Cow;

/// The placeholder for a masked secret.
pub const MASK: &str = "***";

/// Values shorter than this are not masked as fragments (only whole values are).
const MIN_FRAGMENT_LEN: usize = 6;
/// A line of a multi-line value. Output is masked a line at a time, so a value printed over
/// several lines is only ever seen as these; what is shorter would match ordinary text.
const MIN_LINE_LEN: usize = 4;

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
    /// secret of a Proxmox token `USER@REALM!TOKENID=SECRET`). Each is also masked
    /// as JSON escapes it, which is how Ansible prints what a task returned, and without
    /// the space around it.
    pub fn new<'a>(secrets: impl IntoIterator<Item = &'a str>) -> Self {
        let mut patterns: Vec<String> = Vec::new();
        let mut add = |p: &str| {
            let escaped = serde_json::to_string(p).unwrap_or_default();
            let escaped = escaped.trim_matches('"');
            for p in [p, escaped] {
                if !p.is_empty() && p != MASK && !patterns.iter().any(|q| q == p) {
                    patterns.push(p.to_owned());
                }
            }
        };
        for secret in secrets {
            if secret.is_empty() {
                continue;
            }
            add(secret);
            // Tools print a value without the space around it: Ansible strips a task's name.
            if secret.trim() != secret && secret.trim().len() >= MIN_LINE_LEN {
                add(secret.trim());
            }
            if secret.contains('\n') {
                for line in secret.lines().map(|l| l.trim_end_matches('\r')) {
                    if line.trim().len() >= MIN_LINE_LEN {
                        add(line);
                        add(line.trim());
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
    fn masks_a_value_as_json_escapes_it() {
        let m = Masker::new([r#"pa"ss\word"#]);
        assert_eq!(m.mask(r#"{"msg": "pa\"ss\\word"}"#), r#"{"msg": "***"}"#);
    }

    #[test]
    fn masks_a_whole_private_key_printed_on_one_json_line() {
        let key = "-----BEGIN OPENSSH PRIVATE KEY-----\nb3BlbnNzaC1rZXktdjEAAAAA\nab=\n-----END OPENSSH PRIVATE KEY-----\n";
        let m = Masker::new([key]);
        let printed = serde_json::to_string(key).unwrap();
        assert_eq!(m.mask(&printed), "\"***\"");
        // Line by line, a fragment under four characters stays: this is the documented limit.
        let text = m.mask("-----BEGIN OPENSSH PRIVATE KEY-----\nb3BlbnNzaC1rZXktdjEAAAAA\nab=");
        assert_eq!(text, "***\n***\nab=");
    }

    #[test]
    fn a_short_multi_line_value_is_masked_line_by_line() {
        let m = Masker::new(["ab12\ncd34"]);
        assert_eq!(m.mask("ab12"), "***");
        assert_eq!(m.mask("cd34"), "***");
    }

    #[test]
    fn masks_a_value_without_the_space_around_it() {
        let m = Masker::new(["  TOPSECRET  "]);
        assert_eq!(m.mask("TASK [TOPSECRET] ****"), "TASK [***] ****");
        assert_eq!(m.mask("a   TOPSECRET   b"), "a *** b");
        // What is left after trimming follows the same limit as a line of a value.
        let m = Masker::new([" ab "]);
        assert_eq!(m.mask("ab"), "ab");
        let m = Masker::new(["first line\n  second line  "]);
        assert_eq!(m.mask("[second line]"), "[***]");
    }

    #[test]
    fn overlapping_secrets() {
        let m = Masker::new(["password123", "word"]);
        assert_eq!(m.mask("password123 and word"), "*** and ***");
    }
}
