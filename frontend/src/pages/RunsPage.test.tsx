import { screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { expect, it } from "vitest";
import type { RunSummary } from "../api/types";
import { mockApi, problem, renderAt } from "../test/utils";
import { runProgress } from "../lib/runs";
import { RunsPage } from "./RunsPage";

const sha = "3f6c2a9b8e7d6c5b4a39281706f5e4d3c2b1a09f8e7d6c5b4a3928170615e4d3";
const at = "2026-10-20T08:00:00Z";

type Step = RunSummary["steps"][number];
const step = (name: string, status: string, more: Partial<Step> = {}): Step => ({
  id: `step-${name}`,
  name,
  kind: name === "approval" ? "approval" : "agent",
  status,
  counts: null,
  planSha256: null,
  ...more,
});

function run(number: number, overrides: Partial<RunSummary>): RunSummary {
  return {
    id: `run-${number}`,
    number,
    operation: "provision",
    resourceId: "guest-1",
    resourceKind: "guest",
    address: "home/pve01/bb",
    status: "succeeded",
    detail: null,
    reason: "revision",
    commit: "e6b1282aa4d1c0ffee",
    createdAt: at,
    startedAt: at,
    finishedAt: at,
    currentStep: null,
    waiting: null,
    steps: [step("plan", "succeeded"), step("approval", "succeeded"), step("apply", "succeeded")],
    canCancel: false,
    ...overrides,
  };
}

const waiting = run(49, {
  status: "awaiting-approval",
  finishedAt: null,
  canCancel: true,
  steps: [
    step("plan", "succeeded", { counts: { create: 0, update: 1, delete: 0, replace: 0 } }),
    step("approval", "awaiting-approval", { planSha256: sha }),
    step("apply", "pending"),
  ],
});
const running = run(50, {
  status: "running",
  finishedAt: null,
  canCancel: true,
  address: "home/korriban/dns",
  steps: [step("plan", "succeeded"), step("approval", "skipped"), step("apply", "running")],
});
const service = run(41, { operation: "pipeline", resourceKind: "service", address: "home/pve01/web/app", reason: "manual" });

const show = (runs: RunSummary[], route = "/runs") => {
  const api = mockApi({
    "GET /api/runs": () => runs,
    "POST /api/runs/run-49/steps/step-approval/approve": () => undefined,
    "POST /api/runs/run-52/steps/step-approval/approve": () => undefined,
    "POST /api/runs/run-50/cancel": () => undefined,
  });
  renderAt(<RunsPage />, { path: "/runs", route });
  return api;
};

it("sorts runs into sections, leaves out an empty one, and names the target instead of its path", async () => {
  show([waiting, service]);
  const needsYou = await screen.findByRole("list", { name: "Waiting for you" });
  expect(within(needsYou).getByRole("link", { name: "#49 Provision" })).toHaveAttribute("href", "/runs/run-49");
  expect(within(needsYou).getByText("Machine")).toBeInTheDocument();
  expect(within(needsYou).getByText("bb")).toBeInTheDocument();
  expect(within(needsYou).getByText("pve01")).toBeInTheDocument();
  expect(within(needsYou).getByRole("img", { name: "Step 2 of 3: Approval, awaiting approval" })).toBeInTheDocument();
  expect(within(needsYou).getByText("1 to update")).toBeInTheDocument();
  expect(screen.queryByText("home/pve01/bb")).not.toBeInTheDocument();
  expect(screen.queryByRole("list", { name: "In progress" })).not.toBeInTheDocument();

  const earlier = screen.getByRole("list", { name: "Earlier" });
  expect(within(earlier).getByText("Service")).toBeInTheDocument();
  expect(within(earlier).getByText("on web")).toBeInTheDocument();
  expect(within(earlier).getByText("Manual")).toBeInTheDocument();
});

it("approves a waiting run from the list with the step and fingerprint the list showed", async () => {
  const { requests } = show([waiting]);
  await userEvent.click(await screen.findByRole("button", { name: "Approve" }));
  const dialog = await screen.findByRole("dialog", { name: "Approve run #49?" });
  expect(within(dialog).getByText(/1 to update/)).toBeInTheDocument();
  await userEvent.click(within(dialog).getByRole("button", { name: "Approve" }));
  await waitFor(() => expect(requests.some((r) => r.method === "POST")).toBe(true));
  const post = requests.find((r) => r.method === "POST")!;
  expect(post.path).toBe("/api/runs/run-49/steps/step-approval/approve");
  expect(post.body).toEqual({ planSha256: sha, note: null });
});

const waitingToo = run(52, {
  ...waiting,
  id: "run-52",
  number: 52,
  address: "home/pve01/dns",
  steps: [step("plan", "succeeded"), step("approval", "awaiting-approval", { planSha256: "b".repeat(64) }), step("apply", "pending")],
});

it("approves every waiting run shown, each with its own fingerprint, after one confirmation", async () => {
  const { requests } = show([waiting, waitingToo, running]);
  await userEvent.click(await screen.findByRole("button", { name: "Approve all 2" }));
  const dialog = await screen.findByRole("dialog", { name: "Approve 2 runs?" });
  expect(within(dialog).getByText(/Provision bb/)).toBeInTheDocument();
  expect(within(dialog).getByText(/Provision dns/)).toBeInTheDocument();
  await userEvent.click(within(dialog).getByRole("button", { name: "Approve 2" }));
  await waitFor(() => expect(screen.queryByRole("dialog")).not.toBeInTheDocument());
  expect(requests.filter((r) => r.method === "POST").map((r) => [r.path, r.body])).toEqual([
    ["/api/runs/run-49/steps/step-approval/approve", { planSha256: sha, note: null }],
    ["/api/runs/run-52/steps/step-approval/approve", { planSha256: "b".repeat(64), note: null }],
  ]);
});

it("approves only the waiting runs the filters show, and offers no Approve all for a single one", async () => {
  show([waiting, waitingToo], "/runs?q=dns");
  expect(await screen.findByRole("link", { name: "#52 Provision" })).toBeInTheDocument();
  expect(screen.queryByRole("button", { name: /Approve all/ })).not.toBeInTheDocument();
});

it("keeps going when one approval is refused and names the run that was not approved", async () => {
  const { requests } = mockApi({
    "GET /api/runs": () => [waiting, waitingToo],
    "POST /api/runs/run-49/steps/step-approval/approve": () => problem(409, "The plan changed. Plan again."),
    "POST /api/runs/run-52/steps/step-approval/approve": () => undefined,
  });
  renderAt(<RunsPage />, { path: "/runs", route: "/runs" });
  await userEvent.click(await screen.findByRole("button", { name: "Approve all 2" }));
  await userEvent.click(within(await screen.findByRole("dialog")).getByRole("button", { name: "Approve 2" }));
  expect(await screen.findByText(/1 of 2 were not approved\. #49: The plan changed\. Plan again\./)).toBeInTheDocument();
  expect(requests.filter((r) => r.method === "POST")).toHaveLength(2);
});

it("cancels a run in progress after a confirmation, and offers nothing for one that cannot be cancelled", async () => {
  const { requests } = show([running, run(51, { status: "running", finishedAt: null, canCancel: false })]);
  const cancel = await screen.findAllByRole("button", { name: "Cancel" });
  expect(cancel).toHaveLength(1);
  await userEvent.click(cancel[0]);
  await userEvent.click(await screen.findByRole("button", { name: "Cancel the run" }));
  await waitFor(() => expect(requests.some((r) => r.method === "POST")).toBe(true));
  expect(requests.filter((r) => r.method === "POST").map((r) => r.path)).toEqual(["/api/runs/run-50/cancel"]);
});

it("shows only the latest 50 finished runs, and every run that waits", async () => {
  const finished = Array.from({ length: 60 }, (_, i) => run(200 - i, {}));
  show([waiting, ...finished]);
  const earlier = await screen.findByRole("list", { name: "Earlier" });
  expect(within(earlier).getAllByRole("listitem")).toHaveLength(50);
  expect(within(earlier).getByRole("link", { name: "#200 Provision" })).toBeInTheDocument();
  expect(within(earlier).queryByRole("link", { name: "#150 Provision" })).not.toBeInTheDocument();
  expect(screen.getByText("The latest 50 are shown. Filter to find an older run.")).toBeInTheDocument();
  expect(screen.getByRole("link", { name: "#49 Provision" })).toBeInTheDocument();
});

it("filters by type, node and name", async () => {
  show([waiting, running, service], "/runs?type=service");
  expect(await screen.findByRole("link", { name: "#41 Pipeline" })).toBeInTheDocument();
  expect(screen.queryByRole("link", { name: "#49 Provision" })).not.toBeInTheDocument();

  await userEvent.click(screen.getByRole("link", { name: "All" }));
  await userEvent.selectOptions(screen.getByRole("combobox", { name: "Node" }), "korriban");
  expect(screen.getByRole("link", { name: "#50 Provision" })).toBeInTheDocument();
  expect(screen.queryByRole("link", { name: "#49 Provision" })).not.toBeInTheDocument();

  await userEvent.selectOptions(screen.getByRole("combobox", { name: "Node" }), "");
  await userEvent.type(screen.getByRole("searchbox", { name: "Filter by machine or service" }), "web");
  expect(screen.getByRole("link", { name: "#41 Pipeline" })).toBeInTheDocument();
  expect(screen.queryByRole("link", { name: "#50 Provision" })).not.toBeInTheDocument();
});

it("adds up every plan a pipeline's approval covers, and says nothing when they change nothing", () => {
  const pipeline = (first: number, second: number) =>
    run(47, {
      status: "awaiting-approval",
      finishedAt: null,
      detail: "Approval is manual.",
      steps: [
        step("plan", "succeeded", { id: "plan-1", counts: { create: first, update: 0, delete: 0, replace: 0 } }),
        step("plan", "succeeded", { id: "plan-2", counts: { create: second, update: second, delete: 0, replace: 0 } }),
        step("preview", "succeeded"),
        step("approval", "awaiting-approval", { planSha256: sha }),
      ],
    });
  expect(runProgress(pipeline(2, 1)).note).toBe("3 to create, 1 to update");
  expect(runProgress(pipeline(0, 0)).note).toBe("Approval is manual.");
});
