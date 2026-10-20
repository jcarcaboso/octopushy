//! A fake Proxmox VE API for tests and end-to-end runs.
//!
//! It serves the endpoints Octopushy's Proxmox client uses from an in-memory
//! [`Fixture`], accepts exactly one API token, and can serve HTTPS with a
//! certificate issued by a freshly generated CA (like Proxmox's own
//! `pve-root-ca`), so clients can test custom-CA verification.
//!
//! Admin routes (no token needed) change the fixture at runtime:
//! `GET /mock/vms`, `POST /mock/vms` (add or replace a VM), and
//! `DELETE /mock/vms/{vmid}`.

pub mod api;
pub mod fixture;
mod tls;

use std::net::SocketAddr;
use std::sync::{Arc, Mutex};

use tokio::net::TcpListener;
use tokio::task::JoinHandle;

pub use fixture::{Fixture, FixtureCluster, FixtureNode, FixtureVm};

/// How to serve the mock.
#[derive(Debug, Clone)]
pub struct Options {
    pub listen: SocketAddr,
    /// Serve HTTPS with a generated CA and server certificate.
    pub tls: bool,
}

/// A running mock. It stops when dropped.
pub struct RunningMock {
    addr: SocketAddr,
    tls: bool,
    ca_pem: Option<String>,
    state: api::SharedFixture,
    task: Option<JoinHandle<()>>,
}

impl RunningMock {
    /// The address it listens on.
    pub fn addr(&self) -> SocketAddr {
        self.addr
    }

    /// The endpoint URL, such as `https://127.0.0.1:18006/`.
    pub fn endpoint(&self) -> String {
        let scheme = if self.tls { "https" } else { "http" };
        format!("{scheme}://{}/", self.addr)
    }

    /// The PEM of the CA that issued the server certificate (HTTPS only).
    pub fn ca_pem(&self) -> Option<&str> {
        self.ca_pem.as_deref()
    }

    /// The accepted API token.
    pub fn token(&self) -> String {
        self.state.lock().unwrap().token.clone()
    }

    /// A VM's current state.
    pub fn vm(&self, vmid: u32) -> Option<FixtureVm> {
        self.state
            .lock()
            .unwrap()
            .vms
            .iter()
            .find(|v| v.vmid == vmid)
            .cloned()
    }

    /// Adds or replaces a VM.
    pub fn upsert_vm(&self, vm: FixtureVm) {
        let mut f = self.state.lock().unwrap();
        f.vms.retain(|v| v.vmid != vm.vmid);
        f.vms.push(vm);
        f.vms.sort_by_key(|v| v.vmid);
    }

    /// Removes a VM; returns whether it existed.
    pub fn remove_vm(&self, vmid: u32) -> bool {
        let mut f = self.state.lock().unwrap();
        let before = f.vms.len();
        f.vms.retain(|v| v.vmid != vmid);
        f.vms.len() != before
    }

    /// Changes the fixture in place.
    pub fn update(&self, change: impl FnOnce(&mut Fixture)) {
        change(&mut self.state.lock().unwrap());
    }

    /// Waits until the server task ends (it normally runs forever).
    pub async fn wait(mut self) {
        if let Some(task) = self.task.take() {
            let _ = task.await;
        }
    }
}

impl Drop for RunningMock {
    fn drop(&mut self) {
        if let Some(task) = &self.task {
            task.abort();
        }
    }
}

/// Starts the mock on `options.listen` (port 0 picks a free port).
pub async fn start(fixture: Fixture, options: Options) -> anyhow::Result<RunningMock> {
    fixture.validate()?;
    let listener = TcpListener::bind(options.listen).await?;
    let addr = listener.local_addr()?;
    let state = Arc::new(Mutex::new(fixture));
    let app = api::router(state.clone());

    let (task, ca_pem) = if options.tls {
        let identity = tls::generate(addr.ip())?;
        let ca_pem = identity.ca_pem.clone();
        let task = tokio::spawn(async move {
            if let Err(e) = tls::serve(listener, identity, app).await {
                tracing::error!("mock-proxmox stopped: {e:#}");
            }
        });
        (task, Some(ca_pem))
    } else {
        let task = tokio::spawn(async move {
            if let Err(e) = axum::serve(listener, app).await {
                tracing::error!("mock-proxmox stopped: {e:#}");
            }
        });
        (task, None)
    };

    Ok(RunningMock {
        addr,
        tls: options.tls,
        ca_pem,
        state,
        task: Some(task),
    })
}
