//! The agent's state directory and its layout.
//!
//! ```text
//! <state>/
//!   agent.json        enrollment: agent ID, server URL, name
//!   key.pem           the agent's private key (0600)
//!   cert.pem          the agent's certificate, issued by the server's agent CA
//!   ca.pem            the agent CA, the only trust root for the server
//!   outbox/           results not yet acknowledged, one <attempt_id>.json each
//!   work/             attempt workspaces, one <attempt_id>/ each (or --work-dir)
//!   cache/providers/  OpenTofu provider cache shared by attempts
//! ```

use std::path::{Path, PathBuf};

/// Paths inside one state directory.
#[derive(Debug, Clone)]
pub struct StatePaths {
    pub root: PathBuf,
}

impl StatePaths {
    pub fn new(root: impl Into<PathBuf>) -> Self {
        Self { root: root.into() }
    }

    pub fn agent_json(&self) -> PathBuf {
        self.root.join("agent.json")
    }
    pub fn key(&self) -> PathBuf {
        self.root.join("key.pem")
    }
    pub fn cert(&self) -> PathBuf {
        self.root.join("cert.pem")
    }
    pub fn ca(&self) -> PathBuf {
        self.root.join("ca.pem")
    }
    /// New key during a certificate switch (see [`crate::identity`]).
    pub fn key_next(&self) -> PathBuf {
        self.root.join("key.pem.next")
    }
    /// New certificate during a certificate switch.
    pub fn cert_next(&self) -> PathBuf {
        self.root.join("cert.pem.next")
    }
    pub fn outbox(&self) -> PathBuf {
        self.root.join("outbox")
    }
    pub fn default_work_dir(&self) -> PathBuf {
        self.root.join("work")
    }
    pub fn provider_cache(&self) -> PathBuf {
        self.root.join("cache").join("providers")
    }
}

/// The default state directory: `$STATE_DIRECTORY` (set by systemd's
/// `StateDirectory=`), else `/var/lib/octopushy-agent` for root, else
/// `$XDG_STATE_HOME/octopushy-agent` or `~/.local/state/octopushy-agent`.
pub fn default_state_dir() -> PathBuf {
    resolve_state_dir(
        std::env::var_os("STATE_DIRECTORY").map(PathBuf::from),
        is_root(),
        std::env::var_os("XDG_STATE_HOME").map(PathBuf::from),
        std::env::var_os("HOME").map(PathBuf::from),
    )
}

fn resolve_state_dir(
    state_directory: Option<PathBuf>,
    root: bool,
    xdg_state_home: Option<PathBuf>,
    home: Option<PathBuf>,
) -> PathBuf {
    if let Some(dirs) = state_directory {
        // systemd passes a colon-separated list when several are configured.
        let first = dirs
            .to_string_lossy()
            .split(':')
            .next()
            .unwrap_or("")
            .to_owned();
        if !first.is_empty() {
            return PathBuf::from(first);
        }
    }
    if root {
        return PathBuf::from("/var/lib/octopushy-agent");
    }
    if let Some(xdg) = xdg_state_home.filter(|p| p.is_absolute()) {
        return xdg.join("octopushy-agent");
    }
    home.unwrap_or_else(|| PathBuf::from("."))
        .join(".local/state/octopushy-agent")
}

fn is_root() -> bool {
    // SAFETY: geteuid has no preconditions and cannot fail.
    unsafe { libc::geteuid() == 0 }
}

/// Whether `id` is safe to use as a file or directory name: 1-128 characters
/// from `[A-Za-z0-9._-]`, not starting with a dot.
pub fn is_safe_id(id: &str) -> bool {
    !id.is_empty()
        && id.len() <= 128
        && !id.starts_with('.')
        && id
            .bytes()
            .all(|b| b.is_ascii_alphanumeric() || b == b'-' || b == b'_' || b == b'.')
}

/// `path` relative to `base`, if it is inside it.
pub fn relative_to<'a>(path: &'a Path, base: &Path) -> Option<&'a Path> {
    path.strip_prefix(base).ok()
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn state_dir_precedence() {
        let p = |s: &str| Some(PathBuf::from(s));
        assert_eq!(
            resolve_state_dir(p("/var/lib/octopushy-agent:/other"), false, None, None),
            PathBuf::from("/var/lib/octopushy-agent")
        );
        assert_eq!(
            resolve_state_dir(None, true, p("/x"), p("/root")),
            PathBuf::from("/var/lib/octopushy-agent")
        );
        assert_eq!(
            resolve_state_dir(None, false, p("/home/a/.state"), p("/home/a")),
            PathBuf::from("/home/a/.state/octopushy-agent")
        );
        assert_eq!(
            resolve_state_dir(None, false, p("relative"), p("/home/a")),
            PathBuf::from("/home/a/.local/state/octopushy-agent")
        );
    }

    #[test]
    fn safe_ids() {
        assert!(is_safe_id("0b9c6a52-5f7e-4a4e-9a61-3c2f0d7f9e11"));
        assert!(is_safe_id("attempt_1.2"));
        for bad in ["", ".hidden", "../x", "a/b", "a b", "ä", &"x".repeat(129)] {
            assert!(!is_safe_id(bad), "{bad:?}");
        }
    }
}
