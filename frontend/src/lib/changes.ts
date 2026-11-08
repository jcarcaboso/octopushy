import type { ConfigurePreview, Step } from "../api/types";
import { summarizePlan, type PlanChange, type PlanSummary } from "./plan";

/** The approval overview: one small tile per kind of change, each with the rows its detail lists. */

export type TileTone = "neutral" | "add" | "remove" | "warn" | "danger";
export type RowMark = "+" | "~" | "−" | "±" | "";

export interface Row {
  mark: RowMark;
  name: string;
  before?: string;
  after?: string;
  note?: string;
}

export interface Tile {
  key: string;
  label: string;
  /** Short text; " → " separates an old value from the new one. */
  value: string;
  sub: string;
  tone: TileTone;
  /** What the detail is about, such as the resource address. */
  heading?: string;
  rows: Row[];
  unchanged: Row[];
}

export interface OverviewInput {
  plans: PlanSummary[];
  previews: ConfigurePreview[];
}

type Attribute = PlanChange["attributes"][number];

const actionLabels: Record<PlanChange["kind"], string> = {
  create: "create",
  update: "update in place",
  delete: "delete",
  replace: "replace",
  read: "read",
  "no-op": "no change",
  other: "other",
};

const destructive = (change: PlanChange) => change.kind === "delete" || change.kind === "replace";

function scalar(value: unknown): string | undefined {
  if (value === null || value === undefined) return undefined;
  if (typeof value === "string") return value;
  if (typeof value === "number" || typeof value === "boolean") return String(value);
  return undefined;
}

function strings(value: unknown): string[] {
  return Array.isArray(value) ? value.filter((item): item is string => typeof item === "string") : [];
}

function transition(before: string | undefined, after: string | undefined, unit = ""): string {
  const suffix = unit ? ` ${unit}` : "";
  if (before !== undefined && after !== undefined) return `${before} → ${after}${suffix}`;
  return `${after ?? before ?? "—"}${suffix}`;
}

function withUnit(value: string | undefined, unit: string) {
  return value === undefined ? undefined : `${value} ${unit}`;
}

function mark(before: unknown, after: unknown): RowMark {
  if (before === null || before === undefined) return "+";
  if (after === null || after === undefined) return "−";
  return "~";
}

function attributeTile(change: PlanChange, attribute: Attribute): Tile {
  const key = `${change.address}:${attribute.name}`;
  const heading = `${change.address} · ${actionLabels[change.kind]}`;
  const danger = destructive(change);
  const resourceNote = danger ? actionLabels[change.kind] : undefined;
  const parts = attribute.name.split(".");
  let label = attribute.name;
  let unit = "";
  let rowName = attribute.name;
  let sub = "";
  let before = scalar(attribute.before);
  let after = scalar(attribute.after);
  let tone: TileTone = danger ? "danger" : "neutral";
  let note: string | undefined;

  if (parts[0] === "disk") {
    label = "Disk";
    unit = "GiB";
    rowName = `${parts[1]} size`;
    const old = Number(attribute.before);
    const next = Number(attribute.after);
    const known = typeof attribute.before === "number" && typeof attribute.after === "number";
    note = known ? (next < old ? "shrink" : "grow") : undefined;
    if (known && next < old) tone = "danger";
    sub = note ? `${parts[1]} · ${note}` : parts[1];
  } else if (attribute.name === "memory.dedicated") {
    label = "Memory";
    rowName = "memory";
    const sides = [attribute.before, attribute.after].filter((v): v is number => typeof v === "number");
    const gib = sides.length > 0 && sides.every((v) => v % 1024 === 0);
    unit = gib ? "GiB" : "MiB";
    if (gib) {
      before = typeof attribute.before === "number" ? String(attribute.before / 1024) : before;
      after = typeof attribute.after === "number" ? String(attribute.after / 1024) : after;
    }
    sub = "dedicated";
  } else if (parts[0] === "cpu") {
    label = "CPU";
    rowName = parts[1];
    sub = parts[1];
  } else if (parts[0] === "network_device") {
    label = "Network";
    rowName = `net${parts[1]} bridge`;
    sub = `net${parts[1]} bridge`;
  } else if (attribute.name === "tags") {
    return tagsTile(change, attribute, key, heading, danger, resourceNote);
  }

  if (attribute.sensitive) {
    return { key, label, value: "sensitive", sub, tone, heading, unchanged: [],
      rows: [{ mark: "~", name: rowName, note: resourceNote ?? "sensitive" }] };
  }
  if (attribute.unknown) {
    return { key, label, value: "known after apply", sub, tone: danger ? "danger" : "warn", heading, unchanged: [],
      rows: [{ mark: "~", name: rowName, before: withUnit(before, unit), after: "known after apply", note: resourceNote }] };
  }
  return {
    key, label, sub, tone, heading, unchanged: [],
    value: transition(before, after, unit),
    rows: [{ mark: mark(attribute.before, attribute.after), name: rowName, before: withUnit(before, unit),
      after: withUnit(after, unit), note: resourceNote ?? note }],
  };
}

function tagsTile(change: PlanChange, attribute: Attribute, key: string, heading: string, danger: boolean,
  resourceNote: string | undefined): Tile {
  if (attribute.sensitive || attribute.unknown) {
    const value = attribute.sensitive ? "sensitive" : "known after apply";
    return { key, label: "Tags", value, sub: "", tone: danger ? "danger" : attribute.unknown ? "warn" : "neutral", heading,
      unchanged: [], rows: [{ mark: "~", name: "tags", note: resourceNote ?? value }] };
  }
  const before = strings(attribute.before);
  const after = strings(attribute.after);
  const added = after.filter((tag) => !before.includes(tag));
  const removed = before.filter((tag) => !after.includes(tag));
  const kept = after.filter((tag) => before.includes(tag));
  const tone: TileTone = danger ? "danger" : removed.length === 0 ? "add" : added.length === 0 ? "remove" : "neutral";
  return {
    key, label: "Tags", tone, heading,
    value: [...added.map((tag) => `+${tag}`), ...removed.map((tag) => `−${tag}`)].join(" ") || "no change",
    sub: kept.length > 0 ? `${kept.join(", ")} kept` : change.kind === "create" ? "new" : "",
    rows: [
      ...added.map((tag): Row => ({ mark: "+", name: tag, note: resourceNote })),
      ...removed.map((tag): Row => ({ mark: "−", name: tag, note: resourceNote })),
    ],
    unchanged: kept.map((tag) => ({ mark: "", name: tag })),
  };
}

function resourcesTile(changes: PlanChange[]): Tile | null {
  const counted = changes.filter((change) => change.kind !== "no-op" && change.kind !== "read");
  if (counted.length === 0) return null;
  const order: [PlanChange["kind"], string][] = [["create", "+"], ["update", "~"], ["delete", "−"], ["replace", "±"], ["other", "?"]];
  const present = order
    .map(([kind, sign]) => ({ kind, sign, n: counted.filter((change) => change.kind === kind).length }))
    .filter((entry) => entry.n > 0);
  const rowMark = (kind: PlanChange["kind"]): RowMark =>
    kind === "create" ? "+" : kind === "update" ? "~" : kind === "delete" ? "−" : kind === "replace" ? "±" : "";
  return {
    key: "resources",
    label: "Resources",
    value: present.map((entry) => `${entry.sign}${entry.n}`).join(" "),
    sub: present.map((entry) => actionLabels[entry.kind]).join(" · "),
    tone: counted.some(destructive) ? "danger" : counted.every((change) => change.kind === "create") ? "add" : "neutral",
    rows: counted.map((change) => ({
      mark: rowMark(change.kind),
      name: change.address,
      note: change.kind === "other" ? change.actions.join(", ") : actionLabels[change.kind],
    })),
    unchanged: [],
  };
}

function isObject(value: unknown): value is Record<string, unknown> {
  return typeof value === "object" && value !== null && !Array.isArray(value);
}

function leafText(value: unknown): string {
  if (typeof value === "string") return value;
  if (value === null || value === undefined) return "null";
  return JSON.stringify(value);
}

/** A leaf as shown, and as JSON so that `false` and `"false"` still differ. */
export interface Leaf {
  text: string;
  json: string;
}

/** Leaf paths of prepared JSON. A list of plain values stays one leaf, so a new item is not a shifted index. */
export function flatten(value: unknown, path = ""): Map<string, Leaf> {
  const leaves = new Map<string, Leaf>();
  const walk = (node: unknown, at: string) => {
    if (Array.isArray(node)) {
      if (node.every((item) => !isObject(item) && !Array.isArray(item))) {
        leaves.set(at, { text: node.map(leafText).join(", "), json: JSON.stringify(node) });
      } else {
        node.forEach((item, i) => walk(item, `${at}[${i}]`));
      }
    } else if (isObject(node) && Object.keys(node).length > 0) {
      for (const [key, child] of Object.entries(node)) walk(child, at ? `${at}.${key}` : key);
    } else {
      leaves.set(at, { text: isObject(node) ? "{}" : leafText(node), json: JSON.stringify(node ?? null) });
    }
  };
  walk(value, path);
  return leaves;
}

function preparedTile(preview: ConfigurePreview, index: number): Tile | null {
  if (preview.prepared === null || preview.prepared === undefined) return null;
  const now = flatten(preview.prepared);
  const name = (path: string) => path || "(value)";
  const heading = `${preview.action} · ${preview.playbook}`;
  const key = `prepared:${index}`;
  const baseline = isObject(preview.previousPrepared) ? flatten(preview.previousPrepared) : null;
  if (!baseline) {
    return {
      key, label: "Prepared", heading, tone: "neutral",
      value: String(now.size),
      sub: "first run · nothing to compare",
      rows: [...now].map(([path, value]) => ({ mark: "", name: name(path), after: value.text })),
      unchanged: [],
    };
  }
  const changed: Row[] = [];
  const added: Row[] = [];
  const removed: Row[] = [];
  const unchanged: Row[] = [];
  for (const [path, value] of now) {
    const old = baseline.get(path);
    if (old === undefined) added.push({ mark: "+", name: name(path), after: value.text });
    else if (old.json !== value.json) changed.push({ mark: "~", name: name(path), before: old.text, after: value.text });
    else unchanged.push({ mark: "", name: name(path), before: value.text });
  }
  for (const [path, value] of baseline) {
    if (!now.has(path)) removed.push({ mark: "−", name: name(path), before: value.text });
  }
  const parts = [
    changed.length > 0 ? [String(changed.length), "changed"] : null,
    added.length > 0 ? [`+${added.length}`, "added"] : null,
    removed.length > 0 ? [`−${removed.length}`, "removed"] : null,
  ].filter((part): part is string[] => part !== null);
  return {
    key, label: "Prepared", heading,
    value: parts.length > 0 ? parts.map((part) => part[0]).join(" ") : "no change",
    sub: parts.length > 0 ? parts.map((part) => part[1]).join(" · ") : "same as the last successful run",
    tone: "neutral",
    rows: [...changed, ...added, ...removed],
    unchanged,
  };
}

/** A `git diff --name-status` line, such as "M path". */
function fileRow(line: string): Row {
  const match = /^([A-Z])\s+(.+)$/.exec(line);
  if (!match) return { mark: "", name: line };
  const marks: Record<string, RowMark> = { A: "+", D: "−", M: "~", T: "~" };
  return { mark: marks[match[1]] ?? "", name: match[2] };
}

/** Tiles for one stage's plans and previews, in a fixed order: VM settings, resources, prepared values, files, secrets. */
export function buildOverview({ plans, previews }: OverviewInput): Tile[] {
  const tiles: Tile[] = [];
  const plain: PlanChange[] = [];
  for (const plan of plans) {
    for (const change of plan.changes) {
      if (change.attributes.length === 0) plain.push(change);
      else for (const attribute of change.attributes) tiles.push(attributeTile(change, attribute));
    }
  }
  const resources = resourcesTile(plain);
  if (resources) tiles.push(resources);
  previews.forEach((preview, i) => {
    const prepared = preparedTile(preview, i);
    if (prepared) tiles.push(prepared);
    if (preview.changedFiles && preview.changedFiles.length > 0) {
      tiles.push({
        key: `files:${i}`, label: "Recipe files", heading: `Changed since the last successful run · ${preview.playbook}`,
        value: String(preview.changedFiles.length),
        sub: "changed since last run",
        tone: "neutral",
        rows: preview.changedFiles.map(fileRow),
        unchanged: [],
      });
    }
    if (preview.secrets.length > 0) {
      tiles.push({
        key: `secrets:${i}`, label: "Secrets", heading: "References only; values are not shown",
        value: String(preview.secrets.length),
        sub: "store references",
        tone: "neutral",
        rows: preview.secrets.map((secret) => ({ mark: "", name: secret.name, after: secret.reference })),
        unchanged: [],
      });
    }
  });
  return tiles;
}

/** Tiles for a run: its plan and preview steps, grouped by stage, labels prefixed only when there are several stages. */
export function buildRunOverview(steps: Step[]): Tile[] {
  const sorted = [...steps].sort((a, b) => Number(a.ordinal) - Number(b.ordinal));
  const stages = new Map<string, OverviewInput>();
  for (const step of sorted) {
    if (!step.plan && !step.configurePreview) continue;
    const stage = step.stage ?? "";
    const input = stages.get(stage) ?? { plans: [], previews: [] };
    if (step.plan) input.plans.push(summarizePlan(step.plan));
    if (step.configurePreview) input.previews.push(step.configurePreview);
    stages.set(stage, input);
  }
  const prefix = stages.size > 1;
  return [...stages].flatMap(([stage, input]) =>
    buildOverview(input).map((tile) =>
      prefix && stage ? { ...tile, key: `${stage}/${tile.key}`, label: `${stage} · ${tile.label}` } : tile));
}

export interface Segment {
  text: string;
  changed: boolean;
}

/** The new value, split so only the tokens that differ from the old value are marked changed. */
export function emphasize(before: string, after: string): Segment[] {
  const pattern = /[0-9]+|[A-Za-z]+|[^0-9A-Za-z]/g;
  const x = before.match(pattern) ?? [];
  const y = after.match(pattern) ?? [];
  let head = 0;
  while (head < x.length && head < y.length && x[head] === y[head]) head++;
  let tail = 0;
  while (tail < x.length - head && tail < y.length - head && x[x.length - 1 - tail] === y[y.length - 1 - tail]) tail++;
  const midOld = x.slice(head, x.length - tail);
  const midNew = y.slice(head, y.length - tail);
  const segments: Segment[] = [{ text: y.slice(0, head).join(""), changed: false }];
  if (midOld.length === midNew.length) {
    midNew.forEach((token, i) => segments.push({ text: token, changed: token !== midOld[i] }));
  } else {
    segments.push({ text: midNew.join(""), changed: true });
  }
  segments.push({ text: y.slice(y.length - tail).join(""), changed: false });
  // Join neighbours of the same kind and drop empty pieces.
  return segments.reduce<Segment[]>((out, segment) => {
    if (!segment.text) return out;
    const last = out[out.length - 1];
    if (last && last.changed === segment.changed) last.text += segment.text;
    else out.push({ ...segment });
    return out;
  }, []);
}
