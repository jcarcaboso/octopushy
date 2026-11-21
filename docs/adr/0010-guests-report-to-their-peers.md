# A guest's setup reports values to its peers, kept from a run over its pinned host key

A Technitium cluster pins each member's TLS certificate by SHA-256. Members trust only that pin, because a member sends its
admin password and, during a join, a temporary administrator's. Only the member knows the value. Its setup printed it, and
a person copied it from that run's log into every member's `guest.yaml`, committed it, ran `form-cluster` on the primary
and then `join-cluster` on each secondary. Each step was a chance to pin the wrong value or run the actions out of order.

A workflow can now declare `reports`, the values its setup's playbooks may hand back with `set_stats` as
`octopushy_reports`, each with a pattern. It can also declare `peers`, a path into a guest's inputs that names other guests
by id. Octopushy keeps the latest reported values per guest, gives a guest its peers' values as `octopushy.peers`, and
proposes a guest's setup once when its peers' values change. A playbook can then form the cluster once every pin is known,
and stop at a declared outcome before a join, which a person approves.

The trust stays where [0004](0004-guest-configuration-trust.md) put it. A value is kept only from a playbook step of the
guest's own configure run that ended as it meant to, and that step reached the guest over its pinned host key. Nothing is
read from the network or from another guest. A value is therefore as trustworthy as that pin, which is as trustworthy as
the run log a person read before. The same pin also covers the guest's address: a value is given to peers only while the
guest still has the address the run connected to. A peer has to be a guest of the same lab whose workflow has the same id,
so a guest running other code cannot pose as a member.

## Considered options

- **Keep copying by hand.** Nothing new to trust, but the copy is the step that goes wrong, and Git then holds values only
  a machine knows. A rebuilt member needs another commit on every member.
- **A store entry the run writes.** A playbook could write the value into the variable store and peers could name it. But
  a run would then write to the store, which only people and the server do today. Store keys are also operator-facing, and
  a value a guest wrote would look like one a person set.
- **Stage outputs.** [0007](0007-names-a-stage-sets.md) declined these for services. Within one run a later stage's plan is
  made before an earlier stage applies, so outputs would need a second approval. Reports go between guests and are
  delivered at the next run, which takes its own approval.
- **Ordering in the engine.** An engine-level cluster run, as the Clusters item under Later sketches, would apply stages to
  members in order under one approval. It needs a run that binds more than one guest's key, which nothing does yet. With
  reports, each run still reaches one guest, and the order comes from each guest's setup acting only once its peers have
  reported what it needs.

## Consequences

- A playbook's `set_stats` can now change what another guest's run gets. Workflow code was already trusted to run as root
  on the guest ([0004](0004-guest-configuration-trust.md)). The pattern constrains what it can hand over, and the agent
  drops any value that masking would change.
- The values a run gets are fixed in its preview's manifest, so the approval covers them. They are not inputs, and they do
  not change the guest's configure hash, so they are not shown as definitions and do not count as drift.
- Two guests that report to each other settle once their values stop changing. Proposing setup is still governed by each
  guest's policy, and an action that waits for a person still waits.
- A replaced machine's reports are forgotten when its provision run creates it. Its peers then get nothing until its setup
  runs again, and a playbook has to treat a missing value as unknown, not as a reason to change anything.
- `reports` and `peers` are new fields of `workflow.yaml`. Earlier Octopushy versions refuse them, so a workflows release
  that uses them needs an Octopushy that reads them, and an Ansible image whose callback passes `octopushy_reports` on.
