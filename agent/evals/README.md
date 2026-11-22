# Agent performance evals

`crates/octopushy/tests/evals.rs` measures the agent and checks the invariants a faster agent must
keep. It runs the real agent (`octopushy::run::run`) against the test server from
`tests/common` over mutual TLS and against mock-proxmox, plus a few hot paths called directly.
`baseline.json` here is the starting point that later changes are compared with.

```sh
just agent-evals            # measure, compare with baseline.json, fail on a regression or a failed check
just agent-evals-baseline   # measure 5 times and save baseline.json (refused if a check fails)

OCTOPUSHY_EVAL_ONLY=burst,outbox just agent-evals   # only these scenarios
OCTOPUSHY_EVAL_RUNS=5 just agent-evals              # more runs, steadier medians (default 3)
OCTOPUSHY_EVAL_TRACE=1 just agent-evals             # agent logs to stderr
```

The test is `#[ignore]`d, so `cargo test` and CI skip it. It builds with `--release`, the profile
that ships. The report is written to `agent/target/evals/agent-latest.json`, or to
`OCTOPUSHY_EVAL_REPORT`. `OCTOPUSHY_EVAL_BASELINE` names another baseline file.

## Workflow for a performance change

1. On the base commit, run `just agent-evals-baseline` if `baseline.json` came from another
   machine. Timings only compare on the same host. The report records the CPU model and count,
   and the comparison warns when they differ.
2. Make the change. Run `cargo test --workspace` (correctness) and `just agent-evals`
   (performance and resilience under load).
3. A metric marked `Better` is the gain. `Worse` beyond its tolerance, or any `FAIL`, fails the
   run. When the change is accepted, save a new baseline in the same commit.

## Scenarios

| Scenario | What it does |
|---|---|
| `startup` | start to `Hello`; server drops the channel, time to the next `Hello`; idle shutdown |
| `observe` | 20 Proxmox `observe` reads one at a time: assignment to `AttemptStarted` and to result |
| `burst` | 64 reads at once, server limit 8: wall time, throughput, CPU, memory added, messages and bytes per attempt |
| `burst_disconnects` | the same, with the server dropping the channel twice mid-burst |
| `drain` | shutdown with 24 reads and 8 `set-protection` changes queued and running |
| `tool_log` | an OpenTofu `preview` (a fake `tofu`) printing 15 000 lines, five times, through the agent to the server |
| `log_pipeline` | 200 000 lines through masking (13 secrets), cleaning and the retained buffer |
| `outbox` | 100 results with a 256 KiB plan: durable write, list, read, remove |

Scratch directories are under `agent/target/tmp`, on the real disk. On a tmpfs `/tmp`, an fsync
costs nothing and the outbox would look free.

## Checks (pass/fail, every run)

- Every assignment gets a result, and every attempt the server saw is one it assigned.
- Log sequence numbers run from 0 without a gap. A line resent after a reconnect has the same
  text. A result resent after a reconnect is the same result.
- No secret appears in a log line, an error or outputs. The `tool_log` tool prints its token on
  purpose.
- Terminal escapes are stripped. All 15 000 tool lines arrive.
- The outbox and workspaces are empty once results are acknowledged.
- At shutdown, a change that started finishes (`Succeeded`) and is never cancelled. A read ends
  `Succeeded` or `Cancelled`. An assignment the agent had not accepted yet is refused as `Failed`
  without starting. Nothing ends `Uncertain`.
- The disconnect burst really did disconnect mid-burst.

The agent's own tests keep covering the rest: restart recovery, force stop, lease rules,
concurrency limits (`attempts::tests::respects_concurrency`).

## Reading the numbers

Each metric is the median of its runs. It counts as a regression when it is worse than the
baseline by more than `tolerance × baseline + slack`. Both values are set per kind of metric in
`evals.rs`, from the spread of repeated runs on the baseline host. Most timings varied by under 5%
between runs. Reconnect times did not: the agent waits a random half-to-whole backoff step, so
they get ±45%. CPU time is that of the whole test process, fake server and mock included, so it is
a relative measure.

The test servers set `TCP_NODELAY`, as Kestrel does. Without it, Nagle's algorithm and delayed
ACKs added about 40 ms to every small message, and the first measurements were mostly that.

## Starting point (baseline.json, 8 vCPU VM)

| | |
|---|---|
| one `observe` read | 17 ms to result, 12 ms of it before `AttemptStarted` |
| 64 reads, limit 8 | 660 ms, 97 attempts/s, 2.3 ms CPU per attempt |
| same with 2 disconnects | 2.8 s, 55 results sent twice |
| reconnect after the server closes | 735 ms |
| shutdown under load | 190 ms |
| tool output to server | 425 000 lines/s |
| log masking and buffering | 345 000 lines/s, 2.9 µs CPU per line |
| outbox write (256 KiB plan, fsync) | 4.6 ms; remove 2.1 ms |

Where the time goes, from these numbers:

- Durable writes dominate throughput. 64 reads take 660 ms where 8 parallel slots of a 17 ms read
  would take about 140 ms. Each attempt makes several fsyncs (workspace marker, running marker,
  outbox write, removal), and on one file system they serialize. `Outbox::put` runs on an async
  worker thread, not in `spawn_blocking`.
- Most of a single read's latency, 12 of 17 ms, comes before it starts, while the workspace and
  its marker are written. Those writes are what restart recovery relies on, so batching them is
  the safe direction. Skipping them is not.
- A reconnect after a clean close waits a 0.5–1 s backoff, and every unacknowledged result is
  sent again (55 duplicates for 64 attempts). The server drops those, but they cost bandwidth.
- Log masking is the per-line cost: every line is matched against every secret pattern twice.
