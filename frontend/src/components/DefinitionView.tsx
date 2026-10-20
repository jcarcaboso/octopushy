import { Fragment, type ReactNode } from "react";
import type { Issue } from "../api/types";
import { CopyButton } from "./CopyButton";

/** Light YAML tinting: comments, keys, and list dashes. Values stay plain. */
function tint(line: string): ReactNode {
  const comment = line.search(/(^|\s)#/);
  const code = comment >= 0 ? line.slice(0, comment) : line;
  const rest = comment >= 0 ? line.slice(comment) : "";
  const match = /^(\s*)(- )?([^\s:#][^:#]*?)(:)(\s|$)(.*)$/.exec(code);
  let body: ReactNode = code;
  if (match) {
    const [, indent, dash, key, colon, space, value] = match;
    body = (
      <>
        {indent}
        {dash ? <span className="y-dash">{dash}</span> : null}
        <span className="y-key">{key}</span>
        {colon}
        {space}
        {value}
      </>
    );
  } else {
    const list = /^(\s*)(- )(.*)$/.exec(code);
    if (list) {
      body = (
        <>
          {list[1]}
          <span className="y-dash">{list[2]}</span>
          {list[3]}
        </>
      );
    }
  }
  return (
    <>
      {body}
      {rest ? <span className="y-comment">{rest}</span> : null}
    </>
  );
}

interface Props {
  file: string | null;
  text: string | null;
  issues?: Issue[];
}

/** A definition file, read-only, with line numbers; lines with issues are marked. */
export function DefinitionView({ file, text, issues = [] }: Props) {
  if (text === null) {
    return <p className="muted">The definition text is not available for the current revision.</p>;
  }
  const marks = new Map<number, Issue[]>();
  for (const issue of issues) {
    const line = issue.line;
    if (line && (!file || issue.file === file)) marks.set(line, [...(marks.get(line) ?? []), issue]);
  }
  const lines = text.replace(/\n$/, "").split("\n");
  return (
    <div className="definition">
      <div className="codeblock__bar">
        <code className="codeblock__label mono">{file ?? "definition"}</code>
        <CopyButton text={text} />
      </div>
      <div className="definition__scroll" tabIndex={0} role="region" aria-label={`${file ?? "Definition"}, read-only`}>
        <pre className="definition__code">
          {lines.map((line, i) => {
            const n = i + 1;
            const lineIssues = marks.get(n);
            const severity = lineIssues?.some((x) => x.severity === "error") ? "error" : lineIssues ? "warning" : null;
            return (
              <Fragment key={n}>
                <span
                  className={severity ? `definition__line definition__line--${severity}` : "definition__line"}
                  title={lineIssues?.map((x) => x.message).join("\n")}
                >
                  <span className="definition__number" aria-hidden="true">
                    {n}
                  </span>
                  <span className="definition__text">{tint(line) || " "}</span>
                </span>
                {lineIssues?.map((issue, j) => (
                  <span key={j} className={`definition__note definition__note--${issue.severity}`}>
                    <span className="definition__number" aria-hidden="true" />
                    <span className="definition__text">
                      {issue.severity === "error" ? "Error" : "Warning"} on line {n}: {issue.message}
                    </span>
                  </span>
                ))}
              </Fragment>
            );
          })}
        </pre>
      </div>
    </div>
  );
}
