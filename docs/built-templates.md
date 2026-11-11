# Built templates

**Status: implemented on the server, November 11, 2026;** the agent's `nix` executor and its use of `templateImage` are
being implemented separately. The proposal below is kept as written, with facts about the code before it from `8ed3ab9`
(0.7.3). Where the server differs from it, [As implemented on the server](#as-implemented-on-the-server) wins.

A template today is an image URL and its checksum, and Proxmox downloads the image itself. That works for a
distribution's published image, but not for an image we make: someone has to build it, host it at a URL every
Proxmox host can reach, and paste its checksum. This proposal lets Octopushy build a template's image from a Nix
flake output, keep it on the server, and hand it to Proxmox through the approved apply, with no URL and no manual
step.

The first user is a NixOS base image for the `nixos-host` workflow in octopushy-lab: one generic image, with each
guest's own configuration built on the guest afterwards.

## The shape

```text
lab repository                       server                          any free agent with nix
templates/nixos-base/                ─ sync: image for this key? ─┐
  template.yaml  (source.build)        no → queue a build ────────┼──► nix build <output>
  flake.lock     (the lab's pin)                                   │      stream it back, sha256
                                     images/<sha256>.raw  ◄────────┘
guest provision run
  plan   (read-only token)           template = { built, sha256, size }   no file needed
  apply  (privileged, approved)      agent fetches images/<sha256> ──► OpenTofu uploads it to Proxmox
```

1. **A template names a flake output instead of a URL.**
2. **Any free agent builds it.** No labels: building touches no installation, so it needs no reachability.
3. **The server keeps the image,** named by its SHA-256, and reuses it for every guest and installation.
4. **The guest's apply uploads it.** The agent that runs the approved apply fetches the image from the server, and
   OpenTofu uploads it to Proxmox as a resource in the guest's own state, as `proxmox_download_file` is today.

## As implemented on the server

What the server does, and where it differs from the proposal that follows. The wire contract is
`contracts/agent/v1/agent.proto` (`UploadImage`, `DownloadImage`) and `contracts/agent/v1/executors.md` (`nix`, and
"A built template's image" under `opentofu`).

**The template.** `source.build` is `{ workflow, output, lock? }` (`contracts/schemas/template.schema.json`): `output` matches
`^[A-Za-z0-9][A-Za-z0-9_-]*$`, and `lock` is a plain file name in the template's folder. The loader
(`LabRepositoryLoader.ParseBuild`) resolves `workflow` exactly as a guest's reference, including against a workflows
repository's tags (`WorkflowReferences.In` now collects template references too), and fails the template, and so every guest
that uses it, when the reference does not resolve, the workflow has errors, the lock is missing, or the release has no
`modules/nixos/flake.nix`. The flake is always `modules/nixos` (`BuildSource.Flake`): a workflow names no flake yet, and the
`images` list in `workflow.yaml` is not implemented, so `output` is not checked before the build. `availableOn` works as for
any template. `octopushy-validate` accepts a built template offline.

**The build key** is the lowercase hex SHA-256 of the UTF-8 text
`"octopushy-image-build-v1\n" + workflowCommit + "\n" + output + "\n" + "x86_64-linux" + "\n" + (lock's SHA-256 or "none")`
(`BuildSource.KeyOf`). For a workflow kept in the lab repository, which has no release commit, `workflowCommit` is `tree:`
and a hash of the files of `modules/nixos`, so only a change of the flake builds again.

**Builds.** After every sync, and once at startup, the server queues one build for each valid built template of the newest
valid revision that a guest whose lifecycle is active uses, when its key has no image and no build queued or under way
(`RunEngine.QueueImageBuildsAsync`). A build is an `image_builds` row per key (queued, building, built, failed, deleted) and
an attempt with executor `nix` and action `build`, belonging to no run, no step and no installation. It is dispatched to any
connected agent that advertises `nix`, is neither disabled nor draining, and has a free slot by the usual slot accounting.
No labels, no reachability evidence and no installation trust: it carries no secrets and no Proxmox context, and the revision
it is built from has to be a valid revision of the current source. The proposal's capacity check is the agent's: it
advertises `nix` only with enough free disk (`executors.md`). Its input is `{ key, flake, output, system, lock? }`, with the
run bundle of the revision's commit and the workflow's release. An expired lease is retried `ReadOnlyRetries` times, then the
build fails. A result is a built image only when its outputs `{ sha256, size, fileName, outPath }` match the upload that
attempt completed; anything else fails it. A failed key is not built again by itself: `POST /api/templates/{id}/build` queues
it again, as it does a deleted one. `GET /api/templates` lists the head revision's templates with each built template's
status, error and current attempt, whose log is at `/api/attempts/{id}/log`. There is no UI for it yet.

**Transfer.** `UploadImage` is accepted only for a `nix` `build` attempt assigned to the calling agent, under way, whose build
is building; chunks of at most 4 MiB, no more bytes than the first chunk declared, and the SHA-256 in the last chunk must match
what arrived. An upload that does not fit within `MaxBytes` once unreferenced images are gone fails the build with
"image store full", and nothing referenced is deleted for it. `DownloadImage` is allowed only for an attempt assigned to the
calling agent and under way whose OpenTofu input names that SHA-256 as `templateImage`; it streams 4 MiB chunks, the file name
and size in the first and the SHA-256 in the last.

**The store.** `Octopushy__Images__Directory` (default `<DataDirectory>/images`), `Octopushy__Images__KeepUnreferenced` (1)
and `Octopushy__Images__MaxBytes` (10 GiB). An image is stored plain at `<Directory>/<sha256>`; an upload is written to
`<Directory>/.partial/<attempt>`, hashed and counted as it arrives, and renamed into place only once complete.

**Provisioning.** A guest's provision run is made only once its template's image is built. Until then its desired work is
recorded without a run, and the guest shows "Waiting for the image of template `<id>`." as its pending detail; when the
build succeeds, the waiting guests of the head revision are proposed (`InventoryUpdater.ProposeWaitingAsync`). Planning such
a guest by hand is refused with the same reason. The variable `template` becomes
`{ id, os, source: { kind: "built", url: null, checksum, checksumAlgorithm: "sha256", fileName, sha256, size } }`, with
`fileName` = the first 12 characters of the SHA-256, `-`, and the name the build reported; the plan and the apply both get
`templateImage: { sha256, fileName, size }`. The provision hash of such a guest takes the build key, so a new release or a new
lock plans again; a rebuild of the same key does not. Downloaded templates' variables and hashes are unchanged. A retire run
takes the image when it is still kept, and otherwise names no file.

**Retention** runs after every sync, after every finished build, and before an upload is let in. It keeps every image a
built template of the newest valid revision resolves to, every image an unfinished run's `templateImage` names, and an upload
waiting for its result; then the newest `KeepUnreferenced` of the rest per template; then removes the oldest of those while
the total is above `MaxBytes` (`ImageRetention.Plan`). Removed rows are marked deleted, each removal is logged, and a file no
row names is removed. An image whose file disappeared is marked deleted, and built again by the next sync that needs it.

## The template

```yaml
# templates/nixos-base/template.yaml
schemaVersion: 1
os: nixos
source:
  build:
    workflow: nixos-host@1        # the flake is this release's modules/nixos
    output: base-image            # packages.x86_64-linux.<output>
    lock: flake.lock              # optional: the lab's pin, next to template.yaml
availableOn: [home/moraband]
```

- `workflow` resolves like a guest's, against the workflows repository's release tags. The flake is
  `modules/nixos` at the resolved commit. `@1` takes the newest 1.x, so a new release builds a new image.
- `lock` is the lab's `flake.lock`. The build copies it over the flake's own lock and runs with
  `--no-update-lock-file`: inputs that don't match the flake fail the build instead of re-locking. Without it, the
  workflow's own lock is used. So moving nixpkgs is a reviewed commit in the lab repository, not a side effect of a
  workflow release. The workflow's lock remains the one its CI tested; a lab lock that differs is shown on the
  template's page.
- **The build key** is the SHA-256 of the resolved workflow commit, `output`, the lock's bytes and the system
  (`x86_64-linux`). The same key is never built twice. The image's own SHA-256 is known only after the build.

### What changes in the engine

The schema already has `source.build: { workflow }`, accepted with a warning and never used
(`contracts/schemas/template.schema.json:22-32`, `LabRepositoryLoader.cs:299-302`, `docs/workflows.md:827`). This
replaces it:

| Where | Change |
| --- | --- |
| `template.schema.json` | `build` gets `output` (required) and `lock` (optional). `workflow` stays |
| `Model.cs:165-173` | `TemplateDefinition` gets a `BuildSource(WorkflowRef, Output, LockPath)` beside `ImageSource` |
| `ParseTemplate` (`LabRepositoryLoader.cs:282-306`) | Resolves the workflow, checks the lock file exists, computes the build key |
| `CheckGuest` (`:602-616`) | A built template is valid. "Has no downloadable image" applies only to a template with neither source |
| `ProvisionInputs.cs:77-90, 130` | `template.source` is `{ kind: built, sha256, size, fileName }` once built. The provision hash takes the build key, so a new lock or release re-plans the guests that use the template (as editing a template's URL does today) |
| Workflows | `manages` is unchanged. A workflow offering an image output says so in a new `images` list in `workflow.yaml`, so validation can check `output` |

A guest whose template's image isn't built yet doesn't fail: its provision run waits, with the reason "Waiting for
the image of `nixos-base` (building)", as a run waits for trust. A failed build shows on the template's page and
holds those runs until a new revision changes the key, or a person retries the build.

## The build

### A new kind of attempt

Every attempt today belongs to a run or an observation tied to a trusted installation
(`Git/RepositoryTrust.cs:46-67`). A build is neither, so it gets its own kind:

- **Queued** by repository sync, for each template in the newest valid revision whose key has no stored image and no
  build in progress. One build per key at a time.
- **Dispatched** to any connected agent that advertises the `nix` executor and has a free slot, by the existing
  slot accounting (`RunEngine.Agents.cs:53-58, 148-152`). It connects to nothing in the lab, so no reachability
  evidence is needed (as for an Ansible preview, `NetworkNeeds.None`). It does fetch from the internet: nixpkgs and
  the binary cache.
- **Trusted** by the revision: it carries no secrets and no Proxmox context, and builds code from the workflows
  repository at a resolved release plus a file from the lab repository. That's the same code a guest's setup
  already runs, with less authority: no token, no SSH.
- **Read-only** for lease purposes: an expired lease retries it (`ReadOnlyRetries`, `OctopushyOptions.cs:119`).
  A half-sent image is discarded.

### Nix on the agent

`EXECUTORS` (`agent/crates/octopushy/src/executors/mod.rs:22`) gets `nix`, advertised when the tool runtime has a
working `nix`. As for OpenTofu and Ansible (`docs/architecture-agreement.md:498-504`):

- **Local runtime** (the agent image, `agent/Containerfile`): a pinned single-user Nix in the image, with
  `sandbox = false` (an unprivileged container has no user namespaces to give it) and the store under the agent's
  volume, `/var/lib/octopushy-agent/nix`. `TMPDIR` moves to that volume too: the compose file's `/tmp` is 128 MB
  (`deploy/compose.yaml:79-84`).
- **Podman or Docker runtime**: a pinned Nix tool image, as `ansible-2.21.5-…` is for Ansible
  (`OctopushyOptions.cs:104,107`), with a named volume for the store.

**No KVM.** Agents run in containers without `/dev/kvm`. nixpkgs' `make-disk-image`, which its Proxmox image
module uses, builds inside a VM and needs it. The image must be built without a VM: NixOS's `image.repart`
(systemd-repart) does that. **Whether a repart image boots on Proxmox under OVMF/q35 with cloud-init is the first
thing to prove,** before any engine work.

**Resources.** A NixOS image build needs a few GB of RAM and several GB of disk for the store. An agent reports
free memory and disk in its heartbeat, and a build goes only to an agent above a minimum (a server setting). That is
a capacity check, not a label: the internal agent on a small host simply isn't chosen while it's short.

The agent's store is a cache: it is garbage-collected after each build down to a size limit (agent setting), so
the next build of a close key is fast.

## Moving the image

Today every transfer is one message, capped at 64 MiB each way (`Program.cs:76-82`, `connection.rs:30`). Artifacts
are encrypted whole in memory (`AttemptFiles.cs:262-294`), and the agent's outbox keeps them as base64 in JSON
(`outbox.rs:4-5`). None of that suits a 1–3 GB image. Images get their own path:

- **Two RPCs beside `Connect`:** `UploadImage(stream ImageChunk)` from the building agent, and
  `DownloadImage(ImageRequest) returns (stream ImageChunk)` for the applying agent. Chunks of 4 MiB, the SHA-256
  checked at the end, on the same mutual-TLS listener. An upload names its build attempt; a download names an
  apply attempt that is assigned to that agent and needs that image, so an agent can't fetch arbitrary images.
- **Stored plain,** not envelope-encrypted: an image holds no secrets by construction (the flake gets none), and
  streaming it through the envelope would mean holding it in memory. Its integrity is its SHA-256.
- **Resumable:** a broken upload restarts the build's transfer, not the build; the agent keeps the result in its
  store until the server confirms.

## Getting it onto Proxmox

The upload must not happen before approval. Only the apply gets the privileged token, and only after an approved
plan (`AssignmentBuilder.cs:120-126`); a plan may run on a different agent from its apply (the plan's agent is only
preferred). So:

- **Plan** (read-only token, any agent): the module plans a `proxmox_virtual_environment_file` with
  `content_type = "import"`, its name `<guest>-<sha256 prefix>.raw`, and the known checksum. No file is needed to
  plan. *To check: that bpg/proxmox plans an upload from `source_file` with a checksum, without reading the file.*
- **Apply** (privileged, approved): before OpenTofu runs, the agent downloads the image into the attempt's
  workspace and verifies it. OpenTofu uploads it and imports the disk from it, as `import_from` does with today's
  download (`examples/lab/modules/opentofu/proxmox-vm/main.tf:9-19, 62`).

The uploaded file lives in the guest's state, exactly like today's `proxmox_download_file`: retiring the guest
removes it, and `ignore_changes = [disk[0].import_from]` (`:114-118`) means a new image never re-images an existing
VM. A new build does re-plan the guests that use the template, which replaces only the uploaded file. **So nothing
new is needed to clean up Proxmox.** The cost is one copy of the image per guest on its host, as today.

The agent's Proxmox client needs no change: the upload stays OpenTofu's.

## Retention on the server

Today nothing prunes `artifacts/` or attempt logs (`RunEngine.Observations.cs` and `RepositorySync.PruneAsync`
cover only observations and revisions). Images can't join that gap: each is gigabytes, and CT 101 has 32 GB in all.

`<DataDirectory>/images/<sha256>.raw`, with a row per image: build key, template id, size, built, last used.
After every sync and every build, prune:

1. **Never delete an image that a template in the newest valid revision resolves to, or that an unfinished run
   needs.**
2. **Keep the newest `KeepUnreferenced` images per template** that no longer are referenced, for rollback.
   Default 1.
3. **Cap the total at `MaxBytes`.** Above it, delete the oldest unreferenced images first. If only referenced images
   remain and a new build would pass the cap, the build fails with "image store full", and nothing referenced is
   deleted.

Settings, as everything else on the server (`Octopushy__Images__KeepUnreferenced`, `Octopushy__Images__MaxBytes`,
`Octopushy__Images__Directory`). The directory can be its own mount point, so images don't compete with the
database volume. Images stay out of the update's dump: they can always be built again.

## Workflows repository (octopushy-lab), for the first user

Not engine work, listed so the whole change is visible:

- `modules/nixos`: a `base-image` output built with `image.repart`, with the guest baseline, cloud-init, Docker and
  Python (for Ansible).
- `workflows/nixos-host`: `machine` (OpenTofu, `proxmox-vm` taking a built template) and `setup` (Ansible). Setup
  copies `modules/nixos` at the workflow's commit, the guest's `nixos/*.nix` and `flake.lock` (through
  `octopushy.guest_directory`, which setup triggers already cover: `ConfigureInputs.cs:79`) and a `guest.json` of
  its inputs to the guest, and runs `nixos-rebuild switch` **on the guest**. Guest-supplied Nix is evaluated only
  there, never on an agent, which holds every installation's token.
- `compose` 1.1: its preflight accepts NixOS when `/etc/octopushy-compose/engine` exists, and skips apt. Today it
  refuses anything but Debian (`modules/ansible/compose/tasks/preflight.yml:9-14`).

## Order, and what proves each step

1. **A repart NixOS image boots on Proxmox** under OVMF/q35, with cloud-init setting the user, key and network,
   and the guest agent reporting. Built by hand, uploaded by hand. If it doesn't, the "no KVM" plan changes
   before anything else.
2. **bpg plans an `import` upload without the file.**
3. Engine: the template source and its validation, the build attempt, `nix` on the agent, image transfer, the
   store and its retention, the apply's fetch. Released, then updated on ziost.
4. octopushy-lab: `base-image`, `nixos-host`, `compose` 1.1.
5. Lab repository: the template and its lock, then the first guest.

## Open questions

- **Network for builds.** A build fetches from cache.nixos.org and GitHub. Not checked: whether every agent reaches
  them. An agent that can't would fail builds. Is "advertise `nix` only if the binary cache answers" enough?
- **A build on the internal agent.** CT 101 is small, and building there competes with the server. The capacity
  check excludes it when short; should it never build, by setting?
- **Several systems.** Only `x86_64-linux` for now. The key includes the system, so `aarch64` can come later.
- **Signing.** The image's integrity is its SHA-256 from the agent that built it. A compromised agent could hand
  back a bad image for a key. A second agent rebuilding and comparing would catch it, but repart images aren't
  bit-for-bit reproducible yet.
