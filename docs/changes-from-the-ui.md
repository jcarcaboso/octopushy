# Changes from the UI

**Decided by the owner on November 3, 2026** ([D6](workflows-migration.md#decisions)): every change made in the UI is a
commit that Octopushy writes to the lab repository. It then syncs at once, and the normal process runs: validation, plan,
approval as the policy says, apply, verify. Git stays the only source of truth. Nothing a form changes lives only in the
database, so there is no drift to reconcile.

This page is Phase 3A of the [workflows migration](workflows-migration.md#phase-3-change-from-the-ui-and-one-click-deployment).
The workflow wizard and generated values are the next part.

## What a change is

A **change** is a structured edit of one guest or service, made in a form and written as one commit. There are three kinds:

| Kind | What it writes |
|---|---|
| `edit` | A guest's `hardware`, `network`, `inputs` and `workflow`, or a service's `inputs` and `workflow` |
| `upgrade` | An edit that moves `workflow:` to a newer major of the same workflow, such as `fedora-vm@1` to `fedora-vm@2`, with any inputs the new major needs |
| `remove` | Deletes a guest's folder. Octopushy then proposes the guest's retire run, under its approval and destructive policy, as for a folder deleted in Git |

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

Applying it again proposes what has no run yet, and nothing twice: a resource's desired inputs count as proposed only while a
run for exactly those inputs exists, whichever revision recorded them. So work recorded without its run, a provisioning or a
removal, is proposed even when a later revision changes nothing for that resource. The migration that adds the marker sets it
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

**Which revision.** A change follows the first revision that contains its commit, looked up directly among the commits that
lead from it to the branch head, however many revisions came in between. That revision is accepted only if the
resource's definition there still carries the change, checked on the change's own commit too; when a later commit landed
before the sync, it is a descendant, checked the same way: each
section the change wrote (`workflow`, `hardware`, `network`, `inputs`) has the values it wrote, or for a removal the resource
is still absent. Otherwise the change is `superseded` by that commit. The same holds when a change's run is cancelled because a
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
`disabled`, `observed-only`, `unavailable`, `stale`, `invalid`, `no-change`, `unsupported` or `push-failed`, and each field
issue is a JSON pointer into `{ workflow, hardware, network, inputs }` with its message. A change refused once its edit was
made, such as a stale form or one that does not validate, is recorded as `refused` and has a `changeId`; one refused before,
because committing is off or the resource cannot be changed, is not.

## Not yet

- The workflow wizard and generated values, such as the next free VMID and an enrollment token (Phase 3, next).
- Power, snapshots and actions as recorded runs, and live updates (Phase 4).
- Editing secrets, schedules, access and policy from a form.
- Removing a service: deleting a service's folder removes nothing yet ([services](workflows.md#removal)).
