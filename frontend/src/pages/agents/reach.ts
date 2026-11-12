import type { ReachabilityEntry } from "../../api/types";

export type Mark = "yes" | "no" | "unknown";

export const kinds: Record<string, string> = {
  "proxmox-api": "Proxmox API",
  "snippets-ssh": "Snippet upload",
  "guest-ssh": "Guest SSH",
};

/** What an entry is about: its installation or guest, or the endpoint's host when the server did not name it. */
export function entryName(entry: ReachabilityEntry): string {
  if (entry.name) return entry.name;
  try {
    return new URL(entry.endpoint).host || entry.endpoint;
  } catch {
    return entry.endpoint;
  }
}

/** A check when the route is fresh, a dash while it is being found out or has gone stale, a cross when it failed. */
export function entryMark(entry: ReachabilityEntry): Mark {
  if (entry.usable) return "yes";
  if (entry.state === "pending" || entry.state === "reachable") return "unknown";
  return "no";
}

/** The state in words, as the controller sees it. */
export function entryState(entry: ReachabilityEntry): string {
  if (entry.usable) return entry.probing ? "Reachable · refreshing" : "Reachable";
  if (entry.state === "pending") return "Probing";
  if (entry.state === "reachable") return entry.probing ? "Expired · probing" : "Expired";
  if (entry.state === "unsupported") return "Unsupported";
  const label = entry.state.replace("-", " ");
  return `${label[0]!.toUpperCase()}${label.slice(1)}${entry.probing ? " · probing" : ""}`;
}

export interface Pill {
  key: string;
  label: string;
  mark: Mark;
}

/** One pill per Proxmox installation and one counting the guests reached, for the agent's row. */
export function reachPills(entries: ReachabilityEntry[]): Pill[] {
  const installations = new Map<string, Mark[]>();
  for (const entry of entries.filter((e) => e.kind !== "guest-ssh")) {
    const name = entryName(entry);
    installations.set(name, [...(installations.get(name) ?? []), entryMark(entry)]);
  }
  const pills: Pill[] = [...installations].map(([name, marks]) => ({
    key: `installation:${name}`,
    label: name,
    mark: marks.every((m) => m === "yes") ? "yes" : marks.includes("no") ? "no" : "unknown",
  }));
  const guests = entries.filter((e) => e.kind === "guest-ssh");
  if (guests.length > 0) {
    const reached = new Set(guests.filter((e) => e.usable).map(entryName)).size;
    pills.push({
      key: "guests",
      label: `${reached} ${reached === 1 ? "guest" : "guests"}`,
      mark: reached > 0 ? "yes" : guests.some((e) => entryMark(e) === "no") ? "no" : "unknown",
    });
  }
  return pills;
}
