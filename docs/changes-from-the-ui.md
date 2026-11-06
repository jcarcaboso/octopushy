# Changes from the UI

**Decided by the owner on November 3, 2026** ([D6](workflows-migration.md#decisions)): every change made in the UI is a
commit that Octopushy writes to the lab repository. It then syncs at once, and the normal process runs: validation, plan,
approval as the policy says, apply, verify. Git stays the only source of truth. Nothing a form changes lives only in the
database, so there is no drift to reconcile.

This page is Phase 3A and 3B of the [workflows migration](workflows-migration.md#phase-3-change-from-the-ui-and-one-click-deployment):
changes to what exists, and [the workflow wizard](#the-workflow-wizard), which deploys something new.

## What a change is

A **change** is a structured edit of one guest or service, made in a form and written as one commit. There are five kinds:

| Kind | What it writes |
|---|---|
| `edit` | A guest's `hardware`, `network`, `inputs` and `workflow`, or a service's `inputs` and `workflow` |
| `upgrade` | An edit that moves `workflow:` to a newer major of the same workflow, such as `fedora-vm@1` to `fedora-vm@2`, with any inputs the new major needs |
| `remove` | Deletes a guest's folder. Octopushy then proposes the guest's retire run, under its approval and destructive policy, as for a folder deleted in Git |
| `create-guest` | A new guest's folder with its `guest.yaml`, from [the workflow wizard](#the-workflow-wizard) |
| `create-service` | A new service's folder under a guest, with its `service.yaml`, from the wizard |

A change touches only files inside its resource's own folder: `guest.yaml`, `service.yaml`, or for a removal everything under
the guest's folder. It never touches `octopushy.yaml`, an installation, a lab, or another resource. Secrets, schedules,
access and policy are not in the forms: edit those in the [repository editor](repository-editor.md) or in Git.

On the guest and service pages, **Edit** opens the form, **Upgrade to `<id>@<M+1>`** appears when a newer major is released,
and on a guest **Remove** offers the removal. Each goes through the same steps:

1. **Form.** It is rendered from the workflow's inputs schema with its [`ui` hints](workflows.md#the-form-ui-hints), prefilled
   from the definition at the head commit. That commit is the change's *base*: the form remembers it.
2. **Review.** Octopushy builds the commit without pushing it and shows the exact diff Git will record, the commit message,
   and its validation. Issues the server finds are shown next to their fields, and the form stays open to fix them.
3. **Commit change.** The commit is pushed, the repository is synced, and the page moves to the change.

An **upgrade** first tries the current values against the new major's schema. If they validate, the review opens at once;
if not, the form opens with the new major's schema and its issues, so the inputs are fixed in the same change.

## The commit

A change goes through the editor's publish path, the only way Octopushy writes to Git:

1. **The change is recorded first**, as `committing`. Whatever happens to the request after that, the branch never moves
   without a change on record that says why: see [reconciliation](#when-a-request-ends-mid-push).
2. A private draft ref, `refs/octopushy/drafts/<change id>`, is made at the base commit, and the edit is written there.
3. The branch is fetched, and the draft is merged onto it if it moved since the base (`git merge-tree`).
4. **Compare-and-swap.** If any file the change touches differs between the base and the branch now, the change is refused
   with *"pve01-ai changed since the form was loaded: …/guest.yaml is different at 1a2b3c4d5e. Reload it and make the change
   again."* Commits that touch other files do not refuse it: they are merged.
5. **Observed only.** If the server's record of the installation says it is observed only, or its definition in the
   commit about to be pushed says `manage: false`, the change is refused. This is checked on every attempt, against what the
   change would be merged into, not against the commit the form was loaded from.
6. **Validation.** The result is compared with the branch as it is: only *new* errors refuse it, as in the editor, so a
   problem elsewhere never blocks an unrelated change. The refusal lists the issues and places them on fields.
7. **Push** with `--force-with-lease`. Right before each attempt's push, the commit it prepared, its **candidate**, is recorded
   on the change; the lease loop may merge again, so a change keeps every attempt's candidate. If the branch moves during the
   push, steps 3 to 7 are tried again, up to three times. It never force-pushes. The draft ref is removed whatever happens,
   never on the request's cancellation.

**A push whose acknowledgement was lost is a success.** Before an attempt is retried or refused, Octopushy checks whether an
earlier attempt's candidate is reachable from the branch head; if it is, that is the change's commit. So a remote that took
the update and then failed to say so never makes the change look stale. After a push that reports failure, the branch is
fetched: a candidate on it is success; none on it leaves the change `unknown`; a branch that cannot be read leaves it
`uncertain`. Neither is a refusal, and reconciliation settles both.

The commit is **authored** by the signed-in operator (`<name>@octopushy.invalid`) and committed by "Octopushy editor". Its
**message** says what changed, leaf by leaf, with a trailer naming the change:

```
pve01-ai: memory 4096 → 8192

Octopushy-Change: 019a4f1e-6b3c-7d2e-9f10-2a3b4c5d6e7f
```

With more than one difference, the subject names the first three and the body lists them all.

### When a request ends mid-push

A change whose push was attempted and not confirmed is settled by **reconciliation**: `committing` ones whose request was
cancelled or whose server stopped mid-push, and `uncertain` and `unknown` ones. Every sync does it right after fetching the
branch, and so does the first sync at startup.

- **A change is found only by its own candidate commits**, never by a commit's message: a commit that quotes a change's id is
  not that change's. One of its candidates reachable from the branch head makes it `committed` with that commit.
- **A commit found this way is held to what the change wrote,** as every commit is (see [which revision](#where-a-change-stands)):
  if the branch has other values by then, the change is `superseded`.
- **None reachable makes it `unknown`**, *"not on the branch (yet)"*, and every sync looks again, so a late arrival is still
  taken. There is no timeout: a force-push, a clock that jumps or another instance would make one wrong.
- **A committed change is checked too, until its follow-through ends.** If the branch no longer reaches its commit, as after
  a force-push, it is `unknown` again, and is found again only if one of its commits comes back.
- **The batch is picked before the branch is fetched,** under the engine's gate, so every commit it holds was pushed before the
  fetch began. A change pushed while a sync waits is never judged against a head that predates it.
- **Each sync checks a bounded batch** (`Octopushy__Changes__ReconcileBatch`, 100 by default), the changes checked longest
  ago first, so every change is reached in turn. The Git ancestry checks run outside the engine's gate: the batch is picked
  under it and the results applied under it, and a change that moved in between is left for the next sync.
- **A `committing` change with no candidate** never reached a push, so it is `refused`: nothing was committed.

An `unknown` or `uncertain` change can be **dismissed** from its page, once you know its commit will not arrive. Dismissing
fetches the branch first and is refused while one of its candidates is on it; it records who dismissed it and when.

**A dismissal only stops the waiting. It is not a guarantee that nothing was committed.** A push was attempted, and a push
under way can still land after the branch was read; a dismissed change keeps being checked, and one of its commits that
arrives makes it `committed` again, followed like any other, with who dismissed it kept on record. So a dismissed change is
neither final nor `notCommitted`.

### Which transitions are final

| Status | Final | Means for what the change prepared, such as generated values |
|---|---|---|
| `refused` | yes | **Definitely not committed**: refused before any push was attempted. The only such state |
| `applied`, `failed`, `no-change`, `superseded` | yes | Committed: it is in Git |
| `committing`, `uncertain`, `unknown`, `dismissed` | no | Not settled: a push was attempted, and its commit may still be on the branch |
| `committed`, `synced`, `planning`, `waiting-for-approval`, `applying` | no | Committed, and being followed |

The API says so in `final` and `notCommitted` on each change. `notCommitted` is true only for a change `refused` before any
push attempt, so cleanup that must happen only when nothing was committed waits for that, and never for a dismissal.

Right after the push, Octopushy **syncs the repository at once**, as **Repository → Sync now** does, without waiting for the
poll interval. A failed sync leaves the change committed; the next poll picks it up.

### The YAML keeps its comments

`guest.yaml` is rewritten so that only the changed values move (`YamlEditor`):

- A changed value is replaced where it stands, in its own quoting style, so the comment after it stays.
- A key removed from a block mapping takes its own lines with it. A new key goes after the mapping's last entry, at its
  indentation, in block style.
- A block sequence keeps the items it still has, drops the ones at its end, and gains new ones after its last.
- Comments, blank lines, key order and everything the form does not change stay exactly as they were written.
- An explicit `null` stays `null`. Only a key the form leaves out is removed.

Two constructs are written again rather than edited in place, and the review shows exactly what that does:

- **A flow collection whose keys or length change**, such as `network: { bridge: vmbr0, mode: static }` gaining `vlan`, is
  written again on one line, in flow style and in its original key order. A comment inside the braces would be lost.
- **A value whose type changes**, or a block collection that becomes empty, is written in flow style, such as `inputs: {}`.

The edited text is read back and compared with what the form asked for. An edit that cannot be made reliably, such as one
through YAML anchors and aliases, is refused with a pointer to the repository editor rather than written wrong.

### Revisions are applied in one transaction

A sync that moves the head to a revision records the revision, moves the head, and applies its inventory (the desired inputs
of every resource and the runs they propose) in one database transaction, and marks the revision `inventory_applied_at` in
it. A crash between those steps used to leave a head whose runs were never proposed, which the next sync then skipped; a
change could end `no-change` with zero runs. Now a crash leaves nothing, and the next sync, the first at startup included,
applies the revision. A head revision without the marker is applied again.

Applying it again proposes what has no run yet, and nothing twice. Each new piece of desired work a resource records, a
provisioning or a removal, gets its own **occurrence** id (`resources.desired_occurrence`), and the run proposed for it carries
that id (`runs.occurrence`). The work counts as proposed only while a run with its occurrence exists, of any status, so work
cancelled on purpose is not proposed again. So:

- a later revision that changes nothing for the resource keeps the occurrence, and its run;
- coming back to inputs that ran before, as from 2048 to 4096 and back, is new work, and so is rolling the branch back to a
  commit whose revision is reused: an old run for those inputs does not stand in for it;
- a folder deleted, restored and deleted again is a second removal, whatever the clock said in between, and even when the
  restored definition was invalid and nothing was provisioned: a resource that comes back loses its removal's occurrence;
- a run planned by hand (**Plan now**, **Plan removal**) for the work already recorded carries that work's occurrence, so
  cancelling it on purpose is not undone by the next sync;
- work recorded without its run is proposed, keeping its occurrence, even by a later revision that changes nothing for it.

The migration that adds occurrences gives each resource's current work one, and stamps it only on the run that clearly stood
for that work: the latest run matching it by the earlier rules (a retire run made since a deleted guest went missing, or a run
with those inputs for the revision that recorded them), and only if no provision or retire run with other inputs came after
it. After a rollback to the same commit the original run is history, so nothing is stamped and the next sync proposes the work. The migration that adds the marker sets it
on every existing revision except the head, which the next sync, the first at startup included, applies again in this way.

## What it needs

Committing from the UI uses the same switch and credential as publishing drafts:

```
Octopushy__RepositoryEditor__PublishEnabled=true
```

**The repository credential must be able to push** to the branch. For GitHub, that is a deploy key of `homelab-config` with
**write access** ("Allow write access"), set as the repository credential under **Repository**. A read-only deploy key fetches,
but every change is then refused at the push, with Git's message. Without `PublishEnabled`, the forms open read-only and say
why.

A guest or service under an installation with `manage: false` cannot be changed: it is invalid anyway, and the form says
*"home/ziost is observed only (manage: false): Octopushy changes nothing there, so nothing under it can be changed here."*
See the [safety boundary](safety-boundary.md).

## Where a change stands

Each change is recorded in the `changes` table: its id, kind, resource, author, base commit, commit, the revision it became,
the runs that revision proposed for the resource, and its status. The record says who asked for what and where it got to;
the definition itself is only ever in Git.

| Status | Means |
|---|---|
| `committing` | Recorded, and its commit being pushed; reconciliation settles it if the request ends first |
| `uncertain` | A push was attempted and the branch could not be read to see whether it landed; reconciliation settles it, or a person dismisses it |
| `unknown` | A push was attempted and none of its commits is on the branch (yet); each sync looks again, until one is found or a person dismisses it |
| `committed` | Pushed; no revision holds it yet |
| `synced` | The first revision that contains the commit is loaded |
| `planning` | A run it proposed is planning or previewing, or is due and waits for its turn, such as a setup that waits for provisioning |
| `waiting-for-approval` | A run waits for an approval |
| `applying` | A run is approved and applying, or one succeeded and a setup it implies is still due |
| `applied` | Every run it proposed succeeded |
| `failed` | A run failed or was cancelled; or the revision it follows could not plan its run, such as a removal whose last definition is invalid, with the server's reason |
| `no-change` | The revision proposed no run for the resource, and the server recorded no problem for the resource at that revision: nothing a run is built from changed. Errors are kept per resource and revision, so a later commit's error is never this change's |
| `superseded` | A later commit set the resource otherwise before this change was applied, or for a removal defined it again. The change page names that commit. A superseded change is never shown applied by the later commit's runs |
| `refused` | Refused before any push was attempted, such as a stale form, one that does not validate or changes nothing, or a missing credential: nothing was committed |
| `dismissed` | A person stopped waiting for an `uncertain` or `unknown` change; the change records who and when. Not final, and not proof: a commit of it that arrives later moves it on |

**A revision not applied yet decides nothing.** While the revision a change follows has no inventory marker, an empty run list
there is not `no-change`: the change waits, `synced`, until the next sync applies the revision and proposes its runs. That only
happens while it is the head. Once the head has moved on, the change moves to the first applied revision after it that contains
its commit, held to what it wrote like any other (it is `superseded` if that revision has other values), and follows that
revision's runs, together with those of the unapplied revision that still stand for the resource's current work (the same
occurrence), which the successor keeps rather than proposing again. A run the successor superseded or replaced with new work
is dropped, so it never fails the change. The unapplied revision is never applied or marked.

**Which revision.** A change follows the first revision that contains its commit, looked up directly among the commits that
lead from it to the branch head, however many revisions came in between. That revision is accepted only if the
resource's definition there still carries the change, checked on the change's own commit too; when a later commit landed
before the sync, it is a descendant, checked the same way: each
section the change wrote (`workflow`, `hardware`, `network`, `inputs`) has the values it wrote, or for a removal the resource
is still absent, or for a deploy the new resource still exists with every value it wrote. Otherwise the change is
`superseded` by that commit. The same holds when a change's run is cancelled because a
later revision superseded it: the change moves on to that revision only if it still carries the change.

The runs a change follows are those its revision proposed for its resource: a guest's provision run, then its setup run once
provisioning has caught up; a service's pipeline; for a removal, the retire run planned from the guest's last applied
definition. A run someone starts by hand at the same revision is not the change's.

The **change page**, `/changes/<id>`, shows the commit (with a link to it when the repository is on GitHub), the revision,
and each run step by step, until it is applied or fails. An approval it waits for can be given there. The page polls for now;
live updates come with Phase 4. Guest and service pages list their recent changes.

## API

| Call | What it does |
|---|---|
| `GET /api/workflows[?id=]` | Each workflow: its released versions, what it manages, its description, and per version its inputs schema and `ui` hints |
| `GET /api/changes/form/{resourceId}` | What a form starts from: the base commit, the definition's sections, the resolved version's schema and hints, the newer major to upgrade to, and why the form is read-only |
| `POST /api/changes/preview` | The diff, message and validation of a change, with nothing committed |
| `POST /api/changes` | Commits the change and syncs; 409 with a reason when it is refused |
| `GET /api/changes/{id}` | The change and its status, brought up to date from its revision and runs |
| `GET /api/changes?resourceId=` | The newest changes of a resource |
| `GET /api/deploy/targets` | Where the wizard can deploy, at the head commit: installations, managed or not and why, their hosts as declared and observed, their guests, their labs' agent labels, and the templates |
| `GET /api/deploy/vmid?installation=&from=` | The next free VMID on an installation |
| `POST /api/deploy/preview` | A deploy's file, diff, message, validation and the values it would generate, with nothing committed or generated |
| `POST /api/deploy` | Generates the values, commits the deploy and syncs; 409 with a reason when it is refused |

A change request names the resource, the base commit, the kind, and each section it replaces:

```json
{
  "resourceId": "019a…",
  "baseCommit": "4b1d0c0ffee…",
  "kind": "edit",
  "hardware": { "cores": 2, "memory": 8192, "disks": [{ "size": 32 }] }
}
```

A section left out stays as it is. A refusal is `{ changeId, reason, message, issues, fieldIssues }`, where `reason` is
`disabled`, `observed-only`, `unavailable`, `stale`, `invalid`, `no-change`, `unsupported`, `push-failed`, or for a deploy
`exists` or `vmid-taken` (with `nextVmid`, the next free one), and each field
issue is a JSON pointer into `{ workflow, hardware, network, inputs }` with its message. A change refused once its edit was
made, such as a stale form or one that does not validate, is recorded as `refused` and has a `changeId`; one refused before,
because committing is off or the resource cannot be changed, is not.

## The workflow wizard

**Deploy**, in the navigation, opens `/deploy`. It is generic: it knows workflows, not what they are for, so an Octopushy
worker is the `octopushy-worker` workflow deployed like any other. Its steps:

1. **Workflow.** A card for each workflow from `GET /api/workflows`: its description, what it manages, the newest release
   of each major, and the major to use. Workflows with `manages: container` are not offered yet, and a deploy that names one
   is refused: a container guest is written in Git for now. Once it exists, its form edits it like a VM's. A deploy writes `workflow: <id>@<M>`, the newest `M.x`; **Pin** writes `@M.m`.
2. **Where.** For a VM: the installation, its host and the guest's name, the VMID and the template. Installations with
   `manage: false` are listed, disabled, with the reason. Hosts are those `installation.yaml` declares, with what the latest
   observation says of them; a host only observed is shown, disabled. For a service: the installation and the guest it runs on.
3. **Form.** The workflow's inputs schema with its `ui` hints, as for an edit, prefilled with the schema's defaults and what
   it requires.
4. **Secrets.** For each secret the workflow declares, or describes in `ui.secrets`, an existing store key, or a value
   generated when the workflow asks for one ([generated values](workflows.md#generated-values)). For a workflow with a setup
   stage, its login and the store key of the guest's host key, which the person sets after the first boot, suggested as
   `proxmox/<installation>/<guest>/host-key` ([store keys](workflows.md#store-keys)). A workflow whose
   machine stage has `hostKey: managed` starts that field empty: where the installation lists the host under
   `snippets.hosts`, the guest gets Octopushy's own host key, pinned with no person in the loop.
5. **Review.** The exact file and diff Git will record, the commit message, what will be generated and where it is stored,
   and the validation. A VMID taken since the wizard opened is refused here, with the next free one offered.
6. **Deploy,** with a box: **approve the plan in the same click**. Then the change page follows it, as for an edit.

A deploy is a change like any other: the same commit path, compare-and-swap, validation, trailer, immediate sync and change
page. Its commit adds only the new folder:

```text
labs/<lab>/proxmox/<installation>/guests/<name>/guest.yaml
labs/<lab>/proxmox/<installation>/guests/<guest>/services/<name>/service.yaml
```

and its message names what it is: `w2: deploy octopushy-worker@1 on home/pve01, host pve01, VMID 311`. The file is written
clean and commented: the header names the workflow, the VMID says it was free when deployed, the reference says how to pin
it, and each generated secret says so. A deploy is refused, with nothing committed:

- when the folder exists, at the commit the wizard was loaded from or on the branch when it is pushed (`exists`, `stale`);
- when the name is not a resource id: lowercase letters, digits and hyphens (`invalid`);
- on an installation with `manage: false`, saying so (`observed-only`), as every change there is;
- when its VMID is taken (`vmid-taken`), checked at the review and again at the commit the push lands on, with the next free
  one offered: the number is never changed after the review;
- when which VMIDs are free cannot be known (`vmids-unknown`): the installation has no successful, complete and recent
  observation, or a `guest.yaml` under it cannot be read ([generated values](workflows.md#generated-values)). The wizard says
  why and offers **Observe now**. **Known limitation:** an installation with any ACL on a single VM (`/vms/<id>`) or on a
  pool is always `vmids-unknown`, even for a full Administrator token, because such an ACL could hide VMs from the
  observation. The reason names the ACL paths. Keep the ACLs you need, and deploy to that installation in Git until a
  per-VMID availability check exists ([Not yet](#not-yet));
- when what it would commit is not what was reviewed (`stale`): the review returns the workflow release it resolved to and a
  digest of the files, and the deploy sends both back. A release is a tag's commit for a workflows repository, which pins
  its modules too. For a workflow kept in the lab repository, it is what its runs take, by the same definition as their
  hashes: its folder, and every file of every stage's root, such as a module under `modules/`. Before **every** push attempt, the commit it would land
  is read again: what the new resource's reference resolves to there, and its file there, must be what was reviewed. A
  workflow changed on the branch since the review, even without a sync and even at the same version, refuses it with *"The
  workflow release changed since the review … Review again."*. The installation is read from that commit too, so a host
  added since the review must be in the observation;
- when it does not validate, or a store key it would generate is already set (`invalid`, `exists`), such as one a guest of
  the same name generated earlier, or one in another lab on an installation of the same name: generated keys leave the lab
  out ([generated values](workflows.md#generated-values)).

A deploy is recorded `committing` before anything is generated or pushed, with a record of each value it will make. Each
part of a value is recorded in the same save that makes it: the enrollment token's id, and the store version it writes,
tagged with the value's own id, which the version keeps and nothing can set afterwards. A deploy's commit is found as every
change's is: by the candidate commits it
pushed. Its status follows the same model: `refused` when nothing was pushed, `uncertain` or `unknown` after a push attempt
until reconciliation finds its commit or a person dismisses it.

**Generated values are removed under one rule, whatever triggers it:** at every sync, against the branch head that sync has
just fetched, and right after a deploy is refused before any push. A value goes only when all of these hold:

- its change is final, so its status cannot change again. `dismissed` is not final: a dismissal proves nothing, since a push
  was attempted and one of its commits may still land. A dismissed deploy keeps its generated values, and its VMID, until
  its commit turns up and it is followed again, or it ends in another final status;
- no definition file at the branch head references its key: every `store` anywhere in `octopushy.yaml` and the YAML under
  `labs/` and `templates/`, read as plain YAML, valid or not. If one of those files is not YAML at all, nothing is removed.
  So a dismissed change whose commit turns up on the branch later keeps the secrets its definition uses, and so does an
  applied guest;
- no change that is not final references the key. That covers the keys its written files reference, recorded before its
  first push, so an edit of other sections that keeps a `store:` reference still counts. It also covers its files at each
  commit it recorded, pushed or about to be, as far as the mirror holds that commit. A change recorded before those keys
  were kept, whose commit is not in the mirror, stops all removals until it settles;
- no run that has not finished uses the key.

**What is not looked at:** drafts of the [repository editor](repository-editor.md), and branches other than the one
Octopushy reads. A generated key referenced only there can be removed; set it again under **Variables** before such a draft
or branch is merged.

What goes is only what the value owns: the store version it wrote, identified by the value's id that the version was created
with, while that version is still the one present. A key someone wrote since, a person or a later deploy of the same name, is
never touched, even with the same description: a key deleted and set again continues after the version it had, and its new
version was not created with the value's id. Its
enrollment token is revoked unless it was used: a used token stays, and removing it would not affect the agent it enrolled. A
key a run was given is left as it is: delete it under **Variables** once nothing uses it, and the record of what each run
was given stays. A person who deletes a generated key before cleanup does leaves it nothing to remove. Each removal is its own transaction, with a short lock
timeout. One that fails stays pending with its error, each sync tries it again, and the change page says *"2 generated
values still to remove"* until it is done.

A final deploy, whatever its status (`applied`, `failed`, `no-change`, `superseded` or `refused`), holds no VMID: one
applied is held by its definition from then on. Every other deploy holds its VMID, a dismissed one included.

A new guest or service is a resource once the revision with its folder is loaded, so a deploy's change has no resource until
it is synced.

### Approving in the same click

**Decided by the owner:** a deploy can include the approval. It records the operator's intent on the change, and that intent
is decided once, when the first plan of the deploy reaches its approval. It approves that plan, as the operator who deployed,
noted *"Approved at deploy"*, only when all of these hold:

- **The plan only creates.** Every change it lists is a create: no update, delete, replace or import, and at least one. For a
  service, only a pipeline made entirely of OpenTofu stages qualifies, each plan checked change by change. **Decided by the
  owner:** a pipeline with an Ansible stage is never approved at deploy. A playbook's preview says what it might change, never
  that it only creates, and the workflow's own `destructive` flag is not proof. Such a pipeline waits for approval, and says
  why: *"Stage deploy runs Ansible, whose preview cannot be shown to only create."*
- **It is the deploy's own plan:** the provision run, or for a service the pipeline, that the change's revision proposed for
  the new guest or service, and the first one.
- **What an approval meets holds:** the run is of the current head revision, its installation is trusted and not observed
  only, and the plan has the fingerprint an approval names.

Otherwise the run waits for approval as usual, and its approval step and the change page say why: *"Plan does more than
create; approve it yourself."* A plan the policy approves by itself needs nothing, and the change says so. The intent is
spent on that one decision: it never covers a later plan, a setup run, a retire, a removal or anything destructive.

## Not yet

- Power, snapshots and actions as recorded runs, and live updates (Phase 4).
- Editing secrets, schedules, access and policy from a form.
- Removing a service: deleting a service's folder removes nothing yet ([services](workflows.md#removal)).
- Approving a deploy's setup run in the same click: a guest's setup is a later run, so it waits for approval as the policy says.
- Deploying several guests at once, such as a cluster.
- An authoritative check that one VMID is free, independent of ACLs, such as Proxmox's `/cluster/nextid?vmid=<n>` asked by an
  agent before the commit. It would let the wizard deploy to installations with ACLs on single VMs or pools, which the
  observation-based check refuses today.
