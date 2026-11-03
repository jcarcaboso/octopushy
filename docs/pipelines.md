# Pipelines

**Decided November 2, 2026.** The owner's rule: **Octopushy provides the infra to define the lab and run the
operations defined. It holds nothing service-specific.** No code in `backend/src`, `agent/crates`, `contracts` or
`frontend/src` names a service or a kind of service, such as Technitium, PostgreSQL or even DNS. Specifics live
in the lab repository, as recipes. What a service is, and what is on a VM or container, Octopushy infers from the
repository.

This replaces the service design with its own ports, DNS records and approvals in earlier drafts of
[lab-repository.md](lab-repository.md) and the [architecture agreement](architecture-agreement.md). It keeps what
held up from `docs/services.md` on PR 28: a service is a resource of its own, with its own status and history.

**What exists today** is [step 1](#build-order): stages, one approval, and services shown on guests. Anything
marked **Later** is the agreed design, not built. Its fields are refused, not ignored.

## The design

```yaml
# labs/home/lab.yaml                                        Later: interfaces are refused today
interfaces:
  dns: { recipe: technitium-dns }      # pihole-dns later, same inputs

# guests/ha-db/services/postgresql/pipeline.yaml
schemaVersion: 1
stages:
  - { name: expose, recipe: guest-firewall }
  - { name: deploy, recipe: postgresql }
  - name: dns                                              # Later: a stage with `interface` is refused today
    interface: dns
    inputs:
      records:
        - { type: A,     name: ha-db }
        - { type: CNAME, name: metrics, target: ha-db.lab }
inputs: { port: 5432, hostname: ha-db }

# labs/home/clusters/dns/cluster.yaml                       Later: nothing under clusters/ is read today
members: [home/ziost/dns-01, home/pve01/dns-02]
stages:
  - { name: join,   recipe: technitium-join }
  - { name: verify, recipe: technitium-verify }
```

### Stages

A service is an ordered list of stages. Each is a recipe, OpenTofu or Ansible. A service adds whatever it needs.
**Octopushy does not interpret ports or DNS: they are only inputs.** `expose` above means on the VM or container; the
recipe decides how, and a router is out of scope.

- **Inputs.** `inputs` at the top go to every stage, and a stage's own `inputs` go on top of them, replacing a name they
  share. They are checked against the recipe's `inputs` schema, so a recipe with `additionalProperties: false`
  has to declare everything the pipeline passes, or the pipeline has to set those on the stages that use them.
- **An Ansible stage** is a recipe with `operation: configure`, as for a guest's `setup`. It runs the action marked
  `onChange`, on the guest's connection: its address, user, SSH key and pin. `secrets` and `secretFiles` on the stage work
  as they do in `setup`. `{{ octopushy.guest_directory }}` is the service's folder.
- **An OpenTofu stage** is a recipe with `operation: stage`, which is a root module like a provision recipe's, but not
  `verify` or `guestFilesystem`. Its inputs are its variables. OpenTofu warns about one it does not declare, and goes on.
  It has a state of its own and, in step 1, **no credentials of any kind**: no Proxmox token and no backend environment.
- **Files.** Anything else in the service's folder reaches a playbook through `guest_directory`.

### One approval, first

Plan and preview every stage, approve once, then apply them in order. **Nothing changes before that approval.** A
service's run is one run, and its steps read as one timeline:

```
expose  plan
deploy  preview
        approval          one document, covering every stage
expose  apply
deploy  playbook
```

The approval is bound to **one document covering every stage**, as `preview.json` is for a configure run. Octopushy writes
it when the last preview ends. It lists each stage with the SHA-256 of the saved plan or preview that stage will apply, and
what the plan said. An approval names that document's SHA-256, and each apply is refused unless the document still has that
digest and names exactly the artifact it is about to send. The document is on `GET /api/runs/{id}`, under the step's
`approval.document`.

**The limit: an OpenTofu plan runs provider code before approval, with whatever credentials it holds.** "Nothing changes"
is a property of OpenTofu and of the provider, not something Octopushy can enforce on a plan. A provision plan holds the
read-only token for this reason. An OpenTofu stage holds nothing in step 1, so its plan has no token to misuse, but
[step 2](#build-order) gives stages secrets, and from then on a stage's plan runs with its own token before approval. An
Ansible preview has no such limit: it gets the public host key only, and never contacts the guest. A `prepare` playbook runs on
the agent, as it does for a guest.

A plan with no changes has nothing to apply: its apply step is skipped, and if no stage has anything to change the run needs
no approval. An Ansible stage always counts as a change, since nothing can show what a playbook will do.

A recipe with an outcome a person approves **cannot be a stage**: that would be a second approval, after something has
changed. A declared outcome with `result: failed` fails the stage, and `result: succeeded` passes it.

### Policy

**The default is automatic deployment.** With no explicit `policy` on a service, it takes what is configured, through
`PolicyResolver`: `octopushy.yaml` defaults, lab, installation, guest. A service adds only its own override, and the nearest
setting wins. If nothing is set anywhere, approval is `automatic`.

**Destructive plans keep today's `destructive` rules.** Checked against the code, those are:

- `policy.destructive` is `manual` or `automatic`. Unset, it takes the resource's approval value, so a manual resource never
  deletes without approval, and an automatic one deletes without asking.
- An OpenTofu plan is destructive if it has a pure delete. Under `destructive: manual` that waits for approval, whatever
  `approval` says.
- A playbook is destructive if the recipe declares its action `destructive: true`. Octopushy cannot tell otherwise. The same
  rule applies.
- A provision plan that replaces or deletes the guest's VM waits unless `destructive: automatic` is written down
  somewhere. That guard is about a VM's disks, so it applies to a guest's provision run and not to a stage.

**One difference, on purpose:** a plan's `replace` count does not reach that rule today, because the VM guard covers the one
replace that matters to a provision run. A stage has no such guard, so a stage's plan counts deletes and replaces together.
A stage that replaces something under `destructive: manual` therefore waits, where a provision plan replacing a non-VM
resource would not.

### No rollback

A failed stage stops the pipeline. The stages before it stay applied, and nothing undoes them: a later stage's
failure says nothing about an earlier one, and a recipe may not be safely reversible.

**Retrying continues from that stage without a new approval, unless the inputs changed.** Retry is on the failed step,
or `POST /api/runs/{id}/retry-stage`. It is refused, and a new run is needed, when:

- **The inputs changed,** or a newer run exists, or the service went missing or invalid. The approval was of something else.
- **A failed OpenTofu apply moved its stage's state.** OpenTofu will not apply a saved plan to a state that changed, and a new
  plan would be a document nobody approved. So this one takes a new run, which plans what is left. If the apply failed
  without changing the state, the same plan is used again.
- **A change may have been lost.** An uncertain attempt is closed by a person, as for any run, and cannot be repeated. A
  plan or preview that failed changed nothing, so Deploy is the way on.

Deploy on the service's page, or `POST /api/services/{id}/deploy`, starts a new run whatever the last one did.

### Later stages cannot use results of earlier ones

Every plan is made before anything applies, so a stage uses repository inputs only. There are no stage outputs. A value one
stage produces and another needs belongs in the repository, or on the machine for a later stage's playbook to read when it
runs.

### Readiness and turns

**Provision, then setup, then services.** A service waits until its guest is bound, its provisioning has caught up with its
definition, and its setup, if the recipe has an `onChange` action, has been applied at its current inputs. It shows as
*waiting*, not failed, and says what for.

**This is checked again at dispatch.** A run can wait a long time for its turn or for a person, and the guest can change
meanwhile: an approval says nothing about what has happened since. So every step that changes something re-reads the guest,
both when its attempt is created and when it is handed to an agent. Until it holds the run stays where it is, its approval
stays good, and the run says what it waits for. A change held at dispatch goes back to its run and leaves the queue, so it
cannot crowd out the guest's setup it is waiting for, and one that never reached an agent still gives way to new inputs.

**One change at a time on a guest, with its services.** Nothing that changes a guest starts while something else is changing it.
That covers only what changes something: a plan or preview can run beside it, and **a run waiting for approval does not hold
its siblings**, so a service nobody has approved cannot stop another from deploying. An uncertain run does hold them, until a
person closes it, because it may still be running. A guest's own run does not wait for a service's work that is only queued,
since that may itself be waiting for the guest.

**A failed service marks neither its guest nor its siblings.** A guest's setup failing does hold back its services, since they
would be deployed on a machine that is not prepared, and they say why.

**One automatic run per input hash.** The hash covers `pipeline.yaml` without its `description`, `policy` and `execution`, the
recipes of its stages and their files, the service's folder, and how the guest is reached. A failure is not repeated by itself:
it waits for a new commit or a person. A guest's setup changing does not run its services again, and a change to a service
does not plan or configure its guest. A provision run that creates or replaces something may have made a new machine, so its setup
and then its services are proposed again.

### Services on a guest

Services are read from the repository, from `services/<id>/pipeline.yaml` in the guest's folder. The address is
`home/korriban/krb-app-01/web`, and moving the folder makes a new resource. A guest's page lists them with their status and
last run, and each has a page with its stages, policy and history. `GET /api/services/{id}` has the same.

An invalid `pipeline.yaml` marks only that service. An invalid `guest.yaml` marks the guest's services invalid, and they are
not treated as gone. A renamed guest keeps its services and their state. A managed worker cannot have services, because it is
configured by its image.

**The key follows who manages the machine, not what kind of Ansible work runs.** Decided by the owner on November 3, 2026:

- **On a machine Octopushy provisions, any Ansible stage logs in with Octopushy's own key.** A service's Ansible stage counts
  exactly as a `setup.recipe` does, so a guest whose only Ansible work is a service gets a key of its own, as described in
  [guest-configuration.md](guest-configuration.md#ssh-identity). The private half reaches only that guest's `playbook` steps,
  never a preview or an OpenTofu stage, and a run binds only its own guest's key.
- **An explicit `access.sshKey` still wins**, and a guest that names one gets no key of Octopushy's.
- **A machine Octopushy does not manage has to name `access.sshKey`.** Octopushy puts its key on a machine only through an
  apply of that guest's provisioning, and never forces it on otherwise. A service whose stage would log in with Octopushy's
  key, on a machine no such apply has carried it to, is refused and told to name one, on Deploy and on the service's page,
  and no automatic run starts. That is what a guest whose provisioning caught up without an apply looks like, such as one
  whose recipe does not put `sshAuthorizedKeys` on the machine.

**Adding a service with an Ansible stage to an existing guest delivers the key the way a setup recipe does.** It changes the
guest's provisioning hash, so the guest is provisioned again first, and the service waits for that. For a VM that is an
in-place update and one reboot. A container's keys are `ForceNew`, so for a container it is a replacement. Existing guests
are rebuilt rather than migrated, so nothing more is built for them.

### Removal

**Later.** A deleted folder proposes a removal under the same single approval and policy, and removes only what recipes declare.

**Today a deleted folder is shown as *missing*, and removes nothing.** No run starts. Whatever the service deployed stays on
the guest, and its stages' OpenTofu state stays in Octopushy. Restoring the folder picks the service up again. Removing the
guest does remove the service as a resource, with what was inside the guest, and ends any of its runs still going. Anything
a stage made outside the guest stays there, since nothing plans its destroy yet.

## Later steps

### Interfaces

`dns` is a named slot bound to a recipe in the lab. Each recipe of a slot accepts the same inputs, so swapping one is one line.
This replaces `lab.dns`, which is accepted and ignored today.

The slot's schema lives in the repository, such as `interfaces/dns.schema.json`. Octopushy evaluates it with the existing
`SchemaCatalog.Evaluate` against the stage's inputs together with the guest's definition. That lets the repository's
schema require `network.mode: static` for an A record but not a CNAME, as an invalid definition at sync, with no knowledge of
DNS in Octopushy.

### Clusters

A cluster is a set of guests with stages applied to them, under one approval. A stage runs on each member in turn, starting with
the first listed, because a run connects to one guest with its own key and may bind only that key. A failed member stops the
rest. When a member is replaced, the cluster's pipeline is due again.

## Build order

1. **Stages with one approval, and services shown on guests.** Built.
2. **Interfaces, and secrets for OpenTofu stages.** `lab.dns` goes. A stage's plan gets its token here, which is the limit
   stated above.
3. **Containers.**
4. **Clusters.**

### What step 1 leaves out

- `interface` on a stage, `interfaces` on a lab, and any other field: refused by the schema. A `clusters` folder in a lab is
  not read, and the lab shows a warning.
- Secrets on an OpenTofu stage: refused. An external state backend for a stage: there is no field for one.
- `dependsOn` between services: refused like any unknown field.
- A list of what changed since the last success, which a configure run's preview has. A stage's preview does not have it.
- Choosing an action. A stage runs the recipe's `onChange` action, and the service's page offers Deploy only.
