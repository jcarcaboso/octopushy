import { screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, describe, expect, it, vi } from "vitest";
import type { RunDetail } from "../api/types";
import { mockApi, problem, renderAt } from "../test/utils";
import { RunPage } from "./RunPage";

const sha = "3f6c2a9b8e7d6c5b4a39281706f5e4d3c2b1a09f8e7d6c5b4a3928170615e4d3";
const created = "2026-10-20T08:00:00Z";

it("retries a failed guest step without requesting another infrastructure plan", async () => {
  const detail = runDetail({ status: "failed", currentStep: "grow-root" });
  detail.steps = [{ ...detail.steps[0], name: "grow-root", kind: "agent", status: "failed", plan: null, approval: null }];
  const { requests } = mockApi({
    "GET /api/runs/run-1": () => detail,
    "POST /api/runs/run-1/retry-filesystem": () => undefined,
  });
  renderAt(<RunPage />, { path: "/runs/:id", route: "/runs/run-1" });
  await userEvent.click(await screen.findByRole("button", { name: "Retry guest step" }));
  await waitFor(() => expect(requests.some((r) => r.method === "POST")).toBe(true));
  expect(requests.filter((r) => r.method === "POST").map((r) => r.path)).toEqual(["/api/runs/run-1/retry-filesystem"]);
});

it.each([
  { lost: "read-only", mutating: false, offered: true },
  { lost: "mutating", mutating: true, offered: false },
])("offers the guest retry after a lost $lost attempt only when it could not have changed anything", async ({ mutating, offered }) => {
  const detail = runDetail({ status: "failed", currentStep: "verify-root" });
  const lost = { ...detail.steps[0].attempts[0], id: "attempt-lost", status: "uncertain", mutating };
  detail.steps = [{ ...detail.steps[0], name: "verify-root", kind: "agent", status: "failed", plan: null, approval: null, attempts: [lost] }];
  mockApi({ "GET /api/runs/run-1": () => detail });
  renderAt(<RunPage />, { path: "/runs/:id", route: "/runs/run-1" });
  await screen.findByText(/Independently measures guest disk/);
  expect(screen.queryByRole("button", { name: "Retry guest step" }) !== null).toBe(offered);
});

function runDetail(overrides: Partial<RunDetail["run"]> = {}): RunDetail {
  return {
    run: {
      id: "run-1",
      number: 12,
      operation: "provision",
      resourceId: "guest-1",
      resourceKind: "guest",
      address: "home/proxmox/korriban/guests/krb-app-01",
      status: "awaiting-approval",
      detail: "Approval is manual (set by home/proxmox/korriban/guests/krb-app-01/guest.yaml).",
      reason: "manual",
      commit: "e6b1282aa4d1c0ffee",
      createdAt: created,
      startedAt: created,
      finishedAt: null,
      currentStep: "approval",
      waiting: null,
      ...overrides,
    },
    steps: [
      {
        id: "step-plan",
        ordinal: 0,
        name: "plan",
        kind: "agent",
        status: "succeeded",
        detail: null,
        startedAt: created,
        finishedAt: created,
        outputs: null,
        plan: {
          hasChanges: true,
          planSha256: sha,
          toolVersion: "1.10.6",
          changes: [
            { address: "proxmox_virtual_environment_vm.this", type: "proxmox_virtual_environment_vm", name: "this", actions: ["create", "delete"] },
            { address: "terraform_data.marker", type: "terraform_data", name: "marker", actions: ["create"] },
          ],
          counts: { create: 1, update: 0, delete: 0, replace: 1 },
        },
        approval: null,
        attempts: [
          {
            id: "attempt-1",
            number: 1,
            executor: "opentofu",
            action: "preview",
            status: "succeeded",
            waitingReason: null,
            agentId: "agent-1",
            agentName: "site-a-01",
            createdAt: created,
            assignedAt: created,
            startedAt: created,
            finishedAt: created,
            error: null,
            lateResult: false,
            logLines: 42,
            mutating: false,
          },
        ],
      },
      {
        id: "step-approval",
        ordinal: 1,
        name: "approval",
        kind: "approval",
        status: "awaiting-approval",
        detail: "Approval is manual (set by home/proxmox/korriban/guests/krb-app-01/guest.yaml).",
        startedAt: created,
        finishedAt: null,
        outputs: null,
        plan: null,
        approval: { required: true, decision: null, decidedBy: null, decidedAt: null, note: null, planSha256: sha },
        attempts: [],
      },
      {
        id: "step-apply",
        ordinal: 2,
        name: "apply",
        kind: "agent",
        status: "pending",
        detail: null,
        startedAt: null,
        finishedAt: null,
        outputs: null,
        plan: null,
        approval: null,
        attempts: [],
      },
    ],
    agentLabels: ["site-a"],
    agentLabelsSetBy: "home/lab.yaml",
    policy: {
      approval: "manual",
      approvalSetBy: "home/proxmox/korriban/guests/krb-app-01/guest.yaml",
      destructive: "manual",
      destructiveSetBy: "octopushy.yaml",
    },
    canCancel: true,
    canClose: false,
  };
}

describe("RunPage", () => {
  afterEach(() => vi.unstubAllGlobals());

  it("shows the plan's changes and approves with the plan hash the step shows", async () => {
    const user = userEvent.setup();
    const { requests } = mockApi({
      "GET /api/runs/run-1": () => runDetail(),
      "POST /api/runs/run-1/steps/step-approval/approve": () => undefined,
    });

    renderAt(<RunPage />, { path: "/runs/:id", route: "/runs/run-1" });

    expect(await screen.findByRole("heading", { name: /Run #12/ })).toBeInTheDocument();
    expect(screen.getByText("proxmox_virtual_environment_vm.this")).toBeInTheDocument();
    expect(screen.getAllByText("Replace").length).toBeGreaterThan(0);
    expect(screen.getByText("OpenTofu 1.10.6")).toBeInTheDocument();
    for (const link of screen.getAllByRole("link", { name: "home/proxmox/korriban/guests/krb-app-01" })) {
      expect(link).toHaveAttribute("href", "/guests/guest-1");
    }
    expect(screen.getByText("Your approval is needed")).toBeInTheDocument();
    expect(screen.getAllByTitle(sha).length).toBeGreaterThan(0);

    await user.type(screen.getByLabelText(/Note/), "Looks right");
    await user.click(screen.getByRole("button", { name: "Approve" }));

    await waitFor(() => expect(requests.some((r) => r.method === "POST")).toBe(true));
    const approve = requests.find((r) => r.method === "POST")!;
    expect(approve.path).toBe("/api/runs/run-1/steps/step-approval/approve");
    expect(approve.body).toEqual({ planSha256: sha, note: "Looks right" });
  });

  it("shows the problem title when approval is refused", async () => {
    const user = userEvent.setup();
    mockApi({
      "GET /api/runs/run-1": () => runDetail(),
      "POST /api/runs/run-1/steps/step-approval/approve": () =>
        problem(409, "The plan changed since it was shown; review the current plan."),
    });

    renderAt(<RunPage />, { path: "/runs/:id", route: "/runs/run-1" });
    await user.click(await screen.findByRole("button", { name: "Approve" }));
    expect(await screen.findByText("The plan changed since it was shown; review the current plan.")).toBeInTheDocument();
  });

  it("approves setup-only runs without claiming to apply a saved infrastructure plan", async () => {
    const user = userEvent.setup();
    const detail = runDetail();
    detail.steps[0].plan = {
      hasChanges: false, planSha256: null, toolVersion: "1.13.1", changes: [],
      counts: { create: 0, update: 0, delete: 0, replace: 0 },
    };
    detail.steps[1].detail = "This run installs the disk reporter inside the VM after verification.";
    const { requests } = mockApi({
      "GET /api/runs/run-1": () => detail,
      "POST /api/runs/run-1/steps/step-approval/approve": () => undefined,
    });
    renderAt(<RunPage />, { path: "/runs/:id", route: "/runs/run-1" });
    expect(await screen.findByText("Setup approval fingerprint")).toBeInTheDocument();
    expect(screen.getByText(/Approving authorizes the pending guest setup/)).toBeInTheDocument();
    expect(screen.queryByText(/Approving applies exactly this saved plan/)).not.toBeInTheDocument();
    await user.click(screen.getByRole("button", { name: "Approve" }));
    await waitFor(() => expect(requests.some((r) => r.method === "POST")).toBe(true));
    expect(requests.find((r) => r.method === "POST")!.body).toEqual({ planSha256: sha, note: null });
  });

  it("rejects without a plan hash", async () => {
    const user = userEvent.setup();
    const { requests } = mockApi({
      "GET /api/runs/run-1": () => runDetail(),
      "POST /api/runs/run-1/steps/step-approval/reject": () => undefined,
    });

    renderAt(<RunPage />, { path: "/runs/:id", route: "/runs/run-1" });
    await user.click(await screen.findByRole("button", { name: "Reject" }));
    await waitFor(() => expect(requests.some((r) => r.method === "POST")).toBe(true));
    expect(requests.find((r) => r.method === "POST")!.body).toEqual({ planSha256: null, note: null });
  });

  it("shows why a running run is waiting", async () => {
    mockApi({
      "GET /api/runs/run-1": () =>
        runDetail({ status: "running", detail: null, currentStep: "plan", waiting: "Waiting for a connected agent with the labels home." }),
    });

    renderAt(<RunPage />, { path: "/runs/:id", route: "/runs/run-1" });
    expect(await screen.findByText(/Waiting for a connected agent with the labels home/)).toBeInTheDocument();
  });

  it("explains an uncertain outcome and offers to close the run", async () => {
    const detail = runDetail({ status: "uncertain", detail: "The agent stopped reporting." });
    detail.canCancel = false;
    detail.canClose = true;
    mockApi({ "GET /api/runs/run-1": () => detail });

    renderAt(<RunPage />, { path: "/runs/:id", route: "/runs/run-1" });
    expect(await screen.findByText("The outcome is uncertain")).toBeInTheDocument();
    expect(screen.getAllByText(/records the run as failed/).length).toBeGreaterThan(0);
    expect(screen.getAllByRole("button", { name: /Close/ }).length).toBeGreaterThan(0);
    expect(screen.queryByRole("button", { name: /Cancel run/ })).not.toBeInTheDocument();
  });
});

it.each(["prepare-worker", "install-worker", "wait-worker"])("retries known failed %s without another VM apply", async (name) => {
  const detail = runDetail({ status: "failed", currentStep: name });
  detail.steps = [{ ...detail.steps[0], name, kind: "agent", status: "failed", plan: null, approval: null }];
  const { requests } = mockApi({ "GET /api/runs/run-1": () => detail, "POST /api/runs/run-1/retry-filesystem": () => undefined });
  renderAt(<RunPage />, { path: "/runs/:id", route: "/runs/run-1" });
  await userEvent.click(await screen.findByRole("button", { name: "Retry guest step" }));
  await waitFor(() => expect(requests.filter((r) => r.method === "POST")).toHaveLength(1));
  expect(requests.find((r) => r.method === "POST")?.path).toBe("/api/runs/run-1/retry-filesystem");
});
