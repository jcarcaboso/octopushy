import { screen, within } from "@testing-library/react";
import { afterEach, expect, it, vi } from "vitest";
import type { Agent, Overview, RunSummary, Uptime, UptimeCheck } from "../api/types";
import { mockApi, renderAt } from "../test/utils";
import { OverviewPage } from "./OverviewPage";

afterEach(() => {
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
});

const minutesAgo = (m: number) => new Date(Date.now() - m * 60_000).toISOString();

function run(more: Partial<RunSummary> & { id: string; number: number }): RunSummary {
  return {
    operation: "provision", resourceId: "g-media", resourceKind: "guest", address: "home/pve01/media", status: "succeeded",
    detail: null, reason: "revision", commit: null, createdAt: minutesAgo(60), startedAt: minutesAgo(60), finishedAt: minutesAgo(55),
    currentStep: null, waiting: null, steps: [], canCancel: false,
    ...more,
  };
}

const approval = run({
  id: "r48", number: 48, operation: "configure", resourceId: "g-ai", address: "home/pve01/pve01-ai", status: "awaiting-approval",
  detail: "The plan changes its memory.", finishedAt: null, createdAt: minutesAgo(12),
});
const failedRun = run({ id: "r44", number: 44, status: "failed", detail: "Install Docker exited with 2." });
const serviceRun = run({ id: "r47", number: 47, operation: "pipeline", resourceId: "s-app", resourceKind: "service", address: "home/pve01/web/app" });

function overview(more: Partial<Overview> = {}): Overview {
  return {
    repository: {
      url: "https://git.example.org/lab.git", branch: "main", pollSeconds: 60, credentialKind: "none", credentialUsername: null,
      credentialSet: false, lastFetchAt: minutesAgo(1), lastFetchError: null, headCommit: null, revision: null,
    },
    counts: { guests: 8, inSync: 5, pending: 0, running: 1, awaitingApproval: 1, blocked: 0, failed: 1, uncertain: 0, invalid: 0, missing: 0 },
    agentsConnected: 2,
    agentsTotal: 3,
    attention: [approval],
    observeErrors: [],
    recent: [approval, serviceRun, failedRun],
    failed: [failedRun],
    week: { total: 14, succeeded: 12, failed: 0, cancelled: 0, unfinished: 2 },
    ...more,
  };
}

function check(more: Partial<UptimeCheck> & { key: string; kind: string }): UptimeCheck {
  return {
    installation: "home/pve01", host: "pve01", resource: null, url: "https://10.0.0.1:8006", status: "up", detail: null, since: minutesAgo(600),
    checkedAt: minutesAgo(1), elapsedMs: 12, agent: "a1", uptime: { day: 100, week: 100, month: 100 }, beats: "u".repeat(96),
    latency: [], daily: new Array(30).fill(100),
    ...more,
  };
}

const uptime: Uptime = {
  checking: true, intervalSeconds: 60, generatedAt: minutesAgo(0),
  checks: [
    check({ key: "n1", kind: "proxmox-api" }),
    check({ key: "m1", kind: "guest-ssh", resource: "home/pve01/media" }),
    check({ key: "m2", kind: "guest-ssh", resource: "home/pve01/dns", status: "pending", uptime: { day: null, week: null, month: null }, daily: new Array(30).fill(null) }),
    check({
      key: "s1", kind: "service-http", resource: "home/pve01/media/jellyfin", status: "down", detail: "HTTP 502", since: minutesAgo(40),
      uptime: { day: 97, week: 99, month: 99.6 },
    }),
  ],
};

function agent(more: Partial<Agent> & { id: string; name: string }): Agent {
  return {
    labels: [], maxConcurrency: 2, disabled: false, draining: false, connected: true, version: "0.8.1", executors: [], toolRuntime: null,
    runningAttempts: 0, reportsReachability: false, enrolledAt: minutesAgo(60_000), lastSeenAt: minutesAgo(0),
    certificateExpiresAt: minutesAgo(-100_000), reachability: [], work: [], guest: null,
    update: { state: "current", targetVersion: null, reason: null, changeId: null, runId: null },
    ...more,
  };
}

const agents = [
  agent({ id: "a1", name: "site-a" }),
  agent({ id: "a2", name: "site-b", update: { state: "available", targetVersion: "0.9.0", reason: null, changeId: null, runId: null } }),
  agent({ id: "a3", name: "laptop", connected: false, lastSeenAt: minutesAgo(4300) }),
  agent({ id: "a4", name: "old-box", connected: false, disabled: true }),
];

function serve(data: Overview = overview()) {
  mockApi({ "GET /api/overview": () => data, "GET /api/uptime": () => uptime, "GET /api/agents": () => agents });
  renderAt(<OverviewPage />, { path: "/", route: "/" });
}

const tile = async (name: string) => screen.findByRole("region", { name: new RegExp(`^${name}`) });

it("shows the guests by state, each linking to its filter", async () => {
  serve();
  const guests = await tile("Guests");
  expect(within(guests).getByText("5 of 8")).toBeInTheDocument();
  expect(within(guests).getByRole("link", { name: "1 guest queued or running" })).toHaveAttribute("href", "/inventory?status=running,queued");
  expect(within(guests).getByRole("link", { name: "5 guests in sync" })).toHaveAttribute("href", "/inventory?status=in-sync");
  expect(within(guests).queryByRole("link", { name: /blocked/ })).not.toBeInTheDocument();
});

it("lists an approval and a failed run under Needs you, with the right links", async () => {
  serve();
  const needs = await tile("Needs you");
  expect(within(needs).getByText("pve01-ai awaits approval")).toBeInTheDocument();
  expect(within(needs).getByText("media failed")).toBeInTheDocument();
  expect(within(needs).getByRole("link", { name: "Review the plan" })).toHaveAttribute("href", "/runs/r48");
  expect(within(needs).getByRole("link", { name: "Open the run" })).toHaveAttribute("href", "/runs/r44");
});

it("says when nothing needs you", async () => {
  serve(overview({ attention: [], failed: [] }));
  expect(await screen.findByText("Nothing needs you")).toBeInTheDocument();
});

it("leaves the zero parts out of the week line", async () => {
  serve();
  expect(await screen.findByText("14 in the last 7 days: 12 succeeded, 2 not finished")).toBeInTheDocument();
});

it("shows node, VM and type for a machine run and a service run", async () => {
  serve();
  const table = await screen.findByRole("table");
  const machine = within(table).getByRole("link", { name: "#48" }).closest("tr")!;
  expect(within(machine).getByText("pve01")).toBeInTheDocument();
  expect(within(machine).getByRole("link", { name: "pve01-ai" })).toHaveAttribute("href", "/guests/g-ai");
  expect(within(machine).getByText("Machine")).toBeInTheDocument();
  const service = within(table).getByRole("link", { name: "#47" }).closest("tr")!;
  expect(within(service).getByText("web")).toBeInTheDocument();
  expect(within(service).queryByRole("link", { name: "web" })).not.toBeInTheDocument();
  expect(within(service).getByRole("link", { name: "app" })).toHaveAttribute("href", "/services/s-app");
  expect(service).toHaveTextContent("Service app");
});

it("counts up and pending checks by kind and names what is down", async () => {
  serve();
  const up = await tile("Up, 30 days");
  expect(await within(up).findByText("4 checks")).toBeInTheDocument();
  expect(within(up).getByText("1 of 1 up, 1 not answered yet")).toBeInTheDocument();
  expect(within(up).getByText("0 of 1 up")).toBeInTheDocument();
  expect(within(up).getByRole("img", { name: /^Nodes, the last 30 days: 30 days up/ })).toBeInTheDocument();
  const down = within(up).getByRole("link", { name: /jellyfin/ });
  expect(down).toHaveAttribute("href", "/inventory");
  expect(down).toHaveTextContent("jellyfin HTTP 502, down since 40 min ago");
});

it("names a missing agent and links the updates", async () => {
  serve();
  const tileEl = await tile("Agents");
  expect(within(tileEl).getByText("2 of 3")).toBeInTheDocument();
  expect(await within(tileEl).findByText("laptop")).toBeInTheDocument();
  expect(within(tileEl).queryByText("old-box")).not.toBeInTheDocument();
  expect(within(tileEl).getByRole("link", { name: "1 update available" })).toHaveAttribute("href", "/agents");
});
