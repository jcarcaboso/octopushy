// The runs the mock serves: one per scenario in README.md, with their steps, attempts and logs, and the state changes
// that approving, rejecting and cancelling make. Times are relative to when the server started.
import { createHash } from "node:crypto";
import type { Attempt, ConfigurePreview, LogLine, Plan, RunDetail, RunSummary, Step } from "../src/api/types.ts";

export const started = Date.now();
export const iso = (ms: number) => new Date(ms).toISOString();
/** A time `minutes` before the server started, plus `seconds`. */
export const ago = (minutes: number, seconds = 0) => iso(started - minutes * 60_000 + seconds * 1000);
export const hex = (seed: string, length = 64) => createHash("sha256").update(seed).digest("hex").slice(0, length);

export const headCommit = hex("revision-0", 40);
export const labRepository = "https://git.example.org/homelab/octopushy-lab.git";
export const workflowsRepository = "https://git.example.org/homelab/octopushy-workflows.git";

export interface Resource {
  id: string;
  kind: "guest" | "service";
  address: string;
  /** Where Ansible logs in, for configure runs. */
  host?: string;
}

const terminal = new Set(["succeeded", "failed", "cancelled"]);
const doneSteps = new Set(["succeeded", "skipped", "cancelled"]);

// Logs

type Script = [stream: "stdout" | "stderr" | "agent", text: string][];

/** Every attempt's log, and when a running one started showing it. */
export const logs = new Map<string, LogLine[]>();
export const liveSince = new Map<string, number>();

const agents = { a: { id: "agent-pve01-01", name: "pve01-agent-01" }, b: { id: "agent-pve01-02", name: "pve01-agent-02" } };

function tofuPlan(changes: string[], summary: string): Script {
  return [
    ["agent", "Workspace ready: modules/opentofu/proxmox-vm at " + headCommit.slice(0, 10)],
    ["agent", "Image docker.io/skorcius/octopushy:opentofu-1.10.6 is present"],
    ["stdout", "Initializing the backend..."],
    ["stdout", "Initializing provider plugins..."],
    ["stdout", "- Reusing previous version of bpg/proxmox from the dependency lock file"],
    ["stdout", "- Using previously-installed bpg/proxmox v0.84.1"],
    ["stdout", "OpenTofu has been successfully initialized!"],
    ["stdout", "data.proxmox_virtual_environment_node.this: Reading..."],
    ["stdout", "data.proxmox_virtual_environment_node.this: Read complete after 0s [id=pve01]"],
    ["stdout", "proxmox_virtual_environment_vm.this: Refreshing state..."],
    ["stdout", ""],
    ["stdout", "OpenTofu used the selected providers to generate the following execution plan."],
    ...changes.map((line): Script[number] => ["stdout", line]),
    ["stdout", ""],
    ["stdout", summary],
    ["stdout", "Saved the plan to: plan.tfplan"],
    ["agent", "Plan saved and uploaded (sha256 recorded for approval)"],
  ];
}

function tofuApply(resources: string[], failure?: string): Script {
  const lines: Script = [
    ["agent", "Workspace ready; applying the approved plan only"],
    ["stdout", "Initializing provider plugins..."],
    ["stdout", "- Using previously-installed bpg/proxmox v0.84.1"],
  ];
  for (const resource of resources) {
    lines.push(["stdout", `${resource}: Modifying...`]);
    lines.push(["stdout", `${resource}: Still modifying... [10s elapsed]`]);
    if (failure) break;
    lines.push(["stdout", `${resource}: Modifications complete after 14s`]);
  }
  if (failure) {
    lines.push(["stderr", "╷"], ["stderr", `│ Error: ${failure}`], ["stderr", "│"], ["stderr", `│   with ${resources[0]},`], ["stderr", "╵"]);
    lines.push(["agent", "tofu apply exited with code 1; state saved"]);
  } else {
    lines.push(["stdout", ""], ["stdout", `Apply complete! Resources: ${resources.length} changed.`], ["agent", "State saved"]);
  }
  return lines;
}

function ansiblePreview(playbook: string, tasks: string[]): Script {
  return [
    ["agent", "Image docker.io/skorcius/octopushy:ansible-2.21.5-7c1e9a2 is present"],
    ["agent", "Preview: no secrets, no contact with the guest"],
    ["stdout", `ansible-playbook [core 2.21.5]`],
    ["stdout", `playbook: ${playbook}`],
    ...tasks.map((task): Script[number] => ["stdout", task]),
    ["stdout", "PLAY [localhost] prepare.yml ****************************************************"],
    ["stdout", "TASK [Resolve channel versions] *************************************************"],
    ["stdout", "ok: [localhost]"],
    ["stdout", "TASK [Write prepared.json] ******************************************************"],
    ["stdout", "changed: [localhost]"],
    ["stdout", "PLAY RECAP *********************************************************************"],
    ["stdout", "localhost                  : ok=4    changed=1    unreachable=0    failed=0"],
    ["agent", "Preview recorded"],
  ];
}

function ansibleRun(host: string, tasks: string[], recap: string, extra: Script = []): Script {
  return [
    ["agent", `Connecting to ${host} with the pinned host key`],
    ["stdout", "PLAY [all] *********************************************************************"],
    ["stdout", "TASK [Gathering Facts] *********************************************************"],
    ["stdout", `ok: [${host.split("@")[1]?.split(":")[0] ?? host}]`],
    ...tasks.flatMap((task): Script => [
      ["stdout", `TASK [${task.trim()}] ${"*".repeat(Math.max(4, 70 - task.trim().length))}`],
      ["stdout", "changed: [guest]"],
    ]),
    ...extra,
    ["stdout", "PLAY RECAP *********************************************************************"],
    ["stdout", recap],
  ];
}

function verify(vmid: number, kind = "VM"): Script {
  return [
    ["agent", `Reading ${kind} ${vmid} through the Proxmox API (read-only token)`],
    ["stdout", `pve01/qemu/${vmid}: status running, config digest 6c1f0d2a`],
    ["stdout", "Hardware, network and tags match the applied plan"],
  ];
}

/** Builds an attempt and records its log, spread over its duration. */
function attempt(
  stepId: string,
  executor: string,
  action: string,
  status: string,
  start: string,
  seconds: number,
  script: Script,
  options: { error?: string; mutating?: boolean; agent?: keyof typeof agents } = {},
): Attempt {
  const id = `${stepId}-a1`;
  const agent = agents[options.agent ?? "a"];
  const begin = Date.parse(start);
  const queued = status === "queued";
  const lines = queued
    ? []
    : script.map(([stream, text], i) => ({
        sequence: i + 1,
        unixMs: begin + Math.round((seconds * 1000 * i) / Math.max(1, script.length - 1)),
        stream,
        text,
      }));
  logs.set(id, lines);
  if (status === "running") liveSince.set(id, Date.now());
  return {
    id,
    number: 1,
    executor,
    action,
    status,
    waitingReason: queued ? "Waiting for an agent with the labels home." : null,
    agentId: queued ? null : agent.id,
    agentName: queued ? null : agent.name,
    createdAt: iso(begin - 2000),
    assignedAt: queued ? null : iso(begin - 1000),
    startedAt: queued ? null : start,
    finishedAt: status === "running" || queued ? null : iso(begin + seconds * 1000),
    error: options.error ?? null,
    lateResult: false,
    logLines: lines.length,
    mutating: options.mutating ?? false,
  };
}

/** The executor and action a step's attempt runs, as the server's AssignmentBuilder names them. */
function actionFor(name: string): [executor: string, action: string, mutating: boolean] {
  switch (name) {
    case "plan":
      return ["opentofu", "preview", false];
    case "apply":
      return ["opentofu", "apply", true];
    case "unprotect":
    case "reprotect":
      return ["proxmox", "set-protection", true];
    case "verify":
      return ["proxmox", "verify-guest", false];
    case "preview":
      return ["ansible", "preview", false];
    default:
      return ["ansible", "run", true];
  }
}

function scriptFor(name: string, resource: Resource): Script {
  switch (name) {
    case "apply":
      return tofuApply(["proxmox_virtual_environment_vm.this"]);
    case "verify":
      return verify(112);
    case "playbook":
    case "playbook-approved":
      return ansibleRun(resource.host ?? "ops@guest:22", ["Install packages", "Write configuration"], "guest : ok=9 changed=2 unreachable=0 failed=0");
    default:
      return [["agent", `${name} finished`]];
  }
}

// Steps and runs

interface StepInput extends Partial<Step> {
  name: string;
  status: string;
}

function steps(runId: string, inputs: StepInput[]): Step[] {
  return inputs.map(({ name, status, ...rest }, ordinal) => ({
    id: `${runId}-s${ordinal}`,
    ordinal,
    name,
    kind: name === "approval" || name === "outcome-approval" ? "approval" : "agent",
    status,
    detail: null,
    startedAt: null,
    finishedAt: null,
    outputs: null,
    plan: null,
    approval: null,
    attempts: [],
    ...rest,
  }));
}

/** An agent step that ran from `start` for `seconds`, with one attempt. */
function ran(runId: string, ordinal: number, name: string, status: string, start: string, seconds: number, script: Script,
  rest: Partial<Step> & { error?: string; stage?: string } = {}): StepInput {
  const [executor, action, mutating] = actionFor(name);
  const { error, ...step } = rest;
  const running = status === "running";
  return {
    name,
    status,
    startedAt: start,
    finishedAt: running ? null : iso(Date.parse(start) + seconds * 1000),
    attempts: [attempt(`${runId}-s${ordinal}`, executor, action, status, start, seconds, script, { error, mutating })],
    ...step,
  };
}

const awaiting = (sha: string, start: string, detail: string, stage?: string): StepInput => ({
  name: "approval",
  status: "awaiting-approval",
  detail,
  startedAt: start,
  approval: { required: true, decision: null, decidedBy: null, decidedAt: null, note: null, planSha256: sha },
  stage: stage ?? null,
});

const decided = (sha: string, at: string, by: string, note: string | null, name = "approval"): StepInput => ({
  name,
  status: "succeeded",
  startedAt: at,
  finishedAt: at,
  approval: { required: by !== "policy", decision: "approved", decidedBy: by, decidedAt: at, note, planSha256: sha },
});

const pending = (name: string, stage?: string): StepInput => ({ name, status: "pending", stage: stage ?? null });

interface RunInput {
  id: string;
  number: number;
  operation: string;
  resource: Resource;
  reason: string;
  status: string;
  detail: string | null;
  minutesAgo: number;
  /** How long the run took, for a finished one. */
  seconds?: number;
  currentStep: string | null;
  policy: RunDetail["policy"];
  workflow: { id: string; version: string };
  steps: StepInput[];
}

function run(input: RunInput): RunDetail {
  const created = ago(input.minutesAgo);
  const done = terminal.has(input.status);
  const [major] = input.workflow.version.split(".");
  return {
    run: {
      id: input.id,
      number: input.number,
      operation: input.operation,
      resourceId: input.resource.id,
      resourceKind: input.resource.kind,
      address: input.resource.address,
      status: input.status,
      detail: input.detail,
      reason: input.reason,
      commit: input.number >= 46 ? headCommit : hex(`revision-${50 - input.number}`, 40),
      createdAt: created,
      startedAt: ago(input.minutesAgo, 2),
      finishedAt: done ? ago(input.minutesAgo, input.seconds ?? 90) : null,
      currentStep: done ? null : input.currentStep,
      waiting: null,
    },
    steps: steps(input.id, input.steps),
    agentLabels: ["home"],
    agentLabelsSetBy: "labs/home/lab.yaml",
    policy: input.policy,
    canCancel: false,
    canClose: false,
    canRunAgain: false,
    canForceStop: false,
    workflow: {
      id: input.workflow.id,
      version: input.workflow.version,
      reference: `${input.workflow.id}@${major}`,
      commit: hex(`${input.workflow.id}-${input.workflow.version}`, 40),
      repository: workflowsRepository,
    },
  };
}

const guestFile = (name: string) => `labs/home/proxmox/pve01/guests/${name}/guest.yaml`;
const manual = (name: string) => ({ approval: "manual", approvalSetBy: guestFile(name), destructive: "manual", destructiveSetBy: guestFile(name) });
const automatic = { approval: "automatic", approvalSetBy: "octopushy.yaml", destructive: "manual", destructiveSetBy: "octopushy.yaml" };

export const resources = {
  bb: { id: "bb", kind: "guest", address: "home/pve01/bb", host: "ops@10.15.60.12:22" },
  dev: { id: "dev", kind: "guest", address: "home/pve01/dev", host: "ops@10.15.60.13:22" },
  ai: { id: "pve01-ai", kind: "guest", address: "home/pve01/pve01-ai", host: "ops@10.15.60.14:22" },
  media: { id: "media", kind: "guest", address: "home/pve01/media", host: "ops@10.15.60.15:22" },
  pbs: { id: "pbs", kind: "guest", address: "home/pve01/pbs", host: "ops@10.15.60.16:22" },
  web: { id: "web", kind: "guest", address: "home/pve01/web", host: "ops@10.15.60.20:22" },
  dns: { id: "dns", kind: "guest", address: "home/pve01/dns", host: "ops@10.15.60.21:22" },
  app: { id: "web-app", kind: "service", address: "home/pve01/web/app", host: "ops@10.15.60.20:22" },
} satisfies Record<string, Resource>;

const vmChanges = [
  "  # proxmox_virtual_environment_vm.this will be updated in-place",
  '  ~ resource "proxmox_virtual_environment_vm" "this" {',
  "        id   = \"112\"",
  "      ~ tags = [",
  '          + "agents",',
  '            "homelab",',
  "        ]",
  "      ~ disk {",
  "          ~ size = 32 -> 64",
  "        }",
  "      ~ memory {",
  "          ~ dedicated = 4096 -> 8192",
  "        }",
  "      ~ network_device {",
  "          ~ bridge = (sensitive value)",
  "        }",
  "    }",
  "  # proxmox_virtual_environment_firewall_rules.this will be created",
  '  + resource "proxmox_virtual_environment_firewall_rules" "this" {',
  "      + rule { action = \"ACCEPT\", dport = \"9100\", proto = \"tcp\", type = \"in\" }",
  "    }",
];

const provisionPlan: Plan = {
  hasChanges: true,
  planSha256: hex("plan-bb"),
  toolVersion: "1.10.6",
  counts: { create: 1, update: 1, delete: 0, replace: 0 },
  changes: [
    {
      address: "proxmox_virtual_environment_vm.this",
      type: "proxmox_virtual_environment_vm",
      name: "this",
      actions: ["update"],
      attributes: [
        { name: "disk.scsi0.size", before: 32, after: 64, unknown: false, sensitive: false },
        { name: "memory.dedicated", before: 4096, after: 8192, unknown: false, sensitive: false },
        { name: "tags", before: ["homelab"], after: ["homelab", "agents"], unknown: false, sensitive: false },
        { name: "network_device.0.bridge", before: null, after: null, unknown: false, sensitive: true },
      ],
    },
    {
      address: "proxmox_virtual_environment_firewall_rules.this",
      type: "proxmox_virtual_environment_firewall_rules",
      name: "this",
      actions: ["create"],
      attributes: [],
    },
  ],
};

const aiTasks = [
  "play #1 (all): Update the AI tools",
  "  Read what was prepared",
  "  Install codex",
  "  Install claude-code",
  "  Install CLI tools",
  "  Remove retired tools",
  "  Write /etc/ai-server/release",
];

const aiPreview: ConfigurePreview = {
  action: "update-tools",
  playbook: "update-tools.yml",
  commit: headCommit,
  image: "docker.io/skorcius/octopushy:ansible-2.21.5-7c1e9a2",
  host: resources.ai.host,
  toolVersion: "ansible-playbook [core 2.21.5]",
  destructive: false,
  tasks: aiTasks,
  changedFiles: ["M modules/ansible/ai-server/update-tools.yml", "A modules/ansible/ai-server/tasks/fzf.yml"],
  vars: { hostname: "pve01-ai", ai_server_channel: "stable" },
  secrets: [{ name: "ai_server_token", reference: "proxmox/pve01/pve01-ai/ai_server_token v4" }],
  prepared: {
    release: "2.1.0",
    tools: { codex: "0.167.0", "claude-code": "2.1.52", fzf: "0.66.1", ripgrep: "15.1.0" },
  },
  previousPrepared: {
    release: "2.0.3",
    tools: { codex: "0.161.0", "claude-code": "2.1.40", neofetch: "7.1.0", ripgrep: "15.1.0" },
  },
  hostKeyFingerprints: ["ssh-ed25519 SHA256:q3Yk0pJ7m1i3XcR0vQ8w2Gm5mY1d0bqfLZ8nB2kR4sE"],
  previewSha256: hex("preview-ai"),
};

const toolsTasks = ["play #1 (all): Update the machine's tools", "  Check for active work", "  Install tools from the stable channel", "  Record the versions"];

const devPreview: ConfigurePreview = {
  ...aiPreview,
  host: resources.dev.host,
  tasks: toolsTasks,
  changedFiles: ["M modules/ansible/machine-tools/tasks/activity.yml"],
  vars: { hostname: "dev", machine_tools_channel: "stable" },
  secrets: [{ name: "machine_tools_token", reference: "proxmox/pve01/dev/machine_tools_token v2" }],
  prepared: { channel: "stable", tools: { node: "24.18.0", pnpm: "12.14.0", just: "1.44.0" } },
  previousPrepared: { channel: "stable", tools: { node: "24.17.1", pnpm: "12.14.0", just: "1.43.1" } },
  hostKeyFingerprints: ["ssh-ed25519 SHA256:Z1bq8vC0kE4mJ2s9dXyR7tL5wN3aH6pU0oG4iF8cV2M"],
  previewSha256: hex("preview-dev"),
};

const pbsPreview: ConfigurePreview = {
  ...devPreview,
  action: "setup",
  playbook: "configure.yml",
  host: resources.pbs.host,
  changedFiles: null,
  vars: { hostname: "pbs", machine_tools_channel: "stable" },
  secrets: [{ name: "machine_tools_token", reference: "proxmox/pve01/pbs/machine_tools_token v1" }],
  previousPrepared: null,
  previewSha256: hex("preview-pbs"),
};

const infraPlan: Plan = {
  hasChanges: true,
  planSha256: hex("plan-app-infra"),
  toolVersion: "1.10.6",
  counts: { create: 1, update: 1, delete: 0, replace: 0 },
  changes: [
    { address: "technitium_dns_record.app", type: "technitium_dns_record", name: "app", actions: ["create"], attributes: [] },
    { address: "proxmox_virtual_environment_firewall_rules.app", type: "proxmox_virtual_environment_firewall_rules", name: "app", actions: ["update"], attributes: [] },
  ],
};

const deployPreview: ConfigurePreview = {
  action: "deploy",
  playbook: "deploy.yml",
  commit: headCommit,
  image: "docker.io/skorcius/octopushy:ansible-2.21.5-7c1e9a2",
  host: resources.app.host,
  toolVersion: "ansible-playbook [core 2.21.5]",
  destructive: false,
  tasks: ["play #1 (all): Deploy the app", "  Pull the image", "  Write the compose file", "  Restart the service", "  Wait for /healthz"],
  changedFiles: ["M modules/ansible/web/deploy.yml"],
  vars: { app_hostname: "app.home.example.org", app_port: 8080 },
  secrets: [{ name: "web_password", reference: "home/pve01/web/app/web_password v3" }],
  prepared: { image: "ghcr.io/example/app:1.8.2", replicas: 1 },
  previousPrepared: { image: "ghcr.io/example/app:1.8.1", replicas: 1 },
  hostKeyFingerprints: ["ssh-ed25519 SHA256:k8Hn2pQ5rT1vX7yB3cD9fG0jL4mN6sW2aE5uI8oP1zA"],
  previewSha256: hex("pipeline-app"),
};

const recap = (host: string, ok: number, changed: number, failed = 0) =>
  `${host.padEnd(26)} : ok=${ok}    changed=${changed}    unreachable=0    failed=${failed}`;

function scenarios(): RunDetail[] {
  const runs: RunDetail[] = [];

  // e. Running: a new container being created.
  {
    const id = "running";
    const t = (s: number) => ago(3, s);
    const plan: Plan = {
      hasChanges: true,
      planSha256: hex("plan-dns"),
      toolVersion: "1.10.6",
      counts: { create: 1, update: 0, delete: 0, replace: 0 },
      changes: [{
        address: "proxmox_virtual_environment_container.this", type: "proxmox_virtual_environment_container", name: "this", actions: ["create"],
        attributes: [
          { name: "cpu.cores", before: null, after: 1, unknown: false, sensitive: false },
          { name: "memory.dedicated", before: null, after: 1024, unknown: false, sensitive: false },
        ],
      }],
    };
    runs.push(run({
      id, number: 50, operation: "provision", resource: resources.dns, reason: "revision", status: "running",
      detail: null, minutesAgo: 3, currentStep: "apply", policy: automatic, workflow: { id: "lxc-basic", version: "1.2" },
      steps: [
        ran(id, 0, "plan", "succeeded", t(4), 21, tofuPlan(["  # proxmox_virtual_environment_container.this will be created",
          '  + resource "proxmox_virtual_environment_container" "this" {', "      + vm_id = 121", "    }"], "Plan: 1 to add, 0 to change, 0 to destroy."), { plan }),
        decided(plan.planSha256!, t(26), "policy", "Approval is automatic (set by octopushy.yaml)."),
        { name: "unprotect", status: "skipped", detail: "The container is new; there is no protection to remove." },
        ran(id, 3, "apply", "running", t(30), 60, [
          ["agent", "Workspace ready; applying the approved plan only"],
          ["stdout", "proxmox_virtual_environment_container.this: Creating..."],
          ...Array.from({ length: 14 }, (_, i): Script[number] => ["stdout", `proxmox_virtual_environment_container.this: Still creating... [${(i + 1) * 10}s elapsed]`]),
        ]),
        pending("verify"),
      ],
    }));
  }

  // a. Provision awaiting approval: resize bb.
  {
    const id = "provision-approval";
    const t = (s: number) => ago(12, s);
    runs.push(run({
      id, number: 49, operation: "provision", resource: resources.bb, reason: "revision", status: "awaiting-approval",
      detail: `Approval is manual (set by ${guestFile("bb")}).`, minutesAgo: 12, currentStep: "approval", policy: manual("bb"),
      workflow: { id: "machine-tools", version: "1.4" },
      steps: [
        ran(id, 0, "plan", "succeeded", t(3), 34, tofuPlan(vmChanges, "Plan: 1 to add, 1 to change, 0 to destroy."), { plan: provisionPlan }),
        awaiting(provisionPlan.planSha256!, t(38), `Approval is manual (set by ${guestFile("bb")}).`),
        pending("unprotect"),
        pending("apply"),
        pending("verify"),
      ],
    }));
  }

  // b. Configure awaiting approval: new AI tools.
  {
    const id = "configure-approval";
    const t = (s: number) => ago(25, s);
    runs.push(run({
      id, number: 48, operation: "configure", resource: resources.ai, reason: "manual", status: "awaiting-approval",
      detail: `Approval is manual (set by ${guestFile("pve01-ai")}).`, minutesAgo: 25, currentStep: "approval", policy: manual("pve01-ai"),
      workflow: { id: "ai-server", version: "2.1" },
      steps: [
        ran(id, 0, "preview", "succeeded", t(3), 48, ansiblePreview("update-tools.yml", aiTasks), { configurePreview: aiPreview }),
        awaiting(aiPreview.previewSha256!, t(52), `Approval is manual (set by ${guestFile("pve01-ai")}).`),
        pending("playbook"),
      ],
    }));
  }

  // d. A pipeline awaiting one approval across two stages.
  {
    const id = "pipeline-approval";
    const t = (s: number) => ago(40, s);
    runs.push(run({
      id, number: 47, operation: "pipeline", resource: resources.app, reason: "revision", status: "awaiting-approval",
      detail: "Approval is manual (set by labs/home/proxmox/pve01/guests/web/services/app/service.yaml).", minutesAgo: 40,
      currentStep: "approval",
      policy: { approval: "manual", approvalSetBy: "labs/home/proxmox/pve01/guests/web/services/app/service.yaml", destructive: "manual", destructiveSetBy: "octopushy.yaml" },
      workflow: { id: "web", version: "1.2" },
      steps: [
        ran(id, 0, "plan", "succeeded", t(3), 19, tofuPlan([
          "  # technitium_dns_record.app will be created", '  + resource "technitium_dns_record" "app" {', '      + name = "app"', "    }",
          "  # proxmox_virtual_environment_firewall_rules.app will be updated in-place",
          '  ~ resource "proxmox_virtual_environment_firewall_rules" "app" {', "      ~ rule { dport = \"8080\" -> \"8443\" }", "    }",
        ], "Plan: 1 to add, 1 to change, 0 to destroy."), { plan: infraPlan, stage: "infra" }),
        ran(id, 1, "preview", "succeeded", t(24), 31, ansiblePreview("deploy.yml", deployPreview.tasks), { configurePreview: deployPreview, stage: "deploy" }),
        awaiting(deployPreview.previewSha256!, t(57), "Approval is manual (set by labs/home/proxmox/pve01/guests/web/services/app/service.yaml)."),
        pending("apply", "infra"),
        pending("playbook", "deploy"),
      ],
    }));
  }

  // c. Configure waiting on a declared outcome: work is active on the machine.
  {
    const id = "outcome-approval";
    const t = (s: number) => ago(70, s);
    const outcome = "The playbook reported deferred: Three threads are running.";
    runs.push(run({
      id, number: 46, operation: "configure", resource: resources.dev, reason: "manual", status: "awaiting-approval",
      detail: outcome, minutesAgo: 70, currentStep: "outcome-approval", policy: manual("dev"),
      workflow: { id: "machine-tools", version: "1.4" },
      steps: [
        ran(id, 0, "preview", "succeeded", t(3), 41, ansiblePreview("update-tools.yml", toolsTasks), { configurePreview: devPreview }),
        decided(devPreview.previewSha256!, t(300), "ops", "Node 24.18 is out; go ahead."),
        ran(id, 2, "playbook", "succeeded", t(302), 64, ansibleRun(resources.dev.host, ["Check for active work"], recap("dev", 5, 0), [
          ["stdout", "TASK [Report deferred work] ****************************************************"],
          ["stdout", "ok: [dev] => {\"octopushy_outcome\": \"deferred\", \"octopushy_detail\": \"Three threads are running.\"}"],
          ["stdout", "TASK [meta: end_play] **********************************************************"],
        ]), { outputs: { outcome: "deferred", detail: "Three threads are running." } }),
        {
          ...awaiting(devPreview.previewSha256!, t(370),
            "The playbook reported deferred: Three threads are running. Work is active on the machine. Approving interrupts it. Approving runs update-tools once more with machine_tools_allow_busy."),
          name: "outcome-approval",
        },
        pending("playbook-approved"),
      ],
    }));
  }

  // f. Succeeded: pbs set up.
  {
    const id = "succeeded";
    const t = (s: number) => ago(190, s);
    runs.push(run({
      id, number: 45, operation: "configure", resource: resources.pbs, reason: "revision", status: "succeeded",
      detail: null, minutesAgo: 190, seconds: 412, currentStep: null, policy: automatic, workflow: { id: "machine-tools", version: "1.4" },
      steps: [
        ran(id, 0, "preview", "succeeded", t(3), 38, ansiblePreview("configure.yml", toolsTasks), { configurePreview: pbsPreview }),
        decided(pbsPreview.previewSha256!, t(42), "policy", "Approval is automatic (set by octopushy.yaml)."),
        ran(id, 2, "playbook", "succeeded", t(44), 366, ansibleRun(resources.pbs.host, ["Install base packages", "Install tools from the stable channel", "Configure journald", "Enable node_exporter", "Record the versions"], recap("pbs", 31, 12))),
        { name: "outcome-approval", status: "skipped", detail: "The playbook finished." },
        { name: "playbook-approved", status: "skipped", detail: "The playbook finished." },
      ],
    }));
  }

  // f. Failed: media's disk does not fit.
  {
    const id = "failed";
    const t = (s: number) => ago(300, s);
    const error = "error waiting for VM disk resize: 500 Internal Server Error: storage 'local-lvm' has no space left (needs 64 GiB, 41 GiB free)";
    const plan: Plan = {
      hasChanges: true, planSha256: hex("plan-media"), toolVersion: "1.10.6", counts: { create: 0, update: 1, delete: 0, replace: 0 },
      changes: [{ address: "proxmox_virtual_environment_vm.this", type: "proxmox_virtual_environment_vm", name: "this", actions: ["update"],
        attributes: [{ name: "disk.scsi1.size", before: 200, after: 264, unknown: false, sensitive: false }] }],
    };
    runs.push(run({
      id, number: 44, operation: "provision", resource: resources.media, reason: "revision", status: "failed",
      detail: `Apply failed: ${error}`, minutesAgo: 300, seconds: 151, currentStep: null, policy: manual("media"),
      workflow: { id: "proxmox-vm", version: "1.3" },
      steps: [
        ran(id, 0, "plan", "succeeded", t(3), 29, tofuPlan(["  # proxmox_virtual_environment_vm.this will be updated in-place", "      ~ size = 200 -> 264"], "Plan: 0 to add, 1 to change, 0 to destroy."), { plan }),
        decided(plan.planSha256!, t(80), "ops", null),
        { name: "unprotect", status: "skipped", detail: "Protection stays: the plan changes the VM in place." },
        ran(id, 3, "apply", "failed", t(84), 66, tofuApply(["proxmox_virtual_environment_vm.this"], error), { error, detail: error }),
        { name: "verify", status: "skipped", detail: "An earlier step did not succeed." },
      ],
    }));
  }

  // History: an earlier resize of bb, a rejected configure of dev, and the app's last pipeline.
  {
    const id = "history-bb";
    const t = (s: number) => ago(1500, s);
    const plan: Plan = { hasChanges: true, planSha256: hex("plan-bb-old"), toolVersion: "1.10.5", counts: { create: 0, update: 1, delete: 0, replace: 0 },
      changes: [{ address: "proxmox_virtual_environment_vm.this", type: "proxmox_virtual_environment_vm", name: "this", actions: ["update"],
        attributes: [{ name: "cpu.cores", before: 1, after: 2, unknown: false, sensitive: false }] }] };
    runs.push(run({
      id, number: 43, operation: "provision", resource: resources.bb, reason: "revision", status: "succeeded", detail: null,
      minutesAgo: 1500, seconds: 104, currentStep: null, policy: manual("bb"), workflow: { id: "machine-tools", version: "1.3" },
      steps: [
        ran(id, 0, "plan", "succeeded", t(3), 27, tofuPlan(["      ~ cores = 1 -> 2"], "Plan: 0 to add, 1 to change, 0 to destroy."), { plan }),
        decided(plan.planSha256!, t(40), "ops", null),
        { name: "unprotect", status: "skipped", detail: "Protection stays: the plan changes the VM in place." },
        ran(id, 3, "apply", "succeeded", t(42), 38, tofuApply(["proxmox_virtual_environment_vm.this"])),
        ran(id, 4, "verify", "succeeded", t(82), 3, verify(112)),
      ],
    }));
  }
  {
    const id = "history-rejected";
    const t = (s: number) => ago(2900, s);
    const preview = { ...devPreview, previewSha256: hex("preview-dev-old") };
    runs.push(run({
      id, number: 42, operation: "configure", resource: resources.dev, reason: "schedule", status: "cancelled", detail: "Rejected by ops.",
      minutesAgo: 2900, seconds: 640, currentStep: null, policy: manual("dev"), workflow: { id: "machine-tools", version: "1.3" },
      steps: [
        ran(id, 0, "preview", "succeeded", t(3), 36, ansiblePreview("update-tools.yml", toolsTasks), { configurePreview: preview }),
        { name: "approval", status: "cancelled", detail: "Rejected.", startedAt: t(40), finishedAt: t(640),
          approval: { required: true, decision: "rejected", decidedBy: "ops", decidedAt: t(640), note: "Not during the backup window.", planSha256: preview.previewSha256 } },
        { name: "playbook", status: "skipped", detail: "The run was cancelled." },
        { name: "outcome-approval", status: "skipped", detail: "The run was cancelled." },
        { name: "playbook-approved", status: "skipped", detail: "The run was cancelled." },
      ],
    }));
  }
  {
    const id = "history-pipeline";
    const t = (s: number) => ago(4400, s);
    runs.push(run({
      id, number: 41, operation: "pipeline", resource: resources.app, reason: "revision", status: "succeeded", detail: null,
      minutesAgo: 4400, seconds: 220, currentStep: null, policy: automatic, workflow: { id: "web", version: "1.1" },
      steps: [
        ran(id, 0, "plan", "succeeded", t(3), 18, tofuPlan(["      ~ dport = \"80\" -> \"8080\""], "Plan: 0 to add, 1 to change, 0 to destroy."), { stage: "infra" }),
        ran(id, 1, "preview", "succeeded", t(22), 29, ansiblePreview("deploy.yml", deployPreview.tasks), { stage: "deploy" }),
        { ...decided(hex("pipeline-app-old"), t(52), "policy", "Approval is automatic (set by octopushy.yaml)."), stage: null },
        ran(id, 3, "apply", "succeeded", t(54), 22, tofuApply(["proxmox_virtual_environment_firewall_rules.app"]), { stage: "infra" }),
        ran(id, 4, "playbook", "succeeded", t(78), 140, ansibleRun(resources.app.host, ["Pull the image", "Write the compose file", "Restart the service", "Wait for /healthz"], recap("web", 14, 3)), { stage: "deploy" }),
      ],
    }));
  }
  return runs.map(refreshFlags);
}

/** Every run, newest first. */
export const runs: RunDetail[] = scenarios();

export const isFinished = (status: string) => terminal.has(status);

function refreshFlags(detail: RunDetail): RunDetail {
  const active = detail.steps.some((s) => s.attempts.some((a) => a.mutating && (a.status === "running" || a.status === "assigned")));
  detail.canCancel = !terminal.has(detail.run.status) && detail.run.status !== "uncertain" && !active;
  detail.canForceStop = !terminal.has(detail.run.status) && active;
  detail.canClose = detail.run.status === "uncertain";
  detail.canRunAgain = detail.run.status === "failed";
  return detail;
}

/** A run as lists show it. */
export const summary = (detail: RunDetail): RunSummary => ({ ...detail.run });

// Decisions and other changes to runs

export class Refused extends Error {
  readonly status: number;
  constructor(status: number, message: string) {
    super(message);
    this.status = status;
  }
}

function find(runId: string, stepId: string) {
  const detail = runs.find((r) => r.run.id === runId);
  if (!detail) throw new Refused(404, "The run does not exist.");
  const step = detail.steps.find((s) => s.id === stepId);
  if (!step) throw new Refused(404, "The step does not exist.");
  if (step.status !== "awaiting-approval" || !step.approval) throw new Refused(409, "This step is not waiting for a decision.");
  return { detail, step };
}

/** Ends a run as the server's Finish does: pending steps are skipped, others cancelled when the run is. */
function finish(detail: RunDetail, status: string, text: string | null) {
  const now = iso(Date.now());
  Object.assign(detail.run, { status, detail: text, finishedAt: now, currentStep: null, waiting: null });
  for (const step of detail.steps) {
    if (doneSteps.has(step.status) || step.status === "failed" || step.status === "uncertain") continue;
    step.status = status === "cancelled" && step.status !== "pending" ? "cancelled" : "skipped";
    step.detail ??= status === "cancelled" ? "The run was cancelled." : "An earlier step did not succeed.";
    for (const a of step.attempts) if (!terminal.has(a.status)) Object.assign(a, { status: "cancelled", finishedAt: now });
  }
  refreshFlags(detail);
}

/** Approving runs every later step to success at once: the mock has no agents to wait for. */
export function approve(runId: string, stepId: string, user: string, note: string | null, planSha256: string | null) {
  const { detail, step } = find(runId, stepId);
  if ((planSha256 ?? "").toLowerCase() !== (step.approval!.planSha256 ?? "").toLowerCase())
    throw new Refused(409, "The plan changed since it was shown; review the current plan.");
  const now = iso(Date.now());
  step.approval = { ...step.approval!, decision: "approved", decidedBy: user, decidedAt: now, note };
  Object.assign(step, { status: "succeeded", finishedAt: now });
  const resource = Object.values(resources).find((r) => r.id === detail.run.resourceId) as Resource;
  for (const later of detail.steps.filter((s) => s.ordinal > step.ordinal && s.status === "pending")) {
    if (step.name !== "outcome-approval" && (later.name === "outcome-approval" || later.name === "playbook-approved")) {
      Object.assign(later, { status: "skipped", detail: "The playbook finished." });
    } else if (later.name === "unprotect") {
      Object.assign(later, { status: "skipped", detail: "Protection stays: the plan changes the VM in place." });
    } else {
      const [executor, action, mutating] = actionFor(later.name);
      later.attempts = [attempt(later.id, executor, action, "succeeded", now, 0, scriptFor(later.name, resource), { mutating })];
      Object.assign(later, { status: "succeeded", startedAt: now, finishedAt: now });
    }
  }
  finish(detail, "succeeded", null);
}

export function reject(runId: string, stepId: string, user: string, note: string | null) {
  const { detail, step } = find(runId, stepId);
  const now = iso(Date.now());
  step.approval = { ...step.approval!, decision: "rejected", decidedBy: user, decidedAt: now, note };
  Object.assign(step, { status: "cancelled", detail: "Rejected.", finishedAt: now });
  finish(detail, "cancelled", `Rejected by ${user}.`);
}

export function cancel(runId: string, user: string) {
  const detail = runs.find((r) => r.run.id === runId);
  if (!detail) throw new Refused(404, "The run does not exist.");
  if (terminal.has(detail.run.status)) throw new Refused(409, "The run has already finished.");
  if (!detail.canCancel) throw new Refused(409, "A change is being applied; it cannot be interrupted. Wait for it to finish.");
  finish(detail, "cancelled", `Cancelled by ${user}.`);
}

/** Stops a change in progress without its agent, as the server's ForceStopAsync does: the change becomes uncertain. */
export function forceStop(runId: string, user: string) {
  const detail = runs.find((r) => r.run.id === runId);
  if (!detail) throw new Refused(404, "The run does not exist.");
  if (!detail.canForceStop) throw new Refused(409, "No change is being applied; cancel the run instead.");
  const now = iso(Date.now());
  for (const step of detail.steps) {
    for (const a of step.attempts.filter((a) => a.status === "running" || a.status === "assigned")) {
      Object.assign(a, { status: a.mutating ? "uncertain" : "cancelled", error: `Stopped by ${user} before the agent reported.`, finishedAt: now });
      Object.assign(step, {
        status: a.mutating ? "uncertain" : "cancelled",
        detail: a.mutating
          ? `Stopped by ${user} during ${a.action}; whether the change was made is unknown. Check the guest, then close the run, or wait for the agent to report.`
          : `Stopped by ${user}.`,
        finishedAt: now,
      });
    }
  }
  const uncertain = detail.steps.find((s) => s.status === "uncertain");
  Object.assign(detail.run, { status: "uncertain", detail: uncertain?.detail ?? null, waiting: null });
  refreshFlags(detail);
}

const stepsFor: Record<string, string[]> = {
  provision: ["plan", "approval", "unprotect", "apply", "verify"],
  configure: ["preview", "approval", "playbook"],
  pipeline: ["plan", "preview", "approval", "apply", "playbook"],
  "rotate-access": ["preview", "approval", "verify-current", "authorize-key", "verify-key", "promote-key", "approve-removal", "remove-key", "verify-removal", "confirm-refused"],
};

/** Starts a run that stays queued: the mock has no agents. Returns its id. */
export function startRun(resource: Resource, operation: string, reason = "manual"): string {
  const number = Math.max(...runs.map((r) => Number(r.run.number))) + 1;
  const id = `run-${number}`;
  const now = Date.now();
  const names = stepsFor[operation] ?? stepsFor.provision;
  const stages = operation === "pipeline" ? ["infra", "deploy", undefined, "infra", "deploy"] : [];
  const detail = run({
    id, number, operation, resource, reason, status: "queued", detail: null, minutesAgo: 0, currentStep: names[0],
    policy: automatic, workflow: { id: "machine-tools", version: "1.4" },
    steps: names.map((name, i) => {
      if (i > 0) return pending(name, stages[i]);
      const [executor, action] = actionFor(name);
      return { name, status: "queued", stage: stages[i] ?? null, attempts: [attempt(`${id}-s0`, executor, action, "queued", iso(now), 0, [])] };
    }),
  });
  Object.assign(detail.run, { createdAt: iso(now), startedAt: null, waiting: "The mock server has no agents, so this run stays queued." });
  runs.unshift(refreshFlags(detail));
  return id;
}

/** Lines of an attempt's log after `after`; a running attempt shows two more lines each second. */
export function logChunk(attemptId: string, after: number, limit: number) {
  const all = logs.get(attemptId);
  if (!all) return null;
  const since = liveSince.get(attemptId);
  const live = since !== undefined && runs.some((r) => r.steps.some((s) => s.attempts.some((a) => a.id === attemptId && a.status === "running")));
  const visible = live ? all.slice(0, Math.min(all.length, 3 + Math.floor((Date.now() - since) / 500))) : all;
  const lines = visible.filter((l) => Number(l.sequence) > after).slice(0, limit);
  return { lines, next: lines.length ? Number(lines[lines.length - 1].sequence) : after, finished: !live };
}
