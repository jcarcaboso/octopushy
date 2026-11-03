# Guest host keys without a person

**Researched November 3, 2026. The owner chose option A the same day, and it is built:** see
[host key trust](../guest-configuration.md#host-key-trust), which is the design now. This page
keeps what was checked and the options that were weighed. Scope, as the owner set it: only guests Octopushy creates from now
on. `pve01-ai` will be recreated, so existing machines need no path. `access.hostKey` stays as the
override for machines Octopushy did not create.

The request: Octopushy generates each guest's SSH host key, keeps it next to the machine as it keeps
the guest's access key (`system/guest-access/<resource id>`), delivers it at creation and pins it, so
`access.hostKey` becomes optional.

## What was checked

Proxmox's source was read at `master` on November 3, 2026: `pve-storage` `4250dfd533c7`,
`qemu-server` `fc803fcbe28f` and `pve-container` `761aea86f1ef`. The hosts' own versions were not
checked, so an older release on a host could differ. bpg/proxmox was read at `v0.117.0`, the
version `proxmox-vm` pins.

| Question | Answer | Evidence |
|---|---|---|
| Can the Proxmox API upload a `snippets` file? | **No.** Upload and download-url accept `iso`, `vztmpl` and `import` only | `pve-storage` `src/PVE/API2/Storage/Status.pm`, both `content` enums |
| Can `cicustom` read a file uploaded as `import`? | **No.** It refuses any volume that is not of type `snippets` | `qemu-server` `Cloudinit.pm`, `read_cloudinit_snippets_file` |
| How does bpg/proxmox 0.117.0 upload a snippet? | **Over SSH to the node** (`stream` or `sftp`). Only `iso`, `vztmpl` and `import` go through the API | `proxmoxtf/resource/file.go`, the `switch` on content type; the resource's documentation says the same |
| Does Proxmox's generated user-data carry host keys? | No. It writes hostname, user, password, `ssh_authorized_keys`, `chpasswd` and `package_upgrade` | `Cloudinit.pm`, `cloudinit_userdata` |
| Is `initialization.user_data_file_id` updatable in place? | No, it is `ForceNew`, as are vendor, meta and network data. This does not matter for a new guest | `proxmoxtf/resource/vm/vm.go` |
| Can the guest agent read the host key on Fedora 44? | **No.** With SELinux enforcing, `virt_qemu_ga_t` is refused `sshd_key_t` (the key), `cloud_log_t` (`cloud-init-output.log`, which holds the fingerprint) and `journalctl`. Every `virt_qemu_ga_*` boolean is off | A real Fedora Cloud Base 44 VM, below |
| Does a cloud-config with `ssh_keys` install a key given from outside on Fedora 44? | **Yes.** `ssh_deletekeys: true`, `ssh_genkeytypes: []` and `ssh_keys.ed25519_*` left exactly that key, labelled `sshd_key_t`, and `sshd` served it. No RSA or ECDSA key was made | The same VM |
| Where does that private key end up on the guest? | Also in `/var/lib/cloud/instance/user-data.txt`, root, mode 0600 | The same VM |
| Who can read a VM's user-data through the API? | Anyone with `VM.Audit` on the VM, through `GET …/qemu/{vmid}/cloudinit/dump?type=user` | `qemu-server` `API2/Qemu.pm` |
| How does a container get its host keys? | Proxmox generates them on the host at `pct create`, writes them into the root file system, and prints `done: SHA256:… root@<hostname>` for each type in the create task's log | `pve-container` `src/PVE/LXC/Setup.pm`, `rewrite_ssh_host_keys` |

**The VM test.** The image in `templates/fedora-44` (checksum matched) was booted with QEMU and KVM in a
disposable container, with the q35, OVMF, virtio-scsi and guest agent layout of the `proxmox-vm` module
and a NoCloud seed in the form Proxmox writes. The guest agent was driven over its own socket with
`guest-exec`, as Proxmox does. The VM was then started again on the same disk with a new user-data and
an `instance-id` computed as Proxmox computes it, the SHA-1 of the user-data. Not tested: Proxmox itself,
a snippet stored on a host, bpg's upload, and a real `tofu apply`.

## Options for VMs

### A. Octopushy generates the key and a cloud-config snippet delivers it

This is the shape that was asked for. Octopushy generates an Ed25519 key per guest, keeps it in
`system/guest-host-key/<resource id>`, and gives it to the provision run as a sensitive variable. The
module writes a user-data snippet holding everything Proxmox would have generated, plus `ssh_deletekeys`,
`ssh_genkeytypes: []` and `ssh_keys`, and points `user_data_file_id` at it. Configure runs pin the public
half, and `access.hostKey` overrides it.

**It needs two changes on each Proxmox host:**

1. `snippets` added to the content of a directory datastore, such as `local`.
2. A way to write the file there. The API cannot, so bpg needs SSH to the node. The narrowest form is a
   dedicated account limited to SFTP, chrooted to the snippets directory, with bpg's `upload_mode = "sftp"`,
   which does not use `sudo`. Octopushy would hold that credential in the store and release it only to
   provision applies, as it does the Proxmox token.

**What it costs in exposure.** The private host key goes where the access key's private half never goes:

- into OpenTofu: the saved plan and Octopushy's built-in state, encrypted at rest, but in the clear in the
  agent's workspace while a plan or apply runs;
- onto the host: the snippet file, and the cloud-init drive Proxmox builds from it;
- to anyone with `VM.Audit` on the VM, through the cloud-init dump. With the tokens as they are today, that
  includes every token that can see the VM;
- onto the guest, in cloud-init's copy of its user-data, root-only.

Someone holding it can answer as the guest. Everyone on that list except the read-only API users already
controls the guest or its hypervisor, which is the same trust anchor the manual console route has.

**Rebuild and retire.** A rebuilt VM gets the same key, so its configure runs carry on without a new pin.
Today a rebuild fails closed until a person sets the new key. With A it does not, because the key is
Octopushy's and nobody else has it. Retiring removes the secret, and the stack's destroy removes the
snippet. Rotating the host key would be a staged run like the access key's, and is not designed here.

### B. The guest publishes its own key where the guest agent may read it

No private key leaves the guest. At first boot the guest copies its public key to a file labelled
`etc_t`, such as `/etc/octopushy/ssh-host-ed25519.pub`. An agent step reads it through the Proxmox API
with `guest-exec` or `file-read` and records the pin. That read worked on a test VM on November 2,
with the copy made by hand. Proxmox authenticates the channel, as in today's manual route.

On stock Fedora 44 something has to make that copy, and the two ways are:

- a cloud-config `runcmd`, which is a snippet again, so the same host changes as A, minus the exposure;
- **a template image that does it itself**, for example a systemd unit ordered after `sshd-keygen` and
  cloud-init. That needs no change on the Proxmox host, but `homelab-config` would need an image build
  and somewhere to host the image, and the template would point at it.

A rebuilt VM has a new key, read again automatically. The token needs guest agent privileges, which
pve01's has.

### C. Capture the keys cloud-init prints on the serial console

The test VM printed its full public keys to `ttyS0` at the end of its first boot, and the module gives
every VM a serial port. Capturing them means attaching through `termproxy` before the VM boots, but the
provider starts it during apply. That is a race, so this option is not recommended.

## Containers

**No change on the host is needed, but this is a later step,** because `type: container` (PRs 21 and 23)
is not merged. Proxmox makes the keys itself and logs each fingerprint in the create task. A step after
apply would read that log through the API, connect once, accept only a key with that ed25519
fingerprint, and pin it. A fingerprint authenticates the key it hashes, so this is not trust-on-first-use.
Reading the log needs the token that created the container, or `Sys.Audit`. The log format was read from
the source and not checked on a real Proxmox. Octopushy could afterwards replace that key with one of its
own over the pinned connection, which would make containers match A.

## Recommendation

**A, with an SFTP-only account per host**, if the owner accepts snippets on one datastore and that
account. It is the only route where Octopushy owns the key, and a rebuild keeps its identity. The price
is the private key in OpenTofu state and in the VM's cloud-init data, which `VM.Audit` can read, so
narrowing the tokens matters more afterwards. Containers take the task-log route once containers land.

If no SSH to hypervisors is acceptable, **B with a custom template image** works with no host change,
at the cost of an image pipeline in `homelab-config`. A person stays out of it either way.

## Decided

On November 3, 2026 the owner chose A ("I don't want to build custom images, makes more sense to
make a one time setup"), with these settings:

1. Snippets on each installation's `local` directory datastore by default, settable per installation.
2. One SFTP-only account per host, `octopushy-snippets`, chrooted to the snippets directory, with
   bpg's `upload_mode = "sftp"` and no `sudo`, and a key Octopushy generates per installation.
3. The private host key may sit in OpenTofu's plan and state and in the VM's cloud-init data,
   which `VM.Audit` can read. Narrowing the Proxmox tokens matters more as a result.
4. No migration for machines that already exist. `pve01-ai` will be recreated.

Building it found two more things. bpg trusts any node it has not seen, so Octopushy pins the
host's own SSH key in the attempt's `known_hosts`. sshd will not load a private key without a final
newline, so the module adds one.
