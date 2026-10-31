import type { DraftChange } from "../../api/types";
import { Badge } from "../../components/Badge";

const tones = { added: "success", modified: "info", deleted: "danger" } as const;

/** The files a draft changes; each opens in the editor when `onSelect` is given. */
export function ChangeList({ changes, onSelect }: { changes: DraftChange[]; onSelect?: (path: string) => void }) {
  if (changes.length === 0) return <p className="muted small">No changes yet.</p>;
  return (
    <ul className="change-list">
      {changes.map((change) => (
        <li key={change.path}>
          <Badge tone={tones[change.change as keyof typeof tones] ?? "neutral"}>
            {change.change === "added" ? "Added" : change.change === "deleted" ? "Deleted" : "Modified"}
          </Badge>
          {onSelect ? (
            <button type="button" className="link-button mono" onClick={() => onSelect(change.path)}>
              {change.path}
            </button>
          ) : (
            <code className="mono">{change.path}</code>
          )}
        </li>
      ))}
    </ul>
  );
}
