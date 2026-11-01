---
name: run-octopushy
description: Launch Octopushy from this checkout and drive it in a real headless browser. Use whenever asked to run, start, demo or screenshot the app, to reproduce something in the web interface, or to confirm a frontend or API change in the real app and not just in tests, even if nobody says "browser". Covers the Variables, Inventory, Runs, Agents, Repository and Editor pages, and `just demo` when real plans or an agent are needed.
---

# Run Octopushy

Two scripts do the mechanical work, so launching takes one command and a check is a short script. Run everything from the repository root. `$S` below stands for this path; if your shell does not keep variables between commands, write it out:

```bash
S=.claude/skills/run-octopushy/scripts
```

## Pick the stack

| You need | Use |
|---|---|
| Any page, the API, the variable store, the editor, lab sync | `$S/stack.sh up`: about 15 seconds once built, no Rust build, and its data goes with `down` |
| An agent, real plans or applies, OpenTofu, a mock Proxmox | `just demo`, see [With an agent](#with-an-agent) |

Default to `stack.sh`. Without an agent, runs are created but wait for a worker, so anything past the plan needs the demo.

## Launch and drive

```bash
$S/stack.sh browser                 # once per machine; instant after that
$S/stack.sh up                      # builds, starts, seeds, and returns
node $S/drive.mjs                   # smoke test: every page, exit 0 when clean
```

Then write a short script for what you actually came to check. Keep it in `.dev/run/`, which is gitignored and cleared by the next `up`, so it never needs tidying:

```js
// .dev/run/check.mjs
import { open } from "../../.claude/skills/run-octopushy/scripts/drive.mjs";

const { page, base, check, shot, finish } = await open();
const table = () => page.getByRole("region", { name: "Variable store entries" });

await page.goto(`${base}/variables?folder=proxmox/korriban`);
await check("the folder lists its keys", async () => {
  await table().getByText("read-only-token", { exact: true }).waitFor();
});
await shot("korriban");
await finish();
```

```bash
node .dev/run/check.mjs             # exit 1 on a failed check or any page problem
```

`stack.sh browser` warns that the OS is not supported by Playwright and uses the Ubuntu build; that build works here. Each `open()` starts a new browser context, so `localStorage` starts empty. `shot()` writes to `.dev/run/shots`.

`open()` signs in and records console errors, page errors and every HTTP status of 400 or above. `finish()` prints those as `PROBLEM` lines and fails the run, so a page that looks right but logged an error still fails. `open({ width: 390, height: 800 })` checks a narrow layout.

**Read each screenshot with the Read tool.** Passing checks do not show overlap, wrapping or an empty frame. Most layout bugs here are only visible this way.

**Stop the stack when you finish:**

```bash
$S/stack.sh down
```

A stack left running blocks the next `up`, in this checkout or any other worktree, because the ports are fixed.

## What is in the stack

- **App:** http://127.0.0.1:5175, user `run`, password `octopushy-run`.
- **Other ports:** API 18180, agent channel 18543, PostgreSQL 55433, in the container `octopushy-run-pg`. The demo uses 5174, 18080, 18443 and 55432, so the two can run together.
- **Store:** secrets `proxmox/korriban/read-only-token`, `proxmox/korriban/privileged-token` and `dns/technitium/api-token`, and the plain key `motd`. Secret values start with `MARKER-secret-`, so a check can show they never reach the page.
- **Lab:** a copy of `tests/e2e/lab`, in `.dev/run/lab`, already synced. It is a real Git repository: commit there to change definitions, and the server picks it up within 10 seconds.
- **Logs:** `backend.log` and `frontend.log` in `.dev/run`, kept after `down` until the next `up`.

Add what the check needs through the API:

```bash
$S/stack.sh api PUT /api/variables '{"key":"a/b","value":"leaf","visibility":"plain"}'
$S/stack.sh api GET /api/variables | jq '.entries[].key'
```

Vite serves frontend edits as they are saved. Server changes need `down` and `up`. `up` installs the frontend and builds the server in this checkout, in the same gitignored `node_modules` and `bin/` that `just demo` uses, so restart a running demo after changing server code.

## Traps

- **`up` says a port is taken.** Another checkout's stack holds it, or an earlier `up` failed partway. Run `down` in the checkout that started it, not here: `down` only stops what its own checkout started. `status` says when the running stack is not this code.
- **Substring text matches.** `getByText("token")` also matches `read-only-token` and can pass on the wrong row. Pass `{ exact: true }`.
- **A check that cannot fail proves nothing.** Click the real link and assert where it lands, instead of stubbing a page or asserting that an element exists.
- **The poll interval has a 10 second minimum.** `PUT /api/repository` with a lower `pollSeconds` returns 400.
- **The Installation page shows its token link only for the single `connection.token` form.** The e2e lab uses `readOnlyToken` and `privilegedToken`, so edit the copy in `.dev/run/lab` to see it.
- **`system/` keys exist from the first start.** The server creates `system/agent-ca/*`, and `GET /api/variables` lists them under `.system`, not `.entries`.

### Variables page

- **Key field:** `getByLabel("Key", { exact: true })`.
- **Prefilled key:** `fill()` replaces the folder prefix. To type after it, use `pressSequentially`.
- **Visibility:** the dialog defaults to Secret, so choose Plain for a plain key.
- **Current view:** the Folders and All keys buttons mark it with `aria-current="page"`.
- **Stored view:** All keys stays chosen across navigation, and hides the folder links, until Folders is clicked. A `?folder=` link overrides it for that visit.

## With an agent

`tests/e2e/demo.sh` calls bare `pnpm`, and `stack.sh shim` prints a directory that provides it. Run the demo in the background, because it stays in the foreground until stopped:

```bash
PATH="$($S/stack.sh shim):$PATH" just demo
```

It serves http://127.0.0.1:5174, user `demo`, password `octopushy-demo`, with logs in `.dev/demo`, and it can run alongside `stack.sh`. The first build compiles the Rust agent, which takes a few minutes. It also uses the long-lived `octopushy-dev-pg` container and volume, which stay after it stops. To plan, trust the Proxmox installation on its Inventory page first; the first plan then waits for approval.

`drive.mjs` signs in to the light stack only, so for the demo set the URL and credentials in your own script.

Ctrl-C stops everything, but a backgrounded demo has no terminal. Signal its whole process group, or Vite stays behind on port 5174 and the next demo cannot start:

```bash
kill -TERM -- -"$(ps -o pgid= -p "$(pgrep -x just)" | tr -d ' ')"
```

## If this skill fails

If a step fails for a reason unrelated to the work at hand, fix the script here rather than working around it, so the next run benefits. `up` needs podman, dotnet, node, jq, curl and git. It finds pnpm on PATH or in the local pnpm store, at the version `frontend/package.json` pins; Playwright is pinned at the top of `stack.sh`.
