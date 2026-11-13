// Everything the mock serves besides runs: one lab, one Proxmox installation and its guests, a service, agents, the variable
// store, the lab repository and its revisions, workflows, drafts and changes. Statuses are derived from the runs, so approving
// a run moves its guest on everywhere.
import type {
  Agent, Change, ChangeForm, DeployTargets, DraftDetail, DraftSummary, EnrollmentToken, GuestConfiguration, GuestDetail,
  GuestSummary, InstallationDetail, InstallationSummary, Inventory, ManagedAccess, ObservedGuest, ObservedHost, Overview, ReachabilityEntry,
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
  /** The installation and its one node; pve01 unless said. */
  node?: string;
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

// korriban is a second, smaller installation, offline for two days: its node and machine show as down on the Inventory.
const otherGuests: GuestBase[] = [
  { id: "nas", name: "nas", vmid: 200, type: "vm", workflow: "proxmox-vm@1", resolved: "proxmox-vm@1.3", ip: "10.15.60.30", cores: 2,
    memory: 4096, disks: [32, 2000], tags: ["homelab", "storage"], uptimeHours: 0, node: "korriban" },
];

const nodeOf = (g: GuestBase) => g.node ?? "pve01";
const address = (g: GuestBase) => `home/${nodeOf(g)}/${g.name}`;
const guestFile = (g: GuestBase) => `labs/home/proxmox/${nodeOf(g)}/guests/${g.name}/guest.yaml`;

function observed(g: GuestBase): ObservedGuest | null {
  // dns is being created by the running run, so Proxmox does not list it yet.
  if (g.id === "dns") return null;
  return {
    vmid: g.vmid, name: g.name, host: nodeOf(g), status: "running", template: false, tags: g.tags, maxCpu: g.cores,
    maxMemoryBytes: g.memory * 1024 ** 2, uptimeSeconds: g.uptimeHours * 3600, managedBy: address(g), managedById: g.id,
    kind: g.type === "container" ? "lxc" : "qemu",
  };
}

function guestYaml(g: GuestBase) {
  return [
    "schemaVersion: 1",
    `type: ${g.type}`,
    `host: ${nodeOf(g)}`,
    `vmid: ${g.vmid}`,
    `template: ${g.type === "vm" ? "fedora-44" : "debian-13-standard"}`,
    "# Sized for what it runs; grow the memory before the cores.",
    "hardware:",
    `  cores: ${g.cores}`,
    `  memory: ${g.memory}`,
    "  disks:",
    ...g.disks.map((size) => `    - { size: ${size} }`),
    "network:",
    "  bridge: vmbr0",
    "  mode: static                  # the address is also reserved on the router",
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
    id: g.id, address: address(g), name: g.name, host: nodeOf(g), vmid: g.vmid, lifecycle: "active", status: state, statusDetail,
    valid: true, observed: g.node === "korriban" ? null : observed(g), activeRun, lastRun, workflow: { reference: g.workflow, resolved: g.resolved },
    type: g.type, accessAddress: g.ip, services: servicesOf(g.id),
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
  const g = [...guests, ...otherGuests].find((x) => x.id === id);
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
    binding: g.id === "dns" ? null : { installation: `home/${nodeOf(g)}`, vmid: g.vmid, boundAt: ago(20000) },
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
    services: servicesOf(g.id),
    hostKey: null,
  };
}

// Services: app has runs; the others are in sync and only show on the Inventory and their own page.

export const appLinks = ["https://app.home.example.org/"];

export const otherServices: {
  id: string; guest: string; name: string; workflow: string; links: string[]; names?: { name: string; stage: string; url: string }[];
}[] = [
  { id: "svc-traefik", guest: "web", name: "traefik", workflow: "traefik@2", links: ["https://traefik.home.example.org/dashboard/"] },
  { id: "svc-jellyfin", guest: "media", name: "jellyfin", workflow: "compose-app@1", links: ["https://jellyfin.home.example.org/", "https://media.home.example.org/"],
    names: [{ name: "jellyfin.home.example.org", stage: "dns", url: "https://jellyfin.home.example.org/" }] },
  { id: "svc-technitium", guest: "dns", name: "technitium", workflow: "compose-app@1", links: ["http://10.15.60.21:5380/"] },
  { id: "svc-pbs", guest: "pbs", name: "proxmox-backup", workflow: "pbs@1", links: ["https://pbs.home.example.org:8007/"] },
  { id: "svc-samba", guest: "nas", name: "samba", workflow: "samba@1", links: ["tcp://10.15.60.40:445"] },
  // Under a second domain: fleet's dashboard and api names fold into ops.example.org.
  { id: "svc-fleet", guest: "dev", name: "fleet", workflow: "compose-app@1",
    links: ["https://fleet.ops.example.org/", "https://fleet.dashboard.ops.example.org/", "https://fleet.api.ops.example.org/v1/health"] },
  { id: "svc-open-webui", guest: "pve01-ai", name: "open-webui", workflow: "compose-app@1", links: ["https://chat.ops.example.org/"] },
];

// Icons: the predefined files, as if an operator mapped a folder of them. traefik, jellyfin, samba, fleet and open-webui
// match by name; app, technitium and proxmox-backup have no match and show their letter.

export const iconNames = [
  "adguard.svg", "fleet.svg", "gitea.svg", "grafana.svg", "home-assistant.svg", "jellyfin.svg", "nextcloud.svg", "open-webui.svg",
  "proxmox.svg", "samba.svg", "traefik.svg", "vaultwarden.svg",
];

/** Each service's own icon, set from the page: an uploaded picture or a chosen file. */
export const ownIcons = new Map<string, { picture: { type: string; bytes: Uint8Array; version: number } } | { chosen: string }>();

/** A predefined file: a rounded square in a colour from the name, with its first two letters. Not a real logo. */
export function iconSvg(name: string): string {
  const hue = [...name].reduce((sum, c) => sum * 31 + c.charCodeAt(0), 7) % 360;
  const letters = name.slice(0, 1).toUpperCase() + name.slice(1, 2);
  return `<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 48 48"><rect width="48" height="48" rx="10" fill="hsl(${hue} 55% 45%)"/>` +
    `<text x="24" y="31" text-anchor="middle" font-family="sans-serif" font-size="20" font-weight="600" fill="#fff">${letters}</text></svg>`;
}

/** The server's order: an uploaded picture, a chosen file still there, the file named after the service, else none. */
function iconOf(id: string, name: string): ServiceSummary["icon"] {
  const own = ownIcons.get(id);
  if (own && "picture" in own) return { url: `/api/services/${id}/icon?v=${own.picture.version}`, source: "picture" };
  if (own && iconNames.includes(own.chosen)) return { url: `/api/icons/${own.chosen}`, source: "chosen" };
  const match = iconNames.find((n) => n.replace(/\.[^.]+$/, "") === name);
  return match ? { url: `/api/icons/${match}`, source: "name" } : null;
}

const guestAddressOf = (id: string) => {
  const g = [...guests, ...otherGuests].find((x) => x.id === id)!;
  return address(g);
};

function serviceSummary(): ServiceSummary {
  const { activeRun, lastRun, status: state, statusDetail } = status(resources.app.id);
  return { id: resources.app.id, address: resources.app.address, name: "app", status: state, statusDetail, valid: true, activeRun, lastRun,
    workflow: { reference: "web@1", resolved: "web@1.2" }, links: appLinks, names: [], icon: iconOf(resources.app.id, "app") };
}

function otherSummary(s: (typeof otherServices)[number]): ServiceSummary {
  return { id: s.id, address: `${guestAddressOf(s.guest)}/${s.name}`, name: s.name, status: "in-sync", statusDetail: null, valid: true,
    activeRun: null, lastRun: null, workflow: { reference: s.workflow, resolved: `${s.workflow}.0` }, links: s.links, names: s.names ?? [], icon: iconOf(s.id, s.name) };
}

function servicesOf(guestId: string): ServiceSummary[] {
  return [...(guestId === "web" ? [serviceSummary()] : []), ...otherServices.filter((s) => s.guest === guestId).map(otherSummary)];
}

const serviceFile = "labs/home/proxmox/pve01/guests/web/services/app/service.yaml";

export function serviceDetail(id: string): ServiceDetail | null {
  const other = otherServices.find((s) => s.id === id);
  if (other) {
    const file = `labs/home/proxmox/${guestAddressOf(other.guest).split("/")[1]}/guests/${other.guest}/services/${other.name}/service.yaml`;
    return {
      service: otherSummary(other), guestId: other.guest, guestAddress: guestAddressOf(other.guest), definitionFile: file,
      definitionText: `schemaVersion: 1\nworkflow: ${other.workflow}\n${other.links.length ? `links:\n${other.links.map((l) => `  - ${l}\n`).join("")}` : ""}`,
      issues: [], policy: { approval: "automatic", approvalSetBy: "octopushy.yaml", destructive: "manual", destructiveSetBy: "octopushy.yaml" },
      agentLabels: ["home"], agentLabelsSetBy: "labs/home/lab.yaml", stages: [{ name: "deploy", executor: "ansible" }],
      appliedCommit: hex("revision-7", 40), appliedAt: ago(9000), missingSince: null, retiredAt: null, runs: [],
    };
  }
  if (id !== resources.app.id) return null;
  return {
    service: serviceSummary(),
    guestId: "web",
    guestAddress: "home/pve01/web",
    definitionFile: serviceFile,
    definitionText: "schemaVersion: 1\nworkflow: web@1\nlinks:\n  - https://app.home.example.org/\ninputs:\n  app_hostname: app.home.example.org\n  app_port: 8443\nsecrets:\n  web_password: { store: home/pve01/web/app/web_password }\npolicy:\n  approval: manual\n",
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
  uptimeSeconds: 38 * 86400, concurrentOperations: 2, endpoint,
};

export const korribanEndpoint = "https://10.15.60.3:8006/";

const korribanHost: ObservedHost = {
  name: "korriban", declared: true, online: false, cpu: null, maxCpu: 12, memoryBytes: null, maxMemoryBytes: 48 * gib,
  uptimeSeconds: null, concurrentOperations: 1, endpoint: korribanEndpoint,
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
    hosts: [host], guests: guests.map(guestSummary), unmanagedCount: unmanaged.filter((u) => !u.template).length,
    trust: { endpoints: [endpoint], untrusted: [], trustedAt: ago(30000), trustedBy: "ops", managementChanged: false },
    unmanagedGuests: unmanaged,
  };
}

function korribanSummary(): InstallationSummary {
  return {
    id: "korriban", address: "home/korriban", name: "korriban", type: "standalone", endpoint: korribanEndpoint, valid: true, managed: true,
    issueCount: 0, observedAt: ago(2900), observeError: "Cannot reach https://10.15.60.3:8006/: no route to host", observeErrorAt: ago(1),
    observing: false, observeWaiting: null, proxmoxVersion: "8.4.1", hosts: [korribanHost], guests: otherGuests.map(guestSummary),
    unmanagedCount: 0,
    trust: { endpoints: [korribanEndpoint], untrusted: [], trustedAt: ago(40000), trustedBy: "ops", managementChanged: false },
    unmanagedGuests: [],
  };
}

export function installationDetail(id: string): InstallationDetail | null {
  if (id === "korriban") {
    return {
      installation: korribanSummary(), definitionFile: "labs/home/proxmox/korriban/installation.yaml", definitionText: null, issues: [],
      tokenKey: null, verifyTls: false, observedGuests: [], clusterName: null, snippets: null,
    };
  }
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
  "dev: add the docker role",
  "web: a VM for the reverse proxy",
  "korriban: a second installation",
  "pve01-ai: pass the GPU through",
  "Default approval to automatic",
  "media: a VM for Jellyfin",
  "bb: first definition",
  "pbs: a datastore for the backups",
  "dns: reserve the address",
  "lxc-basic: unprivileged by default",
  "Add octopushy.yaml",
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
  isCurrentSource: i < subjects.length - 2,
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
    labs: [{ id: "lab-home", address: "home", name: "Home", valid: true, issues: [], installations: [korribanSummary(), installationSummary()] }],
  };
}

/** The runs created in the last 7 days, by status, as the server counts them. */
function week(all: RunSummary[]): Overview["week"] {
  const since = Date.now() - 7 * 86_400_000;
  const recent = all.filter((r) => Date.parse(r.createdAt) >= since);
  const count = (...names: string[]) => recent.filter((r) => names.includes(r.status)).length;
  const succeeded = count("succeeded");
  const failed = count("failed");
  const cancelled = count("cancelled");
  const unfinished = count("queued", "running", "awaiting-approval", "blocked", "uncertain");
  return { total: succeeded + failed + cancelled + unfinished, succeeded, failed, cancelled, unfinished };
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
    // The last run of each guest or service, when it failed. `all` is newest first.
    failed: all.filter((r, i) => r.status === "failed" && all.findIndex((o) => o.resourceId === r.resourceId) === i).slice(0, 50),
    week: week(all),
  };
}

// Agents

const ansible = "docker.io/skorcius/octopushy:ansible-2.21.5-7c1e9a2";
const korriban = "https://10.15.70.2:8006/";

/** One reachability entry, as the server reports it: checked a few minutes ago and still fresh unless said otherwise. */
function reach(kind: string, name: string, target: string, state: string, minutes: number, more: Partial<ReachabilityEntry> = {}): ReachabilityEntry {
  const usable = state === "reachable";
  return {
    kind, endpoint: target, context: kind === "guest-ssh" ? "tool" : "agent", image: kind === "guest-ssh" ? ansible : null, state,
    detail: null, checkedAt: ago(minutes), expiresAt: ago(minutes - 10), usable, probing: false,
    leafSha256: kind === "proxmox-api" && usable ? hex(`leaf-${name}`) : null, name, resourceId: null, ...more,
  };
}

export const latestAgentVersion = "0.8.1";

export const agents: Agent[] = [
  {
    id: "agent-pve01-01", name: "pve01-agent-01", labels: ["home"], maxConcurrency: 2, disabled: false, connected: true, enrolledAt: ago(60000),
    lastSeenAt: ago(0.05), version: "0.8.1", executors: ["opentofu", "ansible", "proxmox"], toolRuntime: "podman 5.6.2",
    certificateExpiresAt: ago(-120000), runningAttempts: 1, draining: false, reportsReachability: true,
    reachability: [
      reach("proxmox-api", "home/pve01", endpoint, "reachable", 2, { detail: "TLS handshake completed" }),
      reach("proxmox-api", "home/korriban", korriban, "timeout", 0.7, { detail: "connecting timed out" }),
      reach("snippets-ssh", "home/pve01", "ssh://10.15.60.2:22", "reachable", 2),
      reach("guest-ssh", "home/pve01/pve01-ai", "ssh://10.15.60.14:22", "reachable", 2, { resourceId: "pve01-ai" }),
      reach("guest-ssh", "home/pve01/dns", "ssh://10.15.60.21:22", "reachable", 6, { resourceId: "dns" }),
    ],
    work: [{ runId: "running", runNumber: 50, operation: "provision", address: "home/pve01/dns", step: "apply", status: "running", since: ago(3) }],
    guest: { resourceId: "bb", address: "home/pve01/bb", declaredVersion: "0.8.1" },
    update: { state: "current", targetVersion: null, reason: null, changeId: null, runId: null },
  },
  {
    id: "agent-pve01-02", name: "pve01-agent-02", labels: ["home", "gpu"], maxConcurrency: 1, disabled: false, connected: true, enrolledAt: ago(20000),
    lastSeenAt: ago(0.1), version: "0.7.1", executors: ["opentofu", "ansible", "proxmox"], toolRuntime: "podman 5.6.2",
    certificateExpiresAt: ago(-88000), runningAttempts: 0, draining: false, reportsReachability: true,
    reachability: [
      reach("proxmox-api", "home/korriban", korriban, "reachable", 1, { detail: "TLS handshake completed" }),
      reach("proxmox-api", "home/pve01", endpoint, "unreachable", 3, { detail: "no route to host" }),
      reach("guest-ssh", "home/pve01/media", "ssh://10.15.60.15:22", "reachable", 9, { resourceId: "media" }),
    ],
    work: [],
    guest: { resourceId: "dev", address: "home/pve01/dev", declaredVersion: "0.7.1" },
    update: { state: "available", targetVersion: latestAgentVersion, reason: null, changeId: null, runId: null },
  },
  {
    id: "agent-media", name: "media-agent", labels: [], maxConcurrency: 1, disabled: false, connected: true, enrolledAt: ago(150000),
    lastSeenAt: ago(0.2), version: "0.5.2", executors: ["opentofu", "proxmox"], toolRuntime: "local",
    certificateExpiresAt: ago(-30000), runningAttempts: 0, draining: true, reportsReachability: false, reachability: null,
    work: [],
    guest: { resourceId: "media", address: "home/pve01/media", declaredVersion: "0.5.2" },
    update: { state: "available", targetVersion: latestAgentVersion, reason: null, changeId: null, runId: null },
  },
  {
    id: "agent-laptop", name: "laptop", labels: ["dev"], maxConcurrency: 1, disabled: false, connected: false, enrolledAt: ago(90000),
    lastSeenAt: ago(4300), version: "0.6.4", executors: ["opentofu"], toolRuntime: "podman 5.4.0",
    certificateExpiresAt: ago(-1500), runningAttempts: 0, draining: false, reportsReachability: false, reachability: null,
    work: [],
    guest: null,
    update: {
      state: "unavailable", targetVersion: latestAgentVersion, changeId: null, runId: null,
      reason: "It does not run on a guest Octopushy manages, so it has to be updated on its own machine.",
    },
  },
];

export const tokens: EnrollmentToken[] = [
  { id: "token-2", name: "pve02-agent-01", labels: [], createdAt: ago(30), expiresAt: ago(-1410), usedAt: null, agentId: null },
  { id: "token-1", name: null, labels: [], createdAt: ago(3000), expiresAt: ago(2940), usedAt: null, agentId: null },
  { id: "token-0", name: "pve01-agent-02", labels: ["home", "gpu"], createdAt: ago(20010), expiresAt: ago(18570), usedAt: ago(20000), agentId: "agent-pve01-02" },
];

export const operatorTokens = [
  { id: "op-1", name: "laptop CLI", createdAt: ago(9000), expiresAt: ago(-34000), revokedAt: null as string | null },
];

// The variable store: secret values are never sent, as on the real server. Every key keeps its versions with their
// values, so saving, staging, making a version active and revoking click through.

type StoredVersion = VariableVersion & { value: string };

const outline = "proxmox/moraband/apps-mrb-01/outline";
const composeEnv = [
  "# outline on apps-mrb-01",
  "NODE_ENV=production",
  "URL=https://outline.testing.example.org",
  "PORT=3000",
  "SECRET_KEY=8f2c61d0a7b94e53c1d6f0a2b7e4938d",
  "UTILS_SECRET=41b7e09c5d2a6f83e7c0b1a94d5f2e68",
  "DATABASE_URL=postgres://outline:kV3n8QpLx2Tz@db:5432/outline",
  "REDIS_URL=redis://redis:6379",
  "OIDC_CLIENT_ID=outline",
  "OIDC_CLIENT_SECRET=Zr7mW2xKq9LdP4vT",
  "SMTP_HOST=smtp.example.org",
  "SMTP_PASSWORD=h4Jc9sNb2Yq",
].join("\n");
const oidcConfig = [
  "oidc:",
  "  issuer: https://auth.example.org",
  "  client_id: outline",
  "  client_secret: Zr7mW2xKq9LdP4vT",
  "  scopes: [openid, profile, email]",
  "storage:",
  "  bucket: outline-uploads",
  "  access_key: outline-uploads-rw",
  "  secret_key: m9Xc2vB7nQ4kL8pR",
].join("\n");

/** A key's versions, newest first. */
export const variableHistory = new Map<string, StoredVersion[]>();
/** The version each key uses, which stays the same when it is revoked. */
export const variableActive = new Map<string, number>();

function stored(version: number, value: string, status: string, minutes: number, note: string | null = null, usedBy: string[] = []): StoredVersion {
  const activated = status !== "candidate";
  return {
    version, status, createdAt: ago(minutes + 5), activatedAt: activated ? ago(minutes) : null, activatedBy: activated ? "ops" : null,
    activationNote: activated ? note : null, revokedAt: status === "revoked" ? ago(minutes - 60) : null, revokedBy: status === "revoked" ? "ops" : null,
    revocationNote: status === "revoked" ? "Pasted in a chat by mistake." : null, usedBy, value,
  };
}

/** Recomputes an entry's summary from its versions, as the server would answer it. */
export function summarizeVariable(entry: Variable) {
  const history = variableHistory.get(entry.key) ?? [];
  const active = history.find((v) => v.version === variableActive.get(entry.key));
  entry.version = active?.version ?? 0;
  entry.latestVersion = Math.max(entry.latestVersion, history[0]?.version ?? 0);
  entry.revoked = active?.status === "revoked";
  // A revoked version after the active one is no newer value to make active, as the server counts it.
  entry.newerVersion = history.find((v) => v.version > entry.version && v.status !== "revoked")?.version ?? null;
  const usable = active && active.status === "active" ? active.value : null;
  entry.lines = usable === null ? null : usable.split("\n").length - (usable.endsWith("\n") ? 1 : 0);
  entry.value = entry.visibility === "plain" ? usable : null;
  entry.updatedAt = active?.activatedAt ?? active?.createdAt ?? entry.updatedAt;
  for (const v of history) v.usedBy = v === active ? entry.usedBy : [];
}

const entry = (key: string, visibility: "secret" | "plain", usedBy: string[], version = 1, value: string | null = null, description: string | null = null): Variable => {
  const own = value ?? `mock-secret-of-${key}`;
  variableActive.set(key, version);
  variableHistory.set(key, Array.from({ length: version }, (_, i) => {
    const n = version - i;
    return stored(n, n === version ? own : `${own}-v${n}`, n === version ? "active" : "previous", 1000 * (i + 1), n > 1 && n === version ? "Rotated after the token audit." : null);
  }));
  const created: Variable = { key, visibility, description, version, updatedAt: ago(1000), value: null, usedBy, latestVersion: version, revoked: false, lines: null, newerVersion: null };
  summarizeVariable(created);
  return created;
};

/** A key with the versions given, newest first. */
const withHistory = (key: string, visibility: "secret" | "plain", usedBy: string[], description: string, versions: StoredVersion[]): Variable => {
  variableHistory.set(key, versions);
  variableActive.set(key, versions.find((v) => v.status === "active")?.version ?? 0);
  const created: Variable = { key, visibility, description, version: 0, updatedAt: ago(0), value: null, usedBy, latestVersion: 0, revoked: false, lines: null, newerVersion: null };
  summarizeVariable(created);
  return created;
};

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
    withHistory(`${outline}/compose_env`, "secret", ["apps-mrb-01/outline"], "Compose environment of Outline.", [
      stored(7, composeEnv, "active", 2880, "New SMTP password."),
      stored(6, composeEnv.replace("h4Jc9sNb2Yq", "Lq8vT3mZx1Rw"), "previous", 30240),
      stored(5, composeEnv.replace("h4Jc9sNb2Yq", "Lq8vT3mZx1Rw").replace("SMTP_HOST=smtp.example.org\n", ""), "previous", 86400),
      stored(4, composeEnv.replace("h4Jc9sNb2Yq", "Ab1cD2eF3gH4"), "revoked", 90000),
    ]),
    withHistory(`${outline}/oidc_config`, "secret", ["apps-mrb-01/outline"], "Sign-in and storage settings, mounted as a file.", [
      stored(4, oidcConfig.replace("m9Xc2vB7nQ4kL8pR", "p2Ws7nYq4kZb9Lx"), "candidate", 600),
      stored(3, oidcConfig, "active", 30240),
      stored(2, oidcConfig.replace("[openid, profile, email]", "[openid, email]"), "previous", 86400),
    ]),
    entry(`${outline}/db_password`, "secret", ["apps-mrb-01/outline"], 2, "kV3n8QpLx2Tz", "Password of the outline database user."),
    entry(`${outline}/smtp_password`, "secret", ["apps-mrb-01/outline"], 1, "h4Jc9sNb2Yq", "Password for outgoing mail."),
    entry(`${outline}/public_url`, "plain", ["apps-mrb-01/outline"], 1, "https://outline.testing.example.org", "Address people open Outline at."),
    entry("proxmox/moraband/apps-mrb-01/plane/db_password", "secret", ["apps-mrb-01/plane"], 1, "Qw8nR2xLm5Vt"),
    entry("proxmox/moraband/mrb-crt-01/traefik/dashboard_users", "secret", ["mrb-crt-01/traefik"], 1,
      "admin:$apr1$H6uskkkW$IgXLP6ewTrSuBkTrqE8wj/\nops:$apr1$Xq2vR7lm$1aWkF0nqWnS4yPq7B8b1c/", "Basic auth users of the Traefik dashboard."),
    entry("shared/acme_email", "plain", ["mrb-crt-01/traefik"], 1, "ops@example.org"),
  ],
  missing: [
    { key: "proxmox/pve01/bb/machine_tools_token", usedBy: ["home/pve01/bb"] },
    { key: "proxmox/moraband/apps-mrb-01/plane/s3_secret", usedBy: ["apps-mrb-01/plane"] },
  ],
  system: [
    { key: "system/agent-ca/certificate", visibility: "plain", updatedAt: ago(90000) },
    { key: "system/agent-ca/private-key", visibility: "secret", updatedAt: ago(90000) },
  ],
};

export function variableVersions(key: string): VariableVersion[] {
  return (variableHistory.get(key) ?? []).map(({ value: _value, ...version }) => version);
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
