# Executor examples

One input and one output per executor action of [executors.md](../executors.md), as JSON:
`<executor>/<action>.input.json` and `<executor>/<action>.output.json`. A file named
`<action>.output.<variant>.json` is another shape the same action reports, such as a playbook's
declared outcome. The backend and the agent are both tested against these files, so a field
renamed on one side fails a test.

Each side writes what it produces and reads what the other produces:

| | Written and compared by | Read by |
|---|---|---|
| Inputs | `ContractExampleTests` (backend): the example lab's guest `home/korriban/krb-app-01` through provision, configure and retire, and a built template's build, each input from `AssignmentBuilder` | `executors::contract_inputs` (agent): each input parsed into the executor's own input type, and every value of it changed in turn, which must change what the executor parsed |
| Outputs | `contract_outputs.rs` and `nix_build.rs` (agent): each executor run on its input example, with tools that print what the real ones printed (`agent/crates/octopushy/tests/fixtures/contract`) and mock-proxmox | `ContractOutputReadTests` and `ContractExampleTests` (backend), whose readers must come out with each example's values, and `FakeAgent`, which reports the examples when a test gives no outputs and refuses outputs a test made up in another shape |

Comparisons are of keys at every level, array lengths, the kind of each value, and the values,
except where a value depends on the run: the commits and run ids of inputs, the build key, and a
build's `outPath`.

To change a field, change the code on one side, then rewrite the files that side produces and
review the diff:

```sh
OCTOPUSHY_WRITE_CONTRACT_EXAMPLES=1 dotnet test backend/Octopushy.slnx --filter FullyQualifiedName~ContractExampleTests
cd agent && OCTOPUSHY_WRITE_CONTRACT_EXAMPLES=1 cargo test -p octopushy --test contract_outputs --test nix_build
```

The other side's tests then fail until it reads the field under its new name.

## Not in an example yet

These fields of executors.md have no example, so nothing checks them across the two sides.
`FakeAgent` lets tests report them until one does.

- `opentofu/preview`: a change's `actionReason` and `targetUnknown`, and an attribute's `before`,
  `unknown` and `sensitive`. The example is a create, and a plan with them needs a Proxmox to refresh.
- `opentofu/*` input: `knownHosts` and `templateImage`.
- `proxmox/verify-guest`: the input's `hostKeys`, and the outputs `hostKeys`, `hostKeysTask` and
  `hostKeysError` of a container.
- `ansible/run`: the input's `approvedVars`, `prepared` and `expectRefused`, and the output `refusedKeys`.
- Outputs of failed attempts.
