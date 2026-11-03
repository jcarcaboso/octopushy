# Run lab repository playbooks as root on guests, behind a pinned host key

Building on [0002](0002-installation-trust.md). A recipe with `operation: configure` runs
tool-native code from the lab repository as root on a guest, through an agent. That is a
new reach for a commit, though not a new kind of trust: a commit could already run code on
the agent at plan time, and with an installation's token at apply.

Do not add a per-commit gate. 0002 rejected that as too costly for one administrator, and a
playbook is reviewed where an OpenTofu module is: before it merges. Keep installation trust
as the condition for working on any guest of an installation. Add one gate, in the terms
0002 chose: credentials go only to endpoints an operator trusted, and a guest's endpoint is
identified by its SSH host key. So `access.hostKey` is a store reference that a commit can
name but not fill, a run refuses to start without it, a mismatch fails closed, and Octopushy
never scans for a key.

An installation's Proxmox tokens and `system/` keys cannot be delivered to a playbook, and
neither can any key that has already gone to Proxmox or OpenTofu work, whatever today's
definitions say. `preview` and `prepare` run before approval and get no secrets.

The pin is against a network impostor and a mistaken setting. It is not against hostile
repository code, which runs locally with the SSH agent's socket and the run's secrets, and
can connect anywhere with them or send them anywhere. Code on a `local` runtime agent
can also read that agent's identity. All of this follows from accepting tool-native code,
as 0002 did: review before merging is the defence, and the last point is the reason to run
sensitive work on an agent of its own. Scoping a secret to reviewed code is separate work.

**Amended November 3, 2026.** For a guest Octopushy creates, the pin can be Octopushy's own key,
generated before the machine exists and delivered in its cloud-init snippet, so no person sets it.
The decision above is unchanged: a commit still cannot put a new key behind an address, because
the key is the guest's record in Octopushy, and the snippet upload is itself pinned to the Proxmox
host's own SSH key. [Host key trust](../guest-configuration.md#host-key-trust) has the design,
including that anyone with `VM.Audit` on such a VM can read its private host key.
