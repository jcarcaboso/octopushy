import { expect, it } from "vitest";
import type { UptimeCheck } from "../api/types";
import { formatPercent, livenessOf, percentOf, proxmoxGuestUrl } from "./inventory";

const check = (status: string, day: number | null = null): UptimeCheck => ({
  key: status, kind: "service-http", installation: "home/pve01", host: "pve01", resource: "home/pve01/web/app", url: "https://app.test/",
  status, detail: null, since: null, checkedAt: null, elapsedMs: null, agent: null, uptime: { day, week: null, month: null },
  beats: "-".repeat(96), latency: [], daily: [],
});

it("rolls checks up: all up, all down, some of each, waiting, or nothing to check", () => {
  expect(livenessOf([])).toBe("none");
  expect(livenessOf([check("pending")])).toBe("pending");
  expect(livenessOf([check("up"), check("pending")])).toBe("up");
  expect(livenessOf([check("down"), check("down")])).toBe("down");
  expect(livenessOf([check("down"), check("up")])).toBe("degraded");
});

it("reports the lowest percentage, and formats it", () => {
  expect(percentOf([check("up", 99.5), check("up", 100), check("pending")], "day")).toBe(99.5);
  expect(percentOf([check("pending")], "day")).toBeNull();
  expect(formatPercent(100)).toBe("100%");
  expect(formatPercent(99.987)).toBe("99.99%");
  expect(formatPercent(97.25)).toBe("97.3%");
  expect(formatPercent(0)).toBe("0.00%");
  expect(formatPercent(null)).toBe("—");
});

it("links a guest in Proxmox's interface", () => {
  expect(proxmoxGuestUrl("https://10.0.0.2:8006", "lxc", 120)).toBe("https://10.0.0.2:8006/#v1:0:=lxc%2F120:4:::::::");
});
