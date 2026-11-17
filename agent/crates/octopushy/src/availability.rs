//! Which executors this agent can offer, and why not the others; and how much room its
//! machine has. One implementation for three callers: `agent run` at startup, the re-probe
//! every [`REPROBE_INTERVAL`] while it runs, and `agent doctor`.
//!
//! A probe is a `--version` of a program, bounded by [`crate::tools::PROBE_TIMEOUT`], and a
//! `statvfs`. It never pulls an image, starts a container or takes a lock, so it runs next to
//! attempts without touching them.

use std::collections::{BTreeMap, HashSet};
use std::fs;
use std::os::unix::fs::MetadataExt;
use std::path::{Path, PathBuf};
use std::time::Duration;

use octopushy_proto::v1::{CacheSize, FreeSpace, StorageReport};

use crate::executors::EXECUTORS;
use crate::executors::nix;
use crate::tools::ToolRuntime;

/// How often a running agent probes its executors again. A tool installed or fixed is offered
/// within this, and probing costs three `--version` runs, so a few minutes is cheap and soon enough.
pub const REPROBE_INTERVAL: Duration = Duration::from_secs(300);
/// Every this many heartbeats carries a [`StorageReport`]: once a minute at the default 10 s.
/// Free space moves slowly, and the server keeps only the latest.
pub const STORAGE_EVERY_HEARTBEATS: u64 = 6;
/// The most directory entries one cache walk visits. A provider cache holds a few hundred
/// files; a walk this long takes well under a second on a local disk.
pub const CACHE_WALK_ENTRIES: u64 = 20_000;
/// The most entries `doctor` visits in the Nix store, which can hold millions.
pub const STORE_WALK_ENTRIES: u64 = 500_000;
/// The longest reason sent, in characters, as the protocol promises.
pub const MAX_REASON_CHARS: usize = 300;

/// One executor's probe.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct ExecutorProbe {
    pub name: &'static str,
    /// `Ok`: what the probe found, such as a version. `Err`: why the executor is not offered.
    /// Both cleaned with [`clean`].
    pub result: Result<String, String>,
    /// Whether this agent's settings mean to offer it. `nix` with a container runtime is not
    /// meant to be offered, so `doctor` does not count it a failure.
    pub expected: bool,
}

/// The result of probing every executor.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Availability {
    pub probes: Vec<ExecutorProbe>,
}

impl Availability {
    /// The executors offered, in [`EXECUTORS`] order.
    pub fn executors(&self) -> Vec<String> {
        self.probes
            .iter()
            .filter(|p| p.result.is_ok())
            .map(|p| p.name.to_owned())
            .collect()
    }

    /// Why each executor not offered is missing.
    pub fn unavailable(&self) -> BTreeMap<String, String> {
        self.probes
            .iter()
            .filter_map(|p| Some((p.name.to_owned(), p.result.clone().err()?)))
            .collect()
    }

    /// Whether an executor this agent means to offer is missing.
    pub fn missing_expected(&self) -> bool {
        self.probes.iter().any(|p| p.expected && p.result.is_err())
    }
}

/// Probes every executor. `proxmox` needs no tool.
pub async fn probe(runtime: &ToolRuntime, nix_store_dir: &Path, nix_min_free: u64) -> Availability {
    let tools = runtime.probe().await.map_err(|e| format!("{e:#}"));
    let ansible = match &tools {
        Ok(_) => runtime.probe_ansible().await.map_err(|e| format!("{e:#}")),
        Err(e) => Err(format!(
            "the tool runtime {} is not usable: {e}",
            runtime.kind.as_str()
        )),
    };
    let nix = {
        let version = runtime.probe_nix().await.map_err(|e| format!("{e:#}"));
        let dir = nix_store_dir.to_path_buf();
        let free = tokio::task::spawn_blocking(move || nix::free_bytes(&dir))
            .await
            .unwrap_or_else(|e| Err(std::io::Error::other(e.to_string())));
        nix::availability(!runtime.is_container(), version, free, nix_min_free)
    };
    let probes = EXECUTORS
        .iter()
        .map(|&name| {
            let (result, expected) = match name {
                "opentofu" => (tools.clone(), true),
                "ansible" => (ansible.clone(), true),
                "nix" => (nix.clone(), !runtime.is_container()),
                _ => (Ok("needs no tool".to_owned()), true),
            };
            ExecutorProbe {
                name,
                result: result.map(|s| clean(&s)).map_err(|s| clean(&s)),
                expected,
            }
        })
        .collect();
    Availability { probes }
}

/// `text` without control or formatting characters, on one line, at most
/// [`MAX_REASON_CHARS`] characters. A probe's error can carry what a tool printed.
pub fn clean(text: &str) -> String {
    let words: String = text
        .chars()
        .take(MAX_REASON_CHARS * 8)
        .map(|c| {
            if c.is_control() || is_format(c) {
                ' '
            } else {
                c
            }
        })
        .collect();
    let line = words.split_whitespace().collect::<Vec<_>>().join(" ");
    if line.chars().count() <= MAX_REASON_CHARS {
        return line;
    }
    let mut out: String = line.chars().take(MAX_REASON_CHARS - 1).collect();
    out.push('…');
    out
}

/// Bidirectional overrides, zero-width characters and the like, which can make a line read
/// other than it is.
fn is_format(c: char) -> bool {
    matches!(c, '\u{200B}'..='\u{200F}' | '\u{202A}'..='\u{202E}' | '\u{2060}'..='\u{2069}' | '\u{FEFF}' | '\u{00AD}')
}

/// The folders whose room is reported.
#[derive(Debug, Clone)]
pub struct StorageDirs {
    pub state: PathBuf,
    pub work: PathBuf,
    pub nix_store: PathBuf,
    pub providers: PathBuf,
    pub nix_cache: PathBuf,
}

/// Free space of the state, work and Nix store folders' file systems, each file system once.
/// A folder that does not exist, such as `/nix` without Nix, is left out.
pub fn free_space(dirs: &StorageDirs) -> Vec<FreeSpace> {
    let mut seen = HashSet::new();
    let mut out = Vec::new();
    for (name, dir) in [
        ("state", &dirs.state),
        ("work", &dirs.work),
        ("nix-store", &dirs.nix_store),
    ] {
        let Ok(meta) = fs::metadata(dir) else {
            continue;
        };
        if !seen.insert(meta.dev()) {
            continue;
        }
        if let Ok((free, total)) = statvfs(dir) {
            out.push(FreeSpace {
                name: name.to_owned(),
                free_bytes: free,
                total_bytes: total,
            });
        }
    }
    out
}

/// The size of the provider cache and Nix's cache, each from a walk of at most
/// [`CACHE_WALK_ENTRIES`]. A cache that does not exist is left out.
pub fn cache_sizes(dirs: &StorageDirs) -> Vec<CacheSize> {
    [
        ("providers", &dirs.providers),
        ("nix-cache", &dirs.nix_cache),
    ]
    .into_iter()
    .filter_map(|(name, dir)| {
        let (bytes, complete) = bounded_usage(dir, CACHE_WALK_ENTRIES).ok()?;
        Some(CacheSize {
            name: name.to_owned(),
            bytes,
            complete,
        })
    })
    .collect()
}

/// A report of free space now and the cache sizes measured last.
pub fn report(dirs: &StorageDirs, caches: &[CacheSize]) -> StorageReport {
    StorageReport {
        free: free_space(dirs),
        caches: caches.to_vec(),
    }
}

/// Bytes available to an unprivileged user and the size of the file system holding `path`.
pub fn statvfs(path: &Path) -> std::io::Result<(u64, u64)> {
    use std::os::unix::ffi::OsStrExt;
    let c = std::ffi::CString::new(path.as_os_str().as_bytes())?;
    // SAFETY: `c` is a valid C string and `stat` a valid out-pointer for the call.
    let mut stat: libc::statvfs = unsafe { std::mem::zeroed() };
    if unsafe { libc::statvfs(c.as_ptr(), &mut stat) } != 0 {
        return Err(std::io::Error::last_os_error());
    }
    #[allow(clippy::unnecessary_cast)]
    let (avail, blocks, frsize) = (
        stat.f_bavail as u64,
        stat.f_blocks as u64,
        stat.f_frsize as u64,
    );
    Ok((avail.saturating_mul(frsize), blocks.saturating_mul(frsize)))
}

/// Bytes the files under `dir` occupy, stopping after `max_entries` entries. Each hard-linked
/// file is counted once, and links are never followed. `complete` is false when the walk
/// stopped early: the bytes are then a lower bound.
pub fn bounded_usage(dir: &Path, max_entries: u64) -> std::io::Result<(u64, bool)> {
    let mut total = 0u64;
    let mut visited = 0u64;
    let mut seen = HashSet::new();
    let mut stack = vec![dir.to_path_buf()];
    fs::symlink_metadata(dir)?;
    while let Some(d) = stack.pop() {
        let Ok(entries) = fs::read_dir(&d) else {
            continue;
        };
        for entry in entries {
            visited += 1;
            if visited > max_entries {
                return Ok((total, false));
            }
            let Ok(entry) = entry else { continue };
            let Ok(meta) = entry.metadata() else { continue };
            if meta.nlink() > 1 && !meta.is_dir() && !seen.insert((meta.dev(), meta.ino())) {
                continue;
            }
            total = total.saturating_add(meta.blocks().saturating_mul(512));
            if meta.is_dir() {
                stack.push(entry.path());
            }
        }
    }
    Ok((total, true))
}

/// Prints a report of `availability` and the machine's room. Returns whether every executor
/// this agent means to offer is available.
pub fn print(runtime: &str, availability: &Availability, dirs: &StorageDirs) -> bool {
    println!("tool runtime: {runtime}");
    for p in &availability.probes {
        match &p.result {
            Ok(detail) if detail.is_empty() => println!("{:<10} available", p.name),
            Ok(detail) => println!("{:<10} available ({detail})", p.name),
            Err(reason) if p.expected => println!("{:<10} UNAVAILABLE: {reason}", p.name),
            Err(reason) => println!("{:<10} not offered: {reason}", p.name),
        }
    }
    for f in free_space(dirs) {
        println!(
            "free disk  {:<10} {} of {}",
            f.name,
            bytes(f.free_bytes),
            bytes(f.total_bytes)
        );
    }
    let mut sizes: Vec<(&str, &Path, u64)> = vec![
        ("providers", &dirs.providers, CACHE_WALK_ENTRIES),
        ("nix-cache", &dirs.nix_cache, CACHE_WALK_ENTRIES),
    ];
    if dirs.nix_store.join("store").is_dir() {
        sizes.push(("nix-store", &dirs.nix_store, STORE_WALK_ENTRIES));
    }
    for (name, dir, max) in sizes {
        match bounded_usage(dir, max) {
            Ok((n, true)) => println!("cache      {name:<10} {}", bytes(n)),
            Ok((n, false)) => println!(
                "cache      {name:<10} at least {} (stopped after {max} entries)",
                bytes(n)
            ),
            Err(_) => println!("cache      {name:<10} none at {}", dir.display()),
        }
    }
    !availability.missing_expected()
}

/// `n` bytes for people: GiB above one, else MiB.
pub fn bytes(n: u64) -> String {
    const MIB: f64 = (1u64 << 20) as f64;
    const GIB: f64 = (1u64 << 30) as f64;
    if n >= 1 << 30 {
        format!("{:.1} GiB", n as f64 / GIB)
    } else {
        format!("{:.1} MiB", n as f64 / MIB)
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::tools::RuntimeKind;
    use std::os::unix::fs::PermissionsExt;

    #[test]
    fn reasons_are_one_bounded_line_without_control_characters() {
        assert_eq!(clean("  a\tb\r\n\x1b[31mc\u{202E}d  "), "a b [31mc d");
        let long = "é".repeat(1000);
        let cleaned = clean(&long);
        assert_eq!(cleaned.chars().count(), MAX_REASON_CHARS);
        assert!(cleaned.ends_with('…'));
        assert_eq!(clean(&"x ".repeat(400)).chars().count(), MAX_REASON_CHARS);
        assert_eq!(clean(""), "");
        assert_eq!(clean("exactly"), "exactly");
    }

    fn script(dir: &Path, name: &str, body: &str) -> PathBuf {
        let path = dir.join(name);
        fs::write(&path, format!("#!/bin/sh\n{body}\n")).unwrap();
        fs::set_permissions(&path, fs::Permissions::from_mode(0o700)).unwrap();
        path
    }

    #[tokio::test]
    async fn a_missing_program_is_unavailable_with_its_reason() {
        let dir = tempfile::tempdir().unwrap();
        let tofu = script(dir.path(), "tofu", "echo 'OpenTofu v1.9.0'");
        let mut runtime = ToolRuntime::new(RuntimeKind::Local, None, tofu, None);
        runtime.ansible_binary = dir.path().join("missing-ansible");
        runtime.nix_binary = script(dir.path(), "nix", "echo bad >&2; exit 3");
        let a = probe(&runtime, dir.path(), 0).await;
        assert_eq!(a.executors(), ["opentofu", "proxmox"]);
        let unavailable = a.unavailable();
        assert!(
            unavailable["ansible"].starts_with("cannot run ")
                && unavailable["ansible"].contains("missing-ansible"),
            "{unavailable:?}"
        );
        assert!(
            unavailable["nix"].contains("exited with") && !unavailable["nix"].contains("bad"),
            "stderr is never kept: {unavailable:?}"
        );
        assert!(a.missing_expected());

        runtime.ansible_binary = script(dir.path(), "ansible", "echo 'ansible 2.17'");
        runtime.nix_binary = script(dir.path(), "nix2", "echo 'nix 2.24'");
        let a = probe(&runtime, dir.path(), 0).await;
        assert_eq!(a.executors(), EXECUTORS);
        assert!(a.unavailable().is_empty());
        assert!(!a.missing_expected());
    }

    #[tokio::test]
    async fn nix_under_a_container_runtime_is_not_a_failure() {
        let dir = tempfile::tempdir().unwrap();
        let podman = script(dir.path(), "podman", "echo 'podman version 5.0'");
        let runtime = ToolRuntime::new(RuntimeKind::Podman, Some(podman), "tofu".into(), None);
        let a = probe(&runtime, dir.path(), 0).await;
        assert_eq!(a.executors(), ["opentofu", "proxmox", "ansible"]);
        assert_eq!(
            a.unavailable()["nix"],
            "nix runs only with the local tool runtime"
        );
        assert!(!a.missing_expected());
    }

    #[test]
    fn storage_lists_each_file_system_once_and_bounds_walks() {
        let dir = tempfile::tempdir().unwrap();
        let providers = dir.path().join("providers");
        fs::create_dir_all(providers.join("a/b")).unwrap();
        for i in 0..10 {
            fs::write(providers.join(format!("a/b/{i}")), vec![1u8; 8192]).unwrap();
        }
        let dirs = StorageDirs {
            state: dir.path().into(),
            work: dir.path().join("providers"),
            nix_store: dir.path().join("no-nix"),
            providers: providers.clone(),
            nix_cache: dir.path().join("no-cache"),
        };
        let free = free_space(&dirs);
        assert_eq!(free.len(), 1, "work is on the state's file system");
        assert_eq!(free[0].name, "state");
        assert!(free[0].total_bytes >= free[0].free_bytes && free[0].total_bytes > 0);
        let caches = cache_sizes(&dirs);
        assert_eq!(caches.len(), 1);
        assert!(caches[0].complete && caches[0].bytes >= 10 * 8192);
        let (partial, complete) = bounded_usage(&providers, 3).unwrap();
        assert!(!complete && partial < caches[0].bytes);
    }
}
