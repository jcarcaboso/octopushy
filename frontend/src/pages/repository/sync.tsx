import { Check, LoaderCircle } from "lucide-react";
import type { RepositorySync } from "../../api/types";
import { Commit } from "../../components/Mono";

/** The steps of one sync, in order, as the server names them (RepositorySyncPhases). */
const phases = [
  ["fetch", "Fetch"],
  ["workflows", "Workflows"],
  ["validate", "Validate"],
  ["apply", "Apply"],
] as const;

function Track({ phase, done }: { phase: string; done: boolean }) {
  const at = phases.findIndex(([name]) => name === phase);
  return (
    <span className="track" aria-hidden="true">
      {phases.map(([name], index) => (
        <span
          key={name}
          className={`track__segment ${done || index < at ? "track__segment--success" : index === at ? "track__segment--now" : "track__segment--todo"}`}
        />
      ))}
    </span>
  );
}

/**
 * The sync as the first row of the history: a real row from the first second, filled in as the server learns the head.
 * The change column carries the steps; the Definition column answers last.
 */
export function SyncRow({ sync, done, nothingNew, branch }: { sync: RepositorySync; done: boolean; nothingNew: boolean; branch: string }) {
  const step = phases.find(([name]) => name === sync.phase)?.[1] ?? sync.phase;
  const label = done
    ? `Up to date: no new commits on ${branch}`
    : !sync.commit
      ? `Fetching ${branch}…`
      : nothingNew
        ? `No new commits on ${branch}`
        : null;
  return (
    <li className="revs__item revs__item--sync">
      <div className="revs__row">
        <span>{sync.commit && !nothingNew && !done ? <Commit commit={sync.commit} /> : <span className="muted">—</span>}</span>
        <span className="revs__change">
          <span className="sync-steps" role="status" aria-label={done ? "Synced, up to date" : `Syncing: ${step}`}>
            <Track phase={sync.phase} done={done} />
            {label ? <span>{label}</span> : null}
          </span>
          {label ? null : (
            <span className="sync-subject" title={sync.subject ?? undefined}>
              {sync.subject}
            </span>
          )}
        </span>
        <span className="muted small">{sync.commit && !nothingNew && !done ? sync.author : "—"}</span>
        <span className="muted small nowrap">{sync.commit ? "now" : "—"}</span>
        <span>
          {done ? (
            <span className="sync-check sync-check--done">
              <Check size={14} aria-hidden="true" />
              Up to date
            </span>
          ) : (
            <span className="sync-check">
              <LoaderCircle size={14} aria-hidden="true" className="spin" />
              {sync.phase === "fetch" ? "Fetching" : sync.phase === "apply" ? "Applying" : "Validating"}
            </span>
          )}
        </span>
      </div>
    </li>
  );
}
