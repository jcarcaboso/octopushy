import { screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, describe, expect, it, vi } from "vitest";
import type { RunDetail } from "../api/types";
import { mockApi, problem, renderAt } from "../test/utils";
import { RunPage } from "./RunPage";

const sha = "3f6c2a9b8e7d6c5b4a39281706f5e4d3c2b1a09f8e7d6c5b4a3928170615e4d3";
const created = "2026-10-20T08:00:00Z";

it("retries a failed stage of a pipeline and names its stage", async () => {
  const detail = runDetail({ operation: "pipeline", status: "failed", currentStep: null });
  detail.steps = [{ ...detail.steps[0], name: "playbook", stage: "deploy", kind: "agent", status: "failed", plan: null, approval: null }];
  const { requests } = mockApi({
    "GET /api/runs/run-1": () => detail,
    "POST /api/runs/run-1/retry-stage": () => undefined,
  });
  renderAt(<RunPage />, { path: "/runs/:id", route: "/runs/run-1" });
  await userEvent.click(await screen.findByRole("button", { name: "Retry this stage" }));
  await waitFor(() => expect(requests.some((r) => r.method === "POST")).toBe(true));
  expect(requests.filter((r) => r.method === "POST").map((r) => r.path)).toEqual(["/api/runs/run-1/retry-stage"]);
  expect(screen.getByText("deploy")).toBeInTheDocument();
});

it("force stops a change in progress that cannot be cancelled", async () => {
  const detail = runDetail({ status: "running" });
  detail.canCancel = false;
  detail.canForceStop = true;
  const { requests } = mockApi({
    "GET /api/runs/run-1": () => detail,
    "POST /api/runs/run-1/force-stop": () => undefined,
  });
  renderAt(<RunPage />, { path: "/runs/:id", route: "/runs/run-1" });
  expect(screen.queryByRole("button", { name: /Cancel run/ })).not.toBeInTheDocument();
  await userEvent.click(await screen.findByRole("button", { name: "Force stop" }));
  await userEvent.click(await screen.findByRole("button", { name: "Force stop the run" }));
  await waitFor(() => expect(requests.some((r) => r.method === "POST")).toBe(true));
  expect(requests.filter((r) => r.method === "POST").map((r) => r.path)).toEqual(["/api/runs/run-1/force-stop"]);
});

it("runs a failed run again as a new run and opens it", async () => {
  const detail = runDetail({ status: "failed", currentStep: null });
  detail.canRunAgain = true;
  const { requests } = mockApi({
    "GET /api/runs/run-1": () => detail,
    "POST /api/runs/run-1/run-again": () => ({ runId: "run-2" }),
    "GET /api/runs/run-2": () => runDetail({ id: "run-2", number: 13, status: "queued" }),
  });
  renderAt(<RunPage />, { path: "/runs/:id", route: "/runs/run-1" });
  await userEvent.click(await screen.findByRole("button", { name: "Run again" }));
  await userEvent.click(await screen.findByRole("button", { name: "Start a new run" }));
  expect(await screen.findByText(/Run #13/)).toBeInTheDocument();
  expect(requests.filter((r) => r.method === "POST").map((r) => r.path)).toEqual(["/api/runs/run-1/run-again"]);
});

it("offers no run again when the server says a run cannot be", async () => {
  mockApi({ "GET /api/runs/run-1": () => runDetail({ status: "failed", currentStep: null }) });
  renderAt(<RunPage />, { path: "/runs/:id", route: "/runs/run-1" });
  await screen.findByText(/Run #12/);
  expect(screen.queryByRole("button", { name: "Run again" })).toBeNull();
});

it("says when a step's network access is not modeled, and never calls it reachable", async () => {
  const detail = runDetail({ operation: "pipeline", status: "running", currentStep: null });
  detail.steps = [{ ...detail.steps[0], name: "apply", stage: "infra", kind: "agent", status: "running", plan: null, approval: null,
    networkAccess: "Network access not modeled: this step goes to any agent, and its reachability is not checked." }];
  mockApi({ "GET /api/runs/run-1": () => detail });
  renderAt(<RunPage />, { path: "/runs/:id", route: "/runs/run-1" });
  expect(await screen.findByText(/Network access not modeled/)).toBeInTheDocument();
  expect(screen.queryByText(/reachable/i)).not.toBeInTheDocument();
});

it("names the workflow a run executed, its version and the commit its files came from", async () => {
  const detail = runDetail({});
  detail.workflow = {
    id: "fedora-vm",
    version: "2.3",
    reference: "fedora-vm@2",
    commit: "c0ffee1234c0ffee1234c0ffee1234c0ffee1234",
    repository: "git@github.com:jcarcaboso/octopushy-lab.git",
  };
  mockApi({ "GET /api/runs/run-1": () => detail });
  renderAt(<RunPage />, { path: "/runs/:id", route: "/runs/run-1" });
  expect(await screen.findByText("fedora-vm@2.3")).toBeInTheDocument();
  expect(screen.getByText("c0ffee1234")).toBeInTheDocument();
  expect(screen.getByText("git@github.com:jcarcaboso/octopushy-lab.git")).toBeInTheDocument();
});

it("offers no stage retry once a change may have been lost", async () => {
  const detail = runDetail({ operation: "pipeline", status: "failed", currentStep: null });
  const lost = { ...detail.steps[0].attempts[0], id: "attempt-lost", status: "uncertain", mutating: true };
  detail.steps = [{ ...detail.steps[0], name: "apply", stage: "expose", kind: "agent", status: "failed", plan: null, approval: null, attempts: [lost] }];
  mockApi({ "GET /api/runs/run-1": () => detail });
  renderAt(<RunPage />, { path: "/runs/:id", route: "/runs/run-1" });
  await screen.findByText("expose");
  expect(screen.queryByRole("button", { name: "Retry this stage" })).toBeNull();
});

it("names one document for the whole pipeline when asking for approval", async () => {
  const detail = runDetail({ operation: "pipeline", status: "awaiting-approval", currentStep: "approval" });
  detail.steps[0] = { ...detail.steps[0], stage: "expose" };
  mockApi({ "GET /api/runs/run-1": () => detail });
  renderAt(<RunPage />, { path: "/runs/:id", route: "/runs/run-1" });
  expect(await screen.findByText(/Approving covers every stage/)).toBeInTheDocument();
  expect(screen.getByText("Pipeline document SHA-256")).toBeInTheDocument();
});

it("shows what a configure run will do, and approves that preview", async () => {
  const detail = runDetail({ operation: "configure", status: "awaiting-approval", currentStep: "approval" });
  detail.steps[0] = {
    ...detail.steps[0],
    name: "preview",
    plan: null,
    configurePreview: {
      action: "update-tools",
      playbook: "playbooks/update-tools.yml",
      commit: "e6b1282aa4d1c0ffee",
      image: "docker.io/skorcius/octopushy:ansible-2.21.5-abc",
      host: "ops@10.15.60.10:22",
      toolVersion: "ansible-playbook [core 2.21.5]",
      destructive: true,
      tasks: ["play #1 (all): Configure", "  Install tools"],
      changedFiles: ["M modules/ansible/ai-server/site.yml"],
      vars: { hostname: "pve01-ai" },
      secrets: [{ name: "ansible.ssh-key", reference: "access/pve01-ai/ssh-key v2" }],
      prepared: { tools: ["t3 1.2.3"] },
      hostKeyFingerprints: ["ssh-ed25519 SHA256:abcdef"],
      previewSha256: sha,
    },
  };
  const { requests } = mockApi({
    "GET /api/runs/run-1": () => detail,
    "POST /api/runs/run-1/steps/step-approval/approve": () => undefined,
  });
  renderAt(<RunPage />, { path: "/runs/:id", route: "/runs/run-1" });
  expect(await screen.findByText("ops@10.15.60.10:22")).toBeTruthy();
  expect(screen.getByText("ssh-ed25519 SHA256:abcdef")).toBeTruthy();
  expect(screen.getByText("access/pve01-ai/ssh-key v2")).toBeTruthy();
  expect(screen.getByText("M modules/ansible/ai-server/site.yml")).toBeTruthy();
  expect(screen.getByText("Declared by the workflow")).toBeTruthy();
  expect(screen.getByText(/It shows what will run, not what will change/)).toBeTruthy();
  expect(screen.getByText(/Octopushy cannot show what it will change/)).toBeTruthy();
  expect(screen.getByText("Preview SHA-256")).toBeTruthy();
  await userEvent.click(screen.getByRole("button", { name: "Approve" }));
  await waitFor(() => expect(requests.some((r) => r.method === "POST")).toBe(true));
  expect(requests.find((r) => r.method === "POST")?.path).toBe("/api/runs/run-1/steps/step-approval/approve");
});

it("opens an approval with tiles of what changes, one detail at a time", async () => {
  const detail = runDetail({ operation: "provision" });
  detail.steps[0].plan!.changes[0] = {
    ...detail.steps[0].plan!.changes[0],
    actions: ["update"],
    attributes: [
      { name: "disk.scsi0.size", before: 32, after: 64, unknown: false, sensitive: false },
      { name: "memory.dedicated", before: 4096, after: 8192, unknown: false, sensitive: false },
    ],
  };
  mockApi({ "GET /api/runs/run-1": () => detail });
  renderAt(<RunPage />, { path: "/runs/:id", route: "/runs/run-1" });
  const tiles = within(await screen.findByRole("group", { name: "What changes" }));
  const disk = tiles.getByRole("button", { name: /Disk/ });
  const resources = tiles.getByRole("button", { name: /Resources/ });
  expect(tiles.getByRole("button", { name: /Memory/ })).toHaveAttribute("aria-expanded", "false");
  expect(screen.getByText("What this run will apply, not read from the guest.")).toBeInTheDocument();
  expect(screen.queryByRole("region", { name: "Disk" })).toBeNull();

  await userEvent.click(disk);
  expect(disk).toHaveAttribute("aria-expanded", "true");
  const panel = screen.getByRole("region", { name: "Disk" });
  expect(within(panel).getByText("scsi0 size")).toBeInTheDocument();
  expect(within(panel).getByText("32 GiB")).toBeInTheDocument();

  await userEvent.click(resources);
  expect(disk).toHaveAttribute("aria-expanded", "false");
  expect(screen.getByRole("region", { name: "Resources" })).toHaveTextContent("terraform_data.marker");

  await userEvent.keyboard("{Escape}");
  expect(resources).toHaveAttribute("aria-expanded", "false");
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
            { address: "proxmox_virtual_environment_vm.this", type: "proxmox_virtual_environment_vm", name: "this", actions: ["create", "delete"], attributes: [] },
            { address: "terraform_data.marker", type: "terraform_data", name: "marker", actions: ["create"], attributes: [] },
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
    canRunAgain: false,
    canForceStop: false,
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

  it("says which store value started a run", async () => {
    mockApi({
      "GET /api/runs/run-1": () =>
        runDetail({
          operation: "configure",
          reason: "secret",
          reasonDetail: "A store value this guest's setup uses changed: shared/git-ssh-key v2 to v3.",
        }),
    });

    renderAt(<RunPage />, { path: "/runs/:id", route: "/runs/run-1" });
    expect(await screen.findByText("Store value changed")).toBeInTheDocument();
    expect(screen.getByText(/shared\/git-ssh-key v2 to v3/)).toBeInTheDocument();
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

