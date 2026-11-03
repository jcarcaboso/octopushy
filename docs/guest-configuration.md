# Configuring a guest with Ansible

**Decided November 1, 2026.** A guest in the lab repository can be configured by Ansible,
from Octopushy, once it is provisioned. This page is the design: what a definition says,
what a run does, and what it trusts. It carries no knowledge of any one application.
[ADR 0004](adr/0004-guest-configuration-trust.md) records the trust decision.

`configure` is a recipe operation like `provision`. It is not VM-specific: a run reaches a
guest over SSH and never talks to Proxmox, so a container guest with `sshd` uses the same
definitions once the [Technitium branch](#how-this-fits-other-work) brings `type: container`.

## Definitions

A recipe names the tool-native project and what can be run from it:

```yaml
# recipes/ai-server/recipe.yaml
schemaVersion: 1
operation: configure
executor: ansible
root: ../../modules/ansible/ai-server   # the Ansible project: ansible.cfg, playbooks, roles
inputs: inputs.schema.json              # JSON Schema for the guest's setup.inputs
secrets: [ai_server_tailscale_key]      # what a guest must provide
actions:
  configure:
    playbook: playbooks/configure.yml   # relative to root
    prepare: playbooks/prepare.yml      # runs on the agent before approval; see below
    destructive: false                  # required: Octopushy cannot tell, so the recipe says
    onChange: true                      # runs when the guest or these files change
    timeoutMinutes: 180
  update-tools:
    playbook: playbooks/update-tools.yml
    prepare: playbooks/prepare.yml
    destructive: false
  update-system:
    playbook: playbooks/update-system.yml
    snapshot: applied                   # reuses what the last successful run prepared
    destructive: false
outcomes:
  deferred:
    description: Work is active on the guest. Approving interrupts it.
    approve:
      vars: { ai_server_allow_busy: true }
```

A guest points at it from `setup`, next to the existing `diskReporter`:

```yaml
# guest.yaml
setup:
  recipe: ai-server
  inputs:                               # extra vars, validated by the recipe's schema
    ai_server_hostname: pve01-ai
  secrets:                              # extra var name -> store reference
    ai_server_tailscale_key: { store: pve01-ai/tailscale-key }
  secretFiles:                          # extra var name -> a private file holding the value
    ai_server_git_private_key_src: { store: pve01-ai/git-ssh-key }
access:
  address: 10.15.60.10                  # defaults to the static or reserved address
  user: ops
  hostKey: { store: access/pve01-ai/host-key }
```

- **`provision` makes the machine, `setup` configures it.** They are separate runs on one
  resource, so the existing per-resource rule serializes them: a later run waits for an
  earlier one, and an uncertain run holds the guest until a person closes it.
- **`operation` selects the shape.** `provision` keeps its plan and apply. `configure` has
  the steps below. The guest never names an executor; the recipe does.
- **An action is the whole unit.** Octopushy passes no `--tags`, `--limit`, `--start-at-task`
  or `--check`, and no definition can add one, so the selective runs the `nodes` wrapper
  refuses cannot be asked for. The inventory holds this one guest.
- **Files.** The run receives the repository snapshot, like a plan does. `{{ octopushy.guest_directory }}`
  is the guest's folder in it, for files that belong to one guest.

Extra vars may not start with `ansible_` or `octopushy`: those are how Octopushy pins the
connection, and they are refused in `setup.inputs`, `setup.secrets`, `setup.secretFiles`,
`approve.vars` and whatever a prepare step returns.

### What starts a run

| Trigger | Action | Status |
|---|---|---|
| The first time a provisioned, verified guest has a `setup.recipe` | the `onChange` action | Implemented |
| A change to `setup`, `access`, the recipe, its `root`, or the guest's folder | the `onChange` action | Implemented |
| A provision run that creates or replaces something, such as a new VM | the `onChange` action | Implemented |
| **Configure** on the guest page, or `POST /api/guests/{id}/configure` | the one chosen | Implemented |
| A schedule declared on the guest | the one it names | Implemented, [below](#schedules) |

Each input hash gets one automatic run. A failure is not retried by itself: it waits for a
new commit or a person. A guest that is not in sync with its provisioning waits for that
first, so one commit that changes both runs them in order. A guest whose recipe declares no
`onChange` action is only ever run by hand. Automatic runs use the head of the branch, so a
rollback configures the commit it returned to. A person cannot start a run on a guest whose
provisioning has not caught up either.

### Schedules

**Decided November 2, 2026.** A schedule is declared in Git, on the guest, and runs one action:

```yaml
setup:
  recipe: ai-server
  schedules:
    - { action: update-tools, cron: "0 6 * * *" }
    - { action: update-system, cron: "30 6 * * sun" }
```

It belongs on the guest because one recipe serves machines with different maintenance times,
and a field on a recipe action would force one time on all of them. The expression has five
fields and is read in the lab's `timezone`, or UTC if the lab names none. When the clocks change, a
time in the hour they skip runs as they jump, and a time in the hour they repeat runs once, the
first time round. A schedule that runs every hour keeps going through both. A schedule that names
an action the recipe lacks, an expression that does not parse or never falls due, or a time zone that does not
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
| **Declared outcome**, such as `deferred`, on a recipe that lets a person approve it | **A scheduled run does not wait.** It ends as succeeded, naming the outcome and its detail, promotes nothing, and frees the guest. The next time tries again. A person who wants it now starts the action by hand, and that run does wait for approval. This rule is about outcomes with `approve`: one without it has nobody to wait for, so it ends as its `result` declares, as on any run. `failed` fails the run and promotes nothing, and `succeeded` ends it as succeeded and promotes what was prepared |
| **Failure** | Each time comes up once, whatever happens to it, so a failed run is not repeated until the next time. It stays on the guest's page and in **Runs**, with a link from the schedule |

**A schedule is not a window.** A schedule says when an action starts. `policy.windows`, which
is documented in the lab repository format and not built, would limit when any automatic
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

Ansible's check mode is refused, as the [Fedora module](../examples/lab/modules/ansible/fedora/README.md)
records: it is not an exact preview, tasks that depend on earlier tasks misreport, and
playbooks like the Fedora workflow assert against it. **Octopushy therefore does not show a
prediction of what will change.** It shows what will run, and binds approval to exactly that.

The `preview` step runs on an agent with **no secrets and no SSH key**, and never contacts
the guest. It runs `--syntax-check` and `--list-tasks`, then the action's `prepare` playbook
if it has one. An operator sees:

- the action, playbook, commit, tool version and image;
- the list of tasks, by name. Dynamic includes are listed as the include, not expanded;
- the files changed under the recipe and project since this guest's last successful run;
- the inputs, with their values, and the secrets, by name, store key and version;
- what `prepare` resolved, such as the versions an update would install;
- the SHA-256 fingerprint of the pinned host key.

All of this goes into one document, `preview.json`, whose SHA-256 the approval names. The
`playbook` step receives that exact artifact and refuses anything else, as an apply refuses
another plan.

- **`policy.approval: manual`** waits for approval on every run, automatic, scheduled or manual.
- **`policy.destructive: manual`** waits only for an action declared `destructive: true`.
  Rule 11 says Octopushy decides from the preview, but a playbook has no preview that shows
  deletes. So the recipe states it, and `destructive` is required on every action: a
  forgotten declaration fails validation rather than running unattended. This is a claim
  by the repository, not something Octopushy verifies.
- **A rebuilt machine** is a provision run. It follows that run's rules, including those of
  [replacing a VM](lab-repository.md#rules).

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
the first time it is needed. That work is a `setup.recipe` or an Ansible stage of one of its
[services](pipelines.md#services-on-a-guest) alike: the key follows who manages the machine, not what
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
- **It is unaffected by the refusal of Proxmox and OpenTofu credentials,** because only the
  public half goes to that work, as a variable, and is never a released secret.

`access.sshKey` remains for a machine Octopushy did not create: a store reference to a **secret**
entry holding an unencrypted OpenSSH private key. A guest that names one gets no managed key, and an
explicit key always wins. Octopushy puts its own key on a machine only through an apply of its
provisioning, so a service stage meant to log in with it, on a machine no such apply has carried it
to, is refused and told to name `access.sshKey`.

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
[rotation](progress-and-next-steps.md) is built, and that is also why the provisioning hash holds
no key. PR 21's containers take keys through the same `sshAuthorizedKeys` variable, so they need
nothing further.

**Adding a service with an Ansible stage to an existing guest delivers the key the same way.** A guest
that had no Ansible work had no managed key, so the service changes its provisioning hash: the guest is
provisioned again first, which for a VM is the in-place update and one reboot above, with new host keys,
and for a container is a replacement. The service waits for that. The owner's approach is to rebuild
guests rather than migrate them, so there is no migration path for existing guests.

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

`access.hostKey` is a store reference to the guest's public host key, in the form
`ssh-ed25519 AAAA…`. One entry per line is allowed. It is a required field, and a key that
is not set blocks the run with **Referenced but not set**. Octopushy writes a private
`known_hosts` from it and connects with `StrictHostKeyChecking=yes` and a null global
file. It **never scans** a host: the first thing it learns about a host's key is what an
operator put in the store.

The value lives in the store rather than in Git for the same reason trust does: a commit
may point a guest at a new address, but it cannot put a new key behind one. It can only
reference an entry an operator set. It has to be a plain entry, because a preview receives
it: a secret posing as a pin is refused.

A mismatch is a failed run that says so. A rebuilt VM has a new key, so its configure run
fails closed until someone sets the new one. That is the point, not a defect to smooth over.

**Where the value comes from, for a VM made by cloud-init.** The guest generates its key on
first boot, and the channels that could read it automatically are closed: on Fedora the
guest agent runs as `virt_qemu_ga_t`, which cannot read `sshd_key_t`, cannot run `ssh`, and
cannot connect to the SSH port even on loopback. That was observed in `nodes`, and the rules
are confirmed against the policy in `selinux-policy-targeted-44.11`. On November 2, 2026 the
first of these was also confirmed on a real Fedora 44 cloud image with SELinux enforcing, at
`pve01-ai`'s size, through the guest agent's own protocol: `guest-exec` of `cat` on the host key
is refused, and the same call on a copy labelled `etc_t` works. So:

1. **Read it through the Proxmox console.** Use the guest agent to set a short-lived
   password, which it is allowed to do, log in on the console, and read the key:

   ```sh
   qm guest passwd <vmid> ops                  # on the Proxmox host
   # then, on the VM's console in the Proxmox interface:
   cat /etc/ssh/ssh_host_ed25519_key.pub
   sudo passwd -l ops
   ```

   Put the line in Variables as a **plain** value. This is the route for a VM that already
   exists, such as `pve01-ai`. The chain of authentication is your Proxmox login, then the
   VM's console, with no network path to the guest involved. It costs a person a minute per
   VM, and again after a rebuild. It writes to the guest, briefly. The password change worked on
   that test VM through `guest-set-user-password`, which is what `qm guest passwd` sends; `qm`
   itself and the console login were not part of the test.
2. **For new VMs, have cloud-init leave a copy the guest agent may read.** A cloud-config
   with `runcmd: [[install, -Dm0644, /etc/ssh/ssh_host_ed25519_key.pub, /etc/octopushy/ssh-host-ed25519.pub]]`
   creates a file labelled `etc_t`, which `virt_qemu_ga_t` can read with no policy change.
   Then `qm guest exec <vmid> -- cat /etc/octopushy/ssh-host-ed25519.pub` gives the key with
   no console password. This needs a cloud-config snippet, which the current `proxmox-vm`
   module does not use, and the read is by hand for now. The read worked on that test VM, with
   the copy made by `sudo` rather than by cloud-init. An agent step could make it automatic
   later and write to the same pin; nothing here would change.

**The trade-off, plainly.** The pin is exactly as trustworthy as the channel it was read
through. Route 1 and 2 both anchor it in Proxmox: anyone who controls the hypervisor can lie
about the guest, and they already own it. With `tls.verify: false` on the installation, a
person using the Proxmox interface or API is trusting that TLS session too. What this rules
out is a machine on the network answering in the guest's place, which is the attack
host-key checking exists for. Copying the key from a laptop's `known_hosts`, or from
`ssh-keyscan`, is possible and Octopushy cannot tell the difference, but that is
trust-on-first-use: the first key seen is not authenticated. If you do that on an isolated
VLAN, it is your decision, and you should know that it is one.

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
  trusted. For a guest, the endpoint's identity is its host key, set out of band.
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
  names its own image with `image:` on the recipe. Nothing is installed during a run.
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
  it, and a recipe's `image:` has no effect and is logged as ignored. This adds about 150 MB
  to the agent image.
- **Footprint.** Ansible runs with one fork. A 120-task playbook with fact gathering peaked
  at 109 MiB, measured on a local connection, so each `ssh` it runs comes on top. Nothing
  stays in memory between runs. The agent announces `ansible` only if the tool works, so
  an older agent is never assigned one.

## How this fits other work

**The Technitium work** ([PR 21](https://github.com/jcarcaboso/octopushy/pull/21)) adds `guestType`, a recipe
`secrets` list, `provision.inputs` and `provision.secrets`. This design uses the same
shapes, `setup.inputs`, `setup.secrets` and a recipe `secrets` list, so one recipe format
covers both. Its `technitium` executor stays what it is: a product API, not a guest
configuration seam.

**One seam for installing on a guest.** The generic "install software on an LXC" request, with
`ha-db` first, needs no second mechanism: `type: container`, an `sshd`, and `setup.recipe`.
That branch's container already installs over SSH, through OpenTofu provisioners, which its
own documentation says cannot authenticate who answers, so it seals what it sends. A plan
noted on November 1 extracts that into a generic repository install module. Doing so would
leave two ways to declare what runs on a guest, and the second has no pin, no preview
of tasks, no declared outcomes, and secrets that pass through OpenTofu. **The recommendation
is to build `ha-db` as a configure recipe instead.** It reuses `setup.secrets`, the pin and
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
its new host key fails closed until the pin is updated. A configure run has no plan and
never lifts protection, so none of that logic applies to it.

## What this does not do

- No host key read by the agent. Route 2 above is by hand.
- No container guests until `type: container` lands.
- No drift detection. A run shows what happened when it ran, not what is true now.
- A deferred run waiting for approval holds the guest until someone decides. There is no
  timeout, because a run that is holding a real outcome should not disappear by itself. A
  [scheduled run](#schedules) does not wait.
