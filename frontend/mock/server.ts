// A mock of Octopushy's API for UI work: realistic data, no backend, database or agent. See README.md.
// Run it with `just mock`, or `node mock/server.ts` from frontend/ (MOCK_PORT sets the port).
import { createServer, type IncomingMessage, type ServerResponse } from "node:http";
import type {
  Agent, AuthState, Change, ChangePreview, CreatedToken, DraftValidation, InstallationTrust, LogChunk, Problem, Repository, RunDetail,
  SnippetAccess,
} from "../src/api/types.ts";
import * as data from "./data.ts";
import { Refused, approve, cancel, forceStop, headCommit, hex, logChunk, reject, resources, runs, startRun, summary, type Resource } from "./runs.ts";
import { uptime } from "./uptime.ts";

const port = Number(process.env.MOCK_PORT ?? 18280);
let user: string | null = "ops";

// Routing

interface Request {
  params: Record<string, string>;
  query: URLSearchParams;
  body: Record<string, unknown>;
  /** The body as sent, and its Content-Type, for an upload. */
  raw: Buffer;
  type: string;
}

/** A reply with a status other than 200, or no body. */
class Reply {
  readonly status: number;
  readonly body: unknown;
  constructor(status: number, body?: unknown) {
    this.status = status;
    this.body = body;
  }
}

/** A reply that is a file, not JSON. */
class FileReply {
  readonly type: string;
  readonly bytes: Uint8Array | string;
  constructor(type: string, bytes: Uint8Array | string) {
    this.type = type;
    this.bytes = bytes;
  }
}

const problem = (status: number, title: string) => new Reply(status, { status, title } satisfies Problem);
const notFound = (what: string) => problem(404, `The ${what} does not exist.`);

type Handler = (request: Request) => unknown;
const routes: { method: string; pattern: RegExp; keys: string[]; handler: Handler }[] = [];

function on(method: string, path: string, handler: Handler) {
  const keys: string[] = [];
  const pattern = new RegExp(`^${path.replace(/\{(\w+)\}/g, (_, key: string) => {
    keys.push(key);
    return "([^/]+)";
  })}$`);
  routes.push({ method, pattern, keys, handler });
}

const resourceById = (id: string) => Object.values(resources).find((r) => r.id === id) as Resource | undefined;
const runById = (id: string) => runs.find((r) => r.run.id === id);
const started = (runId: string) => ({ runId });

// Account

const authState = (): AuthState => ({ setupRequired: false, authenticated: user !== null, username: user });
on("GET", "/api/auth/state", authState);
// Any user name and password sign in.
const signIn = ({ body }: Request) => {
  user = String(body.username || "ops");
  return authState();
};
on("POST", "/api/auth/login", signIn);
on("POST", "/api/auth/setup", signIn);
on("POST", "/api/auth/logout", () => {
  user = null;
});
on("GET", "/api/auth/operator-tokens", () => data.operatorTokens);
on("POST", "/api/auth/operator-tokens", ({ body }) => {
  const days = Number(body.expiresInDays ?? 30);
  const token = { id: `op-${data.operatorTokens.length + 1}`, name: String(body.name), createdAt: new Date().toISOString(),
    expiresAt: new Date(Date.now() + days * 86_400_000).toISOString(), revokedAt: null };
  data.operatorTokens.push(token);
  return { id: token.id, token: `octo_op_${hex(token.id, 40)}`, expiresAt: token.expiresAt };
});
on("DELETE", "/api/auth/operator-tokens/{id}", ({ params }) => {
  const token = data.operatorTokens.find((t) => t.id === params.id);
  if (!token) return notFound("token");
  token.revokedAt = new Date().toISOString();
});

// Reads of the lab

on("GET", "/api/overview", data.overview);
on("GET", "/api/inventory", data.inventory);
on("GET", "/api/uptime", () => uptime(data.inventory()));
on("GET", "/api/guests/{id}", ({ params }) => data.guestDetail(params.id) ?? notFound("guest"));
on("GET", "/api/services/{id}", ({ params }) => data.serviceDetail(params.id) ?? notFound("service"));
on("GET", "/api/installations/{id}", ({ params }) => data.installationDetail(params.id) ?? notFound("installation"));
on("GET", "/api/workflows", () => data.workflows);

// Service icons: uploads and choices are kept in memory; the predefined files are drawn from their names.

const serviceExists = (id: string) => Boolean(data.serviceDetail(id));
on("GET", "/api/icons", () => ({ names: data.iconNames }));
on("GET", "/api/icons/{name}", ({ params }) =>
  data.iconNames.includes(params.name) ? new FileReply("image/svg+xml", data.iconSvg(params.name)) : notFound("icon"));
on("GET", "/api/services/{id}/icon", ({ params }) => {
  const own = data.ownIcons.get(params.id);
  return own && "picture" in own ? new FileReply(own.picture.type, own.picture.bytes) : notFound("icon");
});
on("PUT", "/api/services/{id}/icon/picture", ({ params, raw, type }) => {
  if (!serviceExists(params.id)) return notFound("service");
  if (!["image/png", "image/jpeg", "image/webp", "image/svg+xml"].includes(type)) return problem(415, "Send a PNG, JPEG, WebP or SVG image.");
  if (raw.length > 1024 * 1024) return problem(413, "The picture is larger than 1 MiB.");
  data.ownIcons.set(params.id, { picture: { type, bytes: raw, version: Date.now() } });
});
on("PUT", "/api/services/{id}/icon/predefined", ({ params, body }) => {
  if (!serviceExists(params.id)) return notFound("service");
  const name = String(body.name ?? "");
  if (!data.iconNames.includes(name)) return problem(400, `There is no icon ${name} in the icons directory.`);
  data.ownIcons.set(params.id, { chosen: name });
});
on("DELETE", "/api/services/{id}/icon", ({ params }) => {
  if (!serviceExists(params.id)) return notFound("service");
  data.ownIcons.delete(params.id);
});

// Starting runs: they stay queued, since nothing executes them.

function startFor(id: string, kind: Resource["kind"], operation: string) {
  const resource = resourceById(id);
  if (!resource || resource.kind !== kind) return notFound(kind);
  const busy = runs.find((r) => r.run.resourceId === id && !["succeeded", "failed", "cancelled"].includes(r.run.status));
  if (busy) return problem(409, `Run #${busy.run.number} is still going for ${resource.address}; it has to finish first.`);
  return started(startRun(resource, operation));
}

on("POST", "/api/guests/{id}/plan", ({ params }) => startFor(params.id, "guest", "provision"));
on("POST", "/api/guests/{id}/configure", ({ params }) => startFor(params.id, "guest", "configure"));
on("POST", "/api/guests/{id}/access/rotate", ({ params }) => startFor(params.id, "guest", "rotate-access"));
on("POST", "/api/services/{id}/deploy", ({ params }) => startFor(params.id, "service", "pipeline"));
on("POST", "/api/installations/{id}/observe", () => undefined);
on("POST", "/api/installations/{id}/trust", ({ body }) => ({
  endpoints: (body.endpoints as string[]) ?? [], untrusted: [], trustedAt: new Date().toISOString(), trustedBy: user ?? "ops", managementChanged: false,
}) satisfies InstallationTrust);
on("POST", "/api/installations/{id}/snippets-key", () => ({
  publicKey: `ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAI${hex("snippets", 43)} octopushy-snippets@pve01`, fingerprint: "SHA256:snippets",
  createdAt: new Date().toISOString(), declared: false, datastore: "local", user: "octopushy-snippets",
  hosts: [{ host: "pve01", address: "10.15.60.2", port: 22, hostKeyStore: "proxmox/pve01/host-key", hostKeySet: true }],
  command: "pveum user add octopushy-snippets@pam",
}) satisfies SnippetAccess);

// Runs

on("GET", "/api/runs", ({ query }) => {
  const status = query.get("status");
  const limit = Number(query.get("limit") ?? 100);
  return runs.filter((r) => !status || r.run.status === status).slice(0, limit).map(summary);
});
on("GET", "/api/runs/{id}", ({ params }) => runById(params.id) ?? notFound("run"));
on("POST", "/api/runs/{id}/steps/{stepId}/approve", ({ params, body }) =>
  approve(params.id, params.stepId, user ?? "ops", (body.note as string) ?? null, (body.planSha256 as string) ?? null));
on("POST", "/api/runs/{id}/steps/{stepId}/reject", ({ params, body }) => reject(params.id, params.stepId, user ?? "ops", (body.note as string) ?? null));
on("POST", "/api/runs/{id}/cancel", ({ params }) => cancel(params.id, user ?? "ops"));
on("POST", "/api/runs/{id}/force-stop", ({ params }) => forceStop(params.id, user ?? "ops"));
on("POST", "/api/runs/{id}/close", () => problem(409, "Only a run with an uncertain outcome can be closed."));
on("POST", "/api/runs/{id}/retry-stage", () => undefined);
on("POST", "/api/runs/{id}/run-again", ({ params }) => {
  const detail = runById(params.id) as RunDetail | undefined;
  if (!detail) return notFound("run");
  return startFor(detail.run.resourceId, detail.run.resourceKind as Resource["kind"], detail.run.operation);
});
on("GET", "/api/attempts/{id}/log", ({ params, query }) =>
  (logChunk(params.id, Number(query.get("after") ?? -1), Number(query.get("limit") ?? 2000)) satisfies LogChunk | null) ?? notFound("attempt"));

// Agents

on("GET", "/api/agents", () => data.agents);
on("PATCH", "/api/agents/{id}", ({ params, body }) => {
  const agent = data.agents.find((a) => a.id === params.id);
  if (!agent) return notFound("agent");
  for (const key of ["labels", "maxConcurrency", "disabled", "draining"] as const) {
    if (body[key] !== null && body[key] !== undefined) Object.assign(agent, { [key]: body[key] });
  }
});
on("DELETE", "/api/agents/{id}", ({ params }) => {
  const i = data.agents.findIndex((a) => a.id === params.id);
  if (i < 0) return notFound("agent");
  data.agents.splice(i, 1);
});
// Updates run one at a time, as on the server: the agent is paused while it updates, the others wait in the queue.
const updateSeconds = Number(process.env.MOCK_UPDATE_SECONDS ?? 8);
const pausedBefore = new Map<string, boolean>();

function startUpdate(agent: Agent) {
  const guest = agent.guest ? resourceById(agent.guest.resourceId) : undefined;
  const runId = guest ? startRun(guest, "configure") : null;
  pausedBefore.set(agent.id, agent.draining);
  Object.assign(agent, { draining: true });
  agent.update = { state: "updating", targetVersion: data.latestAgentVersion, reason: null, changeId: null, runId };
  setTimeout(() => {
    const run = runs.find((r) => r.run.id === runId);
    if (run) Object.assign(run.run, { status: "succeeded", finishedAt: new Date().toISOString(), waiting: null });
    Object.assign(agent, { version: data.latestAgentVersion, draining: pausedBefore.get(agent.id) ?? false });
    if (agent.guest) agent.guest.declaredVersion = data.latestAgentVersion;
    agent.update = { state: "current", targetVersion: null, reason: null, changeId: null, runId: null };
    const next = data.agents.find((a) => a.update.state === "queued");
    if (next) startUpdate(next);
  }, updateSeconds * 1000);
}

function requestUpdate(agent: Agent): Reply | undefined {
  if (agent.update.state === "current") return problem(409, `${agent.name} already runs the latest version.`);
  if (agent.update.state === "unavailable") return problem(409, agent.update.reason ?? `${agent.name} cannot be updated from here.`);
  if (agent.update.state === "queued" || agent.update.state === "updating") return problem(409, `${agent.name} is already being updated.`);
  if (data.agents.some((a) => a.update.state === "updating")) {
    agent.update = { ...agent.update, state: "queued", targetVersion: data.latestAgentVersion, reason: null };
  } else {
    startUpdate(agent);
  }
}

on("POST", "/api/agents/{id}/update", ({ params }) => {
  const agent = data.agents.find((a) => a.id === params.id);
  return agent ? requestUpdate(agent) : notFound("agent");
});
on("DELETE", "/api/agents/{id}/update", ({ params }) => {
  const agent = data.agents.find((a) => a.id === params.id);
  if (!agent) return notFound("agent");
  if (agent.update.state !== "queued") return problem(409, `${agent.name} is not waiting for an update.`);
  agent.update = { state: "available", targetVersion: data.latestAgentVersion, reason: null, changeId: null, runId: null };
});
on("POST", "/api/agents/update", () => {
  for (const agent of data.agents.filter((a) => a.update.state === "available" || a.update.state === "failed")) requestUpdate(agent);
});
on("GET", "/api/agents/tokens", () => data.tokens);
on("POST", "/api/agents/tokens", ({ body }) => {
  const id = `token-${Date.now()}`;
  const expiresAt = new Date(Date.now() + Number(body.expiresInMinutes ?? 1440) * 60_000).toISOString();
  data.tokens.unshift({ id, name: (body.name as string) ?? null, labels: (body.labels as string[] | undefined) ?? [], createdAt: new Date().toISOString(), expiresAt, usedAt: null, agentId: null });
  const token = `octo_enroll_${hex(id, 32)}`;
  return { id, token, expiresAt, caFingerprint: `SHA256:${hex("agent-ca", 43)}`,
    command: `octopushy agent enroll --server https://octopushy.home.example.org:8443 --token ${token}` } satisfies CreatedToken;
});
on("DELETE", "/api/agents/tokens/{id}", ({ params }) => {
  const i = data.tokens.findIndex((t) => t.id === params.id);
  if (i < 0) return notFound("token");
  data.tokens.splice(i, 1);
});

// The variable store

on("GET", "/api/variables", () => data.variables);
on("GET", "/api/variables/versions", ({ query }) => data.variableVersions(query.get("key") ?? ""));
const now = () => new Date().toISOString();
const findVariable = (key: unknown) => data.variables.entries.find((v) => v.key === key);
/** The version that is active now, or 0 for a key that does not exist yet; as the server compares it. */
const activeNow = (key: string) => (findVariable(key) ? (data.variableActive.get(key) ?? 0) : 0);
function newVersion(key: string, value: string, status: "active" | "candidate", note: unknown) {
  const history = data.variableHistory.get(key) ?? [];
  const version = (history[0]?.version ?? 0) + 1;
  const active = status === "active";
  if (active) for (const v of history) if (v.status === "active") v.status = "previous";
  history.unshift({ version, status, createdAt: now(), activatedAt: active ? now() : null, activatedBy: active ? user : null,
    activationNote: active && typeof note === "string" ? note : null, revokedAt: null, revokedBy: null, revocationNote: null, usedBy: [], value });
  data.variableHistory.set(key, history);
  if (active) data.variableActive.set(key, version);
  return version;
}
on("PUT", "/api/variables", ({ body }) => {
  const key = String(body.key);
  const existing = findVariable(key);
  const expected = body.expectedCurrentVersion;
  if (typeof expected === "number" && expected !== activeNow(key)) {
    return problem(409, expected === 0 ? `'${key}' is already set.` : `v${activeNow(key)} of '${key}' became active meanwhile.`);
  }
  const visibility = (body.visibility as string | null) ?? existing?.visibility ?? "secret";
  const entry = existing ?? { key, visibility, description: null, version: 0, latestVersion: 0, updatedAt: now(), value: null, usedBy: [], revoked: false, lines: null };
  entry.description = (body.description as string | null) ?? null;
  if (typeof body.value === "string") {
    entry.visibility = visibility;
    newVersion(key, body.value, "active", body.note);
  }
  if (!existing) data.variables.entries.push(entry);
  data.summarizeVariable(entry);
  data.variables.missing = data.variables.missing.filter((m) => m.key !== key);
});
on("DELETE", "/api/variables", ({ query }) => {
  const key = query.get("key") ?? "";
  data.variables.entries = data.variables.entries.filter((v) => v.key !== key);
  data.variableActive.delete(key);
});
on("POST", "/api/variables/reveal", ({ body }) => ({
  values: (body.keys as string[]).map((key) => {
    const active = (data.variableHistory.get(key) ?? []).find((v) => v.version === data.variableActive.get(key) && v.status === "active");
    return { key, value: active?.value ?? null };
  }),
}));
on("POST", "/api/variables/candidates", ({ body }) => {
  const existing = findVariable(body.key);
  if (!existing) return notFound("key");
  const version = newVersion(existing.key, String(body.value), "candidate", null);
  data.summarizeVariable(existing);
  return { version };
});
on("POST", "/api/variables/promote", ({ body }) => {
  const existing = findVariable(body.key);
  if (!existing) return notFound("key");
  if (body.expectedCurrentVersion !== activeNow(existing.key)) return problem(409, `v${activeNow(existing.key)} of '${existing.key}' became active meanwhile.`);
  const history = data.variableHistory.get(existing.key) ?? [];
  const target = history.find((v) => v.version === body.version);
  if (!target || target.status === "revoked") return problem(400, `v${String(body.version)} cannot be made active.`);
  for (const v of history) if (v.status === "active") v.status = "previous";
  Object.assign(target, { status: "active", activatedAt: now(), activatedBy: user, activationNote: typeof body.verificationNote === "string" ? body.verificationNote : null });
  data.variableActive.set(existing.key, target.version);
  data.summarizeVariable(existing);
});
on("POST", "/api/variables/revoke", ({ body }) => {
  const existing = findVariable(body.key);
  const target = existing && (data.variableHistory.get(existing.key) ?? []).find((v) => v.version === body.version);
  if (!existing || !target) return notFound("version");
  Object.assign(target, { status: "revoked", revokedAt: now(), revokedBy: user, revocationNote: String(body.reason) });
  data.summarizeVariable(existing);
});

// The lab repository

on("GET", "/api/repository", () => data.repository);
on("PUT", "/api/repository", ({ body }) => Object.assign(data.repository, {
  url: body.url ?? data.repository.url, branch: body.branch ?? data.repository.branch, pollSeconds: body.pollSeconds ?? data.repository.pollSeconds,
  credentialKind: body.credentialKind ?? data.repository.credentialKind, credentialUsername: body.credentialUsername ?? null,
}) satisfies Repository);
on("POST", "/api/repository/sync", () => new Reply(202));
on("GET", "/api/repository/revisions", ({ query }) => data.revisions.slice(0, Number(query.get("limit") ?? 50)));
on("GET", "/api/repository/revisions/{id}", ({ params }) => data.revisionDetail(params.id) ?? notFound("revision"));

// Drafts

on("GET", "/api/repository/drafts", () => [...data.drafts.keys()].map((id) => data.draftDetail(id)!.draft));
on("POST", "/api/repository/drafts", ({ body }) => data.draftDetail(data.newDraft(String(body.title || "Untitled draft"))));
on("GET", "/api/repository/drafts/{id}", ({ params }) => data.draftDetail(params.id) ?? notFound("draft"));
on("DELETE", "/api/repository/drafts/{id}", ({ params }) => void data.drafts.delete(params.id));
on("GET", "/api/repository/drafts/{id}/file", ({ params, query }) => data.draftFile(params.id, query.get("path") ?? "") ?? notFound("draft"));
on("POST", "/api/repository/drafts/{id}/changes", ({ params, body }) => {
  if (!data.drafts.has(params.id)) return notFound("draft");
  data.changeDraft(params.id, body.changes as { path: string; content: string | null }[]);
  return data.draftDetail(params.id);
});
on("POST", "/api/repository/drafts/{id}/update", ({ params }) => data.draftDetail(params.id) ?? notFound("draft"));
on("POST", "/api/repository/drafts/{id}/validate", () =>
  ({ comparedWith: headCommit, valid: true, newIssues: [], formatIssues: [] }) satisfies DraftValidation);
on("POST", "/api/repository/drafts/{id}/publish", ({ params }) => {
  const draft = data.drafts.get(params.id);
  if (!draft) return notFound("draft");
  const commit = hex(`published-${params.id}`, 40);
  Object.assign(draft.summary, { status: "published", publishedCommit: commit, publishedAt: new Date().toISOString(), publishedBy: user });
  return { commit, draft: draft.summary };
});

// Changes from forms and the deploy wizard

on("GET", "/api/changes", ({ query }) => {
  const id = query.get("resourceId");
  return data.changes().filter((c) => !id || c.resourceId === id).slice(0, Number(query.get("limit") ?? 20));
});
on("GET", "/api/changes/{id}", ({ params }) => data.changes().find((c) => c.id === params.id) ?? notFound("change"));
on("POST", "/api/changes/{id}/dismiss", ({ params }) => data.changes().find((c) => c.id === params.id) ?? notFound("change"));
on("GET", "/api/changes/form/{resourceId}", ({ params }) => data.changeForm(params.resourceId) ?? notFound("guest or service"));

/** A diff of each section that differs, as YAML-ish JSON lines. */
function sectionDiff(file: string, before: Record<string, unknown>, after: Record<string, unknown>) {
  const lines = [`diff --git a/${file} b/${file}`, `--- a/${file}`, `+++ b/${file}`];
  for (const key of Object.keys(after)) {
    if (after[key] === null || after[key] === undefined || JSON.stringify(after[key]) === JSON.stringify(before[key])) continue;
    lines.push(`@@ ${key} @@`);
    for (const l of JSON.stringify(before[key] ?? null, null, 2).split("\n")) lines.push(`-${key}: ${l}`);
    for (const l of JSON.stringify(after[key], null, 2).split("\n")) lines.push(`+${key}: ${l}`);
  }
  return lines.join("\n") + "\n";
}

function changePreview(body: Record<string, unknown>): ChangePreview | Reply {
  const form = data.changeForm(String(body.resourceId));
  if (!form) return notFound("guest or service");
  const kind = String(body.kind ?? "edit");
  const subject = kind === "remove" ? `${form.name}: remove` : `${form.name}: edit from the form`;
  return {
    kind, address: form.address, subject, message: `${subject}\n\nOctopushy-Change: <given when committed>\n`, baseCommit: headCommit,
    files: [form.file!],
    diff: kind === "remove" ? `diff --git a/${form.file} b/${form.file}\ndeleted file mode 100644\n` : sectionDiff(form.file!, form as never, body),
    valid: true, issues: [], fieldIssues: [], refused: null,
  };
}

function commit(preview: ChangePreview, resourceId: string | null, resourceKind: string): Change {
  const now = new Date().toISOString();
  const change: Change = {
    id: `change-${data.madeChanges.length + 1}`, kind: preview.kind, resourceKind, resourceId, address: preview.address, author: user ?? "ops",
    message: preview.subject, baseCommit: preview.baseCommit, commit: hex(`commit-${now}`, 40), commitUrl: null, revisionId: null,
    status: "committed", detail: "Committed; the mock server never syncs it, so it stays here.", files: preview.files, runs: [], createdAt: now,
    syncedAt: null, updatedAt: now, final: false, notCommitted: false, cleanupPending: 0,
  };
  data.madeChanges.unshift(change);
  return change;
}

on("POST", "/api/changes/preview", ({ body }) => changePreview(body));
on("POST", "/api/changes", ({ body }) => {
  const preview = changePreview(body);
  if (preview instanceof Reply) return preview;
  return commit(preview, String(body.resourceId), data.changeForm(String(body.resourceId))!.resourceKind);
});

on("GET", "/api/deploy/targets", data.deployTargets);
on("GET", "/api/deploy/vmid", ({ query }) => {
  const from = Number(query.get("from") ?? 100);
  return { installation: query.get("installation") ?? "home/pve01", vmid: data.nextVmid(from), from };
});

function deployPreview(body: Record<string, unknown>): ChangePreview {
  const name = String(body.name);
  const service = body.kind === "create-service";
  const file = service ? `labs/home/proxmox/pve01/guests/${String(body.parent).split("/").pop()}/services/${name}/service.yaml`
    : `labs/home/proxmox/pve01/guests/${name}/guest.yaml`;
  const yaml = service ? ["schemaVersion: 1", `workflow: ${String(body.workflow)}`]
    : ["schemaVersion: 1", "type: vm", `host: ${String(body.host ?? "pve01")}`, `vmid: ${String(body.vmid ?? data.nextVmid(100))}`, `workflow: ${String(body.workflow)}`];
  const subject = `${name}: deploy ${String(body.workflow)} on ${String(body.parent)}`;
  return {
    kind: String(body.kind), address: `${String(body.parent)}/${name}`, subject, message: `${subject}\n\nOctopushy-Change: <given when committed>\n`,
    baseCommit: headCommit, files: [file],
    diff: [`diff --git a/${file} b/${file}`, "new file mode 100644", "--- /dev/null", `+++ b/${file}`, `@@ -0,0 +1,${yaml.length} @@`, ...yaml.map((l) => `+${l}`)].join("\n") + "\n",
    valid: true, issues: [], fieldIssues: [], refused: null, generated: [], nextVmid: null,
    reviewedRelease: `${String(body.workflow)} at ${hex(String(body.workflow), 40)}`, reviewDigest: hex(`review-${name}`),
  };
}

on("POST", "/api/deploy/preview", ({ body }) => deployPreview(body));
on("POST", "/api/deploy", ({ body }) => commit(deployPreview(body), null, body.kind === "create-service" ? "service" : "guest"));

// The server

async function readRaw(request: IncomingMessage): Promise<Buffer> {
  const chunks: Buffer[] = [];
  for await (const chunk of request) chunks.push(chunk as Buffer);
  return Buffer.concat(chunks);
}

function parseBody(raw: Buffer): Record<string, unknown> {
  const text = raw.toString("utf8");
  if (!text) return {};
  try {
    const parsed: unknown = JSON.parse(text);
    return parsed && typeof parsed === "object" ? (parsed as Record<string, unknown>) : {};
  } catch {
    return {};
  }
}

function send(response: ServerResponse, status: number, body: unknown) {
  if (body instanceof FileReply) {
    response.writeHead(status, { "content-type": body.type }).end(body.bytes);
    return;
  }
  if (body === undefined) {
    response.writeHead(status).end();
    return;
  }
  const problemBody = status >= 400;
  response.writeHead(status, { "content-type": problemBody ? "application/problem+json" : "application/json" }).end(JSON.stringify(body));
}

async function handle(request: IncomingMessage, response: ServerResponse) {
  const url = new URL(request.url ?? "/", "http://mock");
  const method = request.method ?? "GET";
  const begin = performance.now();
  let status = 500;
  try {
    const route = routes.map((r) => ({ r, m: r.method === method ? r.pattern.exec(url.pathname) : null })).find((x) => x.m);
    if (!route) {
      status = 404;
      console.log(`  no mock for ${method} ${url.pathname}: add it in mock/server.ts`);
      send(response, 404, { status: 404, title: `The mock server has no route for ${method} ${url.pathname}.` } satisfies Problem);
      return;
    }
    // Every route but the account ones needs a session, as on the real server.
    if (user === null && !url.pathname.startsWith("/api/auth/")) {
      status = 401;
      send(response, 401, { status: 401, title: "Sign in to continue." } satisfies Problem);
      return;
    }
    const params = Object.fromEntries(route.r.keys.map((key, i) => [key, decodeURIComponent(route.m![i + 1])]));
    const raw = method === "GET" ? Buffer.alloc(0) : await readRaw(request);
    const type = (request.headers["content-type"] ?? "").split(";")[0].trim();
    const result = route.r.handler({ params, query: url.searchParams, body: type.startsWith("image/") ? {} : parseBody(raw), raw, type });
    const reply = result instanceof Reply ? result : result instanceof FileReply ? new Reply(200, result) : result === undefined ? new Reply(204) : new Reply(200, result);
    status = reply.status;
    send(response, reply.status, reply.body);
  } catch (error) {
    if (error instanceof Refused) {
      status = error.status;
      send(response, status, { status, title: error.message } satisfies Problem);
    } else {
      console.error(error);
      send(response, 500, { status: 500, title: "The mock server failed; see its console." } satisfies Problem);
    }
  } finally {
    console.log(`${method.padEnd(6)} ${url.pathname}${url.search} ${status} ${Math.round(performance.now() - begin)}ms`);
  }
}

createServer((request, response) => void handle(request, response)).listen(port, "127.0.0.1", () => {
  console.log(`Octopushy mock API on http://127.0.0.1:${port} (signed in as ${user}); see frontend/mock/README.md`);
});
