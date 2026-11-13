import { CircleCheck, TriangleAlert } from "lucide-react";
import { useMemo, type DragEvent } from "react";
import { check, detect, formatSummary, splitLine, summarize } from "./format";

interface EditorProps {
  id?: string;
  label: string;
  value: string;
  onChange: (value: string) => void;
  placeholder?: string;
  /** Rows to show at least; the box grows with its content past them. */
  minRows?: number;
  autoFocus?: boolean;
  describedBy?: string;
}

/** A monospace box with line numbers that never wraps. A text file dropped on it replaces the text. */
export function ValueEditor({ id, label, value, onChange, placeholder, minRows = 3, autoFocus, describedBy }: EditorProps) {
  const count = value.split("\n").length;
  const numbers = Array.from({ length: count }, (_, i) => i + 1).join("\n");

  async function drop(event: DragEvent<HTMLDivElement>) {
    const file = event.dataTransfer.files[0];
    if (!file) return;
    event.preventDefault();
    onChange((await file.text()).replace(/\r\n/g, "\n"));
  }

  return (
    <div className="vars-code vars-editor" onDragOver={(event) => event.dataTransfer.types.includes("Files") && event.preventDefault()} onDrop={(e) => void drop(e)}>
      <pre className="vars-code__numbers" aria-hidden="true">
        {numbers}
      </pre>
      <textarea
        id={id}
        aria-label={label}
        className="vars-editor__text"
        value={value}
        onChange={(event) => onChange(event.target.value)}
        rows={Math.max(minRows, count + 1)}
        wrap="off"
        spellCheck={false}
        autoComplete="off"
        autoCapitalize="off"
        placeholder={placeholder}
        autoFocus={autoFocus}
        aria-describedby={describedBy}
      />
    </div>
  );
}

/** How the value reads, with any problems as warnings that never block saving; and what changed, when there is a base. */
export function FormatLine({ id, value, base }: { id?: string; value: string; base?: string }) {
  const format = detect(value);
  const problems = useMemo(() => check(value, format), [value, format]);
  const changes = base === undefined ? null : summarize(base, value, detect(base));
  return (
    <div id={id} className="vars-format">
      {problems.length > 0 ? (
        <ul className="vars-format__problems">
          {problems.map((problem) => (
            <li key={`${problem.line}:${problem.message}`}>
              <TriangleAlert size={14} aria-hidden="true" />
              <span>
                Line {problem.line}: {problem.message}
              </span>
            </li>
          ))}
        </ul>
      ) : (
        <div className={value.trim() ? "vars-format__ok" : "vars-format__ok vars-format__ok--empty"}>
          {value.trim() ? <CircleCheck size={14} aria-hidden="true" /> : null}
          <span>{formatSummary(value, format)}</span>
        </div>
      )}
      {changes !== null ? (
        <div className="vars-format__changes">
          <span className="muted">Changes</span>
          <span>{changes || "Nothing is changed yet."}</span>
        </div>
      ) : null}
    </div>
  );
}

/** A value read with line numbers, no wrapping, and the names of an env file or YAML lightly coloured. */
export function ValueViewer({ value, label }: { value: string; label: string }) {
  const format = detect(value);
  // One trailing newline ends the last line and does not start another, as the server counts them.
  const lines = (value.endsWith("\n") ? value.slice(0, -1) : value).split("\n");
  return (
    <div className="vars-code vars-viewer" role="group" aria-label={label} tabIndex={0}>
      <pre className="vars-code__numbers" aria-hidden="true">
        {lines.map((_, i) => i + 1).join("\n")}
      </pre>
      <pre className="vars-viewer__text">
        {lines.map((line, i) => {
          const part = splitLine(line, format);
          return (
            <span key={i}>
              {part.comment ? (
                <span className="vars-viewer__comment">{part.rest}</span>
              ) : (
                <>
                  {part.name ? <span className="vars-viewer__name">{part.name}</span> : null}
                  {part.sep ? <span className="vars-viewer__sep">{part.sep}</span> : null}
                  {part.rest}
                </>
              )}
              {i < lines.length - 1 ? "\n" : null}
            </span>
          );
        })}
      </pre>
    </div>
  );
}
