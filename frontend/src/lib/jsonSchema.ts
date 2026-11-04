/**
 * What the schema form understands of JSON Schema. A workflow's inputs schema is the form: nothing here knows a workflow.
 *
 * Rendered as fields: objects with `properties`, arrays whose `items` is one schema, `string`, `integer`, `number`,
 * `boolean`, `enum`, `const`, local `$ref` into `$defs`/`definitions`, `title`, `description`, `required`, `default`,
 * `minimum`/`maximum`, and `pattern`/`format` as hints. Keywords that only constrain values, such as `if`/`then`/`else`,
 * `allOf`, `not`, `dependentSchemas`, `dependentRequired`, `contains` and `uniqueItems`, are left to the server, which
 * validates every change. Anything else, such as `oneOf`, `anyOf`, a type list, `patternProperties`, or an object with no
 * `properties`, is edited as YAML for that part of the value, so nothing is ever dropped.
 */

export type JsonValue = null | boolean | number | string | JsonValue[] | { [key: string]: JsonValue };
export type JsonObject = { [key: string]: JsonValue };
export type Schema = { [key: string]: unknown };

/** Hints for one field, by its dotted path in `ui.fields`. Every hint is optional, and unknown ones are ignored. */
export interface FieldHints {
  /** Shown instead of the property's name or the schema's `title`. */
  label?: string;
  /** Shown under the field instead of the schema's `description`. */
  help?: string;
  /** For an object: the order of its properties. Those not listed follow, in the schema's order. */
  order?: string[];
  /** Fields of one object with the same group are shown together, under the group's name. */
  group?: string;
  /** How to show the field. */
  widget?: Widget;
}

export type Widget = "text" | "textarea" | "select" | "radio" | "checkboxes" | "yaml" | "hidden";

/** A workflow's `ui`: the order of the form's sections, and hints per field. */
export interface UiHints {
  order?: string[];
  fields?: Record<string, FieldHints>;
}

export type FieldKind = "object" | "array" | "string" | "integer" | "number" | "boolean" | "enum" | "const" | "yaml";

const shapeKeywords = ["oneOf", "anyOf", "patternProperties", "prefixItems", "unevaluatedProperties", "$dynamicRef"];

export function isObject(value: unknown): value is JsonObject {
  return typeof value === "object" && value !== null && !Array.isArray(value);
}

function asSchema(value: unknown): Schema {
  if (value === true || value === undefined) return {};
  if (value === false) return { not: {} };
  return isObject(value) ? (value as Schema) : {};
}

/** The schema at a local JSON pointer such as `#/$defs/ipv4`, or null. */
function at(root: Schema, ref: string): Schema | null {
  if (!ref.startsWith("#")) return null;
  let node: unknown = root;
  for (const raw of ref.slice(1).split("/").filter((p) => p.length > 0)) {
    const part = decodeURIComponent(raw).replace(/~1/g, "/").replace(/~0/g, "~");
    if (!isObject(node) || !(part in node)) return null;
    node = (node as Schema)[part];
  }
  return isObject(node) || typeof node === "boolean" ? asSchema(node) : null;
}

/**
 * The schema with local `$ref`s followed. Keywords next to a `$ref` apply too, and win over the target's, so a property can
 * describe a shared definition. A `$ref` that leaves the document, or loops, stays, and the field is edited as YAML.
 */
export function resolve(schema: unknown, root: Schema): Schema {
  let current = asSchema(schema);
  for (let depth = 0; typeof current.$ref === "string" && depth < 20; depth++) {
    const target = at(root, current.$ref);
    if (!target) return current;
    const { $ref: _ref, ...siblings } = current;
    void _ref;
    current = { ...target, ...siblings };
  }
  return current;
}

function types(schema: Schema): string[] {
  if (typeof schema.type === "string") return [schema.type];
  if (Array.isArray(schema.type)) return schema.type.filter((t): t is string => typeof t === "string");
  return [];
}

/** How a resolved schema is edited, and for YAML why. */
export function kindOf(schema: Schema, widget?: Widget): { kind: FieldKind; reason?: string } {
  if (widget === "yaml") return { kind: "yaml", reason: "The workflow asks for YAML here." };
  if (typeof schema.$ref === "string") return { kind: "yaml", reason: `${schema.$ref} is not in this schema.` };
  const shape = shapeKeywords.find((k) => k in schema);
  if (shape) return { kind: "yaml", reason: `The form does not render ${shape}.` };
  if (Array.isArray(schema.enum)) {
    return schema.enum.every((v) => v === null || ["string", "number", "boolean"].includes(typeof v))
      ? { kind: "enum" }
      : { kind: "yaml", reason: "Its choices are not plain values." };
  }
  if ("const" in schema) return { kind: "const" };
  const declared = types(schema).filter((t) => t !== "null");
  if (declared.length > 1) return { kind: "yaml", reason: `It can be ${declared.join(" or ")}.` };
  const type = declared[0] ?? (isObject(schema.properties) ? "object" : "items" in schema ? "array" : undefined);
  switch (type) {
    case "object": {
      const properties = isObject(schema.properties) ? Object.keys(schema.properties) : [];
      if (properties.length === 0 && schema.additionalProperties !== false)
        return { kind: "yaml", reason: "Its keys are free-form." };
      return { kind: "object" };
    }
    case "array":
      if (!("items" in schema) || Array.isArray(schema.items)) return { kind: "yaml", reason: "Its items have no single schema." };
      return { kind: "array" };
    case "string":
    case "integer":
    case "number":
    case "boolean":
      return { kind: type };
    default:
      return { kind: "yaml", reason: type ? `The form does not render ${type}.` : "The schema does not say what it is." };
  }
}

/** The `ui.fields` key of a JSON pointer: `/hardware/disks/0/size` is `hardware.disks[].size`. */
export function hintPath(pointer: string): string {
  let path = "";
  for (const part of pointer.split("/").slice(1)) {
    if (/^\d+$/.test(part)) path += "[]";
    else path += (path ? "." : "") + part.replace(/~1/g, "/").replace(/~0/g, "~");
  }
  return path;
}

export function hintsFor(ui: UiHints | null | undefined, pointer: string): FieldHints {
  if (!ui) return {};
  const hints = ui.fields?.[hintPath(pointer)] ?? {};
  return pointer === "" && ui.order && !hints.order ? { ...hints, order: ui.order } : hints;
}

/** Reads a workflow's `ui` loosely: anything that is not a hint is ignored. */
export function readUi(value: unknown): UiHints {
  if (!isObject(value)) return {};
  const ui: UiHints = {};
  if (Array.isArray(value.order)) ui.order = value.order.filter((v): v is string => typeof v === "string");
  if (isObject(value.fields)) {
    ui.fields = {};
    for (const [path, hints] of Object.entries(value.fields)) if (isObject(hints)) ui.fields[path] = hints as FieldHints;
  }
  return ui;
}

export function escapePointer(key: string): string {
  return key.replace(/~/g, "~0").replace(/\//g, "~1");
}

export function child(pointer: string, key: string | number): string {
  return `${pointer}/${typeof key === "number" ? key : escapePointer(key)}`;
}

/** An object's properties in the order to show them: the hints' order, then the schema's, then keys only the value has. */
export function propertyOrder(schema: Schema, value: JsonObject | undefined, hints: FieldHints): { known: string[]; extra: string[] } {
  const properties = isObject(schema.properties) ? Object.keys(schema.properties) : [];
  const ordered = [...(hints.order ?? []).filter((k) => properties.includes(k)), ...properties.filter((k) => !(hints.order ?? []).includes(k))];
  const extra = value ? Object.keys(value).filter((k) => !properties.includes(k)) : [];
  return { known: ordered, extra };
}

export function requiredOf(schema: Schema): string[] {
  return Array.isArray(schema.required) ? schema.required.filter((r): r is string => typeof r === "string") : [];
}

/** What a new item or field starts as: its `default`, or an empty value of its kind. */
export function initialValue(schema: Schema, root: Schema): JsonValue | undefined {
  const resolved = resolve(schema, root);
  if ("default" in resolved) return structuredClone(resolved.default as JsonValue);
  if ("const" in resolved) return structuredClone(resolved.const as JsonValue);
  switch (kindOf(resolved).kind) {
    case "object":
      return {};
    case "array":
      return [];
    case "string":
      return "";
    case "boolean":
      return false;
    default:
      return undefined;
  }
}

/** Sets or, for undefined, removes a key, returning a new object. */
export function withKey(value: JsonObject | undefined, key: string, next: JsonValue | undefined): JsonObject {
  const copy: JsonObject = { ...(value ?? {}) };
  if (next === undefined) delete copy[key];
  else copy[key] = next;
  return copy;
}

/** A pattern as a hint: whether the text matches it, or null when the browser cannot read the pattern. */
export function matchesPattern(pattern: unknown, text: string): boolean | null {
  if (typeof pattern !== "string") return null;
  try {
    return new RegExp(pattern, "u").test(text);
  } catch {
    return null;
  }
}
