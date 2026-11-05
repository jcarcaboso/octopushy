# Configuring a guest with Ansible

**Decided November 1, 2026.** A guest in the lab repository can be configured by Ansible,
from Octopushy, once it is provisioned. This page is the design: what a definition says,
what a run does, and what it trusts. It carries no knowledge of any one application.
[ADR 0004](adr/0004-guest-configuration-trust.md) records the trust decision.

A guest's setup is the Ansible stage of its [workflow](workflows.md), after the stage that makes the
machine. It is not VM-specific: a run reaches a guest over SSH and never talks to Proxmox, so a
container guest with `sshd` uses the same definitions once `type: container` lands. *Until November 3,
2026 this was a recipe with `operation: configure`, named by `setup.recipe`.*

## Definitions

A workflow with `manages: vm` names the Ansible project and what can be run from it:

```yaml
# workflows/ai-server/workflow.yaml
schemaVersion: 1
version: "1.0"
manages: vm
inputs: inputs.schema.json              # JSON Schema for { hardware, network, inputs }
secrets: [ai_server_tailscale_key]      # what a guest must provide
stages:
  - name: machine
    executor: opentofu
    root: ../../modules/opentofu/proxmox-vm
  - name: setup
    executor: ansible
    root: ../../modules/ansible/ai-server # the Ansible project: ansible.cfg, playbooks, roles
    playbook: playbooks/configure.yml   # the action named setup; relative to root
    prepare: playbooks/prepare.yml      # runs on the agent before approval; see below
    timeoutMinutes: 180                 # destructive: false unless the stage says so
actions:
  update-tools:
    stage: setup
    playbook: playbooks/update-tools.yml
    prepare: playbooks/prepare.yml
    destructive: false                  # required: Octopushy cannot tell, so the workflow says
  update-system:
    stage: setup
    playbook: playbooks/update-system.yml
    snapshot: applied                   # reuses what the last successful run prepared
    destructive: false
outcomes:
  deferred:
    description: Work is active on the guest. Approving interrupts it.
    approve:
      vars: { ai_server_allow_busy: true }
```

A guest names the workflow and gives it inputs and secrets:

```yaml
# guest.yaml
workflow: ai-server@1
inputs:                                 # extra vars, validated by the workflow's schema
  ai_server_hostname: pve01-ai
secrets:                                # extra var name -> store reference
  ai_server_tailscale_key: { store: pve01-ai/tailscale-key }
secretFiles:                            # extra var name -> a private file holding the value
  ai_server_git_private_key_src: { store: pve01-ai/git-ssh-key }
access:
  address: 10.15.60.10                  # defaults to the static or reserved address
  user: ops
  # hostKey: { store: access/pve01-ai/host-key }   # only for a machine Octopushy did not create
```

- **The machine stage makes the machine, the setup stage configures it.** They are separate runs
  on one resource, provision and configure, so the existing per-resource rule serializes them: a
  later run waits for an earlier one, and an uncertain run holds the guest until a person closes it.
- **The stage's executor selects the shape.** The OpenTofu stage keeps its plan and apply. The
  Ansible stage has the steps below. The guest never names an executor; the workflow does.
- **An action is the whole unit.** The setup stage's own playbook is the action `setup`, and the
  workflow's `actions` are the others. Octopushy passes no `--tags`, `--limit`, `--start-at-task`
  or `--check`, and no definition can add one, so the selective runs the `nodes` wrapper refuses
  cannot be asked for. The inventory holds this one guest.
- **Files.** The run receives the repository snapshot, like a plan does. `{{ octopushy.guest_directory }}`
  is the guest's folder in it, for files that belong to one guest.

Extra vars may not start with `ansible_` or `octopushy`: those are how Octopushy pins the
connection, and they are refused in `inputs`, `secrets`, `secretFiles`, `approve.vars` and
whatever a prepare step returns.

### What starts a run

| Trigger | Action | Status |
|---|---|---|
| The first time a provisioned, verified guest's workflow has a setup stage | `setup` | Implemented |
| A change to the guest's `inputs`, `secrets`, `secretFiles` or `access`, the workflow's version or folder, the setup stage's `root`, or the guest's folder | `setup` | Implemented |
| A provision run that creates or replaces something, such as a new VM | `setup` | Implemented |
| A new active version of a store value `secrets` or `secretFiles` names | `setup` | Implemented, [below](#shared-store-values) |
| **Configure** on the guest page, or `POST /api/guests/{id}/configure` | the one chosen | Implemented |
| A schedule declared on the guest | the one it names | Implemented, [below](#schedules) |

Each input hash gets one automatic run, and so does each active version of a store value the
setup follows. A failure is not retried by itself: it waits for a new commit, a new version or
a person. A guest that is not in sync with its provisioning waits for that
first, so one commit that changes both runs them in order. Automatic runs use the head of the branch, so a
rollback configures the commit it returned to. A person cannot start a run on a guest whose
provisioning has not caught up either.

### Shared store values

**Decided November 3, 2026.** One store key can hold a credential many guests use, such as a Git
SSH key, a GitHub CLI `hosts.yml` or a GitLab CLI config. Promoting a new version of it reaches
every guest that names it:

```yaml
secrets:
  machine_gh_hosts: { store: shared/gh-hosts }
secretFiles:
  machine_git_ssh_key: { store: shared/git-ssh-key }
```

Octopushy records, for each guest, the active version of each key its `secrets` and `secretFiles` name without a
`version`, at the moment its `setup` action starts. When a key's active version later differs,
the guest gets one run of that action, with the reason **Store value changed** and a detail naming
the key and both versions, such as `shared/git-ssh-key v2 to v3`. Its guest page lists the keys it
follows, the version active now, the one it was last attempted with, and **Run pending** while a
run is owed. "Attempted" is deliberate: a run that failed still counts, because a failure is not
repeated for the same version, so the machine may still hold the older value. Before revoking the
old credential upstream, check that each guest's newest run with the new version succeeded. Each guest's own `policy.approval` applies, so a guest with manual approval waits
for a person, as for a commit. A promotion configures every guest that follows the key, each
independently: one failing does not hold back another.

| Case | What happens |
|---|---|
| **Staged candidate.** | Nothing. Only the active version counts, so a candidate starts nothing until it is promoted |
| **Rollback.** An older version is promoted again | A change like any other: one run, with that older version. As a commit rollback configures the commit it returned to, the value that is active is the one delivered |
| **Revoked or deleted.** The active version is revoked, or the key is removed | Nothing. There is nothing to deliver, and a run would only block. The record keeps the version it had, so promoting a working version afterwards starts the run |
| **Failure.** | Each version gets one automatic run. A failed run is not repeated for the same version: it waits for the next promotion, a commit, or a person |
| **Active run.** Any run is active or waiting on the guest | The change waits, like a commit's. If that run is of the `setup` action and pinned the new version, because the promotion came before its first step went out, it delivered it, and nothing more is owed. Otherwise the run starts once the guest is free |
| **Uncertain run.** | Holds the change with everything else until a person closes it |
| **Schedule.** | A scheduled run, or one a person starts, of the `setup` action delivers what is active and is recorded the same way, so nothing is owed after it. A schedule of another action does not count, even though it takes the new version too: the `setup` action is the one that converges a guest. A firing while the store's run is active is skipped, as with any overlap |
| **A commit too.** | A commit that changes the guest's configure inputs runs the action once, recording the store's versions at the same time, so one change of each makes one run |
| **Pinned in Git.** `{ store: …, version: 2 }` | Not followed. That version changes only with a commit, which the configure hash already covers |
| **`system/` keys.** | Not followed. Validation refuses them in a guest's definition, and the guest's own access key keeps its [own rotation](#rotating-the-key), whose stages prove a login. `access.sshKey` and `access.hostKey` are not followed either: they decide how a run connects, not what it configures |
| **Upgrade.** | A guest configured before this was built records what is active at its first check and starts nothing, so upgrading runs no guest |

A plan of the guest's provisioning is not affected: OpenTofu receives no setup secret, and the
provisioning hash does not include store versions.

### Schedules

**Decided November 2, 2026.** A schedule is declared in Git, on the guest, and runs one action:

```yaml
workflow: ai-server@1
schedules:                              # setup, or an action of the workflow
  - { action: update-tools, cron: "0 6 * * *" }
  - { action: update-system, cron: "30 6 * * sun" }
```

It belongs on the guest because one workflow serves machines with different maintenance times,
and a field on a workflow's action would force one time on all of them. The expression has five
fields and is read in the lab's `timezone`, or UTC if the lab names none. When the clocks change, a
time in the hour they skip runs as they jump, and a time in the hour they repeat runs once, the
first time round. A schedule that runs every hour keeps going through both. A schedule that names
an action the workflow lacks, an expression that does not parse or never falls due, or a time zone that does not
exist is a validation error. Changing a schedule starts no run: it says when an action runs,
not what it does, so it is outside the configure hash.

A firing calls the same `Planner.ConfigureGuestAsync` as a person, with the reason `schedule`,
and gets the same checks. Octopushy looks for what has fallen due once a minute, and a guest's
page shows its next time and what the last firing did.

| Case | What happens |
|---|---|
| **Missed.** Octopushy was down at that time | **At most one catch-up, within an hour.** Back within the hour, it runs once. Later, the time is skipped and noted on the guest, because a schedule says when, and an action that reboots should not run at whatever hour a server came back. Only the newest time counts, so there is never a backlog |
| **Missed.** No agent took the run | A step that can change the guest may go out until an hour after the run's time, not an hour after a catch-up started it. That is checked at each moment one would go out, so a preview that reports late or an agent that connects late cannot release it, and the run is cancelled with that reason. A finished preview does not change that, because nothing has reached the guest. Once such a step has gone out, it ends the usual way. The limit is on going out: an agent starts the step moments later, and one that has not started it within its 60-second lease leaves the run uncertain |
| **Overlap.** Any run is active on the guest | The firing is skipped and says which run is in the way. It is not queued |
| **Overlap.** An uncertain run holds the guest | The same. Every firing is skipped, visibly, until a person closes that run |
| **Approval.** `policy.approval: manual`, or a destructive action under `policy.destructive: manual` | The run waits for approval as any other does. While it waits, later firings are skipped, so at most one is ever waiting. What a person approves is that preview: it does not move forward by itself, and a person who wants newer versions rejects it and lets the next time run. The hour does not apply to a run waiting for a person or approved by one: the time is then theirs, however late, and an approved run waits for an agent as any other does |
| **Declared outcome**, such as `deferred`, on a workflow that lets a person approve it | **A scheduled run does not wait.** It ends as succeeded, naming the outcome and its detail, promotes nothing, and frees the guest. The next time tries again. A person who wants it now starts the action by hand, and that run does wait for approval. This rule is about outcomes with `approve`: one without it has nobody to wait for, so it ends as its `result` declares, as on any run. `failed` fails the run and promotes nothing, and `succeeded` ends it as succeeded and promotes what was prepared |
| **Failure** | Each time comes up once, whatever happens to it, so a failed run is not repeated until the next time. It stays on the guest's page and in **Runs**, with a link from the schedule |

**A schedule is not a window.** A schedule says when an action starts. `policy.windows`, which
is documented in the [repository format](workflows.md#maintenance-windows) and not built, would limit when any automatic
change may start. When windows arrive, a scheduled run is an automatic change: a firing
outside a window would be skipped with a note like any other, and nothing here needs to
change. Until then a guest's schedule is the only limit on when its scheduled actions start.

The maintenance Jenkins runs at 06:00 therefore moves into the guest's definition. Jenkins
keeps maintaining the machines it maintains today.

## A run

```text
preview -> approval -> playbook [-> outcome-approval -> playbook-approved]
```

### Preview, approval and destructive

Ansible's check mode is refused, as the [Fedora module](https://github.com/jcarcaboso/octopushy/blob/1446f1b6e7866992df64306d4b0b23d08edaae0e/examples/lab/modules/ansible/fedora/README.md)
(now in `octopushy-lab`) records: it is not an exact preview, tasks that depend on earlier tasks misreport, and
playbooks like the Fedora workflow assert against it. **Octopushy therefore does not show a
prediction of what will change.** It shows what will run, and binds approval to exactly that.

The `preview` step runs on an agent with **no secrets and no SSH key**, and never contacts
the guest. It runs `--syntax-check` and `--list-tasks`, then the action's `prepare` playbook
if it has one. An operator sees:

- the action, playbook, commit, tool version and image;
- the list of tasks, by name. Dynamic includes are listed as the include, not expanded;
- the files changed under the workflow's folder, the stage's project and the guest's folder since this guest's last successful run;
- the inputs, with their values, and the secrets, by name, store key and version;
- what `prepare` resolved, such as the versions an update would install;
- the SHA-256 fingerprint of the pinned host key.

All of this goes into one document, `preview.json`, whose SHA-256 the approval names. The
`playbook` step receives that exact artifact and refuses anything else, as an apply refuses
another plan.

- **`policy.approval: manual`** waits for approval on every run, automatic, scheduled or manual.
- **`policy.destructive: manual`** waits only for an action declared `destructive: true`.
  Rule 11 says Octopushy decides from the preview, but a playbook has no preview that shows
  deletes. So the workflow states it: `destructive` is required on every action, and the setup stage's own playbook is `false` unless the stage says so: a
  forgotten declaration fails validation rather than running unattended. This is a claim
  by the repository, not something Octopushy verifies.
- **A rebuilt machine** is a provision run. It follows that run's rules, including those of
  [replacing a VM](workflows.md#rules).

### Prepare

A prepare playbook is the generic form of the `nodes` controller step that resolves
release channels. It runs on the agent during `preview`, with `hosts: localhost` and no
secrets, and writes a JSON object to the path in `OCTOPUSHY_PREPARED_FILE` (at most
256 KiB). Those values become extra vars of the `playbook` step and appear in the preview.
So a person approves the versions that will be installed, and a retry after approval
installs the same ones, not whatever is newest by then.

**State that outlives a run.** After a `playbook` step succeeds, Octopushy promotes what was
prepared to the guest's applied snapshot. An action with `snapshot: applied` uses that
instead of preparing, and fails its preview if there is none yet. A failed or deferred run
promotes nothing. An outcome declared `succeeded`, with no `approve`, counts as a success and
promotes.

### Outcomes and failure

| What happened | Step | Run |
|---|---|---|
| Exit 0 | `succeeded` | `succeeded`, snapshot promoted |
| A task failed (exit 2, recap present) | `failed` | `failed`. The change is partial and known; run again |
| The tool never started a task: pull failure, syntax error, host key or login refused on the first connection | `failed` | `failed`. Nothing happened on the guest |
| Unreachable after a task started, even if an outcome was reported first or the play ignored it | `uncertain` | `uncertain` |
| Unreachable more than once, in a play that ignored it, even if the last was a refused login | `uncertain` | `uncertain` |
| Killed, timed out, or ended without a recap, unless the tool provably never started | `uncertain` | `uncertain` |
| Agent lost or restarted while it ran | `uncertain` | `uncertain` |
| A declared outcome | `succeeded` | Waits on `outcome-approval`, or ends as declared |

An uncertain run behaves like any other mutating action: it is never retried, it holds the
guest until a person closes it, and a late result is still accepted. **Octopushy does not
touch a lock held on the guest.** A killed run can leave one, such as the Fedora module's
`/run/lock/octopushy-fedora-workflow`. A person has to establish that nothing is still
running and remove it. Removing it automatically to retry is exactly what the lock exists
to prevent.

**A playbook reports an outcome** with `set_stats`, which works whether it then fails or
ends cleanly:

```yaml
- ansible.builtin.set_stats:
    data: { octopushy_outcome: deferred, octopushy_detail: Three threads are running. }
    aggregate: false
    per_host: false
- ansible.builtin.meta: end_play
```

Octopushy knows only the name, which must be declared under `outcomes`; an undeclared name
is a failure. With `approve`, the run waits for a person, who sees the description, the
detail, and the vars approval adds. Approving runs the action once more, with the same
commit, the same preview and those vars. A second declared outcome ends the run as failed,
so there is at most one approved attempt, as in the Jenkins job. Rejecting cancels the run.
Without `approve`, an outcome's `result` (`succeeded` or `failed`, default `failed`) ends
the run, with the outcome's name in its detail, so it never reads as a plain failure. A
scheduled run follows the same rule for those; only an outcome with `approve` is [treated
differently](#schedules).

Exit 75 from the `nodes` wrapper does not carry over: that wrapper invents the code by
searching the log. A port replaces it with the `set_stats` task above.

**Long runs and reboots.** `timeoutMinutes` defaults to 120 and allows up to 1440. A timeout
stops the run and is uncertain, and it keeps applying after the tool exits, to whatever it left
running with its output open. `ansible.builtin.reboot` works: the host key is unchanged
and nothing needs the connection to persist. The attempt keeps its lease through the agent's
heartbeat however long it runs. The agent's 30-minute stop grace period is shorter than
many runs, so updating the agent during one makes it uncertain. Configure attempts do not
count against a Proxmox host's `concurrentOperations`, because they do not touch Proxmox,
but they do take an agent slot: the default is two.

## Access

### SSH identity

**Octopushy logs in with a key of its own, and `access.sshAuthorizedKeys` is for people.**

A guest Octopushy provisions that runs any Ansible work gets one Ed25519 key, generated by the server
the first time it is needed. That work is its workflow's setup stage or an Ansible stage of one of its valid
[services](workflows.md#services-on-a-guest) alike: the key follows who manages the machine, not what
kind of Ansible work runs on it. A leaked key reaches one machine. Nobody copies a private key anywhere.

- **The private half** is a versioned secret at `system/guest-access/<resource id>`, which no
  definition can name. It reaches only this guest's `playbook` steps, never `preview`, and stays in
  an `ssh-agent` there, never in a file. Each release is recorded in `SecretResolution`, and it is
  masked like any other secret.
- **The public half** joins `sshAuthorizedKeys` when a provision run's variables are built, so it
  is never in Git. It is on the guest's page, with when an apply carried it and when a run
  holding it last finished. That is use, not proof of a login: a playbook can finish without connecting.
- **`GuestAccessKey` records where each version stands**, apart from the provisioning hash. That
  hash only says that the guest has a managed key, so a change of key plans nothing.
- **A rebuild keeps the key**, and retiring the guest removes it. The record of who received it stays.
- **Once issued, the key stays wanted** until the guest is retired or names `access.sshKey`. Moving the guest to a
  workflow without a setup stage, removing every Ansible stage of the guest's services, or breaking a service in any way, does not take it off
  the guest's provisioning, which would reboot a VM and replace a container for a key that is harmless where it
  is. Before pipelines, removing a setup dropped it.
- **It is unaffected by the refusal of Proxmox and OpenTofu credentials,** because only the
  public half goes to that work, as a variable, and is never a released secret.

`access.sshKey` remains for a machine Octopushy did not create: a store reference to a **secret**
entry holding an unencrypted OpenSSH private key. A guest that names one gets no managed key, and an
explicit key always wins.

**Nothing logs in with Octopushy's key without a record that it reached the machine.** That record
is a successful provisioning apply that carried the public key, or a login with that key alone
during a rotation. It shows delivery, not who owns the machine, and not that the key is still
installed. Where there is no such record, no setup run starts automatically, including for a changed
store value the setup follows. Configure, a schedule and a service's Deploy are refused, saying to
name `access.sshKey`, and the guest's page says so under Configuration. The check runs once more
just before the private half would be released to a step. A guest from 0.4.0 whose provisioning
carried the key already has the record, and so does a key a rotation verified.

`access.user` is the login, and `become` is the playbook's business. For a VM from the
`proxmox-vm` module, that login is the module's `admin_user`.

**Adding the key to a VM that already exists does not rebuild it.** Checked in bpg/proxmox 0.117.0,
in Proxmox's `Cloudinit.pm`, and on a real Fedora 44 VM:

- `user_account.keys` is not `ForceNew` on a VM. A real `tofu plan` with that provider shows
  `update`, and the provider rebuilds the cloud-init disk and reboots the VM once, because
  `reboot_after_update` defaults to true.
- Proxmox computes the `instance-id` from the user data, so a changed key is a new instance. Cloud-init
  adds the key on that boot, and existing keys and data stay.
- **Cloud-init also regenerates the host keys on that boot.** Read the host key after it.

**On a container, the same field is `ForceNew`: a changed key replaces the container.** A real plan
shows `delete, create`, naming `keys` as a reason. That is
why the version OpenTofu carries, marked `Bootstrap`, stays fixed while the machine exists. A
later change of key goes through the machine's own configuration instead, which is how
[rotation](#rotating-the-key) is built, and that is also why the provisioning hash holds
no key. PR 21's containers take keys through the same `sshAuthorizedKeys` variable, so they need
nothing further.

**Adding a valid service with an Ansible stage to an existing guest delivers the key the same way.** A
guest that had no Ansible work had no managed key, so the service changes its provisioning hash: the guest is
provisioned again first, which for a VM is the in-place update and one reboot above, with new host keys,
and for a container is a replacement. The service waits for that. The owner's approach is to rebuild
guests rather than migrate them, so there is no migration path for existing guests. An invalid
service gives no key, and nothing about a service takes one away, as [workflows.md](workflows.md#services-on-a-guest) says.

The guest agent cannot carry a key: on Fedora, with SELinux enforcing, `guest-ssh-add-authorized-keys`
fails and `guest-file-open` for appending to `authorized_keys` is denied.

### Rotating the key

**Rotate key** on the guest's page, or `POST /api/guests/{id}/access/rotate`, starts one run:

```text
preview -> approval -> verify-current -> authorize-key -> verify-key -> promote-key -> approve-removal -> remove-key -> verify-removal -> confirm-refused
```

| Stage | Key used | What it does |
|---|---|---|
| `preview`, `approval` | none | Shows the fingerprints going on and coming off. Follows `policy.approval` |
| `verify-current` | In use alone | A login that does nothing else, so a rotation starts from a key shown to work. It stops here if it cannot |
| `authorize-key` | In use | Appends the candidate's public key. Nothing is rewritten |
| `verify-key` | Candidate alone | A fresh login, no multiplexing, strict host key: the expected login, and root through `become` |
| `promote-key` | none | **A person, whatever the policy.** Moves this guest's binding to the candidate and records the evidence |
| `approve-removal` | none | **A person, separately.** The old key stays on the guest until then |
| `remove-key` | New | Takes off every line that authorizes an old controller-owned key, as the guest's OpenSSH reads it |
| `verify-removal` | New | Logs in again and reads the file: no line holds the old key, and the key in use is still found |
| `confirm-refused` | Each old key alone, then new | Each old key has to be refused by `sshd`, and then the key in use has to get in. Only then are they recorded as removed, and those versions revoked in the store |

- **A rotation chooses only Octopushy's own keys.** The guest's own OpenSSH says what each line holds: a small program passes every physical line through `ssh-keygen -l` and compares fingerprints, so nothing here parses a key. A line whose key is an old one goes, whatever else it says, and that includes a line `sshd` would have refused. A line that only mentions the old key stays, and every other line it read stays byte for byte. The one way a person's key can go is the limit under "What someone else writes" below. The program and playbooks ship with the server, so a repository cannot change what is removed.
- **Only `sshd` can say a key is gone.** The file is one place a key can be: `AuthorizedKeysFile` can name others, and a login's `HOME` can be somewhere else. So the last stage logs in with each old key alone. If `sshd` lets one in, the run fails and says which, and if the guest did not answer, nothing is concluded. A login that got in and then failed is not a refusal, even if a forced command prints the same words, as an inner `ssh` would: `ssh` itself reports having authenticated before any session starts, and that decides it, including for a key that is one factor of two. The login with the key in use comes straight after, from the same agent, because a refusal means nothing if `sshd` is turning everyone away, as `DenyUsers` would.
- **What someone else writes can be lost in one instant, and that is an accepted limit.** Adding appends. Removing has to replace the file, so it reads the file again just before doing so and starts over if it changed. A write to the file in the instant between that last read and the replacement is lost, and that can be a person's key: sshd has no lock to take. The replacement keeps the mode, label and, as far as the login may, the owner. A hard link to the file stays on the old copy.
- **Anything that stops the check is not read as gone.** The program has to find the key Octopushy logged in with, in the same pass. If it cannot, because `ssh-keygen` failed or this is not the file `sshd` read, it stops, and writes nothing. So a removal can never take the key in use.
- **A candidate that cannot log in changes nothing in use.** The run fails, runs keep the working key, and the next rotation uses the same candidate again, because it may already be on the guest.
- **An interruption is uncertain.** The run holds the guest until a person closes it, and nothing is removed to tidy up.
- **Rejecting a stage ends the run where it stands.** After a rejected removal the new key is in use and the old one stays listed as still on the guest; the next rotation takes it off.
- **One guest at a time.** Each guest has its own entry, so promoting one rotates no other.
- **It reuses the store's versions.** Staging, promotion and revocation are the store's own transitions, on the same columns, with `system/` staying closed to the operator endpoints. The [credential lifecycle](credential-lifecycle.md) rotates a value and proves nothing about a guest, so what a rotation adds is the guest stages and their evidence, in `GuestAccessKey`.
- **A removal nobody could verify stays on the list.** If `verify-removal` or `confirm-refused` fails, the old version is neither marked removed nor revoked, and the next rotation looks again.
- **No provision plan comes of it.** OpenTofu keeps carrying the version the machine was created with. A plan that makes this guest's machine carries the key in use: for a machine that is gone it just does, and for one being replaced the plan stops once and asks to plan again. A plan that makes anything else changes nothing. A plan that makes a machine without saying its VMID and host stops, once this guest's key has been rotated, because it might be this guest's.
- **It cannot carry a first key.** Its first step logs in with the key in use, so a guest without one stops there, with nothing changed.

### Host key trust

A configure run connects with `StrictHostKeyChecking=yes`, a private `known_hosts` and a null
global file. It **never scans** a host: the key it accepts comes from one of two places.

**A guest Octopushy creates gets a host key of Octopushy's.** Decided November 3, 2026, and built
on workflows on November 5, 2026. Octopushy generates an Ed25519 key for the guest before the
machine exists, the provision run puts it in the guest's cloud-init user data, and every configure,
rotation and pipeline run pins its public half. Nobody reads a key off a console. That needs three
things, and validation says which is missing:

- a guest Octopushy provisions and runs Ansible work on, from its workflow's setup stage or a
  service's stage, as for [its login key](#ssh-identity), and no `access.hostKey`;
- a workflow whose machine stage has `hostKey: managed`, which says its module installs the key it
  is given. Only the first stage of a workflow with `manages: vm` may say it;
- an installation whose `snippets.hosts` lists the guest's host, once that host is [set up](#setting-up-a-host).

```yaml
# workflows/fedora-vm/workflow.yaml, in the workflows repository
stages:
  - name: machine
    executor: opentofu
    root: ../../modules/opentofu/proxmox-vm
    hostKey: managed            # the module installs octopushy_host_key from a snippet

# labs/home/proxmox/pve01/installation.yaml, in the lab repository
snippets:
  datastore: local              # the default; a directory datastore with snippets content
  hosts:
    pve01:
      hostKey: { store: proxmox/pve01/ssh-host-key }   # plain: what snippets.sh printed
      # address: 10.15.5.100     # default: the host of its API endpoint; port defaults to 22
```

| | Where it is | Who gets it |
|---|---|---|
| Private half | The secret `system/guest-host-key/<resource id>`, one store version per key, which no definition can name | The **plan** of a provision run, as the sensitive variable `TF_VAR_octopushy_host_key`. The apply uses the saved plan, and no configure run, preview, verify step or API response ever holds it |
| Public half | `GuestHostKey`, one row per version, on the guest's page | The plan, in `octopushy_snippets.hostPublicKey`, and every configure, rotation and pipeline run as its pin |

**The one secret an OpenTofu run gets besides the Proxmox token.** A provision run's OpenTofu
environment otherwise holds the Proxmox token and nothing else, and a service's OpenTofu stage holds
nothing at all. This is a narrow exception, checked where every run's credentials are checked, not a
change to that rule:

- only a provision run for a guest whose host key is Octopushy's carries the three snippet roles,
  `opentofu.host-key`, `snippets.ssh-key` and `snippets.host-key`; any other run that holds one is
  refused, and so is one that lacks one;
- each role has its one environment name, `TF_VAR_octopushy_host_key` and
  `PROXMOX_VE_SSH_PRIVATE_KEY`, and only those may be mapped; the host's key is written to a file;
- the guest's private host key goes to the plan step alone, the upload key to the apply step alone,
  and each binds only its own guest's or its own installation's `system/` entry;
- the agent summarizes the plan as before: the JSON plan itself, which holds the snippet, is never
  logged or returned, and the module marks the variable sensitive. Both keys are released secrets of
  their attempts, so the agent and the controller mask them like any other.

**Which key, and when the pin moves.** The store's current version is the key a plan carries; the
pin is the newest version an apply delivered. When a plan is made, before anyone is asked to approve
it, Octopushy compares what it does with what it carried:

| The plan | It carried | What happens |
|---|---|---|
| Makes the guest's machine: the first one, or a replacement | A key no machine was given | It goes on |
| Makes the guest's machine | The key an earlier machine was given | Octopushy makes a new key and plans again with it, in the same run. A new machine never starts with a key the old one held: that key may be on its disks, its cloud-init drive or a backup |
| Keeps the machine | The key it was given | It goes on |
| Keeps the machine | Another key, such as one a rejected replacement left | Octopushy plans again with the key the machine was given, so the snippet never hands a running machine a key nothing pins |

The plan that is kept says why it was made again, and the approval is of that plan. The pin moves
when the apply that delivered a new key finishes, in the same save that records the apply, and not
before: while a replacement waits for approval, configure runs still pin the old machine's key, and
the guest's page shows both. A run whose plans keep changing their mind, three times, stops and asks
for a new one. Until any apply has delivered a key, a configure run waits and says so, because the
guest still has whatever key its image made.

Once Octopushy holds a host key for a guest, the guest keeps it while it is offered, whatever happens
to its Ansible work: dropping the snippet would replace the VM. A stage runs on the guest its service
is on, so it pins that guest's key. A guest that names `access.hostKey` keeps that pin, which wins:
that is how a machine Octopushy did not create is reached. Retiring the guest deletes every version of
both halves, and the record of where the private halves were released stays in `SecretResolution`.

**How it reaches the guest.** Proxmox's own user data carries no host keys, and its API cannot
upload the snippet that would: it accepts only ISO images, container templates and `import`
content, and `cicustom` reads only `snippets` volumes. So the module writes everything Proxmox
would have written, adds `ssh_deletekeys`, `ssh_genkeytypes: [ed25519]` and `ssh_keys`, and the
provider uploads it over SFTP. Proxmox computes the `instance-id` from the user data, so cloud-init
treats the VM as new, deletes the image's keys and installs Octopushy's. On a real Fedora 44 VM
with SELinux enforcing, the key it served was exactly the one given, labelled `sshd_key_t`, and the
only one.

**The upload.** The provider logs in as the account [`snippets.sh`](#setting-up-a-host) creates,
with a key Octopushy generates per installation at `system/installation-snippets/<installation>`.
Only provision applies get it, as `PROXMOX_VE_SSH_PRIVATE_KEY`, and only for a guest whose host
key is Octopushy's. The provider trusts any host it has not seen, so Octopushy writes the host's
own key, from the plain entry `snippets.hosts.<host>.hostKey` names, to the attempt's
`HOME/.ssh/known_hosts`, and the provider refuses any other: a real `tofu apply` with a different
key there failed with *REMOTE HOST IDENTIFICATION HAS CHANGED* and uploaded nothing.

**Who can read the private key, plainly.** The owner accepted this on November 3, 2026:

- **anyone with `VM.Audit` on the VM**, through `GET /nodes/{node}/qemu/{vmid}/cloudinit/dump?type=user`,
  which returns the snippet. Today that is every token that can see the VM, so **narrowing the
  Proxmox tokens matters more** once guests use this;
- root on the Proxmox host: the snippet file (mode 0600, owned by the upload account) and the
  cloud-init drive built from it;
- OpenTofu's saved plan and Octopushy's built-in state, both encrypted at rest, and in the clear in
  the agent's workspace while the plan or apply runs;
- the guest's root, in cloud-init's copy of its user data.

Someone holding it can answer as the guest. Everyone on that list except API users with only
`VM.Audit` already controls the guest or its hypervisor.

**A guest that switches to this is a new machine.** The provider replaces a VM whose
`user_data_file_id` changes, so dropping `access.hostKey` from a guest that exists plans a
replacement, which follows `policy.destructive`. There is no migration for a machine that already
exists: recreate it. A workflow that adds `hostKey: managed` is a minor version for that reason:
a guest that keeps its `access.hostKey` gets no snippet, and its machine does not change.

**Afterwards, a change to what the snippet holds keeps the VM.** A person's key in
`access.sshAuthorizedKeys`, for example, uploads the snippet again: the provider deletes the file
and uploads it under the same name. The VM names the snippet by a volume id known when planning,
`<datastore>:snippets/octopushy-<vmid>-user-data.yaml`, so its configuration does not change and
the plan leaves it alone. Proxmox rebuilds the cloud-init drive from the snippet each time it
starts the VM, so the change reaches the guest at its next start from Proxmox, where cloud-init
sees a new instance and applies it; a reboot from inside the guest does not rebuild the drive.

**The upload is not atomic, an accepted limit.** The provider deletes the file, then creates it
under its final name and writes into it, and Proxmox reads whatever is there when it starts the VM.
A start from Proxmox in that moment, or after an upload that failed, reads a missing, empty or
partial file. On a real Fedora 44 VM with SELinux enforcing:

- **no file:** the start fails, and the next one works (from qemu-server's source);
- **an empty file, or one cut so it is no longer YAML:** a new instance. cloud-init deletes the host
  keys and makes new ones, so the pin no longer matches and configure runs are refused. A person's
  existing `authorized_keys` still work;
- **a file cut inside the private key:** sshd does not start, so nothing reaches the guest over SSH;
- **the whole snippet at the next start:** Octopushy's key is back, alone, and the pin matches.

So Octopushy refuses the guest's unexpected identity, and the next start from Proxmox with the
whole snippet heals it. That is fail-closed for Octopushy only: other SSH access to the guest,
such as a person's key, may still work in the meantime. The apply
itself never starts or reboots the VM in that moment: the VM waits on the snippet, and an upload
that fails stops the apply before any call for the VM, which octopushy-lab's test checks. What is
left is a start by something else, such as a person, HA or the host booting, during the few
milliseconds of an upload, or after one that failed and before the next apply.

**An apply that replaces the VM and does not finish** leaves a machine that may hold either key: the
new machine may exist with the new one, or the old with the old. The pin stays where it was, and the
next plan follows the rule above from what it finds. Where the machine serves a key the pin does not
hold, configure runs are refused until an apply delivers the key in its snippet and the machine is
started from Proxmox. It fails closed, and a person may have to start the VM.

**What replaces the VM.** Changing a guest's `vmid` or the installation's `snippets.datastore`
changes `user_data_file_id`, so the provider replaces the VM, as it does when a guest first gets
this, and the new machine gets a new key. Each follows the guest's `policy.destructive`.

**For a machine Octopushy did not create**, `access.hostKey` is a store reference to its public
host key, in the form `ssh-ed25519 AAAA…`. One entry per line is allowed. It has to be a plain
entry, because a preview receives it: a secret posing as a pin is refused. The value lives in the
store rather than in Git for the same reason trust does: a commit may point a guest at a new
address, but it cannot put a new key behind one. A mismatch is a failed run that says so, and a
rebuilt machine of that kind fails closed until someone sets its new key. On Fedora the guest agent
cannot read the key (`virt_qemu_ga_t` is refused `sshd_key_t`, and also `cloud_log_t`, where
cloud-init logs the fingerprint; confirmed on a real Fedora 44 cloud image with SELinux enforcing on
November 2, 2026), so read it through the Proxmox console:

```sh
qm guest passwd <vmid> ops                  # on the Proxmox host
# then, on the VM's console in the Proxmox interface:
cat /etc/ssh/ssh_host_ed25519_key.pub
sudo passwd -l ops
```

That pin is exactly as trustworthy as the channel it was read through, here your Proxmox login.
Anyone who controls the hypervisor can lie about the guest, and they already own it; with
`tls.verify: false` on the installation, a person using the Proxmox interface or API is trusting
that TLS session too. Copying a key from a laptop's `known_hosts` or from `ssh-keyscan` is
trust-on-first-use, which Octopushy cannot tell apart; on an isolated VLAN that is your decision,
and you should know that it is one.

#### Setting up a host

Once per Proxmox host, as root, for example in the host's shell in the Proxmox web interface. The
installation's page has a **Create upload key** button and then shows the exact command, with the
key, the installation's `snippets.user` and `snippets.datastore`, and this release's script:

```sh
OCTOPUSHY_SNIPPETS_KEY='ssh-ed25519 AAAA… octopushy-snippets@home/pve01' \
  OCTOPUSHY_SNIPPETS_USER='octopushy-snippets' OCTOPUSHY_SNIPPETS_DATASTORE='local' \
  bash -c "$(curl -fsSL https://raw.githubusercontent.com/jcarcaboso/octopushy/v<version>/deploy/proxmox/snippets.sh)"
```

[`deploy/proxmox/snippets.sh`](../deploy/proxmox/snippets.sh) adds `snippets` to the datastore's
content, creates the account `octopushy-snippets` with no password and no shell, and lets it log in
only with that key, only over SFTP, only inside a chroot that holds the datastore's snippets
directory at its own path, where it creates files mode 0600. It is safe by construction rather
than by testing one connection:

- **The block is read first.** The drop-in is `00-<account>.conf`, and the script refuses unless
  `sshd_config` starts with Debian's `Include /etc/ssh/sshd_config.d/*.conf` and no drop-in sorts
  before it. sshd takes the first value it finds for these settings, so the block wins for every
  connection of the account.
- **It names one account.** The block is `Match User <account>` with no other condition, so no
  other user's settings can change.
- **Only the key in its file lets the account in.** The block turns off every other way: no CA
  certificate (`TrustedUserCAKeys none`), no `AuthorizedKeysCommand` or principals, no password,
  keyboard-interactive, host-based, GSSAPI or Kerberos login, and no forwarding of any kind, even
  where the host sets them for everyone. Before reloading, it checks each of these with `sshd -T`.
- **Before it changes anything**, it refuses: root, an account or group that exists and that it did
  not create, a configuration `sshd -t` already rejects, a drop-in of its name that it did not write,
  and any other sshd file that mentions the account, such as a `Match User` block for it on some
  addresses. It also refuses a link, or anything but a regular file or a real directory, at any
  path it writes or backs up: the drop-in, the key file and its directory, the mount unit and the
  chroot's directories. This holds for `--remove` too, which also refuses while the account has a
  process, such as an open SFTP session, while any process uses the chroot, such as one working in
  it, which would keep it from being unmounted, or while the chroot holds anything it did not make.
- **sshd's files are written, checked and reloaded, or put back.** It copies them first, with
  their modes, into a directory only root can read. After writing, it checks `sshd -t` and
  reloads only then. If that or the reload fails, it puts the copies back, reloads again if the
  first reload went through, and sshd keeps running what it had, for setup and `--remove` alike.
  Every `sshd -T` it runs names a whole connection (user, host, address, local address and port,
  routing domain), so a `Match` on any of them is evaluated.
- **`--remove` takes the block away last.** It locks and expires the account and deletes its key
  first. The block still turns off every other way in, so the account cannot log in at all from
  that moment. Then it unmounts the chroot, gives root the snippets the account owns, deletes the
  account, its group and the mount unit, and only then removes the drop-in, checks `sshd -t` and
  reloads. If any step fails part way, the block is still there and nothing gets in as the
  account, not even with a certificate from a CA the host trusts. Running `--remove` again carries
  on from there; running setup again clears the expiry and puts the key back.
- **What is not put back.** Setup's earlier steps (the datastore's content, the account, the chroot
  and its mount) stay if sshd's step fails; a second run or `--remove` deals with them. In
  `--remove`, only the drop-in is put back if its own step fails: the steps before it, which leave
  the account with no way in, are not undone.
- **Kept snippets stay root's.** `--remove` keeps the snippets, which hold guests' private host
  keys. Before the account's UID is freed, it gives root every file the account owns there,
  without following links, keeping each file's mode, so an account made later with the same UID
  reads none of them. The directory goes back to mode `00755`, without the setgid bit.

It prints what it changed, and the host's own SSH host key. Store that key as a plain value under the name
`snippets.hosts.<host>.hostKey` gives. Running it again changes nothing; running it with a new key
replaces the old one. `bash snippets.sh --remove` undoes it, and leaves the datastore's content and
its snippets alone. [`tests/proxmox-snippets/run.sh`](../tests/proxmox-snippets/run.sh) runs all of
this on Debian bookworm and trixie, as Proxmox VE 8 and 9 use, with Proxmox's own commands stubbed.

**Why a key Octopushy generates.** Nobody copies a private key: what leaves Octopushy is the public
half, for the script. The account can write snippets and nothing else, so a leaked key writes
snippets. A password would have to be typed into the host, would need password logins enabled for
the account, and could be replayed. The key's line is marked `restrict` as well.

**Containers** do not use this: Proxmox makes a container's host keys itself when it creates it.
`hostKey: managed` is refused on any stage but the one that makes a VM.

## Secrets

A secret reaches Ansible like this, on every tool runtime:

1. The controller releases them to the `playbook` attempt only, recording each in
   `SecretResolution`, with versions pinned before the run's first attempt.
2. The agent writes one JSON document to the **stdin** of `octopushy-ansible`, a small program
   in the image. Nothing is in argv or the environment, and `podman run` gets only `-i`.
3. It loads the SSH key into a private `ssh-agent` with `ssh-add -`, so the key is never a file.
4. It writes secret vars and `secretFiles` to a mode 0700 directory under `/tmp`, and refuses to
   run unless `/tmp` is a tmpfs. In a container that is the private tmpfs the runtime
   creates. The internal agent gets one from `compose.yaml`.
5. Ansible reads the vars with `-e @<file>`. Values are written with YAML's `!unsafe`, so a
   password holding `{{` is not treated as a template.
6. The program removes the directory when Ansible ends. A hard kill loses the container and
   its tmpfs. With the `local` runtime, the agent removes what the program left.

Ansible runs with `ANSIBLE_PIPELINING=True`, so module arguments go over the SSH channel
rather than through temporary files on either side, and with `ANSIBLE_NO_TARGET_SYSLOG=True`,
so they do not reach the guest's journal. `ANSIBLE_LOG_PATH`, keeping remote files and
verbosity are fixed too. A recap callback records counts, the failed task's name and the
declared outcome, and **never a task's result**. A task's name is templated, so it can hold a
secret even with `no_log`. The recap therefore stays in memory, and the callback hands it to
the agent as one control line on stderr when the playbook ends. A run that dies leaves none,
which makes it uncertain, and there is no file to go stale or to outlive the run. A recap
decides an attempt's outcome, so it is not a log line: it is never logged, it is parsed as
written so masking cannot rename a field, and its text is masked and only then cut to
size. An undeclared outcome's text is never repeated at all. Its size is bounded in bytes as
written, so a long name in any alphabet leaves a shorter note and never loses the recap.

**Masking.** Every line of tool output and every error is masked with the attempt's secrets.
The check of multi-line values for this design found three gaps, all fixed here:

- A long line was cut at 16 KiB **before** masking, so a secret across the cut stayed in
  the log. It now masks before cutting and again after. A line over 32 KiB is dropped with a
  note, because the masker cannot see a secret that runs past what was read.
- Control characters were removed before masking, so a value holding a tab no longer
  matched. The same reordering fixes that.
- Ansible's default output is JSON, which shows a newline as `\n` and a quote as `\"`. A whole
  private key is still masked, line by line. A password holding a quote or backslash was
  not. The masker now also matches each value as JSON escapes it.
- A value padded with spaces reached the log through a task's name, because Ansible strips the
  name for its banner. The masker now also matches a value without the space around it.

The first line of defence stays with the playbook: **tasks that handle secrets need
`no_log: true`.** Masking recognizes literal text. It cannot recognize a value that a
playbook encoded, split or hashed, and a line of a multi-line value shorter than four
characters is not masked on its own, because that would match ordinary text. Output is
masked a line at a time, so those lines are the only form a value printed across lines
takes. The controller's second pass covers each line of a multi-line value as well.

## Trust

A lab repository can now run arbitrary code as root on a guest. [ADR 0004](adr/0004-guest-configuration-trust.md)
has the reasoning; the result:

- **No per-commit gate.** ADR 0002 rejected one as too costly and accepted that tool-native
  code runs with credentials once merged. Playbooks are the same kind of thing as an
  OpenTofu module, which already runs at plan time.
- **Installation trust still applies.** A guest of an untrusted installation, or one whose
  endpoint changed, gets no configure run, because the host running it is the
  same trust domain as its provisioning.
- **One new gate, and it is the host key.** Credentials go only to endpoints an operator
  trusted. For a guest, the endpoint's identity is its host key: Octopushy's own, which it
  delivered, or one an operator set out of band.
- **What the pin does not do.** It stops a machine on the network answering as the guest,
  and it stops a setting in a play or an `ansible.cfg` from turning the check off: the
  connection is pinned with extra vars, including the `ssh` binary. It does not stop hostile
  repository code. A local task runs on the agent with the SSH agent's socket and the
  run's secrets within reach, so it can call `ssh` however it likes, and it can write a
  false recap to the same stderr. That is what accepting tool-native code means, and
  reviewing commits is the defence against it.
- **A narrower blast radius than the store.** Definitions cannot name a `system/` key or an
  installation's Proxmox token as an `access` key or a setup secret; both fail validation.
  That check reads today's definitions, and a removed installation's token stays stored,
  so the controller also refuses, at every release, a key any installation last declared,
  including one that is gone, and a key that has ever gone to Proxmox or OpenTofu work. A key
  therefore cannot be shared between an OpenTofu backend and a playbook: give each its own.
  One case stays open: a token an installation stopped naming, in a later commit, before it
  was ever used. Revoke credentials you retire.
- **Code runs before approval.** `preview` and `prepare` run repository code with no
  secrets, like `tofu plan`. In a container that code sees only its workspace. With the
  `local` runtime it runs as the agent's user, in the agent's container, where the agent's
  identity also lives. That is true of OpenTofu there today and is the reason to give
  work on sensitive guests to an agent on its own VM.

## Image

The image is `docker.io/skorcius/octopushy:ansible-<ansible-core>-<hash>`.

- **What is in it.** `agent/ansible/`: a Containerfile on Debian trixie-slim, `ansible-core`
  and its dependencies installed from a lock with `--require-hashes`, `openssh-client`,
  `octopushy-ansible`, and the recap callback. No collections: the `nodes` playbooks use only
  `ansible.builtin`, and a repository that needs more vendors them beside its playbooks, or
  names its own image with `image:` on a workflow's stage. Nothing is installed during a run.
- **How it is pinned.** The tag's hash covers every file in `agent/ansible/`, so changed
  content is a new tag, and `images.yml` never overwrites one. The server's default,
  `Tools:AnsibleImage`, has to match, and `scripts/checks/offline.sh` fails if it does not.
- **How it is published.** The `images.yml` workflow builds it on a push to `main`, beside
  the three other images.
- **How an agent gets it.** With `podman` or `docker`, `--pull=missing` fetches it from
  Docker Hub on first use. No agent ever builds anything, so a host with no build tools needs
  only a container runtime and a route to Docker Hub. A failed pull fails the attempt
  before anything happens on the guest.
- **The internal agent has no container runtime.** It runs tools with `--tool-runtime local`
  inside its own container, so the agreement's "pinned image the agent starts" cannot apply
  to it. `agent/Containerfile` copies the same locked environment into the agent image, as
  it already does for OpenTofu. There, the agent image is the pin: `octopushy-update` moves
  it, and a stage's `image:` has no effect and is logged as ignored. This adds about 150 MB
  to the agent image.
- **Footprint.** Ansible runs with one fork. A 120-task playbook with fact gathering peaked
  at 109 MiB, measured on a local connection, so each `ssh` it runs comes on top. Nothing
  stays in memory between runs. The agent announces `ansible` only if the tool works, so
  an older agent is never assigned one.

## How this fits other work

**The Technitium work** ([PR 21](https://github.com/jcarcaboso/octopushy/pull/21)) added `guestType`, a recipe
`secrets` list, `provision.inputs` and `provision.secrets`. Workflows keep the same shapes:
a guest's `inputs` and `secrets`, and a workflow's `secrets` list, given to its stages. Its `technitium` executor stays what it is: a product API, not a guest
configuration seam.

**One seam for installing on a guest.** The generic "install software on an LXC" request, with
`ha-db` first, needs no second mechanism: `type: container`, an `sshd`, and a workflow with a setup stage.
That branch's container already installs over SSH, through OpenTofu provisioners, which its
own documentation says cannot authenticate who answers, so it seals what it sends. A plan
noted on November 1 extracts that into a generic repository install module. Doing so would
leave two ways to declare what runs on a guest, and the second has no pin, no preview
of tasks, no declared outcomes, and secrets that pass through OpenTofu. **The recommendation
is to build `ha-db` as a workflow's setup stage instead.** It reuses `secrets`, the pin and
the approval flow. What it needs from that branch is `type: container` and a root SSH key in
the container, and neither depends on the install module. The services layer being built on
this executor settles that question in the same direction.

- **Host keys on a container.** Proxmox generates them when it creates the container, and
  the create task's log shows the fingerprint, which is a Proxmox-authenticated source
  with no console step. Check this before relying on it.
- **Protected secrets.** That work binds a grant to a digest of the code a secret may run
  with. `ConfigureInputs` produces such a digest, and a grant could bind to it. Its
  `ReviewedPaths` would need the project root, or the run receives no playbooks.
- **Landing order.** This first, because `pve01-ai` waits on it and that work does not wait on
  this. Neither depends on the other, and a trial merge conflicts in 21 files, almost all of
  them lists both sides add to: `ActionFor`, the executor list, schema enums, and generated
  files. Whichever lands second regenerates the OpenAPI document, the frontend client and
  the EF model snapshot. One thing needs thought rather than a merge: a secret protected
  by that branch's grants reaches a configure run only once `BuildAnsibleAsync` passes it a
  code digest. Until then it fails closed.

**Replacing a protected VM** (PR 19, merged) meets this at one point: a provision run that
replaces a VM resets the guest's proposed configuration, so the new VM is configured, and
its new host key fails closed until the pin is updated, unless the key is Octopushy's, whose pin moves
with the apply that made the new machine. A configure run has no plan and
never lifts protection, so none of that logic applies to it.

## What this does not do

- No host key for a machine Octopushy did not create, other than the one a person pins.
- No container guests until `type: container` lands.
- No drift detection. A run shows what happened when it ran, not what is true now.
- A deferred run waiting for approval holds the guest until someone decides. There is no
  timeout, because a run that is holding a real outcome should not disappear by itself. A
  [scheduled run](#schedules) does not wait.
