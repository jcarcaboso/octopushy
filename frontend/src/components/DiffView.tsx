/** A unified diff as Git prints it, one line per row, added and removed lines marked by more than color. */
export function DiffView({ diff, label = "Diff" }: { diff: string; label?: string }) {
  const lines = diff.replace(/\n$/, "").split("\n");
  return (
    <div className="codeblock diff" role="region" aria-label={label}>
      <pre className="codeblock__pre diff__pre" tabIndex={0}>
        <code>
          {lines.map((line, i) => {
            const kind = line.startsWith("diff --git") || line.startsWith("index ") || line.startsWith("--- ") || line.startsWith("+++ ")
              || line.startsWith("new file") || line.startsWith("deleted file")
              ? "meta"
              : line.startsWith("@@")
                ? "hunk"
                : line.startsWith("+")
                  ? "add"
                  : line.startsWith("-")
                    ? "del"
                    : "context";
            return (
              <span key={i} className={`diff__line diff__line--${kind}`} data-kind={kind}>
                {line || " "}
                {"\n"}
              </span>
            );
          })}
        </code>
      </pre>
    </div>
  );
}
