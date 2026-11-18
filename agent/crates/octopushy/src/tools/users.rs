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
//! directories and the System V IPC objects it created. Only when all of that is done and
//! verified does the user go back to the pool; otherwise it is quarantined, and nothing runs as
//! it until a later cleanup of it succeeds. The Nix store's owner is held the same way.
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
use std::rc::Rc;
use std::sync::atomic::{AtomicUsize, Ordering};
use std::sync::{Arc, Mutex};
use std::time::{Duration, Instant};

use anyhow::{Context, Result, bail};
use tokio::process::Command;
use tokio::sync::{Notify, OwnedSemaphorePermit, Semaphore};

use super::dirfd::{self, Dir};

/// The names of the pool's users, followed by 0, 1, 2, ...
pub const POOL_PREFIX: &str = "octopushy-tool-";
/// The most pool users looked for.
pub const MAX_POOL: usize = 64;
/// Entries of a workspace that stay the agent's. The agent writes into `ansible/` between a
/// tool's runs, so a link the tool left there would redirect a write made as root.
pub const AGENT_OWNED: &[&str] = &[crate::attempts::workspace::MARKER, "ansible"];
/// The one folder tool users share and can write to: Python's multiprocessing, which Ansible's
/// queues use, makes its semaphores there. Kept sticky and swept of what a user leaves.
pub const SHARED_WRITABLE: &[&str] = &["/dev/shm"];
/// Folders everyone could write to in a stock image, which tool users can write to no longer:
/// root's, 0755, in the image and again when the agent starts. Each attempt's temporary files
/// go in its workspace instead, and leave with it.
pub const LOCKED_DOWN: &[&str] = &["/tmp", "/var/tmp", "/run/lock", "/dev/mqueue"];
/// Where the agent makes each isolated Ansible attempt a private folder for its secrets, in
/// memory: only the agent adds to it.
pub const SECRETS_DIR: &str = "/tmp/octopushy-secrets";
/// Where the agent makes a probe that runs as another user a home of its own.
pub const PROBE_HOMES: &str = "/tmp/octopushy-probes";
/// How long a release keeps killing a user's processes before it gives up on the user.
const KILL_TIMEOUT: Duration = Duration::from_secs(30);
/// The most entries a sweep of the shared directories looks at.
const SWEEP_LIMIT: usize = 200_000;
/// How long a lease waiting for a user waits before it tries a quarantined one again.
const RETRY_QUARANTINED: Duration = Duration::from_secs(30);

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
    /// Its last cleanup did not finish, or could not be verified: a process may still run as it,
    /// or a file of it be left where the next attempt could read it. It is never leased until a
    /// later cleanup of it succeeds.
    Quarantined,
    /// A lease is cleaning it up again.
    Retrying,
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
    /// Whether the Nix user's last cleanup succeeded. While it has not, no build runs as it.
    nix_clean: Mutex<bool>,
    /// Who version probes run as: nobody.
    probe: ToolUser,
    /// The folders tool users can write to, swept at every cleanup.
    shared: Vec<PathBuf>,
    /// Folders tool users must not be able to write to; see [`LOCKED_DOWN`].
    locked: Vec<PathBuf>,
    /// The agent's own directories, which a sweep leaves alone even inside a shared one: the
    /// state and work directories, whose workspaces are taken back instead, and the Nix store.
    kept: Mutex<Vec<PathBuf>>,
    /// Workspaces of a user that could not be taken back, tried again with its next cleanup.
    unreclaimed: Mutex<Vec<(u32, PathBuf)>>,
    /// The most entries one sweep looks at.
    sweep_limit: AtomicUsize,
    /// How long a user's processes may take to die, in milliseconds.
    kill_timeout_ms: AtomicUsize,
    /// Users a process of which outlived one of their attempt's tool runs.
    tainted: Mutex<Vec<u32>>,
    /// Where the next search for a quarantined user to try again starts.
    retry_from: AtomicUsize,
    /// Users whose cleanup fails whatever happens, for tests.
    #[cfg(test)]
    fail_cleanup: Mutex<Vec<u32>>,
    /// Holds every cleanup until the test opens it, counting them.
    #[cfg(test)]
    gate: Mutex<Option<Arc<tests::Gate>>>,
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
        // The agent's own folders in /tmp hold folders it made for users: swept too.
        let shared = SHARED_WRITABLE
            .iter()
            .map(PathBuf::from)
            .filter(|p| p.is_dir())
            .chain([PathBuf::from(SECRETS_DIR), PathBuf::from(PROBE_HOMES)])
            .collect();
        let mut pool = Self::new(users, nix, probe, shared)?;
        pool.locked = LOCKED_DOWN
            .iter()
            .map(PathBuf::from)
            .filter(|p| p.is_dir())
            .collect();
        Ok(pool)
    }

    /// Makes the folders in [`LOCKED_DOWN`] root's and writable by root alone, and `/dev/shm`
    /// root's and sticky, so no user can rename another's entries there. At start, before any
    /// tool runs: a volume or a tmpfs may have replaced what the image set.
    pub fn lock_down(&self) -> Result<()> {
        for (path, mode) in self.locked.iter().map(|p| (p, 0o755)).chain(
            self.shared
                .iter()
                .take(SHARED_WRITABLE.len())
                .map(|p| (p, 0o1777)),
        ) {
            let dir = match Dir::open(path) {
                Ok(dir) => dir,
                Err(e) if e.kind() == io::ErrorKind::NotFound => continue,
                Err(e) => return Err(e).with_context(|| format!("opening {}", path.display())),
            };
            dir.chown(0, 0)
                .and_then(|()| dir.chmod(mode))
                .with_context(|| format!("locking {} down", path.display()))?;
        }
        Ok(())
    }

    /// Removes what `user` left in the locked folders before they were locked: once, at start,
    /// after its processes were killed, as no user can add to them any more.
    fn sweep_locked(&self, user: ToolUser) -> Result<(), String> {
        let kept = self.kept.lock().unwrap().clone();
        sweep(
            &self.locked,
            &kept,
            user.uid,
            self.sweep_limit.load(Ordering::Relaxed),
        )
    }

    /// Whether the folders in [`LOCKED_DOWN`] are still root's and writable by root alone.
    fn check_locked(&self) -> Result<(), String> {
        for path in &self.locked {
            let entry = Dir::open(path).and_then(|d| d.stat());
            match entry {
                Ok(e) if e.uid == 0 && e.mode & 0o022 == 0 => {}
                Ok(_) => {
                    return Err(format!(
                        "{} can be written to by tool users",
                        path.display()
                    ));
                }
                Err(e) if e.kind() == io::ErrorKind::NotFound => {}
                Err(e) => return Err(format!("cannot look at {}: {e}", path.display())),
            }
        }
        Ok(())
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
            nix_clean: Mutex::new(true),
            probe,
            shared,
            locked: Vec::new(),
            kept: Mutex::new(Vec::new()),
            unreclaimed: Mutex::new(Vec::new()),
            sweep_limit: AtomicUsize::new(SWEEP_LIMIT),
            kill_timeout_ms: AtomicUsize::new(KILL_TIMEOUT.as_millis() as usize),
            tainted: Mutex::new(Vec::new()),
            retry_from: AtomicUsize::new(0),
            #[cfg(test)]
            fail_cleanup: Mutex::new(Vec::new()),
            #[cfg(test)]
            gate: Mutex::new(None),
        })
    }

    /// Directories a sweep never enters, nor removes.
    pub fn keep_out_of_sweeps(&self, dirs: impl IntoIterator<Item = PathBuf>) {
        self.kept.lock().unwrap().extend(dirs);
    }

    /// The most entries one sweep looks at before it gives up, and its user is quarantined.
    pub fn set_sweep_limit(&self, limit: usize) {
        self.sweep_limit.store(limit, Ordering::Relaxed);
    }

    /// Ends whatever `user` left: kills its processes, takes its workspaces back, removes what it
    /// owns in `/dev/shm` and its IPC objects. `Ok` only when each step is done and verified;
    /// otherwise why not, and nothing may run as the user again until a later cleanup succeeds.
    /// Runs only inside a cleanup job ([`UserPool::spawn_cleanup`]) or at startup, so never twice
    /// at once for one user.
    fn cleanup(&self, user: ToolUser, workspace: Option<&Path>) -> Result<(), String> {
        #[cfg(test)]
        let _held = self.gate.lock().unwrap().clone().map(|g| g.pass());
        let mut problems = Vec::new();
        // First: a process still running could leave more behind.
        if !kill_all(user, self.kill_timeout()) {
            problems.push("its processes survive SIGKILL, or /proc cannot tell".to_owned());
        }
        // Workspaces an earlier cleanup could not take back stay listed until one does.
        let mut workspaces: Vec<PathBuf> = self
            .unreclaimed
            .lock()
            .unwrap()
            .iter()
            .filter(|(uid, _)| *uid == user.uid)
            .map(|(_, ws)| ws.clone())
            .collect();
        if let Some(ws) = workspace
            && !workspaces.iter().any(|w| w == ws)
        {
            workspaces.push(ws.to_path_buf());
        }
        for ws in workspaces {
            let mut unreclaimed = self.unreclaimed.lock().unwrap();
            match reclaim(&ws) {
                Ok(()) => unreclaimed.retain(|(uid, w)| !(*uid == user.uid && *w == ws)),
                Err(e) => {
                    problems.push(format!("cannot take back {}: {e:#}", ws.display()));
                    if !unreclaimed
                        .iter()
                        .any(|(uid, w)| *uid == user.uid && *w == ws)
                    {
                        unreclaimed.push((user.uid, ws));
                    }
                }
            }
        }
        let kept = self.kept.lock().unwrap().clone();
        let limit = self.sweep_limit.load(Ordering::Relaxed);
        if let Err(e) = sweep(&self.shared, &kept, user.uid, limit) {
            problems.push(e);
        }
        // Nothing of the user's can be in them while they stay locked.
        if let Err(e) = self.check_locked() {
            problems.push(e);
        }
        if let Err(e) = remove_ipc(user) {
            problems.push(e);
        }
        #[cfg(test)]
        if self.fail_cleanup.lock().unwrap().contains(&user.uid) {
            problems.push("a cleanup failure the test asked for".to_owned());
        }
        if problems.is_empty() {
            Ok(())
        } else {
            Err(problems.join("; "))
        }
    }

    /// Starts a cleanup of `user` as a job of its own, which holds `owner`, the slot's cleaning
    /// state or the Nix turn, until it ends, and then records the outcome itself: whoever waits
    /// for it may stop waiting without letting another cleanup, or a lease, in meanwhile. The
    /// handle gives the outcome back, with the Nix turn, which is given up once the handle is
    /// dropped. A release is clean only if no process of the user survived one of the attempt's
    /// tool runs either; a successful retry clears that.
    fn spawn_cleanup(
        self: &Arc<Self>,
        user: ToolUser,
        workspace: Option<PathBuf>,
        owner: Owner,
        kind: CleanupKind,
    ) -> tokio::task::JoinHandle<(Result<(), String>, Option<OwnedSemaphorePermit>)> {
        let pool = self.clone();
        tokio::task::spawn_blocking(move || {
            let (slot, turn) = match owner {
                Owner::Slot(i) => (Some(i), None),
                Owner::Nix(turn) => (None, Some(turn)),
            };
            // Records the outcome when it drops, a panic's included, before the turn is given up.
            let mut finish = Finish {
                pool: pool.clone(),
                slot,
                clean: false,
            };
            let mut result = pool.cleanup(user, workspace.as_deref());
            match kind {
                CleanupKind::Release if result.is_ok() && pool.is_tainted(user.uid) => {
                    result = Err(
                        "a process of the user outlived one of the attempt's tool runs, \
                         so it stays quarantined until a later cleanup"
                            .into(),
                    );
                }
                CleanupKind::Retry if result.is_ok() => pool.untaint(user.uid),
                _ => {}
            }
            finish.clean = result.is_ok();
            drop(finish);
            (result, turn)
        })
    }

    /// Marks `uid` as having had a process outlive one of its tool runs: no tool runs as it again
    /// in the attempt, and its release quarantines it.
    pub fn taint(&self, uid: u32) {
        let mut tainted = self.tainted.lock().unwrap();
        if !tainted.contains(&uid) {
            tainted.push(uid);
        }
    }

    pub fn is_tainted(&self, uid: u32) -> bool {
        self.tainted.lock().unwrap().contains(&uid)
    }

    fn untaint(&self, uid: u32) {
        self.tainted.lock().unwrap().retain(|&u| u != uid);
    }

    /// How long the processes of a user may take to die before its cleanup fails.
    pub fn kill_timeout(&self) -> Duration {
        Duration::from_millis(self.kill_timeout_ms.load(Ordering::Relaxed) as u64)
    }

    pub fn set_kill_timeout(&self, timeout: Duration) {
        self.kill_timeout_ms
            .store(timeout.as_millis() as usize, Ordering::Relaxed);
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

    /// The users nothing runs as because their last cleanup failed, or one runs now, Nix's
    /// included.
    pub fn quarantined(&self) -> Vec<u32> {
        let slots = self.slots.lock().unwrap();
        let mut out: Vec<u32> = self
            .users
            .iter()
            .zip(slots.iter())
            .filter(|(_, s)| matches!(s, Slot::Quarantined | Slot::Retrying))
            .map(|(u, _)| u.uid)
            .collect();
        if let Some(nix) = self.nix
            && !*self.nix_clean.lock().unwrap()
        {
            out.push(nix.uid);
        }
        out
    }

    /// Whether Nix builds can run: its user is clean, or is cleaned now. Waits for no build: one
    /// that runs had a clean user when it started.
    pub async fn nix_ready(self: &Arc<Self>) -> Result<()> {
        let Some(user) = self.nix else {
            bail!("the Nix store is root's, so no build can run as an unprivileged user");
        };
        if *self.nix_clean.lock().unwrap() {
            return Ok(());
        }
        let Ok(turn) = self.nix_turn.clone().try_acquire_owned() else {
            return Ok(());
        };
        self.clean_nix(user, turn).await.map(drop)
    }

    /// Cleans the Nix user up again in a job that holds `turn` until it ends. On success the turn
    /// comes back for a build.
    async fn clean_nix(
        self: &Arc<Self>,
        user: ToolUser,
        turn: OwnedSemaphorePermit,
    ) -> Result<OwnedSemaphorePermit> {
        let job = self.spawn_cleanup(user, None, Owner::Nix(turn), CleanupKind::Retry);
        let (result, turn) = job.await.context("the cleanup of the Nix user")?;
        match (result, turn) {
            (Ok(()), Some(turn)) => Ok(turn),
            (Ok(()), None) => bail!("the cleanup of the Nix user lost its turn"),
            (Err(why), _) => bail!(
                "the Nix store's owner (uid {}) is not clean, so no build runs as it: {why}",
                user.uid
            ),
        }
    }

    /// A user of its own for an attempt of `executor`, or `None` when the executor runs no tool.
    /// A Nix build waits for the store's owner, and fails while it cannot be cleaned up; anything
    /// else waits for a free pool user, or a quarantined one whose cleanup now succeeds. Each
    /// quarantined user is tried in turn, once each, before it waits again.
    pub async fn lease(self: &Arc<Self>, executor: &str) -> Result<Option<Lease>> {
        match executor {
            "opentofu" | "ansible" => {}
            "nix" => {
                let Some(user) = self.nix else {
                    bail!("the Nix store is root's, so no build can run as an unprivileged user");
                };
                let mut turn = self.nix_turn.clone().acquire_owned().await?;
                if !*self.nix_clean.lock().unwrap() {
                    turn = self.clean_nix(user, turn).await?;
                }
                return Ok(Some(Lease {
                    pool: self.clone(),
                    slot: None,
                    user,
                    released: false,
                    _nix_turn: Some(turn),
                }));
            }
            _ => return Ok(None),
        }
        let mut tried: Vec<usize> = Vec::new();
        loop {
            let freed = self.freed.notified();
            tokio::pin!(freed);
            freed.as_mut().enable();
            let retry = {
                let mut slots = self.slots.lock().unwrap();
                if let Some(i) = slots.iter().position(|s| *s == Slot::Free) {
                    slots[i] = Slot::Leased;
                    return Ok(Some(Lease {
                        pool: self.clone(),
                        slot: Some(i),
                        user: self.users[i],
                        released: false,
                        _nix_turn: None,
                    }));
                }
                let n = slots.len();
                let from = self.retry_from.load(Ordering::Relaxed);
                let next = (0..n)
                    .map(|k| (from + k) % n)
                    .find(|i| slots[*i] == Slot::Quarantined && !tried.contains(i));
                if let Some(i) = next {
                    slots[i] = Slot::Retrying;
                    self.retry_from.store(i + 1, Ordering::Relaxed);
                }
                next
            };
            match retry {
                Some(i) => {
                    tried.push(i);
                    let user = self.users[i];
                    // The job owns the slot until it ends: dropping this wait changes nothing.
                    let job = self.spawn_cleanup(user, None, Owner::Slot(i), CleanupKind::Retry);
                    match job.await {
                        Ok((Ok(()), _)) => {
                            tracing::info!(uid = user.uid, "a quarantined tool user is clean again")
                        }
                        Ok((Err(why), _)) => {
                            tracing::warn!(uid = user.uid, "a tool user is still not clean: {why}")
                        }
                        Err(e) => tracing::error!(uid = user.uid, "a cleanup failed: {e}"),
                    }
                }
                None => {
                    // Every quarantined user was tried: a release, or time to try them again.
                    tried.clear();
                    let _ = tokio::time::timeout(RETRY_QUARANTINED, freed).await;
                }
            }
        }
    }

    /// Kills what a previous agent process left running as any tool user, and removes what they
    /// left, before recovery. A user whose cleanup fails is quarantined, Nix's included. Fails
    /// when no pool user is left to run tools as.
    pub fn clean_all(&self) -> Result<()> {
        for (i, &user) in self.users.iter().enumerate() {
            let result = self
                .cleanup(user, None)
                .and_then(|()| self.sweep_locked(user));
            if let Err(why) = &result {
                tracing::error!(
                    uid = user.uid,
                    "quarantining a tool user that is not clean: {why}"
                );
            }
            self.slots.lock().unwrap()[i] = if result.is_ok() {
                Slot::Free
            } else {
                Slot::Quarantined
            };
        }
        if let Some(nix) = self.nix {
            let result = self
                .cleanup(nix, None)
                .and_then(|()| self.sweep_locked(nix));
            if let Err(why) = &result {
                tracing::error!(
                    uid = nix.uid,
                    "the Nix store's owner is not clean; no build runs until it is: {why}"
                );
            }
            *self.nix_clean.lock().unwrap() = result.is_ok();
        }
        if !self.slots.lock().unwrap().contains(&Slot::Free) {
            bail!(
                "none of the {} tool users could be cleaned up, so no attempt could run apart \
                 from what an earlier one left; see the errors above",
                self.users.len()
            );
        }
        Ok(())
    }
}

/// What a cleanup job holds until it ends.
enum Owner {
    /// A pool user's slot, `Retrying` or `Leased` meanwhile.
    Slot(usize),
    /// The Nix turn: no build runs meanwhile.
    Nix(OwnedSemaphorePermit),
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
enum CleanupKind {
    /// At the end of an attempt.
    Release,
    /// Of a quarantined user, before it may be leased.
    Retry,
}

/// Records how a cleanup job ended, when dropped: a pool user free or quarantined, the Nix user
/// clean or not. A job that panicked leaves its user quarantined.
struct Finish {
    pool: Arc<UserPool>,
    slot: Option<usize>,
    clean: bool,
}

impl Drop for Finish {
    fn drop(&mut self) {
        match self.slot {
            Some(i) => {
                self.pool.slots.lock().unwrap()[i] = if self.clean {
                    Slot::Free
                } else {
                    Slot::Quarantined
                };
            }
            None => *self.pool.nix_clean.lock().unwrap() = self.clean,
        }
        self.pool.freed.notify_waiters();
    }
}

/// One attempt's user. [`Lease::release`] gives it back.
#[derive(Debug)]
pub struct Lease {
    pool: Arc<UserPool>,
    slot: Option<usize>,
    user: ToolUser,
    released: bool,
    _nix_turn: Option<OwnedSemaphorePermit>,
}

impl Lease {
    pub fn user(&self) -> ToolUser {
        self.user
    }

    /// Whether a process of the user outlived one of the attempt's tool runs: what the attempt
    /// did, or read back, cannot be trusted.
    pub fn tainted(&self) -> bool {
        self.pool.is_tainted(self.user.uid)
    }

    /// Ends the attempt's hold on its user: kills its processes, takes `workspace` back, removes
    /// what the user owns in `/dev/shm` and its IPC objects, in a job that holds the user until
    /// it ends. Returns the user to the pool only when all of it is done and verified, and no
    /// process of it outlived a tool run; quarantines it otherwise. Nix's user is marked unclean
    /// before its turn is given back.
    pub async fn release(mut self, workspace: &Path) {
        let user = self.user;
        let owner = match (self.slot, self._nix_turn.take()) {
            (Some(i), _) => Owner::Slot(i),
            (None, Some(turn)) => Owner::Nix(turn),
            (None, None) => return,
        };
        self.released = true;
        let job = self.pool.spawn_cleanup(
            user,
            Some(workspace.to_path_buf()),
            owner,
            CleanupKind::Release,
        );
        match job.await {
            Ok((Ok(()), _)) => {}
            Ok((Err(why), _)) => tracing::error!(
                uid = user.uid,
                "a tool user is not clean, so nothing runs as it until a later cleanup succeeds: {why}"
            ),
            Err(e) => tracing::error!(uid = user.uid, "the cleanup of a tool user failed: {e}"),
        }
    }
}

impl Drop for Lease {
    /// Only an attempt whose task panicked gets here without a release: its user is not known to
    /// be clean, so it is quarantined, before a Nix turn is given back.
    fn drop(&mut self) {
        if self.released {
            return;
        }
        match self.slot {
            Some(i) => self.pool.slots.lock().unwrap()[i] = Slot::Quarantined,
            None => *self.pool.nix_clean.lock().unwrap() = false,
        }
        tracing::error!(
            uid = self.user.uid,
            "a tool user was not released; quarantined"
        );
        self.pool.freed.notify_waiters();
    }
}

/// Kills every process of `user`, waiting at most `timeout`. True when none is left; false when
/// one survives, or `/proc` cannot be read to tell.
///
/// A child process becomes the user and sends SIGKILL to every process it may signal, `kill(-1)`:
/// all of the user's, however they were started, and nothing else. It also signals each thread
/// group [`live_thread_groups`] found by its ID, which reaches a group whose leader has exited
/// while its other threads run. As the user it can signal nothing else, even if an ID is reused
/// meanwhile. It repeats until `/proc` shows no thread of the user alive, so a process forked
/// meanwhile dies in the next round.
pub fn kill_all(user: ToolUser, timeout: Duration) -> bool {
    let started = Instant::now();
    loop {
        let groups = match live_thread_groups(user.uid) {
            Ok(groups) => groups,
            Err(e) => {
                tracing::error!(
                    uid = user.uid,
                    "cannot tell which processes a tool user has: {e}"
                );
                return false;
            }
        };
        if groups.is_empty() {
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
            for &pid in &groups {
                // SAFETY: a plain syscall; the user may only signal its own.
                unsafe { libc::kill(pid, libc::SIGKILL) };
            }
        });
        std::thread::sleep(Duration::from_millis(20));
    }
}

/// The thread groups with a live thread whose real, effective, saved or file system user is
/// `uid`. Every thread is looked at, not only the leader: a leader that called `pthread_exit`
/// shows as a zombie while its other threads run. A thread or process that ends while it is read
/// is gone; any other failure to read `/proc` is an error, never "none".
fn live_thread_groups(uid: u32) -> io::Result<Vec<libc::pid_t>> {
    let gone = |e: &io::Error, path: &Path| {
        e.kind() == io::ErrorKind::NotFound
            || e.raw_os_error() == Some(libc::ESRCH)
            || !path.exists()
    };
    let mut found = Vec::new();
    for entry in fs::read_dir("/proc")? {
        let entry = entry?;
        let name = entry.file_name();
        let Some(pid) = std::str::from_utf8(name.as_bytes())
            .ok()
            .and_then(|n| n.parse::<libc::pid_t>().ok())
        else {
            continue;
        };
        let process = entry.path();
        let tasks = match fs::read_dir(process.join("task")) {
            Ok(tasks) => tasks,
            Err(e) if gone(&e, &process) => continue,
            Err(e) => return Err(e),
        };
        for task in tasks {
            let task = match task {
                Ok(task) => task.path(),
                Err(e) if gone(&e, &process) => break,
                Err(e) => return Err(e),
            };
            let status = match fs::read_to_string(task.join("status")) {
                Ok(status) => status,
                Err(e) if gone(&e, &task) => continue,
                Err(e) => return Err(e),
            };
            if thread_is_live_as(&status, uid) {
                found.push(pid);
                break;
            }
        }
    }
    Ok(found)
}

/// Whether a `/proc/<pid>/task/<tid>/status` is of a thread that is not dead or a zombie, with
/// `uid` among its user IDs.
fn thread_is_live_as(status: &str, uid: u32) -> bool {
    let mut dead = false;
    let mut mine = false;
    for line in status.lines() {
        if let Some(state) = line.strip_prefix("State:") {
            dead = matches!(state.trim_start().chars().next(), Some('Z' | 'X'));
        } else if let Some(ids) = line.strip_prefix("Uid:") {
            mine = ids
                .split_whitespace()
                .any(|id| id.parse::<u32>() == Ok(uid));
        }
    }
    mine && !dead
}

fn lchown(path: &Path, uid: u32, gid: u32) -> io::Result<()> {
    std::os::unix::fs::lchown(path, Some(uid), Some(gid))
}

/// Makes a workspace this user's to work in, before its tool runs. The workspace itself and
/// [`AGENT_OWNED`] entries stay root's, readable by the user's group; every other entry the agent
/// made becomes the user's. An entry already the user's is left as the tool left it. The walk is
/// by descriptor and follows no link: a link is changed itself, never what it names, and a
/// directory swapped for a link fails the walk. A file of root's with more than one link is not
/// handed over: it may be a link to a file of the agent's.
pub fn hand_over(ws: &Path, user: ToolUser) -> Result<()> {
    let root = Rc::new(Dir::open(ws).with_context(|| format!("opening {}", ws.display()))?);
    let meta = root.stat()?;
    if !meta.is_dir() || meta.uid != 0 {
        bail!("{} is not a workspace of the agent's", ws.display());
    }
    root.chown(0, user.gid)?;
    root.chmod(0o750)?;
    for name in root.entries()? {
        let shown = || format!("{}/{}", ws.display(), name.to_string_lossy());
        if name.as_bytes() == crate::attempts::workspace::MARKER.as_bytes() {
            root.chown_at_if(&name, 0, 0, |_| true)
                .with_context(shown)?;
        } else if AGENT_OWNED.iter().any(|a| a.as_bytes() == name.as_bytes()) {
            agent_readable(&root, &name, user.gid).with_context(shown)?;
        } else {
            give(&root, &name, user).with_context(shown)?;
        }
    }
    Ok(())
}

/// Hands `name` and what is below it over to `user`: whatever of it is root's, apart from files
/// with another link.
fn give(parent: &Rc<Dir>, name: &CStr, user: ToolUser) -> io::Result<()> {
    let mut stack = vec![(parent.clone(), name.to_owned())];
    while let Some((dir, name)) = stack.pop() {
        let entry = dir.stat_at(&name)?;
        dir.chown_at_if(&name, user.uid, user.gid, |e| {
            e.uid == 0 && (e.is_dir() || e.nlink == 1)
        })?;
        if entry.is_dir() {
            let sub = Rc::new(dir.open_at(&name)?);
            for child in sub.entries()? {
                stack.push((sub.clone(), child));
            }
        }
    }
    Ok(())
}

/// Makes `name`, a tree only the agent writes to, readable by the user's group and nobody else.
fn agent_readable(parent: &Rc<Dir>, name: &CStr, gid: u32) -> io::Result<()> {
    let mut stack = vec![(parent.clone(), name.to_owned())];
    while let Some((dir, name)) = stack.pop() {
        let entry = dir.stat_at(&name)?;
        if entry.is_symlink() {
            continue;
        }
        if entry.is_dir() {
            let sub = Rc::new(dir.open_at(&name)?);
            sub.chown(0, gid)?;
            sub.chmod(0o750)?;
            for child in sub.entries()? {
                stack.push((sub.clone(), child));
            }
        } else if entry.is_file() {
            dir.chown_at_if(&name, 0, gid, |e| e.is_file())?;
            dir.chmod_file_at(&name, 0o640)?;
        }
    }
    Ok(())
}

/// Takes a workspace back from its user: it can no longer enter it. By descriptor, following no
/// link.
pub fn reclaim(ws: &Path) -> Result<()> {
    let dir = match Dir::open(ws) {
        Ok(dir) => dir,
        Err(e) if e.kind() == io::ErrorKind::NotFound => return Ok(()),
        Err(e) => return Err(e).with_context(|| format!("opening {}", ws.display())),
    };
    dir.chown(0, 0)?;
    dir.chmod(0o700)?;
    Ok(())
}

/// Removes everything `uid` owns in `dirs`, at any depth, by descriptor and without following
/// links, apart from what is in `kept`. `Ok` only when the whole of `dirs` was looked at and
/// each of the user's entries removed: looking at more than `limit` entries, a folder that cannot
/// be read, or an entry that cannot be removed is an error, and the user is not clean. Another
/// user's entries count toward `limit` too: a folder of another user's can hold this one's files,
/// so the sweep cannot skip it.
pub fn sweep(dirs: &[PathBuf], kept: &[PathBuf], uid: u32, limit: usize) -> Result<(), String> {
    let vanished = |e: &io::Error| e.kind() == io::ErrorKind::NotFound;
    let mut seen = 0usize;
    let mut failures = Vec::new();
    // (the directory, its path, for messages and `kept`)
    let mut stack: Vec<(Rc<Dir>, PathBuf)> = Vec::new();
    for top in dirs {
        match Dir::open(top) {
            Ok(dir) => stack.push((Rc::new(dir), top.clone())),
            Err(e) if vanished(&e) => {}
            Err(e) => failures.push(format!("cannot open {}: {e}", top.display())),
        }
    }
    while let Some((dir, path)) = stack.pop() {
        let names = match dir.entries() {
            Ok(names) => names,
            Err(e) => {
                failures.push(format!("cannot read {}: {e}", path.display()));
                continue;
            }
        };
        for name in names {
            seen += 1;
            if seen > limit {
                return Err(format!(
                    "the shared folders hold more than {limit} entries, so they were not all \
                     looked at for what the user left"
                ));
            }
            let child = path.join(std::ffi::OsStr::from_bytes(name.as_bytes()));
            if kept.contains(&child) {
                continue;
            }
            let entry = match dir.stat_at(&name) {
                Ok(entry) => entry,
                Err(e) if vanished(&e) => continue,
                Err(e) => {
                    failures.push(format!("cannot look at {}: {e}", child.display()));
                    continue;
                }
            };
            if entry.uid == uid {
                if let Err(e) = dirfd::remove_tree_at(&dir, &name) {
                    failures.push(format!("cannot remove {}: {e}", child.display()));
                }
            } else if entry.is_dir() {
                match dir.open_at(&name) {
                    Ok(sub) => stack.push((Rc::new(sub), child)),
                    Err(e) if vanished(&e) => {}
                    Err(e) => failures.push(format!("cannot open {}: {e}", child.display())),
                }
            }
        }
    }
    if failures.is_empty() {
        Ok(())
    } else {
        Err(failures.join("; "))
    }
}

/// Removes the System V shared memory, message queues and semaphores `user` owns or created. As
/// the user itself, in a child: root needs CAP_SYS_ADMIN to remove another user's. `Ok` only when
/// the tables then show none of the user's.
pub fn remove_ipc(user: ToolUser) -> Result<(), String> {
    let tables = || {
        let table = |file: &str| match fs::read_to_string(file) {
            Ok(t) => Ok(ipc_ids(&t, user.uid)),
            // A kernel without System V IPC has none.
            Err(e) if e.kind() == io::ErrorKind::NotFound => Ok(Vec::new()),
            Err(e) => Err(format!("cannot read {file}: {e}")),
        };
        Ok::<_, String>((
            table("/proc/sysvipc/shm")?,
            table("/proc/sysvipc/msg")?,
            table("/proc/sysvipc/sem")?,
        ))
    };
    let (shm, msg, sem) = tables()?;
    if shm.is_empty() && msg.is_empty() && sem.is_empty() {
        return Ok(());
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
    match tables()? {
        (shm, msg, sem) if shm.is_empty() && msg.is_empty() && sem.is_empty() => Ok(()),
        _ => Err("System V IPC objects of the user remain".to_owned()),
    }
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
        sweep(
            std::slice::from_ref(&shared),
            &[shared.join("state")],
            me,
            100,
        )
        .unwrap();
        let left: Vec<_> = fs::read_dir(&shared)
            .unwrap()
            .map(|e| e.unwrap().file_name())
            .collect();
        assert_eq!(left, ["state"]);
        assert!(shared.join("state/work/a/plan.bin").exists());
        // A user with nothing there removes nothing.
        sweep(std::slice::from_ref(&shared), &[], 4_000_009, 100).unwrap();
        assert!(shared.join("state/work/a/plan.bin").exists());
    }

    fn pool_of(ids: &[u32], nix: Option<u32>) -> Arc<UserPool> {
        Arc::new(
            UserPool::new(
                ids.iter().map(|&id| user(id)).collect(),
                nix.map(user),
                user(65534),
                vec![],
            )
            .unwrap(),
        )
    }

    /// Holds cleanups until opened; counts those that started and the most that ran at once.
    #[derive(Debug, Default)]
    pub(super) struct Gate {
        open: Mutex<bool>,
        opened: std::sync::Condvar,
        pub started: std::sync::atomic::AtomicUsize,
        running: std::sync::atomic::AtomicUsize,
        pub most_at_once: std::sync::atomic::AtomicUsize,
    }

    /// Marks a cleanup as running until dropped.
    pub(super) struct Passing(Arc<Gate>);

    impl Drop for Passing {
        fn drop(&mut self) {
            self.0.running.fetch_sub(1, Ordering::SeqCst);
        }
    }

    impl Gate {
        pub(super) fn pass(self: Arc<Self>) -> Passing {
            self.started.fetch_add(1, Ordering::SeqCst);
            let now = self.running.fetch_add(1, Ordering::SeqCst) + 1;
            self.most_at_once.fetch_max(now, Ordering::SeqCst);
            let mut open = self.open.lock().unwrap();
            while !*open {
                open = self.opened.wait(open).unwrap();
            }
            drop(open);
            Passing(self)
        }

        fn open(&self) {
            *self.open.lock().unwrap() = true;
            self.opened.notify_all();
        }

        fn started(&self) -> usize {
            self.started.load(Ordering::SeqCst)
        }
    }

    fn hold(pool: &UserPool) -> Arc<Gate> {
        let gate = Arc::new(Gate::default());
        *pool.gate.lock().unwrap() = Some(gate.clone());
        gate
    }

    async fn until(what: &str, mut done: impl FnMut() -> bool) {
        tokio::time::timeout(Duration::from_secs(5), async {
            while !done() {
                tokio::time::sleep(Duration::from_millis(10)).await;
            }
        })
        .await
        .unwrap_or_else(|_| panic!("{what}"));
    }

    #[tokio::test(flavor = "multi_thread", worker_threads = 2)]
    async fn a_cancelled_waiter_leaves_its_cleanup_to_finish_alone() {
        let a = 4_300_051;
        let pool = pool_of(&[a], None);
        fail_cleanup(&pool, a, true);
        let _ = pool.clean_all();
        fail_cleanup(&pool, a, false);
        let gate = hold(&pool);
        let first = {
            let pool = pool.clone();
            tokio::spawn(async move { pool.lease("opentofu").await })
        };
        until("the first cleanup starts", || gate.started() == 1).await;
        first.abort();
        let _ = first.await;
        // The cleanup still runs: nobody else cleans the user up, nor leases it, meanwhile.
        let second = {
            let pool = pool.clone();
            tokio::spawn(async move { pool.lease("opentofu").await.unwrap().unwrap() })
        };
        tokio::time::sleep(Duration::from_millis(200)).await;
        pool.freed.notify_waiters();
        tokio::time::sleep(Duration::from_millis(200)).await;
        assert_eq!(
            gate.started(),
            1,
            "a second cleanup started while the first ran"
        );
        assert!(
            !second.is_finished(),
            "the user was leased while its cleanup ran"
        );
        assert_eq!(pool.quarantined(), [a]);
        gate.open();
        let lease = tokio::time::timeout(Duration::from_secs(5), second)
            .await
            .unwrap()
            .unwrap();
        assert_eq!(lease.user().uid, a);
        assert_eq!(
            gate.started(),
            1,
            "leased after the first cleanup, without another"
        );
        assert_eq!(gate.most_at_once.load(Ordering::SeqCst), 1);
    }

    #[tokio::test(flavor = "multi_thread", worker_threads = 2)]
    async fn a_cancelled_nix_lease_keeps_the_turn_until_its_cleanup_ends() {
        let nix = 4_300_062;
        let pool = pool_of(&[4_300_061], Some(nix));
        fail_cleanup(&pool, nix, true);
        pool.clean_all().unwrap();
        fail_cleanup(&pool, nix, false);
        let gate = hold(&pool);
        let first = {
            let pool = pool.clone();
            tokio::spawn(async move { pool.lease("nix").await })
        };
        until("the Nix cleanup starts", || gate.started() == 1).await;
        first.abort();
        let _ = first.await;
        let second = {
            let pool = pool.clone();
            tokio::spawn(async move { pool.lease("nix").await.unwrap().unwrap() })
        };
        tokio::time::sleep(Duration::from_millis(300)).await;
        assert_eq!(
            gate.started(),
            1,
            "a second Nix cleanup started while the first ran"
        );
        assert!(
            !second.is_finished(),
            "a build got the turn while the cleanup ran"
        );
        gate.open();
        let lease = tokio::time::timeout(Duration::from_secs(5), second)
            .await
            .unwrap()
            .unwrap();
        assert_eq!(lease.user().uid, nix);
        assert_eq!(gate.started(), 1);
        assert_eq!(gate.most_at_once.load(Ordering::SeqCst), 1);
    }

    #[tokio::test]
    async fn a_user_that_never_comes_clean_does_not_starve_the_others() {
        let (a, b) = (4_300_071, 4_300_072);
        let pool = pool_of(&[a, b], None);
        fail_cleanup(&pool, a, true);
        fail_cleanup(&pool, b, true);
        assert!(pool.clean_all().is_err());
        // a never comes clean; b would now.
        fail_cleanup(&pool, b, false);
        let lease = tokio::time::timeout(Duration::from_secs(3), pool.lease("opentofu"))
            .await
            .expect("b is never tried while a is")
            .unwrap()
            .unwrap();
        assert_eq!(lease.user().uid, b);
    }

    fn fail_cleanup(pool: &UserPool, uid: u32, fail: bool) {
        let mut failing = pool.fail_cleanup.lock().unwrap();
        failing.retain(|&u| u != uid);
        if fail {
            failing.push(uid);
        }
    }

    #[tokio::test]
    async fn a_user_whose_startup_cleanup_fails_is_quarantined_until_one_succeeds() {
        let dir = tempfile::tempdir().unwrap();
        let gone = dir.path().join("gone");
        let (a, b) = (4_300_001, 4_300_002);
        let pool = pool_of(&[a, b], None);
        fail_cleanup(&pool, a, true);
        pool.clean_all().unwrap();
        assert_eq!(pool.quarantined(), [a]);
        let lease = pool.lease("opentofu").await.unwrap().unwrap();
        assert_eq!(lease.user().uid, b, "only the clean user");
        // While a's cleanup still fails, nothing is leased: b is taken, a is not clean.
        let waiting = {
            let pool = pool.clone();
            tokio::spawn(async move { pool.lease("ansible").await.unwrap().unwrap() })
        };
        tokio::time::sleep(Duration::from_millis(100)).await;
        pool.freed.notify_waiters();
        tokio::time::sleep(Duration::from_millis(100)).await;
        assert!(!waiting.is_finished(), "a is leased while not clean");
        assert_eq!(pool.quarantined(), [a]);
        // Once a cleanup of a succeeds, a is leased again.
        fail_cleanup(&pool, a, false);
        pool.freed.notify_waiters();
        let leased = tokio::time::timeout(Duration::from_secs(5), waiting)
            .await
            .unwrap()
            .unwrap();
        assert_eq!(leased.user().uid, a);
        assert!(pool.quarantined().is_empty());
        lease.release(&gone).await;
        leased.release(&gone).await;
        assert!(pool.quarantined().is_empty());
    }

    #[test]
    fn the_agent_does_not_start_when_no_user_can_be_cleaned_up() {
        let pool = pool_of(&[4_300_011, 4_300_012], None);
        fail_cleanup(&pool, 4_300_011, true);
        fail_cleanup(&pool, 4_300_012, true);
        let err = pool.clean_all().unwrap_err().to_string();
        assert!(err.contains("none of the 2 tool users"), "{err}");
    }

    #[tokio::test]
    async fn a_user_whose_release_fails_is_not_leased_again() {
        let dir = tempfile::tempdir().unwrap();
        let gone = dir.path().join("gone");
        let only = 4_300_021;
        let pool = pool_of(&[only], None);
        let lease = pool.lease("opentofu").await.unwrap().unwrap();
        fail_cleanup(&pool, only, true);
        lease.release(&gone).await;
        assert_eq!(pool.quarantined(), [only]);
        let next = tokio::time::timeout(Duration::from_millis(300), pool.lease("opentofu")).await;
        assert!(next.is_err(), "the quarantined user was leased again");
    }

    #[tokio::test]
    async fn the_nix_user_runs_no_build_while_its_cleanup_fails() {
        let dir = tempfile::tempdir().unwrap();
        let gone = dir.path().join("gone");
        let nix = 4_300_032;
        let pool = pool_of(&[4_300_031], Some(nix));
        // At startup.
        fail_cleanup(&pool, nix, true);
        pool.clean_all().unwrap();
        assert_eq!(pool.quarantined(), [nix]);
        let err = pool.lease("nix").await.unwrap_err().to_string();
        assert!(err.contains("not clean"), "{err}");
        assert!(
            pool.nix_ready().await.is_err(),
            "the probe does not offer nix"
        );
        fail_cleanup(&pool, nix, false);
        pool.nix_ready().await.unwrap();
        let lease = pool.lease("nix").await.unwrap().unwrap();
        // After a build whose cleanup fails, the turn comes back but the user is not clean.
        fail_cleanup(&pool, nix, true);
        lease.release(&gone).await;
        assert_eq!(pool.quarantined(), [nix]);
        let err = pool.lease("nix").await.unwrap_err().to_string();
        assert!(err.contains("not clean"), "{err}");
        fail_cleanup(&pool, nix, false);
        assert!(pool.lease("nix").await.unwrap().is_some());
    }

    #[tokio::test]
    async fn a_sweep_that_runs_out_quarantines_its_user() {
        // Another user's entries, here this test's own, fill the budget of the victim's sweep.
        let dir = tempfile::tempdir().unwrap();
        let shared = dir.path().join("shm");
        fs::create_dir_all(shared.join("hog")).unwrap();
        for n in 0..50 {
            fs::write(shared.join("hog").join(n.to_string()), "x").unwrap();
        }
        let err = sweep(std::slice::from_ref(&shared), &[], 4_300_041, 20).unwrap_err();
        assert!(err.contains("more than 20 entries"), "{err}");
        let pool = Arc::new(
            UserPool::new(vec![user(4_300_041)], None, user(65534), vec![shared]).unwrap(),
        );
        pool.set_sweep_limit(20);
        let lease = pool.lease("opentofu").await.unwrap().unwrap();
        lease.release(&dir.path().join("gone")).await;
        assert_eq!(pool.quarantined(), [4_300_041]);
        // With room to look at everything, its cleanup succeeds and it is leased again.
        pool.set_sweep_limit(1000);
        let again = tokio::time::timeout(Duration::from_secs(5), pool.lease("opentofu"))
            .await
            .unwrap()
            .unwrap()
            .unwrap();
        assert_eq!(again.user().uid, 4_300_041);
    }

    #[test]
    fn a_thread_counts_whatever_its_leader_is() {
        let status = |state: &str| {
            format!("Name:\tdetach\nState:\t{state}\nUid:\t10100\t10100\t10100\t10100\n")
        };
        assert!(thread_is_live_as(&status("S (sleeping)"), 10100));
        assert!(!thread_is_live_as(&status("Z (zombie)"), 10100));
        assert!(!thread_is_live_as(&status("X (dead)"), 10100));
        assert!(!thread_is_live_as(&status("S (sleeping)"), 10101));
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
