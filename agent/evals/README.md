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

## History (8 vCPU VM, btrfs)

| | first baseline | disk work off the loop |
|---|---|---|
| one `observe` read | 16 ms, 11 ms before `AttemptStarted` | unchanged |
| 64 reads, limit 8 | 625 ms, 102 attempts/s | 80 ms, 800 attempts/s |
| CPU per attempt (whole test process) | 2.2 ms | 2.5 ms |
| same with 2 disconnects | 2.6 s, 55 results sent twice | 2.3 s, none twice |
| shutdown under load | 185 ms | 28 ms |
| reconnect after the server closes | 615 ms | unchanged (random backoff) |
| tool output to server | 435 000 lines/s | unchanged |
| log masking and buffering | 345 000 lines/s, 2.9 µs CPU per line | unchanged |
| outbox write (256 KiB plan, fsync) | 4.6 ms; remove 1.9 ms | unchanged |

**Disk work off the loop.** Accepting an assignment (workspace and `queued` marker) and
acknowledging a result (marker and outbox entry removed) used to run in the connection's loop. So
every attempt's fsyncs queued behind every other's, and logs and results waited behind them. The
result write ran on an async worker. All three now run on tokio's blocking pool, in the same order
within an attempt and with the same fsyncs, so the fsyncs of different attempts overlap and the
file system commits them together. On this disk, 64 atomic writes take 263 ms one after another
and 64 ms from 8 threads. An acknowledged attempt stays counted until its files are gone, so a
draining agent waits for them, and nothing of it is sent again in the meantime.

What is left, from these numbers:

- A single read still spends 11 of its 16 ms on four fsyncs before it starts (`queued` marker,
  then `running` marker). Restart recovery relies on both. A crash between them reports the
  attempt as never started rather than as a lost lease, so writing them less often changes what
  a crash means.
- A reconnect after a clean close waits a 0.5–1 s backoff.
- Log masking is the per-line cost: every line is matched against every secret pattern twice.
