// Everything the mock serves besides runs: one lab, one Proxmox installation and its guests, a service, agents, the variable
// store, the lab repository and its revisions, workflows, drafts and changes. Statuses are derived from the runs, so approving
// a run moves its guest on everywhere.
import type {
  Agent, Change, ChangeForm, DeployTargets, DraftDetail, DraftSummary, EnrollmentToken, GuestConfiguration, GuestDetail,
  GuestSummary, InstallationDetail, InstallationSummary, Inventory, ManagedAccess, ObservedGuest, ObservedHost, Overview,
  Repository, Revision, RunDetail, RunSummary, ServiceDetail, ServiceSummary, Variable, VariableVersion, Variables,
  WorkflowCatalog, WorkflowVersion,
} from "../src/api/types.ts";
import { ago, headCommit, hex, isFinished, labRepository, resources, runs, summary, workflowsRepository } from "./runs.ts";

const gib = 1024 ** 3;
const endpoint = "https://10.15.60.2:8006/";

// Status, as the server derives it from a resource's active and last run.

function runsOf(id: string): RunSummary[] {
  return runs.filter((r) => r.run.resourceId === id).map(summary);
}

function status(id: string) {
  const all = runsOf(id);
  const activeRun = all.find((r) => !isFinished(r.status)) ?? null;
  const lastRun = all.find((r) => isFinished(r.status)) ?? null;
  if (activeRun) {
    const waiting = activeRun.status === "awaiting-approval" ? `Run #${activeRun.number} waits for a decision.` : `Run #${activeRun.number} is ${activeRun.status}.`;
    return { activeRun, lastRun, status: activeRun.status === "assigned" ? "running" : activeRun.status, statusDetail: waiting };
  }
  if (lastRun?.status === "failed") return { activeRun, lastRun, status: "failed", statusDetail: `Run #${lastRun.number} failed.` };
  if (lastRun?.status === "cancelled")
    return { activeRun, lastRun, status: "pending", statusDetail: `Run #${lastRun.number} was cancelled; the definition is not applied.` };
  return { activeRun, lastRun, status: "in-sync", statusDetail: null };
}

// Guests

interface GuestBase {
  id: string;
  name: string;
  vmid: number;
  type: "vm" | "container";
  workflow: string;
  resolved: string;
  ip: string;
  cores: number;
  memory: number;
  disks: number[];
  tags: string[];
  uptimeHours: number;
  inputs?: Record<string, unknown>;
  configuration?: GuestConfiguration;
  access?: boolean;
  approval?: "manual";
}

const configuration = (workflow: string, actions: [string, string | null][], schedules: GuestConfiguration["schedules"] = []): GuestConfiguration => ({
  workflow,
  actions: actions.map(([id, description]) => ({ id, description, destructive: false, onChange: id === "setup" })),
  inSync: true,
  appliedAction: "setup",
  appliedCommit: hex("revision-3", 40),
  appliedAt: ago(4200),
  hasSnapshot: true,
  schedules,
  secrets: [],
  refused: null,
});

const toolActions: [string, string | null][] = [
  ["setup", "Set the machine up from what the channels offer now."],
  ["update-tools", "Move the tools to what the channels offer now."],
  ["update-system", "Update the OS, keeping the tools the last run installed. May reboot."],
];

const guests: GuestBase[] = [
  { id: "bb", name: "bb", vmid: 112, type: "vm", workflow: "machine-tools@1", resolved: "machine-tools@1.4", ip: "10.15.60.12", cores: 2,
    memory: 8192, disks: [64], tags: ["homelab", "agents"], uptimeHours: 412, approval: "manual", access: true,
    inputs: { machine_tools_channel: "stable" }, configuration: configuration("machine-tools@1.4", toolActions) },
  { id: "dev", name: "dev", vmid: 113, type: "vm", workflow: "machine-tools@1", resolved: "machine-tools@1.4", ip: "10.15.60.13", cores: 4,
    memory: 16384, disks: [120], tags: ["homelab"], uptimeHours: 96, approval: "manual", access: true,
    inputs: { machine_tools_channel: "stable" },
    configuration: configuration("machine-tools@1.4", toolActions, [{ action: "update-tools", cron: "0 6 * * *", timezone: "Europe/Madrid",
      nextAt: ago(-600), lastRunId: "history-rejected", lastRunNumber: 42, lastRunStatus: "cancelled", lastRunDetail: "Rejected by ops.",
      note: null, noteAt: null }]) },
  { id: "pve01-ai", name: "pve01-ai", vmid: 114, type: "vm", workflow: "ai-server@2", resolved: "ai-server@2.1", ip: "10.15.60.14", cores: 8,
    memory: 32768, disks: [200], tags: ["homelab", "ai"], uptimeHours: 30, approval: "manual", access: true,
    inputs: { ai_server_channel: "stable" },
    configuration: configuration("ai-server@2.1", [["setup", null], ["update-tools", "Move codex, claude-code and the CLI tools to the channel's versions."]]) },
  { id: "media", name: "media", vmid: 115, type: "vm", workflow: "proxmox-vm@1", resolved: "proxmox-vm@1.3", ip: "10.15.60.15", cores: 4,
    memory: 8192, disks: [32, 264], tags: ["homelab", "media"], uptimeHours: 1310, approval: "manual" },
  { id: "pbs", name: "pbs", vmid: 116, type: "vm", workflow: "machine-tools@1", resolved: "machine-tools@1.4", ip: "10.15.60.16", cores: 2,
    memory: 4096, disks: [32], tags: ["homelab", "backup"], uptimeHours: 3, access: true,
    inputs: { machine_tools_channel: "stable" }, configuration: configuration("machine-tools@1.4", toolActions) },
  { id: "web", name: "web", vmid: 120, type: "container", workflow: "lxc-basic@1", resolved: "lxc-basic@1.2", ip: "10.15.60.20", cores: 2,
    memory: 2048, disks: [16], tags: ["homelab", "web"], uptimeHours: 700, access: true },
  { id: "dns", name: "dns", vmid: 121, type: "container", workflow: "lxc-basic@1", resolved: "lxc-basic@1.2", ip: "10.15.60.21", cores: 1,
    memory: 1024, disks: [8], tags: ["homelab", "dns"], uptimeHours: 0 },
];

const address = (g: GuestBase) => `home/pve01/${g.name}`;
const guestFile = (g: GuestBase) => `labs/home/proxmox/pve01/guests/${g.name}/guest.yaml`;

function observed(g: GuestBase): ObservedGuest | null {
  // dns is being created by the running run, so Proxmox does not list it yet.
  if (g.id === "dns") return null;
  return {
    vmid: g.vmid, name: g.name, host: "pve01", status: "running", template: false, tags: g.tags, maxCpu: g.cores,
    maxMemoryBytes: g.memory * 1024 ** 2, uptimeSeconds: g.uptimeHours * 3600, managedBy: address(g), managedById: g.id,
    kind: g.type === "container" ? "lxc" : "qemu",
  };
}

function guestYaml(g: GuestBase) {
  return [
    "schemaVersion: 1",
    `type: ${g.type}`,
    "host: pve01",
    `vmid: ${g.vmid}`,
    `template: ${g.type === "vm" ? "fedora-44" : "debian-13-standard"}`,
    "hardware:",
    `  cores: ${g.cores}`,
    `  memory: ${g.memory}`,
    "  disks:",
    ...g.disks.map((size) => `    - { size: ${size} }`),
    "network:",
    "  bridge: vmbr0",
    "  mode: static",
    `  address: ${g.ip}/24`,
    "  gateway: 10.15.60.1",
    `tags: [${g.tags.join(", ")}]`,
    `workflow: ${g.workflow}`,
    ...(g.inputs ? ["inputs:", ...Object.entries(g.inputs).map(([k, v]) => `  ${k}: ${String(v)}`)] : []),
    ...(g.access ? ["access:", "  user: ops", `  sshKey: { store: proxmox/pve01/${g.name}/ssh-key }`] : []),
    ...(g.approval ? ["policy:", "  approval: manual              # every change waits for a person"] : []),
    "",
  ].join("\n");
}

function guestSummary(g: GuestBase): GuestSummary {
  const { activeRun, lastRun, status: state, statusDetail } = status(g.id);
  return {
    id: g.id, address: address(g), name: g.name, host: "pve01", vmid: g.vmid, lifecycle: "active", status: state, statusDetail,
    valid: true, observed: observed(g), activeRun, lastRun, workflow: { reference: g.workflow, resolved: g.resolved },
    type: g.type,
  };
}

const access = (g: GuestBase): ManagedAccess => ({
  publicKey: `ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAI${hex(g.id, 43)} octopushy@${g.name}`,
  fingerprint: `SHA256:${Buffer.from(hex(`fp-${g.id}`, 32), "hex").toString("base64").replace(/=+$/, "")}`,
  version: 1,
  provisionedAt: ago(9000),
  lastUsedAt: ago(70),
  keys: [{ version: 1, fingerprint: `SHA256:${Buffer.from(hex(`fp-${g.id}`, 32), "hex").toString("base64").replace(/=+$/, "")}`,
    state: "in-use", authorizedAt: ago(9000), verifiedAt: ago(9000), promotedAt: ago(9000), promotedBy: "ops", promotionEvidence: null, removedAt: null }],
});

export function guestDetail(id: string): GuestDetail | null {
  const g = guests.find((x) => x.id === id);
  if (!g) return null;
  const summaryNow = guestSummary(g);
  const applied = summaryNow.lastRun?.status === "succeeded" ? summaryNow.lastRun : null;
  return {
    guest: summaryNow,
    definitionFile: guestFile(g),
    definitionText: guestYaml(g),
    issues: [],
    policy: g.approval
      ? { approval: "manual", approvalSetBy: guestFile(g), destructive: "manual", destructiveSetBy: guestFile(g) }
      : { approval: "automatic", approvalSetBy: "octopushy.yaml", destructive: "manual", destructiveSetBy: "octopushy.yaml" },
    agentLabels: ["home"],
    agentLabelsSetBy: "labs/home/lab.yaml",
    binding: g.id === "dns" ? null : { installation: "home/pve01", vmid: g.vmid, boundAt: ago(20000) },
    desiredCommit: headCommit,
    checkedCommit: headCommit,
    checkedAt: ago(1),
    appliedCommit: applied?.commit ?? hex("revision-3", 40),
    appliedAt: applied?.finishedAt ?? ago(4300),
    verifiedAt: applied?.finishedAt ?? ago(4300),
    inSync: summaryNow.status === "in-sync",
    outputs: g.id === "dns" ? null : { vm_id: g.vmid, ipv4: g.ip, node: "pve01", mac: `BC:24:11:${hex(g.id, 6).toUpperCase().match(/../g)!.join(":")}` },
    missingSince: null,
    retiredAt: null,
    runs: runsOf(g.id),
    configuration: g.configuration ?? null,
    managedAccess: g.access ? access(g) : null,
    services: g.id === "web" ? [serviceSummary()] : [],
    hostKey: null,
  };
}

// The service

function serviceSummary(): ServiceSummary {
  const { activeRun, lastRun, status: state, statusDetail } = status(resources.app.id);
  return { id: resources.app.id, address: resources.app.address, name: "app", status: state, statusDetail, valid: true, activeRun, lastRun,
    workflow: { reference: "web@1", resolved: "web@1.2" } };
}

const serviceFile = "labs/home/proxmox/pve01/guests/web/services/app/service.yaml";

export function serviceDetail(id: string): ServiceDetail | null {
  if (id !== resources.app.id) return null;
  return {
    service: serviceSummary(),
    guestId: "web",
    guestAddress: "home/pve01/web",
    definitionFile: serviceFile,
    definitionText: "schemaVersion: 1\nworkflow: web@1\ninputs:\n  app_hostname: app.home.example.org\n  app_port: 8443\nsecrets:\n  web_password: { store: home/pve01/web/app/web_password }\npolicy:\n  approval: manual\n",
    issues: [],
    policy: { approval: "manual", approvalSetBy: serviceFile, destructive: "manual", destructiveSetBy: "octopushy.yaml" },
    agentLabels: ["home"],
    agentLabelsSetBy: "labs/home/lab.yaml",
    stages: [{ name: "infra", executor: "opentofu" }, { name: "deploy", executor: "ansible" }],
    appliedCommit: hex("revision-5", 40),
    appliedAt: ago(4396),
    missingSince: null,
    retiredAt: null,
    runs: runsOf(resources.app.id),
  };
}

// The installation

const host: ObservedHost = {
  name: "pve01", declared: true, online: true, cpu: 0.23, maxCpu: 16, memoryBytes: 41 * gib, maxMemoryBytes: 128 * gib,
  uptimeSeconds: 38 * 86400, concurrentOperations: 2,
};

const unmanaged: ObservedGuest[] = [
  { vmid: 100, name: "haos", host: "pve01", status: "running", template: false, tags: [], maxCpu: 2, maxMemoryBytes: 4 * gib,
    uptimeSeconds: 38 * 86400, managedBy: null, managedById: null, kind: "qemu" },
  { vmid: 9000, name: "fedora-44", host: "pve01", status: "stopped", template: true, tags: ["template"], maxCpu: 2, maxMemoryBytes: 2 * gib,
    uptimeSeconds: null, managedBy: null, managedById: null, kind: "qemu" },
];

function installationSummary(): InstallationSummary {
  return {
    id: "pve01", address: "home/pve01", name: "pve01", type: "standalone", endpoint, valid: true, managed: true, issueCount: 0,
    observedAt: ago(0.5), observeError: null, observeErrorAt: null, observing: false, observeWaiting: null, proxmoxVersion: "9.0.11",
    hosts: [host], guests: guests.map(guestSummary), unmanagedCount: unmanaged.length,
    trust: { endpoints: [endpoint], untrusted: [], trustedAt: ago(30000), trustedBy: "ops", managementChanged: false },
  };
}

export function installationDetail(id: string): InstallationDetail | null {
  if (id !== "pve01") return null;
  return {
    installation: installationSummary(),
    definitionFile: "labs/home/proxmox/pve01/installation.yaml",
    definitionText: [
      "schemaVersion: 1", "type: standalone", "connection:", `  endpoint: ${endpoint}`, "  tls: { verify: false }",
      "  readOnlyToken: { store: proxmox/pve01/read-only-token }", "  privilegedToken: { store: proxmox/pve01/privileged-token }",
      "hosts:", "  pve01:", "    datastores: { images: local, disks: local-lvm }", "    bridges: [vmbr0]",
      "    limits: { concurrentOperations: 2 }", "",
    ].join("\n"),
    issues: [],
    tokenKey: null,
    verifyTls: false,
    observedGuests: [...guests.map(observed).filter((o): o is ObservedGuest => o !== null), ...unmanaged],
    clusterName: null,
    snippets: null,
  };
}

// Repository and revisions

const subjects = [
  "bb: 64 GiB disk, 8 GiB memory, agents tag",
  "web/app: serve on 8443 behind the new DNS name",
  "dns: a container for Technitium",
  "pbs: set up with machine-tools@1",
  "Bump ai-server to 2.1",
  "media: grow the data disk",
  "Pin machine-tools to @1",
];

export const revisions: Revision[] = subjects.map((subject, i) => ({
  id: `revision-${i}`,
  commit: hex(`revision-${i}`, 40),
  subject,
  author: i % 3 === 1 ? "Alice" : "ops",
  committedAt: ago(15 + i * 700),
  loadedAt: ago(14 + i * 700),
  valid: i !== 4,
  errorCount: i === 4 ? 1 : 0,
  warningCount: i === 2 ? 1 : 0,
  isCurrentSource: i === 0,
  workflowsRepository,
  workflowsDigest: hex(`workflows-${i}`),
}));

export function revisionDetail(id: string) {
  const revision = revisions.find((r) => r.id === id);
  if (!revision) return null;
  const issues = revision.errorCount
    ? [{ severity: "error", file: "labs/home/proxmox/pve01/guests/pve01-ai/guest.yaml", line: 14, message: "inputs.ai_server_channel must be one of: stable, beta.", address: "home/pve01/pve01-ai" }]
    : revision.warningCount
    ? [{ severity: "warning", file: "labs/home/proxmox/pve01/guests/dns/guest.yaml", line: null, message: "No access.user: configure runs cannot log in.", address: "home/pve01/dns" }]
    : [];
  return { revision, issues };
}

export const repository: Repository = {
  url: labRepository,
  branch: "main",
  pollSeconds: 60,
  credentialKind: "token",
  credentialUsername: "octopushy",
  credentialSet: true,
  lastFetchAt: ago(0.3),
  lastFetchError: null,
  headCommit,
  revision: revisions[0],
  workflows: {
    source: "repository",
    repository: workflowsRepository,
    lastFetchAt: ago(0.3),
    lastFetchError: null,
    workflows: [
      ["machine-tools", ["1.4", "1.3"]], ["ai-server", ["2.1", "2.0"]], ["proxmox-vm", ["1.3"]], ["lxc-basic", ["1.2"]], ["web", ["1.2", "1.1"]],
    ].map(([id, versions]) => ({
      id: id as string,
      releases: (versions as string[]).map((version, i) => ({
        version, commit: hex(`${id}-${version}`, 40), firstSeenAt: ago(2000 + i * 9000), usable: true, tagDeleted: false, movedTo: null, issue: null,
      })),
    })),
    issues: [],
    uses: [
      ...guests.map((g) => ({ address: address(g), kind: "guest", reference: g.workflow, version: g.resolved.split("@")[1], commit: hex(g.resolved.replace("@", "-"), 40), problem: null, newerMajor: null })),
      { address: resources.app.address, kind: "service", reference: "web@1", version: "1.2", commit: hex("web-1.2", 40), problem: null, newerMajor: null },
    ],
  },
};

// Inventory and overview

export function inventory(): Inventory {
  return {
    revision: revisions[0],
    labs: [{ id: "lab-home", address: "home", name: "Home", valid: true, issues: [], installations: [installationSummary()] }],
  };
}

export function overview(): Overview {
  const statuses = guests.map((g) => status(g.id).status);
  const count = (...names: string[]) => statuses.filter((s) => names.includes(s)).length;
  const all = runs.map(summary);
  return {
    repository,
    counts: {
      guests: guests.length, inSync: count("in-sync"), pending: count("pending"), running: count("running", "queued"),
      awaitingApproval: count("awaiting-approval"), blocked: count("blocked"), failed: count("failed"), uncertain: count("uncertain"),
      invalid: 0, missing: 0,
    },
    agentsConnected: agents.filter((a) => a.connected).length,
    agentsTotal: agents.filter((a) => !a.disabled).length,
    attention: all.filter((r) => ["awaiting-approval", "blocked", "uncertain"].includes(r.status)).reverse(),
    observeErrors: [],
    recent: all.slice(0, 15),
  };
}

// Agents

export const agents: Agent[] = [
  {
    id: "agent-pve01-01", name: "pve01-agent-01", labels: ["home"], maxConcurrency: 2, disabled: false, connected: true, enrolledAt: ago(60000),
    lastSeenAt: ago(0.05), version: "0.7.1", executors: ["opentofu", "ansible", "proxmox"], toolRuntime: "podman 5.6.2",
    certificateExpiresAt: ago(-80000), runningAttempts: 1, draining: false, reportsReachability: true,
    reachability: [
      { kind: "proxmox", endpoint, context: "home/pve01", image: null, state: "reachable", detail: null, checkedAt: ago(2), expiresAt: ago(-8),
        usable: true, probing: false, leafSha256: hex("leaf-pve01") },
      { kind: "ssh", endpoint: "10.15.60.14:22", context: "home/pve01/pve01-ai", image: "docker.io/skorcius/octopushy:ansible-2.21.5-7c1e9a2",
        state: "reachable", detail: null, checkedAt: ago(2), expiresAt: ago(-8), usable: true, probing: false, leafSha256: null },
    ],
  },
  {
    id: "agent-pve01-02", name: "pve01-agent-02", labels: ["home", "gpu"], maxConcurrency: 1, disabled: false, connected: true, enrolledAt: ago(20000),
    lastSeenAt: ago(0.1), version: "0.7.1", executors: ["opentofu", "ansible", "proxmox"], toolRuntime: "podman 5.6.2",
    certificateExpiresAt: ago(-100000), runningAttempts: 0, draining: true, reportsReachability: false, reachability: null,
  },
  {
    id: "agent-laptop", name: "laptop", labels: ["dev"], maxConcurrency: 1, disabled: false, connected: false, enrolledAt: ago(90000),
    lastSeenAt: ago(5000), version: "0.6.4", executors: ["opentofu"], toolRuntime: "podman 5.4.0",
    certificateExpiresAt: ago(-2000), runningAttempts: 0, draining: false, reportsReachability: false, reachability: null,
  },
];

export const tokens: EnrollmentToken[] = [
  { id: "token-1", name: "pve02-agent-01", labels: ["home"], createdAt: ago(30), expiresAt: ago(-1410), usedAt: null, agentId: null },
  { id: "token-0", name: "pve01-agent-02", labels: ["home", "gpu"], createdAt: ago(20010), expiresAt: ago(18570), usedAt: ago(20000), agentId: "agent-pve01-02" },
];

export const operatorTokens = [
  { id: "op-1", name: "laptop CLI", createdAt: ago(9000), expiresAt: ago(-34000), revokedAt: null as string | null },
];

// The variable store: secret values are never sent, as on the real server.

const entry = (key: string, visibility: "secret" | "plain", usedBy: string[], version = 1, value: string | null = null, description: string | null = null): Variable => ({
  key, visibility, description, version, updatedAt: ago(1000 * version), value: visibility === "secret" ? null : value, usedBy, latestVersion: version, revoked: false,
});

export const variables: Variables = {
  entries: [
    entry("proxmox/pve01/read-only-token", "secret", ["home/pve01"], 2, null, "PVEAuditor token for observing"),
    entry("proxmox/pve01/privileged-token", "secret", ["home/pve01"], 1, null, "Token runs use to change guests"),
    entry("proxmox/pve01/bb/ssh-key", "secret", ["home/pve01/bb"]),
    entry("proxmox/pve01/dev/ssh-key", "secret", ["home/pve01/dev"]),
    entry("proxmox/pve01/dev/machine_tools_token", "secret", ["home/pve01/dev"], 2),
    entry("proxmox/pve01/pve01-ai/ssh-key", "secret", ["home/pve01/pve01-ai"]),
    entry("proxmox/pve01/pve01-ai/ai_server_token", "secret", ["home/pve01/pve01-ai"], 4),
    entry("proxmox/pve01/pbs/machine_tools_token", "secret", ["home/pve01/pbs"]),
    entry("home/pve01/web/app/web_password", "secret", ["home/pve01/web/app"], 3),
    entry("dns/technitium/api-token", "secret", ["home/pve01/web/app"]),
    entry("motd", "plain", [], 2, "Managed by Octopushy. Changes here are overwritten."),
    entry("dns/zone", "plain", ["home/pve01/web/app"], 1, "home.example.org"),
  ],
  missing: [{ key: "proxmox/pve01/bb/machine_tools_token", usedBy: ["home/pve01/bb"] }],
  system: [
    { key: "system/agent-ca/certificate", visibility: "plain", updatedAt: ago(90000) },
    { key: "system/agent-ca/private-key", visibility: "secret", updatedAt: ago(90000) },
  ],
};

export function variableVersions(key: string): VariableVersion[] {
  const current = variables.entries.find((v) => v.key === key);
  if (!current) return [];
  return Array.from({ length: Number(current.latestVersion) }, (_, i) => {
    const version = Number(current.latestVersion) - i;
    const active = version === Number(current.version);
    return {
      version, status: active ? "active" : "superseded", createdAt: ago(1000 * version + 5), activatedAt: ago(1000 * version), activatedBy: "ops",
      activationNote: active && version > 1 ? "Rotated after the token audit." : null, revokedAt: null, revokedBy: null, revocationNote: null,
      usedBy: active ? current.usedBy : [],
    };
  });
}

// Workflows and the deploy wizard

const inputsSchema = {
  type: "object",
  properties: {
    hardware: {
      type: "object",
      properties: {
        cores: { type: "integer", minimum: 1, maximum: 64 },
        memory: { description: "MiB", type: "integer", minimum: 256 },
        disks: { type: "array", minItems: 1, items: { type: "object", properties: { size: { description: "GiB", type: "integer", minimum: 1 } }, required: ["size"] } },
      },
      required: ["cores", "memory", "disks"],
    },
    network: {
      type: "object",
      properties: {
        bridge: { type: "string" },
        mode: { enum: ["static", "dhcp"] },
        address: { type: "string", description: "IPv4 with prefix, such as 10.15.60.30/24" },
        gateway: { type: "string" },
      },
      required: ["bridge", "mode"],
    },
    inputs: {
      type: "object",
      properties: { machine_tools_channel: { enum: ["stable", "beta"], default: "stable" } },
    },
  },
};

const version = (id: string, v: string, manages: string, description: string, secrets: string[], setup: boolean): WorkflowVersion => ({
  id, version: v, commit: hex(`${id}-${v}`, 40), usable: true, issue: null, manages, description,
  inputs: manages === "service" ? null : inputsSchema,
  ui: manages === "service" ? null : { vmid: { generate: "next-vmid" }, access: { user: "ops" } },
  secrets, setup, managedHostKey: false,
});

export const workflows: WorkflowCatalog = {
  source: "repository",
  repository: workflowsRepository,
  workflows: [
    { id: "machine-tools", manages: "vm", description: "A Proxmox VM, then set up by Ansible as a machine with no particular tools in it.",
      versions: ["1.4", "1.3"].map((v) => version("machine-tools", v, "vm", "A Proxmox VM, set up by Ansible.", ["machine_tools_token"], true)) },
    { id: "ai-server", manages: "vm", description: "A Fedora VM with codex, claude-code and the CLI tools they use.",
      versions: ["2.1", "2.0"].map((v) => version("ai-server", v, "vm", "A Fedora VM with AI coding tools.", ["ai_server_token"], true)) },
    { id: "proxmox-vm", manages: "vm", description: "A plain Proxmox VM from a template.", versions: [version("proxmox-vm", "1.3", "vm", "A plain Proxmox VM.", [], false)] },
    { id: "lxc-basic", manages: "container", description: "A Debian container.", versions: [version("lxc-basic", "1.2", "container", "A Debian container.", [], false)] },
    { id: "web", manages: "service", description: "DNS, firewall and a container app, deployed by Ansible.",
      versions: [version("web", "1.2", "service", "A web service.", ["web_password"], false)] },
  ],
};

export function deployTargets(): DeployTargets {
  return {
    baseCommit: headCommit,
    blockedReason: null,
    templates: ["fedora-44", "debian-13-standard"],
    installations: [{
      address: "home/pve01", lab: "home", name: "pve01", type: "standalone", managed: true, reason: null,
      hosts: [{ name: "pve01", declared: true, observed: true, status: "online" }],
      guests: guests.map((g) => ({ address: address(g), name: g.name, workflow: g.workflow, valid: true })),
      agentLabels: ["home"], observedAt: ago(0.5), id: "pve01", vmidProblem: null,
    }],
  };
}

export const nextVmid = (from: number) => {
  const taken = new Set([...guests.map((g) => g.vmid), ...unmanaged.map((g) => g.vmid)]);
  let vmid = Math.max(from, 100);
  while (taken.has(vmid)) vmid++;
  return vmid;
};

// Change forms and changes

export function changeForm(resourceId: string): ChangeForm | null {
  if (resourceId === resources.app.id) {
    return {
      resourceId, resourceKind: "service", address: resources.app.address, name: "app", baseCommit: headCommit, file: serviceFile,
      text: serviceDetail(resourceId)!.definitionText, workflow: "web@1", resolved: workflows.workflows[4].versions[0],
      hardware: null, network: null, inputs: { app_hostname: "app.home.example.org", app_port: 8443 },
      blockedReason: null, canRemove: false, upgrade: null, fieldIssues: [],
    };
  }
  const g = guests.find((x) => x.id === resourceId);
  if (!g) return null;
  const workflow = workflows.workflows.find((w) => g.workflow.startsWith(`${w.id}@`));
  return {
    resourceId, resourceKind: "guest", address: address(g), name: g.name, baseCommit: headCommit, file: guestFile(g), text: guestYaml(g),
    workflow: g.workflow, resolved: workflow?.versions[0] ?? null,
    hardware: { cores: g.cores, memory: g.memory, disks: g.disks.map((size) => ({ size })) },
    network: { bridge: "vmbr0", mode: "static", address: `${g.ip}/24`, gateway: "10.15.60.1" },
    inputs: g.inputs ?? {},
    blockedReason: null, canRemove: true, upgrade: null, fieldIssues: [],
  };
}

/** The bb change that started run #49; its status follows that run. */
function bbChange(): Change {
  const detail = runs.find((r) => r.run.id === "provision-approval") as RunDetail;
  const run = summary(detail);
  const [state, text, final] =
    run.status === "succeeded" ? ["applied", `Run #${run.number} succeeded.`, true]
    : isFinished(run.status) ? ["failed", `Run #${run.number} ${run.status === "cancelled" ? "was cancelled" : "failed"}.`, true]
    : run.status === "awaiting-approval" ? ["waiting-for-approval", `Run #${run.number} waits for a decision.`, false]
    : ["applying", null, false];
  return {
    id: "change-bb-resize", kind: "edit", resourceKind: "guest", resourceId: "bb", address: "home/pve01/bb", author: "ops",
    message: subjects[0], baseCommit: hex("revision-1", 40), commit: headCommit, commitUrl: null, revisionId: "revision-0",
    status: state as string, detail: text as string | null, files: ["labs/home/proxmox/pve01/guests/bb/guest.yaml"], runs: [run],
    createdAt: ago(15), syncedAt: ago(13), updatedAt: run.finishedAt ?? ago(12), final: final as boolean, notCommitted: false, cleanupPending: 0,
  };
}

/** Changes made from forms in this session, after the seeded one. */
export const madeChanges: Change[] = [];

export function changes(): Change[] {
  return [...madeChanges, bbChange()];
}

// Drafts in the repository editor

const labFiles: Record<string, string> = {
  "octopushy.yaml": "schemaVersion: 1\nworkflows:\n  repository: " + workflowsRepository + "\ndefaults:\n  policy:\n    approval: automatic\n",
  "labs/home/lab.yaml": "schemaVersion: 1\nname: Home\ntimezone: Europe/Madrid\nexecution:\n  agentLabels: [home]\n",
  "labs/home/proxmox/pve01/installation.yaml": installationDetail("pve01")!.definitionText!,
  ...Object.fromEntries(guests.map((g) => [guestFile(g), guestYaml(g)])),
  [serviceFile]: serviceDetail(resources.app.id)!.definitionText!,
};

interface Draft {
  summary: DraftSummary;
  files: Record<string, string>;
}

const draftSummary = (id: string, title: string, overrides: Partial<DraftSummary> = {}): DraftSummary => ({
  id, title, status: "open", baseCommit: headCommit, headCommit: hex(`draft-${id}`, 40), changedFiles: 0, behind: false, conflictPaths: [],
  createdAt: ago(240), updatedAt: ago(30), publishedCommit: null, publishedAt: null, publishedBy: null, ...overrides,
});

export const drafts = new Map<string, Draft>([
  ["draft-pbs-schedule", {
    summary: draftSummary("draft-pbs-schedule", "Nightly update-system on pbs"),
    files: { ...labFiles, [guestFile(guests[4])]: guestYaml(guests[4]) + "schedules:\n  - { action: update-system, cron: \"30 3 * * 0\" }\n" },
  }],
  ["draft-dns-tags", {
    summary: draftSummary("draft-dns-tags", "Tag the DNS container", { status: "published", publishedCommit: hex("revision-2", 40), publishedAt: ago(1400), publishedBy: "ops", updatedAt: ago(1400) }),
    files: { ...labFiles },
  }],
]);

function changedPaths(files: Record<string, string>) {
  const paths = new Set([...Object.keys(labFiles), ...Object.keys(files)]);
  return [...paths].sort().flatMap((path) =>
    !(path in files) ? [{ path, change: "deleted" }]
    : !(path in labFiles) ? [{ path, change: "added" }]
    : files[path] !== labFiles[path] ? [{ path, change: "modified" }] : []);
}

export function draftDetail(id: string): DraftDetail | null {
  const draft = drafts.get(id);
  if (!draft) return null;
  const changesNow = draft.summary.status === "published" ? [] : changedPaths(draft.files);
  draft.summary.changedFiles = changesNow.length;
  return {
    draft: draft.summary,
    branch: "main",
    editBlockedReason: draft.summary.status === "published" ? "This draft is published." : null,
    publishBlockedReason: changesNow.length === 0 ? "The draft has no changes yet." : null,
    changes: changesNow,
    files: Object.keys(draft.files).sort().map((path) => ({ path, size: draft.files[path].length, kind: "file" })),
  };
}

export function draftFile(id: string, path: string) {
  const draft = drafts.get(id);
  if (!draft) return null;
  const content = draft.files[path] ?? null;
  return {
    path, headCommit: draft.summary.headCommit, exists: content !== null, kind: content === null ? null : "text",
    size: content?.length ?? null, content, baseExists: path in labFiles, baseContent: labFiles[path] ?? null,
    editable: draft.summary.status !== "published",
  };
}

export function newDraft(title: string): string {
  const id = `draft-${drafts.size + 1}-${Date.now().toString(36)}`;
  drafts.set(id, { summary: draftSummary(id, title, { createdAt: new Date().toISOString(), updatedAt: new Date().toISOString() }), files: { ...labFiles } });
  return id;
}

export function changeDraft(id: string, edits: { path: string; content: string | null }[]) {
  const draft = drafts.get(id)!;
  for (const { path, content } of edits) {
    if (content === null) delete draft.files[path];
    else draft.files[path] = content;
  }
  draft.summary.headCommit = hex(`${id}-${Date.now()}`, 40);
  draft.summary.updatedAt = new Date().toISOString();
}

export const guestIds = guests.map((g) => g.id);
