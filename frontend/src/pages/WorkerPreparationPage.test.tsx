import { screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, expect, it, vi } from "vitest";
import type { WorkerPreparation } from "../api/types";
import { mockApi, problem, renderAt } from "../test/utils";
import { WorkerPreparationPage } from "./WorkerPreparationPage";
import { AgentsPage } from "./AgentsPage";

afterEach(() => vi.unstubAllGlobals());

function options(): WorkerPreparation {
  return {
    sourceUrl: "https://git.example.invalid/lab.git",
    branch: "main",
    headCommit: "a".repeat(40),
    lastFetchError: null,
    revision: {
      id: "revision-1", commit: "a".repeat(40), subject: "Configured hosts", author: "ops",
      committedAt: null, loadedAt: "2026-10-25T10:00:00Z", valid: true, errorCount: 0, warningCount: 0,
      trustedAt: null, trustedBy: null, isCurrentSource: true,
    },
    preparationBlockedReason: null,
    stateBackends: ["postgres"], deploymentBlockedReason: "Operator image configuration is required.", existingWorkers: [],
    installations: [
      {
        address: "home/cluster", name: "Home cluster", verifyTls: false, sharedToken: true,
        hosts: [
          { id: "pve-a", endpoint: "https://pve-a.invalid:8006", datastores: { images: "local", disks: "fast-a" }, bridges: ["vmbr0"] },
          { id: "pve-b", endpoint: "https://pve-b.invalid:8006", datastores: { disks: "fast-b" }, bridges: ["vmbr1"] },
        ],
      },
      {
        address: "remote/standalone", name: "Remote host", verifyTls: true, sharedToken: false,
        hosts: [{ id: "pve-c", endpoint: "https://pve-c.invalid:8006", datastores: { disks: "remote-disk" }, bridges: ["br-remote"] }],
      },
    ],
    deploymentGates: ["Missing: pinned NixOS worker image.", "Missing: native CSR enrollment and lifecycle guards.", "Not qualified: remote worker mTLS ingress."],
  };
}

it("reviews a Ready worker OS release without updating until explicitly requested", async () => {
  const user = userEvent.setup();
  const data = options();
  data.existingWorkers = [{
    resourceId: "worker-1", generation: "884d09b4-0934-43a4-bffb-d2f7977bc01c",
    address: "home/cluster/worker", host: "pve-a", vmid: 902,
    status: "Ready", agentId: "agent-1", runId: null,
    verifiedOsImageSha256: "b".repeat(64), verifiedOsUpdateRunId: "old-os-run",
    canVerifyRetirement: false, retirementBlockedReason: null,
  }];
  const { requests } = mockApi({
    "GET /api/managed-worker-preparation": () => data,
    "GET /api/managed-worker-preparation/worker-1/update": () => ({
      generation: data.existingWorkers![0].generation, archiveUrl: "https://release.test/worker.nar.xz",
      archiveSha256: "c".repeat(64), imageSha256: "a".repeat(64),
      systemPath: "/nix/store/00000000000000000000000000000000-nixos-system-worker-v1",
    }),
    "POST /api/managed-worker-preparation/worker-1/update": () => ({ runId: "update-1" }),
  });
  renderAt(<WorkerPreparationPage />, { path: "/agents/prepare", route: "/agents/prepare" });
  expect(await screen.findByRole("link", { name: "Verified OS release" })).toHaveAttribute("href", "/runs/old-os-run");
  await user.click(await screen.findByRole("button", { name: "Review OS update" }));
  expect(await screen.findByText(/worker.nar.xz/)).toBeVisible();
  expect(requests.every((r) => r.method === "GET")).toBe(true);
  await user.click(screen.getByRole("button", { name: "Drain and plan OS update" }));
  await waitFor(() => expect(requests.some((r) => r.method === "POST")).toBe(true));
  expect(requests.find((r) => r.method === "POST")?.body).toEqual({
    generation: data.existingWorkers![0].generation, archiveSha256: "c".repeat(64),
  });
  expect(await screen.findByRole("link", { name: "Review OS update plan" })).toHaveAttribute("href", "/runs/update-1");
});

it("prepares a review-only draft from a configured installation and host with small hardware defaults", async () => {
  const user = userEvent.setup();
  const { requests } = mockApi({
    "GET /api/managed-worker-preparation": () => options(),
    "POST /api/managed-worker-preparation": () => ({
      fileName: "octopushy-worker-01.managed-worker-preparation.json",
      content: '{"kind":"octopushy-managed-worker-preparation","preparationOnly":true}\n',
    }),
  });
  renderAt(<WorkerPreparationPage />, { path: "/agents/prepare", route: "/agents/prepare" });
  expect(await screen.findByLabelText("Proxmox installation")).toHaveValue("home/cluster");
  expect(screen.getByLabelText("Proxmox host")).toHaveValue("pve-a");
  expect(screen.getByLabelText("CPU sockets")).toHaveValue(1);
  expect(screen.getByLabelText("Cores per socket")).toHaveValue(2);
  expect(screen.getByLabelText("RAM (MiB)")).toHaveValue(2048);
  expect(screen.getByLabelText("Boot disk (GiB)")).toHaveValue(8);
  expect(screen.getByLabelText("Agent state disk (GiB)")).toHaveValue(16);
  expect(screen.getByLabelText("Disk datastore")).toHaveValue("fast-a");
  expect(screen.getByLabelText("Ethernet bridge")).toHaveValue("vmbr0");
  expect(screen.getByText(/current-source revision is not trusted/)).toBeInTheDocument();
  expect(screen.getByText(/Missing: pinned NixOS/)).toBeInTheDocument();
  expect(screen.queryByRole("button", { name: /^Deploy/ })).not.toBeInTheDocument();
  await user.click(screen.getByRole("button", { name: "Generate review draft" }));
  const download = await screen.findByRole("link", { name: "Download review draft" });
  expect(download).toHaveAttribute("download", "octopushy-worker-01.managed-worker-preparation.json");
  expect(requests.filter((r) => r.method !== "GET")).toEqual([{
    method: "POST", path: "/api/managed-worker-preparation", search: "",
    body: {
      revisionId: "revision-1", installation: "home/cluster", host: "pve-a", name: "octopushy-worker-01",
      vmid: 901, sockets: 1, cores: 2, memoryMiB: 2048, bootDiskGiB: 8, stateDiskGiB: 16,
      datastore: "fast-a", bridge: "vmbr0", vlan: null,
    },
  }]);
  await user.clear(screen.getByLabelText("Guest name"));
  await user.type(screen.getByLabelText("Guest name"), "changed-worker");
  await waitFor(() => expect(screen.queryByRole("link", { name: "Download review draft" })).not.toBeInTheDocument());
});

it("changes disk and Ethernet choices with the selected host, and sends edited hardware without caller endpoints", async () => {
  const user = userEvent.setup();
  const { requests } = mockApi({
    "GET /api/managed-worker-preparation": () => options(),
    "POST /api/managed-worker-preparation": () => ({ fileName: "worker.json", content: "{}" }),
  });
  renderAt(<WorkerPreparationPage />, { path: "/agents/prepare", route: "/agents/prepare" });
  await user.selectOptions(await screen.findByLabelText("Proxmox host"), "pve-b");
  expect(screen.getByLabelText("Disk datastore")).toHaveValue("fast-b");
  expect(screen.getByLabelText("Ethernet bridge")).toHaveValue("vmbr1");
  expect(screen.queryByRole("option", { name: "fast-a" })).not.toBeInTheDocument();
  expect(screen.queryByRole("option", { name: "vmbr0" })).not.toBeInTheDocument();
  for (const [label, value] of [
    ["Guest name", "worker-b"], ["VMID", "902"], ["CPU sockets", "2"], ["Cores per socket", "3"],
    ["RAM (MiB)", "4096"], ["Boot disk (GiB)", "12"], ["Agent state disk (GiB)", "32"], ["VLAN (optional)", "42"],
  ]) {
    const field = screen.getByLabelText(label);
    await user.clear(field);
    await user.type(field, value);
  }
  await user.click(screen.getByRole("button", { name: "Generate review draft" }));
  await screen.findByRole("link", { name: "Download review draft" });
  expect(requests.find((r) => r.method === "POST")?.body).toEqual({
    revisionId: "revision-1", installation: "home/cluster", host: "pve-b", name: "worker-b",
    vmid: 902, sockets: 2, cores: 3, memoryMiB: 4096, bootDiskGiB: 12, stateDiskGiB: 32,
    datastore: "fast-b", bridge: "vmbr1", vlan: 42,
  });
  await user.selectOptions(screen.getByLabelText("Proxmox installation"), "remote/standalone");
  expect(screen.getByLabelText("Proxmox host")).toHaveValue("pve-c");
  expect(screen.getByLabelText("Disk datastore")).toHaveValue("remote-disk");
  expect(screen.getByLabelText("Ethernet bridge")).toHaveValue("br-remote");
  expect(screen.getByText(/Split Proxmox credentials/)).toBeInTheDocument();
  expect(screen.queryByRole("link", { name: "Download review draft" })).not.toBeInTheDocument();
});

it("starts on the installation selected from inventory", async () => {
  mockApi({ "GET /api/managed-worker-preparation": () => options() });
  renderAt(<WorkerPreparationPage />, { path: "/agents/prepare", route: "/agents/prepare?installation=remote%2Fstandalone" });
  expect(await screen.findByLabelText("Proxmox installation")).toHaveValue("remote/standalone");
  expect(screen.getByLabelText("Proxmox host")).toHaveValue("pve-c");
  expect(screen.getByLabelText("Disk datastore")).toHaveValue("remote-disk");
});

it("does not silently substitute another installation for an obsolete inventory selection", async () => {
  const user = userEvent.setup();
  mockApi({ "GET /api/managed-worker-preparation": () => options() });
  renderAt(<WorkerPreparationPage />, { path: "/agents/prepare", route: "/agents/prepare?installation=old%2Fremoved" });
  expect(await screen.findByLabelText("Proxmox installation")).toHaveValue("");
  expect(screen.getByRole("button", { name: "Generate review draft" })).toBeDisabled();
  await user.selectOptions(screen.getByLabelText("Proxmox installation"), "home/cluster");
  expect(screen.getByRole("button", { name: "Generate review draft" })).toBeEnabled();
});

it.each([
  ["no configured source", { sourceUrl: null, headCommit: null, revision: null, installations: [], preparationBlockedReason: "Configure a lab repository first." }],
  ["unloaded source head", { headCommit: null, revision: null, installations: [], preparationBlockedReason: "Sync the current repository source." }],
  ["invalid repository", { installations: [], preparationBlockedReason: "Fix the repository definition and sync." }],
  ["no valid installation", { installations: [] }],
] satisfies [string, Partial<WorkerPreparation>][])("does not offer generation with %s", async (_, overrides) => {
  const { requests } = mockApi({ "GET /api/managed-worker-preparation": () => ({ ...options(), ...overrides }) });
  renderAt(<WorkerPreparationPage />, { path: "/agents/prepare", route: "/agents/prepare" });
  await screen.findByText("Current repository source");
  expect(screen.queryByRole("button", { name: "Generate review draft" })).not.toBeInTheDocument();
  expect(requests.every((r) => r.method === "GET")).toBe(true);
});

it("blocks preparation on hosts without declared disk storage or Ethernet bridges", async () => {
  const data = options();
  data.installations[0].hosts[0].datastores = {};
  data.installations[0].hosts[0].bridges = [];
  mockApi({ "GET /api/managed-worker-preparation": () => data });
  renderAt(<WorkerPreparationPage />, { path: "/agents/prepare", route: "/agents/prepare" });
  expect(await screen.findByRole("button", { name: "Generate review draft" })).toBeDisabled();
  expect(screen.getByText(/host needs configured datastores/)).toBeInTheDocument();
});

it.each([
  ["Guest name", "../escape"], ["VMID", "99"], ["CPU sockets", "0"], ["Cores per socket", "1.5"],
  ["RAM (MiB)", "511"], ["Boot disk (GiB)", "7"], ["Agent state disk (GiB)", "15"], ["VLAN (optional)", "4095"],
])("uses native form validation for invalid %s", async (label, value) => {
  const user = userEvent.setup();
  const { requests } = mockApi({ "GET /api/managed-worker-preparation": () => options() });
  renderAt(<WorkerPreparationPage />, { path: "/agents/prepare", route: "/agents/prepare" });
  const field = await screen.findByLabelText(label);
  await user.clear(field);
  await user.type(field, value);
  await user.click(screen.getByRole("button", { name: "Generate review draft" }));
  expect(field).toBeInvalid();
  expect(requests.filter((r) => r.method === "POST")).toHaveLength(0);
});

it("shows stale-source refusal without an executable action and reloads onto a fresh revision", async () => {
  const user = userEvent.setup();
  let data = options();
  const { requests } = mockApi({
    "GET /api/managed-worker-preparation": () => data,
    "POST /api/managed-worker-preparation": () => problem(409, "The current repository source or head changed. Reload preparation."),
  });
  renderAt(<WorkerPreparationPage />, { path: "/agents/prepare", route: "/agents/prepare" });
  await user.click(await screen.findByRole("button", { name: "Generate review draft" }));
  expect(await screen.findByText(/source or head changed/)).toBeInTheDocument();
  expect(screen.queryByRole("link", { name: "Download review draft" })).not.toBeInTheDocument();
  data = { ...data, revision: { ...data.revision!, id: "revision-2", commit: "b".repeat(40) }, headCommit: "b".repeat(40) };
  await user.click(screen.getByRole("button", { name: "Reload preparation" }));
  await screen.findByText("b".repeat(40));
  await user.click(screen.getByRole("button", { name: "Generate review draft" }));
  await waitFor(() => expect(requests.filter((r) => r.method === "POST")).toHaveLength(2));
  expect(requests.filter((r) => r.method === "POST")[1].body).toMatchObject({ revisionId: "revision-2" });
  expect(requests.filter((r) => r.method !== "GET").every((r) => r.path === "/api/managed-worker-preparation")).toBe(true);
});

it("does not confuse exact revision trust or a local worker with deployment readiness", async () => {
  const data = options();
  data.revision = { ...data.revision!, trustedAt: "2026-10-25T10:05:00Z", trustedBy: "ops" };
  data.lastFetchError = "Unable to fetch current remote head.";
  mockApi({ "GET /api/managed-worker-preparation": () => data });
  renderAt(<WorkerPreparationPage />, { path: "/agents/prepare", route: "/agents/prepare" });
  expect(await screen.findByText(/revision trusted.*not deployment approval/)).toBeInTheDocument();
  expect(screen.getByRole("heading", { name: "Deployment and safety checks" })).toBeInTheDocument();
  expect(screen.getByText(/remote worker mTLS ingress/)).toBeInTheDocument();
  expect(screen.getByText(/last loaded source head/)).toBeInTheDocument();
  expect(screen.queryByRole("button", { name: /^Deploy/ })).not.toBeInTheDocument();
});

it("reviews the exact repository files, requires confirmation and publishes without trusting or approving", async () => {
  const user = userEvent.setup();
  const current = { ...options(), stateBackends: ["postgres"], deploymentBlockedReason: null, existingWorkers: [] };
  const { requests } = mockApi({
    "GET /api/managed-worker-preparation": () => current,
    "POST /api/managed-worker-preparation/review": () => ({
      address: "home/cluster/octopushy-worker-01", generation: "generation-1", branch: "main",
      baseCommit: "a".repeat(40), reviewSha256: "d".repeat(64), alreadyExists: false,
      files: { "labs/home/proxmox/cluster/guests/octopushy-worker-01/guest.yaml": "worker: { templateVersion: 1 }\n<script>unsafe()</script>" },
    }),
    "POST /api/managed-worker-preparation/publish": () => ({ address: "home/cluster/octopushy-worker-01", commit: "b".repeat(40), alreadyPresent: false }),
  });
  renderAt(<WorkerPreparationPage />, { path: "/agents/prepare", route: "/agents/prepare" });
  await user.click(await screen.findByRole("button", { name: "Review deployment" }));
  expect(await screen.findByText("<script>unsafe()</script>", { exact: false })).toBeInTheDocument();
  expect(document.querySelector("script")).toBeNull();
  const publish = screen.getByRole("button", { name: "Add worker to repository" });
  expect(publish).toBeDisabled();
  await user.click(screen.getByRole("checkbox", { name: /I reviewed these exact files/ }));
  await user.click(publish);
  expect(await screen.findByText(/Worker definition published/)).toBeInTheDocument();
  const mutation = requests.filter((r) => r.method === "POST");
  expect(mutation.map((r) => r.path)).toEqual(["/api/managed-worker-preparation/review", "/api/managed-worker-preparation/publish"]);
  expect(mutation[1].body).toMatchObject({ reviewSha256: "d".repeat(64), confirmRepositoryWrite: true, draft: { stateBackend: "postgres", selection: { vmid: 901, sockets: 1, cores: 2 } } });
  expect(screen.getByRole("link", { name: "Review and trust the new revision" })).toHaveAttribute("href", "/repository");
});

it("disables publication when image/ingress setup is missing and shows existing workers rather than redeploying", async () => {
  mockApi({ "GET /api/managed-worker-preparation": () => ({ ...options(), stateBackends: ["postgres"], deploymentBlockedReason: "Publish the tested worker image first.", existingWorkers: [{ address: "home/cluster/worker-01", host: "pve-a", vmid: 999, generation: "g" }] }) });
  renderAt(<WorkerPreparationPage />, { path: "/agents/prepare", route: "/agents/prepare" });
  expect(await screen.findByText("Publish the tested worker image first.")).toBeInTheDocument();
  expect(screen.getByRole("button", { name: "Review deployment" })).toBeDisabled();
  expect(screen.getByText("home/cluster/worker-01")).toBeInTheDocument();
});

it("requires explicit confirmation before checking an already removed worker and refreshes its retirement state", async () => {
  const user = userEvent.setup();
  let retired = false;
  const worker = {
    address: "home/cluster/worker-old", host: "pve-a", vmid: 901, generation: "old-generation",
    resourceId: "old-resource", runId: "old-deployment", agentId: "deleted-agent",
    status: "Enrollment removed", canVerifyRetirement: true, retirementBlockedReason: null,
  };
  const healthy = { ...worker, address: "home/cluster/worker-live", generation: "live-generation", resourceId: "live-resource",
    status: "Ready", canVerifyRetirement: false, retirementBlockedReason: "Remove the definition first." };
  const { requests } = mockApi({
    "GET /api/managed-worker-preparation": () => ({ ...options(), existingWorkers: retired ? [healthy] : [worker, healthy] }),
    "POST /api/managed-worker-preparation/old-resource/retire": () => {
      retired = true;
      return { runId: "retirement-run" };
    },
  });
  renderAt(<WorkerPreparationPage />, { path: "/agents/prepare", route: "/agents/prepare" });
  await user.click(await screen.findByRole("button", { name: "Verify retirement for home/cluster/worker-old" }));
  expect(requests.some((r) => r.method === "POST")).toBe(false);
  expect(screen.getByText(/This does not destroy a VM/)).toBeInTheDocument();
  await user.click(screen.getByRole("button", { name: "Verify VM absence" }));
  await waitFor(() => expect(screen.queryByText("home/cluster/worker-old")).not.toBeInTheDocument());
  expect(screen.getByText("home/cluster/worker-live")).toBeInTheDocument();
  expect(requests.filter((r) => r.method !== "GET")).toEqual([{
    method: "POST", path: "/api/managed-worker-preparation/old-resource/retire", search: "",
    body: { generation: "old-generation", confirmAlreadyRemoved: true },
  }]);
});

it("does not offer retirement while its server-side prerequisites are blocked", async () => {
  const { requests } = mockApi({
    "GET /api/managed-worker-preparation": () => ({ ...options(), existingWorkers: [{
      address: "home/cluster/worker-old", host: "pve-a", vmid: 901, generation: "old-generation",
      resourceId: "old-resource", status: "Enrollment removed", canVerifyRetirement: false,
      retirementBlockedReason: "Resolve uncertain outcomes first.",
    }] }),
  });
  renderAt(<WorkerPreparationPage />, { path: "/agents/prepare", route: "/agents/prepare" });
  expect(await screen.findByRole("button", { name: "Verify retirement for home/cluster/worker-old" })).toBeDisabled();
  expect(screen.getByText("Resolve uncertain outcomes first.")).toBeInTheDocument();
  expect(requests.some((r) => r.method !== "GET")).toBe(false);
});

it("refreshes the managed-worker view immediately after enrollment deletion and explains that no VM is deleted", async () => {
  const user = userEvent.setup();
  let removed = false;
  const { requests } = mockApi({
    "GET /api/agents": () => removed ? [] : [{
      id: "old-agent", name: "old-worker", labels: [], maxConcurrency: 1, disabled: true, connected: false,
      enrolledAt: "2026-10-27T10:00:00Z", lastSeenAt: null, version: "test", executors: ["proxmox"],
      toolRuntime: "podman", certificateExpiresAt: "2026-11-27T10:00:00Z", runningAttempts: 0,
    }],
    "GET /api/agents/tokens": () => [],
    "DELETE /api/agents/old-agent": () => { removed = true; },
    "GET /api/managed-worker-preparation": () => ({ ...options(), existingWorkers: [{
      address: "home/cluster/worker-old", host: "pve-a", vmid: 901, generation: "old-generation",
      resourceId: "old-resource", agentId: "old-agent", status: removed ? "Enrollment removed" : "Disabled",
      canVerifyRetirement: removed, retirementBlockedReason: removed ? null : "Remove the enrollment first.",
    }] }),
  });
  renderAt(<><AgentsPage /><WorkerPreparationPage /></>, { path: "/agents", route: "/agents" });
  await user.click(await screen.findByRole("button", { name: "Remove" }));
  expect(screen.getByText(/This does not delete its VM/)).toBeInTheDocument();
  await user.click(screen.getByRole("button", { name: "Remove the agent" }));
  await waitFor(() => expect(screen.getByRole("button", { name: "Verify retirement for home/cluster/worker-old" })).toBeEnabled());
  await waitFor(() => expect(requests.filter((r) => r.path === "/api/managed-worker-preparation")).toHaveLength(2));
  expect(requests.filter((r) => r.method !== "GET").map((r) => r.path)).toEqual(["/api/agents/old-agent"]);
});

it("shows a refused retirement check without claiming retirement or silently retrying", async () => {
  const user = userEvent.setup();
  const { requests } = mockApi({
    "GET /api/managed-worker-preparation": () => ({ ...options(), existingWorkers: [{
      address: "home/cluster/worker-old", host: "pve-a", vmid: 901, generation: "old-generation",
      resourceId: "old-resource", status: "Enrollment removed", canVerifyRetirement: true,
    }] }),
    "POST /api/managed-worker-preparation/old-resource/retire": () => problem(409, "The Proxmox endpoint changed."),
  });
  renderAt(<WorkerPreparationPage />, { path: "/agents/prepare", route: "/agents/prepare" });
  await user.click(await screen.findByRole("button", { name: "Verify retirement for home/cluster/worker-old" }));
  await user.click(screen.getByRole("button", { name: "Verify VM absence" }));
  expect(await screen.findByText("The Proxmox endpoint changed.")).toBeInTheDocument();
  expect(screen.getByText("home/cluster/worker-old")).toBeInTheDocument();
  expect(requests.filter((r) => r.method === "POST")).toHaveLength(1);
});

it("requires a fresh review after stale publication and never silently retries a write", async () => {
  const user = userEvent.setup();
  const { requests } = mockApi({
    "GET /api/managed-worker-preparation": () => ({ ...options(), deploymentBlockedReason: null }),
    "POST /api/managed-worker-preparation/review": () => ({ address: "home/cluster/worker", generation: "g", branch: "main", baseCommit: "a".repeat(40), reviewSha256: "d".repeat(64), files: { "guest.yaml": "worker" }, alreadyExists: false }),
    "POST /api/managed-worker-preparation/publish": () => problem(409, "The repository branch changed."),
  });
  renderAt(<WorkerPreparationPage />, { path: "/agents/prepare", route: "/agents/prepare" });
  await user.click(await screen.findByRole("button", { name: "Review deployment" }));
  await user.click(await screen.findByRole("checkbox", { name: /I reviewed these exact files/ }));
  await user.click(screen.getByRole("button", { name: "Add worker to repository" }));
  expect(await screen.findByText("The repository branch changed.")).toBeInTheDocument();
  expect(screen.getByRole("checkbox", { name: /I reviewed these exact files/ })).not.toBeChecked();
  expect(screen.getByRole("button", { name: "Add worker to repository" })).toBeDisabled();
  expect(requests.filter((r) => r.path.endsWith("/publish"))).toHaveLength(1);
  await user.click(screen.getByRole("button", { name: "Review deployment" }));
  await waitFor(() => expect(screen.getByRole("checkbox", { name: /I reviewed these exact files/ })).not.toBeDisabled());
  expect(screen.getByRole("checkbox", { name: /I reviewed these exact files/ })).not.toBeChecked();
});

it("an existing exact generation offers status instead of a second deployment", async () => {
  const user = userEvent.setup();
  const { requests } = mockApi({
    "GET /api/managed-worker-preparation": () => ({ ...options(), deploymentBlockedReason: null }),
    "POST /api/managed-worker-preparation/review": () => ({ address: "home/cluster/worker", generation: "g", branch: "main", baseCommit: "a".repeat(40), reviewSha256: "d".repeat(64), files: {}, alreadyExists: true }),
  });
  renderAt(<WorkerPreparationPage />, { path: "/agents/prepare", route: "/agents/prepare" });
  await user.click(await screen.findByRole("button", { name: "Review deployment" }));
  expect(await screen.findByRole("heading", { name: "Worker already exists" })).toBeInTheDocument();
  expect(screen.queryByRole("button", { name: "Add worker to repository" })).not.toBeInTheDocument();
  expect(requests.filter((r) => r.method === "POST")).toHaveLength(1);
});
