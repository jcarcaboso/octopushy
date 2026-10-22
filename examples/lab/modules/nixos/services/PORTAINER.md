# Docker service-node / Portainer test

Two optional modules, disabled by default:

- `../service-node.nix`: rootful Docker, Compose CLI, curl, jq, Git, OpenSSL,
  bounded Docker logs, no automatic prune, no TCP Docker API.
- `portainer.nix`: digest-pinned Portainer CE 2.39.8 LTS in Docker, managed by
  the native `docker-portainer.service` unit. This is not a native Portainer
  binary or a Podman deployment.

Compose them with the existing `proxmox-guest.nix` baseline:

```nix
{
  imports = [ ./proxmox-guest.nix ./services/portainer.nix ];
  octopushy.serviceNode.enable = true;
  octopushy.services.portainer = {
    enable = true;
    expose = false; # Only opt in after authenticated bootstrap verification.
    lanInterface = "ens18";
    allowedNetworks = [ "10.1.0.0/24" ];
  };
}
```

Supply the actual installed machine's hardware, stable MAC-matched networking,
SSH keys, and stateVersion separately. The image can include the service-node
base with **Portainer disabled**, keeping the template reusable and secret-free.

## Credentials, network, and ownership

Create a root-owned `0600` runtime password file at
`/var/lib/octopushy-secrets/portainer-admin-password` in a `0700` directory.
Never put its contents in Git, a Nix expression/path, the store, command-line
arguments, or logs. The unit loads it through systemd credentials and mounts
the runtime copy read-only. Missing/empty credentials prevent startup.
Portainer uses it to create `admin` only for an uninitialized database; it
does **not** change the password of an existing administrator.

Portainer connects to the local Docker socket. Its administrator therefore has
**root-equivalent control of this VM**, not just a read-only container dashboard.
Use a dedicated VM and do not connect it to the Proxmox host's Docker/socket.

The container uses host networking specifically to avoid Docker-published ports
bypassing the host input firewall. The role admits only TCP 9443, through the
declared interface and IPv4 source ranges, after `expose = true`. An earlier
nftables guard denies other sources (including remote IPv6), even if ordinary
host policy trusts an interface. HTTP 9000 and Edge tunnel 8000 are not exposed.
Unrelated host ports such as SSH remain under their existing policy.
This policy covers Portainer's UI only: other Docker workloads that publish
bridge-network ports need their own ingress policy, since those packets follow
Docker's forwarding rules rather than this host-input rule.

Portainer's generated HTTPS certificate is self-signed. For the test, verify
its fingerprint through pinned SSH; expect a browser certificate warning.
Production needs an explicitly managed certificate and rotation procedure.

The persistent named volume is `octopushy-portainer-data`. Ordinary disable/
removal stops/removes the container and removes this role's firewall rules;
it does **not** remove the volume. Deleting the VM destroys that data, so
backup/restore is a separate responsibility.

NixOS owns the Portainer container specification. Do not redeploy or edit
Portainer itself through its UI. Workloads created through Portainer are
Portainer/manual-owned, **not reconciled Octopushy services**.

## Execution boundary

Build/copy/activate a reviewed NixOS closure as in the parent workflow.
Configuration activation is a **whole-system NixOS generation**, serialized
with the guest's native lock. Routine unit operations are narrower:

```sh
sudo systemctl status docker-portainer
sudo journalctl -u docker-portainer
sudo systemctl restart docker-portainer
```

Generation rollback does not roll back Portainer's database or Docker volumes.
The live test exercises the controller's VM plan/approve/apply/verify path, then
native NixOS service operations. There is no first-class Portainer/service
executor, service status model, or service retry button in Octopushy yet.

See `tests/portainer/README.md` from the repository root.

## Version evidence

- [Portainer CE Linux Docker instructions](https://docs.portainer.io/start/install-ce/server/docker/linux)
- [2.39.8 LTS release](https://github.com/portainer/portainer/releases/tag/2.39.8)
- [Immutable-version CLI options](https://github.com/portainer/portainer/blob/2.39.8/api/cli/cli.go)
- [Administrator bootstrap behavior](https://github.com/portainer/portainer/blob/2.39.8/api/cmd/portainer/main.go)

The selected digest was resolved from Docker Hub for that release, not a
floating `latest` tag. A pinned release is not an automatic security-update
policy: review new releases before changing it.
