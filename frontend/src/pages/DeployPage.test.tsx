import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { MemoryRouter, Route, Routes } from "react-router";
import { afterEach, describe, expect, it, vi } from "vitest";
import type { ChangePreview, DeployTargets, WorkflowCatalog } from "../api/types";
import worker from "../test/fixtures/octopushy-worker.inputs.schema.json";
import { mockApi, type RecordedRequest } from "../test/utils";
import { DeployPage } from "./DeployPage";

const base = "4b1d0c0ffee4b1d0c0ffee4b1d0c0ffee4b1d0c0";
const file = "labs/home/proxmox/pve01/guests/w2/guest.yaml";

const catalog: WorkflowCatalog = {
  source: "repository",
  repository: "https://github.com/example/lab-workflows.git",
  workflows: [
    {
      id: "octopushy-worker",
      manages: "vm",
      description: "A Fedora VM running an Octopushy agent.",
      versions: ["1.1", "1.0"].map((version) => ({
        id: "octopushy-worker", version, commit: base, usable: true, issue: null, manages: "vm",
        description: "A Fedora VM running an Octopushy agent.", inputs: worker, secrets: ["worker_enrollment_token"], setup: true,
        ui: {
          vmid: { generate: "next-vmid" },
          access: { user: "ops" },
          secrets: {
            worker_enrollment_token: { generate: "agent-enrollment-token", labels: "inputs.worker_agent_labels" },
            worker_extra_key: { requiredWhen: { pointer: "/inputs/worker_agent_labels", value: "extra" } },
          },
        },
      })),
    },
    {
      id: "web",
      manages: "service",
      description: "A web service.",
      versions: [{ id: "web", version: "2.0", commit: base, usable: true, issue: null, manages: "service", description: "A web service.",
        inputs: null, secrets: [], setup: false, ui: null }],
    },
  ],
};

const targets: DeployTargets = {
  baseCommit: base,
  blockedReason: null,
  templates: ["fedora-44"],
  installations: [
    {
      address: "home/pve01", lab: "home", name: "pve01", type: "standalone", managed: true, reason: null,
      hosts: [{ name: "pve01", declared: true, observed: true, status: "online" }],
      guests: [{ address: "home/pve01/w1", name: "w1", workflow: "octopushy-worker@1", valid: true }],
      agentLabels: ["home"], observedAt: "2026-11-04T09:00:00Z", id: "inst-pve01", vmidProblem: null,
    },
    {
      address: "home/ziost", lab: "home", name: "ziost", type: "standalone", managed: false,
      reason: "home/ziost is observed only (manage: false): Octopushy changes nothing there, so nothing can be deployed to it.",
      hosts: [], guests: [], agentLabels: ["home"], observedAt: null,
    },
  ],
};

const diff = `diff --git a/${file} b/${file}
new file mode 100644
--- /dev/null
+++ b/${file}
@@ -0,0 +1,4 @@
+schemaVersion: 1
+type: vm
+host: pve01
+vmid: 311                         # free on home/pve01 when deployed
`;

function preview(overrides: Partial<ChangePreview> = {}): ChangePreview {
  return {
    kind: "create-guest", address: "home/pve01/w2", subject: "w2: deploy octopushy-worker@1 on home/pve01, host pve01, VMID 311",
    message: "w2: deploy octopushy-worker@1\n\nOctopushy-Change: <given when committed>\n", baseCommit: base, files: [file], diff, valid: true,
    issues: [], fieldIssues: [], refused: null, nextVmid: null,
    reviewedRelease: `octopushy-worker@1.1 at ${base}`, reviewDigest: "d".repeat(64),
    generated: [{ secret: "worker_enrollment_token", key: "guests/home/pve01/w2/worker_enrollment_token", generator: "agent-enrollment-token",
      detail: "a one-time enrollment token for an agent named w2, labels [home], valid for 72 hours" }],
    ...overrides,
  };
}

const change = { id: "change-1", kind: "create-guest", resourceKind: "guest", resourceId: null, address: "home/pve01/w2", author: "ops",
  message: "w2: deploy", baseCommit: base, commit: base, commitUrl: null, revisionId: null, status: "committed", detail: null, files: [file],
  runs: [], createdAt: "2026-11-04T09:00:00Z", syncedAt: null, updatedAt: "2026-11-04T09:00:00Z", vmid: 311, preApproval: null,
  preApprovalDetail: null, generatedKeys: [] };

function api(extra: Record<string, (r: RecordedRequest) => unknown> = {}) {
  return mockApi({
    "GET /api/deploy/targets": () => targets,
    "GET /api/workflows": () => catalog,
    "GET /api/variables": () => ({ entries: [{ key: "shared/token" }], missing: [], system: [] }),
    "GET /api/deploy/vmid": () => ({ installation: "home/pve01", vmid: 311, from: 100 }),
    "POST /api/deploy/preview": () => preview(),
    "POST /api/deploy": () => change,
    ...extra,
  });
}

function renderWizard() {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false, refetchOnWindowFocus: false }, mutations: { retry: false } } });
  return render(
    <QueryClientProvider client={client}>
      <MemoryRouter initialEntries={["/deploy"]}>
        <Routes>
          <Route path="/deploy" element={<DeployPage />} />
          <Route path="/changes/:id" element={<p>The change page</p>} />
        </Routes>
      </MemoryRouter>
    </QueryClientProvider>,
  );
}

/** Walks the wizard to its review: the worker workflow, pve01, the name w2, the form's defaults, a generated token. */
async function toReview(user: ReturnType<typeof userEvent.setup>) {
  await user.click(await screen.findByRole("radio", { name: /octopushy-worker/ }));
  expect(screen.getByText("Newest per major: 1.1")).toBeInTheDocument();
  expect(screen.getByText("octopushy-worker@1")).toBeInTheDocument();
  await user.click(screen.getByRole("button", { name: /Next: where/ }));

  await user.click(screen.getByRole("radio", { name: /home\/pve01/ }));
  await waitFor(() => expect(screen.getByLabelText("VMID")).toHaveValue("311"));
  expect(screen.getByLabelText("Host")).toHaveValue("pve01");
  await user.type(screen.getByLabelText("Name"), "w2");
  await user.click(screen.getByRole("button", { name: /Next: form/ }));

  // The form, from the workflow's inputs schema.
  expect(await screen.findByText("worker_server_url")).toBeInTheDocument();
  await user.click(screen.getByRole("button", { name: /Next: secrets/ }));

  const token = screen.getByRole("group", { name: /worker_enrollment_token/ });
  expect(within(token).getByRole("radio", { name: /Generate a one-time agent enrollment token/ })).toBeChecked();
  expect(within(token).getByText("guests/home/pve01/w2/worker_enrollment_token")).toBeInTheDocument();
  expect(within(token).getByLabelText("Labels")).toHaveValue("home");
  expect(screen.getByLabelText("Login")).toHaveValue("ops");
  // Needed only when the form selects what uses it: optional here, and left out.
  const extra = screen.getByRole("group", { name: /worker_extra_key/ });
  expect(within(extra).getByText("optional")).toBeInTheDocument();
  expect(within(extra).getByRole("radio", { name: "Leave it out" })).toBeChecked();
  expect(screen.getByLabelText("Host key, as a store key")).toHaveValue("guests/home/pve01/w2/host-key");
  await user.click(screen.getByRole("button", { name: /Review the deploy/ }));
}

describe("DeployPage", () => {
  afterEach(() => vi.unstubAllGlobals());

  it("walks the steps, shows an observed-only installation disabled, reviews the exact diff, and deploys with approval", async () => {
    const user = userEvent.setup();
    const { requests } = api();
    renderWizard();

    await user.click(await screen.findByRole("radio", { name: /octopushy-worker/ }));
    await user.click(screen.getByRole("button", { name: /Next: where/ }));
    const ziost = screen.getByRole("radio", { name: /home\/ziost/ });
    expect(ziost).toBeDisabled();
    expect(screen.getByText(/home\/ziost is observed only \(manage: false\)/)).toBeInTheDocument();
    await user.click(screen.getByRole("button", { name: /1\. Workflow/ }));

    await toReview(user);
    expect(await screen.findByText(/a one-time enrollment token for an agent named w2/)).toBeInTheDocument();
    expect(screen.getByText(/\+vmid: 311/)).toBeInTheDocument();
    const sent = requests.find((r) => r.method === "POST" && r.path === "/api/deploy/preview")!.body as Record<string, unknown>;
    expect(sent).toMatchObject({
      kind: "create-guest", baseCommit: base, parent: "home/pve01", name: "w2", workflow: "octopushy-worker@1", host: "pve01", vmid: 311,
      template: null, accessUser: "ops", hostKey: "guests/home/pve01/w2/host-key", approveAtDeploy: false,
      secrets: { worker_enrollment_token: { generate: "agent-enrollment-token", labels: ["home"], name: "w2" } },
    });
    // Prefilled from the schema: the required disks list starts with its one required item.
    expect((sent.hardware as Record<string, unknown>).disks).toEqual([{}]);

    await user.click(screen.getByRole("button", { name: /Next: deploy/ }));
    await user.click(screen.getByRole("checkbox", { name: /Approve the plan in the same click/ }));
    await user.click(screen.getByRole("button", { name: "Deploy and approve" }));
    expect(await screen.findByText("The change page")).toBeInTheDocument();
    const deployed = requests.find((r) => r.method === "POST" && r.path === "/api/deploy")!.body as Record<string, unknown>;
    // Bound to what was reviewed: the release and the files.
    expect(deployed).toMatchObject({ approveAtDeploy: true, name: "w2", vmid: 311, reviewedRelease: `octopushy-worker@1.1 at ${base}`,
      reviewDigest: "d".repeat(64) });
  });

  it("deploys without approval when the box is left unticked", async () => {
    const user = userEvent.setup();
    const { requests } = api();
    renderWizard();
    await toReview(user);
    await user.click(await screen.findByRole("button", { name: /Next: deploy/ }));
    await user.click(screen.getByRole("button", { name: "Deploy" }));
    expect(await screen.findByText("The change page")).toBeInTheDocument();
    expect(requests.find((r) => r.method === "POST" && r.path === "/api/deploy")!.body).toMatchObject({ approveAtDeploy: false });
  });

  it("offers the next free VMID when the reviewed one was taken at deploy", async () => {
    const user = userEvent.setup();
    const { requests } = api({
      "POST /api/deploy": () =>
        new Response(JSON.stringify({ changeId: "c", reason: "vmid-taken", message: "VMID 311 is not free on home/pve01: declared by home/pve01/other. The next free VMID is 312; review the deploy again with it.",
          issues: [], fieldIssues: [{ pointer: "/vmid", message: "Taken." }], nextVmid: 312 }), { status: 409, headers: { "content-type": "application/json" } }),
    });
    renderWizard();
    await toReview(user);
    await user.click(await screen.findByRole("button", { name: /Next: deploy/ }));
    await user.click(screen.getByRole("button", { name: "Deploy" }));
    expect(await screen.findByText(/VMID 311 is not free on home\/pve01/)).toBeInTheDocument();
    await user.click(screen.getByRole("button", { name: /Use VMID 312 and review again/ }));
    await waitFor(() =>
      expect(requests.filter((r) => r.path === "/api/deploy/preview").at(-1)!.body).toMatchObject({ vmid: 312 }));
    expect(screen.queryByText("The change page")).not.toBeInTheDocument();
  });

  it("offers no VMID without a usable observation, and asks for one", async () => {
    const user = userEvent.setup();
    const problem = "home/pve01 has not been observed successfully since 2026-11-04 08:00 UTC, more than 15 minutes ago. Refresh its observation first.";
    const { requests } = api({ "GET /api/deploy/vmid": () => ({ installation: "home/pve01", vmid: null, from: 100, problem }),
      "POST /api/installations/inst-pve01/observe": () => undefined });
    renderWizard();
    await user.click(await screen.findByRole("radio", { name: /octopushy-worker/ }));
    await user.click(screen.getByRole("button", { name: /Next: where/ }));
    await user.click(screen.getByRole("radio", { name: /home\/pve01/ }));
    expect(await screen.findByText(problem, { exact: false })).toBeInTheDocument();
    expect(screen.getByLabelText("VMID")).toHaveValue("");
    await user.type(screen.getByLabelText("Name"), "w2");
    expect(screen.getByRole("button", { name: /Next: form/ })).toBeDisabled();
    await user.click(screen.getByRole("button", { name: "Observe now" }));
    await waitFor(() => expect(requests.some((r) => r.method === "POST" && r.path === "/api/installations/inst-pve01/observe")).toBe(true));
  });

  it("does not let an invalid deploy past its review", async () => {
    const user = userEvent.setup();
    api({ "POST /api/deploy/preview": () => preview({ valid: false, issues: [{ severity: "error", file, line: 4, message: "memory must be at least 256", address: null }],
      fieldIssues: [{ pointer: "/hardware/memory", message: "must be at least 256" }] }) });
    renderWizard();
    await toReview(user);
    expect(await screen.findByText("The deploy does not validate")).toBeInTheDocument();
    expect(screen.getByRole("button", { name: /Next: deploy/ })).toBeDisabled();
    await user.click(screen.getByRole("button", { name: /Back to the form/ }));
    expect(await screen.findByText("must be at least 256")).toBeInTheDocument();
  });
});
