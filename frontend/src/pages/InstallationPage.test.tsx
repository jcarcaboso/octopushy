import { screen, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, describe, expect, it, vi } from "vitest";
import type { InstallationDetail, InstallationSummary, InstallationTrust, Inventory } from "../api/types";
import { guest } from "../test/inventory";
import { runDetail } from "../test/runs";
import { mockApi, problem, renderAt } from "../test/utils";
import { InstallationPage } from "./InstallationPage";
import { InventoryPage } from "./InventoryPage";

const id = "installation-1";
const endpoint = "https://10.1.0.10:8006/";

function summary(trust: Partial<InstallationTrust> = {}): InstallationSummary {
  return {
    id,
    address: "home/korriban",
    name: "korriban",
    type: "standalone",
    endpoint,
    valid: true,
    managed: true,
    issueCount: 0,
    observedAt: null,
    observeError: null,
    observeErrorAt: null,
    observing: false,
    observeWaiting: null,
    proxmoxVersion: null,
    hosts: [],
    guests: [],
    unmanagedCount: 0,
    unmanagedGuests: [],
    trust: { endpoints: [endpoint], untrusted: [endpoint], trustedAt: null, trustedBy: null, managementChanged: false, ...trust },
  };
}

function detail(installation = summary()): InstallationDetail {
  return {
    installation,
    definitionFile: "labs/home/proxmox/korriban/installation.yaml",
    definitionText: null,
    issues: [],
    tokenKey: null,
    verifyTls: false,
    observedGuests: [],
    clusterName: null,
  };
}

function renderPage() {
  return renderAt(<InstallationPage />, { path: "/installations/:id", route: `/installations/${id}` });
}

describe("installation trust", () => {
  afterEach(() => vi.unstubAllGlobals());

  it("asks to trust a new installation with the endpoints shown, then shows who trusted it", async () => {
    const user = userEvent.setup();
    let current = summary();
    const { requests } = mockApi({
      [`GET /api/installations/${id}`]: () => detail(current),
      [`POST /api/installations/${id}/trust`]: () => {
        current = summary({ untrusted: [], trustedAt: "2026-10-28T10:00:00Z", trustedBy: "ops" });
        return current.trust;
      },
    });
    renderPage();

    expect(await screen.findByText("New Proxmox installation")).toBeInTheDocument();
    expect(screen.getByText("Not trusted")).toBeInTheDocument();
    await user.click(screen.getByRole("button", { name: "Trust installation" }));

    expect(requests.find((r) => r.method === "POST")?.body).toEqual({ endpoints: [endpoint] });
    expect(await screen.findByText("Trusted")).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Trust installation" })).not.toBeInTheDocument();
    expect(screen.getByText(/by ops/)).toBeInTheDocument();
  });

  it("asks again only for a new endpoint and shows a refused trust", async () => {
    const user = userEvent.setup();
    const moved = "https://10.1.0.99:8006/";
    mockApi({
      [`GET /api/installations/${id}`]: () =>
        detail(summary({ endpoints: [moved], untrusted: [moved], trustedAt: "2026-10-27T10:00:00Z", trustedBy: "ops" })),
      [`POST /api/installations/${id}/trust`]: () => problem(409, "The installation's endpoints changed since they were shown. Review them again."),
    });
    renderPage();

    const callout = (await screen.findByText("New Proxmox endpoint")).closest(".callout") as HTMLElement;
    expect(within(callout).getByText(moved)).toBeInTheDocument();
    expect(screen.getByText("New endpoint")).toBeInTheDocument();
    await user.click(within(callout).getByRole("button", { name: "Trust installation" }));
    expect(await within(callout).findByText(/endpoints changed since they were shown/)).toBeInTheDocument();
  });

  it("shows an installation Octopushy only observes", async () => {
    mockApi({ [`GET /api/installations/${id}`]: () => detail({ ...summary(), managed: false }) });
    renderPage();

    expect(await screen.findByText(/sends its Proxmox token only to observations/)).toBeInTheDocument();
    // In the header, the callout's title and the connection's facts.
    expect(screen.getAllByText("Observed only")).toHaveLength(3);
    expect(screen.getByText(/^Observations wait until you trust/)).toBeInTheDocument();
  });

  it("asks to trust again an installation that was trusted as observed only and now asks to be managed", async () => {
    mockApi({
      [`GET /api/installations/${id}`]: () =>
        detail(summary({ trustedAt: "2026-10-27T10:00:00Z", trustedBy: "ops", managementChanged: true })),
    });
    renderPage();

    expect(await screen.findByText("Now asks to be managed")).toBeInTheDocument();
    expect(screen.getByText("Trusted as observed only")).toBeInTheDocument();
    expect(screen.getByText(/It was trusted while it was observed only/)).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Trust installation" })).toBeInTheDocument();
  });

  it("marks observed-only installations in the inventory", async () => {
    const inventory: Inventory = {
      revision: null,
      labs: [{ id: "lab-1", address: "home", name: "Home", valid: true, issues: [], installations: [{ ...summary({ untrusted: [] }), managed: false }] }],
    };
    mockApi({ "GET /api/inventory": () => inventory });
    renderAt(<InventoryPage />, { path: "/inventory", route: "/inventory" });

    // On the node's row, and on its details.
    expect(await screen.findAllByText("Observed only")).toHaveLength(2);
  });

  it("marks installations that need trust in the inventory", async () => {
    const inventory: Inventory = {
      revision: null,
      labs: [{ id: "lab-1", address: "home", name: "Home", valid: true, issues: [], installations: [summary()] }],
    };
    mockApi({ "GET /api/inventory": () => inventory });
    renderAt(<InventoryPage />, { path: "/inventory", route: "/inventory" });

    expect(await screen.findByRole("link", { name: "Needs trust" })).toHaveAttribute("href", `/installations/${id}#trust`);
  });
});

describe("installation page", () => {
  const trusted = { untrusted: [], trustedAt: "2026-10-27T10:00:00Z", trustedBy: "ops" };

  it("lists its guests' runs that wait for approval and the guests that failed, each linked to its run", async () => {
    const waiting = { ...runDetail().run, id: "run-49", number: 49, detail: "Approval is manual." };
    const failedRun = { ...runDetail().run, id: "run-44", number: 44, status: "failed" };
    const guests = [
      { ...guest("bb", []), activeRun: waiting, status: "awaiting-approval" },
      { ...guest("media", []), lastRun: failedRun, status: "failed", statusDetail: "Run #44 failed." },
      guest("pbs", []),
    ];
    mockApi({ [`GET /api/installations/${id}`]: () => detail({ ...summary(trusted), guests }) });
    renderPage();

    const panel = await screen.findByRole("region", { name: "Runs of its guests" });
    expect(within(panel).getByRole("heading")).toHaveTextContent("1 guest waits for your approval, 1 failed");
    const rows = within(panel).getAllByRole("listitem");
    expect(rows).toHaveLength(2);
    expect(rows[0]).toHaveTextContent("Provision #49");
    expect(within(rows[0]).getByRole("link", { name: "Open run" })).toHaveAttribute("href", "/runs/run-49");
    expect(rows[1]).toHaveTextContent("Provision #44 failed");
    expect(within(rows[1]).getByRole("link", { name: "Open run" })).toHaveAttribute("href", "/runs/run-44");
  });

  it("shows no runs panel when nothing waits or failed, and its figures and one host in them", async () => {
    const host = { name: "pve01", declared: true, online: true, cpu: 0.25, maxCpu: 8, memoryBytes: 16 * 1024 ** 3, maxMemoryBytes: 64 * 1024 ** 3,
      uptimeSeconds: 86400 * 3, concurrentOperations: 2, endpoint: endpoint };
    mockApi({
      [`GET /api/installations/${id}`]: () =>
        detail({ ...summary(trusted), hosts: [host], guests: [guest("pbs", [])], unmanagedCount: 1, proxmoxVersion: "9.0.11", observedAt: "2026-11-13T08:00:00Z" }),
    });
    renderPage();

    const figure = async (label: string) => (await screen.findByText(label, { selector: ".glance__label" })).parentElement!;
    expect(await figure("Host")).toHaveTextContent("Onlineup 3 d");
    expect(within(await figure("CPU")).getByRole("meter", { name: "CPU in use" })).toHaveAttribute("aria-valuenow", "25");
    expect(await figure("Memory")).toHaveTextContent("64.0 GiB");
    expect(await figure("Guests")).toHaveTextContent("1managed · 1 unmanaged");
    expect(await figure("Proxmox VE")).toHaveTextContent("9.0.11");
    expect(screen.queryByRole("region", { name: "Runs of its guests" })).toBeNull();
    // One declared host is in the figures; its table is left out.
    expect(screen.queryByRole("heading", { name: "Hosts" })).toBeNull();
  });
});
