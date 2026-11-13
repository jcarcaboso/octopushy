import { FileCode, Lock } from "lucide-react";
import { useState } from "react";
import { Link } from "react-router";
import type { Issue } from "../api/types";
import { readDefinition, type DefinitionField, type DefinitionKind, type DefinitionSection } from "../lib/definition";
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

function Block({ section, onLines }: { section: DefinitionSection; onLines: () => void }) {
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
          return (
            <div key={field.key} className={isWide(field) ? "sheet-field sheet-field--wide" : "sheet-field"}>
              <div className="sheet-field__label">{field.label}</div>
              <div className={`sheet-field__value sheet-field__value--${field.value.kind}`}>
                <FieldValue field={field} />
              </div>
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
}

/**
 * The definition read as sections of labelled values, each opening the YAML at its own lines.
 * A definition that cannot be read that way, or has an error, is shown as YAML with its marks instead.
 */
export function DefinitionSheet({ file, text, kind, issues = [] }: SheetProps) {
  const [shown, setShown] = useState<[number, number] | null>(null);
  const sections = text === null || issues.some((issue) => issue.severity === "error") ? null : readDefinition(text, kind);
  const name = file?.split("/").pop();
  return (
    <section className="sheet" id="definition" aria-labelledby="definition-title">
      <header className="sheet__bar">
        <span className="inline-group">
          <h2 className="sheet__title" id="definition-title">
            Definition
          </h2>
          {name ? <span className="mono small muted">{name}</span> : null}
        </span>
        {sections?.length && text !== null ? <ViewYamlButton file={file} text={text} issues={issues} small /> : null}
      </header>
      {sections?.length ? (
        sections.map((section) => <Block key={section.id} section={section} onLines={() => setShown(section.lines)} />)
      ) : (
        <div className="sheet__yaml">
          <DefinitionView file={file} text={text} issues={issues} />
        </div>
      )}
      <DefinitionDialog file={file} text={text} issues={issues} open={shown !== null} highlight={shown} onClose={() => setShown(null)} />
    </section>
  );
}
