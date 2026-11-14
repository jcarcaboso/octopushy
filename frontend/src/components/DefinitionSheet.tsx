import { FileCode, Lock } from "lucide-react";
import { useState } from "react";
import { Link } from "react-router";
import type { Issue } from "../api/types";
import { readDefinition, type DefinitionField, type DefinitionKind, type DefinitionSection, type FieldValue as Value } from "../lib/definition";
import { plural } from "../lib/format";
import { Badge } from "./Badge";
import { folderLink } from "../pages/variables/folders";
import { DefinitionView } from "./DefinitionView";
import { Dialog } from "./Dialog";
import { LabelList } from "./LabelEditor";

interface FileProps {
  file: string | null;
  text: string | null;
  issues?: Issue[];
}

/** The definition file as YAML in a dialog, with a range of lines marked when one is given. */
function DefinitionDialog({ open, file, text, issues, highlight, onClose }: FileProps & { open: boolean; highlight: [number, number] | null; onClose: () => void }) {
  return (
    <Dialog open={open} onClose={onClose} title="Definition" size="large">
      <DefinitionView file={file} text={text} issues={issues} highlight={highlight} />
    </Dialog>
  );
}

/** A "View YAML" button that opens the whole definition file in a dialog. */
export function ViewYamlButton({ small = false, ...file }: FileProps & { small?: boolean }) {
  const [open, setOpen] = useState(false);
  return (
    <>
      <button type="button" className={small ? "button button--small" : "button"} onClick={() => setOpen(true)}>
        <FileCode size={small ? 14 : 15} aria-hidden="true" />
        View YAML
      </button>
      <DefinitionDialog {...file} open={open} highlight={null} onClose={() => setOpen(false)} />
    </>
  );
}

function FieldValue({ field }: { field: DefinitionField }) {
  const value = field.value;
  switch (value.kind) {
    case "text":
      return value.text;
    case "list":
      return <LabelList labels={value.items} />;
    case "link":
      return (
        <a href={value.url} target="_blank" rel="noreferrer" className="break">
          {value.url}
        </a>
      );
    case "secret":
      return (
        <span className="inline-group">
          <Lock size={13} className="muted" aria-label="Secret" />
          <Link to={folderLink(value.key)} className="mono break">
            {value.key}
          </Link>
          {value.version !== null ? <span className="chip">v{value.version}</span> : null}
        </span>
      );
  }
}

/** Secrets, links, lists of more than two and long text take the whole row. */
function isWide({ value }: DefinitionField) {
  return value.kind === "secret" || value.kind === "link" || (value.kind === "list" && value.items.length > 2) || (value.kind === "text" && value.text.length > 30);
}

/** The definition a kind of run last applied, and the sections of the sheet that kind of run applies. */
export interface AppliedDefinition {
  text: string;
  sections: string[];
}

function written(value: Value): string {
  switch (value.kind) {
    case "text":
      return value.text;
    case "list":
      return value.items.join(", ") || "none";
    case "link":
      return value.url;
    case "secret":
      return value.version === null ? value.key : `${value.key} v${value.version}`;
  }
}

/** What each value was at the last apply, where the file now says something else; null when it was not there. */
function lastApplied(sections: DefinitionSection[], applied: AppliedDefinition[], kind: DefinitionKind): Map<string, string | null> {
  const differs = new Map<string, string | null>();
  for (const { text, sections: ids } of applied) {
    const then = readDefinition(text, kind);
    if (!then) continue;
    const was = new Map(then.flatMap((section) => section.fields.map((field) => [`${section.id}.${field.key}`, field.value] as const)));
    for (const section of sections.filter((item) => ids.includes(item.id))) {
      for (const field of section.fields) {
        const before = was.get(`${section.id}.${field.key}`);
        if (!before || JSON.stringify(before) !== JSON.stringify(field.value)) differs.set(`${section.id}.${field.key}`, before ? written(before) : null);
      }
    }
  }
  return differs;
}

function Block({ section, differs, onLines }: { section: DefinitionSection; differs: Map<string, string | null>; onLines: () => void }) {
  const [from, to] = section.lines;
  return (
    <div className="sheet-block">
      <div className="sheet-block__head">
        <h3 className="sheet-block__title">{section.title}</h3>
        <button type="button" className="link-button sheet-block__lines" onClick={onLines}>
          {from === to ? `line ${from}` : `lines ${from}–${to}`}
        </button>
      </div>
      <div className="sheet-fields">
        {section.note ? <p className="sheet-block__note">{section.note}</p> : null}
        {section.fields.map((field) => {
          const raw = field.value.kind === "text" ? field.value.raw : undefined;
          const key = `${section.id}.${field.key}`;
          const was = differs.get(key);
          return (
            <div key={field.key} className={["sheet-field", isWide(field) ? "sheet-field--wide" : "", differs.has(key) ? "sheet-field--pending" : ""].filter(Boolean).join(" ")}>
              <div className="sheet-field__label">{field.label}</div>
              <div className={`sheet-field__value sheet-field__value--${field.value.kind}`}>
                <FieldValue field={field} />
              </div>
              {differs.has(key) ? <div className="sheet-field__applied">{was === null ? "Not in the last apply" : `Last applied: ${was}`}</div> : null}
              {raw ? <div className="sheet-field__note">{raw}</div> : null}
              {field.note ? <div className="sheet-field__note">{field.note}</div> : null}
            </div>
          );
        })}
      </div>
    </div>
  );
}

interface SheetProps extends FileProps {
  kind: DefinitionKind;
  /** What was last applied, when that is not what the file says now: the values that differ are marked. */
  applied?: AppliedDefinition[];
}

/**
 * The definition read as sections of labelled values, each opening the YAML at its own lines.
 * A definition that cannot be read that way, or has an error, is shown as YAML with its marks instead.
 * A value that is not what was last applied says what was.
 */
export function DefinitionSheet({ file, text, kind, issues = [], applied = [] }: SheetProps) {
  const [shown, setShown] = useState<[number, number] | null>(null);
  const sections = text === null || issues.some((issue) => issue.severity === "error") ? null : readDefinition(text, kind);
  const differs = sections ? lastApplied(sections, applied, kind) : new Map<string, string | null>();
  const name = file?.split("/").pop();
  return (
    <section className="sheet" id="definition" aria-labelledby="definition-title">
      <header className="sheet__bar">
        <span className="inline-group">
          <h2 className="sheet__title" id="definition-title">
            Definition
          </h2>
          {name ? <span className="mono small muted">{name}</span> : null}
          {differs.size > 0 ? <Badge tone="attention">{plural(differs.size, "value")} not applied yet</Badge> : null}
        </span>
        {sections?.length && text !== null ? <ViewYamlButton file={file} text={text} issues={issues} small /> : null}
      </header>
      {sections?.length ? (
        sections.map((section) => <Block key={section.id} section={section} differs={differs} onLines={() => setShown(section.lines)} />)
      ) : (
        <div className="sheet__yaml">
          <DefinitionView file={file} text={text} issues={issues} />
        </div>
      )}
      <DefinitionDialog file={file} text={text} issues={issues} open={shown !== null} highlight={shown} onClose={() => setShown(null)} />
    </section>
  );
}
