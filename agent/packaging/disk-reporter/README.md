# Guest disk reporter

A separate, one-shot Rust binary for **Linux VMs with systemd**. It calls
`statvfs("/")`, sends `{"usedBytes": …}` over HTTPS, and exits. There is no HTTP
listener, directory scan, CPU/memory sampling, persistent connection, or local
queue. The default timer runs approximately hourly, with up to one minute of
jitter, and first runs two minutes after boot (or shortly after enabling it if
that time has passed).

This is not the Octopushy execution agent and cannot execute assignments.

## What the number means

- Used bytes are `(f_blocks - f_bfree) * f_frsize` for the **root filesystem**.
  Reserved-but-free blocks are not counted as used.
- The backend takes configured system-disk capacity from
  `hardware.disks[0].size` in the current guest definition, in **GiB**, matching
  the example Proxmox recipe. Missing/invalid capacity is unknown, never zero.
- The guest page displays used/configured capacity and estimated remaining
  configured capacity. These are **not** filesystem fullness or space available
  to applications: partitions, reservations, unexpanded filesystems, and pending
  disk-size changes can make them differ. `/boot`, data disks, and separate
  filesystems are not included. Do not use this estimate for disk-full alerts.
- A report becomes stale after two configured intervals plus two minutes.
  Stopping/rebooting a VM does not turn an old reading into zero usage.
- Only the latest reading is stored per guest, using the server's receipt time.
  There is no historical metrics database.

## Build

From the repository root:

```sh
cd agent
cargo build --release -p octopushy-disk-reporter
./target/release/octopushy-disk-reporter measure
```

Build for the guest's architecture and a compatible libc. Copy the release binary;
the VM does not need Rust, a container runtime, or the execution agent.
Releases are built by hand, like the rest of the project.

## Automatic installation after provisioning

Deploy the backend with the included `GuestDiskUsage` database migration first
(the normal `octopushy-server migrate` deployment step), then the updated frontend
and execution agents. Publish the release binary at a direct HTTPS URL and record
its digest with `sha256sum agent/target/release/octopushy-disk-reporter`.

Set the backend configuration once (environment variables use `__` instead of `:`):

```json
{
  "Octopushy": {
    "DiskReporter": {
      "Enabled": true,
      "PublicUrl": "https://octopushy.example",
      "BinaryUrl": "https://releases.example/octopushy-disk-reporter-v1",
      "BinarySha256": "<64 hex characters from sha256sum>",
      "IntervalSeconds": 3600
    }
  }
}
```

Compose exposes these as `OCTOPUSHY_DISK_REPORTER_*`; see
[`deploy/README.md`](../../../deploy/README.md#automatic-guest-disk-reporting).
It defaults to disabled until these deployment-specific values are supplied.
There is one pinned binary for the deployment: it must match every opted-in
guest's architecture and libc. No package registry or automatic cross-compilation
is assumed.

Once enabled, the workflow is:

```text
plan → approval → apply → verify → install-disk-reporter
```

The final step uses Proxmox/QEMU guest execution, without SSH keys or an inbound
VM listener. It checks identity, downloads over HTTPS, verifies SHA-256 **before
executing the binary**, installs the timer, and sends the first report immediately.
Retire runs never install. Verification is required for setup, even with a recipe
that normally skips it. **Plan now** installs or repairs existing VMs even when
OpenTofu has no changes; manual approval still gates this in-VM mutation.
Configuration is frozen per run. Changing the server's release URL/checksum or
defaults requires a new run; it does not change an already-approved run.

Guest prerequisites:

- Linux with systemd 247+, a running QEMU guest agent, and guest-exec enabled.
- A mutable, FHS-style filesystem with writable `/etc` and `/usr/local/bin`.
  Opt out for NixOS/immutable guests; this installer is not a declarative NixOS module.
- Root QEMU guest execution; `/usr/bin/timeout`, `/bin/sh`, `curl`, `systemctl`,
  `flock`, and standard coreutils (`base64`, `sha256sum`, `install`, `mktemp`).
- Access to both HTTPS endpoints, with certificates valid on the VM. Optional
  `CaCertificatePem` contains a public CA bundle for **both** download and reporting;
  it replaces system trust for those requests. Never supply a private key.
- The Proxmox token must have `VM.Audit` and `VM.GuestAgent.Unrestricted` on the VM
  (`VM.Monitor` on older Proxmox versions). Restrict this powerful privilege to
  the managed VMs. Execution agents must advertise the `guest` executor.

The per-VM token is generated only when dispatching installation, kept encrypted
in the internal variable store, and reused on repair. Only its hash is used to
authorize reports. Tokens stay out of Git, run configuration, OpenTofu state, and
command arguments. The script travels over the authenticated Proxmox API as QEMU
stdin, and guest stdout/stderr is not copied into attempt logs.

### Per-guest overrides

In `guest.yaml`:

```yaml
setup:
  diskReporter:
    intervalSeconds: 1800
    # enabled: false  # opt out of future automatic installation
```

These settings trigger a new provision run but are never sent to OpenTofu.
Omitting them uses server defaults; a guest cannot opt in while the server-wide
feature is disabled. Opting out **does not uninstall** an already-installed timer.

### Installation failures

Download, checksum, prerequisites, or first-report failures fail the visible setup
step, not silently succeed. If the first push fails, the installed timer may still
send later; fix connectivity and use **Plan now** to complete setup.
The installation is locked on the guest and bounded to 240 seconds plus a
5-second forced-kill grace period. A lost execution response, status, or execution
agent is **uncertain**, never automatically retried. Wait at least 245 seconds,
inspect the VM and `journalctl -u octopushy-disk-reporter.service`, then use the
run's recovery/close flow before planning again.

## Manual installation

This remains available when automatic installation is disabled or to administer
a guest directly.

1. Open the guest page, **System disk → Configure reporter**. Enter the HTTPS
   web/API address reachable from the VM (not the execution-agent port) and the
   reporting interval, then create a token.
2. Save the displayed `config.json` and `token` files. The token is shown only
   once, belongs to this guest, and can only submit disk usage. Keep it out of Git,
   logs, command arguments, shared images, and OpenTofu variables/state.
3. Copy those files, `octopushy-disk-reporter`, `install.sh`, and both unit files
   to the VM using your existing authenticated administration connection.
4. On the VM:

   ```sh
   chmod 600 token
   sudo sh install.sh ./octopushy-disk-reporter ./config.json ./token
   rm token
   sudo systemctl start octopushy-disk-reporter.service
   sudo systemctl list-timers octopushy-disk-reporter.timer
   sudo journalctl -u octopushy-disk-reporter.service
   ```

The installer enables the timer by default. It installs the binary under
`/usr/local/bin`, configuration under `/etc/octopushy-disk-reporter`, and system
units plus an interval drop-in under `/etc/systemd/system`. The token is
root-readable only; systemd passes it to an unprivileged dynamic user using
`LoadCredential`. No root permissions are needed to measure the filesystem.
Use systemd 247 or newer for these features.

A shared image may contain the binary and unit files, but never a guest-specific
token or configuration. Configure each clone separately.

### Configuration and changing the interval

```json
{
  "endpoint": "https://octopushy.example/api/guests/GUEST-RESOURCE-UUID/disk-usage",
  "intervalSeconds": 3600
}
```

Intervals are 60–604800 seconds (one minute to seven days). For automatic installs,
change `setup.diskReporter.intervalSeconds` and let the new provision run apply it.
For manual installs, generate a replacement configuration/token in the guest page
and rerun the installer: the server's freshness expectation and timer then agree. Creating
a replacement token immediately invalidates the previous one. Merely editing
the JSON file does not reconfigure an already loaded timer.

For a private CA, add `"caCertificateFile": "/etc/octopushy-disk-reporter/ca.pem"`
and install that PEM certificate as mode 0644. Certificate/hostname verification
is mandatory; HTTP URLs, URL credentials, and redirects are rejected. The API
also requires HTTPS (use the existing trusted-forwarded-header configuration
behind a TLS reverse proxy, with the backend port kept private).

A request has a ten-second timeout. Failures exit nonzero and are recorded in
the journal; the next timer run sends a fresh value. There are no immediate
retries or accumulated reports. The unit has a twenty-second start timeout,
low scheduling priority, a 5% CPU quota, and a 64 MiB memory ceiling; those are
**limits**, not measured normal consumption. Nothing remains resident between
runs.

To disable reporting persistently, first opt out with
`setup.diskReporter.enabled: false`, revoke the token on the guest page, and run:

```sh
sudo systemctl disable --now octopushy-disk-reporter.timer
sudo systemctl stop octopushy-disk-reporter.service
sudo rm /etc/octopushy-disk-reporter/token
```

The last reading remains visible, marked disabled. Missing/retired guests cannot
submit reports. Revoking the reporter grants no execution-agent privileges and
does not affect infrastructure provisioning. Without the opt-out, a later
automatic setup creates a new credential and enables reporting again.

## API and checks

- Signed-in operator: `POST /api/guests/{id}/disk-usage/token` with
  `{"intervalSeconds":3600}` creates/replaces a credential; `DELETE` on the same
  path revokes it.
- Reporter: `POST /api/guests/{id}/disk-usage`, `Authorization: Bearer <token>`,
  `Content-Type: application/json`, body `{"usedBytes":12345}`. Success is 204.
  Bodies are limited to 1024 bytes, including chunked requests; the token cannot
  read inventory, report for another guest, or create more credentials.
- Signed-in operator: `GET /api/guests/{id}` includes `diskUsage`, without secrets.

```sh
cd agent
cargo test -p octopushy-disk-reporter
cargo build -p octopushy-disk-reporter
sh packaging/disk-reporter/test-install.sh
```

`DESTDIR=/some/staging/root sh install.sh BINARY CONFIG TOKEN` stages files for
packaging tests without changing the host's systemd state.
