import yaml from "js-yaml";
import { Plus, Trash2 } from "lucide-react";
import { createContext, useCallback, useContext, useEffect, useId, useState, type ReactNode } from "react";
import {
  child,
  hintsFor,
  initialValue,
  isObject,
  kindOf,
  matchesPattern,
  propertyOrder,
  requiredOf,
  resolve,
  withKey,
  type FieldHints,
  type JsonObject,
  type JsonValue,
  type Schema,
  type UiHints,
} from "../lib/jsonSchema";

/** A problem the server found with one field, at a JSON pointer into the form's value. */
export interface FieldIssue {
  pointer: string;
  message: string;
}

interface FormContext {
  root: Schema;
  ui: UiHints;
  issues: FieldIssue[];
  disabled: boolean;
  /** A YAML field that does not parse holds the form back until it does. */
  reportYaml: (pointer: string, error: string | null) => void;
}

const Context = createContext<FormContext | null>(null);

function useForm(): FormContext {
  const context = useContext(Context);
  if (!context) throw new Error("A schema field is used outside a SchemaForm.");
  return context;
}

interface Props {
  schema: Schema;
  ui?: UiHints;
  value: JsonObject;
  onChange: (value: JsonObject) => void;
  issues?: FieldIssue[];
  disabled?: boolean;
  /** Called with the pointers of YAML fields that do not parse, so the form is not sent until they do. */
  onYamlErrors?: (pointers: string[]) => void;
}

/**
 * A form rendered from a JSON Schema, as a workflow's inputs schema with its `ui` hints. The server stays authoritative:
 * patterns and formats are hints here, and every change is validated there. See docs/changes-from-the-ui.md.
 */
export function SchemaForm({ schema, ui = {}, value, onChange, issues = [], disabled = false, onYamlErrors }: Props) {
  const [yamlErrors, setYamlErrors] = useState<Record<string, string>>({});
  useEffect(() => onYamlErrors?.(Object.keys(yamlErrors)), [yamlErrors, onYamlErrors]);
  const reportYaml = useCallback(
    (pointer: string, error: string | null) =>
      setYamlErrors((current) => {
        if ((current[pointer] ?? null) === error) return current;
        const next = { ...current };
        if (error === null) delete next[pointer];
        else next[pointer] = error;
        return next;
      }),
    [],
  );
  const general = issues.filter((i) => i.pointer === "");
  return (
    <Context.Provider value={{ root: schema, ui, issues, disabled, reportYaml }}>
      <div className="schema-form">
        {general.length > 0 ? <Issues issues={general} /> : null}
        <ObjectFields schema={resolve(schema, schema)} pointer="" value={value} onChange={(v) => onChange(v ?? {})} />
      </div>
    </Context.Provider>
  );
}

function Issues({ issues }: { issues: FieldIssue[] }) {
  return (
    <>
      {issues.map((issue, i) => (
        <span key={i} className="field__error" role="alert">
          {issue.message}
        </span>
      ))}
    </>
  );
}

interface FieldProps {
  schema: unknown;
  pointer: string;
  name: string;
  value: JsonValue | undefined;
  onChange: (value: JsonValue | undefined) => void;
  required?: boolean;
  /** The field is an item of an array, so it is never left out: an empty text is an empty string. */
  item?: boolean;
  onRemove?: () => void;
}

function Label({ text, name, required, htmlFor }: { text: string; name: string; required?: boolean; htmlFor?: string }) {
  const content = (
    <>
      {text}
      {required ? <span className="schema-form__required" aria-label="required">*</span> : null}
      {text !== name ? <code className="mono small muted">{name}</code> : null}
    </>
  );
  return htmlFor ? (
    <label htmlFor={htmlFor} className="field__label">
      {content}
    </label>
  ) : (
    <span className="field__label">{content}</span>
  );
}

function labelOf(schema: Schema, hints: FieldHints, name: string): string {
  return hints.label ?? (typeof schema.title === "string" ? schema.title : name);
}

function helpOf(schema: Schema, hints: FieldHints): string | null {
  return hints.help ?? (typeof schema.description === "string" ? schema.description : null);
}

function constraints(schema: Schema): string | null {
  const parts: string[] = [];
  if (typeof schema.minimum === "number" && typeof schema.maximum === "number") parts.push(`${schema.minimum} to ${schema.maximum}`);
  else if (typeof schema.minimum === "number") parts.push(`at least ${schema.minimum}`);
  else if (typeof schema.maximum === "number") parts.push(`at most ${schema.maximum}`);
  if (typeof schema.format === "string") parts.push(schema.format);
  return parts.length ? parts.join(", ") : null;
}

/** One property or item, rendered by what its schema says it is. */
function Field(props: FieldProps) {
  const { root, ui, issues } = useForm();
  const schema = resolve(props.schema, root);
  const hints = hintsFor(ui, props.pointer);
  if (hints.widget === "hidden") return null;
  const { kind, reason } = kindOf(schema, hints.widget);
  const own = issues.filter((i) => i.pointer === props.pointer);
  const shared = { ...props, schema, hints, issues: own };
  switch (kind) {
    case "object":
      return <ObjectField {...shared} />;
    case "array":
      return <ArrayField {...shared} />;
    case "boolean":
      return <BooleanField {...shared} />;
    case "enum":
      return <EnumField {...shared} />;
    case "const":
      return <ConstField {...shared} />;
    case "integer":
    case "number":
      return <NumberField {...shared} integer={kind === "integer"} />;
    case "string":
      return <StringField {...shared} />;
    default:
      return <YamlField {...shared} issues={issues.filter((i) => i.pointer === props.pointer || i.pointer.startsWith(`${props.pointer}/`))} reason={reason} />;
  }
}

interface ResolvedProps extends Omit<FieldProps, "schema"> {
  schema: Schema;
  hints: FieldHints;
  issues: FieldIssue[];
}

function Shell({ props, htmlFor, children, extra }: { props: ResolvedProps; htmlFor?: string; children: ReactNode; extra?: string | null }) {
  const help = helpOf(props.schema, props.hints);
  const hint = [constraints(props.schema), extra].filter(Boolean).join(" · ");
  return (
    <div className="field schema-form__field" data-pointer={props.pointer}>
      <div className="schema-form__label-row">
        <Label text={labelOf(props.schema, props.hints, props.name)} name={props.name} required={props.required} htmlFor={htmlFor} />
        {props.onRemove ? <RemoveButton onClick={props.onRemove} name={props.name} /> : null}
      </div>
      {children}
      {help ? <span className="field__hint">{help}</span> : null}
      {hint ? <span className="field__hint">{hint}</span> : null}
      <Issues issues={props.issues} />
    </div>
  );
}

function RemoveButton({ onClick, name }: { onClick: () => void; name: string }) {
  const { disabled } = useForm();
  return (
    <button type="button" className="icon-button" onClick={onClick} disabled={disabled} aria-label={`Remove ${name}`} title="Remove">
      <Trash2 size={14} aria-hidden="true" />
    </button>
  );
}

function StringField(props: ResolvedProps) {
  const { disabled } = useForm();
  const id = useId();
  const [touched, setTouched] = useState(false);
  const text = typeof props.value === "string" ? props.value : props.value === undefined || props.value === null ? "" : String(props.value);
  const matches = touched && text !== "" ? matchesPattern(props.schema.pattern, text) : null;
  const invalid = props.issues.length > 0 || matches === false;
  const input =
    props.hints.widget === "textarea" ? (
      <textarea id={id} rows={4} value={text} disabled={disabled} aria-invalid={invalid}
        onChange={(e) => props.onChange(e.target.value === "" && !props.item ? undefined : e.target.value)} onBlur={() => setTouched(true)} />
    ) : (
      <input id={id} type="text" value={text} disabled={disabled} aria-invalid={invalid}
        onChange={(e) => props.onChange(e.target.value === "" && !props.item ? undefined : e.target.value)} onBlur={() => setTouched(true)} />
    );
  return (
    <Shell props={props} htmlFor={id} extra={matches === false ? "This does not match the pattern the workflow expects." : null}>
      {input}
    </Shell>
  );
}

function NumberField(props: ResolvedProps & { integer: boolean }) {
  const { disabled } = useForm();
  const id = useId();
  const external = typeof props.value === "number" ? String(props.value) : "";
  const [text, setText] = useState(external);
  // A value set from outside, such as a reset, replaces what is typed; what is typed and means the same stays.
  const [seen, setSeen] = useState(props.value);
  if (seen !== props.value) {
    setSeen(props.value);
    if ((text.trim() === "" ? undefined : Number(text)) !== props.value) setText(external);
  }
  const parsed = text.trim() === "" ? undefined : Number(text);
  const notANumber = parsed !== undefined && !Number.isFinite(parsed);
  const notWhole = props.integer && parsed !== undefined && Number.isFinite(parsed) && !Number.isInteger(parsed);
  return (
    <Shell props={props} htmlFor={id} extra={notANumber ? "Not a number." : notWhole ? "A whole number." : null}>
      <input id={id} type="number" inputMode={props.integer ? "numeric" : "decimal"} className="input--narrow schema-form__number"
        value={text} disabled={disabled} aria-invalid={props.issues.length > 0 || notANumber || notWhole}
        step={props.integer ? 1 : "any"}
        min={typeof props.schema.minimum === "number" ? props.schema.minimum : undefined}
        max={typeof props.schema.maximum === "number" ? props.schema.maximum : undefined}
        onChange={(e) => {
          setText(e.target.value);
          const next = e.target.value.trim() === "" ? undefined : Number(e.target.value);
          if (next === undefined) props.onChange(props.item ? props.value : undefined);
          else if (Number.isFinite(next)) props.onChange(next);
        }} />
    </Shell>
  );
}

function BooleanField(props: ResolvedProps) {
  const { disabled } = useForm();
  const id = useId();
  const help = helpOf(props.schema, props.hints);
  return (
    <div className="field schema-form__field" data-pointer={props.pointer}>
      <div className="schema-form__label-row">
        <span className="checkbox">
          <input id={id} type="checkbox" checked={props.value === true} disabled={disabled} onChange={(e) => props.onChange(e.target.checked)} />
          <Label text={labelOf(props.schema, props.hints, props.name)} name={props.name} required={props.required} htmlFor={id} />
        </span>
        {props.onRemove ? <RemoveButton onClick={props.onRemove} name={props.name} /> : null}
      </div>
      {help ? <span className="field__hint">{help}</span> : null}
      <Issues issues={props.issues} />
    </div>
  );
}

function optionLabel(value: unknown): string {
  return value === null ? "null" : String(value);
}

function EnumField(props: ResolvedProps) {
  const { disabled } = useForm();
  const id = useId();
  const options = (props.schema.enum as JsonValue[]) ?? [];
  const index = options.findIndex((o) => o === props.value);
  const unknown = props.value !== undefined && index < 0;
  if (props.hints.widget === "radio") {
    return (
      <Shell props={props}>
        <div className="schema-form__choices" role="radiogroup">
          {options.map((option, i) => (
            <label key={i} className="checkbox">
              <input type="radio" name={id} checked={i === index} disabled={disabled} onChange={() => props.onChange(option)} />
              <span className="mono">{optionLabel(option)}</span>
            </label>
          ))}
        </div>
      </Shell>
    );
  }
  return (
    <Shell props={props} htmlFor={id} extra={unknown ? `${optionLabel(props.value)} is not one of the choices.` : null}>
      <select id={id} value={index < 0 ? "" : String(index)} disabled={disabled} aria-invalid={props.issues.length > 0 || unknown}
        onChange={(e) => props.onChange(e.target.value === "" ? (props.item ? props.value : undefined) : options[Number(e.target.value)])}>
        {index < 0 || !props.required ? <option value="">{unknown ? optionLabel(props.value) : "—"}</option> : null}
        {options.map((option, i) => (
          <option key={i} value={String(i)}>
            {optionLabel(option)}
          </option>
        ))}
      </select>
    </Shell>
  );
}

function ConstField(props: ResolvedProps) {
  return (
    <Shell props={props}>
      <code className="mono">{JSON.stringify(props.schema.const)}</code>
    </Shell>
  );
}

function ObjectField(props: ResolvedProps) {
  const help = helpOf(props.schema, props.hints);
  const title = labelOf(props.schema, props.hints, props.name);
  return (
    <fieldset className="schema-form__object" data-pointer={props.pointer}>
      <legend className="schema-form__legend">
        <span>
          {title}
          {props.required ? <span className="schema-form__required" aria-label="required">*</span> : null}
          {title !== props.name ? <code className="mono small muted"> {props.name}</code> : null}
        </span>
        {props.onRemove ? <RemoveButton onClick={props.onRemove} name={props.name} /> : null}
      </legend>
      {help ? <p className="field__hint">{help}</p> : null}
      <Issues issues={props.issues} />
      <ObjectFields schema={props.schema} pointer={props.pointer} value={isObject(props.value) ? props.value : undefined}
        onChange={(v) => props.onChange(v === undefined && props.item ? {} : v)} />
    </fieldset>
  );
}

/** An object's properties, in groups where the hints give them, then any keys the schema does not name, as YAML. */
function ObjectFields({ schema, pointer, value, onChange }: { schema: Schema; pointer: string; value: JsonObject | undefined;
  onChange: (value: JsonObject | undefined) => void }) {
  const { ui } = useForm();
  const hints = hintsFor(ui, pointer);
  const { known, extra } = propertyOrder(schema, value, hints);
  const required = requiredOf(schema);
  const properties = (isObject(schema.properties) ? schema.properties : {}) as Record<string, unknown>;
  const set = (key: string, next: JsonValue | undefined) => {
    const updated = withKey(value, key, next);
    onChange(Object.keys(updated).length === 0 && value === undefined ? undefined : updated);
  };

  const groups: { name: string | null; keys: string[] }[] = [];
  for (const key of known) {
    const group = hintsFor(ui, child(pointer, key)).group ?? null;
    const existing = group === null ? null : groups.find((g) => g.name === group);
    if (existing) existing.keys.push(key);
    else groups.push({ name: group, keys: [key] });
  }

  const render = (key: string) => (
    <Field key={key} schema={properties[key]} pointer={child(pointer, key)} name={key} value={value?.[key]}
      required={required.includes(key)} onChange={(next) => set(key, next)} />
  );
  const additional = schema.additionalProperties;
  return (
    <div className="schema-form__fields">
      {groups.map((group, i) =>
        group.name === null ? (
          group.keys.map(render)
        ) : (
          <div key={`group-${i}`} className="schema-form__group" role="group" aria-label={group.name}>
            <h3 className="schema-form__group-title">{group.name}</h3>
            {group.keys.map(render)}
          </div>
        ),
      )}
      {extra.length > 0 ? (
        <ExtraKeys pointer={pointer} value={Object.fromEntries(extra.map((k) => [k, value![k]]))}
          allowed={additional !== false}
          onChange={(rest) => {
            const kept: JsonObject = {};
            for (const key of known) if (value?.[key] !== undefined) kept[key] = value[key];
            onChange({ ...kept, ...rest });
          }} />
      ) : null}
      {known.length === 0 && extra.length === 0 && additional === false ? <p className="muted small">Nothing to set.</p> : null}
    </div>
  );
}

/** Keys of an object that its schema does not name: kept, and edited as YAML, never dropped. */
function ExtraKeys({ pointer, value, allowed, onChange }: { pointer: string; value: JsonObject; allowed: boolean;
  onChange: (value: JsonObject) => void }) {
  const { issues } = useForm();
  return (
    <YamlEditor
      pointer={`${pointer}#extra`}
      label="Other keys"
      value={value}
      reason={allowed ? "The schema does not name these keys." : "The schema does not allow these keys: remove them, or the change will not validate."}
      issues={issues.filter((i) => Object.keys(value).some((k) => i.pointer === child(pointer, k) || i.pointer.startsWith(`${child(pointer, k)}/`)))}
      onChange={(next) => onChange(isObject(next) ? next : {})}
    />
  );
}

let itemCount = 0;

interface Pair {
  key: string;
  value: JsonValue;
}

/**
 * The keys for `items`: each item keeps the key of an equal value it had before, the first unused one, so a reorder moves
 * keys with their items; an item whose value is new takes the key left at its place, else a new one. Returns `pairs` itself
 * when nothing changed.
 */
function paired(pairs: Pair[], items: JsonValue[]): Pair[] {
  if (pairs.length === items.length && pairs.every((p, i) => sameJson(p.value, items[i]))) return pairs;
  const free = pairs.map((p) => ({ ...p, used: false }));
  const result: (Pair | null)[] = items.map((value) => {
    const same = free.find((p) => !p.used && sameJson(p.value, value));
    if (!same) return null;
    same.used = true;
    return { key: same.key, value };
  });
  return result.map((pair, i) => {
    if (pair) return pair;
    const atPlace = free[i] && !free[i].used ? free[i] : undefined;
    if (atPlace) atPlace.used = true;
    return { key: atPlace?.key ?? itemKey(), value: items[i] };
  });
}

/** A key for an array item that stays with it when items before it are removed. */
function itemKey(): string {
  itemCount += 1;
  return `item-${itemCount}`;
}

function ArrayField(props: ResolvedProps) {
  const { root, disabled } = useForm();
  const items = Array.isArray(props.value) ? props.value : [];
  // One key per item, paired with the item's value: removing, adding or reordering items, here or from outside, keeps each
  // key, and so each item's fields and what they hold, such as YAML being typed, with its own item.
  const [pairs, setPairs] = useState<Pair[]>(() => items.map((value) => ({ key: itemKey(), value })));
  const matched = paired(pairs, items);
  if (matched !== pairs) setPairs(matched);
  const keys = matched.map((p) => p.key);
  const itemSchema = resolve(props.schema.items, root);
  const itemKind = kindOf(itemSchema).kind;
  const id = useId();
  if (itemKind === "enum" && props.hints.widget !== "text") {
    const options = (itemSchema.enum as JsonValue[]) ?? [];
    const outside = items.filter((v) => !options.includes(v));
    return (
      <Shell props={props} extra={outside.length ? `Not among the choices: ${outside.map(optionLabel).join(", ")}.` : null}>
        <div className="schema-form__choices" role="group" aria-labelledby={id}>
          {options.map((option, i) => (
            <label key={i} className="checkbox">
              <input type="checkbox" checked={items.includes(option)} disabled={disabled}
                onChange={(e) => {
                  const next = e.target.checked ? [...items, option] : items.filter((v) => v !== option);
                  props.onChange(next.length === 0 && props.value === undefined ? undefined : next);
                }} />
              <span className="mono">{optionLabel(option)}</span>
            </label>
          ))}
        </div>
      </Shell>
    );
  }

  const set = (index: number, next: JsonValue | undefined) => {
    const updated = items.map((v, i) => (i === index ? (next ?? null) : v));
    setPairs(matched.map((p, i) => (i === index ? { key: p.key, value: updated[i] } : p)));
    props.onChange(updated);
  };
  const remove = (index: number) => {
    const next = items.filter((_, i) => i !== index);
    setPairs(matched.filter((_, i) => i !== index));
    props.onChange(next.length === 0 && props.value === undefined ? undefined : next);
  };
  const add = () => {
    const value = initialValue(itemSchema, root) ?? null;
    setPairs([...matched, { key: itemKey(), value }]);
    props.onChange([...items, value]);
  };
  const help = helpOf(props.schema, props.hints);
  const title = labelOf(props.schema, props.hints, props.name);
  return (
    <fieldset className="schema-form__object schema-form__array" data-pointer={props.pointer}>
      <legend className="schema-form__legend">
        <span>
          {title}
          {props.required ? <span className="schema-form__required" aria-label="required">*</span> : null}
          {title !== props.name ? <code className="mono small muted"> {props.name}</code> : null}
        </span>
        {props.onRemove ? <RemoveButton onClick={props.onRemove} name={props.name} /> : null}
      </legend>
      {help ? <p className="field__hint">{help}</p> : null}
      <Issues issues={props.issues} />
      <div className="schema-form__items">
        {items.length === 0 ? <p className="muted small">None.</p> : null}
        {items.map((item, index) => (
          <Field key={keys[index] ?? `new-${index}`} schema={props.schema.items} pointer={child(props.pointer, index)} name={`${props.name}[${index + 1}]`}
            value={item} item onChange={(next) => set(index, next)} onRemove={() => remove(index)} />
        ))}
      </div>
      <div>
        <button type="button" className="button button--small" onClick={add} disabled={disabled}>
          <Plus size={14} aria-hidden="true" />
          Add to {props.name}
        </button>
      </div>
    </fieldset>
  );
}

function YamlField(props: ResolvedProps & { reason?: string }) {
  return (
    <YamlEditor pointer={props.pointer} label={labelOf(props.schema, props.hints, props.name)} name={props.name} value={props.value}
      reason={props.reason} help={helpOf(props.schema, props.hints)} issues={props.issues} required={props.required}
      onRemove={props.onRemove} onChange={props.onChange} />
  );
}

type Parsed = { ok: true; value: JsonValue | undefined } | { ok: false; error: string };

/** YAML text as a value: empty text is no value. */
function parseYaml(text: string): Parsed {
  if (text.trim() === "") return { ok: true, value: undefined };
  try {
    return { ok: true, value: yaml.load(text, { schema: yaml.CORE_SCHEMA }) as JsonValue };
  } catch (e) {
    return { ok: false, error: e instanceof Error ? e.message.split("\n")[0] : "Not valid YAML." };
  }
}

function sameJson(a: JsonValue | undefined, b: JsonValue | undefined): boolean {
  return JSON.stringify(a) === JSON.stringify(b);
}

function dump(value: JsonValue | undefined): string {
  return value === undefined ? "" : yaml.dump(value, { lineWidth: -1, noRefs: true }).replace(/\n$/, "");
}

/** Part of the value edited as YAML, where the form does not render the schema. */
function YamlEditor({ pointer, label, name, value, reason, help, issues, required, onRemove, onChange }: {
  pointer: string; label: string; name?: string; value: JsonValue | undefined; reason?: string; help?: string | null;
  issues: FieldIssue[]; required?: boolean; onRemove?: () => void; onChange: (value: JsonValue | undefined) => void;
}) {
  const { disabled, reportYaml } = useForm();
  const id = useId();
  const [text, setText] = useState(() => dump(value));
  // A value changed from outside, such as a reset, replaces the text; text that means the same value stays as typed.
  const [seen, setSeen] = useState(value);
  if (!sameJson(seen, value)) {
    setSeen(value);
    const current = parseYaml(text);
    if (!current.ok || !sameJson(current.value, value)) setText(dump(value));
  }
  const parsed = parseYaml(text);
  const error = parsed.ok ? null : parsed.error;
  useEffect(() => reportYaml(pointer, error), [pointer, error, reportYaml]);
  useEffect(() => () => reportYaml(pointer, null), [pointer, reportYaml]);
  return (
    <div className="field schema-form__field" data-pointer={pointer}>
      <div className="schema-form__label-row">
        <Label text={label} name={name ?? label} required={required} htmlFor={id} />
        {onRemove ? <RemoveButton onClick={onRemove} name={name ?? label} /> : null}
      </div>
      <textarea id={id} className="mono schema-form__yaml" rows={Math.min(14, Math.max(3, text.split("\n").length + 1))} value={text}
        disabled={disabled} spellCheck={false} aria-invalid={Boolean(error) || issues.length > 0}
        onChange={(e) => {
          setText(e.target.value);
          const next = parseYaml(e.target.value);
          if (next.ok) {
            setSeen(next.value);
            onChange(next.value);
          }
        }} />
      <span className="field__hint">YAML{reason ? `: ${reason}` : ""}</span>
      {help ? <span className="field__hint">{help}</span> : null}
      {error ? <span className="field__error" role="alert">Not valid YAML: {error}</span> : null}
      <Issues issues={issues} />
    </div>
  );
}
