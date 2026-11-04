import { ExternalLink } from "lucide-react";
import { Link, useParams } from "react-router";
import { isNotFound } from "../api/client";
import { useChange, useDismissChange, useRun } from "../api/queries";
import type { Change, RunSummary } from "../api/types";
import { StatusBadge } from "../components/Badge";
import { Callout } from "../components/Callout";
import { Card } from "../components/Card";
import { KeyValue } from "../components/KeyValue";
import { Commit } from "../components/Mono";
import { PageHeader } from "../components/PageHeader";
import { RelativeTime } from "../components/RelativeTime";
import { ErrorMessage, NotFound, PageSkeleton } from "../components/States";
import { operationLabel } from "../lib/format";
import { StepTimeline } from "./run/StepTimeline";

const kindLabels: Record<string, string> = {
  edit: "Edit",
  upgrade: "Upgrade",
  remove: "Removal",
  "create-guest": "New guest",
  "create-service": "New service",
};

const preApprovalLabels: Record<string, string> = {
  requested: "Asked for: the first plan is approved at deploy if it only creates",
  approved: "Approved at deploy",
  declined: "Not approved at deploy: waits for approval",
  "not-needed": "Not needed: nothing waited for approval",
};

/** Where a change is on its way, as the steps of the normal process: each is done, the current one, or still to come. */
const stages = [
  { status: "committed", label: "Committed" },
  { status: "synced", label: "Synced" },
  { status: "planning", label: "Planning" },
  { status: "waiting-for-approval", label: "Approval" },
  { status: "applying", label: "Applying" },
  { status: "applied", label: "Applied" },
] as const;

/** How far a change got: the index of the last step it reached, and whether it stopped there. */
function reached(change: Change): { index: number; stopped: "failed" | "done" | null } {
  const at = stages.findIndex((s) => s.status === change.status);
  if (at >= 0) return { index: at, stopped: change.status === "applied" ? "done" : null };
  switch (change.status) {
    case "committing":
    case "uncertain":
    case "unknown":
      return { index: 0, stopped: null };
    case "dismissed":
      return { index: 0, stopped: "failed" };
    case "no-change":
    case "superseded":
      return { index: change.syncedAt ? 1 : 0, stopped: "done" };
    case "failed":
      return { index: !change.commit ? 0 : change.runs.length > 0 ? 2 : 1, stopped: "failed" };
    default:
      return { index: 0, stopped: null };
  }
}

function Progress({ change }: { change: Change }) {
  if (change.status === "refused") return null;
  const { index, stopped } = reached(change);
  return (
    <ol className="change-progress" aria-label="Progress">
      {stages.map((stage, i) => {
        const state = i < index ? "done" : i > index ? "todo" : stopped ?? "current";
        return (
          <li key={stage.status} className={`change-progress__step change-progress__step--${state}`} aria-current={state === "current" ? "step" : undefined}>
            <span className="change-progress__dot" aria-hidden="true" />
            <span>{stage.label}</span>
            <span className="visually-hidden">{state === "done" ? " (done)" : state === "current" ? " (now)" : state === "failed" ? " (failed)" : ""}</span>
          </li>
        );
      })}
    </ol>
  );
}

/** The resource's page; a new guest or service has none until its revision is loaded. */
function resourcePath(change: Change): string | null {
  return change.resourceId ? `/${change.resourceKind === "service" ? "services" : "guests"}/${change.resourceId}` : null;
}

function ResourceLink({ change }: { change: Change }) {
  const path = resourcePath(change);
  return path ? (
    <Link to={path} className="mono break">
      {change.address}
    </Link>
  ) : (
    <span className="mono break">{change.address}</span>
  );
}

function Facts({ change }: { change: Change }) {
  return (
    <KeyValue
      items={[
        { term: "Kind", value: kindLabels[change.kind] ?? change.kind },
        {
          term: change.resourceKind === "service" ? "Service" : "Guest",
          value: <ResourceLink change={change} />,
        },
        ...(change.vmid ? [{ term: "VMID", value: <span className="mono">{change.vmid}</span> }] : []),
        { term: "Author", value: change.author },
        ...(change.preApproval
          ? [{ term: "Approve at deploy", value: <span>{preApprovalLabels[change.preApproval] ?? change.preApproval}</span> }]
          : []),
        ...(change.generatedKeys && change.generatedKeys.length > 0
          ? [
              {
                term: "Generated",
                value: (
                  <ul className="plain-list">
                    {change.generatedKeys.map((k) => (
                      <li key={k} className="mono small break">
                        {k}
                      </li>
                    ))}
                  </ul>
                ),
              },
            ]
          : []),
        {
          term: "Commit",
          value: change.commit ? (
            <span className="inline-group">
              <Commit commit={change.commit} length={10} />
              {change.commitUrl ? (
                <a href={change.commitUrl} target="_blank" rel="noreferrer" className="inline-group">
                  on GitHub <ExternalLink size={13} aria-hidden="true" />
                </a>
              ) : null}
            </span>
          ) : (
            <span className="muted">Nothing was committed</span>
          ),
        },
        { term: "Loaded from", value: <Commit commit={change.baseCommit} length={10} /> },
        {
          term: "Revision",
          value: change.revisionId ? (
            <span>
              Synced <RelativeTime value={change.syncedAt} />
            </span>
          ) : change.commit ? (
            <span className="muted">Not synced yet</span>
          ) : (
            <span className="muted">—</span>
          ),
        },
        { term: "Made", value: <RelativeTime value={change.createdAt} /> },
        {
          term: "Files",
          value: (
            <ul className="plain-list">
              {change.files.map((f) => (
                <li key={f} className="mono small break">
                  {f}
                </li>
              ))}
            </ul>
          ),
        },
      ]}
    />
  );
}

/** One run the change's revision proposed, step by step, with its approval given here when it waits for one. */
function ChangeRun({ summary }: { summary: RunSummary }) {
  const { data, error } = useRun(summary.id);
  return (
    <Card
      title={
        <span className="inline-group">
          <Link to={`/runs/${summary.id}`}>Run #{summary.number}</Link>
          <span>{operationLabel(summary.operation)}</span>
        </span>
      }
      actions={<StatusBadge status={data?.run.status ?? summary.status} />}
    >
      {data ? <StepTimeline detail={data} /> : error ? <ErrorMessage error={error} /> : <PageSkeleton rows={2} />}
    </Card>
  );
}

/** A change made in a form, from its commit to the end of the runs it proposed. Polls until it is finished. */
/** Settles a change whose push is unsettled, after asking: a person says it will not arrive. */
function Unsettled({ change }: { change: Change }) {
  const dismiss = useDismissChange();
  return (
    <Callout
      kind="uncertain"
      title={change.status === "uncertain" ? "Whether its push landed could not be checked" : "Its commit is not on the branch (yet)"}
      actions={
        <button type="button" className="button" disabled={dismiss.isPending}
          onClick={() => {
            if (window.confirm("Stop waiting for this change? This does not mean nothing was committed: if one of its commits reaches the branch later, it is followed again.")) dismiss.mutate(change.id);
          }}>
          {dismiss.isPending ? "Dismissing…" : "Dismiss"}
        </button>
      }
    >
      <p>{change.detail}</p>
      <p>Each sync looks for the commits its push prepared on the branch. Dismissing it stops the waiting, and is not proof that nothing was committed: a commit of it that arrives later is still followed.</p>
      <ErrorMessage error={dismiss.error} />
    </Callout>
  );
}

export function ChangePage() {
  const { id = "" } = useParams();
  const { data: change, error, isPending } = useChange(id);

  if (isPending) return <PageSkeleton rows={3} />;
  if (!change) return isNotFound(error) ? <NotFound what="change" /> : <ErrorMessage error={error} title="The change could not be loaded" />;
  // The API says when a change can no longer move; a dismissed one can, so it is still followed.
  const live = !change.final;
  return (
    <>
      <PageHeader
        eyebrow={
          <>
            <ResourceLink change={change} /> / Change
          </>
        }
        title={change.message}
        documentTitle={`Change · ${change.address}`}
        meta={
          <span className="inline-group">
            <StatusBadge status={change.status} />
            {change.detail ? <span>{change.detail}</span> : null}
            {live ? (
              <span className="muted small">
                <span className="live-dot" aria-hidden="true" /> Updating
              </span>
            ) : null}
          </span>
        }
      />
      <div className="stack stack--large">
        {error ? <ErrorMessage error={error} title="Showing the last data; refreshing failed" /> : null}
        {change.status === "superseded" && change.supersededBy ? (
          <Callout kind="info" title="Superseded: a later commit set this otherwise before it was applied">
            <span className="inline-group">
              {change.detail}
              <Commit commit={change.supersededBy} length={10} />
              {change.supersededByUrl ? (
                <a href={change.supersededByUrl} target="_blank" rel="noreferrer" className="inline-group">
                  on GitHub <ExternalLink size={13} aria-hidden="true" />
                </a>
              ) : null}
            </span>
          </Callout>
        ) : null}
        {change.status === "committing" ? (
          <Callout kind="info" title="Being pushed">
            The change is on record and its commit is being pushed. If this page was left mid-push, the next sync finds the
            commit on the branch, or fails the change if it never arrived.
          </Callout>
        ) : null}
        {change.status === "uncertain" || change.status === "unknown" ? <Unsettled change={change} /> : null}
        {change.status === "dismissed" ? (
          <Callout kind="info" title={`Dismissed by ${change.dismissedBy ?? "someone"}`}>
            <p>{change.detail}</p>
            <p>Octopushy stopped waiting for it. This is not proof that nothing was committed: each sync still looks for its commits, and one that arrives is followed again.</p>
          </Callout>
        ) : null}
        {change.status === "refused" ? (
          <Callout kind="danger" title="Refused: nothing was committed">
            {change.detail}
          </Callout>
        ) : null}
        {change.preApproval === "declined" ? (
          <Callout kind="warning" title="Not approved at deploy">
            {change.preApprovalDetail}
          </Callout>
        ) : change.preApproval === "approved" ? (
          <Callout kind="success" title="Approved at deploy">
            {change.preApprovalDetail}
          </Callout>
        ) : null}
        <Progress change={change} />
        <div className="columns columns--main-side">
          <div className="stack stack--large">
            {change.runs.length === 0 && change.commit ? (
              <Card title="Runs">
                <p className="muted">
                  {change.status === "committed"
                    ? "Waiting for the revision to be read."
                    : change.status === "no-change"
                      ? "The revision proposes no run: nothing a run is built from changed."
                      : change.status === "superseded"
                        ? "None: the runs of the later commit are its own change's, not this one's."
                        : (change.detail ?? "No run yet.")}
                </p>
              </Card>
            ) : null}
            {change.runs.map((run) => (
              <ChangeRun key={run.id} summary={run} />
            ))}
          </div>
          <Card title="Change">
            <Facts change={change} />
          </Card>
        </div>
      </div>
    </>
  );
}
