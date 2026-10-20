import { Link } from "react-router";
import type { RunSummary } from "../api/types";
import { humanize, operationLabel, reasonLabel, runResourcePath } from "../lib/format";
import { elapsed, formatDuration } from "../lib/time";
import { StatusBadge } from "./Badge";
import { WaitingNote } from "./WaitingNote";
import { Commit } from "./Mono";
import { RelativeTime } from "./RelativeTime";
import { useNow } from "./useNow";
import { EmptyRow, Table } from "./Table";

interface Props {
  runs: RunSummary[];
  label: string;
  /** Leave out the address column when every run belongs to the same resource. */
  showAddress?: boolean;
  emptyText?: string;
}

export function RunTable({ runs, label, showAddress = true, emptyText = "No runs yet." }: Props) {
  const now = useNow();
  const columns = showAddress ? 7 : 6;
  return (
    <Table label={label}>
      <thead>
        <tr>
          <th scope="col">Run</th>
          {showAddress ? <th scope="col">Address</th> : null}
          <th scope="col">Status</th>
          <th scope="col">Step / detail</th>
          <th scope="col">Reason</th>
          <th scope="col">Commit</th>
          <th scope="col">Created</th>
        </tr>
      </thead>
      <tbody>
        {runs.length === 0 ? <EmptyRow colSpan={columns}>{emptyText}</EmptyRow> : null}
        {runs.map((run) => {
          const duration = elapsed(run.startedAt, run.finishedAt, now);
          return (
            <tr key={run.id}>
              <td className="nowrap">
                <Link to={`/runs/${run.id}`} className="strong-link">
                  #{run.number}
                </Link>{" "}
                <span className="muted">{operationLabel(run.operation)}</span>
              </td>
              {showAddress ? (
                <td className="nowrap">
                  <Link to={runResourcePath(run)} className="mono">
                    {run.address}
                  </Link>
                </td>
              ) : null}
              <td>
                <StatusBadge status={run.status} />
              </td>
              <td className="cell-detail">
                {run.currentStep && run.finishedAt === null ? (
                  <span className="cell-detail__step">{humanize(run.currentStep)}</span>
                ) : null}
                {run.detail ? <span className="muted">{run.detail}</span> : null}
                {run.waiting && run.waiting !== run.detail ? <WaitingNote text={run.waiting} small /> : null}
                {!run.detail && !run.currentStep && duration !== null ? (
                  <span className="muted num">{formatDuration(duration)}</span>
                ) : null}
              </td>
              <td className="nowrap muted">{reasonLabel(run.reason)}</td>
              <td>
                <Commit commit={run.commit} />
              </td>
              <td className="nowrap">
                <RelativeTime value={run.createdAt} />
              </td>
            </tr>
          );
        })}
      </tbody>
    </Table>
  );
}
