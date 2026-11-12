// What agents answered about each uptime check, made up deterministically from a few incidents, as GET /api/uptime returns
// it: pve01 and its machines are up with a short blip or two; korriban, its node and the nas machine have been down for
// two days; jellyfin's first link answers 502 since 40 minutes ago, so the service is degraded; dns is still being
// created, so nothing has answered for it yet.
import type { Inventory, Uptime, UptimeCheck } from "../src/api/types.ts";
import { ago, hex } from "./runs.ts";

const QUARTERS = 30 * 96;

interface Incident {
  /** Minutes before now it started. */
  start: number;
  /** How long it lasted, in minutes; null while it goes on. */
  minutes: number | null;
}

function seeded(seed: string) {
  let n = parseInt(hex(seed, 8), 16);
  return () => {
    n = (n * 1103515245 + 12345) % 2 ** 31;
    return n / 2 ** 31;
  };
}

function check(
  key: string,
  kind: string,
  installation: string,
  host: string | null,
  resource: string | null,
  url: string,
  { incidents = [], ms = 20, pending = false, detail }: { incidents?: Incident[]; ms?: number; pending?: boolean; detail?: string } = {},
): UptimeCheck {
  const random = seeded(key);
  // Quarter hours, oldest first: the share of answers that said up in each.
  const up = new Array<number>(QUARTERS).fill(1);
  for (const incident of incidents) {
    const end = incident.minutes === null ? 0 : Math.max(0, incident.start - incident.minutes);
    for (let q = 0; q < QUARTERS; q++) {
      const from = (QUARTERS - q) * 15;
      const to = from - 15;
      const overlap = Math.max(0, Math.min(from, incident.start) - Math.max(to, end));
      up[q] = Math.min(up[q], 1 - overlap / 15);
    }
  }
  const beats = pending
    ? "-".repeat(96)
    : up.slice(-96).map((u) => (u === 1 ? "u" : u === 0 ? "d" : "p")).join("");
  const latency = pending ? new Array(96).fill(null) : up.slice(-96).map((u) => (u === 0 ? null : Math.round(ms * (0.7 + random() * 0.6))));
  const percent = (quarters: number) => (pending ? null : Math.round((up.slice(-quarters).reduce((a, b) => a + b, 0) / quarters) * 100_000) / 1000);
  const daily = Array.from({ length: 30 }, (_, d) =>
    pending ? null : Math.round((up.slice(d * 96, (d + 1) * 96).reduce((a, b) => a + b, 0) / 96) * 1000) / 10,
  );
  const ongoing = incidents.find((i) => i.minutes === null);
  const last = incidents.filter((i) => i.minutes !== null).sort((a, b) => a.start - a.minutes! - (b.start - b.minutes!))[0];
  const down = Boolean(ongoing);
  return {
    key,
    kind,
    installation,
    host,
    resource,
    url,
    status: pending ? "pending" : down ? "down" : "up",
    detail: pending ? null : down ? (detail ?? "timeout: connecting timed out after 10s") : kind === "service-http" ? "HTTP 200" : kind === "guest-ssh" ? "SSH answered" : "TLS handshake completed",
    since: pending ? null : ago(ongoing ? ongoing.start : last ? last.start - last.minutes! : 43_000),
    checkedAt: pending ? null : ago(0.4),
    elapsedMs: pending || down ? null : latency[95],
    agent: pending ? null : "bb",
    uptime: { day: percent(96), week: percent(7 * 96), month: percent(QUARTERS) },
    beats,
    latency,
    daily,
  };
}

const hours = (h: number) => h * 60;

export function uptime(inventory: Inventory): Uptime {
  const checks: UptimeCheck[] = [];
  for (const installation of inventory.labs.flatMap((l) => l.installations)) {
    const offline = installation.address === "home/korriban";
    for (const host of installation.hosts) {
      const url = host.endpoint ?? installation.endpoint!;
      checks.push(check(`proxmox-api|${installation.address}/${host.name}|${url}`, "proxmox-api", installation.address, host.name, null, url, {
        ms: 8,
        incidents: offline ? [{ start: hours(49), minutes: null }] : [{ start: hours(80), minutes: 20 }],
        detail: "unreachable: 10.15.60.3:8006: No route to host",
      }));
    }
    for (const guest of installation.guests) {
      if (guest.accessAddress) {
        const url = `ssh://${guest.accessAddress}:22`;
        checks.push(check(`guest-ssh|${guest.address}|${url}`, "guest-ssh", installation.address, guest.host, guest.address, url, {
          ms: 3,
          pending: guest.name === "dns",
          incidents: offline ? [{ start: hours(49), minutes: null }] : guest.name === "pbs" ? [{ start: hours(3.2), minutes: 6 }] : [],
          detail: "unreachable: 10.15.60.30:22: No route to host",
        }));
      }
      for (const service of guest.services) {
        service.links.forEach((link, i) => {
          const broken = service.name === "jellyfin" && i === 0;
          checks.push(check(`service-http|${service.address}|${link}`, "service-http", installation.address, guest.host, service.address, link, {
            ms: 25 + i * 9,
            pending: guest.name === "dns",
            incidents: broken
              ? [{ start: 40, minutes: null }, { start: hours(26), minutes: 30 }]
              : service.name === "app"
                ? [{ start: hours(20), minutes: 35 }]
                : [],
            detail: "HTTP 502",
          }));
        });
      }
    }
  }
  return { checking: true, intervalSeconds: 60, generatedAt: ago(0), checks };
}
