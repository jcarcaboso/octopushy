# Mock API

A small Node server that answers every `/api` call the web app makes with realistic data, so the whole app can be
browsed without the backend, a database or an agent. Use it for UI work and for showing changes. **It is not the real
backend:** it checks nothing and runs nothing, and its rules are rough copies of the server's.

```bash
just mock          # API on 127.0.0.1:18280 (MOCK_PORT), app on http://127.0.0.1:5176; Ctrl-C stops both
```

You are signed in as `ops` from the start. Any user name and password sign in again after Sign out. State lives in
memory: approving, rejecting or cancelling changes it until the server restarts.

- Approve: the step records the decision and every later step succeeds at once, so the run succeeds.
- Reject: the step is cancelled with "Rejected.", and the run ends as cancelled ("Rejected by ops."), as on the server.
- Plan now, Configure, Deploy, Run again: a new run starts and stays queued, since nothing executes it.
- Updating an agent: it shows as updating for 8 seconds (`MOCK_UPDATE_SECONDS`), with a configure run on its guest, then
  runs the latest version. A second update waits in the queue until the first is done, and Cancel takes it out.
- Other changes (variables, agents, drafts, forms) answer with a plausible body and are kept in memory.
- An unknown route answers 404 with problem+json, and the console names the route.

## Scenarios

One lab, `home`, with the Proxmox installation `home/pve01`, seven guests, the service `home/pve01/web/app` and five
more services with links, four agents, variables and a lab repository with revisions and drafts. A second installation,
`home/korriban`, has been offline for two days, with its one machine, `nas`.

[/inventory](http://127.0.0.1:5176/inventory) shows uptime from `GET /api/uptime` (mock/uptime.ts): korriban and nas are
down, jellyfin on media is degraded (its first link answers 502 since 40 minutes ago), and dns has no answers yet.

| Run | What it shows |
|---|---|
| [/runs/provision-approval](http://127.0.0.1:5176/runs/provision-approval) | #49, bb (VM 112) waiting for approval: disk scsi0 32→64 GiB, memory 4→8 GiB, tags +agents, a sensitive bridge, a new firewall rule |
| [/runs/configure-approval](http://127.0.0.1:5176/runs/configure-approval) | #48, pve01-ai waiting for approval: prepared values against the last run (codex, claude-code, +fzf, −neofetch, release 2.1.0), 2 changed files, 1 secret |
| [/runs/outcome-approval](http://127.0.0.1:5176/runs/outcome-approval) | #46, dev at "Decide on the outcome": the playbook reported `deferred`; approving adds `machine_tools_allow_busy` |
| [/runs/pipeline-approval](http://127.0.0.1:5176/runs/pipeline-approval) | #47, the app's pipeline: one approval for the `infra` plan and the `deploy` preview |
| [/runs/running](http://127.0.0.1:5176/runs/running) | #50, the dns container being created, with a live log |
| [/runs/succeeded](http://127.0.0.1:5176/runs/succeeded) | #45, pbs set up |
| [/runs/failed](http://127.0.0.1:5176/runs/failed) | #44, media's disk resize that ran out of space; offers Run again |

Also worth a look: [/guests/bb](http://127.0.0.1:5176/guests/bb), [/services/web-app](http://127.0.0.1:5176/services/web-app),
[/installations/pve01](http://127.0.0.1:5176/installations/pve01), [/changes/change-bb-resize](http://127.0.0.1:5176/changes/change-bb-resize)
and [/editor/draft-pbs-schedule](http://127.0.0.1:5176/editor/draft-pbs-schedule).

## Adding a scenario

1. In `runs.ts`, add a block to `scenarios()` that pushes a `run({...})`, building steps with `ran()` (an agent step with
   an attempt and its log), `awaiting()`, `decided()` and `pending()`. Give it a readable `id`; that is its URL.
2. If it needs a new guest, add it to `resources` in `runs.ts` and to `guests` in `data.ts`. Its status, active run and
   counts follow from its runs.
3. Run `pnpm run typecheck`: the fixtures are typed against `src/api/types.ts`, so an API change shows up here.
4. Add a row to the table above.

`server.ts` holds the routes. A new endpoint in `src/api/queries.ts` needs one `on(...)` line there.
