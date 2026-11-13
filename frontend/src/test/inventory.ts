import type { GuestSummary, Inventory, ServiceSummary, UptimeCheck } from "../api/types";

// Small builders for an inventory with services and their checks, for the Services page and its grouping.

export const service = (name: string, links: string[], extra: Partial<ServiceSummary> = {}): ServiceSummary => ({
  id: `svc-${name}`, address: `home/pve01/web/${name}`, name, status: "in-sync", statusDetail: null, valid: true, activeRun: null, lastRun: null,
  workflow: null, links, names: [], icon: null, ...extra,
});

export const guest = (name: string, services: ServiceSummary[]): GuestSummary => ({
  id: `guest-${name}`, address: `home/pve01/${name}`, name, host: "pve01", vmid: 100, lifecycle: "active", status: "in-sync", statusDetail: null,
  valid: true, observed: null, activeRun: null, lastRun: null, workflow: null, type: "vm", accessAddress: null,
  // Services take the guest's address, as the server gives them.
  services: services.map((s) => ({ ...s, address: `home/pve01/${name}/${s.name}` })),
});

export const inventoryOf = (guests: GuestSummary[]): Inventory => ({
  revision: null,
  labs: [{
    id: "lab", address: "home", name: "Home", valid: true, issues: [],
    installations: [{
      id: "installation-pve01", address: "home/pve01", name: "pve01", type: "standalone", endpoint: "https://10.0.0.2:8006/", valid: true,
      managed: true, issueCount: 0, observedAt: null, observeError: null, observeErrorAt: null, observing: false, observeWaiting: null,
      proxmoxVersion: "8.4.1", hosts: [], guests, unmanagedCount: 0, unmanagedGuests: [],
      trust: { endpoints: [], untrusted: [], trustedAt: null, trustedBy: null, managementChanged: false },
    }],
  }],
});

/** A service-http check of one link; `resource` is the service's address. */
export const check = (resource: string, url: string, status = "up", since: string | null = null): UptimeCheck => ({
  key: `service-http|${resource}|${url}`, kind: "service-http", installation: "home/pve01", host: "pve01", resource, url, status,
  detail: null, since, checkedAt: null, elapsedMs: status === "up" ? 21 : null, agent: "bb", uptime: { day: 100, week: 100, month: 100 },
  beats: "u".repeat(96), latency: [], daily: new Array(30).fill(status === "down" ? 0 : 100),
});
