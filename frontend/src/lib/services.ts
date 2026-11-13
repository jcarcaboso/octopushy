import type { GuestSummary, ServiceSummary, UptimeCheck } from "../api/types";
import type { NodeEntry } from "./inventory";

/** One link of a service, as the Services page shows it. */
export interface ServiceLink {
  key: string;
  url: string;
  /** The hostname with its port, lower case. */
  host: string;
  /** The host without the group's suffix. */
  label: string;
  /** The group's suffix, with the port when there is one; empty for an apex or a link without a domain. */
  tail: string;
  /** Pathname and search, empty when that is just "/". */
  path: string;
  service: ServiceSummary;
  guest: GuestSummary;
  check: UptimeCheck | null;
}

/** The links under one domain; `domain` is null for addresses and single-label hosts. */
export interface LinkGroup {
  domain: string | null;
  links: ServiceLink[];
}

const parent = (host: string) => host.slice(host.indexOf(".") + 1);

/** An address or a single label has no domain to group under. */
function hasDomain(hostname: string): boolean {
  return hostname.includes(".") && !hostname.startsWith("[") && !/^[\d.]+$/.test(hostname);
}

/** Every link of every service that is not retired, grouped by the domain its host is under. */
export function groupLinks(nodes: NodeEntry[]): LinkGroup[] {
  const parsed: { url: URL | null; link: string; service: ServiceSummary; guest: GuestSummary; check: UptimeCheck | null }[] = [];
  for (const machine of nodes.flatMap((n) => n.machines)) {
    for (const entry of machine.services) {
      if (entry.service.status === "retired") continue;
      for (const link of entry.service.links) {
        parsed.push({
          url: URL.canParse(link) ? new URL(link) : null,
          link,
          service: entry.service,
          guest: machine.guest,
          check: entry.checks.find((c) => c.url === link) ?? null,
        });
      }
    }
  }

  // The services with a link right under each domain.
  const direct = new Map<string, Set<string>>();
  for (const { url, service } of parsed) {
    if (!url || !hasDomain(url.hostname)) continue;
    const d = parent(url.hostname);
    direct.set(d, (direct.get(d) ?? new Set()).add(service.id));
  }
  const under = (domain: string) => direct.get(domain)?.size ?? 0;

  const groupOf = (hostname: string): string => {
    const p = parent(hostname);
    // A zone's own name opens with what is under it: when two services are, or when only a top-level name is above it.
    if (under(hostname) >= 2 || (under(hostname) === 1 && !p.includes("."))) return hostname;
    if (under(p) >= 2) return p;
    // A lone nested name, such as api.app.home.example.org, joins the nearest domain above it that has links.
    for (let a = parent(p); a.includes("."); a = parent(a)) {
      if (under(a) > 0) return a;
    }
    return p;
  };

  const groups = new Map<string | null, ServiceLink[]>();
  for (const { url, link, service, guest, check } of parsed) {
    const domain = url && hasDomain(url.hostname) ? groupOf(url.hostname) : null;
    const host = url?.host ?? link;
    const port = url?.port ? `:${url.port}` : "";
    const apex = domain === url?.hostname;
    const path = url && url.pathname + url.search !== "/" ? url.pathname + url.search : "";
    const entry: ServiceLink = {
      key: `${service.id}|${link}`,
      url: link,
      host,
      label: domain === null || apex ? host : url!.hostname.slice(0, -domain.length - 1),
      tail: domain === null || apex ? "" : `.${domain}${port}`,
      path,
      service,
      guest,
      check,
    };
    groups.set(domain, [...(groups.get(domain) ?? []), entry]);
  }

  // Services by name, then guest; a service's links keep their order, since the sort is stable.
  const order = (a: ServiceLink, b: ServiceLink) =>
    a.service.name.localeCompare(b.service.name) || a.guest.address.localeCompare(b.guest.address) || a.service.id.localeCompare(b.service.id);
  return [...groups]
    .map(([domain, links]) => ({ domain, links: links.sort(order) }))
    .sort((a, b) => (a.domain === null ? 1 : b.domain === null ? -1 : a.domain.localeCompare(b.domain)));
}

/** The groups with only the links whose service, guest or host contains the text; empty groups go. */
export function filterGroups(groups: LinkGroup[], text: string): LinkGroup[] {
  const q = text.trim().toLowerCase();
  if (!q) return groups;
  return groups
    .map((g) => ({ ...g, links: g.links.filter((l) => [l.service.name, l.guest.name, l.host].some((v) => v.toLowerCase().includes(q))) }))
    .filter((g) => g.links.length > 0);
}
