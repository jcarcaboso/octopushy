import { ChevronRight } from "lucide-react";
import { Fragment, useState } from "react";
import { Link } from "react-router";
import type { RunSummary } from "../api/types";
import { emphasize } from "../lib/changes";
import { humanize, reasonLabel, runResourcePath } from "../lib/format";
import { foundNothingNew, namedChanges, runTitle, type RunChange } from "../lib/runs";
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

/** One change: its name and the new value, with the part that differs from the old one bright. */
function Change({ change }: { change: RunChange }) {
  const { name, before, after } = change;
  return (
    <span className="run-change" title={`${name}: ${before ?? "(new)"} → ${after ?? "(removed)"}`}>
      <span className="run-change__name">{name}</span>
      {after === null ? (
        <span className="run-change__value run-change__value--gone">{before}</span>
      ) : (
        <span className="run-change__value">
          {before === null
            ? <b>{after}</b>
            : emphasize(before, after).map((s, i) => (s.changed ? <b key={i}>{s.text}</b> : <span key={i}>{s.text}</span>))}
        </span>
      )}
    </span>
  );
}

/** What the run changed against the last run, or that it found nothing new. Nothing when it compared nothing. */
function Changes({ changes }: { changes: RunSummary["changes"] }) {
  if (!changes) return null;
  if (changes.length === 0) return <span className="run-changes run-changes--none">Nothing new</span>;
  return (
    <span className="run-changes">
      {changes.slice(0, namedChanges).map((change, i) => (
        <Change key={`${i}-${change.name}`} change={change} />
      ))}
      {changes.length > namedChanges ? <span className="muted">+{changes.length - namedChanges} more</span> : null}
    </span>
  );
}

/** Neighbouring runs of the same title and reason that found nothing new become one group; any other run is alone. */
function fold(runs: RunSummary[]): RunSummary[][] {
  const groups: RunSummary[][] = [];
  for (const run of runs) {
    const last = groups.at(-1);
    const joins =
      last !== undefined &&
      foundNothingNew(run) &&
      foundNothingNew(last[0]) &&
      runTitle(last[0]) === runTitle(run) &&
      last[0].reason === run.reason;
    if (joins) last.push(run);
    else groups.push([run]);
  }
  return groups;
}

export function RunTable({ runs, label, showAddress = true, emptyText = "No runs yet." }: Props) {
  const now = useNow();
  // Groups are known by their oldest run, which stays the same when a newer run joins.
  const [unfolded, setUnfolded] = useState<ReadonlySet<string>>(() => new Set());
  const columns = showAddress ? 8 : 7;

  const row = (run: RunSummary, folded = false) => {
    const duration = elapsed(run.startedAt, run.finishedAt, now);
    const live = run.finishedAt === null;
    // A live run that says what it will change needs no sentence about its approval policy.
    const detail = live && run.changes?.length ? null : run.detail;
    return (
      <tr key={run.id} className={folded ? "row--folded" : undefined}>
        <td className="nowrap">
          <Link to={`/runs/${run.id}`} className="strong-link">
            #{run.number}
          </Link>{" "}
          <span className={foundNothingNew(run) ? "muted" : undefined}>{runTitle(run)}</span>
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
          {run.currentStep && live ? <span className="cell-detail__step">{humanize(run.currentStep)}</span> : null}
          {detail ? <span className="muted">{detail}</span> : <Changes changes={run.changes} />}
          {run.waiting && run.waiting !== run.detail ? <WaitingNote text={run.waiting} small /> : null}
        </td>
        <td className="nowrap muted num">{duration !== null && !live ? formatDuration(duration) : null}</td>
        <td className="nowrap muted" title={run.reasonDetail ?? undefined}>
          {reasonLabel(run.reason)}
        </td>
        <td>
          <Commit commit={run.commit} />
        </td>
        <td className="nowrap">
          <RelativeTime value={run.createdAt} />
        </td>
      </tr>
    );
  };

  return (
    <Table label={label}>
      <thead>
        <tr>
          <th scope="col">Run</th>
          {showAddress ? <th scope="col">Address</th> : null}
          <th scope="col">Status</th>
          <th scope="col">What changed</th>
          <th scope="col">Took</th>
          <th scope="col">Reason</th>
          <th scope="col">Commit</th>
          <th scope="col">Created</th>
        </tr>
      </thead>
      <tbody>
        {runs.length === 0 ? <EmptyRow colSpan={columns}>{emptyText}</EmptyRow> : null}
        {fold(runs).map((group) => {
          if (group.length === 1) return row(group[0]);
          const newest = group[0];
          const oldest = group[group.length - 1];
          const open = unfolded.has(oldest.id);
          const toggle = () =>
            setUnfolded((current) => {
              const next = new Set(current);
              if (!next.delete(oldest.id)) next.add(oldest.id);
              return next;
            });
          return (
            <Fragment key={oldest.id}>
              <tr className="row--fold">
                <td colSpan={columns}>
                  <button type="button" className="run-fold" aria-expanded={open} onClick={toggle}>
                    <ChevronRight className="run-fold__chevron" size={14} aria-hidden="true" />
                    <span>
                      {group.length} runs of {runTitle(newest)} found nothing new
                    </span>
                    <span className="run-fold__range">
                      #{oldest.number} to #{newest.number} · {reasonLabel(newest.reason).toLowerCase()} ·{" "}
                      <RelativeTime value={oldest.createdAt} /> to <RelativeTime value={newest.createdAt} />
                    </span>
                  </button>
                </td>
              </tr>
              {open ? group.map((run) => row(run, true)) : null}
            </Fragment>
          );
        })}
      </tbody>
    </Table>
  );
}
