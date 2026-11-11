import { screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, describe, expect, it, vi } from "vitest";
import type { Change, RunDetail } from "../api/types";
import { isChangeFinished } from "../api/queries";
import { mockApi, renderAt } from "../test/utils";
import { ChangePage } from "./ChangePage";

const sha = "3f6c2a9b8e7d6c5b4a39281706f5e4d3c2b1a09f8e7d6c5b4a3928170615e4d3";
const at = "2026-11-04T08:00:00Z";
const commit = "9a8b7c6d5e4f3a2b1c0d9a8b7c6d5e4f3a2b1c0d";

function change(overrides: Partial<Change> = {}): Change {
  return {
    id: "change-1",
    kind: "edit",
    resourceKind: "guest",
    resourceId: "guest-1",
    address: "home/pve01/pve01-ai",
    author: "ops",
    message: "pve01-ai: memory 4096 → 8192",
    baseCommit: "1111111111111111111111111111111111111111",
    commit,
    commitUrl: `https://github.com/jcarcaboso/homelab-config/commit/${commit}`,
    revisionId: "rev-1",
    status: "waiting-for-approval",
    detail: "Run #7 waits for approval.",
    files: ["labs/home/proxmox/pve01/guests/pve01-ai/guest.yaml"],
    runs: [{ id: "run-7", number: 7, operation: "provision", resourceId: "guest-1", resourceKind: "guest", address: "home/pve01/pve01-ai",
      status: "awaiting-approval", detail: null, reason: "revision", commit, createdAt: at, startedAt: at, finishedAt: null, currentStep: "approval",
      waiting: null }],
    createdAt: at,
    syncedAt: at,
    updatedAt: at,
    supersededBy: null,
    supersededByUrl: null,
    dismissedBy: null,
    dismissedAt: null,
    final: false,
    notCommitted: false,
    cleanupPending: 0,
    ...overrides,
  };
}

function run(status: string): RunDetail {
  const summary = change().runs[0];
  return {
    run: { ...summary, status },
    steps: [
      { id: "step-plan", ordinal: 0, name: "plan", kind: "agent", status: "succeeded", detail: null, startedAt: at, finishedAt: at, outputs: null,
        plan: { hasChanges: true, planSha256: sha, toolVersion: "1.10.6", changes: [{ address: "terraform_data.guest", type: "terraform_data", name: "guest", actions: ["update"], attributes: [] }],
          counts: { create: 0, update: 1, delete: 0, replace: 0 } },
        approval: null, attempts: [] },
      { id: "step-approval", ordinal: 1, name: "approval", kind: "approval", status: status === "awaiting-approval" ? "awaiting-approval" : "succeeded",
        detail: "Approval is manual.", startedAt: at, finishedAt: null, outputs: null, plan: null,
        approval: status === "awaiting-approval"
          ? { required: true, decision: null, decidedBy: null, decidedAt: null, note: null, planSha256: sha }
          : { required: true, decision: "approved", decidedBy: "ops", decidedAt: at, note: null, planSha256: sha },
        attempts: [] },
    ],
    agentLabels: [],
    agentLabelsSetBy: "home/lab.yaml",
    policy: null,
    canCancel: true,
    canClose: false,
    canRunAgain: false,
    canForceStop: false,
  };
}

const route = { path: "/changes/:id", route: "/changes/change-1" };

describe("ChangePage", () => {
  afterEach(() => vi.unstubAllGlobals());

  it("shows the commit on GitHub, the revision, and the run waiting for an approval given here", async () => {
    const user = userEvent.setup();
    const { requests } = mockApi({
      "GET /api/changes/change-1": () => change(),
      "GET /api/runs/run-7": () => run("awaiting-approval"),
      "POST /api/runs/run-7/steps/step-approval/approve": () => undefined,
    });
    renderAt(<ChangePage />, route);

    expect(await screen.findByRole("heading", { name: "pve01-ai: memory 4096 → 8192" })).toBeInTheDocument();
    expect(screen.getAllByText("Waiting for approval").length).toBeGreaterThan(0);
    expect(screen.getByRole("link", { name: /on GitHub/ })).toHaveAttribute("href", `https://github.com/jcarcaboso/homelab-config/commit/${commit}`);
    expect(screen.getByText("Synced", { selector: "li *" }).closest("li")).toHaveClass("change-progress__step--done");
    expect(screen.getByText("Approval", { selector: "li *" }).closest("li")).toHaveAttribute("aria-current", "step");
    expect(screen.getByRole("link", { name: "Run #7" })).toHaveAttribute("href", "/runs/run-7");

    await user.click(await screen.findByRole("button", { name: "Approve" }));
    await waitFor(() => expect(requests.some((r) => r.method === "POST")).toBe(true));
    expect(requests.find((r) => r.method === "POST")!.body).toEqual({ planSha256: sha, note: null });
  });

  it("shows a change that was applied", async () => {
    mockApi({
      "GET /api/changes/change-1": () => change({ status: "applied", final: true, detail: "Run #7 succeeded.", runs: [{ ...change().runs[0], status: "succeeded" }] }),
      "GET /api/runs/run-7": () => run("succeeded"),
    });
    renderAt(<ChangePage />, route);
    expect(await screen.findByText("Run #7 succeeded.")).toBeInTheDocument();
    expect(screen.getByText("Applied", { selector: "li *" }).closest("li")).toHaveClass("change-progress__step--done");
    expect(screen.queryByText("Updating")).toBeNull();
    expect(await screen.findByText("Approved by ops")).toBeInTheDocument();
  });

  it("shows a change committed and not synced yet, without a GitHub link for another remote", async () => {
    mockApi({
      "GET /api/changes/change-1": () => change({ status: "committed", detail: "Waiting for the sync.", revisionId: null, syncedAt: null, runs: [], commitUrl: null }),
    });
    renderAt(<ChangePage />, route);
    expect(await screen.findByText("Waiting for the revision to be read.")).toBeInTheDocument();
    expect(screen.getByText("Not synced yet")).toBeInTheDocument();
    expect(screen.queryByRole("link", { name: /on GitHub/ })).toBeNull();
    expect(screen.getByText("Committed", { selector: "li *" }).closest("li")).toHaveAttribute("aria-current", "step");
  });

  it("says a refused change committed nothing", async () => {
    mockApi({
      "GET /api/changes/change-1": () =>
        change({ status: "refused", final: true, notCommitted: true, detail: "pve01-ai changed since the form was loaded; reload it.", commit: null, commitUrl: null, revisionId: null, runs: [] }),
    });
    renderAt(<ChangePage />, route);
    expect(await screen.findByText("Refused: nothing was committed")).toBeInTheDocument();
    expect(screen.getByText("Nothing was committed")).toBeInTheDocument();
    expect(screen.queryByRole("list", { name: "Progress" })).toBeNull();
  });

  it("says a later commit superseded the change, and names it", async () => {
    const later = "7777777777777777777777777777777777777777";
    mockApi({
      "GET /api/changes/change-1": () =>
        change({ status: "superseded", final: true, detail: "Superseded by 7777777777: it sets hardware otherwise.", runs: [], revisionId: null,
          supersededBy: later, supersededByUrl: `https://github.com/jcarcaboso/homelab-config/commit/${later}` }),
    });
    renderAt(<ChangePage />, route);
    expect(await screen.findByText("Superseded: a later commit set this otherwise before it was applied")).toBeInTheDocument();
    expect(screen.getAllByText("Superseded").length).toBeGreaterThan(0);
    expect(screen.getAllByRole("link", { name: /on GitHub/ }).map((a) => a.getAttribute("href"))).toContain(
      `https://github.com/jcarcaboso/homelab-config/commit/${later}`);
    expect(screen.getByText("Applied", { selector: "li *" }).closest("li")).toHaveClass("change-progress__step--todo");
    expect(screen.queryByText("Updating")).toBeNull();
  });

  it("lets a person dismiss a change whose commit is not on the branch", async () => {
    vi.stubGlobal("confirm", () => true);
    const unknown = change({ status: "unknown", detail: "Not on main (yet): none of the 1 commit(s) its push prepared is there.", commit: null,
      commitUrl: null, revisionId: null, syncedAt: null, runs: [] });
    let current = unknown;
    const { requests } = mockApi({
      "GET /api/changes/change-1": () => current,
      "POST /api/changes/change-1/dismiss": () =>
        (current = { ...unknown, status: "dismissed", dismissedBy: "ops", detail: "Octopushy stopped waiting for it.", final: false, notCommitted: false }),
    });
    renderAt(<ChangePage />, route);
    expect(await screen.findByText("Its commit is not on the branch (yet)")).toBeInTheDocument();
    await userEvent.click(screen.getByRole("button", { name: "Dismiss" }));
    expect(await screen.findByText("Dismissed by ops")).toBeInTheDocument();
    expect(requests.some((r) => r.method === "POST" && r.path === "/api/changes/change-1/dismiss")).toBe(true);
    expect(screen.queryByRole("button", { name: "Dismiss" })).toBeNull();
    // Dismissing proves nothing: the page says so, and keeps following the change for a late arrival.
    expect(screen.getByText(/not proof that nothing was committed/)).toBeInTheDocument();
    expect(screen.getByText("Updating")).toBeInTheDocument();
    expect(screen.queryByText(/settled as not committed/i)).toBeNull();
  });

  it("follows a change until the API says it is final, whatever its status", async () => {
    expect(isChangeFinished("dismissed")).toBe(false);
    mockApi({ "GET /api/changes/change-1": () => change({ status: "dismissed", detail: "Stopped waiting.", commit: null, runs: [], dismissedBy: "ops" }) });
    renderAt(<ChangePage />, route);
    expect(await screen.findByText("Updating")).toBeInTheDocument();
  });

  it("shows a change being pushed", async () => {
    mockApi({ "GET /api/changes/change-1": () => change({ status: "committing", detail: "Pushing its commit.", commit: null, commitUrl: null, revisionId: null, syncedAt: null, runs: [] }) });
    renderAt(<ChangePage />, route);
    expect(await screen.findByText("Being pushed")).toBeInTheDocument();
    expect(screen.getByText("Committed", { selector: "li *" }).closest("li")).toHaveAttribute("aria-current", "step");
  });

  it("says when the revision proposed no run", async () => {
    mockApi({ "GET /api/changes/change-1": () => change({ status: "no-change", final: true, detail: "abc proposes no run.", runs: [] }) });
    renderAt(<ChangePage />, route);
    expect(await screen.findByText("The revision proposes no run: nothing a run is built from changed.")).toBeInTheDocument();
  });

  it("says how many generated values of a deploy are still to remove, and why", async () => {
    mockApi({
      "GET /api/changes/change-1": () =>
        change({ kind: "create-guest", status: "dismissed", final: true, notCommitted: true, runs: [], resourceId: null, cleanupPending: 1,
          generated: [
            { secret: "worker_enrollment_token", key: "proxmox/pve01/w2/worker_enrollment_token", generator: "agent-enrollment-token",
              state: "created", cleanupPending: true, detail: null, cleanupError: "canceling statement due to lock timeout" },
            { secret: "admin_password", key: "proxmox/pve01/w2/admin_password", generator: "random-password", state: "removed",
              cleanupPending: false, detail: "proxmox/pve01/w2/admin_password version 1 deleted.", cleanupError: null },
          ] }),
    });
    renderAt(<ChangePage />, { path: "/changes/:id", route: "/changes/change-1" });
    expect(await screen.findByText("1 generated value still to remove")).toBeInTheDocument();
    expect(screen.getByText(/canceling statement due to lock timeout/)).toBeInTheDocument();
    expect(screen.getByText("proxmox/pve01/w2/admin_password version 1 deleted.")).toBeInTheDocument();
  });
});
