import { screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, describe, expect, it, vi } from "vitest";
import type { ClusterDetail, ClusterSummary, Inventory } from "../api/types";
import { mockApi, problem, renderAt } from "../test/utils";
import { ClusterPage } from "./ClusterPage";
import { InventoryPage } from "./InventoryPage";

const id = "cluster-1";
const endpoints = ["https://10.1.0.32:53443", "https://10.1.0.42:53443"];

function summary(overrides: Partial<ClusterSummary> = {}): ClusterSummary {
  return {
    id,
    address: "home/clusters/technitium",
    name: "technitium",
    type: "technitium",
    clusterDomain: "cluster.home.arpa",
    status: "in-sync",
    statusDetail: null,
    valid: true,
    issueCount: 0,
    members: [
      { guest: "home/korriban/krb-dns-01", role: "primary", address: "10.1.0.32", guestId: "g1", guestStatus: "in-sync" },
      { guest: "home/moraband/mrb-dns-01", role: "secondary", address: "10.1.0.42", guestId: "g2", guestStatus: "in-sync" },
    ],
    activeRun: null,
    lastRun: null,
    trust: {
      endpoints,
      untrusted: [],
      codeDigest: "digest",
      codeChanged: false,
      trustedAt: "2026-11-01T08:00:00Z",
      trustedBy: "ops",
      certificates: endpoints.map((endpoint) => ({ endpoint, sha256: `sha-${endpoint}`, trustedSha256: `sha-${endpoint}`, trusted: true })),
      passwordProtected: true,
    },
    verifiedAt: null,
    observedAt: null,
    observeError: null,
    observeErrorAt: null,
    ...overrides,
  };
}

function detail(cluster: ClusterSummary, verification: unknown = null): ClusterDetail {
  return {
    cluster,
    definitionFile: "labs/home/clusters/technitium/cluster.yaml",
    definitionText: "schemaVersion: 1\n",
    issues: [],
    policy: null,
    agentLabels: [],
    agentLabelsSetBy: null,
    desiredCommit: "abc",
    checkedCommit: null,
    checkedAt: null,
    appliedCommit: null,
    appliedAt: null,
    verification: verification as ClusterDetail["verification"],
    missingSince: null,
    runs: [],
    leaving: [],
  };
}

function render() {
  return renderAt(<ClusterPage />, { path: "/clusters/:id", route: `/clusters/${id}` });
}

describe("ClusterPage", () => {
  afterEach(() => vi.restoreAllMocks());

  it("asks for trust of a new member endpoint and sends exactly what it showed", async () => {
    let current = summary({ trust: { ...summary().trust, untrusted: [endpoints[1]!] } });
    const { requests } = mockApi({
      [`GET /api/clusters/${id}`]: () => detail(current),
      [`POST /api/clusters/${id}/trust`]: () => {
        current = summary();
        return current.trust;
      },
    });
    render();
    expect(await screen.findByText("New member endpoint")).toBeInTheDocument();
    expect(screen.getAllByText(endpoints[1]!).length).toBeGreaterThan(0);
    await userEvent.click(screen.getByRole("button", { name: "Trust cluster" }));
    const request = requests.find((r) => r.path.endsWith("/trust"));
    expect(request?.body).toEqual({
      endpoints,
      codeDigest: "digest",
      certificates: Object.fromEntries(endpoints.map((e) => [e, `sha-${e}`])),
    });
    await waitFor(() => expect(screen.queryByRole("button", { name: "Trust cluster" })).not.toBeInTheDocument());
  });

  it("explains why changed recipe code needs trust", async () => {
    mockApi({ [`GET /api/clusters/${id}`]: () => detail(summary({ trust: { ...summary().trust, codeChanged: true } })) });
    render();
    expect(await screen.findByText("Members or code changed")).toBeInTheDocument();
    expect(screen.getByText(/before any person approves it/)).toBeInTheDocument();
  });

  it("shows a refused trust", async () => {
    mockApi({
      [`GET /api/clusters/${id}`]: () => detail(summary({ trust: { ...summary().trust, trustedAt: null, trustedBy: null, untrusted: endpoints } })),
      [`POST /api/clusters/${id}/trust`]: () => problem(409, "The cluster's members or recipe code changed since they were shown. Review them again."),
    });
    render();
    await userEvent.click(await screen.findByRole("button", { name: "Trust cluster" }));
    expect(await screen.findByText(/changed since they were shown/)).toBeInTheDocument();
  });

  it("names the member that failed verification and its recovery, and passes the other", async () => {
    mockApi({
      [`GET /api/clusters/${id}`]: () =>
        detail(summary({ status: "verification-failed" }), {
          ok: false,
          members: [
            { guest: "home/korriban/krb-dns-01", ok: true, checks: [] },
            {
              guest: "home/moraband/mrb-dns-01",
              ok: false,
              checks: [{ name: "cluster", ok: false, detail: "it reports clusterInitialized=false" }],
              recovery: "call deleteSecondary, then Plan now",
            },
          ],
        }),
    });
    render();
    expect((await screen.findAllByText("Verification failed")).length).toBeGreaterThan(0);
    expect(screen.getByText("it reports clusterInitialized=false")).toBeInTheDocument();
    expect(screen.getByText("call deleteSecondary, then Plan now")).toBeInTheDocument();
    expect(screen.getByText("Passed")).toBeInTheDocument();
  });

  it("shows how a member would run before it is trusted", async () => {
    const members = summary().members.map((member) => ({
      ...member,
      execution: {
        backend: { type: "http", config: { address: "https://state.example.invalid/" } },
        secrets: { admin_password: "dns/admin" },
        agentLabels: ["home"],
        proxmox: { endpoint: "https://10.1.0.10:8006/", verifyTls: false },
        template: { source: { url: "https://images.example.invalid/one.tar.zst" } },
      },
    }));
    const changed = summary({ members, trust: { ...summary().trust, codeChanged: true, codeDigest: "digest2" } });
    mockApi({ [`GET /api/clusters/${id}`]: () => detail(changed) });
    render();
    expect((await screen.findAllByText(/state\.example\.invalid/)).length).toBe(2);
    expect(screen.getAllByText("Login keys: none")).toHaveLength(2);
    expect(screen.getAllByText("admin_password = dns/admin")).toHaveLength(2);
    expect(screen.getAllByText(/its certificate not verified/)).toHaveLength(2);
    expect(screen.getAllByText(/images\.example\.invalid/)).toHaveLength(2);
  });

  it("shows who could log in to a member, and where it is, before the password goes there", async () => {
    const members = summary().members.map((member) => ({
      ...member,
      execution: {
        proxmox: { endpoint: "https://10.1.0.10:8006/", readOnlyToken: { store: "proxmox/ro" }, privilegedToken: { store: "proxmox/priv" } },
        guest: {
          vmid: 320,
          host: "korriban",
          access: { sshAuthorizedKeys: ["ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIAttackerKeyAttackerKey attacker@evil"] },
          hardware: { mountPoints: [{ path: "/etc/dns", volume: "local-lvm:vm-9320-disk-0" }] },
        },
        host: { datastores: { images: "local" }, bridges: ["vmbr0"] },
      },
    }));
    const changed = summary({ members, trust: { ...summary().trust, codeChanged: true, codeDigest: "digest2" } });
    mockApi({ [`GET /api/clusters/${id}`]: () => detail(changed) });
    render();
    expect((await screen.findAllByText("Can log in as root:")).length).toBe(2);
    expect(screen.getAllByText(/ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAA… attacker@evil/)).toHaveLength(2);
    expect(screen.getAllByText(/credentials read-only proxmox\/ro, privileged proxmox\/priv/)).toHaveLength(2);
    expect(screen.getAllByText(/volumes \/etc\/dns = local-lvm:vm-9320-disk-0/)).toHaveLength(2);
    expect(screen.getAllByText(/bridges vmbr0/)).toHaveLength(2);
  });

  it("says a member list changed when only the members did", async () => {
    mockApi({ [`GET /api/clusters/${id}`]: () => detail(summary({ trust: { ...summary().trust, codeChanged: true, codeDigest: "digest2" } })) });
    render();
    expect(await screen.findByText("Members or code changed")).toBeInTheDocument();
    expect(screen.queryByText("Recipe code changed")).not.toBeInTheDocument();
  });

  it("shows no member as passed when the latest check failed without saying why", async () => {
    mockApi({
      [`GET /api/clusters/${id}`]: () =>
        detail(summary({ status: "verification-failed", observeError: "the agent lost its route" }), { ok: false, error: "the agent lost its route", members: [] }),
    });
    render();
    expect((await screen.findAllByText("Unknown: the last check failed")).length).toBe(2);
    expect(screen.queryByText("Passed")).not.toBeInTheDocument();
    expect(screen.getAllByText("Verification failed").length).toBeGreaterThan(0);
  });

  it("warns plainly when a member presents another certificate", async () => {
    const certificates = summary().trust.certificates.map((c, i) => (i === 1 ? { ...c, sha256: "sha-new", trusted: false } : c));
    mockApi({ [`GET /api/clusters/${id}`]: () => detail(summary({ trust: { ...summary().trust, certificates } })) });
    render();
    expect(await screen.findByText("A member presents another certificate")).toBeInTheDocument();
    expect(screen.getByText("sha-new")).toBeInTheDocument();
    expect(screen.getByText(/something else is answering at its address/)).toBeInTheDocument();
  });

  it("says when the password is not protected", async () => {
    mockApi({ [`GET /api/clusters/${id}`]: () => detail(summary({ trust: { ...summary().trust, codeChanged: true, passwordProtected: false } })) });
    render();
    expect(await screen.findByText(/Mark it protected under Variables/)).toBeInTheDocument();
  });

  it("says when nothing saw a guest leave", async () => {
    mockApi({
      [`GET /api/clusters/${id}`]: () => ({
        ...detail(summary()),
        leaving: [{ guestId: "g2", guest: "home/moraband/mrb-dns-01", address: "10.1.0.42", peerName: null, unconfirmed: true }],
      }),
    });
    render();
    expect(await screen.findByText(/nothing saw it leave/)).toBeInTheDocument();
  });

  it("asks how a guest left before acknowledging it", async () => {
    const { requests } = mockApi({
      [`GET /api/clusters/${id}`]: () => ({ ...detail(summary()), leaving: [{ guestId: "g2", guest: "home/moraband/mrb-dns-01", address: "10.1.0.42", peerName: null, unconfirmed: false }] }),
      [`POST /api/clusters/${id}/members/g2/detached`]: () => undefined,
    });
    render();
    const button = await screen.findByRole("button", { name: "It has left" });
    expect(button).toBeDisabled();
    await userEvent.type(screen.getByLabelText("How it left"), "by hand");
    await userEvent.click(button);
    await waitFor(() => expect(requests.find((r) => r.path.endsWith("/detached"))?.body).toEqual({ note: "by hand" }));
  });

  it("cannot plan a cluster whose definition was deleted", async () => {
    mockApi({ [`GET /api/clusters/${id}`]: () => ({ ...detail(summary({ status: "missing", statusDetail: "cluster.yaml was deleted." })), missingSince: "2026-11-01T08:00:00Z" }) });
    render();
    expect(await screen.findByText("cluster.yaml was deleted.")).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Plan now" })).toBeDisabled();
  });

  it("appears in the inventory and links its trust", async () => {
    const inventory: Inventory = {
      revision: null,
      labs: [{ id: "lab", address: "home", name: "Home", valid: true, issues: [], installations: [], clusters: [summary({ trust: { ...summary().trust, untrusted: endpoints } })] }],
    };
    mockApi({ "GET /api/inventory": () => inventory });
    renderAt(<InventoryPage />, { path: "/inventory", route: "/inventory" });
    expect((await screen.findByRole("link", { name: "technitium" })).getAttribute("href")).toBe(`/clusters/${id}`);
    expect(screen.getByText("Needs trust").closest("a")).toHaveAttribute("href", `/clusters/${id}#trust`);
  });
});
