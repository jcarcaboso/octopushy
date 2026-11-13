import { screen, within } from "@testing-library/react";
import { describe, expect, it } from "vitest";
import type { GuestDetail, UptimeCheck } from "../api/types";
import { guest } from "../test/inventory";
import { runDetail } from "../test/runs";
import { mockApi, renderAt } from "../test/utils";
import { GuestPage } from "./GuestPage";

const text = [
  "schemaVersion: 1",
  "type: vm",
  "hardware:",
  "  cores: 4",
  "  memory: 8192",
  "  disks:",
  "    - { size: 64, datastore: local-lvm }",
  "    - { size: 100 }",
  "network:",
  "  bridge: vmbr1",
  "  mode: static",
  "",
].join("\n");

function detail(overrides: Partial<GuestDetail> = {}, summary: Partial<GuestDetail["guest"]> = {}): GuestDetail {
  return {
    guest: { ...guest("bb", []), id: "guest-1", accessAddress: "10.15.60.12", workflow: { reference: "machine-tools@1", resolved: "machine-tools@1.4" }, ...summary },
    definitionFile: "labs/home/proxmox/pve01/guests/bb/guest.yaml",
    definitionText: text,
    issues: [],
    policy: { approval: "manual", approvalSetBy: "labs/home/proxmox/pve01/guests/bb/guest.yaml", destructive: "manual", destructiveSetBy: "octopushy.yaml" },
    agentLabels: ["home"],
    agentLabelsSetBy: "labs/home/lab.yaml",
    binding: { installation: "home/pve01", vmid: 112, boundAt: "2026-10-30T08:00:00Z" },
    desiredCommit: "4eb842aa",
    checkedCommit: "4eb842aa",
    checkedAt: "2026-11-13T08:00:00Z",
    appliedCommit: "766bf87a",
    appliedAt: "2026-11-12T08:00:00Z",
    verifiedAt: "2026-11-12T08:00:00Z",
    inSync: false,
    outputs: { vmid: 112 },
    missingSince: null,
    retiredAt: null,
    runs: [],
    configuration: null,
    managedAccess: null,
    services: [],
    hostKey: null,
    ...overrides,
  } as GuestDetail;
}

const observed = {
  vmid: 112, name: "bb", host: "pve01", status: "running", template: false, tags: ["homelab"], maxCpu: 2, maxMemoryBytes: 8 * 1024 ** 3,
  uptimeSeconds: 3 * 3600, managedBy: null, managedById: null, kind: "qemu",
};

const ssh: UptimeCheck = {
  key: "guest-ssh|home/pve01/bb", kind: "guest-ssh", installation: "home/pve01", host: "pve01", resource: "home/pve01/bb", url: "ssh://10.15.60.12:22",
  status: "up", detail: null, since: null, checkedAt: null, elapsedMs: 3, agent: null, uptime: { day: 100, week: 100, month: 99.5 },
  beats: "u".repeat(96), latency: [], daily: [],
};

const route = { path: "/guests/:id", route: "/guests/guest-1" };

describe("GuestPage", () => {
  it("brings the run that waits for approval to the page, with its tiles and Approve", async () => {
    const run = { ...runDetail().run, resourceId: "guest-1" };
    mockApi({
      "GET /api/guests/guest-1": () => detail({ runs: [run] }, { status: "awaiting-approval", activeRun: run, observed }),
      "GET /api/runs/run-1": () => runDetail(),
      "GET /api/uptime": () => ({ checks: [ssh] }),
    });
    renderAt(<GuestPage />, route);
    const panel = await screen.findByRole("region", { name: "Provision #12 waits for your approval" });
    expect(await within(panel).findByRole("group", { name: "What changes" })).toBeInTheDocument();
    expect(within(panel).getByRole("button", { name: "Approve" })).toBeInTheDocument();
  });

  it("shows its figures from what Proxmox reports, the definition and its SSH check", async () => {
    mockApi({
      "GET /api/guests/guest-1": () => detail({}, { observed }),
      "GET /api/uptime": () => ({ checks: [ssh] }),
    });
    renderAt(<GuestPage />, route);
    await screen.findByRole("heading", { name: "Hardware" });
    const figure = (label: string) => screen.getAllByText(label).find((el) => el.tagName === "DT")!.parentElement!;
    expect(figure("Power")).toHaveTextContent("Runningup 3 h");
    expect(figure("Cores")).toHaveTextContent("2observed");
    expect(figure("Memory")).toHaveTextContent("8.0 GiBobserved");
    expect(figure("Disk")).toHaveTextContent("164 GiB2 disks");
    expect(figure("Address")).toHaveTextContent("10.15.60.12static, vmbr1");
    expect(await screen.findByText("99.5%")).toBeInTheDocument();
    expect(screen.getByText("answers in 3 ms")).toBeInTheDocument();
    expect(screen.queryByRole("region", { name: /waits for your approval/ })).toBeNull();
  });

  it("keeps workflow, revisions, identity and what Proxmox reports in one State card, and outputs folded", async () => {
    mockApi({ "GET /api/guests/guest-1": () => detail({}, { observed }) });
    renderAt(<GuestPage />, route);
    const state = (await screen.findByRole("heading", { name: "State" })).closest("section")!;
    for (const term of ["Workflow", "Desired", "Applied", "In sync", "Installation", "VMID", "Bound", "Name", "Host", "Template", "Tags"]) {
      expect(within(state).getByText(term, { selector: "dt" })).toBeInTheDocument();
    }
    expect(within(state).getByText("machine-tools@1.4")).toBeInTheDocument();
    const policy = screen.getByRole("heading", { name: "Policy" }).closest("section")!;
    expect(within(policy).getByText("Agent labels")).toBeInTheDocument();
    expect(within(policy).getByText("set by labs/home/lab.yaml")).toBeInTheDocument();
    expect(screen.getByText("Outputs").closest("details")).not.toHaveAttribute("open");
    // No issues, no Issues card.
    expect(screen.queryByRole("heading", { name: /^Issues/ })).toBeNull();
  });

  it("says when Proxmox has not reported it, and lists its issues", async () => {
    const issue = { severity: "warning", file: "labs/home/proxmox/pve01/guests/bb/guest.yaml", line: 4, message: "Few cores.", address: null };
    mockApi({ "GET /api/guests/guest-1": () => detail({ issues: [issue], outputs: null }) });
    renderAt(<GuestPage />, route);
    expect(await screen.findByText("Not observed: the installation has not reported a VM with this VMID.")).toBeInTheDocument();
    const power = screen.getByText("Power", { selector: "dt" }).parentElement!;
    expect(power).toHaveTextContent("Not observed");
    expect(screen.getByText("Cores", { selector: "dt" }).parentElement).toHaveTextContent("4declared");
    expect(screen.getByRole("heading", { name: /^Issues/ }).closest("section")).toHaveAttribute("id", "issues");
    expect(screen.getByText("Few cores.")).toBeInTheDocument();
    expect(screen.queryByText("Outputs")).toBeNull();
  });

  it("marks the values that are not what was last applied, and none once the guest is in sync", async () => {
    const before = text.replace("memory: 8192", "memory: 4096").replace("bridge: vmbr1", "bridge: vmbr0");
    mockApi({ "GET /api/guests/guest-1": () => detail({ appliedDefinitionText: before }), "GET /api/uptime": () => ({ checks: [] }) });
    const first = renderAt(<GuestPage />, route);
    expect((await screen.findByText("Bridge")).parentElement).toHaveTextContent("Last applied: vmbr0");
    expect(screen.getByText("2 values not applied yet")).toBeInTheDocument();
    first.unmount();

    // In sync after a plan that changed nothing: the older text says nothing about the machine.
    mockApi({ "GET /api/guests/guest-1": () => detail({ appliedDefinitionText: before, inSync: true }), "GET /api/uptime": () => ({ checks: [] }) });
    renderAt(<GuestPage />, route);
    await screen.findByText("Bridge");
    expect(screen.queryByText(/not applied yet/)).toBeNull();
  });
});
