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

An installation's Proxmox tokens and `system/` keys cannot be delivered to a playbook.
`preview` and `prepare` run before approval and get no secrets, as a plan gets none it
should not have. Nothing here stops a commit from sending a guest's own setup secrets
somewhere else, or code on a `local` runtime agent from reading that agent's identity; both
follow from accepting tool-native code, as 0002 did, and the second is the reason to run
sensitive work on an agent of its own. Scoping a secret to reviewed code is separate work.
