import { screen, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, describe, expect, it, vi } from "vitest";
import type { InstallationDetail, InstallationSummary, InstallationTrust, Inventory } from "../api/types";
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
    trust: { endpoints: [endpoint], untrusted: [endpoint], trustedAt: null, trustedBy: null, ...trust },
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

  it("marks installations that need trust in the inventory", async () => {
    const inventory: Inventory = {
      revision: null,
      labs: [{ id: "lab-1", address: "home", name: "Home", valid: true, issues: [], installations: [summary()] }],
    };
    mockApi({ "GET /api/inventory": () => inventory });
    renderAt(<InventoryPage />, { path: "/inventory", route: "/inventory" });

    const badge = await screen.findByText("Needs trust");
    expect(badge.closest("a")).toHaveAttribute("href", `/installations/${id}#trust`);
  });
});
