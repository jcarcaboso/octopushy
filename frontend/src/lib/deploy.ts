/**
 * What the workflow wizard reads from a workflow: its versions per major, the starting value of its form, and the `ui` hints
 * that ask for generated values. Nothing here knows a particular workflow: see docs/workflows.md, "Generated values".
 */
import type { WorkflowSummary, WorkflowVersion } from "../api/types";
import { initialValue, isObject, kindOf, requiredOf, resolve, type JsonObject, type JsonValue, type Schema } from "./jsonSchema";

/** A workflow's newest usable release in each major, newest major first. */
export function newestPerMajor(workflow: WorkflowSummary): WorkflowVersion[] {
  const byMajor = new Map<number, WorkflowVersion>();
  for (const version of workflow.versions) {
    if (!version.usable) continue;
    const major = majorOf(version.version);
    const known = byMajor.get(major);
    if (!known || compareVersions(version.version, known.version) > 0) byMajor.set(major, version);
  }
  return [...byMajor.values()].sort((a, b) => compareVersions(b.version, a.version));
}

export function majorOf(version: string): number {
  return Number.parseInt(version.split(".")[0] ?? "0", 10);
}

function compareVersions(a: string, b: string): number {
  const [am = 0, an = 0] = a.split(".").map((p) => Number.parseInt(p, 10));
  const [bm = 0, bn = 0] = b.split(".").map((p) => Number.parseInt(p, 10));
  return am - bm || an - bn;
}

/** The reference a deploy writes: `id@M` by default, `id@M.m` when pinned. */
export function referenceOf(version: WorkflowVersion, pinned: boolean): string {
  return pinned ? `${version.id}@${version.version}` : `${version.id}@${majorOf(version.version)}`;
}

/**
 * A new form's value from its schema: each property with a `default`, and each required one, filled in to any depth. An array
 * with `minItems` starts with that many items.
 */
export function defaultsOf(schema: Schema, root: Schema = schema): JsonValue | undefined {
  const resolved = resolve(schema, root);
  if ("default" in resolved) return structuredClone(resolved.default as JsonValue);
  if ("const" in resolved) return structuredClone(resolved.const as JsonValue);
  const { kind } = kindOf(resolved);
  if (kind === "object" && isObject(resolved.properties)) {
    const value: JsonObject = {};
    const required = requiredOf(resolved);
    for (const [key, property] of Object.entries(resolved.properties)) {
      const child = defaultsOf(property as Schema, root);
      if (child === undefined) continue;
      // A required property is there; an optional one only with a default, or an object something inside it fills.
      const filledObject = isObject(child) && Object.keys(child).length > 0;
      if (required.includes(key) || "default" in resolve(property as Schema, root) || filledObject) value[key] = child;
    }
    return value;
  }
  if (kind === "array") {
    const count = typeof resolved.minItems === "number" ? resolved.minItems : 0;
    const items = isObject(resolved.items) ? (resolved.items as Schema) : {};
    return Array.from({ length: count }, () => defaultsOf(items, root) ?? initialValue(items, root) ?? null);
  }
  return undefined;
}

/** A secret's hint in `ui.secrets`: the generator it asks for, and where an enrollment token's labels and name come from. */
export interface SecretHint {
  generate?: string;
  /** A dotted path into the form's value, such as `inputs.worker_agent_labels`. */
  labels?: string;
  name?: string;
  label?: string;
  help?: string;
}

/** What a workflow's `ui` asks of a new guest or service beyond its form. Every hint is optional. */
export interface DeployHints {
  /** `{ generate: next-vmid, from }`: propose the next free VMID at or above `from` (100 by default). */
  vmid?: { generate?: string; from?: number };
  /** The template a new guest starts with. */
  template?: string;
  /** The login a new guest's setup stage uses. */
  accessUser?: string;
  secrets: Record<string, SecretHint>;
}

export const generators = {
  nextVmid: "next-vmid",
  agentEnrollmentToken: "agent-enrollment-token",
  randomPassword: "random-password",
} as const;

export function readDeployHints(ui: unknown): DeployHints {
  const hints: DeployHints = { secrets: {} };
  if (!isObject(ui)) return hints;
  if (isObject(ui.vmid)) {
    hints.vmid = {
      generate: typeof ui.vmid.generate === "string" ? ui.vmid.generate : undefined,
      from: typeof ui.vmid.from === "number" ? ui.vmid.from : undefined,
    };
  }
  if (typeof ui.template === "string") hints.template = ui.template;
  if (isObject(ui.access) && typeof ui.access.user === "string") hints.accessUser = ui.access.user;
  if (isObject(ui.secrets)) {
    for (const [name, value] of Object.entries(ui.secrets)) {
      if (!isObject(value)) continue;
      const hint: SecretHint = {};
      for (const key of ["generate", "labels", "name", "label", "help"] as const) if (typeof value[key] === "string") hint[key] = value[key] as string;
      hints.secrets[name] = hint;
    }
  }
  return hints;
}

/** The value at a dotted path of the form, such as `inputs.worker_agent_labels`. */
export function valueAt(value: JsonObject, path: string | undefined): JsonValue | undefined {
  if (!path) return undefined;
  let node: JsonValue | undefined = value;
  for (const part of path.split(".")) {
    if (!isObject(node)) return undefined;
    node = node[part];
  }
  return node;
}

export const namePattern = /^[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?$/;
