import { screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { describe, expect, it } from "vitest";
import type { ServiceDetail } from "../api/types";
import { mockApi, renderAt } from "../test/utils";
import { ServicePage } from "./ServicePage";

const id = "svc-1";

function detail(overrides: Partial<ServiceDetail> = {}, status = "in-sync", statusDetail: string | null = null): ServiceDetail {
  return {
    service: {
      id,
      address: "home/ziost/ha-db/postgresql",
      name: "postgresql",
      recipe: "postgresql",
      status,
      statusDetail,
      valid: true,
      ports: [{ port: 5432, protocol: "tcp", sources: ["10.15.40.10"] }],
      activeRun: null,
      lastRun: null,
    },
    guestId: "guest-1",
    guestAddress: "home/ziost/ha-db",
    definitionFile: "labs/home/proxmox/ziost/guests/ha-db/services/postgresql/service.yaml",
    definitionText: "schemaVersion: 1\n",
    issues: [],
    policy: null,
    agentLabels: [],
    agentLabelsSetBy: null,
    actions: [{ id: "deploy", description: null, destructive: false, onChange: true }],
    hasRemoval: false,
    appliedAction: null,
    appliedCommit: null,
    appliedAt: null,
    missingSince: null,
    retiredAt: null,
    runs: [],
    trust: { keys: [], codeDigest: null, execution: null, trusted: false, cluster: null, refusal: null, trustedAt: null, trustedBy: null },
    ...overrides,
  };
}

function render() {
  return renderAt(<ServicePage />, { path: "/services/:id", route: `/services/${id}` });
}

describe("ServicePage", () => {
  it("shows the service's own status, ports and actions", async () => {
    mockApi({ [`GET /api/services/${id}`]: () => detail() });
    render();
    expect(await screen.findByText("home/ziost/ha-db/postgresql")).toBeInTheDocument();
    expect(screen.getByText("5432/tcp from 10.15.40.10")).toBeInTheDocument();
    expect(screen.getByRole("button", { name: /deploy/ })).toBeInTheDocument();
  });

  it("says what a waiting service waits for", async () => {
    mockApi({ [`GET /api/services/${id}`]: () => detail({}, "waiting", "Waiting for the guest's setup to be applied.") });
    render();
    expect(await screen.findByText("Waiting for the guest's setup to be applied.")).toBeInTheDocument();
    expect(screen.getByText("Waiting")).toBeInTheDocument();
  });

  it("marks an action that can lose data", async () => {
    mockApi({
      [`GET /api/services/${id}`]: () =>
        detail({ actions: [{ id: "shrink-retention", description: null, destructive: true, onChange: false }] }),
    });
    render();
    expect(await screen.findByText("Destructive")).toBeInTheDocument();
  });

  it("offers Forget for a missing service nothing will remove, and says the data stays", async () => {
    const requests: string[] = [];
    mockApi({
      [`GET /api/services/${id}`]: () => detail({ missingSince: "2026-11-02T08:00:00Z" }, "missing"),
      [`POST /api/services/${id}/forget`]: () => {
        requests.push("forget");
        return null;
      },
    });
    render();
    expect(await screen.findByText(/its data stays/)).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: /deploy/ })).not.toBeInTheDocument();
    await userEvent.click(screen.getByRole("button", { name: "Forget" }));
    await screen.findByText(/its data stays/);
    expect(requests).toEqual(["forget"]);
  });

  it("offers the removal again, and Forget, for a missing service whose recipe has one", async () => {
    mockApi({ [`GET /api/services/${id}`]: () => detail({ missingSince: "2026-11-02T08:00:00Z", hasRemoval: true }, "missing") });
    render();
    expect(await screen.findByText(/waits for a person/)).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Run removal" })).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Forget" })).toBeInTheDocument();
  });

  it("asks for trust while a protected secret waits", async () => {
    mockApi({
      [`GET /api/services/${id}`]: () =>
        detail({
          trust: { keys: ["ha-db/postgres-password"], codeDigest: "c".repeat(64), execution: { address: "10.15.30.40" }, trusted: false, cluster: null, refusal: null, trustedAt: null, trustedBy: null },
        }),
    });
    render();
    expect(await screen.findByText("Waiting for trust")).toBeInTheDocument();
    expect(screen.getByText("ha-db/postgres-password")).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Trust" })).toBeInTheDocument();
  });
});
