import { screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, describe, expect, it, vi } from "vitest";
import type { RunDetail } from "../api/types";
import { created, runDetail, sha } from "../test/runs";
import { mockApi, problem, renderAt } from "../test/utils";
import { RunPage } from "./RunPage";


it("retries a failed stage of a pipeline and names its stage", async () => {
  const detail = runDetail({ operation: "pipeline", status: "failed", currentStep: null });
  detail.steps = [{ ...detail.steps[0], name: "playbook", stage: "deploy", kind: "agent", status: "failed", plan: null, approval: null }];
  const { requests } = mockApi({
    "GET /api/runs/run-1": () => detail,
    "POST /api/runs/run-1/retry-stage": () => undefined,
  });
  renderAt(<RunPage />, { path: "/runs/:id", route: "/runs/run-1" });
  await userEvent.click(await screen.findByRole("button", { name: "Retry this stage" }));
  await waitFor(() => expect(requests.some((r) => r.method === "POST")).toBe(true));
  expect(requests.filter((r) => r.method === "POST").map((r) => r.path)).toEqual(["/api/runs/run-1/retry-stage"]);
  expect(screen.getByText("deploy")).toBeInTheDocument();
});

it("force stops a change in progress that cannot be cancelled", async () => {
  const detail = runDetail({ status: "running" });
  detail.canCancel = false;
  detail.canForceStop = true;
  const { requests } = mockApi({
    "GET /api/runs/run-1": () => detail,
    "POST /api/runs/run-1/force-stop": () => undefined,
  });
  renderAt(<RunPage />, { path: "/runs/:id", route: "/runs/run-1" });
  expect(screen.queryByRole("button", { name: /Cancel run/ })).not.toBeInTheDocument();
  await userEvent.click(await screen.findByRole("button", { name: "Force stop" }));
  expect(await screen.findByText(/Older agents keep the tool running/)).toBeInTheDocument();
  await userEvent.click(await screen.findByRole("button", { name: "Force stop the run" }));
  await waitFor(() => expect(requests.some((r) => r.method === "POST")).toBe(true));
  expect(requests.filter((r) => r.method === "POST").map((r) => r.path)).toEqual(["/api/runs/run-1/force-stop"]);
});

it("runs a failed run again as a new run and opens it", async () => {
  const detail = runDetail({ status: "failed", currentStep: null });
  detail.canRunAgain = true;
  const { requests } = mockApi({
    "GET /api/runs/run-1": () => detail,
    "POST /api/runs/run-1/run-again": () => ({ runId: "run-2" }),
    "GET /api/runs/run-2": () => runDetail({ id: "run-2", number: 13, status: "queued" }),
  });
  renderAt(<RunPage />, { path: "/runs/:id", route: "/runs/run-1" });
  await userEvent.click(await screen.findByRole("button", { name: "Run again" }));
  await userEvent.click(await screen.findByRole("button", { name: "Start a new run" }));
  expect(await screen.findByText(/Run #13/)).toBeInTheDocument();
  expect(requests.filter((r) => r.method === "POST").map((r) => r.path)).toEqual(["/api/runs/run-1/run-again"]);
});

it("offers no run again when the server says a run cannot be", async () => {
  mockApi({ "GET /api/runs/run-1": () => runDetail({ status: "failed", currentStep: null }) });
  renderAt(<RunPage />, { path: "/runs/:id", route: "/runs/run-1" });
  await screen.findByText(/Run #12/);
  expect(screen.queryByRole("button", { name: "Run again" })).toBeNull();
});

it("says when a step's network access is not modeled, and never calls it reachable", async () => {
  const detail = runDetail({ operation: "pipeline", status: "running", currentStep: null });
  detail.steps = [{ ...detail.steps[0], name: "apply", stage: "infra", kind: "agent", status: "running", plan: null, approval: null,
    networkAccess: "Network access not modeled: this step goes to any agent, and its reachability is not checked." }];
  mockApi({ "GET /api/runs/run-1": () => detail });
  renderAt(<RunPage />, { path: "/runs/:id", route: "/runs/run-1" });
  expect(await screen.findByText(/Network access not modeled/)).toBeInTheDocument();
  expect(screen.queryByText(/reachable/i)).not.toBeInTheDocument();
});

it("names the workflow a run executed, its version and the commit its files came from", async () => {
  const detail = runDetail({});
  detail.workflow = {
    id: "fedora-vm",
    version: "2.3",
    reference: "fedora-vm@2",
    commit: "c0ffee1234c0ffee1234c0ffee1234c0ffee1234",
    repository: "git@github.com:jcarcaboso/octopushy-lab.git",
  };
  mockApi({ "GET /api/runs/run-1": () => detail });
  renderAt(<RunPage />, { path: "/runs/:id", route: "/runs/run-1" });
  expect(await screen.findByText("fedora-vm@2.3")).toBeInTheDocument();
  await userEvent.click(screen.getByRole("button", { name: "All details" }));
  expect(screen.getByText("c0ffee1234")).toBeInTheDocument();
  expect(screen.getByText("git@github.com:jcarcaboso/octopushy-lab.git")).toBeInTheDocument();
});

it("offers no stage retry once a change may have been lost", async () => {
  const detail = runDetail({ operation: "pipeline", status: "failed", currentStep: null });
  const lost = { ...detail.steps[0].attempts[0], id: "attempt-lost", status: "uncertain", mutating: true };
  detail.steps = [{ ...detail.steps[0], name: "apply", stage: "expose", kind: "agent", status: "failed", plan: null, approval: null, attempts: [lost] }];
  mockApi({ "GET /api/runs/run-1": () => detail });
  renderAt(<RunPage />, { path: "/runs/:id", route: "/runs/run-1" });
  await screen.findByText("expose");
  expect(screen.queryByRole("button", { name: "Retry this stage" })).toBeNull();
});

it("names one document for the whole pipeline when asking for approval", async () => {
  const detail = runDetail({ operation: "pipeline", status: "awaiting-approval", currentStep: "approval" });
  detail.steps[0] = { ...detail.steps[0], stage: "expose" };
  mockApi({ "GET /api/runs/run-1": () => detail });
  renderAt(<RunPage />, { path: "/runs/:id", route: "/runs/run-1" });
  expect(await screen.findByText(/Approving covers every stage/)).toBeInTheDocument();
  expect(screen.getByText("Pipeline document SHA-256")).toBeInTheDocument();
});

it("shows what a configure run will do, and approves that preview", async () => {
  const detail = runDetail({ operation: "configure", status: "awaiting-approval", currentStep: "approval" });
  detail.steps[0] = {
    ...detail.steps[0],
    name: "preview",
    plan: null,
    configurePreview: {
      action: "update-tools",
      playbook: "playbooks/update-tools.yml",
      commit: "e6b1282aa4d1c0ffee",
      image: "docker.io/skorcius/octopushy:ansible-2.21.5-abc",
      host: "ops@10.15.60.10:22",
      toolVersion: "ansible-playbook [core 2.21.5]",
      destructive: true,
      tasks: ["play #1 (all): Configure", "  Install tools"],
      changedFiles: ["M modules/ansible/ai-server/site.yml"],
      vars: { hostname: "pve01-ai" },
      secrets: [{ name: "ansible.ssh-key", reference: "access/pve01-ai/ssh-key v2" }],
      prepared: { tools: ["t3 1.2.3"] },
      hostKeyFingerprints: ["ssh-ed25519 SHA256:abcdef"],
      previewSha256: sha,
    },
  };
  const { requests } = mockApi({
    "GET /api/runs/run-1": () => detail,
    "POST /api/runs/run-1/steps/step-approval/approve": () => undefined,
  });
  renderAt(<RunPage />, { path: "/runs/:id", route: "/runs/run-1" });
  await userEvent.click(await screen.findByRole("button", { name: /Preview/ }));
  // The page names the host once more, above the tasks the playbook runs.
  expect(within(screen.getByRole("region", { name: "Preview" })).getByText("ops@10.15.60.10:22")).toBeTruthy();
  expect(screen.getByText("ssh-ed25519 SHA256:abcdef")).toBeTruthy();
  expect(screen.getByText("access/pve01-ai/ssh-key v2")).toBeTruthy();
  expect(screen.getByText("M modules/ansible/ai-server/site.yml")).toBeTruthy();
  expect(screen.getByText("Declared by the workflow")).toBeTruthy();
  expect(screen.getByText(/It shows what will run, not what will change/)).toBeTruthy();
  expect(screen.getByText(/Octopushy cannot show what it will change/)).toBeTruthy();
  expect(screen.getByText("Preview SHA-256")).toBeTruthy();
  await userEvent.click(screen.getByRole("button", { name: "Approve" }));
  await waitFor(() => expect(requests.some((r) => r.method === "POST")).toBe(true));
  expect(requests.find((r) => r.method === "POST")?.path).toBe("/api/runs/run-1/steps/step-approval/approve");
});

it("says what a playbook run changes as things, and lists the tasks it runs", async () => {
  const release = (version: string) => ({
    url: `https://example.org/t3code/releases/download/v${version}/t3-${version}-linux-x64.tar.gz`,
    sha256: (version.replace(/\D/g, "") + "0123456789abcdef".repeat(4)).slice(0, 64),
  });
  const codex = { url: "https://example.org/codex/releases/download/v0.167.0/codex-0.167.0.tar.gz", sha256: "c".repeat(64) };
  const detail = runDetail({ operation: "configure", status: "succeeded", currentStep: null });
  detail.steps[0] = {
    ...detail.steps[0],
    name: "preview",
    plan: null,
    configurePreview: {
      action: "update-tools",
      playbook: "update-tools.yml",
      commit: "e6b1282aa4d1c0ffee",
      image: "docker.io/skorcius/octopushy:ansible-2.21.5-abc",
      host: "ops@10.15.60.12:22",
      toolVersion: "ansible-playbook [core 2.21.5]",
      destructive: false,
      tasks: ["playbook: update-tools.yml", "  play #1 (all): Update the tools\tTAGS: []", "    tasks:", "      Install the tools\tTAGS: []"],
      changedFiles: [],
      vars: {},
      secrets: [],
      prepared: { machine_resolved_t3: release("0.1.12-nightly.5178"), machine_resolved_codex: codex },
      previousPrepared: { machine_resolved_t3: release("0.1.12-nightly.5177"), machine_resolved_codex: codex },
      hostKeyFingerprints: [],
      previewSha256: sha,
    },
  };
  mockApi({ "GET /api/runs/run-1": () => detail });
  renderAt(<RunPage />, { path: "/runs/:id", route: "/runs/run-1" });

  // The action and its playbook, not just "Configure"; the summary leads with the release that moved.
  expect(await screen.findByText("Update tools")).toBeInTheDocument();
  const summary = within(screen.getByRole("region", { name: "Summary" }));
  expect(summary.getByText("v0.1.12-nightly.5177")).toBeInTheDocument();
  expect(summary.getByText("t3")).toBeInTheDocument();

  // One line per thing, named without the prefix the keys share; the unchanged one waits behind a toggle.
  const changed = within(screen.getByRole("region", { name: "What changed" }));
  expect(changed.getByText("t3")).toBeInTheDocument();
  expect(changed.queryByText("codex")).toBeNull();
  expect(changed.queryByText(release("0.1.12-nightly.5178").url)).toBeNull();
  await userEvent.click(changed.getByRole("button", { name: "url, sha256" }));
  expect(changed.getByTitle(release("0.1.12-nightly.5177").url)).toBeInTheDocument();
  expect(changed.getByTitle(release("0.1.12-nightly.5178").sha256)).toBeInTheDocument();
  await userEvent.click(changed.getByRole("button", { name: "+ 1 unchanged" }));
  expect(changed.getByText("codex")).toBeInTheDocument();

  const ran = within(screen.getByRole("region", { name: "What it ran" }));
  expect(ran.getByText("ops@10.15.60.12:22")).toBeInTheDocument();
  expect(ran.getAllByRole("listitem").map((item) => item.textContent)).toEqual(["Update the tools", "Install the tools"]);
});

it("says so when a playbook run prepared the same values as the last one", async () => {
  const detail = runDetail({ operation: "configure", status: "succeeded", currentStep: null });
  detail.steps[0] = {
    ...detail.steps[0],
    name: "preview",
    plan: null,
    configurePreview: {
      action: "update-tools", playbook: "update-tools.yml", commit: null, image: null, host: null, toolVersion: null, destructive: false,
      tasks: [], changedFiles: [], vars: {}, secrets: [], prepared: { release: "2.1.0" }, previousPrepared: { release: "2.1.0" },
      hostKeyFingerprints: [], previewSha256: sha,
    },
  };
  mockApi({ "GET /api/runs/run-1": () => detail });
  renderAt(<RunPage />, { path: "/runs/:id", route: "/runs/run-1" });
  const changed = within(await screen.findByRole("region", { name: "What changed" }));
  expect(changed.getByText("Nothing new: the same values as the last successful run.")).toBeInTheDocument();
  expect(changed.getByRole("button", { name: "+ 1 unchanged" })).toBeInTheDocument();
});

it("lists what an approval will change, one line per kind, opening in place", async () => {
  const detail = runDetail({ operation: "provision" });
  detail.steps[0].plan!.changes[0] = {
    ...detail.steps[0].plan!.changes[0],
    actions: ["update"],
    attributes: [
      { name: "disk.scsi0.size", before: 32, after: 64, unknown: false, sensitive: false },
      { name: "memory.dedicated", before: 4096, after: 8192, unknown: false, sensitive: false },
    ],
  };
  mockApi({ "GET /api/runs/run-1": () => detail });
  renderAt(<RunPage />, { path: "/runs/:id", route: "/runs/run-1" });
  const section = within(await screen.findByRole("region", { name: "What will change" }));
  const disk = section.getByRole("button", { name: /Disk/ });
  const memory = section.getByRole("button", { name: /Memory/ });
  const resources = section.getByRole("button", { name: /Resources/ });
  expect(screen.getByText("What this run will apply, not read from the guest.")).toBeInTheDocument();

  // The first line is open, the others are not.
  expect(disk).toHaveAttribute("aria-expanded", "true");
  expect(memory).toHaveAttribute("aria-expanded", "false");
  const panel = section.getByRole("region", { name: "Disk" });
  expect(within(panel).getByText("scsi0 size")).toBeInTheDocument();
  expect(within(panel).getByText("32 GiB")).toBeInTheDocument();
  expect(section.queryByRole("region", { name: "Memory" })).toBeNull();

  await userEvent.click(resources);
  expect(resources).toHaveAttribute("aria-expanded", "true");
  expect(section.getByRole("region", { name: "Resources" })).toHaveTextContent("terraform_data.marker");
  expect(disk).toHaveAttribute("aria-expanded", "true");

  await userEvent.click(disk);
  expect(disk).toHaveAttribute("aria-expanded", "false");
  expect(section.queryByRole("region", { name: "Disk" })).toBeNull();

  // With no image data, the headline is the most telling before and after of the plan.
  const summary = within(screen.getByRole("region", { name: "Summary" }));
  expect(summary.getByText("Disk · scsi0 · grow")).toBeInTheDocument();
  expect(screen.queryByRole("button", { name: /Versions/ })).toBeNull();
});

describe("RunPage", () => {
  afterEach(() => vi.unstubAllGlobals());

  it("shows the plan's changes and approves with the plan hash the step shows", async () => {
    const user = userEvent.setup();
    const { requests } = mockApi({
      "GET /api/runs/run-1": () => runDetail(),
      "POST /api/runs/run-1/steps/step-approval/approve": () => undefined,
    });

    renderAt(<RunPage />, { path: "/runs/:id", route: "/runs/run-1" });

    expect(await screen.findByRole("heading", { level: 1, name: /krb-app-01/ })).toBeInTheDocument();
    expect(screen.getByRole("navigation", { name: "Breadcrumb" })).toHaveTextContent("Runs / Run #12");
    await waitFor(() => expect(document.title).toBe("Run #12 · Octopushy"));
    expect(within(screen.getByRole("region", { name: "Resources" })).getByText("proxmox_virtual_environment_vm.this")).toBeInTheDocument();
    await user.click(screen.getByRole("button", { name: /Plan/ }));
    const plan = within(screen.getByRole("region", { name: "Plan" }));
    expect(plan.getByText("proxmox_virtual_environment_vm.this")).toBeInTheDocument();
    expect(plan.getAllByText("Replace").length).toBeGreaterThan(0);
    expect(plan.getByText("OpenTofu 1.10.6")).toBeInTheDocument();
    for (const link of screen.getAllByRole("link", { name: "home/proxmox/korriban/guests/krb-app-01" })) {
      expect(link).toHaveAttribute("href", "/guests/guest-1");
    }
    expect(screen.getByText("Your approval is needed")).toBeInTheDocument();
    expect(screen.getAllByTitle(sha).length).toBeGreaterThan(0);

    await user.type(screen.getByLabelText(/Note/), "Looks right");
    await user.click(screen.getByRole("button", { name: "Approve" }));

    await waitFor(() => expect(requests.some((r) => r.method === "POST")).toBe(true));
    const approve = requests.find((r) => r.method === "POST")!;
    expect(approve.path).toBe("/api/runs/run-1/steps/step-approval/approve");
    expect(approve.body).toEqual({ planSha256: sha, note: "Looks right" });
  });

  it("shows the problem title when approval is refused", async () => {
    const user = userEvent.setup();
    mockApi({
      "GET /api/runs/run-1": () => runDetail(),
      "POST /api/runs/run-1/steps/step-approval/approve": () =>
        problem(409, "The plan changed since it was shown; review the current plan."),
    });

    renderAt(<RunPage />, { path: "/runs/:id", route: "/runs/run-1" });
    await user.click(await screen.findByRole("button", { name: "Approve" }));
    expect(await screen.findByText("The plan changed since it was shown; review the current plan.")).toBeInTheDocument();
  });

  it("rejects without a plan hash", async () => {
    const user = userEvent.setup();
    const { requests } = mockApi({
      "GET /api/runs/run-1": () => runDetail(),
      "POST /api/runs/run-1/steps/step-approval/reject": () => undefined,
    });

    renderAt(<RunPage />, { path: "/runs/:id", route: "/runs/run-1" });
    await user.click(await screen.findByRole("button", { name: "Reject" }));
    await waitFor(() => expect(requests.some((r) => r.method === "POST")).toBe(true));
    expect(requests.find((r) => r.method === "POST")!.body).toEqual({ planSha256: null, note: null });
  });

  it("shows why a running run is waiting", async () => {
    mockApi({
      "GET /api/runs/run-1": () =>
        runDetail({ status: "running", detail: null, currentStep: "plan", waiting: "Waiting for a connected agent with the labels home." }),
    });

    renderAt(<RunPage />, { path: "/runs/:id", route: "/runs/run-1" });
    expect(await screen.findByText(/Waiting for a connected agent with the labels home/)).toBeInTheDocument();
  });

  it("says which store value started a run", async () => {
    mockApi({
      "GET /api/runs/run-1": () =>
        runDetail({
          operation: "configure",
          reason: "secret",
          reasonDetail: "A store value this guest's setup uses changed: shared/git-ssh-key v2 to v3.",
        }),
    });

    renderAt(<RunPage />, { path: "/runs/:id", route: "/runs/run-1" });
    expect(await screen.findByText("Store value changed")).toBeInTheDocument();
    await userEvent.click(screen.getByRole("button", { name: "All details" }));
    expect(screen.getByText(/shared\/git-ssh-key v2 to v3/)).toBeInTheDocument();
  });

  it("explains an uncertain outcome and offers to close the run", async () => {
    const detail = runDetail({ status: "uncertain", detail: "The agent stopped reporting." });
    detail.canCancel = false;
    detail.canClose = true;
    mockApi({ "GET /api/runs/run-1": () => detail });

    renderAt(<RunPage />, { path: "/runs/:id", route: "/runs/run-1" });
    expect(await screen.findByText("The outcome is uncertain")).toBeInTheDocument();
    expect(screen.getAllByText(/records the run as failed/).length).toBeGreaterThan(0);
    expect(screen.getAllByRole("button", { name: /Close/ }).length).toBeGreaterThan(0);
    expect(screen.queryByRole("button", { name: /Cancel run/ })).not.toBeInTheDocument();
  });
});


/** A finished pipeline of a service: preview, approval, playbook. */
function serviceRun(status: string, images: RunDetail["images"]) {
  const detail = runDetail({
    operation: "pipeline", status, currentStep: null, resourceKind: "service", resourceId: "svc-fleet",
    address: "home/pve01/dev/fleet", detail: null, finishedAt: "2026-10-20T08:05:00Z",
  });
  const [plan, approval] = detail.steps;
  detail.steps = [
    { ...plan, id: "step-preview", name: "preview", stage: "deploy", plan: null },
    { ...approval, status: "succeeded", stage: null, approval: { ...approval.approval!, decision: "approved", decidedBy: "ops", decidedAt: created } },
    {
      ...plan, id: "step-playbook", ordinal: 2, name: "playbook", stage: "deploy", plan: null, status,
      detail: status === "failed" ? "The playbook failed at Wait for the container." : null,
      attempts: [{ ...plan.attempts[0], id: "attempt-play", executor: "ansible", action: "run", status, mutating: true,
        error: status === "failed" ? "fatal: [web]: FAILED! container exited with code 1" : null }],
    },
  ];
  detail.images = images;
  detail.baselineCommit = "a41c09e1234567890";
  detail.canCancel = false;
  return detail;
}

const fleetImages = [
  { service: "server", before: "docker.io/skorcius/fleet-manager:0.12.4", after: "docker.io/skorcius/fleet-manager:0.13.0" },
  { service: "dashboard", before: "docker.io/skorcius/fleet-dashboard:dashboard-0.12.4", after: "docker.io/skorcius/fleet-dashboard:dashboard-0.13.0" },
  { service: "db", before: "docker.io/library/postgres:17.2-alpine", after: "docker.io/library/postgres:17.2-alpine" },
];

describe("RunPage for a service", () => {
  afterEach(() => vi.unstubAllGlobals());

  it("leads with the main image's version bump and lists every image under Versions", async () => {
    mockApi({ "GET /api/runs/run-1": () => serviceRun("succeeded", fleetImages) });
    renderAt(<RunPage />, { path: "/runs/:id", route: "/runs/run-1" });

    const summary = within(await screen.findByRole("region", { name: "Summary" }));
    expect(summary.getByText("Deployed")).toBeInTheDocument();
    expect(summary.getByText("0.12.4")).toBeInTheDocument();
    expect(summary.getByText("Minor")).toBeInTheDocument();
    expect(summary.getByText("fleet-manager")).toBeInTheDocument();
    expect(summary.getByText(/and 1 other image/)).toBeInTheDocument();
    expect(summary.getByText(/approved by ops/)).toBeInTheDocument();

    const versions = screen.getByRole("button", { name: /Versions/ });
    expect(versions).toHaveAttribute("aria-expanded", "true");
    expect(versions).toHaveTextContent("2 bumped · 1 unchanged");
    const panel = within(screen.getByRole("region", { name: "Versions" }));
    expect(panel.getByText("dashboard")).toBeInTheDocument();
    expect(panel.getByText("docker.io/skorcius/fleet-dashboard")).toBeInTheDocument();
    expect(panel.getAllByText("Minor")).toHaveLength(2);
    // Only the part of the tag that changed is bold.
    expect(panel.getAllByText("13").every((el) => el.tagName === "B")).toBe(true);
    expect(panel.queryByText("db")).toBeNull();

    await userEvent.click(panel.getByRole("button", { name: "+ 1 unchanged" }));
    expect(panel.getByText("db")).toBeInTheDocument();
    expect(panel.getByText("a41c09e")).toBeInTheDocument();
  });

  it("names a new image and keeps quiet when no image changed", async () => {
    const images = [{ service: "db", before: "postgres:17", after: "postgres:17" }];
    mockApi({ "GET /api/runs/run-1": () => serviceRun("succeeded", images) });
    renderAt(<RunPage />, { path: "/runs/:id", route: "/runs/run-1" });
    expect(await screen.findByRole("button", { name: /Versions/ })).toHaveTextContent("1 unchanged");
    expect(within(screen.getByRole("region", { name: "Summary" })).queryByText(/→/)).toBeNull();
  });

  it("opens a failed step with its log, and names it with its error at the top", async () => {
    mockApi({
      "GET /api/runs/run-1": () => serviceRun("failed", fleetImages),
      "GET /api/attempts/attempt-play/log": () => ({
        lines: [{ sequence: 1, unixMs: Date.parse(created), stream: "stderr", text: "jellyfin exited with code 1" }],
        next: 1,
        finished: true,
      }),
    });
    renderAt(<RunPage />, { path: "/runs/:id", route: "/runs/run-1" });

    const summary = within(await screen.findByRole("region", { name: "Summary" }));
    expect(summary.getByText("Failed")).toBeInTheDocument();
    expect(summary.getByText("deploy · Run the playbook")).toBeInTheDocument();
    expect(summary.getByText("fatal: [web]: FAILED! container exited with code 1")).toBeInTheDocument();

    expect(screen.getByRole("button", { name: /Run the playbook/ })).toHaveAttribute("aria-expanded", "true");
    const step = within(screen.getByRole("region", { name: "deploy · Run the playbook" }));
    expect(await step.findByText("jellyfin exited with code 1")).toBeInTheDocument();
    expect(step.getByRole("log", { name: "Attempt log" })).toBeInTheDocument();
    // A step that succeeded stays closed.
    expect(screen.getByRole("button", { name: /Preview/ })).toHaveAttribute("aria-expanded", "false");
  });

  it("opens a step that succeeded on click, and keeps a closed step closed when the run refreshes", async () => {
    const detail = serviceRun("running", null);
    const { fetchMock } = mockApi({ "GET /api/runs/run-1": () => detail, "GET /api/services/svc-fleet": () => problem(404, "Gone") });
    renderAt(<RunPage />, { path: "/runs/:id", route: "/runs/run-1" });

    const preview = await screen.findByRole("button", { name: /Preview/ });
    expect(preview).toHaveAttribute("aria-expanded", "false");
    expect(screen.queryByRole("region", { name: "deploy · Preview" })).toBeNull();
    await userEvent.click(preview);
    expect(preview).toHaveAttribute("aria-expanded", "true");
    expect(screen.getByRole("region", { name: "deploy · Preview" })).toHaveTextContent("Attempt 1");

    const playbook = screen.getByRole("button", { name: /Run the playbook/ });
    expect(playbook).toHaveAttribute("aria-expanded", "true");
    await userEvent.click(playbook);
    expect(playbook).toHaveAttribute("aria-expanded", "false");

    const calls = fetchMock.mock.calls.length;
    await waitFor(() => expect(fetchMock.mock.calls.length).toBeGreaterThan(calls), { timeout: 4000 });
    expect(playbook).toHaveAttribute("aria-expanded", "false");
    expect(preview).toHaveAttribute("aria-expanded", "true");
    expect(screen.getByText("How it is going")).toBeInTheDocument();
  }, 8000);

  it("closes a running step nobody touched once it is done", async () => {
    let status = "running";
    const { fetchMock } = mockApi({ "GET /api/runs/run-1": () => serviceRun(status, null), "GET /api/services/svc-fleet": () => problem(404, "Gone") });
    renderAt(<RunPage />, { path: "/runs/:id", route: "/runs/run-1" });

    const playbook = await screen.findByRole("button", { name: /Run the playbook/ });
    expect(playbook).toHaveAttribute("aria-expanded", "true");
    status = "succeeded";
    const calls = fetchMock.mock.calls.length;
    await waitFor(() => expect(fetchMock.mock.calls.length).toBeGreaterThan(calls), { timeout: 4000 });
    await waitFor(() => expect(playbook).toHaveAttribute("aria-expanded", "false"));
  }, 8000);

  it("keeps a step that awaits approval closed, with the approval in the summary", async () => {
    const detail = runDetail({ operation: "pipeline", status: "awaiting-approval", currentStep: "approval" });
    mockApi({ "GET /api/runs/run-1": () => detail });
    renderAt(<RunPage />, { path: "/runs/:id", route: "/runs/run-1" });

    const summary = within(await screen.findByRole("region", { name: "Summary" }));
    expect(summary.getByRole("button", { name: "Approve" })).toBeInTheDocument();
    expect(screen.getByRole("button", { name: /Approval.*waits for you/ })).toHaveAttribute("aria-expanded", "false");
  });

  it("shows the run's facts, policy and agent labels under All details", async () => {
    mockApi({ "GET /api/runs/run-1": () => serviceRun("succeeded", fleetImages) });
    renderAt(<RunPage />, { path: "/runs/:id", route: "/runs/run-1" });
    const toggle = await screen.findByRole("button", { name: "All details" });
    expect(toggle).toHaveAttribute("aria-expanded", "false");
    expect(screen.queryByRole("region", { name: "All details" })).toBeNull();

    await userEvent.click(toggle);
    const details = within(screen.getByRole("region", { name: "All details" }));
    expect(details.getByText("Compared with")).toBeInTheDocument();
    expect(details.getByText("Destructive")).toBeInTheDocument();
    expect(details.getByText("site-a")).toBeInTheDocument();
    expect(details.getByText("set by home/lab.yaml")).toBeInTheDocument();

    await userEvent.click(screen.getByRole("button", { name: "Hide details" }));
    expect(screen.queryByRole("region", { name: "All details" })).toBeNull();
  });
});
