import { Bot, CircleCheck, Circle, GitBranch, KeyRound } from "lucide-react";
import { Link } from "react-router";
import { useOverview } from "../api/queries";
import type { ObserveFailure, Overview, RunSummary } from "../api/types";
import { StatusBadge } from "../components/Badge";
import { WaitingNote } from "../components/WaitingNote";
import { Callout } from "../components/Callout";
import { Card } from "../components/Card";
import { KeyValue } from "../components/KeyValue";
import { Commit } from "../components/Mono";
import { PageHeader } from "../components/PageHeader";
import { RelativeTime } from "../components/RelativeTime";
import { RunTable } from "../components/RunTable";
import { EmptyState, ErrorMessage, PageSkeleton } from "../components/States";
import { statusStyle } from "../components/status";
import { operationLabel, runResourcePath } from "../lib/format";
import { isCredentialBlock } from "../lib/observe";

const tiles: { key: keyof Overview["counts"]; status: string; filter: string; label?: string }[] = [
  { key: "inSync", status: "in-sync", filter: "in-sync" },
  { key: "pending", status: "pending", filter: "pending" },
  { key: "running", status: "running", filter: "running,queued", label: "Queued or running" },
  { key: "awaitingApproval", status: "awaiting-approval", filter: "awaiting-approval" },
  { key: "blocked", status: "blocked", filter: "blocked" },
  { key: "failed", status: "failed", filter: "failed" },
  { key: "uncertain", status: "uncertain", filter: "uncertain" },
  { key: "invalid", status: "invalid", filter: "invalid" },
  { key: "missing", status: "missing", filter: "missing" },
];

function StatusTiles({ counts }: { counts: Overview["counts"] }) {
  return (
    <section aria-labelledby="tiles-title" className="tiles-section">
      <h2 id="tiles-title" className="visually-hidden">
        Guests by status
      </h2>
      <ul className="tiles">
        <li>
          <Link to="/inventory" className="tile tile--total">
            <span className="tile__label">Guests</span>
            <span className="tile__value num">{counts.guests}</span>
          </Link>
        </li>
        {tiles.map((tile) => {
          const value = counts[tile.key];
          const style = statusStyle(tile.status);
          return (
            <li key={tile.key}>
              <Link
                to={`/inventory?status=${tile.filter}`}
                className={value > 0 ? `tile tile--${style.tone}` : "tile tile--zero"}
                aria-label={`${value} ${value === 1 ? "guest" : "guests"} ${tile.label ?? style.label}`.toLowerCase()}
              >
                <span className="tile__label">
                  <span className={`tile__dot tile__dot--${style.tone}`} aria-hidden="true" />
                  {tile.label ?? style.label}
                </span>
                <span className="tile__value num">{value}</span>
              </Link>
            </li>
          );
        })}
      </ul>
    </section>
  );
}

function GettingStarted({ overview }: { overview: Overview }) {
  const repositoryDone = Boolean(overview.repository.url);
  const agentDone = overview.agentsTotal > 0;
  const steps = [
    {
      done: repositoryDone,
      icon: GitBranch,
      title: "Connect the lab repository",
      body: "Octopushy reads labs, installations, and guests from Git.",
      to: "/repository",
      action: "Repository settings",
    },
    {
      done: agentDone,
      icon: Bot,
      title: "Enroll an agent",
      body: "Agents observe Proxmox and run plans and applies; nothing runs without one.",
      to: "/agents",
      action: "Enroll an agent",
    },
    {
      done: false,
      icon: KeyRound,
      title: "Set the variables the definitions reference",
      body: "Proxmox tokens and state-backend credentials live in the variable store, not in Git.",
      to: "/variables",
      action: "Variables",
    },
  ];
  return (
    <Card title="Get started">
      <ol className="checklist">
        {steps.map((step) => (
          <li key={step.title} className={step.done ? "checklist__item checklist__item--done" : "checklist__item"}>
            {step.done ? (
              <CircleCheck className="checklist__mark" size={20} aria-label="Done" />
            ) : (
              <Circle className="checklist__mark" size={20} aria-label="Not done yet" />
            )}
            <div className="checklist__text">
              <div className="checklist__title">{step.title}</div>
              <div className="muted">{step.body}</div>
            </div>
            <Link to={step.to} className={step.done ? "button button--small" : "button button--small button--primary"}>
              {step.action}
            </Link>
          </li>
        ))}
      </ol>
    </Card>
  );
}

function AttentionList({ runs, observeErrors }: { runs: RunSummary[]; observeErrors: ObserveFailure[] }) {
  if (runs.length === 0 && observeErrors.length === 0) {
    return <EmptyState compact icon={CircleCheck} title="Nothing needs attention" />;
  }
  return (
    <ul className="attention">
      {observeErrors.map((failure) => (
        <li key={failure.installationId} className="attention__item">
          <StatusBadge status={isCredentialBlock(failure.error) ? "blocked" : "failed"} />
          <div className="attention__main">
            <div>
              <Link to={`/installations/${failure.installationId}`} className="strong-link">
                Observing {failure.name}
              </Link>{" "}
              <span className="mono small muted">{failure.address}</span>
            </div>
            <div className="muted small">{failure.error}</div>
          </div>
          <RelativeTime value={failure.at} />
        </li>
      ))}
      {runs.map((run) => (
        <li key={run.id} className="attention__item">
          <StatusBadge status={run.status} />
          <div className="attention__main">
            <div>
              <Link to={`/runs/${run.id}`} className="strong-link">
                Run #{run.number} · {operationLabel(run.operation)}
              </Link>{" "}
              <Link to={runResourcePath(run)} className="mono small">
                {run.address}
              </Link>
            </div>
            {run.detail ? <div className="muted small">{run.detail}</div> : null}
            {run.waiting && run.waiting !== run.detail ? <WaitingNote text={run.waiting} small /> : null}
          </div>
          <RelativeTime value={run.createdAt} />
        </li>
      ))}
    </ul>
  );
}

function RepositorySummary({ overview }: { overview: Overview }) {
  const repo = overview.repository;
  if (!repo.url) {
    return (
      <EmptyState compact icon={GitBranch} title="No lab repository yet" actions={<Link to="/repository" className="button button--small">Connect it</Link>} />
    );
  }
  const revision = repo.revision;
  return (
    <div className="stack">
      {repo.lastFetchError ? (
        <Callout kind="danger" title="The last fetch failed">
          <span className="mono small">{repo.lastFetchError}</span>
        </Callout>
      ) : null}
      <KeyValue
        items={[
          { term: "URL", value: <span className="mono break">{repo.url}</span> },
          { term: "Branch", value: <span className="mono">{repo.branch}</span> },
          { term: "Last fetch", value: <RelativeTime value={repo.lastFetchAt} empty="Not fetched yet" /> },
          {
            term: "Revision",
            value: revision ? (
              <span>
                <Commit commit={revision.commit} /> {revision.subject}{" "}
                {revision.valid ? null : <StatusBadge status="invalid" />}
              </span>
            ) : (
              <span className="muted">None loaded yet</span>
            ),
          },
        ]}
      />
    </div>
  );
}

export function OverviewPage() {
  const { data, error, isPending } = useOverview();

  if (isPending) return <PageSkeleton />;
  if (!data) return <ErrorMessage error={error} title="The overview could not be loaded" />;

  const connected = data.agentsConnected;
  const total = data.agentsTotal;
  const fresh = !data.repository.url || total === 0;

  return (
    <>
      <PageHeader title="Overview" />
      {error ? <ErrorMessage error={error} title="Showing the last data; refreshing failed" /> : null}
      <div className="stack stack--large">
        {fresh ? <GettingStarted overview={data} /> : null}
        <StatusTiles counts={data.counts} />
        <div className="columns columns--main-side">
          <div className="stack stack--large">
            <Card title="Needs attention" meta={data.attention.length + data.observeErrors.length || undefined}>
              <AttentionList runs={data.attention} observeErrors={data.observeErrors} />
            </Card>
            <Card title="Recent runs" flush actions={<Link to="/runs" className="button button--small button--ghost">All runs</Link>}>
              <RunTable runs={data.recent} label="Recent runs" emptyText="No runs yet. Runs start when a revision changes a guest, or by hand." />
            </Card>
          </div>
          <div className="stack stack--large">
            <Card title="Repository" actions={<Link to="/repository" className="button button--small button--ghost">Settings</Link>}>
              <RepositorySummary overview={data} />
            </Card>
            <Card title="Agents" actions={<Link to="/agents" className="button button--small button--ghost">Manage</Link>}>
              {total === 0 ? (
                <EmptyState compact icon={Bot} title="No agents enrolled" actions={<Link to="/agents" className="button button--small">Enroll an agent</Link>}>
                  Runs wait until an agent connects.
                </EmptyState>
              ) : (
                <p className="agents-summary">
                  <span className={connected > 0 ? "conn conn--on" : "conn conn--off"} aria-hidden="true" />
                  <span className="num strong">{connected}</span> of <span className="num">{total}</span>{" "}
                  {total === 1 ? "agent" : "agents"} connected
                </p>
              )}
            </Card>
          </div>
        </div>
      </div>
    </>
  );
}
