import { plural } from "./format";
import type { GuestSummary, InstallationSummary, Inventory, ObservedGuest, ObservedHost, ServiceSummary, Uptime, UptimeCheck } from "../api/types";

/** What the checks of something say together. `none`: nothing checks it; `pending`: no answer yet. */
export type Liveness = "up" | "degraded" | "down" | "pending" | "none";

/** The span an uptime percentage covers. */
export type UptimeWindow = "day" | "week" | "month";

export const windows: { value: UptimeWindow; label: string }[] = [
  { value: "day", label: "24h" },
  { value: "week", label: "7d" },
  { value: "month", label: "30d" },
];

export interface ServiceEntry {
  key: string;
  service: ServiceSummary;
  checks: UptimeCheck[];
  liveness: Liveness;
}

export interface MachineEntry {
  key: string;
  guest: GuestSummary;
  checks: UptimeCheck[];
  liveness: Liveness;
  services: ServiceEntry[];
}

export interface UnmanagedEntry {
  key: string;
  guest: ObservedGuest;
}

export interface NodeEntry {
  key: string;
  installation: InstallationSummary;
  host: ObservedHost;
  checks: UptimeCheck[];
  liveness: Liveness;
  machines: MachineEntry[];
  unmanaged: UnmanagedEntry[];
}

/** What several checks say together: all up, all down, or some of each. Answers outweigh checks still waiting for one. */
export function livenessOf(checks: UptimeCheck[]): Liveness {
  if (checks.length === 0) return "none";
  const answered = checks.filter((c) => c.status !== "pending");
  if (answered.length === 0) return "pending";
  const down = answered.filter((c) => c.status === "down").length;
  return down === 0 ? "up" : down === answered.length ? "down" : "degraded";
}

/** The lowest percentage of the checks over the window: the one a person would want to know about. Null without answers. */
export function percentOf(checks: UptimeCheck[], window: UptimeWindow): number | null {
  const values = checks.map((c) => c.uptime[window]).filter((v): v is number => v !== null && v !== undefined);
  return values.length === 0 ? null : Math.min(...values);
}

export function formatPercent(value: number | null): string {
  if (value === null) return "—";
  if (value === 100) return "100%";
  return `${value >= 99.95 || value === 0 ? value.toFixed(2) : value.toFixed(1)}%`;
}

/** Where Proxmox's web interface shows a guest, on the node's endpoint. */
export function proxmoxGuestUrl(endpoint: string, kind: string, vmid: number): string {
  return `${endpoint.replace(/\/?$/, "/")}#v1:0:=${kind}%2F${vmid}:4:::::::`;
}

/**
 * The tab Proxmox opens in for one node. Links name it, so opening Proxmox again goes to that tab, and browsers bring it
 * to the front, instead of opening another.
 */
export function proxmoxTab(node: NodeEntry): string {
  return `proxmox-${`${node.installation.address}-${node.host.name}`.replace(/[^A-Za-z0-9-]/g, "-")}`;
}

export function nodeEndpoint(node: NodeEntry): string | null {
  return node.host.endpoint ?? node.installation.endpoint;
}

export function guestKind(guest: GuestSummary): string {
  return guest.observed?.kind ?? (guest.type === "container" ? "lxc" : "qemu");
}

/** Nodes, their machines and services, with what the uptime checks say about each. */
export function buildTree(inventory: Inventory, uptime: Uptime | undefined): NodeEntry[] {
  const checks = uptime?.checks ?? [];
  const nodes: NodeEntry[] = [];
  for (const lab of inventory.labs) {
    for (const installation of lab.installations) {
      // An installation whose definition lists no host, and that nothing observed yet, still shows, under its own name.
      const hosts: ObservedHost[] =
        installation.hosts.length > 0
          ? installation.hosts
          : [{ name: installation.name, declared: false, online: null, cpu: null, maxCpu: null, memoryBytes: null, maxMemoryBytes: null,
              uptimeSeconds: null, concurrentOperations: null, endpoint: installation.endpoint }];
      for (const host of hosts) {
        const own = checks.filter(
          (c) => c.kind === "proxmox-api" && c.installation === installation.address && (c.host === host.name || c.host === null),
        );
        // A guest whose definition names no known host stays with the first one, so it is never lost.
        const machines = installation.guests
          .filter((g) => g.lifecycle !== "retired")
          .filter((g) => g.host === host.name || (host === hosts[0] && !hosts.some((h) => h.name === g.host)))
          .map((guest): MachineEntry => {
            const services = guest.services.map((service): ServiceEntry => {
              const serviceChecks = checks.filter((c) => c.kind === "service-http" && c.resource === service.address);
              return { key: service.address, service, checks: serviceChecks, liveness: livenessOf(serviceChecks) };
            });
            const guestChecks = checks.filter((c) => c.kind === "guest-ssh" && c.resource === guest.address);
            return { key: guest.address, guest, checks: guestChecks, liveness: livenessOf(guestChecks), services };
          });
        const unmanaged = installation.unmanagedGuests
          .filter((g) => g.host === host.name || (host === hosts[0] && !hosts.some((h) => h.name === g.host)))
          .map((guest) => ({ key: `unmanaged:${installation.address}:${guest.vmid}`, guest }));
        nodes.push({
          key: `${installation.address}/${host.name}`,
          installation,
          host,
          checks: own,
          liveness: livenessOf(own),
          machines,
          unmanaged,
        });
      }
    }
  }
  return nodes;
}

/** What the installation is waiting for, in a word or two, for a node's row; null when nothing. */
export function installationNote(installation: InstallationSummary): string | null {
  if (!installation.valid) return "Invalid";
  if (installation.valid && installation.trust.untrusted.length > 0) return "Needs trust";
  if (installation.issueCount > 0) return plural(installation.issueCount, "error");
  if (!installation.managed) return "Observed only";
  return null;
}

/** Whether something needs a look: a check is down, or some of its checks are. */
export const isProblem = (liveness: Liveness) => liveness === "down" || liveness === "degraded";

export interface Filters {
  /** Lower-case words that must all appear. */
  text: string;
  problems: boolean;
}

const words = (text: string) => text.toLowerCase().split(/\s+/).filter(Boolean);

function matches(haystack: (string | number | null | undefined)[], text: string): boolean {
  const all = haystack.filter((h) => h !== null && h !== undefined).join(" ").toLowerCase();
  return words(text).every((w) => all.includes(w));
}

export const nodeText = (n: NodeEntry) => [n.host.name, n.installation.name, n.installation.address, nodeEndpoint(n)];
export const machineText = (m: MachineEntry) => [m.guest.name, m.guest.address, m.guest.accessAddress, m.guest.vmid, m.guest.workflow?.reference];
export const serviceText = (s: ServiceEntry) => [s.service.name, s.service.address, ...s.service.links];
export const unmanagedText = (u: UnmanagedEntry) => [u.guest.name, u.guest.vmid, u.guest.status, ...u.guest.tags];

/**
 * The tree as the filters leave it. Something stays when it matches or something below it does; what matches keeps
 * everything below it, so a node found by name still shows its machines. Problems keep only what is down or degraded,
 * and the nodes and machines above it.
 */
export function filterTree(nodes: NodeEntry[], filters: Filters): NodeEntry[] {
  const text = filters.text.trim();
  if (!text && !filters.problems) return nodes;
  const result: NodeEntry[] = [];
  for (const node of nodes) {
    const nodeHit = !text || matches(nodeText(node), text);
    const machines: MachineEntry[] = [];
    for (const machine of node.machines) {
      const machineHit = nodeHit || matches(machineText(machine), text);
      const found = machine.services.filter((s) => machineHit || matches(serviceText(s), text));
      if (!machineHit && found.length === 0) continue;
      const services = filters.problems ? found.filter((s) => isProblem(s.liveness)) : found;
      if (filters.problems && !isProblem(machine.liveness) && services.length === 0) continue;
      machines.push({ ...machine, services });
    }
    const unmanaged = filters.problems ? [] : node.unmanaged.filter((u) => nodeHit || matches(unmanagedText(u), text));
    const keepNode = filters.problems ? isProblem(node.liveness) || machines.length > 0 : nodeHit || machines.length > 0 || unmanaged.length > 0;
    if (keepNode) result.push({ ...node, machines, unmanaged });
  }
  return result;
}

/** How many of each liveness, for the summary. */
export function counts(nodes: NodeEntry[]) {
  const machines = nodes.flatMap((n) => n.machines);
  const services = machines.flatMap((m) => m.services);
  const problems = [...nodes, ...machines, ...services].filter((x) => isProblem(x.liveness)).length;
  return { nodes: nodes.length, machines: machines.length, services: services.length, problems };
}

/** The mean of the checks' daily percentages, oldest day first; null for a day nothing answered. */
export function dailyMean(checks: UptimeCheck[], days = 30): (number | null)[] {
  return Array.from({ length: days }, (_, i) => {
    const values = checks.map((c) => c.daily[30 - days + i]).filter((v): v is number => v !== null && v !== undefined);
    return values.length === 0 ? null : values.reduce((a, b) => a + b, 0) / values.length;
  });
}

/** A strip bar's state for a percentage: u up, d down, p partly, - no answers. */
export function barState(value: number | null): "u" | "d" | "p" | "-" {
  return value === null ? "-" : value === 100 ? "u" : value === 0 ? "d" : "p";
}
