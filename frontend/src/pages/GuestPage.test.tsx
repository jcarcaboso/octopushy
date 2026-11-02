import { screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, describe, expect, it, vi } from "vitest";
import type { GuestDetail, GuestTrust } from "../api/types";
import { mockApi, problem, renderAt } from "../test/utils";
import { GuestPage } from "./GuestPage";

const id = "guest-1";

function trust(overrides: Partial<GuestTrust> = {}): GuestTrust {
  return {
    keys: ["ha-db/postgres-password", "ha-db/victoriametrics-password"],
    codeDigest: "d".repeat(64),
    execution: {
      backend: { type: "built-in" },
      secrets: { postgres_password: "ha-db/postgres-password", victoriametrics_password: "ha-db/victoriametrics-password" },
      agentLabels: ["home"],
      proxmox: { endpoint: "https://10.15.5.101:8006/", verifyTls: true, privilegedToken: { store: "proxmox/ziost/privileged" } },
      guest: {
        vmid: 102,
        host: "ziost",
        access: { sshAuthorizedKeys: ["ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIAttackerKeyAttackerKey attacker@evil"] },
        hardware: { mountPoints: [{ path: "/data", volume: "vm-ssd:vm-9102-disk-0" }] },
      },
      host: { datastores: { disks: "vm-ssd" }, bridges: ["apps"] },
      template: { source: { url: "http://download.proxmox.com/images/system/debian-13.tar.zst" } },
    },
    trusted: false,
    cluster: null,
    refusal: null,
    trustedAt: null,
    trustedBy: null,
    ...overrides,
  };
}

function detail(current: GuestTrust, status = "blocked"): GuestDetail {
  return {
    guest: {
      id,
      address: "home/ziost/ha-db",
      name: "ha-db",
      type: "container",
      host: "ziost",
      vmid: 102,
      lifecycle: "active",
      status,
      statusDetail: "Waiting for trust: home/ziost/ha-db has no grant for the protected secret ha-db/postgres-password.",
      valid: true,
      observed: null,
      activeRun: null,
      lastRun: null,
    },
    definitionFile: "labs/home/proxmox/ziost/guests/ha-db/guest.yaml",
    definitionText: "schemaVersion: 1\n",
    issues: [],
    policy: null,
    agentLabels: [],
    agentLabelsSetBy: null,
    binding: null,
    desiredCommit: "abc",
    checkedCommit: null,
    checkedAt: null,
    appliedCommit: null,
    appliedAt: null,
    verifiedAt: null,
    inSync: false,
    outputs: null,
    missingSince: null,
    retiredAt: null,
    runs: [],
    diskUsage: { enabled: false, intervalSeconds: 3600, configuredBytes: null, usedBytes: null, receivedAt: null, staleAfter: null },
    trust: current,
    stateful: false,
    dataGuarded: false,
  };
}

function render() {
  return renderAt(<GuestPage />, { path: "/guests/:id", route: `/guests/${id}` });
}

describe("GuestPage trust", () => {
  afterEach(() => vi.restoreAllMocks());

  it("shows why a guest waits, what it would trust, and sends exactly the digest it showed", async () => {
    let current = trust();
    const { requests } = mockApi({
      [`GET /api/guests/${id}`]: () => detail(current),
      [`POST /api/guests/${id}/trust`]: () => {
        current = trust({ trusted: true, trustedAt: "2026-11-02T08:00:00Z", trustedBy: "ops" });
        return current;
      },
    });
    render();
    expect(await screen.findByText("Waiting for trust")).toBeInTheDocument();
    // The protected entries it binds, and the member details Trust cluster shows.
    expect(screen.getByText("ha-db/postgres-password", { selector: "li" })).toBeInTheDocument();
    expect(screen.getByText("ha-db/victoriametrics-password", { selector: "li" })).toBeInTheDocument();
    expect(screen.getByText("Can log in as root:")).toBeInTheDocument();
    expect(screen.getByText(/ssh-ed25519 …AIAttackerKeyAttackerKey attacker@evil/)).toBeInTheDocument();
    expect(screen.getByText(/postgres_password = ha-db\/postgres-password, victoriametrics_password = ha-db\/victoriametrics-password/)).toBeInTheDocument();
    expect(screen.getByText(/credentials privileged proxmox\/ziost\/privileged/)).toBeInTheDocument();
    expect(screen.getByText(/volumes \/data = vm-ssd:vm-9102-disk-0/)).toBeInTheDocument();
    expect(screen.getByText("d".repeat(64))).toBeInTheDocument();

    await userEvent.click(screen.getByRole("button", { name: "Trust guest" }));
    await waitFor(() => expect(requests.find((r) => r.path.endsWith("/trust"))?.body).toEqual({ codeDigest: "d".repeat(64) }));
    await waitFor(() => expect(screen.queryByRole("button", { name: "Trust guest" })).not.toBeInTheDocument());
  });

  it("shows a refused trust", async () => {
    mockApi({
      [`GET /api/guests/${id}`]: () => detail(trust()),
      [`POST /api/guests/${id}/trust`]: () => problem(409, "The guest's code, or how it runs, changed since it was shown. Review it again."),
    });
    render();
    await userEvent.click(await screen.findByRole("button", { name: "Trust guest" }));
    expect(await screen.findByText(/changed since it was shown/)).toBeInTheDocument();
  });

  it("leaves a cluster member to Trust cluster", async () => {
    mockApi({ [`GET /api/guests/${id}`]: () => detail(trust({ cluster: "home/clusters/technitium" })) });
    render();
    expect(await screen.findByText(/Trust cluster, on the cluster.s page/)).toBeInTheDocument();
    expect(screen.getByText("home/clusters/technitium", { selector: "span.mono" })).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Trust guest" })).not.toBeInTheDocument();
  });

  it("explains a secret no grant can release", async () => {
    mockApi({ [`GET /api/guests/${id}`]: () => detail(trust({ refusal: "it gives the protected secret x to its state backend as LEAK, and that is never released." })) });
    render();
    expect(await screen.findByText(/never released/)).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Trust guest" })).not.toBeInTheDocument();
  });

  it("asks for nothing once the guest is trusted, or when it binds no protected secret", async () => {
    mockApi({ [`GET /api/guests/${id}`]: () => detail(trust({ trusted: true, trustedAt: "2026-11-02T08:00:00Z", trustedBy: "ops" }), "in-sync") });
    render();
    expect(await screen.findByText("home/ziost/ha-db")).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Trust guest" })).not.toBeInTheDocument();
    expect(screen.queryByText("Waiting for trust")).not.toBeInTheDocument();
  });

  it("shows nothing for a guest with no protected secret", async () => {
    mockApi({ [`GET /api/guests/${id}`]: () => detail(trust({ keys: [], codeDigest: "e".repeat(64) }), "in-sync") });
    render();
    expect(await screen.findByText("home/ziost/ha-db")).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Trust guest" })).not.toBeInTheDocument();
  });

  it("says whether a stateful guest's data is guarded yet", async () => {
    const bound = { installation: "home/ziost", vmid: 102, boundAt: "2026-11-02T08:00:00Z" };
    const guarded = { ...detail(trust({ keys: [] }), "in-sync"), binding: bound, stateful: true, dataGuarded: true };
    mockApi({ [`GET /api/guests/${id}`]: () => guarded });
    const view = render();
    expect(await screen.findByText(/stops before approval/)).toBeInTheDocument();
    view.unmount();

    mockApi({ [`GET /api/guests/${id}`]: () => ({ ...guarded, dataGuarded: false }) });
    render();
    expect(await screen.findByText(/guarded once its first deployment is verified/)).toBeInTheDocument();
  });
});
