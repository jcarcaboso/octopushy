import { shortCommit, shortHash } from "../lib/format";
import { CopyButton } from "./CopyButton";

export function Commit({ commit, length = 7 }: { commit: string | null | undefined; length?: number }) {
  if (!commit) return <span className="muted">—</span>;
  return (
    <code className="mono commit" title={commit}>
      {shortCommit(commit, length)}
    </code>
  );
}

/** A hash shown short, full on hover, with a copy button. */
export function Hash({ value, label = "Copy hash" }: { value: string; label?: string }) {
  return (
    <span className="hash">
      <code className="mono" title={value}>
        {shortHash(value)}
      </code>
      <CopyButton text={value} label={label} compact />
    </span>
  );
}
