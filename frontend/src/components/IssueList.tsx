import type { Issue } from "../api/types";
import { Badge } from "./Badge";

function issueLocation(issue: Issue): string {
  const line = issue.line;
  return line ? `${issue.file}:${line}` : issue.file;
}

export function IssueList({ issues, emptyText = "No issues." }: { issues: Issue[]; emptyText?: string }) {
  if (issues.length === 0) return <p className="muted">{emptyText}</p>;
  const sorted = [...issues].sort(
    (a, b) =>
      (a.severity === "error" ? 0 : 1) - (b.severity === "error" ? 0 : 1) ||
      a.file.localeCompare(b.file) ||
      (a.line ?? 0) - (b.line ?? 0),
  );
  return (
    <ul className="issues">
      {sorted.map((issue, i) => (
        <li key={i} className={`issue issue--${issue.severity}`}>
          <Badge tone={issue.severity === "error" ? "danger" : "warning"}>
            {issue.severity === "error" ? "Error" : "Warning"}
          </Badge>
          <code className="mono issue__location">{issueLocation(issue)}</code>
          <span className="issue__message">{issue.message}</span>
          {issue.address ? <span className="issue__address mono muted">{issue.address}</span> : null}
        </li>
      ))}
    </ul>
  );
}
