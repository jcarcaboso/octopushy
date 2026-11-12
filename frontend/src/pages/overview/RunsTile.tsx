import { Link } from "react-router";
import type { Overview, RunSummary } from "../../api/types";
import { StatusMark } from "../../components/Badge";
import { RelativeTime } from "../../components/RelativeTime";
import { Table } from "../../components/Table";
import { statusStyle } from "../../components/status";
import { operationLabel, runResourcePath } from "../../lib/format";
import { runTarget } from "../../lib/runs";
import { Tile } from "./Tile";

/** "14 in the last 7 days: 12 succeeded, 1 failed, 1 not finished", without the parts that are zero. Null without runs. */
function weekLine(week: Overview["week"]): string | null {
  if (week.total === 0) return null;
  const parts = (
    [
      [week.succeeded, "succeeded"],
      [week.failed, "failed"],
      [week.cancelled, "cancelled"],
      [week.unfinished, "not finished"],
    ] as const
  )
    .filter(([n]) => n > 0)
    .map(([n, word]) => `${n} ${word}`);
  return `${week.total} in the last 7 days: ${parts.join(", ")}`;
}

function RunRow({ run }: { run: RunSummary }) {
  const target = runTarget(run);
  const style = statusStyle(run.status);
  const path = runResourcePath(run);
  const resource = (name: string) => <Link to={path}>{name}</Link>;
  // The VM a run touched: the machine itself, or the machine its service runs on, which the summary cannot link.
  const vm = target.type === "Machine" ? resource(target.name) : target.type === "Service" ? target.host : null;
  return (
    <tr>
      <td>
        <span className="overview-run">
          <StatusMark status={run.status} decorative />
          <Link to={`/runs/${run.id}`} className="overview-run__number num">
            #{run.number}
          </Link>
          <span className="muted small">{operationLabel(run.operation)}</span>
        </span>
      </td>
      <td>{target.type === "Node" ? resource(target.name) : target.node}</td>
      <td className="strong">{vm}</td>
      <td className="muted">
        {target.type === "Service" ? <>Service {resource(target.name)}</> : target.type === "Other" ? run.resourceKind : target.type}
      </td>
      <td className={`overview-run__state overview-run__state--${style.tone}`}>{style.label}</td>
      <td className="muted overview-run__started">
        <RelativeTime value={run.createdAt} />
      </td>
    </tr>
  );
}

export function RunsTile({ overview }: { overview: Overview }) {
  const line = weekLine(overview.week);
  const runs = overview.recent.slice(0, 8);
  return (
    <Tile id="runs" title="Runs" link={{ to: "/runs", label: "All runs" }} wide>
      {line ? <p className="muted small num">{line}</p> : null}
      {runs.length === 0 ? (
        <p className="muted">No runs yet. Runs start when a revision changes a guest, or by hand.</p>
      ) : (
        <Table label="Recent runs" className="overview-runs">
          <thead>
            <tr>
              <th scope="col">Run</th>
              <th scope="col">Node</th>
              <th scope="col">VM</th>
              <th scope="col">Type</th>
              <th scope="col">State</th>
              <th scope="col" className="overview-run__started">
                Started
              </th>
            </tr>
          </thead>
          <tbody>
            {runs.map((run) => (
              <RunRow key={run.id} run={run} />
            ))}
          </tbody>
        </Table>
      )}
    </Tile>
  );
}
