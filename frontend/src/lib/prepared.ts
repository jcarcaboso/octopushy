/**
 * What a configure run prepared, against what the last successful run prepared, as things a person can read:
 * `machine_resolved_t3.url` and `machine_resolved_t3.sha256` become one line, "t3", with the release that changed.
 */

/** A leaf as shown, and as JSON so that `false` and `"false"` still differ. */
export interface Leaf {
  text: string;
  json: string;
}

function isObject(value: unknown): value is Record<string, unknown> {
  return typeof value === "object" && value !== null && !Array.isArray(value);
}

function leafText(value: unknown): string {
  if (typeof value === "string") return value;
  if (value === null || value === undefined) return "null";
  return JSON.stringify(value);
}

const identityKeys = ["name", "id", "key"];
const plainKey = /^[^.[\]]+$/;

/**
 * The member that tells a list's objects apart, such as `name`: a string on every item, unique, and usable as a path
 * part. Undefined when there is none, and the list is indexed.
 */
function identity(items: unknown[]): string | undefined {
  return identityKeys.find((key) => {
    const ids = items.map((item) => (isObject(item) ? item[key] : undefined));
    return ids.every((id) => typeof id === "string" && plainKey.test(id)) && new Set(ids).size === ids.length;
  });
}

/**
 * Leaf paths of prepared JSON. A list of plain values stays one leaf, and objects with unique names are addressed by
 * name, `tools.codex`, so a new item is not a shifted index.
 */
export function flatten(value: unknown, path = ""): Map<string, Leaf> {
  const leaves = new Map<string, Leaf>();
  const walk = (node: unknown, at: string) => {
    if (Array.isArray(node)) {
      const key = identity(node);
      if (node.every((item) => !isObject(item) && !Array.isArray(item))) {
        leaves.set(at, { text: node.map(leafText).join(", "), json: JSON.stringify(node) });
      } else if (key) {
        for (const item of node as Record<string, string>[]) walk(item, at ? `${at}.${item[key]}` : item[key]);
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

export type PreparedMark = "+" | "~" | "−" | "";

export interface PreparedLeaf {
  /** The last part of the path, such as `url`. */
  name: string;
  path: string;
  mark: PreparedMark;
  before?: string;
  after?: string;
}

/** One line of the comparison: a plain value, or a thing with several values behind one telling one. */
export interface PreparedItem {
  /** A plain value's path, or a thing's short name, such as `t3`, with the whole of it in `path`. */
  name: string;
  path: string;
  mark: PreparedMark;
  before?: string;
  after?: string;
  /** The values behind a thing's line; empty for a plain value. */
  leaves: PreparedLeaf[];
  /** Which values changed when the telling one did not, such as "sha256 changed". */
  note?: string;
}

export interface PreparedComparison {
  /** False for a first run: there was nothing to compare with, so no item has a mark. */
  compared: boolean;
  items: PreparedItem[];
}

const hash = /^(sha\d+:)?[0-9a-f]{32,}$/i;
const versionNames = new Set(["version", "tag"]);

/** A checksum is unreadable at full length: its first twelve characters tell two apart. */
export function shortValue(text: string): string {
  const match = hash.exec(text);
  if (!match) return text;
  const prefix = match[1] ?? "";
  return `${text.slice(0, prefix.length + 12)}…`;
}

export const isHash = (text: string | undefined) => text !== undefined && hash.test(text);
const isUrl = (text: string | undefined) => text !== undefined && /^[a-z][a-z0-9+.-]*:\/\/\S+$/i.test(text);

/** The part of an address that tells a release: the first path segment that differs, else the file name. */
function telling(before: string | undefined, after: string | undefined): { before?: string; after?: string } {
  const last = (url: string | undefined) => url?.split("/").filter(Boolean).at(-1);
  if (before === undefined || after === undefined || before === after) return { before: last(before), after: last(after) };
  const x = before.split("/");
  const y = after.split("/");
  const at = y.findIndex((segment, i) => segment !== x[i]);
  return at < 0 || x[at] === undefined ? { before: last(before), after: last(after) } : { before: x[at], after: y[at] };
}

function groupMark(leaves: PreparedLeaf[]): PreparedMark {
  if (leaves.every((leaf) => leaf.mark === "+")) return "+";
  if (leaves.every((leaf) => leaf.mark === "−")) return "−";
  return leaves.some((leaf) => leaf.mark !== "") ? "~" : "";
}

/**
 * A group of values is one thing when one of them tells the rest: a `version` or `tag`, or an address next to its
 * checksum. Anything else stays one line per value, so nothing is hidden behind a guess.
 */
function thing(path: string, leaves: PreparedLeaf[]): PreparedItem | null {
  const version = leaves.find((leaf) => versionNames.has(leaf.name));
  const url = leaves.find((leaf) => isUrl(leaf.after ?? leaf.before));
  const checksum = leaves.some((leaf) => isHash(leaf.after ?? leaf.before));
  const head = version ? { before: version.before, after: version.after } : url && checksum ? telling(url.before, url.after) : null;
  if (!head) return null;
  const mark = groupMark(leaves);
  const same = mark === "~" && head.before === head.after;
  return {
    name: path,
    path,
    mark,
    ...head,
    leaves,
    note: same ? `${leaves.filter((leaf) => leaf.mark !== "").map((leaf) => leaf.name).join(", ")} changed` : undefined,
  };
}

/** `machine_resolved_t3` and `machine_resolved_codex` are `t3` and `codex`: what they share says nothing. */
function shortenNames(things: PreparedItem[]) {
  if (things.length < 2) return;
  const names = things.map((item) => item.path);
  let shared = names[0];
  for (const name of names) {
    let i = 0;
    while (i < shared.length && shared[i] === name[i]) i++;
    shared = shared.slice(0, i);
  }
  const cut = Math.max(shared.lastIndexOf("_"), shared.lastIndexOf("-"), shared.lastIndexOf(".")) + 1;
  if (cut === 0 || names.some((name) => name.length === cut)) return;
  for (const item of things) item.name = item.path.slice(cut);
}

/** What was prepared, one item per thing or plain value: changed first, then added, removed and unchanged. */
export function comparePrepared(prepared: unknown, previous: unknown): PreparedComparison | null {
  if (prepared === null || prepared === undefined) return null;
  const now = flatten(prepared);
  const old = isObject(previous) ? flatten(previous) : null;
  const paths = [...now.keys(), ...[...(old?.keys() ?? [])].filter((path) => !now.has(path))];
  const groups = new Map<string, PreparedLeaf[]>();
  for (const path of paths) {
    const before = old?.get(path);
    const after = now.get(path);
    const mark: PreparedMark = !old ? "" : !before ? "+" : !after ? "−" : before.json !== after.json ? "~" : "";
    const dot = path.lastIndexOf(".");
    const parent = dot < 0 ? "" : path.slice(0, dot);
    const leaf: PreparedLeaf = { name: path.slice(dot + 1) || "(value)", path, mark, before: before?.text, after: after?.text };
    groups.set(parent, [...(groups.get(parent) ?? []), leaf]);
  }
  const items: PreparedItem[] = [];
  const things: PreparedItem[] = [];
  for (const [parent, leaves] of groups) {
    const one = parent ? thing(parent, leaves) : null;
    if (one) {
      items.push(one);
      things.push(one);
    } else {
      // A plain value keeps its whole path: `a.port` and `b.port` are not the same line.
      items.push(...leaves.map((leaf) => ({ ...leaf, name: leaf.path || "(value)", leaves: [] })));
    }
  }
  shortenNames(things);
  const order: PreparedMark[] = ["~", "+", "−", ""];
  items.sort((a, b) => order.indexOf(a.mark) - order.indexOf(b.mark));
  return { compared: old !== null, items };
}

/** One thing a run changes, as a list of runs names it. */
export interface PreparedChange {
  name: string;
  before: string | null;
  after: string | null;
}

/**
 * What a list of runs says a playbook run changes: the items that differ, by name. Empty when the run prepared the same
 * values as the last one, null when there was nothing to compare. The server makes the same list for the API's run
 * summaries, and `contracts/examples/run-changes.json` holds the cases both have to agree on.
 */
export function preparedChanges(prepared: unknown, previous: unknown): PreparedChange[] | null {
  const comparison = comparePrepared(prepared, previous);
  if (!comparison?.compared) return null;
  return comparison.items
    .filter((item) => item.mark !== "")
    .map((item) => ({ name: item.name, before: item.before ?? null, after: item.after ?? null }));
}
