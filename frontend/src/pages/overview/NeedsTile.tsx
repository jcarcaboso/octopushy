import { CircleCheck } from "lucide-react";
import { useId, type ReactNode } from "react";
import { Link } from "react-router";
import type { Overview, RunSummary } from "../../api/types";
import { StatusMark } from "../../components/Badge";
import { Commit } from "../../components/Mono";
import { RelativeTime } from "../../components/RelativeTime";
import { EmptyState } from "../../components/States";
import { WaitingNote } from "../../components/WaitingNote";
import { operationLabel } from "../../lib/format";
import { isCredentialBlock } from "../../lib/observe";
import { runTarget } from "../../lib/runs";
import { Tile } from "./Tile";

const verbs: Record<string, string> = {
  "awaiting-approval": "awaits approval",
  blocked: "is blocked",
  uncertain: "is uncertain",
  failed: "failed",
};

interface ItemProps {
  status: string;
  title: ReactNode;
  children?: ReactNode;
  to: string;
  action: string;
  primary?: boolean;
}

/** One thing to look at: a mark, what it is, and one link to where it is dealt with. */
function Item({ status, title, children, to, action, primary = false }: ItemProps) {
  const id = useId();
  return (
    <li className="need">
      <StatusMark status={status} decorative size={16} />
      <div className="need__main">
        <span className="need__title" id={id}>
          {title}
        </span>
        {children}
      </div>
      <Link to={to} className={primary ? "button button--small button--primary" : "button button--small"} aria-describedby={id}>
        {action}
      </Link>
    </li>
  );
}

function RunItem({ run }: { run: RunSummary }) {
  const target = runTarget(run);
  const approval = run.status === "awaiting-approval";
  return (
    <Item
      status={run.status}
      title={`${target.name}${target.host ? ` on ${target.host}` : ""} ${verbs[run.status] ?? run.status}`}
      to={`/runs/${run.id}`}
      action={approval ? "Review the plan" : "Open the run"}
      primary={approval}
    >
      {run.detail ? <span className="need__detail">{run.detail}</span> : null}
      {run.waiting && run.waiting !== run.detail ? <WaitingNote text={run.waiting} small /> : null}
      <span className="need__meta">
        <span className="num">#{run.number}</span> {operationLabel(run.operation)}, <RelativeTime value={run.createdAt} />
      </span>
    </Item>
  );
}

export function NeedsTile({ overview }: { overview: Overview }) {
  const { repository, observeErrors, attention, failed } = overview;
  const revision = repository.revision;
  const fetchFailed = Boolean(repository.url && repository.lastFetchError);
  const invalid = Boolean(revision && !revision.valid);
  const count = observeErrors.length + Number(fetchFailed) + Number(invalid) + attention.length + failed.length;
  return (
    <Tile id="needs" title="Needs you" meta={count || undefined} wide>
      {count === 0 ? (
        <EmptyState compact icon={CircleCheck} title="Nothing needs you" />
      ) : (
        <ul className="needs">
          {observeErrors.map((failure) => (
            <Item
              key={failure.installationId}
              status={isCredentialBlock(failure.error) ? "blocked" : "failed"}
              title={`Observing ${failure.name} failed`}
              to={`/installations/${failure.installationId}`}
              action="Open the installation"
            >
              <span className="need__detail">{failure.error}</span>
              <span className="need__meta">
                <span className="mono">{failure.address}</span>, <RelativeTime value={failure.at} />
              </span>
            </Item>
          ))}
          {fetchFailed ? (
            <Item status="failed" title="The last fetch of the lab repository failed" to="/repository" action="Open the repository">
              <span className="need__detail mono">{repository.lastFetchError}</span>
              {repository.lastFetchAt ? (
                <span className="need__meta">
                  Last fetched <RelativeTime value={repository.lastFetchAt} />
                </span>
              ) : null}
            </Item>
          ) : null}
          {invalid && revision ? (
            <Item status="invalid" title="The loaded revision is not valid" to="/repository" action="Open the repository">
              <span className="need__detail">
                <Commit commit={revision.commit} /> {revision.subject}
              </span>
            </Item>
          ) : null}
          {attention.map((run) => (
            <RunItem key={run.id} run={run} />
          ))}
          {failed.map((run) => (
            <RunItem key={run.id} run={run} />
          ))}
        </ul>
      )}
    </Tile>
  );
}
