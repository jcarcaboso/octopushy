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
  sshKey: { store: access/pve01-ai/ssh-key }
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
| A schedule | the one chosen | Later slice |

Each input hash gets one automatic run. A failure is not retried by itself: it waits for a
new commit or a person. A guest that is not in sync with its provisioning waits for that
first, so one commit that changes both runs them in order. A guest whose recipe declares no
`onChange` action is only ever run by hand. Automatic runs use the head of the branch, so a
rollback configures the commit it returned to. A person cannot start a run on a guest whose
provisioning has not caught up either.

A schedule is a later slice because it needs a cron evaluator, missed-run rules and a
maintenance-window decision, none of which the first consumer needs on day one. Nothing
closes it off: a schedule would call the same `Planner.ConfigureGuestAsync` with the reason
`schedule`, which `Run.Reason` already documents, and a field on a recipe action is a
compatible addition. Until then the daily 06:00 `update-tools` stays in Jenkins or is
pressed by hand.

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

- **`policy.approval: manual`** waits for approval on every run, automatic or manual,
  including a scheduled one later.
- **`policy.destructive: manual`** waits only for an action declared `destructive: true`.
  Rule 11 says Octopushy decides from the preview, but a playbook has no preview that shows
  deletes. So the recipe states it, and `destructive` is required on every action: a
  forgotten declaration fails validation rather than running unattended. This is a claim
  by the repository, not something Octopushy verifies.
- **A rebuilt machine** is a provision run. It follows that run's rules, including those of
  [PR 19](https://github.com/jcarcaboso/octopushy/pull/19) when it lands.

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
promotes nothing.

### Outcomes and failure

| What happened | Step | Run |
|---|---|---|
| Exit 0 | `succeeded` | `succeeded`, snapshot promoted |
| A task failed (exit 2, recap present) | `failed` | `failed`. The change is partial and known; run again |
| The tool never started a task: pull failure, syntax error, host key or login refused on the first connection | `failed` | `failed`. Nothing happened on the guest |
| Unreachable after a task started, even if an outcome was reported first | `uncertain` | `uncertain` |
| Killed, timed out, or ended without a recap | `uncertain` | `uncertain` |
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
the run, with the outcome's name in its detail, so it never reads as a plain failure.

Exit 75 from the `nodes` wrapper does not carry over: that wrapper invents the code by
searching the log. A port replaces it with the `set_stats` task above.

**Long runs and reboots.** `timeoutMinutes` defaults to 120 and allows up to 1440. A timeout
stops the run and is uncertain. `ansible.builtin.reboot` works: the host key is unchanged
and nothing needs the connection to persist. The attempt keeps its lease through the agent's
heartbeat however long it runs. The agent's 30-minute stop grace period is shorter than
many runs, so updating the agent during one makes it uncertain. Configure attempts do not
count against a Proxmox host's `concurrentOperations`, because they do not touch Proxmox,
but they do take an agent slot: the default is two.

## Access

### SSH identity

`access.sshKey` is a store reference to a **secret** entry holding an unencrypted OpenSSH
private key. `access.user` is the login, and `become` is the playbook's business. The key
reaches only this guest's `playbook` steps: never `preview`, and no other kind of step.

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
are confirmed against the policy in `selinux-policy-targeted-44.11`. So:

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
   VM, and again after a rebuild. It writes to the guest, briefly. The policy allows the
   password change (`virt_qemu_ga_t` can enter `passwd_t`), but this has not been tried on a VM.
2. **For new VMs, have cloud-init leave a copy the guest agent may read.** A cloud-config
   with `runcmd: [[install, -Dm0644, /etc/ssh/ssh_host_ed25519_key.pub, /etc/octopushy/ssh-host-ed25519.pub]]`
   creates a file labelled `etc_t`, which `virt_qemu_ga_t` can read with no policy change.
   Then `qm guest exec <vmid> -- cat /etc/octopushy/ssh-host-ed25519.pub` gives the key with
   no console password. This needs a cloud-config snippet, which the current `proxmox-vm`
   module does not use, and the read is by hand for now. That the guest agent may read such
   a file comes from the policy's rules, not a test on a VM. An agent step could make it
   automatic later and write to the same pin; nothing here would change.

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
secret even with `no_log`. The recap therefore stays in the private directory, and
`octopushy-ansible` hands it to the agent as the last line of stderr, where the logger
masks it like any other line. It never reaches the workspace, which can outlive a run
for as long as an agent is disconnected.

**Masking.** Every line of tool output and every error is masked with the attempt's secrets.
The check of multi-line values for this design found three gaps, all fixed here:

- A long line was cut at 16 KiB **before** masking, so a secret across the cut stayed in
  the log. It now masks before cutting and again after.
- Control characters were removed before masking, so a value holding a tab no longer
  matched. The same reordering fixes that.
- Ansible's default output is JSON, which shows a newline as `\n` and a quote as `\"`. A whole
  private key is still masked, line by line. A password holding a quote or backslash was
  not. The masker now also matches each value as JSON escapes it.

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
  run's secrets within reach, so it can call `ssh` however it likes. That is what
  accepting tool-native code means, and reviewing commits is the defence against it.
- **A narrower blast radius than the store.** Definitions cannot name a `system/` key or an
  installation's Proxmox token as an `access` key or a setup secret; both fail validation.
  That check reads today's definitions, so the controller also refuses, at every release,
  a key that has ever gone to Proxmox or OpenTofu work. A removed installation's token
  stays stored, and this is what keeps it out of a playbook. It also means a key cannot be
  shared between an OpenTofu backend and a playbook: give each its own.
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

**The Technitium branch** (`t3/deploy-technitium-proxmox-lxc`) adds `guestType`, a recipe
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
the container, and neither depends on the install module. This is the decision to settle
before that stacked work starts.

- **Host keys on a container.** Proxmox generates them when it creates the container, and
  the create task's log shows the fingerprint, which is a Proxmox-authenticated source
  with no console step. Check this before relying on it.
- **Protected secrets.** That branch binds a grant to a digest of the code a secret may run
  with. `ConfigureInputs` produces such a digest, and a grant could bind to it. Its
  `ReviewedPaths` would need the project root, or the run receives no playbooks.
- **Landing order.** This first. It adds one operation and touches the run engine in a few
  lines, while that branch is on hold and much larger. Rebasing that branch means small
  conflicts in `RunContext`, `AssignmentBuilder.ActionFor`, `CredentialBindings.Validate`,
  the schemas, and the EF model snapshot, whose migration must be regenerated.

**PR 19** changes how a provision run replaces a protected VM. It meets this at one point:
a replacement resets the guest's proposed configuration, so a new VM is configured, and the
new host key fails closed until the pin is updated. Both add to `RunContext` and
`ActionFor`; neither order is harder than the other.

## What this does not do

- No schedule, as above.
- No host key read by the agent. Route 2 above is by hand.
- No container guests until `type: container` lands.
- No drift detection. A run shows what happened when it ran, not what is true now.
- A deferred run waiting for approval holds the guest until someone decides. There is no
  timeout, because a run that is holding a real outcome should not disappear by itself.
