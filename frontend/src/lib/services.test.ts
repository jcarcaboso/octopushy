import { describe, expect, it } from "vitest";
import type { Uptime, UptimeCheck } from "../api/types";
import { check, guest, inventoryOf, service } from "../test/inventory";
import { buildTree } from "./inventory";
import { filterGroups, groupLinks } from "./services";

/** Each service on its own guest, named after it. */
function groupsOf(services: ReturnType<typeof service>[], checks: UptimeCheck[] = []) {
  const uptime: Uptime = { checking: true, intervalSeconds: 60, generatedAt: "2026-11-12T09:00:00Z", checks };
  return groupLinks(buildTree(inventoryOf(services.map((s) => guest(`g-${s.name}`, [s]))), uptime));
}

const summary = (groups: ReturnType<typeof groupLinks>) =>
  groups.map((g) => [g.domain, g.links.map((l) => `${l.service.name} ${l.label}|${l.tail}|${l.path}`)]);

describe("grouping links by domain", () => {
  it("puts a link under its host's parent", () => {
    expect(summary(groupsOf([service("vault", ["https://vault.home.example.org/"])]))).toEqual([
      ["home.example.org", ["vault vault|.home.example.org|"]],
    ]);
  });

  it("keeps a parent that two or more services are under", () => {
    const groups = groupsOf([
      service("a", ["https://a.dashboard.lab.example.org/"]),
      service("b", ["https://b.dashboard.lab.example.org/"]),
      service("c", ["https://c.lab.example.org/"]),
    ]);
    expect(groups.map((g) => g.domain)).toEqual(["dashboard.lab.example.org", "lab.example.org"]);
  });

  it("folds a lone nested name into the nearest domain with links", () => {
    const groups = groupsOf([
      service("fleet", ["https://fleet.hlab.example.org/", "https://fleet.dashboard.hlab.example.org/", "https://fleet.api.hlab.example.org/"]),
      service("arcane", ["https://arcane.hlab.example.org/"]),
    ]);
    expect(summary(groups)).toEqual([
      ["hlab.example.org", [
        "arcane arcane|.hlab.example.org|",
        "fleet fleet|.hlab.example.org|",
        "fleet fleet.dashboard|.hlab.example.org|",
        "fleet fleet.api|.hlab.example.org|",
      ]],
    ]);
  });

  it("keeps a lone link under its parent when no ancestor has links", () => {
    const groups = groupsOf([service("cliproxy", ["https://cliproxy.mlab.example.org/"]), service("vault", ["https://vault.hlab.example.org/"])]);
    expect(groups.map((g) => g.domain)).toEqual(["hlab.example.org", "mlab.example.org"]);
  });

  it("opens a zone's group with the zone's own name", () => {
    const zone = groupsOf([
      service("traefik", ["https://hlab.example.org/", "https://traefik.hlab.example.org/"]),
      service("vault", ["https://vault.hlab.example.org/"]),
    ]);
    expect(summary(zone)).toEqual([
      ["hlab.example.org", ["traefik hlab.example.org||", "traefik traefik|.hlab.example.org|", "vault vault|.hlab.example.org|"]],
    ]);
    const registered = groupsOf([service("site", ["https://example.org/"]), service("blog", ["https://blog.example.org/"])]);
    expect(summary(registered)).toEqual([["example.org", ["blog blog|.example.org|", "site example.org||"]]]);
  });

  it("keeps a service's own nested name with the service", () => {
    const groups = groupsOf([
      service("minio", ["https://minio.home.example.org/", "https://s3.minio.home.example.org/"]),
      service("app", ["https://app.home.example.org/"]),
    ]);
    expect(summary(groups)).toEqual([
      ["home.example.org", ["app app|.home.example.org|", "minio minio|.home.example.org|", "minio s3.minio|.home.example.org|"]],
    ]);
  });

  it("puts addresses and single labels under No domain, last", () => {
    const groups = groupsOf([
      service("dns", ["http://10.0.0.21:5380/"]),
      service("v6", ["http://[fd00::1]/"]),
      service("nas", ["http://nas/"]),
      service("app", ["https://app.home.example.org/"]),
    ]);
    expect(summary(groups)).toEqual([
      ["home.example.org", ["app app|.home.example.org|"]],
      [null, ["dns 10.0.0.21:5380||", "nas nas||", "v6 [fd00::1]||"]],
    ]);
  });

  it("groups a two-label host under its last label", () => {
    expect(groupsOf([service("nas", ["https://nas.lan/"])])[0].domain).toBe("lan");
  });

  it("sorts services by name then guest, keeps a service's links together, and leaves retired services out", () => {
    const one = guest("b-guest", [service("app", ["https://app2.home.example.org/"])]);
    const two = guest("a-guest", [
      service("app", ["https://app1.home.example.org/", "https://app0.home.example.org/"]),
      service("zed", ["https://zed.home.example.org/"]),
      service("old", ["https://old.home.example.org/"], { status: "retired" }),
    ]);
    const groups = groupLinks(buildTree(inventoryOf([one, two]), undefined));
    expect(groups[0].links.map((l) => `${l.guest.name} ${l.label}`)).toEqual(["a-guest app1", "a-guest app0", "b-guest app2", "a-guest zed"]);
  });

  it("splits a link into label, tail with the port, and path", () => {
    const [group] = groupsOf([service("pbs", ["https://pbs.home.example.org:8007/ui/?tab=1"]), service("app", ["https://app.home.example.org/"])]);
    const pbs = group.links.find((l) => l.service.name === "pbs")!;
    expect([pbs.host, pbs.label, pbs.tail, pbs.path]).toEqual(["pbs.home.example.org:8007", "pbs", ".home.example.org:8007", "/ui/?tab=1"]);
  });

  it("gives each link its own check", () => {
    const s = service("jellyfin", ["https://jellyfin.home.example.org/", "https://media.home.example.org/"]);
    const resource = "home/pve01/g-jellyfin/jellyfin";
    const [group] = groupsOf([s], [check(resource, "https://media.home.example.org/", "down"), check(resource, "https://jellyfin.home.example.org/")]);
    expect(group.links.map((l) => l.check?.status)).toEqual(["up", "down"]);
  });
});

it("filters by service, guest or host and drops empty groups", () => {
  const groups = groupsOf([service("vault", ["https://vault.home.example.org/"]), service("dns", ["http://10.0.0.21/"])]);
  expect(filterGroups(groups, "VAULT").map((g) => g.domain)).toEqual(["home.example.org"]);
  expect(filterGroups(groups, "g-dns").map((g) => g.domain)).toEqual([null]);
  expect(filterGroups(groups, "10.0.0")[0].links).toHaveLength(1);
  expect(filterGroups(groups, "nothing")).toEqual([]);
});
