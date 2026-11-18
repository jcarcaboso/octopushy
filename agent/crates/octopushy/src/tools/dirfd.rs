//! Directories walked by file descriptor, as root, over trees a tool's user can change: every
//! step is relative to an open directory and follows no link, so a name swapped for a link
//! between two steps fails the walk instead of leading it into the agent's own files.

use std::ffi::{CStr, CString};
use std::io;
use std::os::fd::{AsRawFd, FromRawFd, OwnedFd};
use std::os::unix::ffi::OsStrExt;
use std::path::Path;
use std::rc::Rc;

/// An open directory.
#[derive(Debug)]
pub struct Dir(OwnedFd);

/// What `fstatat` says about an entry, without following it.
#[derive(Debug, Clone, Copy)]
pub struct Entry {
    pub uid: u32,
    pub mode: u32,
    pub nlink: u64,
}

impl Entry {
    pub fn is_dir(&self) -> bool {
        self.mode & libc::S_IFMT == libc::S_IFDIR
    }

    pub fn is_file(&self) -> bool {
        self.mode & libc::S_IFMT == libc::S_IFREG
    }

    pub fn is_symlink(&self) -> bool {
        self.mode & libc::S_IFMT == libc::S_IFLNK
    }

    // `nlink_t` is narrower than u64 on some targets.
    #[allow(clippy::unnecessary_cast)]
    fn of(st: &libc::stat) -> Self {
        Self {
            uid: st.st_uid,
            mode: st.st_mode,
            nlink: st.st_nlink as u64,
        }
    }
}

fn check(rc: libc::c_int) -> io::Result<()> {
    if rc < 0 {
        Err(io::Error::last_os_error())
    } else {
        Ok(())
    }
}

const DIR_FLAGS: libc::c_int =
    libc::O_RDONLY | libc::O_DIRECTORY | libc::O_NOFOLLOW | libc::O_CLOEXEC;

impl Dir {
    /// Opens `path`, whose parents are the agent's, without following its last component.
    pub fn open(path: &Path) -> io::Result<Self> {
        let c = CString::new(path.as_os_str().as_bytes())?;
        // SAFETY: a valid C string and flags.
        let fd = unsafe { libc::open(c.as_ptr(), DIR_FLAGS) };
        check(fd)?;
        // SAFETY: open returned a new descriptor that nothing else owns.
        Ok(Self(unsafe { OwnedFd::from_raw_fd(fd) }))
    }

    /// Opens the directory `name` in this one; a link, or anything but a directory, fails.
    pub fn open_at(&self, name: &CStr) -> io::Result<Self> {
        // SAFETY: an open directory and a valid C string.
        let fd = unsafe { libc::openat(self.0.as_raw_fd(), name.as_ptr(), DIR_FLAGS) };
        check(fd)?;
        // SAFETY: openat returned a new descriptor that nothing else owns.
        Ok(Self(unsafe { OwnedFd::from_raw_fd(fd) }))
    }

    /// This directory itself.
    pub fn stat(&self) -> io::Result<Entry> {
        // SAFETY: zeroed stat is a valid out-value.
        let mut st: libc::stat = unsafe { std::mem::zeroed() };
        // SAFETY: an open descriptor and a valid out-pointer.
        check(unsafe { libc::fstat(self.0.as_raw_fd(), &mut st) })?;
        Ok(Entry::of(&st))
    }

    /// The entry `name`, not followed.
    pub fn stat_at(&self, name: &CStr) -> io::Result<Entry> {
        // SAFETY: zeroed stat is a valid out-value.
        let mut st: libc::stat = unsafe { std::mem::zeroed() };
        // SAFETY: an open directory, a valid C string and out-pointer.
        check(unsafe {
            libc::fstatat(
                self.0.as_raw_fd(),
                name.as_ptr(),
                &mut st,
                libc::AT_SYMLINK_NOFOLLOW,
            )
        })?;
        Ok(Entry::of(&st))
    }

    /// The names in this directory, `.` and `..` left out.
    pub fn entries(&self) -> io::Result<Vec<CString>> {
        // fdopendir takes the descriptor it is given: a duplicate, at the start of the directory.
        // SAFETY: an open descriptor.
        let fd = unsafe { libc::fcntl(self.0.as_raw_fd(), libc::F_DUPFD_CLOEXEC, 0) };
        check(fd)?;
        // SAFETY: fd is a directory descriptor of our own.
        unsafe { libc::lseek(fd, 0, libc::SEEK_SET) };
        // SAFETY: as above; on failure the descriptor is still ours to close.
        let dir = unsafe { libc::fdopendir(fd) };
        if dir.is_null() {
            let e = io::Error::last_os_error();
            // SAFETY: fdopendir failed, so fd is still ours.
            unsafe { libc::close(fd) };
            return Err(e);
        }
        let mut names = Vec::new();
        let result = loop {
            // SAFETY: errno is this thread's; readdir sets it only on an error.
            unsafe { *libc::__errno_location() = 0 };
            // SAFETY: an open DIR stream.
            let entry = unsafe { libc::readdir(dir) };
            if entry.is_null() {
                let e = io::Error::last_os_error();
                break if e.raw_os_error() == Some(0) {
                    Ok(())
                } else {
                    Err(e)
                };
            }
            // SAFETY: readdir returned an entry whose name is a C string inside it.
            let name = unsafe { CStr::from_ptr((*entry).d_name.as_ptr()) };
            if name.to_bytes() != b"." && name.to_bytes() != b".." {
                names.push(name.to_owned());
            }
        };
        // SAFETY: closes the stream and the duplicate it holds.
        unsafe { libc::closedir(dir) };
        result.map(|()| names)
    }

    /// Changes this directory's owner.
    pub fn chown(&self, uid: u32, gid: u32) -> io::Result<()> {
        // SAFETY: an open descriptor.
        check(unsafe { libc::fchown(self.0.as_raw_fd(), uid, gid) })
    }

    /// Changes this directory's mode.
    pub fn chmod(&self, mode: u32) -> io::Result<()> {
        // SAFETY: an open descriptor.
        check(unsafe { libc::fchmod(self.0.as_raw_fd(), mode) })
    }

    /// Changes the owner of the entry `name`, a link itself and not what it names, but only if
    /// `allowed` holds for that very entry: it is opened first, and the check and the change are
    /// made on what was opened, so a name swapped in between changes nothing. False when it does
    /// not hold.
    pub fn chown_at_if(
        &self,
        name: &CStr,
        uid: u32,
        gid: u32,
        allowed: impl Fn(&Entry) -> bool,
    ) -> io::Result<bool> {
        // SAFETY: an open directory and a valid C string.
        let fd = unsafe {
            libc::openat(
                self.0.as_raw_fd(),
                name.as_ptr(),
                libc::O_PATH | libc::O_NOFOLLOW | libc::O_CLOEXEC,
            )
        };
        check(fd)?;
        // SAFETY: openat returned a new descriptor that nothing else owns.
        let handle = unsafe { OwnedFd::from_raw_fd(fd) };
        // SAFETY: zeroed stat is a valid out-value.
        let mut st: libc::stat = unsafe { std::mem::zeroed() };
        // SAFETY: an O_PATH descriptor with AT_EMPTY_PATH stats what it refers to.
        check(unsafe {
            libc::fstatat(
                handle.as_raw_fd(),
                c"".as_ptr(),
                &mut st,
                libc::AT_EMPTY_PATH | libc::AT_SYMLINK_NOFOLLOW,
            )
        })?;
        if !allowed(&Entry::of(&st)) {
            return Ok(false);
        }
        // SAFETY: as above; changes exactly the inode checked.
        check(unsafe {
            libc::fchownat(
                handle.as_raw_fd(),
                c"".as_ptr(),
                uid,
                gid,
                libc::AT_EMPTY_PATH | libc::AT_SYMLINK_NOFOLLOW,
            )
        })?;
        Ok(true)
    }

    /// Changes the mode of the regular file `name`, in a tree only the agent can change: a mode
    /// cannot be set on a link itself, so this checks first that it is a regular file.
    pub fn chmod_file_at(&self, name: &CStr, mode: u32) -> io::Result<()> {
        if !self.stat_at(name)?.is_file() {
            return Err(io::Error::other("not a regular file"));
        }
        // SAFETY: an open directory and a valid C string.
        check(unsafe { libc::fchmodat(self.0.as_raw_fd(), name.as_ptr(), mode, 0) })
    }

    /// Removes the entry `name`, a directory when `dir`.
    pub fn unlink_at(&self, name: &CStr, dir: bool) -> io::Result<()> {
        let flags = if dir { libc::AT_REMOVEDIR } else { 0 };
        // SAFETY: an open directory and a valid C string.
        check(unsafe { libc::unlinkat(self.0.as_raw_fd(), name.as_ptr(), flags) })
    }
}

fn gone(e: &io::Error) -> bool {
    e.kind() == io::ErrorKind::NotFound
}

/// Removes `name` in `parent`, and everything below it when it is a directory, by descriptor,
/// following no link. Without recursion: a tool can nest directories as deep as it likes.
pub fn remove_tree_at(parent: &Rc<Dir>, name: &CStr) -> io::Result<()> {
    // (directory, name, the entry opened when its children are pushed)
    let mut stack: Vec<(Rc<Dir>, CString, bool)> = vec![(parent.clone(), name.to_owned(), false)];
    while let Some((dir, name, opened)) = stack.pop() {
        if opened {
            match dir.unlink_at(&name, true) {
                Err(e) if !gone(&e) => return Err(e),
                _ => continue,
            }
        }
        let entry = match dir.stat_at(&name) {
            Ok(entry) => entry,
            Err(e) if gone(&e) => continue,
            Err(e) => return Err(e),
        };
        if entry.is_dir() {
            let sub = Rc::new(dir.open_at(&name)?);
            stack.push((dir, name, true));
            for child in sub.entries()? {
                stack.push((sub.clone(), child, false));
            }
        } else {
            match dir.unlink_at(&name, false) {
                Err(e) if !gone(&e) => return Err(e),
                _ => {}
            }
        }
    }
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::fs;
    use std::os::unix::fs::symlink;

    #[test]
    fn walks_follow_no_link() {
        let dir = tempfile::tempdir().unwrap();
        let outside = dir.path().join("outside");
        fs::create_dir(&outside).unwrap();
        fs::write(outside.join("keep"), "x").unwrap();
        let tree = dir.path().join("tree");
        fs::create_dir_all(tree.join("a/b")).unwrap();
        fs::write(tree.join("a/b/f"), "x").unwrap();
        symlink(&outside, tree.join("a/link")).unwrap();
        let root = Rc::new(Dir::open(&tree).unwrap());
        let mut names = root.entries().unwrap();
        names.sort();
        assert_eq!(names, [c"a".to_owned()]);
        assert!(root.stat_at(c"a").unwrap().is_dir());
        let a = root.open_at(c"a").unwrap();
        assert!(a.stat_at(c"link").unwrap().is_symlink());
        assert!(a.open_at(c"link").is_err(), "a link is never entered");
        assert!(Dir::open(&tree.join("a/link")).is_err());
        remove_tree_at(&root, c"a").unwrap();
        assert!(!tree.join("a").exists());
        assert!(
            outside.join("keep").exists(),
            "what a link named is left alone"
        );
        remove_tree_at(&root, c"a").unwrap();
    }

    #[test]
    fn a_change_of_owner_is_made_only_on_the_entry_checked() {
        let dir = tempfile::tempdir().unwrap();
        fs::write(dir.path().join("f"), "x").unwrap();
        let d = Dir::open(dir.path()).unwrap();
        // SAFETY: no preconditions.
        let (uid, gid) = unsafe { (libc::geteuid(), libc::getegid()) };
        assert!(!d.chown_at_if(c"f", uid, gid, |_| false).unwrap());
        assert!(d.chown_at_if(c"f", uid, gid, |e| e.is_file()).unwrap());
        assert!(d.chown_at_if(c"missing", uid, gid, |_| true).is_err());
    }
}
