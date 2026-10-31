# OpenTofu state

Octopushy keeps each guest's OpenTofu state in its own PostgreSQL database. There is no
backend or credential to set up: install Octopushy, add a lab repository, and provision.
`stateBackends` in `octopushy.yaml` remains an optional override for a guest that names one
with `provision.state`.

## How it reaches OpenTofu

State moves on the existing mutual-TLS agent channel and nowhere else. It goes out in the
assignment of an attempt, and OpenTofu reads it as a generated `local` backend file in the
attempt's private workspace. It comes back with the apply's result, and as snapshots while
the apply runs, which OpenTofu makes possible by writing the file as resources finish, at
most every 20 seconds. OpenTofu needs no network route for it, so it works the same in every tool
runtime, on any container network, and for internal, remote and managed agents.

Agents never connect to the controller's PostgreSQL, and they get neither the key nor the
database password. [Why not an OpenTofu `http` backend](adr/0003-built-in-opentofu-state.md).
The wire details are in the [executor contract](../contracts/agent/v1/executors.md#built-in-state).

## What protects it

- **At rest.** Each version is sealed with a fresh data key under the variable store's
  key-encryption key, bound to its guest and version.
- **In logs and the API.** State is never logged, with one exception outside Octopushy's
  control: if OpenTofu can write neither the backend file nor `errored.tfstate`, usually
  because the disk is full, it prints the state as its last resort, which reaches the
  attempt's log. The API shows a version's serial, lineage,
  resource count, digest, size and origin, never its content; there is no way to download it.
- **On agents.** It is plaintext in the attempt's workspace while the attempt runs, and in
  the outbox until the result is acknowledged. A saved plan embeds the same state, so this
  is the exposure saved plans already had. Those plans stay encrypted artifacts in the data
  volume, outside the database dump.
- **Reading.** State goes only into the assignment of a plan or apply of that guest, after
  the same gates that release its credentials: a valid revision of the current source, an
  [installation trusted](adr/0002-installation-trust.md) for that endpoint, and for an
  apply, the approved plan. An agent has no way to ask for it. A plan does get the state,
  because OpenTofu cannot plan without it. No one reviews a commit before its plan runs, so
  tool-native code in any commit can read the guest's state, as it can its credentials.
- **Writing.** A plan cannot change state: only an `apply` attempt can write, and only while
  it holds the stack's lock, which it gets once its plan is approved, by a person or by the
  guest's automatic policy. A write must be valid OpenTofu
  state of the same lineage, with no older serial. Otherwise it is refused, the last good
  version stays, and a finished apply becomes `uncertain`. So does an apply that started
  and returned no state, even if it reported a failure: it may have changed something.
- **When OpenTofu cannot save.** If it cannot write the backend file, it leaves the state
  in `errored.tfstate`. The agent reports that file, so the controller keeps what the apply
  did and the next plan starts from it.

## Locking

An apply takes the guest's lock when it is assigned. A plan waits while a running apply
holds it, and does not take it, because a plan writes nothing. A plan made from an older
state is refused before its apply starts and again by OpenTofu itself (`Saved plan is stale`).

The lock has no timeout of its own. It follows the attempt: while the agent reports, the
attempt runs and the lock holds. When the agent stops reporting, the attempt's lease expires,
so a crashed apply stops blocking the stack as soon as its run is closed. The holder may
still report late, and what it reports is kept, until another apply takes the lock. From then
on the old apply is fenced off and a late result of it is refused.

Runs of one guest were already serialized, and an `uncertain` apply still waits for an
operator to close it, because infrastructure may have changed.

## Versions and restoring one

Each apply that changes the state adds one version, and its snapshots replace each other
within it. A guest with no version yet starts from an empty seed, which is not stored. The newest 50 versions of each guest are kept.

```sh
octopushy operator request /api/guests/GUEST_UUID/state
printf '%s\n' '{"version":12,"expectedHead":14}' |
  octopushy operator request /api/guests/GUEST_UUID/state/rollback --method POST --json-stdin
```

Restoring adds a new version with the old content and the next serial; nothing is deleted.
It is refused while any run of the guest is unfinished, and `expectedHead` makes sure you
are restoring from what you just reviewed. Saved plans of the replaced state become stale.
Plan afterwards to compare the restored state with the infrastructure.

## Backups and updates

The state is in the database, so it survives `octopushy-update` and is in its dump. Restoring
it needs the **original key-encryption key**, like stored secrets: see the
[recovery checklist](recovery-rehearsal.md). Without any key, an installation holding state
refuses to start rather than create a new one.

## Moving between backends

There is no migration in either direction. Once an apply has started on an external
backend, even one that failed, that backend may hold the guest's resources. Removing
`provision.state` then fails the plan rather than planning from an empty state, which would
try to create everything again. The same refusal applies when
adding `provision.state` to a guest whose built-in state tracks resources. A guest with no started apply can use either, and so can one whose external retirement
finished.

The key is the guest's resource, not its name, so a rename with `renamedFrom` keeps the
state. A retired guest keeps its versions; a new guest at the same address starts empty.
