//! Durable file helpers: atomic writes (temp file, fsync, rename, fsync of the
//! directory) and private directories.

use std::ffi::CString;
use std::fs::{self, File, OpenOptions};
use std::io::{Read, Write};
use std::os::fd::{AsRawFd, FromRawFd};
use std::os::unix::ffi::OsStrExt;
use std::os::unix::fs::{DirBuilderExt, OpenOptionsExt, PermissionsExt};
use std::path::{Component, Path};

use anyhow::{Context, Result, bail};

/// Reads a tool-written regular file beneath the workspace. Walk directory FDs
/// without following any symlink, so replacing a parent while we open it cannot
/// redirect the read. O_NONBLOCK avoids hanging on a hostile FIFO before fstat.
pub fn read_tool_file(workspace: &Path, path: &Path, cap: usize) -> Result<Vec<u8>> {
    let read = || -> Result<Vec<u8>> {
        let relative = path
            .strip_prefix(workspace)
            .context("file is outside the workspace")?;
        let parts = relative
            .components()
            .map(|c| match c {
                Component::Normal(name) => Ok(name),
                _ => anyhow::bail!("file path must stay inside the workspace"),
            })
            .collect::<Result<Vec<_>>>()?;
        if parts.is_empty() {
            bail!("expected a regular file inside the workspace");
        }
        let mut file = OpenOptions::new()
            .read(true)
            .custom_flags(libc::O_DIRECTORY | libc::O_NOFOLLOW | libc::O_CLOEXEC)
            .open(workspace)?;
        for (index, part) in parts.iter().enumerate() {
            let name = CString::new(part.as_bytes())?;
            let mut flags = libc::O_RDONLY | libc::O_NOFOLLOW | libc::O_CLOEXEC | libc::O_NONBLOCK;
            if index + 1 != parts.len() {
                flags |= libc::O_DIRECTORY;
            }
            // SAFETY: the directory FD and NUL-terminated name live through openat.
            let fd = unsafe { libc::openat(file.as_raw_fd(), name.as_ptr(), flags) };
            if fd < 0 {
                return Err(std::io::Error::last_os_error().into());
            }
            // SAFETY: openat returned a new FD, now owned only by this File.
            file = unsafe { File::from_raw_fd(fd) };
        }
        let metadata = file.metadata()?;
        if !metadata.is_file() {
            bail!("expected a regular file, not a directory or special file");
        }
        if metadata.len() > cap as u64 {
            bail!("file exceeds the {cap} byte cap");
        }
        let mut contents = Vec::new();
        file.take(cap as u64 + 1).read_to_end(&mut contents)?;
        if contents.len() > cap {
            bail!("file exceeds the {cap} byte cap");
        }
        Ok(contents)
    };
    read().with_context(|| {
        format!(
            "reading tool file {} without following links",
            path.display()
        )
    })
}

/// Writes `contents` to `path` atomically: readers see either the old or the new
/// file, and the new one survives a crash once this returns.
pub fn write_atomic(path: &Path, contents: &[u8], mode: u32) -> Result<()> {
    let dir = path
        .parent()
        .context("cannot write a file without a parent directory")?;
    let name = path
        .file_name()
        .context("cannot write a file without a name")?
        .to_string_lossy();
    let tmp = dir.join(format!(
        ".{name}.tmp-{}-{}",
        std::process::id(),
        fastrand::u64(..)
    ));
    let result = (|| -> Result<()> {
        let mut file = OpenOptions::new()
            .write(true)
            .create_new(true)
            .mode(mode)
            .open(&tmp)
            .with_context(|| format!("creating {}", tmp.display()))?;
        // `mode` is filtered by the umask on creation; set it exactly.
        file.set_permissions(fs::Permissions::from_mode(mode))?;
        file.write_all(contents)?;
        file.sync_all()?;
        drop(file);
        fs::rename(&tmp, path)
            .with_context(|| format!("renaming {} to {}", tmp.display(), path.display()))?;
        sync_dir(dir)
    })();
    if result.is_err() {
        let _ = fs::remove_file(&tmp);
    }
    result
}

/// Flushes a directory's entries (new, renamed, or removed files) to disk.
pub fn sync_dir(dir: &Path) -> Result<()> {
    File::open(dir)
        .and_then(|d| d.sync_all())
        .with_context(|| format!("syncing directory {}", dir.display()))
}

/// Removes a file if it exists and syncs its directory.
pub fn remove_durably(path: &Path) -> Result<()> {
    match fs::remove_file(path) {
        Ok(()) => {}
        Err(e) if e.kind() == std::io::ErrorKind::NotFound => return Ok(()),
        Err(e) => return Err(e).with_context(|| format!("removing {}", path.display())),
    }
    if let Some(dir) = path.parent() {
        sync_dir(dir)?;
    }
    Ok(())
}

/// Creates `dir` (and parents) if needed, with mode 0700 for new directories.
pub fn ensure_private_dir(dir: &Path) -> Result<()> {
    fs::DirBuilder::new()
        .recursive(true)
        .mode(0o700)
        .create(dir)
        .with_context(|| format!("creating directory {}", dir.display()))
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn tool_reads_reject_links_parents_escapes_special_files_and_oversize() {
        use std::os::unix::fs::symlink;
        let dir = tempfile::tempdir().unwrap();
        let ws = dir.path().join("ws");
        fs::create_dir(&ws).unwrap();
        fs::write(ws.join("ok"), b"abc").unwrap();
        assert_eq!(read_tool_file(&ws, &ws.join("ok"), 3).unwrap(), b"abc");
        assert!(
            read_tool_file(&ws, &ws.join("ok"), 2)
                .unwrap_err()
                .to_string()
                .contains("without following links")
        );
        symlink("ok", ws.join("link")).unwrap();
        symlink(&ws, ws.join("parent")).unwrap();
        for path in [
            ws.join("link"),
            ws.join("parent/ok"),
            ws.join("../ws/ok"),
            ws.clone(),
            dir.path().join("outside"),
        ] {
            assert!(read_tool_file(&ws, &path, 3).is_err(), "{}", path.display());
        }
        let fifo = ws.join("fifo");
        let name = CString::new(fifo.as_os_str().as_bytes()).unwrap();
        // SAFETY: name is a valid NUL-terminated path.
        assert_eq!(unsafe { libc::mkfifo(name.as_ptr(), 0o600) }, 0);
        assert!(read_tool_file(&ws, &fifo, 3).is_err());
    }

    #[test]
    fn atomic_write_replaces_and_sets_mode() {
        let dir = tempfile::tempdir().unwrap();
        let path = dir.path().join("key.pem");
        write_atomic(&path, b"one", 0o600).unwrap();
        write_atomic(&path, b"two", 0o600).unwrap();
        assert_eq!(fs::read(&path).unwrap(), b"two");
        let mode = fs::metadata(&path).unwrap().permissions().mode() & 0o777;
        assert_eq!(mode, 0o600);
        // No temp files are left behind.
        assert_eq!(fs::read_dir(dir.path()).unwrap().count(), 1);
        remove_durably(&path).unwrap();
        remove_durably(&path).unwrap();
        assert!(!path.exists());
    }
}
