import { screen, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { expect, it } from "vitest";
import type { GuestSummary, InstallationSummary, Inventory, ObservedGuest, ServiceSummary, Uptime, UptimeCheck } from "../api/types";
import { mockApi, renderAt } from "../test/utils";
import { InventoryPage } from "./InventoryPage";

const service = (guest: string, name: string, links: string[], names: ServiceSummary["names"] = []): ServiceSummary => ({
  id: `svc-${name}`, address: `${guest}/${name}`, name, status: "in-sync", statusDetail: null, valid: true, activeRun: null, lastRun: null,
  workflow: null, links, names, icon: null,
});

const guest = (installation: string, host: string, name: string, vmid: number, services: ServiceSummary[] = []): GuestSummary => ({
  id: `guest-${name}`, address: `${installation}/${name}`, name, host, vmid, lifecycle: "active", status: "in-sync", statusDetail: null, valid: true,
  observed: null, activeRun: null, lastRun: null, workflow: { reference: "debian-vm@1", resolved: "debian-vm@1.2" }, type: "vm",
  accessAddress: `10.0.0.${vmid % 250}`, services,
});

const haos: ObservedGuest = {
  vmid: 100, name: "haos", host: "pve01", status: "running", template: false, tags: [], maxCpu: 2, maxMemoryBytes: 4 * 1024 ** 3,
  uptimeSeconds: 3600, managedBy: null, managedById: null, kind: "qemu",
};

function installation(name: string, endpoint: string, online: boolean, guests: GuestSummary[], unmanaged: ObservedGuest[] = []): InstallationSummary {
  return {
    id: `installation-${name}`, address: `home/${name}`, name, type: "standalone", endpoint, valid: true, managed: true, issueCount: 0,
    observedAt: null, observeError: null, observeErrorAt: null, observing: false, observeWaiting: null, proxmoxVersion: "8.4.1",
    hosts: [{ name, declared: true, online, cpu: online ? 0.2 : null, maxCpu: 8, memoryBytes: null, maxMemoryBytes: null, uptimeSeconds: null,
      concurrentOperations: null, endpoint }],
    guests, unmanagedCount: unmanaged.length, unmanagedGuests: unmanaged,
    trust: { endpoints: [endpoint], untrusted: [], trustedAt: null, trustedBy: null, managementChanged: false },
  };
}

const media = guest("home/pve01", "pve01", "media", 115, [
  service("home/pve01/media", "jellyfin", ["https://jellyfin.lab.test/", "https://media.lab.test/"]),
  service("home/pve01/media", "arcane", ["https://arcane.lab.test/"], [{ name: "arcane.lab.test", stage: "dns", url: "https://arcane.lab.test/" }]),
]);
const dns = guest("home/pve01", "pve01", "dns", 121);
const data = guest("home/pve01", "pve01", "data", 111, [service("home/pve01/data", "postgres", ["tcp://10.0.0.111:5432"])]);
const nas = guest("home/korriban", "korriban", "nas", 200);

const inventory: Inventory = {
  revision: null,
  labs: [{
    id: "lab", address: "home", name: "Home", valid: true, issues: [],
    installations: [
      installation("pve01", "https://10.0.0.2:8006/", true, [media, dns, data], [haos]),
      installation("korriban", "https://10.0.0.3:8006/", false, [nas]),
    ],
  }],
};

function check(kind: string, installationAddress: string, host: string | null, resource: string | null, url: string, status: string, day: number): UptimeCheck {
  return {
    key: `${kind}|${resource ?? installationAddress}|${url}`, kind, installation: installationAddress, host, resource, url, status,
    detail: status === "down" ? "HTTP 502" : null, since: "2026-11-12T08:00:00Z", checkedAt: "2026-11-12T09:00:00Z", elapsedMs: 12, agent: "bb",
    uptime: { day, week: day, month: day }, beats: (status === "down" ? "d" : "u").repeat(96), latency: new Array(96).fill(12),
    daily: new Array(30).fill(day),
  };
}

const uptime = (checking = true): Uptime => ({
  checking,
  intervalSeconds: 60,
  generatedAt: "2026-11-12T09:00:00Z",
  checks: [
    check("proxmox-api", "home/pve01", "pve01", null, "https://10.0.0.2:8006/", "up", 100),
    check("proxmox-api", "home/korriban", "korriban", null, "https://10.0.0.3:8006/", "down", 0),
    check("guest-ssh", "home/pve01", "pve01", media.address, "ssh://10.0.0.115:22", "up", 100),
    check("guest-ssh", "home/korriban", "korriban", nas.address, "ssh://10.0.0.200:22", "down", 0),
    check("service-http", "home/pve01", "pve01", "home/pve01/media/jellyfin", "https://jellyfin.lab.test/", "down", 97.5),
    check("service-http", "home/pve01", "pve01", "home/pve01/media/jellyfin", "https://media.lab.test/", "up", 100),
    check("service-tcp", "home/pve01", "pve01", "home/pve01/data/postgres", "tcp://10.0.0.111:5432", "up", 100),
  ],
});

function show(route = "/inventory", checking = true) {
  mockApi({ "GET /api/inventory": () => inventory, "GET /api/uptime": () => uptime(checking) });
  renderAt(<InventoryPage />, { path: "/inventory", route });
}

const column = (name: string) => screen.getByRole("region", { name });

it("starts on the first node with a problem and opens Proxmox in that node's own tab", async () => {
  show();
  const nodes = await screen.findByRole("region", { name: "Nodes" });
  expect(within(nodes).getByRole("button", { name: /korriban/ })).toHaveAttribute("aria-current", "true");
  expect(within(nodes).getByRole("img", { name: "Down" })).toBeInTheDocument();
  const details = column("Details");
  expect(within(details).getByRole("heading", { name: "korriban" })).toBeInTheDocument();
  const proxmox = within(details).getByRole("link", { name: /Open Proxmox/ });
  expect(proxmox).toHaveAttribute("href", "https://10.0.0.3:8006/");
  expect(proxmox).toHaveAttribute("target", "proxmox-home-korriban-korriban");
  // noopener would make every click a new tab.
  expect(proxmox).not.toHaveAttribute("rel");
  expect(screen.getByText(/3 problems/)).toBeInTheDocument();
});

it("shows the services column only for a machine that has services", async () => {
  const user = userEvent.setup();
  show("/inventory?node=home/pve01/pve01");
  const machines = await screen.findByRole("region", { name: "Machines" });
  await user.click(within(machines).getByRole("button", { name: /dns/ }));
  expect(screen.queryByRole("region", { name: "Services" })).not.toBeInTheDocument();
  expect(within(column("Details")).getByRole("heading", { name: "dns" })).toBeInTheDocument();

  await user.click(within(machines).getByRole("button", { name: /media/ }));
  const services = column("Services");
  await user.click(within(services).getByRole("button", { name: /jellyfin/ }));
  const details = column("Details");
  expect(within(details).getByText("Degraded")).toBeInTheDocument();
  expect(within(details).getByRole("link", { name: "https://media.lab.test/" })).toHaveAttribute("target", "_blank");
  expect(within(details).getByText(/HTTP 502/)).toBeInTheDocument();
});

it("shows the names a service's stages set, opens the first and offers it as a link", async () => {
  show("/inventory?node=home/pve01/pve01&machine=home/pve01/media&service=home/pve01/media/arcane");
  const details = await screen.findByRole("region", { name: "Details" });
  expect(within(details).getByRole("heading", { name: "arcane" })).toBeInTheDocument();
  expect(within(details).getByRole("link", { name: /^Open/ })).toHaveAttribute("href", "https://arcane.lab.test/");
  expect(within(details).getByRole("link", { name: "arcane.lab.test" })).toHaveAttribute("href", "https://arcane.lab.test/");
  expect(within(details).getByText(/set by stage/)).toHaveTextContent("set by stage dns");
  expect(within(details).queryByText(/No links/)).not.toBeInTheDocument();
});

it("checks a tcp link without offering to open it", async () => {
  show("/inventory?node=home/pve01/pve01&machine=home/pve01/data&service=home/pve01/data/postgres");
  const details = await screen.findByRole("region", { name: "Details" });
  expect(within(details).getByRole("heading", { name: "postgres" })).toBeInTheDocument();
  expect(within(details).queryByRole("link", { name: /^Open/ })).not.toBeInTheDocument();
  expect(within(details).queryByRole("link", { name: /tcp:/ })).not.toBeInTheDocument();
  expect(within(details).getAllByText("tcp://10.0.0.111:5432").length).toBeGreaterThan(0);
  expect(within(details).getByText("Up")).toBeInTheDocument();
});

it("opens a machine in Proxmox in the same tab as its node", async () => {
  show("/inventory?node=home/pve01/pve01&machine=home/pve01/media");
  const details = await screen.findByRole("region", { name: "Details" });
  const proxmox = within(details).getByRole("link", { name: /Open in Proxmox/ });
  expect(proxmox).toHaveAttribute("href", "https://10.0.0.2:8006/#v1:0:=qemu%2F115:4:::::::");
  expect(proxmox).toHaveAttribute("target", "proxmox-home-pve01-pve01");
});

it("keeps unmanaged guests folded until asked", async () => {
  const user = userEvent.setup();
  show("/inventory?node=home/pve01/pve01");
  const machines = await screen.findByRole("region", { name: "Machines" });
  expect(within(machines).getByRole("button", { name: /haos/ })).not.toBeVisible();
  await user.click(within(machines).getByText("Unmanaged"));
  await user.click(within(machines).getByRole("button", { name: /haos/ }));
  expect(within(column("Details")).getByText(/never touches it/)).toBeInTheDocument();
});

it("filters by text across nodes, machines, services and links, and by problems", async () => {
  const user = userEvent.setup();
  show("/inventory");
  await user.type(await screen.findByRole("searchbox", { name: /Filter nodes/ }), "media.lab");
  const nodes = column("Nodes");
  expect(within(nodes).getAllByRole("button")).toHaveLength(1);
  expect(within(column("Machines")).getAllByRole("button", { name: /media|dns/ }).map((b) => b.textContent)).toEqual([expect.stringContaining("media")]);

  await user.clear(screen.getByRole("searchbox", { name: /Filter nodes/ }));
  await user.type(screen.getByRole("searchbox", { name: /Filter nodes/ }), "nothing like this");
  expect(await screen.findByText("Nothing matches these filters")).toBeInTheDocument();
});

it("keeps only the machines in the statuses the address names, and shows all again when asked", async () => {
  const user = userEvent.setup();
  const failing: Inventory = {
    ...inventory,
    labs: [{
      ...inventory.labs[0],
      installations: [
        installation("pve01", "https://10.0.0.2:8006/", true, [media, { ...dns, status: "failed" }], [haos]),
        installation("korriban", "https://10.0.0.3:8006/", false, [nas]),
      ],
    }],
  };
  mockApi({ "GET /api/inventory": () => failing, "GET /api/uptime": () => uptime() });
  renderAt(<InventoryPage />, { path: "/inventory", route: "/inventory?status=failed,uncertain" });
  const nodes = await screen.findByRole("region", { name: "Nodes" });
  expect(within(nodes).getAllByRole("button").map((b) => b.textContent)).toEqual([expect.stringContaining("pve01")]);
  expect(within(column("Machines")).getAllByRole("button", { name: /media|dns|haos/ }).map((b) => b.textContent)).toEqual([expect.stringContaining("dns")]);
  expect(screen.queryByText(/Unmanaged/)).not.toBeInTheDocument();

  await user.click(screen.getByRole("link", { name: "Show all" }));
  expect(within(column("Nodes")).getAllByRole("button")).toHaveLength(2);
});

it("names the list a narrow page shows, and its path leads back up", async () => {
  const user = userEvent.setup();
  show();
  const frame = () => column("Details").parentElement!;
  const path = () => screen.getByRole("navigation", { name: "Path" });
  await screen.findByRole("region", { name: "Nodes" });
  // Nothing chosen yet: the nodes, though the details already show the first one with a problem.
  expect(frame()).toHaveAttribute("data-level", "nodes");
  expect(path()).toHaveTextContent(/^Nodes$/);

  await user.click(within(column("Nodes")).getByRole("button", { name: /pve01/ }));
  expect(frame()).toHaveAttribute("data-level", "machines");
  // No machine is chosen, so the first is the one the Tab key reaches.
  expect(within(column("Machines")).getByRole("button", { name: /media/ })).toHaveAttribute("tabindex", "0");

  // A machine without services stays in the list of machines; one with services opens them.
  await user.click(within(column("Machines")).getByRole("button", { name: /dns/ }));
  expect(frame()).toHaveAttribute("data-level", "machines");
  await user.click(within(column("Machines")).getByRole("button", { name: /media/ }));
  expect(frame()).toHaveAttribute("data-level", "services");
  await user.click(within(column("Services")).getByRole("button", { name: /jellyfin/ }));
  expect(path()).toHaveTextContent("Nodespve01mediajellyfin");

  await user.click(within(path()).getByRole("button", { name: "pve01" }));
  expect(frame()).toHaveAttribute("data-level", "machines");
  expect(within(column("Details")).getByRole("heading", { name: "pve01" })).toBeInTheDocument();
  // Back at the nodes, the one it came from stays chosen.
  await user.click(within(path()).getByRole("button", { name: "Nodes" }));
  expect(frame()).toHaveAttribute("data-level", "nodes");
  expect(path()).toHaveTextContent(/^Nodes$/);
  expect(within(column("Details")).getByRole("heading", { name: "pve01" })).toBeInTheDocument();
  await user.click(within(column("Nodes")).getByRole("button", { name: /pve01/ }));
  expect(frame()).toHaveAttribute("data-level", "machines");
});

it("says when no agent checks uptime", async () => {
  show("/inventory", false);
  expect(await screen.findByText("Nothing checks uptime right now")).toBeInTheDocument();
});
