import yaml from "js-yaml";

/** What a value reads as. Nothing stores it: it is read from the text each time. */
export type Format = "env" | "yaml" | "text";

export const formatNames: Record<Format, string> = { env: ".env", yaml: "YAML", text: "plain text" };

export interface Problem {
  line: number;
  message: string;
}

const envLine = /^(?:export\s+)?([A-Za-z_][A-Za-z0-9_]*)=/;
const yamlLine = /^\s*(- |[\w."'-]+:(\s|$))/;

function contentLines(text: string): string[] {
  return text.split("\n").filter((line) => line.trim() !== "" && !line.trim().startsWith("#"));
}

/** `.env` when most lines are NAME=value, YAML when they are `name: value` or `- item`, plain text otherwise. */
export function detect(text: string): Format {
  const lines = contentLines(text);
  if (lines.length === 0) return "text";
  const env = lines.filter((line) => envLine.test(line)).length;
  const yml = lines.filter((line) => yamlLine.test(line)).length;
  if (env > 0 && env >= yml) return "env";
  if (yml > 0) return "yaml";
  return "text";
}

/** The NAME=value pairs of an env file, in order; a line without = is left out. */
export function pairs(text: string): { name: string; value: string }[] {
  return text.split("\n").flatMap((raw) => {
    const line = raw.trim();
    if (line === "" || line.startsWith("#")) return [];
    const at = line.indexOf("=");
    if (at <= 0) return [];
    return [{ name: line.slice(0, at).replace(/^export\s+/, "").trim(), value: line.slice(at + 1) }];
  });
}

/** Problems worth a warning. They never block saving: the value is kept exactly as entered. */
export function check(text: string, format: Format): Problem[] {
  if (format === "yaml") {
    try {
      yaml.load(text);
      return [];
    } catch (error) {
      if (error instanceof yaml.YAMLException) return [{ line: (error.mark?.line ?? 0) + 1, message: error.reason || "it does not read as YAML" }];
      return [{ line: 1, message: "it does not read as YAML" }];
    }
  }
  if (format !== "env") return [];
  const problems: Problem[] = [];
  const seen = new Map<string, number>();
  text.split("\n").forEach((raw, index) => {
    const line = raw.trim();
    if (line === "" || line.startsWith("#")) return;
    const match = envLine.exec(raw);
    if (!match) {
      problems.push({
        line: index + 1,
        message: raw.includes("=") ? "a name may only hold letters, digits and _" : "there is no = between the name and the value",
      });
      return;
    }
    const first = seen.get(match[1]);
    if (first) problems.push({ line: index + 1, message: `${match[1]} is already set on line ${first}` });
    else seen.set(match[1], index + 1);
  });
  return problems;
}

const lines = (n: number) => (n === 1 ? "1 line" : `${n} lines`);

/**
 * What changed from `before` to `after`, in one short sentence. For an env file, up to three names; otherwise counts of
 * lines. Empty when nothing changed.
 */
export function summarize(before: string, after: string, format: Format = detect(before)): string {
  if (before === after) return "";
  if (format === "env") {
    const old = new Map(pairs(before).map((p) => [p.name, p.value]));
    const now = new Map(pairs(after).map((p) => [p.name, p.value]));
    const parts: string[] = [];
    for (const [name, value] of now) {
      if (!old.has(name)) parts.push(`${name} added`);
      else if (old.get(name) !== value) parts.push(`${name} changed`);
    }
    for (const name of old.keys()) if (!now.has(name)) parts.push(`${name} removed`);
    if (parts.length > 0 && parts.length <= 3) return `${parts.join(", ")}.`;
  }
  const pool = before.split("\n");
  let fresh = 0;
  for (const line of after.split("\n")) {
    const at = pool.indexOf(line);
    if (at === -1) fresh += 1;
    else pool.splice(at, 1);
  }
  const gone = pool.length;
  const changed = Math.min(fresh, gone);
  const parts: string[] = [];
  if (changed) parts.push(`${lines(changed)} changed`);
  if (fresh - changed) parts.push(`${lines(fresh - changed)} added`);
  if (gone - changed) parts.push(`${lines(gone - changed)} removed`);
  return parts.length ? `${parts.join(", ")}.` : "The lines are in another order.";
}

/** How the format line reads a value with no problems. */
export function formatSummary(text: string, format: Format): string {
  if (text.trim() === "") return "Nothing entered yet.";
  if (format === "env") {
    const n = pairs(text).length;
    return `Reads as .env with ${n === 1 ? "1 variable" : `${n} variables`}, no problems.`;
  }
  if (format === "yaml") return "Reads as YAML, no problems.";
  return "Kept exactly as entered.";
}

export interface LinePart {
  /** `NAME` or `name`, with any indent. */
  name: string;
  /** `=` or `:` with the space after it. */
  sep: string;
  rest: string;
  comment: boolean;
}

/** One line split for light colouring: the name, its separator and the rest; a comment is all rest. */
export function splitLine(raw: string, format: Format): LinePart {
  const plain = { name: "", sep: "", rest: raw, comment: false };
  if (format === "text") return plain;
  if (raw.trim().startsWith("#")) return { ...plain, comment: true };
  const match = format === "yaml" ? /^(\s*(?:- )?[^:#\s][^:#]*)(:\s*)(.*)$/.exec(raw) : /^((?:export\s+)?[A-Za-z_][A-Za-z0-9_]*)(=)(.*)$/.exec(raw);
  return match ? { name: match[1], sep: match[2], rest: match[3], comment: false } : plain;
}

export interface DiffLine {
  kind: "same" | "added" | "removed";
  text: string;
}

/** Lines as the viewer shows them: one trailing newline ends the last line and does not start another. */
function valueLines(text: string): string[] {
  if (text === "") return [];
  return (text.endsWith("\n") ? text.slice(0, -1) : text).split("\n");
}

/**
 * Every line of `before` and `after` in one list, each kept, removed or added, by their longest common run of lines: a line
 * diff as a person reads one. The lines both start and end with are left out of the table, so a long value with a small
 * change costs little.
 */
export function diffLines(before: string, after: string): DiffLine[] {
  const a = valueLines(before);
  const b = valueLines(after);
  let start = 0;
  while (start < a.length && start < b.length && a[start] === b[start]) start += 1;
  let end = 0;
  while (end < a.length - start && end < b.length - start && a[a.length - 1 - end] === b[b.length - 1 - end]) end += 1;
  const x = a.slice(start, a.length - end);
  const y = b.slice(start, b.length - end);
  // common[i][j]: the longest common run of x[i..] and y[j..].
  const common = Array.from({ length: x.length + 1 }, () => new Array<number>(y.length + 1).fill(0));
  for (let i = x.length - 1; i >= 0; i -= 1) {
    for (let j = y.length - 1; j >= 0; j -= 1) {
      common[i][j] = x[i] === y[j] ? common[i + 1][j + 1] + 1 : Math.max(common[i + 1][j], common[i][j + 1]);
    }
  }
  const middle: DiffLine[] = [];
  let i = 0;
  let j = 0;
  while (i < x.length || j < y.length) {
    if (i < x.length && j < y.length && x[i] === y[j]) {
      middle.push({ kind: "same", text: x[i] });
      i += 1;
      j += 1;
    } else if (j >= y.length || (i < x.length && common[i + 1][j] >= common[i][j + 1])) {
      middle.push({ kind: "removed", text: x[i] });
      i += 1;
    } else {
      middle.push({ kind: "added", text: y[j] });
      j += 1;
    }
  }
  const same = (text: string): DiffLine => ({ kind: "same", text });
  return [...a.slice(0, start).map(same), ...middle, ...a.slice(a.length - end).map(same)];
}
