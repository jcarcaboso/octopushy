//! Safe extraction of the repository snapshot and plan artifacts, and packing
//! of `plan.tar.gz`.
//!
//! Extraction refuses anything that could write or point outside the target:
//! absolute paths, `..`, hard links, device files and FIFOs, symlinks that
//! leave the target (checked lexically while extracting and again by resolving
//! them at the end), and writing through an existing symlink. Sizes and entry
//! counts are capped.

use std::fs::{self, OpenOptions};
use std::io::{self, Read};
use std::os::unix::fs::OpenOptionsExt;
use std::path::{Component, Path, PathBuf};

use anyhow::{Context, Result, bail};
use flate2::Compression;
use flate2::read::GzDecoder;
use flate2::write::GzEncoder;
use tar::EntryType;

/// Extraction limits.
#[derive(Debug, Clone, Copy)]
pub struct Limits {
    /// Total size of all files.
    pub max_bytes: u64,
    pub max_entries: usize,
}

impl Default for Limits {
    fn default() -> Self {
        Self {
            max_bytes: 256 * 1024 * 1024,
            max_entries: 100_000,
        }
    }
}

/// Counts extracted files and bytes.
#[derive(Debug, Default, Clone, Copy, PartialEq, Eq)]
pub struct Stats {
    pub files: usize,
    pub bytes: u64,
}

/// Stops reading once more than `left` bytes came through (a gzip bomb guard
/// covering tar headers and padding as well as file contents).
struct Capped<R> {
    inner: R,
    left: u64,
}

impl<R: Read> Read for Capped<R> {
    fn read(&mut self, buf: &mut [u8]) -> io::Result<usize> {
        if self.left == 0 {
            return Err(io::Error::other("the archive is larger than allowed"));
        }
        let max = buf.len().min(self.left.min(usize::MAX as u64) as usize);
        let n = self.inner.read(&mut buf[..max])?;
        self.left -= n as u64;
        Ok(n)
    }
}

/// A relative path without `.` and with no way out (`..`, `/`, prefixes).
fn clean_relative(path: &Path) -> Result<PathBuf> {
    let mut out = PathBuf::new();
    for c in path.components() {
        match c {
            Component::Normal(p) => out.push(p),
            Component::CurDir => {}
            Component::ParentDir => bail!("entry {} contains '..'", path.display()),
            Component::RootDir | Component::Prefix(_) => {
                bail!("entry {} is an absolute path", path.display())
            }
        }
    }
    Ok(out)
}

/// Creates the parents of `rel` under `root`, refusing to pass through anything
/// that is not a real directory (such as a symlink extracted earlier).
fn prepare_parents(root: &Path, rel: &Path) -> Result<()> {
    let mut current = root.to_path_buf();
    if let Some(parent) = rel.parent() {
        for c in parent.components() {
            current.push(c);
            match fs::symlink_metadata(&current) {
                Ok(m) if m.is_dir() => {}
                Ok(_) => bail!(
                    "entry {} would be written through {}, which is not a directory",
                    rel.display(),
                    current.display()
                ),
                Err(e) if e.kind() == io::ErrorKind::NotFound => fs::create_dir(&current)
                    .with_context(|| format!("creating {}", current.display()))?,
                Err(e) => return Err(e.into()),
            }
        }
    }
    Ok(())
}

/// Whether symlink `target`, placed at `rel`, stays inside the root when read
/// lexically.
fn symlink_stays_inside(rel: &Path, target: &Path) -> bool {
    if target.is_absolute() {
        return false;
    }
    let mut depth: Vec<&std::ffi::OsStr> = rel
        .parent()
        .map(|p| {
            p.components()
                .filter_map(|c| match c {
                    Component::Normal(n) => Some(n),
                    _ => None,
                })
                .collect()
        })
        .unwrap_or_default();
    for c in target.components() {
        match c {
            Component::Normal(n) => depth.push(n),
            Component::CurDir => {}
            Component::ParentDir => {
                if depth.pop().is_none() {
                    return false;
                }
            }
            Component::RootDir | Component::Prefix(_) => return false,
        }
    }
    true
}

/// Extracts a gzip tar into `dest` (created if missing).
pub fn extract_tar_gz(data: &[u8], dest: &Path, limits: Limits) -> Result<Stats> {
    fs::create_dir_all(dest).with_context(|| format!("creating {}", dest.display()))?;
    let root = dest.canonicalize()?;
    // Headers and padding add at most a few KiB per entry.
    let cap = limits
        .max_bytes
        .saturating_add(limits.max_entries as u64 * 4096)
        .saturating_add(1024 * 1024);
    let mut archive = tar::Archive::new(Capped {
        inner: GzDecoder::new(data),
        left: cap,
    });
    let mut stats = Stats::default();
    let mut entries = 0usize;
    let mut symlinks = Vec::new();

    for entry in archive.entries().context("reading the archive")? {
        let mut entry = entry.context("reading the archive")?;
        entries += 1;
        if entries > limits.max_entries {
            bail!("the archive has more than {} entries", limits.max_entries);
        }
        let kind = entry.header().entry_type();
        // Global pax headers (written by `git archive`) carry no file.
        if matches!(kind, EntryType::XGlobalHeader | EntryType::XHeader) {
            continue;
        }
        let raw = entry.path().context("reading an entry name")?.into_owned();
        let rel = clean_relative(&raw)?;
        if rel.as_os_str().is_empty() {
            continue; // "./"
        }
        prepare_parents(&root, &rel)?;
        let target = root.join(&rel);
        let existing = fs::symlink_metadata(&target);

        match kind {
            EntryType::Directory => match existing {
                Ok(m) if m.is_dir() => {}
                Ok(_) => bail!("entry {} appears twice", rel.display()),
                Err(_) => fs::create_dir(&target)
                    .with_context(|| format!("creating {}", target.display()))?,
            },
            EntryType::Regular | EntryType::Continuous => {
                if existing.is_ok() {
                    bail!("entry {} appears twice", rel.display());
                }
                let size = entry.header().size()?;
                if stats.bytes + size > limits.max_bytes {
                    bail!("the archive holds more than {} bytes", limits.max_bytes);
                }
                let executable = entry.header().mode().unwrap_or(0o644) & 0o111 != 0;
                // create_new (O_EXCL) never follows a symlink at the target.
                let mut file = OpenOptions::new()
                    .write(true)
                    .create_new(true)
                    .mode(if executable { 0o755 } else { 0o644 })
                    .open(&target)
                    .with_context(|| format!("creating {}", target.display()))?;
                let written = io::copy(&mut (&mut entry).take(size), &mut file)?;
                stats.bytes += written;
                stats.files += 1;
            }
            EntryType::Symlink => {
                if existing.is_ok() {
                    bail!("entry {} appears twice", rel.display());
                }
                let link = entry
                    .link_name()?
                    .with_context(|| format!("symlink {} has no target", rel.display()))?
                    .into_owned();
                if !symlink_stays_inside(&rel, &link) {
                    bail!(
                        "symlink {} -> {} points outside the archive",
                        rel.display(),
                        link.display()
                    );
                }
                std::os::unix::fs::symlink(&link, &target)?;
                symlinks.push(rel);
            }
            EntryType::Link => bail!(
                "entry {} is a hard link, which is not allowed",
                rel.display()
            ),
            other => bail!(
                "entry {} has type {other:?}; only files, directories, and symlinks are allowed",
                rel.display()
            ),
        }
    }

    // Lexical checks can be fooled by chains of symlinks; resolve each one.
    for rel in symlinks {
        let path = root.join(&rel);
        match fs::canonicalize(&path) {
            Ok(resolved) if resolved.starts_with(&root) => {}
            Ok(_) => bail!("symlink {} resolves outside the archive", rel.display()),
            Err(_) => bail!("symlink {} is dangling", rel.display()),
        }
    }
    Ok(stats)
}

/// Packs files into a deterministic gzip tar (fixed mode, owner, and mtime).
pub fn pack(files: &[(&str, &[u8])]) -> Result<Vec<u8>> {
    let mut builder = tar::Builder::new(GzEncoder::new(Vec::new(), Compression::default()));
    for (name, contents) in files {
        let mut header = tar::Header::new_gnu();
        header.set_entry_type(EntryType::Regular);
        header.set_size(contents.len() as u64);
        header.set_mode(0o644);
        header.set_uid(0);
        header.set_gid(0);
        header.set_mtime(0);
        builder.append_data(&mut header, name, *contents)?;
    }
    Ok(builder.into_inner()?.finish()?)
}

#[cfg(test)]
mod tests {
    use super::*;

    /// Builds a tar.gz with raw headers, so tests can write names the safe
    /// `tar` APIs refuse.
    fn raw_tar(entries: &[(&str, EntryType, &[u8], Option<&str>)]) -> Vec<u8> {
        let mut builder = tar::Builder::new(GzEncoder::new(Vec::new(), Compression::default()));
        for (name, kind, data, link) in entries {
            let mut header = tar::Header::new_gnu();
            {
                let gnu = header.as_gnu_mut().unwrap();
                gnu.name[..name.len()].copy_from_slice(name.as_bytes());
            }
            header.set_entry_type(*kind);
            header.set_size(data.len() as u64);
            header.set_mode(0o644);
            if let Some(link) = link {
                header.set_link_name(link).unwrap();
            }
            header.set_cksum();
            builder.append(&header, *data).unwrap();
        }
        builder.into_inner().unwrap().finish().unwrap()
    }

    fn extract(data: &[u8]) -> (tempfile::TempDir, Result<Stats>) {
        let dir = tempfile::tempdir().unwrap();
        let res = extract_tar_gz(data, &dir.path().join("repo"), Limits::default());
        (dir, res)
    }

    #[test]
    fn extracts_files_dirs_and_internal_symlinks() {
        let data = raw_tar(&[
            ("modules/", EntryType::Directory, b"", None),
            (
                "modules/vm/main.tf",
                EntryType::Regular,
                b"resource {}",
                None,
            ),
            ("modules/shared.tf", EntryType::Regular, b"locals {}", None),
            (
                "modules/vm/shared.tf",
                EntryType::Symlink,
                b"",
                Some("../shared.tf"),
            ),
            ("./README", EntryType::Regular, b"hi", None),
        ]);
        let (dir, res) = extract(&data);
        let stats = res.unwrap();
        assert_eq!(stats.files, 3);
        let repo = dir.path().join("repo");
        assert_eq!(
            fs::read(repo.join("modules/vm/main.tf")).unwrap(),
            b"resource {}"
        );
        assert_eq!(
            fs::read(repo.join("modules/vm/shared.tf")).unwrap(),
            b"locals {}"
        );
        assert_eq!(fs::read(repo.join("README")).unwrap(), b"hi");
    }

    #[test]
    fn rejects_traversal_and_absolute_paths() {
        for name in ["../evil", "a/../../evil", "/etc/evil"] {
            let data = raw_tar(&[(name, EntryType::Regular, b"x", None)]);
            let (dir, res) = extract(&data);
            assert!(res.is_err(), "{name} was accepted");
            assert!(!dir.path().join("evil").exists());
        }
    }

    #[test]
    fn rejects_escaping_symlinks() {
        for target in ["/etc/passwd", "../outside", "a/../../outside"] {
            let data = raw_tar(&[("link", EntryType::Symlink, b"", Some(target))]);
            let (_dir, res) = extract(&data);
            assert!(res.is_err(), "symlink to {target} was accepted");
        }
    }

    #[test]
    fn rejects_writing_through_a_symlink() {
        // "dir" points inside, but writing "dir/x" must not follow it.
        let data = raw_tar(&[
            ("real/", EntryType::Directory, b"", None),
            ("dir", EntryType::Symlink, b"", Some("real")),
            ("dir/x", EntryType::Regular, b"x", None),
        ]);
        let (_dir, res) = extract(&data);
        let err = res.unwrap_err().to_string();
        assert!(err.contains("not a directory"), "{err}");

        // A file entry that replaces an earlier symlink is refused too.
        let data = raw_tar(&[
            ("real", EntryType::Regular, b"x", None),
            ("ln", EntryType::Symlink, b"", Some("real")),
            ("ln", EntryType::Regular, b"y", None),
        ]);
        let (_dir, res) = extract(&data);
        assert!(res.unwrap_err().to_string().contains("twice"));
    }

    #[test]
    fn rejects_symlink_chains_that_escape() {
        // Each link looks fine lexically, but c -> b/../.. resolves through b.
        let data = raw_tar(&[
            ("x/y/", EntryType::Directory, b"", None),
            ("x/y/b", EntryType::Symlink, b"", Some("../..")),
            ("c", EntryType::Symlink, b"", Some("x/y/b/..")),
        ]);
        let (_dir, res) = extract(&data);
        let err = res.unwrap_err().to_string();
        assert!(err.contains("outside"), "{err}");
    }

    #[test]
    fn rejects_links_devices_and_dangling_symlinks() {
        let cases: Vec<Vec<u8>> = vec![
            raw_tar(&[
                ("f", EntryType::Regular, b"x", None),
                ("h", EntryType::Link, b"", Some("f")),
            ]),
            raw_tar(&[("dev", EntryType::Char, b"", None)]),
            raw_tar(&[("fifo", EntryType::Fifo, b"", None)]),
            raw_tar(&[("d", EntryType::Symlink, b"", Some("missing"))]),
        ];
        for data in cases {
            let (_dir, res) = extract(&data);
            assert!(res.is_err());
        }
    }

    #[test]
    fn enforces_size_limits() {
        let big = vec![0u8; 4096];
        let data = raw_tar(&[
            ("a", EntryType::Regular, &big, None),
            ("b", EntryType::Regular, &big, None),
        ]);
        let dir = tempfile::tempdir().unwrap();
        let limits = Limits {
            max_bytes: 6000,
            max_entries: 10,
        };
        let err = extract_tar_gz(&data, dir.path(), limits)
            .unwrap_err()
            .to_string();
        assert!(err.contains("more than"), "{err}");
        let limits = Limits {
            max_bytes: 1 << 20,
            max_entries: 1,
        };
        assert!(extract_tar_gz(&data, &dir.path().join("2"), limits).is_err());
    }

    #[test]
    fn extracts_pax_archives_without_directory_entries() {
        // Like the backend's bundle: PAX extended headers before each file, no
        // directory entries, and names longer than the 100-byte ustar field.
        let long = format!("modules/{}/main.tf", "nested-directory-name".repeat(6));
        let mut builder = tar::Builder::new(GzEncoder::new(Vec::new(), Compression::default()));
        builder
            .append_pax_extensions([("path", long.as_bytes()), ("mtime", b"0".as_slice())])
            .unwrap();
        let mut header = tar::Header::new_ustar();
        header.set_entry_type(EntryType::Regular);
        header.set_size(5);
        header.set_mode(0o644);
        header.set_path("placeholder").unwrap();
        header.set_cksum();
        builder.append(&header, &b"hello"[..]).unwrap();
        let data = builder.into_inner().unwrap().finish().unwrap();

        let (dir, res) = extract(&data);
        assert_eq!(res.unwrap().files, 1);
        assert_eq!(
            fs::read(dir.path().join("repo").join(&long)).unwrap(),
            b"hello"
        );
        assert!(!dir.path().join("repo/placeholder").exists());
    }

    #[test]
    fn pack_round_trips_deterministically() {
        let a = pack(&[("plan.bin", b"PLAN"), (".terraform.lock.hcl", b"# lock")]).unwrap();
        let b = pack(&[("plan.bin", b"PLAN"), (".terraform.lock.hcl", b"# lock")]).unwrap();
        assert_eq!(a, b);
        let dir = tempfile::tempdir().unwrap();
        extract_tar_gz(&a, dir.path(), Limits::default()).unwrap();
        assert_eq!(fs::read(dir.path().join("plan.bin")).unwrap(), b"PLAN");
        assert_eq!(
            fs::read(dir.path().join(".terraform.lock.hcl")).unwrap(),
            b"# lock"
        );
    }
}
