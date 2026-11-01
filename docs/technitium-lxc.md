# Technitium DNS in a Proxmox container

The `technitium-lxc` recipe runs [Technitium DNS Server](https://technitium.com/dns/)
natively, without Docker, in an unprivileged Debian 13 LXC container. Each server is one
guest, on the Proxmox host it should keep working through. This page covers one running
server. It is not clustered yet.

## Declare a server

```yaml
# labs/home/proxmox/korriban/guests/krb-dns-01/guest.yaml
schemaVersion: 1
type: container
host: korriban
vmid: 320
template: debian-13-lxc
hardware:
  cores: 2
  memory: 2048
  swap: 512
  disks:
    - { size: 8 }
  mountPoints:
    - { path: /etc/dns, size: 4 }
network:
  bridge: vmbr0
  mode: static
  address: 10.1.0.32/24
  gateway: 10.1.0.1
  dns: [10.1.0.1]
provision:
  recipe: technitium-lxc
  inputs:
    startup: { order: 1 }
    # firewall: { enabled: true, adminSources: [10.1.0.0/24] }
  secrets:
    admin_password: { store: dns/technitium/admin-password }
```

- **The address must be static,** and `network.dns` must name other resolvers. The container
  keeps resolving through them, never through itself, so it can boot and install with its
  own DNS server down.
- **Set the password first:** add `dns/technitium/admin-password` as a secret under
  **Variables**, at least 12 characters on one line. A run is blocked until it exists.
- **Agents need a route to the container's port 22.** OpenTofu installs over SSH.

Sign in at `https://<address>:53443` as `admin`. The certificate is self-signed.

## What gets installed

Proxmox has no user-data or exec for containers, so the root generates an SSH key pair and
creates the container with its public key. OpenTofu then connects as root and runs
[`install.sh`](../examples/lab/modules/opentofu/technitium-lxc/install.sh), which:

- installs the pinned ASP.NET Core runtime and Technitium, each checked against its digest,
  under `/opt/technitium`, next to any older version until the new one passes its checks;
- writes `dns.service`, upstream's unit with the runtime's path, enabled at boot. It depends
  on neither Octopushy nor name resolution;
- keeps Technitium's plain HTTP port 5380 on loopback with an nftables rule of its own, and
  serves the API over HTTPS on 53443;
- checks the running version, that the password works and `admin`/`admin` does not, that
  ports 53 and 53443 answer, and that `/etc/resolv.conf` still names `network.dns`. If any
  check fails, the apply fails.

It does not use upstream's `install.sh`, which always fetches the latest release and points
`/etc/resolv.conf` at the server itself. It runs as the `dns-server` user, as upstream does.

## Versions

The recipe's defaults are Technitium 15.6.2 and ASP.NET Core 10.0.14. To pin others, set all
four values in `guest.yaml`:

```yaml
provision:
  inputs:
    technitium:
      version: 15.6.2             # at least 15.6.2, which fixes cluster configuration sync
      sha256: 51f2d4ebaf156a85035e820afde5e1d428f1920a37d7f0c65203fb1766917e08
      aspnetcoreVersion: 10.0.14
      aspnetcoreSha512: dd024fd2616926a555b75cbf7a836f6dce998199f0596d9d75676dca2b9d59c8ee8673c6ac23473c0f1a4bdd59eb5cd02fe9ab097eeaf2dd8c8bd117c85e7031
```

`sha256` is the digest of
`https://download.technitium.com/dns/archive/<version>/DnsServerPortable.tar.gz`. Commit a
change and the next plan shows `terraform_data.install` being replaced: that resource is the
install, and its apply stops the service for the switch. It follows the guest's approval
setting, and `/etc/dns` stays as it is. The container's Debian packages update themselves
through `unattended-upgrades`.

## The admin password

Technitium takes the password on its first start only, when `/etc/dns/auth.config` does not
exist. If it cannot read it, it silently uses `admin`/`admin`, which is why the install logs
in to check.

The value reaches the apply through `TF_VAR_admin_password`, and OpenTofu copies it to a file
under `/run` in the container. The plan never receives it, and the variable is ephemeral, so
it is in neither the saved plan nor the state. In the container it reaches curl as a file,
not as an argument, and it is deleted however the install ends. `auth.config` holds only a
hash of it.

The store is not kept in sync afterwards. **Changing the value does not change the password.**
Change it in Technitium first, then in the store: a later install checks the value in the store
and fails if it does not work.

The generated SSH private key is in the guest's OpenTofu state, and therefore in saved plans.
Octopushy keeps both encrypted, but an external state backend keeps its own copy. OpenTofu does
not check the container's SSH host key, because nothing reports it before the first
connection: keep agents and the container on a network you trust.

## `/etc/dns`

`/etc/dns` holds the zones, the settings, and the server's identity, so the install refuses to
run unless it is a mount point of its own. A vzdump backup includes it unless the mount point
sets `backup: false`.

Proxmox deletes a volume together with the container that owns it, so a volume with a `size`
lasts as long as the container, and neither retiring the guest nor replacing the container
keeps it. To keep the data through those too, create the volume under another VMID that no
guest uses, and format it, on the Proxmox host. Then name it:

```yaml
hardware:
  mountPoints:
    - { path: /etc/dns, volume: "local-lvm:vm-9320-disk-0" }
```

Proxmox skips a volume another VMID owns when it deletes a container. The next install finds
`auth.config` and keeps it, so the password in the store must still be the one in that file.

## Firewall

`firewall.enabled` turns on the Proxmox firewall for the container, with incoming traffic
dropped except UDP and TCP 53 from `dnsSources`, and TCP 53443 and 22 from `adminSources`.
Both default to everyone. `adminSources` must include the agents, or the next install cannot
connect. Turning this on for an existing server restarts its container.

**These rules are inert unless the firewall is also enabled for the datacenter** (Datacenter,
Firewall, Options), which Octopushy does not do. Port 5380 does not rely on them.

## Retiring

Retiring works as for a VM: an approved run lifts the protection, destroys the container, and
verifies that it is gone. It does not need the password.
