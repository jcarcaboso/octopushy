/** A definition file read as sections of labelled values, so a detail page shows what it says without showing the YAML. */
import yaml from "js-yaml";

export type DefinitionKind = "guest" | "service" | "installation";

export type FieldValue =
  /** `raw` is the value as written, when `text` is a friendlier form of it: "8 GiB" for 8192 MiB. */
  | { kind: "text"; text: string; raw?: string }
  | { kind: "list"; items: string[] }
  /** A variable-store reference, `{ store: <key>, version?: <n> }`. */
  | { kind: "secret"; key: string; version: number | null }
  | { kind: "link"; url: string };

export interface DefinitionField {
  /** The key as written, with its parents when it is nested or a secret file: "cores", "env.OLLAMA_BASE_URL", "secretFiles.env_file". */
  key: string;
  label: string;
  value: FieldValue;
  /** The 1-based line the value is written on. */
  line: number;
  /** The comment the author wrote for this value, without its "#". */
  note: string | null;
}

export interface DefinitionSection {
  id: string;
  title: string;
  /** First and last 1-based line of the section, inclusive. */
  lines: [number, number];
  note: string | null;
  fields: DefinitionField[];
}

/** A line of the file that holds a key or a list item, with what is nested under it. */
interface Node {
  line: number;
  /** The indentation, plus one for a list item, so an item written under its key at the same column nests under it. */
  depth: number;
  key: string | null;
  comment: string | null;
  above: string[];
  children: Node[];
  /** The last line of its value, comments and blank lines left out. */
  end: number;
}

const generalTitles: Record<DefinitionKind, string> = { guest: "Placement", service: "Service", installation: "Installation" };
const acronyms = new Set(["tls", "dns", "vmid", "ssh", "url", "id", "cpu", "ip"]);
const keyPattern = /^("(?:[^"\\]|\\.)*"|'(?:[^']|'')*'|[^\s"'#[\]{},][^#]*?)\s*:(?:\s+|$)/;
const days = ["Sundays", "Mondays", "Tuesdays", "Wednesdays", "Thursdays", "Fridays", "Saturdays", "Sundays"];

type Plain = string | number | boolean | null;

function isPlain(value: unknown): value is Plain {
  return value === null || ["string", "number", "boolean"].includes(typeof value);
}

function isMapping(value: unknown): value is Record<string, unknown> {
  return typeof value === "object" && value !== null && !Array.isArray(value);
}

function plain(value: Plain): string {
  return value === null ? "—" : String(value);
}

/** A value in one line, for the items of a list field. */
function flow(value: unknown): string {
  if (isPlain(value)) return plain(value);
  if (Array.isArray(value)) return `[${value.map(flow).join(", ")}]`;
  if (isMapping(value)) return `{ ${Object.entries(value).map(([key, item]) => `${key}: ${flow(item)}`).join(", ")} }`;
  return String(value);
}

function label(key: string): string {
  if (/^[A-Z0-9_]+$/.test(key) && /[A-Z]/.test(key)) return key;
  const words = key.replace(/([a-z0-9])([A-Z])/g, "$1 $2").split(/[\s_-]+/).filter(Boolean);
  const text = words.map((word) => (acronyms.has(word.toLowerCase()) ? word.toUpperCase() : word.toLowerCase())).join(" ");
  return text.charAt(0).toUpperCase() + text.slice(1);
}

function singular(text: string): string {
  if (/ies$/.test(text)) return text.slice(0, -3) + "y";
  if (/(ss|sh|ch|x)es$/.test(text)) return text.slice(0, -2);
  if (/[^s]s$/.test(text)) return text.slice(0, -1);
  return text;
}

function joinNotes(...parts: (string | null | undefined)[]): string | null {
  const kept = parts.filter((part): part is string => !!part);
  return kept.length > 0 ? kept.join(" ") : null;
}

/** Splits a line at its comment: a `#` at the start or after whitespace, outside quotes. */
function splitComment(line: string): [string, string | null] {
  let quote: string | null = null;
  for (let at = 0; at < line.length; at++) {
    const char = line[at];
    const before = at > 0 ? line[at - 1] : " ";
    if (quote) {
      if ((quote === '"' && char === "\\") || (quote === "'" && char === "'" && line[at + 1] === "'")) at++;
      else if (char === quote) quote = null;
    } else if ((char === '"' || char === "'") && /[\s[{,:]/.test(before)) {
      quote = char;
    } else if (char === "#" && /\s/.test(before)) {
      return [line.slice(0, at).trimEnd(), line.slice(at + 1).trim()];
    }
  }
  return [line.trimEnd(), null];
}

function unquote(key: string): string {
  if (key.startsWith('"')) return key.slice(1, -1).replace(/\\(.)/g, "$1");
  if (key.startsWith("'")) return key.slice(1, -1).replace(/''/g, "'");
  return key;
}

/** The keys and list items of the text by indentation, with their lines and comments. */
function scan(text: string): Node {
  const root: Node = { line: 0, depth: -1, key: null, comment: null, above: [], children: [], end: 0 };
  const stack: Node[] = [root];
  const lines = text.split(/\r?\n/);
  let pending: string[] = [];
  let block: { node: Node; indent: number } | null = null;
  const add = (node: Node) => {
    while (stack.length > 1 && stack[stack.length - 1].depth >= node.depth) stack.pop();
    stack[stack.length - 1].children.push(node);
    stack.push(node);
  };
  lines.forEach((raw, index) => {
    const line = index + 1;
    const indent = raw.length - raw.trimStart().length;
    const trimmed = raw.trim();
    if (block) {
      if (trimmed === "" || indent > block.indent) {
        if (trimmed !== "") block.node.end = line;
        return;
      }
      block = null;
    }
    if (trimmed === "") {
      pending = [];
      return;
    }
    if (trimmed.startsWith("#")) {
      pending.push(trimmed.slice(1).trim());
      return;
    }
    const [content, comment] = splitComment(raw.slice(indent));
    let rest = content;
    let column = indent;
    let node: Node | null = null;
    let item: Node | null = null;
    while (rest === "-" || rest.startsWith("- ")) {
      node = { line, depth: column + 1, key: null, comment: null, above: pending, children: [], end: line };
      item ??= node;
      pending = [];
      add(node);
      const after = rest.slice(1);
      column += 1 + (after.length - after.trimStart().length);
      rest = after.trimStart();
    }
    const match = keyPattern.exec(rest);
    if (match) {
      node = { line, depth: column, key: unquote(match[1]), comment: null, above: pending, children: [], end: line };
      pending = [];
      add(node);
      rest = rest.slice(match[0].length);
    }
    if (node) {
      // A comment on a list item's line is the item's, even when the item starts with a key.
      (item ?? node).comment = comment;
      if (/^[|>][-+0-9]*$/.test(rest)) block = { node, indent: column };
    }
    pending = [];
  });
  const finish = (node: Node): number => {
    for (const child of node.children) node.end = Math.max(node.end, finish(child));
    return node.end;
  };
  finish(root);
  return root;
}

function childNode(node: Node | undefined, key: string): Node | undefined {
  return node?.children.find((child) => child.key === key);
}

function itemNodes(node: Node | undefined): Node[] {
  return node?.children.filter((child) => child.key === null) ?? [];
}

function secretOf(value: unknown): FieldValue | null {
  if (!isMapping(value) || typeof value.store !== "string") return null;
  const keys = Object.keys(value);
  if (keys.length === 1) return { kind: "secret", key: value.store, version: null };
  const version = value.version;
  if (keys.length === 2 && "version" in value && typeof version === "number" && Number.isInteger(version) && version > 0) {
    return { kind: "secret", key: value.store, version };
  }
  return null;
}

function scalarValue(value: Plain): FieldValue {
  if (typeof value === "string" && /^https?:\/\/\S+$/i.test(value)) return { kind: "link", url: value };
  return { kind: "text", text: plain(value) };
}

function cronWords(cron: string): string | null {
  const parts = cron.trim().split(/\s+/);
  if (parts.length !== 5 || !/^\d{1,2}$/.test(parts[0]) || !/^\d{1,2}$/.test(parts[1]) || parts[2] !== "*" || parts[3] !== "*") return null;
  const time = `${parts[1].padStart(2, "0")}:${parts[0].padStart(2, "0")}`;
  if (parts[4] === "*") return `Every day at ${time}`;
  if (/^[0-7]$/.test(parts[4])) return `${days[Number(parts[4])]} at ${time}`;
  return null;
}

/** Where a field comes from: the section it is in, its key, label, line and note. */
interface Place {
  section: string;
  kind: DefinitionKind;
  path: string[];
  key: string;
  label: string;
  line: number;
  note: string | null;
}

function noteOf(node: Node | undefined, withAbove: boolean): string | null {
  if (!node) return null;
  return joinNotes(withAbove ? joinNotes(...node.above) : null, node.comment);
}

/** The fields one value gives, nested mappings flattened into the same section. */
function fieldsOf(value: unknown, node: Node | undefined, place: Place): DefinitionField[] {
  const { section, kind, path, key, label: name, line, note } = place;
  const field = (fieldValue: FieldValue): DefinitionField[] => [{ key, label: name, value: fieldValue, line, note }];
  const atTop = path.length === 1;

  if (kind === "guest" && section === "hardware" && atTop && path[0] === "memory" && typeof value === "number" && Number.isInteger(value)) {
    return field(value % 1024 === 0 ? { kind: "text", text: `${value / 1024} GiB`, raw: `${value} MiB` } : { kind: "text", text: `${value} MiB` });
  }
  const secret = secretOf(value);
  if (secret) return field(secret);
  if (isPlain(value)) return field(scalarValue(value));

  if (Array.isArray(value)) {
    if (value.every(isPlain)) {
      const items = value.map(plain);
      // A comment on an item's own line has no place among the chips, so it joins the field's note, after its item.
      const said = itemNodes(node).flatMap((item, index) => (item.comment ? [`${items[index]}: ${item.comment}`] : []));
      return [{ key, label: name, value: { kind: "list", items }, line, note: joinNotes(note, said.join("; ")) }];
    }
    const items = itemNodes(node);
    const disks = kind === "guest" && section === "hardware" && atTop && path[0] === "disks";
    const schedules = section === "schedules" && atTop;
    return value.flatMap((item, index): DefinitionField[] => {
      const itemNode = items[index];
      const one = (fieldValue: FieldValue, itemLabel?: string): DefinitionField => ({
        key: `${key}[${index}]`,
        label: itemLabel ?? singular(name) + (value.length > 1 && index > 0 ? ` ${index + 1}` : ""),
        value: fieldValue,
        line: itemNode?.line ?? line,
        note: joinNotes(index === 0 ? note : null, noteOf(itemNode, index > 0)),
      });
      if (!isMapping(item) || secretOf(item)) return [one(secretOf(item) ?? (isPlain(item) ? scalarValue(item) : { kind: "text", text: flow(item) }))];
      if (disks && typeof item.size === "number") {
        const raw = typeof item.datastore === "string" ? `on ${item.datastore}` : undefined;
        return [one({ kind: "text", text: `${item.size} GiB`, ...(raw ? { raw } : {}) })];
      }
      if (schedules && typeof item.action === "string" && typeof item.cron === "string") {
        const words = cronWords(item.cron);
        const raw = typeof item.timezone === "string" ? `${item.cron} ${item.timezone}` : words ? item.cron : undefined;
        return [one({ kind: "text", text: words ?? item.cron, ...(raw ? { raw } : {}) }, item.action)];
      }
      return [one({ kind: "list", items: Object.entries(item).map(([k, v]) => `${k}: ${flow(v)}`) })];
    });
  }

  if (isMapping(value)) {
    const entries = Object.entries(value);
    const action = section === "policy" && path.length === 2 && path[0] === "actions";
    // Only a mapping written on one line reads as one list: a block mapping keeps a field per key.
    const oneLine = !node || node.children.length === 0;
    if (!action && oneLine && entries.length <= 4 && entries.every(([, item]) => isPlain(item))) {
      return field({ kind: "list", items: entries.map(([k, item]) => `${k}: ${plain(item as Plain)}`) });
    }
    return entries.flatMap(([child, item], index) => {
      const nested = childNode(node, child);
      return fieldsOf(item, nested, {
        ...place,
        path: [...path, child],
        key: `${key}.${child}`,
        label: action && child === "approval" ? `Approval of ${path[1]}` : `${name} · ${child}`,
        line: nested?.line ?? line,
        note: joinNotes(index === 0 ? note : null, noteOf(nested, true)),
      });
    });
  }
  return field({ kind: "text", text: String(value) });
}

/** The fields of a section's value: the children of a mapping, or the items of a list. */
function sectionFields(value: unknown, node: Node, section: string, kind: DefinitionKind): DefinitionField[] {
  const first = node.children[0];
  if (Array.isArray(value)) {
    return fieldsOf(value, node, { section, kind, path: [section], key: section, label: label(section), line: node.line, note: null });
  }
  if (!isMapping(value)) return [];
  return Object.entries(value).flatMap(([key, item]) => {
    const child = childNode(node, key);
    return fieldsOf(item, child, { section, kind, path: [key], key, label: label(key), line: child?.line ?? node.line, note: noteOf(child, child !== first) });
  });
}

function sectionNote(node: Node): string | null {
  return joinNotes(joinNotes(...node.above), node.comment, joinNotes(...(node.children[0]?.above ?? [])));
}

/** The sections of a definition, in the order of the file, or null when the text is not a YAML mapping. */
export function readDefinition(text: string, kind: DefinitionKind): DefinitionSection[] | null {
  let parsed: unknown;
  try {
    parsed = yaml.load(text, { schema: yaml.CORE_SCHEMA });
  } catch {
    return null;
  }
  if (!isMapping(parsed)) return null;
  const root = scan(text);
  const general: DefinitionField[] = [];
  let generalEnd = 0;
  const sections: DefinitionSection[] = [];
  let secrets: DefinitionSection | null = null;

  for (const [key, value] of Object.entries(parsed)) {
    if (key === "schemaVersion") continue;
    const node = childNode(root, key) ?? { line: 1, depth: 0, key, comment: null, above: [], children: [], end: 1 };
    if (isPlain(value) || (Array.isArray(value) && value.every(isPlain))) {
      const note = noteOf(node, true);
      if (key === "links" && Array.isArray(value)) {
        const items = itemNodes(node);
        value.forEach((url, index) => {
          const item = items[index];
          general.push({ key: `links[${index}]`, label: "Link", value: { kind: "link", url: plain(url) }, line: item?.line ?? node.line, note: joinNotes(index === 0 ? note : null, item?.comment) });
        });
      } else {
        general.push(...fieldsOf(value, node, { section: "general", kind, path: [key], key, label: label(key), line: node.line, note }));
      }
      generalEnd = Math.max(generalEnd, node.end);
      continue;
    }
    if (key === "hosts" && kind === "installation" && isMapping(value)) {
      Object.entries(value).forEach(([host, hostValue], index) => {
        const hostNode = childNode(node, host) ?? node;
        const id = `hosts.${host}`;
        sections.push({
          id,
          title: `Host ${host}`,
          lines: [hostNode.line, hostNode.end],
          note: joinNotes(index === 0 ? joinNotes(joinNotes(...node.above), node.comment) : null, sectionNote(hostNode)),
          fields: sectionFields(hostValue, hostNode, id, kind),
        });
      });
      continue;
    }
    const fields = sectionFields(value, node, key, kind);
    if (key === "secrets" || key === "secretFiles") {
      if (key === "secretFiles") {
        for (const field of fields) {
          // The two share a section, and a name can be in both: the key says which one it is under.
          field.key = `secretFiles.${field.key}`;
          field.note = field.note ? `${field.note}${/[.!?]$/.test(field.note) ? "" : "."} Delivered as a file` : "Delivered as a file";
        }
      }
      if (secrets) {
        secrets.fields.push(...fields);
        secrets.lines = [Math.min(secrets.lines[0], node.line), Math.max(secrets.lines[1], node.end)];
        secrets.note = joinNotes(secrets.note, sectionNote(node));
        secrets.fields.sort((a, b) => a.line - b.line);
      } else {
        secrets = { id: "secrets", title: "Secrets", lines: [node.line, node.end], note: sectionNote(node), fields };
        sections.push(secrets);
      }
      continue;
    }
    sections.push({ id: key, title: label(key), lines: [node.line, node.end], note: sectionNote(node), fields });
  }

  if (general.length > 0) {
    sections.unshift({ id: "general", title: generalTitles[kind], lines: [general[0].line, generalEnd], note: null, fields: general });
  }
  return sections;
}
