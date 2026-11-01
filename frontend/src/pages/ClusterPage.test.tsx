import { screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, describe, expect, it, vi } from "vitest";
import type { ClusterDetail, ClusterSummary, Inventory } from "../api/types";
import { mockApi, problem, renderAt } from "../test/utils";
import { ClusterPage } from "./ClusterPage";
import { InventoryPage } from "./InventoryPage";

const id = "cluster-1";
const endpoints = ["https://10.1.0.32:53443", "https://10.1.0.42:53443"];

function summary(overrides: Partial<ClusterSummary> = {}): ClusterSummary {
  return {
    id,
    address: "home/clusters/technitium",
    name: "technitium",
    type: "technitium",
    clusterDomain: "cluster.home.arpa",
    status: "in-sync",
    statusDetail: null,
    valid: true,
    issueCount: 0,
    members: [
      { guest: "home/korriban/krb-dns-01", role: "primary", address: "10.1.0.32", guestId: "g1", guestStatus: "in-sync" },
      { guest: "home/moraband/mrb-dns-01", role: "secondary", address: "10.1.0.42", guestId: "g2", guestStatus: "in-sync" },
    ],
    activeRun: null,
    lastRun: null,
    trust: { endpoints, untrusted: [], codeDigest: "digest", codeChanged: false, trustedAt: "2026-11-01T08:00:00Z", trustedBy: "ops" },
    verifiedAt: null,
    observedAt: null,
    observeError: null,
    observeErrorAt: null,
    ...overrides,
  };
}

function detail(cluster: ClusterSummary, verification: unknown = null): ClusterDetail {
  return {
    cluster,
    definitionFile: "labs/home/clusters/technitium/cluster.yaml",
    definitionText: "schemaVersion: 1\n",
    issues: [],
    policy: null,
    agentLabels: [],
    agentLabelsSetBy: null,
    desiredCommit: "abc",
    checkedCommit: null,
    checkedAt: null,
    appliedCommit: null,
    appliedAt: null,
    verification: verification as ClusterDetail["verification"],
    missingSince: null,
    runs: [],
  };
}

function render() {
  return renderAt(<ClusterPage />, { path: "/clusters/:id", route: `/clusters/${id}` });
}

describe("ClusterPage", () => {
  afterEach(() => vi.restoreAllMocks());

  it("asks for trust of a new member endpoint and sends exactly what it showed", async () => {
    let current = summary({ trust: { ...summary().trust, untrusted: [endpoints[1]!] } });
    const { requests } = mockApi({
      [`GET /api/clusters/${id}`]: () => detail(current),
      [`POST /api/clusters/${id}/trust`]: () => {
        current = summary();
        return current.trust;
      },
    });
    render();
    expect(await screen.findByText("New member endpoint")).toBeInTheDocument();
    expect(screen.getByText(endpoints[1]!)).toBeInTheDocument();
    await userEvent.click(screen.getByRole("button", { name: "Trust cluster" }));
    const request = requests.find((r) => r.path.endsWith("/trust"));
    expect(request?.body).toEqual({ endpoints, codeDigest: "digest" });
    expect(await screen.findByText("Trusted")).toBeInTheDocument();
  });

  it("explains why changed recipe code needs trust", async () => {
    mockApi({ [`GET /api/clusters/${id}`]: () => detail(summary({ trust: { ...summary().trust, codeChanged: true } })) });
    render();
    expect(await screen.findByText("Recipe code changed")).toBeInTheDocument();
    expect(screen.getByText(/before anyone approves a plan/)).toBeInTheDocument();
  });

  it("shows a refused trust", async () => {
    mockApi({
      [`GET /api/clusters/${id}`]: () => detail(summary({ trust: { ...summary().trust, trustedAt: null, trustedBy: null, untrusted: endpoints } })),
      [`POST /api/clusters/${id}/trust`]: () => problem(409, "The cluster's members or recipe code changed since they were shown. Review them again."),
    });
    render();
    await userEvent.click(await screen.findByRole("button", { name: "Trust cluster" }));
    expect(await screen.findByText(/changed since they were shown/)).toBeInTheDocument();
  });

  it("names the member that failed verification and its recovery, and passes the other", async () => {
    mockApi({
      [`GET /api/clusters/${id}`]: () =>
        detail(summary({ status: "verification-failed" }), {
          ok: false,
          members: [
            { guest: "home/korriban/krb-dns-01", ok: true, checks: [] },
            {
              guest: "home/moraband/mrb-dns-01",
              ok: false,
              checks: [{ name: "cluster", ok: false, detail: "it reports clusterInitialized=false" }],
              recovery: "call deleteSecondary, then Plan now",
            },
          ],
        }),
    });
    render();
    expect((await screen.findAllByText("Verification failed")).length).toBeGreaterThan(0);
    expect(screen.getByText("it reports clusterInitialized=false")).toBeInTheDocument();
    expect(screen.getByText("call deleteSecondary, then Plan now")).toBeInTheDocument();
    expect(screen.getByText("Passed")).toBeInTheDocument();
  });

  it("cannot plan a cluster whose definition was deleted", async () => {
    mockApi({ [`GET /api/clusters/${id}`]: () => ({ ...detail(summary({ status: "missing", statusDetail: "cluster.yaml was deleted." })), missingSince: "2026-11-01T08:00:00Z" }) });
    render();
    expect(await screen.findByText("cluster.yaml was deleted.")).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Plan now" })).toBeDisabled();
  });

  it("appears in the inventory and links its trust", async () => {
    const inventory: Inventory = {
      revision: null,
      labs: [{ id: "lab", address: "home", name: "Home", valid: true, issues: [], installations: [], clusters: [summary({ trust: { ...summary().trust, untrusted: endpoints } })] }],
    };
    mockApi({ "GET /api/inventory": () => inventory });
    renderAt(<InventoryPage />, { path: "/inventory", route: "/inventory" });
    expect((await screen.findByRole("link", { name: "technitium" })).getAttribute("href")).toBe(`/clusters/${id}`);
    expect(screen.getByText("Needs trust").closest("a")).toHaveAttribute("href", `/clusters/${id}#trust`);
  });
});
