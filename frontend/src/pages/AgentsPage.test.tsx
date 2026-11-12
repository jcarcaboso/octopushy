import { screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, expect, it, vi } from "vitest";
import type { Agent, EnrollmentToken, ReachabilityEntry } from "../api/types";
import { mockApi, problem, renderAt } from "../test/utils";
import { AgentsPage } from "./AgentsPage";

afterEach(() => {
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
});

const minutesAgo = (m: number) => new Date(Date.now() - m * 60_000).toISOString();

function entry(kind: string, name: string | null, state: string, more: Partial<ReachabilityEntry> = {}): ReachabilityEntry {
  return {
    kind, endpoint: "https://10.1.0.10:8006", context: "agent", image: null, state, detail: null, checkedAt: minutesAgo(2),
    expiresAt: null, usable: state === "reachable", probing: false, leafSha256: null, name, resourceId: null, ...more,
  };
}

function agent(more: Partial<Agent> & { id: string; name: string }): Agent {
  return {
    labels: [], maxConcurrency: 2, disabled: false, draining: false, connected: true, version: "0.8.1",
    executors: ["opentofu", "ansible"], toolRuntime: "podman 5.6.2", runningAttempts: 0, reportsReachability: true,
    enrolledAt: minutesAgo(60_000), lastSeenAt: minutesAgo(0), certificateExpiresAt: new Date(Date.now() + 80.5 * 86_400_000).toISOString(),
    reachability: [], work: [], guest: null,
    update: { state: "current", targetVersion: null, reason: null, changeId: null, runId: null },
    ...more,
  };
}

const busy = agent({
  id: "a1", name: "site-a", runningAttempts: 1,
  reachability: [
    entry("proxmox-api", "home/pve01", "reachable", { detail: "TLS handshake completed" }),
    entry("proxmox-api", "home/korriban", "timeout", { detail: "connecting timed out", probing: true }),
    entry("snippets-ssh", "home/pve01", "reachable"),
    entry("guest-ssh", "home/pve01/dns", "reachable", { context: "tool", resourceId: "g-dns" }),
    entry("guest-ssh", "home/pve01/web", "reachable", { context: "tool", resourceId: "g-web" }),
    entry("guest-ssh", null, "unsupported", { context: "tool", endpoint: "ssh://10.1.0.31:22", detail: "the image has neither python3 nor nc" }),
  ],
  work: [{ runId: "r50", runNumber: 50, operation: "provision", address: "home/pve01/dns", step: "apply", status: "running", since: minutesAgo(3) }],
  guest: { resourceId: "g-bb", address: "home/pve01/bb", declaredVersion: "0.8.1" },
});
const behind = agent({
  id: "a2", name: "site-b", version: "0.7.1",
  update: { state: "available", targetVersion: "0.8.1", reason: null, changeId: null, runId: null },
});
const away = agent({
  id: "a3", name: "laptop", connected: false, lastSeenAt: minutesAgo(4300), version: "0.6.4", reportsReachability: false,
  certificateExpiresAt: new Date(Date.now() + 86_400_000).toISOString(),
  update: { state: "unavailable", targetVersion: "0.8.1", reason: "It was installed by hand, so update it on its machine.", changeId: null, runId: null },
});

function serve(agents: Agent[], tokens: EnrollmentToken[] = [], more: Parameters<typeof mockApi>[0] = {}) {
  const api = mockApi({ "GET /api/agents": () => agents, "GET /api/agents/tokens": () => tokens, ...more });
  renderAt(<AgentsPage />, { path: "/agents", route: "/agents" });
  return api;
}

const row = async (name: string) => (await screen.findByRole("button", { name: new RegExp(`^${name} `) })).closest("li")!;

it("lists each agent closed, with its status, version, what it reaches and its work", async () => {
  serve([busy, behind, away]);
  expect(await screen.findByText("2 of 3 connected · 1 run in progress · 1 update available")).toBeVisible();
  expect(screen.queryByRole("button", { name: "Update all" })).not.toBeInTheDocument();

  const a = await row("site-a");
  expect(within(a).getByRole("button", { name: /^site-a/ })).toHaveAttribute("aria-expanded", "false");
  expect(a).toHaveTextContent("Connected");
  expect(a).toHaveTextContent("Latest");
  const pills = within(a).getByRole("list", { name: "What site-a reaches" });
  expect(within(pills).getAllByRole("listitem").map((p) => p.textContent)).toEqual([
    "Reaches home/pve01",
    "Does not reach home/korriban",
    "Reaches 2 guests",
  ]);
  expect(a).toHaveTextContent("1 run");
  expect(within(a).queryByText("Working on")).not.toBeInTheDocument();

  const c = await row("laptop");
  expect(c).toHaveTextContent("Last seen 3 days ago");
  expect(c).toHaveTextContent("Not connected");
  expect(c).toHaveTextContent("It was installed by hand");
  expect(within(c).queryByRole("button", { name: /Update/ })).not.toBeInTheDocument();
  expect(c).toHaveTextContent("Idle");
});

it("says when a connected agent cannot report what it reaches, or has checked nothing yet", async () => {
  serve([
    agent({ id: "x", name: "old", reportsReachability: false, draining: true, disabled: true }),
    agent({ id: "y", name: "fresh", reachability: [] }),
    agent({ id: "z", name: "never", connected: false, lastSeenAt: null }),
  ]);
  const old = await row("old");
  expect(old).toHaveTextContent("Needs an update to report this");
  expect(old).toHaveTextContent("Connected · Paused · Blocked");
  expect(await row("fresh")).toHaveTextContent("Nothing checked yet");
  expect(await row("never")).toHaveTextContent("Never connected");
});

it("opens a row in place: every route it checked, its work and its settings", async () => {
  const user = userEvent.setup();
  serve([busy, away]);
  const toggle = within(await row("site-a")).getByRole("button", { name: /^site-a/ });
  await user.click(toggle);
  expect(toggle).toHaveAttribute("aria-expanded", "true");

  const lines = screen.getByRole("list", { name: "Every route site-a checked" });
  expect(lines).toHaveTextContent("home/korriban Proxmox API");
  expect(lines).toHaveTextContent("Timeout · probing");
  expect(lines).toHaveTextContent("connecting timed out");
  expect(lines).toHaveTextContent("home/pve01 Snippet upload");
  expect(lines).toHaveTextContent("10.1.0.31:22 Guest SSH");
  expect(lines).toHaveTextContent("Unsupported");
  expect(within(lines).getByRole("link", { name: "home/pve01/dns" })).toHaveAttribute("href", "/guests/g-dns");
  expect(screen.getByText(/A check shows a route, not that a login will work/)).toBeVisible();

  const work = screen.getByRole("list", { name: "What site-a is working on" });
  expect(within(work).getByRole("link")).toHaveAttribute("href", "/runs/r50");
  expect(work).toHaveTextContent("#50 Provision, apply");
  expect(work).toHaveTextContent("3 min");

  expect(screen.getByRole("spinbutton", { name: "Runs at once for site-a" })).toHaveValue(2);
  expect(screen.getByRole("link", { name: "home/pve01/bb" })).toHaveAttribute("href", "/guests/g-bb");
  expect(screen.getByText("Renews in 80 days")).toBeVisible();
  expect(screen.getByText("OpenTofu, Ansible")).toBeVisible();

  await user.click(within(await row("laptop")).getByRole("button", { name: /^laptop/ }));
  expect(screen.getByText(/checked while it is connected/)).toBeVisible();
  expect(screen.getByText("Nothing right now.")).toBeVisible();
  expect(screen.getByText("Renews in 24 hours")).toHaveClass("agent-detail__warning");
  expect(screen.getByText("It was installed by hand, so update it on its machine.", { selector: "dd" })).toBeVisible();
});

it("saves how many runs it takes at once", async () => {
  const user = userEvent.setup();
  const { requests } = serve([behind], [], { "PATCH /api/agents/a2": () => undefined });
  await user.click(within(await row("site-b")).getByRole("button", { name: /^site-b/ }));
  const input = screen.getByRole("spinbutton", { name: "Runs at once for site-b" });
  await user.clear(input);
  await user.type(input, "4");
  await user.click(screen.getByRole("button", { name: "Save" }));
  await waitFor(() => expect(requests.find((r) => r.method === "PATCH")?.body).toMatchObject({ maxConcurrency: 4 }));
});

it("pauses, resumes, blocks and removes from the row's menu", async () => {
  const user = userEvent.setup();
  const target = agent({ id: "a9", name: "worker", runningAttempts: 1 });
  const { requests } = serve([target], [], {
    "PATCH /api/agents/a9": (r) => {
      Object.assign(target, Object.fromEntries(Object.entries(r.body as object).filter(([, v]) => v !== null)));
    },
    "DELETE /api/agents/a9": () => undefined,
  });
  const menu = () => screen.getByRole("button", { name: "Actions for worker" });
  await user.click(await screen.findByRole("button", { name: "Actions for worker" }));
  expect(menu()).toHaveAttribute("aria-expanded", "true");
  expect(screen.getByText("No new work. What it is running finishes.")).toBeVisible();
  await user.keyboard("{Escape}");
  expect(menu()).toHaveAttribute("aria-expanded", "false");
  expect(menu()).toHaveFocus();

  await user.click(menu());
  await user.click(screen.getByRole("button", { name: /^Pause/ }));
  await waitFor(() => expect(requests.filter((r) => r.method === "PATCH").at(-1)?.body).toMatchObject({ draining: true, disabled: null }));
  expect(await screen.findByText("Connected · Paused")).toBeVisible();

  await user.click(menu());
  await user.click(screen.getByRole("button", { name: /^Resume/ }));
  await waitFor(() => expect(requests.filter((r) => r.method === "PATCH").at(-1)?.body).toMatchObject({ draining: false }));

  await user.click(menu());
  await user.click(screen.getByRole("button", { name: /^Block/ }));
  await waitFor(() => expect(requests.filter((r) => r.method === "PATCH").at(-1)?.body).toMatchObject({ disabled: true, draining: null }));

  await user.click(menu());
  await user.click(screen.getByRole("button", { name: /^Remove/ }));
  const dialog = screen.getByRole("dialog", { name: "Remove worker?" });
  expect(dialog).toHaveTextContent("This does not delete its VM");
  expect(dialog).toHaveTextContent("It is running 1 attempt");
  await user.click(within(dialog).getByRole("button", { name: "Remove the agent" }));
  await waitFor(() => expect(requests.some((r) => r.method === "DELETE" && r.path === "/api/agents/a9")).toBe(true));
});

it("closes the menu on a click outside it", async () => {
  const user = userEvent.setup();
  serve([behind]);
  const menu = await screen.findByRole("button", { name: "Actions for site-b" });
  await user.click(menu);
  await user.click(screen.getByText("Agents", { selector: "h1" }));
  expect(menu).toHaveAttribute("aria-expanded", "false");
});

it("updates an agent, says it finishes its runs first, and shows a refusal next to the button", async () => {
  const user = userEvent.setup();
  const working = agent({ ...behind, id: "a5", name: "site-e", work: busy.work });
  const { requests } = serve([behind, working], [], {
    "POST /api/agents/a2/update": () => undefined,
    "POST /api/agents/a5/update": () => problem(409, "site-e is already being updated."),
    "POST /api/agents/update": () => undefined,
  });
  expect(await screen.findByText(/2 updates available/)).toBeVisible();
  await user.click(within(await row("site-b")).getByRole("button", { name: "Update to 0.8.1" }));
  await waitFor(() => expect(requests.some((r) => r.method === "POST" && r.path === "/api/agents/a2/update")).toBe(true));

  const e = await row("site-e");
  expect(e).toHaveTextContent("It finishes its runs first.");
  expect(await row("site-b")).not.toHaveTextContent("It finishes its runs first.");
  await user.click(within(e).getByRole("button", { name: "Update to 0.8.1" }));
  expect(await within(e).findByRole("alert")).toHaveTextContent("site-e is already being updated.");

  await user.click(screen.getByRole("button", { name: "Update all" }));
  await waitFor(() => expect(requests.some((r) => r.method === "POST" && r.path === "/api/agents/update")).toBe(true));
});

it("shows an update waiting, going and failed, and takes a waiting one out of the queue", async () => {
  const user = userEvent.setup();
  const queued = agent({ id: "q", name: "queued", version: "0.7.1", update: { state: "queued", targetVersion: "0.8.1", reason: null, changeId: null, runId: null } });
  const going = agent({
    id: "u", name: "going", version: "0.7.1", draining: true,
    update: { state: "updating", targetVersion: "0.8.1", reason: null, changeId: "c1", runId: "r1" },
  });
  const failed = agent({
    id: "f", name: "broken", version: "0.7.1",
    update: { state: "failed", targetVersion: "0.8.1", reason: "The guest did not come back.", changeId: null, runId: "r2" },
  });
  const { requests } = serve([queued, going, failed], [], {
    "DELETE /api/agents/q/update": () => undefined,
    "POST /api/agents/f/update": () => undefined,
  });

  const q = await row("queued");
  expect(q).toHaveTextContent("Waiting for another update");
  await user.click(within(q).getByRole("button", { name: "Cancel" }));
  await waitFor(() => expect(requests.some((r) => r.method === "DELETE" && r.path === "/api/agents/q/update")).toBe(true));

  const g = await row("going");
  expect(g).toHaveTextContent("Updating to 0.8.1");
  expect(g).toHaveTextContent("Connected · Updating");
  expect(g).not.toHaveTextContent("Paused");
  expect(within(g).getByRole("link", { name: "View change" })).toHaveAttribute("href", "/changes/c1");
  expect(within(g).getByRole("link", { name: "View run" })).toHaveAttribute("href", "/runs/r1");

  const f = await row("broken");
  expect(f).toHaveTextContent("Update failed");
  expect(f).toHaveTextContent("The guest did not come back.");
  expect(within(f).getByRole("link", { name: "View run" })).toHaveAttribute("href", "/runs/r2");
  await user.click(within(f).getByRole("button", { name: "Try again" }));
  await waitFor(() => expect(requests.some((r) => r.method === "POST" && r.path === "/api/agents/f/update")).toBe(true));
});

it("makes a token at once, copies it, and makes another in its place", async () => {
  const user = userEvent.setup();
  vi.stubGlobal("isSecureContext", true);
  const writeText = vi.fn(async () => {});
  Object.defineProperty(navigator, "clipboard", { value: { writeText }, configurable: true });
  let made = 0;
  const { requests } = serve([behind], [], {
    "POST /api/agents/tokens": () => {
      made++;
      return {
        id: `t${made}`, token: `octe_${made}`, command: `octopushy agent enroll --token octe_${made}`,
        expiresAt: new Date(Date.now() + 3_600_000).toISOString(), caFingerprint: "SHA256:abc",
      };
    },
    "DELETE /api/agents/tokens/t1": () => undefined,
  });
  await user.click(await screen.findByRole("button", { name: "Enroll an agent" }));
  const dialog = await screen.findByRole("dialog", { name: "Enroll an agent" });
  expect(await within(dialog).findByText("octe_1")).toBeVisible();
  expect(requests.find((r) => r.method === "POST" && r.path === "/api/agents/tokens")?.body).toEqual({
    name: null, labels: [], maxConcurrency: 2, expiresInMinutes: 60,
  });
  expect(dialog).toHaveTextContent("Expires in 1 hour");
  expect(dialog).toHaveTextContent("Shown once.");

  await user.click(within(dialog).getByRole("button", { name: "Copy token" }));
  expect(writeText).toHaveBeenLastCalledWith("octe_1");
  expect(within(dialog).getByText("Copied to the clipboard")).toBeInTheDocument();
  expect(within(dialog).getByRole("button", { name: /^Copied/ })).toBeVisible();
  await user.click(within(dialog).getByRole("button", { name: "Copy command" }));
  expect(writeText).toHaveBeenLastCalledWith("octopushy agent enroll --token octe_1");
  await user.click(within(dialog).getByRole("button", { name: "Copy the CA fingerprint" }));
  expect(writeText).toHaveBeenLastCalledWith("SHA256:abc");

  const more = within(dialog).getByRole("button", { name: "Name it or change when it expires" });
  expect(more).toHaveAttribute("aria-expanded", "false");
  await user.click(more);
  await user.type(within(dialog).getByLabelText(/Name/), "site-c");
  await user.click(within(dialog).getByRole("button", { name: "1 day" }));
  expect(within(dialog).getByRole("button", { name: "1 day" })).toHaveAttribute("aria-pressed", "true");
  await user.click(within(dialog).getByRole("button", { name: "Make another with these" }));
  expect(await within(dialog).findByText("octe_2")).toBeVisible();
  const order = requests.filter((r) => r.method !== "GET").map((r) => `${r.method} ${r.path}`);
  expect(order).toEqual(["POST /api/agents/tokens", "DELETE /api/agents/tokens/t1", "POST /api/agents/tokens"]);
  expect(requests.filter((r) => r.method === "POST").at(-1)?.body).toEqual({
    name: "site-c", labels: [], maxConcurrency: 2, expiresInMinutes: 1440,
  });
  expect(dialog).toHaveTextContent("Expires in 1 day");
  expect(dialog).toHaveTextContent("site-c");
  expect(dialog).not.toHaveTextContent(/label/i);
});

it("lists the tokens waiting to enroll and reveals the used and expired ones", async () => {
  const user = userEvent.setup();
  const tokens: EnrollmentToken[] = [
    { id: "w", name: "pve02-agent-01", labels: [], createdAt: minutesAgo(30), expiresAt: new Date(Date.now() + 3_600_000).toISOString(), usedAt: null, agentId: null },
    { id: "e", name: null, labels: [], createdAt: minutesAgo(3000), expiresAt: minutesAgo(2940), usedAt: null, agentId: null },
    { id: "u", name: "site-b", labels: ["home", "gpu"], createdAt: minutesAgo(20000), expiresAt: minutesAgo(19000), usedAt: minutesAgo(19990), agentId: "a2" },
  ];
  const { requests } = serve([behind], tokens, { "DELETE /api/agents/tokens/w": () => undefined });
  const section = await screen.findByRole("region", { name: "Waiting to enroll" });
  expect(await within(section).findByText("pve02-agent-01")).toBeVisible();
  expect(section).toHaveTextContent("Made 30 min ago, expires in 1 h");
  expect(within(section).queryByText("Unnamed")).not.toBeInTheDocument();

  const reveal = within(section).getByRole("button", { name: "Show 2 used or expired tokens" });
  await user.click(reveal);
  expect(within(section).getByText("Unnamed")).toBeVisible();
  expect(section).toHaveTextContent(/expired 2 days ago/);
  expect(section).toHaveTextContent(/used by site-b/);
  expect(section).not.toHaveTextContent("gpu");

  await user.click(within(section).getAllByRole("button", { name: "Revoke" })[0]!);
  await waitFor(() => expect(requests.some((r) => r.method === "DELETE" && r.path === "/api/agents/tokens/w")).toBe(true));
});

it("leaves the token section out when there are none, and labels out of the page", async () => {
  serve([busy, behind]);
  await row("site-a");
  expect(screen.queryByRole("region", { name: "Waiting to enroll" })).not.toBeInTheDocument();
  expect(document.body).not.toHaveTextContent(/label/i);
});

it("offers enrollment when there are no agents", async () => {
  serve([]);
  expect(await screen.findByText("No agents enrolled")).toBeVisible();
  expect(screen.getAllByRole("button", { name: "Enroll an agent" })).toHaveLength(2);
});
