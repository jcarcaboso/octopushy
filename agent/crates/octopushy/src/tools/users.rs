//! A user of its own for each attempt's tools, with the `local` runtime when the agent is root.
//!
//! The agent image starts as root and holds a fixed pool of unprivileged users,
//! `octopushy-tool-0`, `octopushy-tool-1`, ... (see `agent/packaging/tool-users.sh`). An attempt
//! that runs a tool leases one for its whole life. Every tool process of the attempt runs as it:
//! real, effective and saved IDs, no supplementary groups, `no_new_privs`, umask 077.
//!
//! What the user may reach:
//!
//! - its workspace: the directory itself is `root:<its group> 0750`, so it can enter and read
//!   it but not add, rename or remove entries. The marker and the directories the agent writes
//!   into while tools run ([`AGENT_OWNED`]) stay root's; it can read but not change them. Every
//!   other entry the agent made is handed to the user before its tool first runs.
//! - nothing else of the agent's: the state directory and the work directory are `root 0711`,
//!   so a name in them can be followed but not listed, and everything in them is root's and
//!   private, apart from the workspaces. Another attempt's workspace is another user's.
//!
//! When the attempt ends, [`Lease::release`] kills every process of the user, takes the
//! workspace back (`root 0700`), and removes whatever the user owns in the shared writable
//! directories and the System V IPC objects it created. Only then does the user go back to the
//! pool. A user whose processes cannot be killed never does.
//!
//! Nix keeps a single-user store owned by one user, which every build writes to. Builds run as
//! that user, the store's owner, one at a time: they are kept from the agent and from other
//! attempts, but not from each other.
//!
//! When the agent is not root none of this is possible; tools run as the agent's own user, one
//! attempt at a time, as before.

use std::ffi::{CStr, CString};
use std::fs;
use std::io;
use std::os::unix::ffi::OsStrExt;
use std::os::unix::fs::{MetadataExt, PermissionsExt};
use std::path::{Path, PathBuf};
use std::sync::{Arc, Mutex};
use std::time::{Duration, Instant};

use anyhow::{Context, Result, bail};
use tokio::process::Command;
use tokio::sync::{Notify, OwnedSemaphorePermit, Semaphore};

/// The names of the pool's users, followed by 0, 1, 2, ...
pub const POOL_PREFIX: &str = "octopushy-tool-";
/// The most pool users looked for.
pub const MAX_POOL: usize = 64;
/// Entries of a workspace that stay the agent's. The agent writes into `ansible/` between a
/// tool's runs, so a link the tool left there would redirect a write made as root.
pub const AGENT_OWNED: &[&str] = &[crate::attempts::workspace::MARKER, "ansible"];
/// Writable by everyone, so swept of what a user leaves when its attempt ends.
pub const SHARED_WRITABLE: &[&str] = &["/tmp", "/var/tmp", "/dev/shm", "/dev/mqueue", "/run/lock"];
/// How long a release keeps killing a user's processes before it gives up on the user.
const KILL_TIMEOUT: Duration = Duration::from_secs(30);
/// The most entries a sweep of the shared directories looks at.
const SWEEP_LIMIT: usize = 200_000;

/// An unprivileged user tools run as.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct ToolUser {
    pub uid: u32,
    pub gid: u32,
}

impl ToolUser {
    /// Makes `cmd` start as this user and nothing more: real, effective and saved user and
    /// group IDs, no supplementary groups (std clears them when root sets a user), and
    /// `no_new_privs`, so no set-user-ID program can lead back. Files it creates are its own
    /// alone (umask 077). The child checks all of it before it runs the program and refuses to
    /// run it otherwise. Its working directory is entered after the drop, as this user.
    pub fn apply(self, cmd: &mut Command) {
        cmd.uid(self.uid).gid(self.gid);
        // SAFETY: only async-signal-safe calls (getres[ug]id, getgroups, setuid, prctl, umask)
        // on values copied into the closure.
        unsafe {
            cmd.pre_exec(move || self.confirm_dropped());
        }
    }

    /// Runs in the child after std has set the IDs.
    fn confirm_dropped(self) -> io::Result<()> {
        let denied = || {
            io::Error::new(
                io::ErrorKind::PermissionDenied,
                "the tool's user is not dropped",
            )
        };
        let (mut r, mut e, mut s) = (0, 0, 0);
        // SAFETY: valid out-pointers; these calls only read the process's credentials.
        unsafe {
            if libc::getresuid(&mut r, &mut e, &mut s) != 0 || [r, e, s] != [self.uid; 3] {
                return Err(denied());
            }
            if libc::getresgid(&mut r, &mut e, &mut s) != 0 || [r, e, s] != [self.gid; 3] {
                return Err(denied());
            }
            if libc::getgroups(0, std::ptr::null_mut()) != 0 {
                return Err(denied());
            }
            // There is no way back.
            if libc::setuid(0) == 0 || libc::setgid(0) == 0 {
                return Err(denied());
            }
            if libc::prctl(libc::PR_SET_NO_NEW_PRIVS, 1, 0, 0, 0) != 0 {
                return Err(io::Error::last_os_error());
            }
            libc::umask(0o077);
        }
        Ok(())
    }
}

/// Looks up a user by name: `None` when there is none.
fn user_by_name(name: &str) -> Result<Option<ToolUser>> {
    let c = CString::new(name)?;
    let mut buf = vec![0 as libc::c_char; 16 * 1024];
    // SAFETY: zeroed passwd is a valid out-value; buf outlives the call.
    let mut pwd: libc::passwd = unsafe { std::mem::zeroed() };
    let mut found: *mut libc::passwd = std::ptr::null_mut();
    // SAFETY: every pointer is valid for the call, with buf's real length.
    let rc = unsafe {
        libc::getpwnam_r(
            c.as_ptr(),
            &mut pwd,
            buf.as_mut_ptr(),
            buf.len(),
            &mut found,
        )
    };
    if rc != 0 {
        return Err(io::Error::from_raw_os_error(rc)).with_context(|| format!("looking up {name}"));
    }
    Ok((!found.is_null()).then_some(ToolUser {
        uid: pwd.pw_uid,
        gid: pwd.pw_gid,
    }))
}

/// Whether this process is root, the only way it can run tools as other users.
pub fn is_root() -> bool {
    // SAFETY: no preconditions.
    unsafe { libc::geteuid() == 0 }
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
enum Slot {
    Free,
    Leased,
    /// Its processes could not all be killed: never leased again.
    Lost,
}

/// The pool of tool users, the Nix store's owner, and the user probes run as.
#[derive(Debug)]
pub struct UserPool {
    users: Vec<ToolUser>,
    slots: Mutex<Vec<Slot>>,
    freed: Notify,
    /// Who Nix builds run as: the owner of the store. `None` when root owns it.
    nix: Option<ToolUser>,
    /// One Nix build at a time: they share the store's owner.
    nix_turn: Arc<Semaphore>,
    /// Who version probes run as: nobody.
    probe: ToolUser,
    /// The directories everyone can write to.
    shared: Vec<PathBuf>,
    /// The agent's own directories, which a sweep leaves alone even inside a shared one: the
    /// state and work directories, whose workspaces are taken back instead, and the Nix store.
    kept: Mutex<Vec<PathBuf>>,
}

impl UserPool {
    /// The pool the image defines, `octopushy-tool-<n>` from 0 up. Refuses a pool that is empty
    /// or not made of distinct, unprivileged users and groups, apart from each other, the Nix
    /// store's owner and the probe user.
    pub fn discover(nix_store_dir: &Path) -> Result<Self> {
        let mut users = Vec::new();
        for n in 0..MAX_POOL {
            match user_by_name(&format!("{POOL_PREFIX}{n}"))? {
                Some(user) => users.push(user),
                None => break,
            }
        }
        let probe = user_by_name("nobody")?.unwrap_or(ToolUser {
            uid: 65534,
            gid: 65534,
        });
        let nix = fs::metadata(nix_store_dir.join("store"))
            .ok()
            .map(|m| ToolUser {
                uid: m.uid(),
                gid: m.gid(),
            })
            .filter(|u| u.uid != 0 && u.gid != 0);
        let shared = SHARED_WRITABLE
            .iter()
            .map(PathBuf::from)
            .filter(|p| p.is_dir())
            .collect();
        Self::new(users, nix, probe, shared)
    }

    pub fn new(
        users: Vec<ToolUser>,
        nix: Option<ToolUser>,
        probe: ToolUser,
        shared: Vec<PathBuf>,
    ) -> Result<Self> {
        if users.is_empty() {
            bail!(
                "the agent runs as root with --tool-runtime local, but there are no tool users \
                 ({POOL_PREFIX}0, {POOL_PREFIX}1, ...) to run tools as; it never runs them as root. \
                 Use the agent image, which has them, or run the agent as an unprivileged user"
            );
        }
        for u in users.iter().chain(nix.iter()).chain([&probe]) {
            if u.uid == 0 || u.gid == 0 {
                bail!("a tool user is root (uid {}, gid {})", u.uid, u.gid);
            }
        }
        let ids = |u: &ToolUser| [u.uid, u.gid];
        for (i, a) in users.iter().enumerate() {
            for b in &users[i + 1..] {
                if ids(a).iter().any(|id| ids(b).contains(id)) {
                    bail!(
                        "tool users must have IDs and groups of their own; {} and {} share one",
                        a.uid,
                        b.uid
                    );
                }
            }
        }
        for other in nix.iter().chain([&probe]) {
            if users
                .iter()
                .any(|u| ids(u).iter().any(|id| ids(other).contains(id)))
            {
                bail!(
                    "a tool user shares an ID with the Nix store's owner or nobody (uid {})",
                    other.uid
                );
            }
        }
        Ok(Self {
            slots: Mutex::new(vec![Slot::Free; users.len()]),
            users,
            freed: Notify::new(),
            nix,
            nix_turn: Arc::new(Semaphore::new(1)),
            probe,
            shared,
            kept: Mutex::new(Vec::new()),
        })
    }

    /// Directories a sweep never enters, nor removes.
    pub fn keep_out_of_sweeps(&self, dirs: impl IntoIterator<Item = PathBuf>) {
        self.kept.lock().unwrap().extend(dirs);
    }

    fn sweep(&self, uid: u32) {
        let kept = self.kept.lock().unwrap().clone();
        sweep(&self.shared, &kept, uid);
    }

    /// How many attempts can run tools at once.
    pub fn size(&self) -> usize {
        self.users.len()
    }

    pub fn nix_user(&self) -> Option<ToolUser> {
        self.nix
    }

    pub fn probe_user(&self) -> ToolUser {
        self.probe
    }

    /// Every user tools may run as, Nix's included.
    pub fn all(&self) -> impl Iterator<Item = ToolUser> + '_ {
        self.users.iter().copied().chain(self.nix)
    }

    /// A user of its own for an attempt of `executor`, or `None` when the executor runs no tool.
    /// A Nix build waits for the store's owner; anything else for a free pool user.
    pub async fn lease(self: &Arc<Self>, executor: &str) -> Result<Option<Lease>> {
        match executor {
            "opentofu" | "ansible" => {}
            "nix" => {
                let Some(user) = self.nix else {
                    bail!("the Nix store is root's, so no build can run as an unprivileged user");
                };
                let turn = self.nix_turn.clone().acquire_owned().await?;
                return Ok(Some(Lease {
                    pool: self.clone(),
                    slot: None,
                    user,
                    _nix_turn: Some(turn),
                }));
            }
            _ => return Ok(None),
        }
        loop {
            let freed = self.freed.notified();
            tokio::pin!(freed);
            freed.as_mut().enable();
            {
                let mut slots = self.slots.lock().unwrap();
                if let Some(i) = slots.iter().position(|s| *s == Slot::Free) {
                    slots[i] = Slot::Leased;
                    return Ok(Some(Lease {
                        pool: self.clone(),
                        slot: Some(i),
                        user: self.users[i],
                        _nix_turn: None,
                    }));
                }
            }
            freed.await;
        }
    }

    /// Kills what a previous agent process left running as any tool user, and sweeps what they
    /// left in the shared directories. At startup, before recovery.
    pub fn clean_all(&self) {
        for user in self.all() {
            if !kill_all(user, KILL_TIMEOUT) {
                tracing::error!(uid = user.uid, "processes of a tool user survive SIGKILL");
            }
            self.sweep(user.uid);
            remove_ipc(user);
        }
    }
}

/// One attempt's user. [`Lease::release`] gives it back.
#[derive(Debug)]
pub struct Lease {
    pool: Arc<UserPool>,
    slot: Option<usize>,
    user: ToolUser,
    _nix_turn: Option<OwnedSemaphorePermit>,
}

impl Lease {
    pub fn user(&self) -> ToolUser {
        self.user
    }

    /// Ends the attempt's hold on its user: kills its processes, takes `workspace` back, removes
    /// what the user owns in the shared directories and its IPC objects. Returns the user to the
    /// pool only when none of its processes is left.
    pub async fn release(mut self, workspace: &Path) {
        let user = self.user;
        let pool = self.pool.clone();
        let ws = workspace.to_path_buf();
        let clean = tokio::task::spawn_blocking(move || {
            let killed = kill_all(user, KILL_TIMEOUT);
            if let Err(e) = reclaim(&ws) {
                tracing::error!(workspace = %ws.display(), "cannot take the workspace back: {e:#}");
            }
            pool.sweep(user.uid);
            remove_ipc(user);
            killed
        })
        .await
        .unwrap_or(false);
        let slot = self.slot.take();
        if let Some(i) = slot {
            let mut slots = self.pool.slots.lock().unwrap();
            if clean {
                slots[i] = Slot::Free;
            } else {
                tracing::error!(
                    uid = user.uid,
                    "processes of a tool user survive SIGKILL; it is never used again"
                );
                slots[i] = Slot::Lost;
            }
        }
        self.pool.freed.notify_waiters();
    }
}

impl Drop for Lease {
    /// Only an attempt whose task panicked gets here without a release: its user is lost.
    fn drop(&mut self) {
        if let Some(i) = self.slot.take() {
            self.pool.slots.lock().unwrap()[i] = Slot::Lost;
            tracing::error!(
                uid = self.user.uid,
                "a tool user was not released; never used again"
            );
        }
    }
}

/// Kills every process of `user`, waiting at most `timeout`. True when none is left.
///
/// A child process becomes the user and sends SIGKILL to every process it may signal, `kill(-1)`:
/// all of the user's, however they were started, and nothing else. It repeats until `/proc`
/// shows none alive, so a process forked meanwhile dies in the next round. Zombies hold nothing
/// and do not count.
pub fn kill_all(user: ToolUser, timeout: Duration) -> bool {
    let started = Instant::now();
    loop {
        if !has_processes(user.uid) {
            return true;
        }
        if started.elapsed() > timeout {
            return false;
        }
        // As the user, SIGKILL to every process it may signal: its own.
        as_user(user, || {
            for _ in 0..100 {
                // SAFETY: a plain syscall.
                if unsafe { libc::kill(-1, libc::SIGKILL) } != 0 {
                    break;
                }
            }
        });
        std::thread::sleep(Duration::from_millis(20));
    }
}

/// Whether a process that is not a zombie has `uid` as its real, effective, saved or file
/// system user.
fn has_processes(uid: u32) -> bool {
    let Ok(entries) = fs::read_dir("/proc") else {
        return false;
    };
    entries.flatten().any(|entry| {
        let name = entry.file_name();
        if !name.as_bytes().iter().all(u8::is_ascii_digit) {
            return false;
        }
        let Ok(status) = fs::read_to_string(entry.path().join("status")) else {
            return false;
        };
        let mut zombie = false;
        let mut mine = false;
        for line in status.lines() {
            if let Some(state) = line.strip_prefix("State:") {
                zombie = state.trim_start().starts_with('Z');
            } else if let Some(ids) = line.strip_prefix("Uid:") {
                mine = ids
                    .split_whitespace()
                    .any(|id| id.parse::<u32>() == Ok(uid));
            }
        }
        mine && !zombie
    })
}

fn lchown(path: &Path, uid: u32, gid: u32) -> io::Result<()> {
    std::os::unix::fs::lchown(path, Some(uid), Some(gid))
}

/// Makes a workspace this user's to work in, before its tool runs. The workspace itself and
/// [`AGENT_OWNED`] entries stay root's, readable by the user's group; every other entry the agent
/// made becomes the user's. An entry already the user's is left as the tool left it, and
/// nothing is followed: a link is changed itself, never what it names. A file of root's with
/// more than one link is not handed over: it may be a link to a file of the agent's.
pub fn hand_over(ws: &Path, user: ToolUser) -> Result<()> {
    let meta = fs::symlink_metadata(ws)?;
    if !meta.is_dir() || meta.uid() != 0 {
        bail!("{} is not a workspace of the agent's", ws.display());
    }
    lchown(ws, 0, user.gid)?;
    fs::set_permissions(ws, fs::Permissions::from_mode(0o750))?;
    for entry in fs::read_dir(ws)? {
        let entry = entry?;
        let path = entry.path();
        let name = entry.file_name();
        if name.as_bytes() == crate::attempts::workspace::MARKER.as_bytes() {
            lchown(&path, 0, 0)?;
            continue;
        }
        if AGENT_OWNED.iter().any(|a| a.as_bytes() == name.as_bytes()) {
            walk(&path, &mut |p, m| {
                if m.file_type().is_symlink() {
                    return Ok(());
                }
                lchown(p, 0, user.gid)?;
                let mode = if m.is_dir() { 0o750 } else { 0o640 };
                fs::set_permissions(p, fs::Permissions::from_mode(mode))
            })?;
            continue;
        }
        walk(&path, &mut |p, m| {
            if m.uid() == 0 && (m.is_dir() || m.nlink() == 1) {
                lchown(p, user.uid, user.gid)?;
            }
            Ok(())
        })?;
    }
    Ok(())
}

/// Takes a workspace back from its user: it can no longer enter it.
pub fn reclaim(ws: &Path) -> Result<()> {
    match fs::symlink_metadata(ws) {
        Ok(m) if m.is_dir() => {
            lchown(ws, 0, 0)?;
            fs::set_permissions(ws, fs::Permissions::from_mode(0o700))?;
            Ok(())
        }
        Ok(_) => bail!("{} is not a directory", ws.display()),
        Err(e) if e.kind() == io::ErrorKind::NotFound => Ok(()),
        Err(e) => Err(e.into()),
    }
}

/// Calls `f` on `path` and, below a directory that is not a link, on everything in it, parents
/// first. Without recursion: a tool can nest directories as deep as it likes.
fn walk(path: &Path, f: &mut dyn FnMut(&Path, &fs::Metadata) -> io::Result<()>) -> Result<()> {
    let mut stack = vec![path.to_path_buf()];
    while let Some(path) = stack.pop() {
        let meta = fs::symlink_metadata(&path)?;
        f(&path, &meta).with_context(|| format!("changing the owner of {}", path.display()))?;
        if meta.is_dir() {
            for entry in fs::read_dir(&path)? {
                stack.push(entry?.path());
            }
        }
    }
    Ok(())
}

/// Removes everything `uid` owns in `dirs`, at any depth, without following links, apart from
/// what is in `kept`.
pub fn sweep(dirs: &[PathBuf], kept: &[PathBuf], uid: u32) {
    let mut seen = 0usize;
    let mut stack: Vec<PathBuf> = dirs.to_vec();
    while let Some(dir) = stack.pop() {
        let Ok(entries) = fs::read_dir(&dir) else {
            continue;
        };
        for entry in entries.flatten() {
            seen += 1;
            if seen > SWEEP_LIMIT {
                tracing::warn!(uid, "stopped sweeping after {SWEEP_LIMIT} entries");
                return;
            }
            let path = entry.path();
            if kept.contains(&path) {
                continue;
            }
            let Ok(meta) = fs::symlink_metadata(&path) else {
                continue;
            };
            let removed = if meta.uid() == uid {
                if meta.is_dir() {
                    fs::remove_dir_all(&path)
                } else {
                    fs::remove_file(&path)
                }
            } else {
                if meta.is_dir() {
                    stack.push(path.clone());
                }
                Ok(())
            };
            if let Err(e) = removed {
                tracing::warn!(uid, path = %path.display(), "cannot remove what a tool user left: {e}");
            }
        }
    }
}

/// Removes the System V shared memory, message queues and semaphores `user` owns or created. As
/// the user itself, in a child: root needs CAP_SYS_ADMIN to remove another user's.
pub fn remove_ipc(user: ToolUser) {
    let table = |file: &str| {
        fs::read_to_string(file)
            .map(|t| ipc_ids(&t, user.uid))
            .unwrap_or_default()
    };
    let (shm, msg, sem) = (
        table("/proc/sysvipc/shm"),
        table("/proc/sysvipc/msg"),
        table("/proc/sysvipc/sem"),
    );
    if shm.is_empty() && msg.is_empty() && sem.is_empty() {
        return;
    }
    as_user(user, || {
        // SAFETY: plain syscalls on IDs; a stale one fails harmlessly.
        unsafe {
            for &id in &shm {
                libc::shmctl(id, libc::IPC_RMID, std::ptr::null_mut());
            }
            for &id in &msg {
                libc::msgctl(id, libc::IPC_RMID, std::ptr::null_mut());
            }
            for &id in &sem {
                libc::semctl(id, 0, libc::IPC_RMID);
            }
        }
    });
}

/// Runs `f` in a child process that has become `user`, and waits for it. `f` must only make
/// async-signal-safe calls: the agent has other threads.
fn as_user(user: ToolUser, f: impl FnOnce()) {
    // SAFETY: the child only switches its IDs, runs `f` and leaves with _exit.
    unsafe {
        let pid = libc::fork();
        if pid == 0 {
            if libc::setgroups(0, std::ptr::null()) != 0
                || libc::setresgid(user.gid, user.gid, user.gid) != 0
                || libc::setresuid(user.uid, user.uid, user.uid) != 0
            {
                libc::_exit(1);
            }
            f();
            libc::_exit(0);
        }
        if pid > 0 {
            let mut status = 0;
            while libc::waitpid(pid, &mut status, 0) < 0
                && io::Error::last_os_error().kind() == io::ErrorKind::Interrupted
            {}
        }
    }
}

/// The IDs in a `/proc/sysvipc` table whose `uid` or `cuid` is `uid`.
fn ipc_ids(table: &str, uid: u32) -> Vec<i32> {
    let mut lines = table.lines();
    let Some(header) = lines.next() else {
        return Vec::new();
    };
    // The ID is the second column (shmid, msqid, semid).
    let columns: Vec<&str> = header.split_whitespace().collect();
    let at = |name: &str| columns.iter().position(|c| *c == name);
    let (id_at, Some(uid_at), Some(cuid_at)) = (1, at("uid"), at("cuid")) else {
        return Vec::new();
    };
    lines
        .filter_map(|line| {
            let fields: Vec<&str> = line.split_whitespace().collect();
            let owner = |i: usize| fields.get(i).and_then(|v| v.parse::<u32>().ok());
            (owner(uid_at) == Some(uid) || owner(cuid_at) == Some(uid))
                .then(|| fields.get(id_at)?.parse().ok())
                .flatten()
        })
        .collect()
}

/// Creates `path` afresh as a private directory of `user`'s, removing whatever was there.
/// For a fixed name in a directory everyone can write to: whoever made it first does not keep it.
pub fn private_dir(path: &Path, user: ToolUser) -> Result<()> {
    match fs::symlink_metadata(path) {
        Ok(m) if m.is_dir() => fs::remove_dir_all(path)?,
        Ok(_) => fs::remove_file(path)?,
        Err(e) if e.kind() == io::ErrorKind::NotFound => {}
        Err(e) => return Err(e.into()),
    }
    use std::os::unix::fs::DirBuilderExt;
    // Fails if someone made it again meanwhile.
    fs::DirBuilder::new()
        .mode(0o700)
        .create(path)
        .with_context(|| format!("creating {}", path.display()))?;
    lchown(path, user.uid, user.gid)?;
    Ok(())
}

/// Makes `path` a directory of root's that others can pass through but not list or add to
/// (0711), replacing whatever another user put there.
pub fn agent_dir(path: &Path) -> Result<()> {
    match fs::symlink_metadata(path) {
        Ok(m) if m.is_dir() && m.uid() == 0 => {}
        Ok(m) => {
            if m.is_dir() {
                fs::remove_dir_all(path)?;
            } else {
                fs::remove_file(path)?;
            }
            fs::create_dir(path).with_context(|| format!("creating {}", path.display()))?;
        }
        Err(e) if e.kind() == io::ErrorKind::NotFound => {
            fs::create_dir(path).with_context(|| format!("creating {}", path.display()))?;
        }
        Err(e) => return Err(e.into()),
    }
    lchown(path, 0, 0)?;
    fs::set_permissions(path, fs::Permissions::from_mode(0o711))?;
    Ok(())
}

/// Locks the agent's state directory down before any tool runs as another user: the agent's
/// files are root's and private. The state directory, the work directory and the cache
/// directory can be passed through (0711) but not listed, as a tool must reach its workspace.
/// A workspace an earlier agent process left is root's and private until its attempt is
/// reported. The Nix cache is the store owner's. Nothing is followed. Files of an agent that
/// ran as an unprivileged user before are taken over here too.
pub fn secure_state(root: &Path, work: &Path, nix_cache: Option<(&Path, ToolUser)>) -> Result<()> {
    fs::create_dir_all(work)?;
    let cache = root.join("cache");
    fs::create_dir_all(&cache)?;
    let mut passable = vec![root.to_path_buf(), cache.clone()];
    // A work directory elsewhere has its parents' own permissions.
    passable.push(work.to_path_buf());
    for dir in &passable {
        lchown(dir, 0, 0)?;
        fs::set_permissions(dir, fs::Permissions::from_mode(0o711))?;
    }
    let mut private: Vec<PathBuf> = Vec::new();
    for dir in [root, &cache, work] {
        for entry in fs::read_dir(dir)? {
            let path = entry?.path();
            if passable.contains(&path) {
                continue;
            }
            if let Some((nix, _)) = nix_cache
                && path == nix
            {
                continue;
            }
            private.push(path);
        }
    }
    for path in private {
        let meta = fs::symlink_metadata(&path)?;
        lchown(&path, 0, 0)?;
        if !meta.file_type().is_symlink() {
            let mode = if meta.is_dir() { 0o700 } else { 0o600 };
            fs::set_permissions(&path, fs::Permissions::from_mode(mode))?;
        }
    }
    if let Some((nix, user)) = nix_cache {
        fs::create_dir_all(nix)?;
        let meta = fs::symlink_metadata(nix)?;
        if !meta.is_dir() {
            bail!("{} is not a directory", nix.display());
        }
        lchown(nix, user.uid, user.gid)?;
        fs::set_permissions(nix, fs::Permissions::from_mode(0o700))?;
    }
    Ok(())
}

/// The name of a user ID, for messages.
pub fn name_of(uid: u32) -> String {
    let mut buf = vec![0 as libc::c_char; 16 * 1024];
    // SAFETY: as in user_by_name.
    let mut pwd: libc::passwd = unsafe { std::mem::zeroed() };
    let mut found: *mut libc::passwd = std::ptr::null_mut();
    // SAFETY: every pointer is valid for the call.
    let rc = unsafe { libc::getpwuid_r(uid, &mut pwd, buf.as_mut_ptr(), buf.len(), &mut found) };
    if rc == 0 && !found.is_null() {
        // SAFETY: getpwuid_r set pw_name to a C string inside buf.
        unsafe { CStr::from_ptr(pwd.pw_name) }
            .to_string_lossy()
            .into_owned()
    } else {
        uid.to_string()
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn user(id: u32) -> ToolUser {
        ToolUser { uid: id, gid: id }
    }

    #[test]
    fn a_pool_must_be_unprivileged_and_apart() {
        let probe = user(65534);
        assert!(UserPool::new(vec![], None, probe, vec![]).is_err(), "empty");
        assert!(
            UserPool::new(vec![user(0)], None, probe, vec![]).is_err(),
            "root"
        );
        assert!(
            UserPool::new(vec![ToolUser { uid: 10, gid: 0 }], None, probe, vec![]).is_err(),
            "root's group"
        );
        assert!(
            UserPool::new(
                vec![user(10), ToolUser { uid: 11, gid: 10 }],
                None,
                probe,
                vec![]
            )
            .is_err(),
            "a shared group"
        );
        assert!(
            UserPool::new(vec![user(10)], Some(user(10)), probe, vec![]).is_err(),
            "Nix's user"
        );
        assert!(
            UserPool::new(vec![user(10)], None, user(10), vec![]).is_err(),
            "nobody"
        );
        let pool = UserPool::new(vec![user(10), user(11)], Some(user(12)), probe, vec![]).unwrap();
        assert_eq!(pool.size(), 2);
        assert_eq!(pool.all().count(), 3);
    }

    #[tokio::test]
    async fn leases_wait_for_a_free_user_and_nix_for_its_turn() {
        let dir = tempfile::tempdir().unwrap();
        let pool = Arc::new(
            UserPool::new(
                vec![user(4_000_001)],
                Some(user(4_000_002)),
                user(65534),
                vec![],
            )
            .unwrap(),
        );
        assert!(pool.lease("proxmox").await.unwrap().is_none());
        let first = pool.lease("opentofu").await.unwrap().unwrap();
        assert_eq!(first.user(), user(4_000_001));
        let waiting = {
            let pool = pool.clone();
            tokio::spawn(async move { pool.lease("ansible").await.unwrap().unwrap().user() })
        };
        tokio::time::sleep(Duration::from_millis(30)).await;
        assert!(!waiting.is_finished());
        // Nobody runs as an ID this high: nothing to kill, nothing to sweep.
        first.release(&dir.path().join("gone")).await;
        assert_eq!(waiting.await.unwrap(), user(4_000_001));

        let nix = pool.lease("nix").await.unwrap().unwrap();
        assert_eq!(nix.user(), user(4_000_002));
        let second = {
            let pool = pool.clone();
            tokio::spawn(async move { pool.lease("nix").await.unwrap().is_some() })
        };
        tokio::time::sleep(Duration::from_millis(30)).await;
        assert!(!second.is_finished(), "one build at a time");
        nix.release(&dir.path().join("gone")).await;
        assert!(second.await.unwrap());
    }

    #[test]
    fn a_sweep_removes_what_the_user_left_at_any_depth_but_not_the_agents_folders() {
        // SAFETY: no preconditions.
        let me = unsafe { libc::geteuid() };
        let dir = tempfile::tempdir().unwrap();
        let shared = dir.path().join("tmp");
        fs::create_dir_all(shared.join("other/deep")).unwrap();
        fs::write(shared.join("left"), "x").unwrap();
        fs::write(shared.join("other/deep/left"), "x").unwrap();
        fs::create_dir_all(shared.join("state/work/a")).unwrap();
        fs::write(shared.join("state/work/a/plan.bin"), "x").unwrap();
        std::os::unix::fs::symlink("/", shared.join("link")).unwrap();
        // Someone else's files would be left; here everything is this user's, so `other` goes
        // with what is in it, and only the kept folder stays.
        sweep(std::slice::from_ref(&shared), &[shared.join("state")], me);
        let left: Vec<_> = fs::read_dir(&shared)
            .unwrap()
            .map(|e| e.unwrap().file_name())
            .collect();
        assert_eq!(left, ["state"]);
        assert!(shared.join("state/work/a/plan.bin").exists());
        // A user with nothing there removes nothing.
        sweep(std::slice::from_ref(&shared), &[], 4_000_009);
        assert!(shared.join("state/work/a/plan.bin").exists());
    }

    #[test]
    fn ipc_tables_are_read_by_their_header() {
        let shm = "       key      shmid perms                  size  cpid  lpid nattch   uid   gid  cuid  cgid      atime      dtime      ctime                   rss                  swap\n\
                   0 7 600 4096 1 1 0 10100 10100 10100 10100 0 0 0 4096 0\n\
                   0 8 600 4096 1 1 0 0 0 10100 0 0 0 0 4096 0\n\
                   0 9 600 4096 1 1 0 0 0 0 0 0 0 0 4096 0\n";
        assert_eq!(ipc_ids(shm, 10100), [7, 8]);
        let sem = "       key      semid perms      nsems   uid   gid  cuid  cgid      otime      ctime\n\
                   0 3 600 1 10101 10101 10101 10101 0 0\n";
        assert_eq!(ipc_ids(sem, 10101), [3]);
        assert!(ipc_ids(sem, 10100).is_empty());
        assert!(ipc_ids("", 1).is_empty());
    }
}
