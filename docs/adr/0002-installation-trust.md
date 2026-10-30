# Trust Proxmox installations, not revisions

Supersedes [0001](0001-revision-execution-trust.md). Reviewing and trusting every
revision made each commit wait for an operator, which proved too costly for a
single-administrator homelab whose repository is already reviewed before merging.
The decision that matters is where credentials go: trust each Proxmox installation's
endpoints once, when it is added or its endpoints change, and run any valid
revision of the current repository source without further trust. Apply approval
and the destructive policy remain the per-change gates. Tool-native code in a
commit now runs with credentials without an Octopushy review; that is accepted.
