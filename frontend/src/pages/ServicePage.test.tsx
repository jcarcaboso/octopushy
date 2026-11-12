import { screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { describe, expect, it } from "vitest";
import type { ServiceDetail } from "../api/types";
import { mockApi, problem, renderAt } from "../test/utils";
import { ServicePage } from "./ServicePage";

function detail(overrides: Partial<ServiceDetail> = {}, status = "in-sync", statusDetail: string | null = null): ServiceDetail {
  return {
    service: { id: "svc-1", address: "home/korriban/krb-app-01/app", name: "app", status, statusDetail, valid: true, activeRun: null, lastRun: null,
      workflow: { reference: "web@1", resolved: "web@1.2" }, links: [] },
    guestId: "guest-1",
    guestAddress: "home/korriban/krb-app-01",
    definitionFile: "labs/home/proxmox/korriban/guests/krb-app-01/services/app/service.yaml",
    definitionText: "schemaVersion: 1\n",
    issues: [],
    policy: { approval: "automatic", approvalSetBy: "default", destructive: "automatic", destructiveSetBy: "default, following approval" },
    agentLabels: [],
    agentLabelsSetBy: null,
    stages: [
      { name: "expose", executor: "opentofu" },
      { name: "deploy", executor: "ansible" },
    ],
    appliedCommit: "e6b1282aa4d1c0ffee",
    appliedAt: "2026-11-02T08:00:00Z",
    missingSince: null,
    retiredAt: null,
    runs: [],
    ...overrides,
  };
}

const route = { path: "/services/:id", route: "/services/svc-1" };

describe("ServicePage", () => {
  it("shows the workflow and its stages and deploys on request", async () => {
    const { requests } = mockApi({
      "GET /api/services/svc-1": () => detail(),
      "POST /api/services/svc-1/deploy": () => ({ runId: "run-2" }),
      "GET /api/runs/run-2": () => new Response(null, { status: 404 }),
    });
    renderAt(<ServicePage />, route);
    expect(await screen.findByText("web@1.2")).toBeInTheDocument();
    expect(screen.getByText("from @1")).toBeInTheDocument();
    expect(screen.getByText("opentofu")).toBeInTheDocument();
    expect(screen.getByText("ansible")).toBeInTheDocument();
    await userEvent.click(screen.getByRole("button", { name: "Deploy" }));
    await waitFor(() => expect(requests.some((r) => r.method === "POST")).toBe(true));
  });

  it("says what a waiting service waits for", async () => {
    mockApi({ "GET /api/services/svc-1": () => detail({}, "waiting", "Waiting for the guest's setup to be applied.") });
    renderAt(<ServicePage />, route);
    expect(await screen.findByText("Waiting for the guest's setup to be applied.")).toBeInTheDocument();
  });

  it("shows the reason a deploy was refused", async () => {
    mockApi({
      "GET /api/services/svc-1": () => detail(),
      "POST /api/services/svc-1/deploy": () => problem(409, "Waiting for the guest's setup to be applied."),
    });
    renderAt(<ServicePage />, route);
    await userEvent.click(await screen.findByRole("button", { name: "Deploy" }));
    expect(await screen.findByText(/setup to be applied/)).toBeInTheDocument();
  });

  it("says a deleted folder removed nothing, and offers no deploy", async () => {
    mockApi({ "GET /api/services/svc-1": () => detail({ missingSince: "2026-11-02T08:00:00Z" }, "missing") });
    renderAt(<ServicePage />, route);
    expect(await screen.findByText(/Nothing was removed, and nothing will be/)).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Deploy" })).toBeNull();
  });
});
