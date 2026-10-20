import { Ban, CircleX } from "lucide-react";
import { useState } from "react";
import { isNotFound } from "../api/client";
import { Link, useParams } from "react-router";
import { isRunFinished, useCancelRun, useCloseRun, useRun } from "../api/queries";
import type { RunDetail } from "../api/types";
import { StatusBadge } from "../components/Badge";
import { WaitingNote } from "../components/WaitingNote";
import { Callout } from "../components/Callout";
import { Card } from "../components/Card";
import { ConfirmDialog } from "../components/ConfirmDialog";
import { KeyValue } from "../components/KeyValue";
import { LabelList } from "../components/LabelEditor";
import { Commit } from "../components/Mono";
import { PageHeader } from "../components/PageHeader";
import { PolicyView } from "../components/PolicyView";
import { RelativeTime } from "../components/RelativeTime";
import { useNow } from "../components/useNow";
import { ErrorMessage, NotFound, PageSkeleton } from "../components/States";
import { humanize, operationLabel, reasonLabel, runResourcePath } from "../lib/format";
import { elapsed, formatDuration } from "../lib/time";
import { StepTimeline } from "./run/StepTimeline";

const closeExplanation =
  "Closing records the run as failed after you have looked at it. Nothing is changed on Proxmox; the next plan shows the real state.";

function UncertainNotice({ detail, onClose }: { detail: RunDetail; onClose: () => void }) {
  return (
    <Callout
      kind="uncertain"
      title="The outcome is uncertain"
      actions={
        detail.canClose ? (
          <button type="button" className="button" onClick={onClose}>
            <CircleX size={15} aria-hidden="true" />
            Close the run
          </button>
        ) : undefined
      }
    >
      <p>
        An agent stopped reporting while a step ran, so Octopushy cannot tell whether the change was made. Nothing is
        retried automatically. If the agent comes back, its result is still recorded here.
      </p>
      <p>
        Otherwise check the infrastructure, then close the run and plan again. {closeExplanation}
      </p>
    </Callout>
  );
}

function RunFacts({ detail }: { detail: RunDetail }) {
  const run = detail.run;
  const now = useNow();
  const duration = elapsed(run.startedAt, run.finishedAt, now);
  return (
    <KeyValue
      items={[
        {
          term: humanize(run.resourceKind),
          value: (
            <Link to={runResourcePath(run)} className="mono break">
              {run.address}
            </Link>
          ),
        },
        { term: "Operation", value: operationLabel(run.operation) },
        { term: "Reason", value: reasonLabel(run.reason) },
        { term: "Commit", value: <Commit commit={run.commit} length={10} /> },
        { term: "Created", value: <RelativeTime value={run.createdAt} /> },
        { term: "Started", value: <RelativeTime value={run.startedAt} empty="Not yet" /> },
        { term: "Finished", value: <RelativeTime value={run.finishedAt} empty="Not yet" /> },
        {
          term: "Duration",
          value: duration === null ? <span className="muted">—</span> : <span className="num">{formatDuration(duration)}</span>,
        },
      ]}
    />
  );
}

export function RunPage() {
  const { id = "" } = useParams();
  const { data, error, isPending } = useRun(id);
  const cancel = useCancelRun();
  const close = useCloseRun();
  const [confirm, setConfirm] = useState<"cancel" | "close" | null>(null);

  if (isPending) return <PageSkeleton rows={3} />;
  if (!data) return isNotFound(error) ? <NotFound what="run" /> : <ErrorMessage error={error} title="The run could not be loaded" />;

  const run = data.run;
  const number = run.number;
  const live = !isRunFinished(run.status);

  return (
    <>
      <PageHeader
        eyebrow={
          <>
            <Link to="/runs">Runs</Link> / Run
          </>
        }
        title={
          <>
            Run #{number} · {operationLabel(run.operation)}{" "}
            <Link to={runResourcePath(run)} className="mono page-header__address">
              {run.address}
            </Link>
          </>
        }
        documentTitle={`Run #${number}`}
        meta={
          <span className="inline-group">
            <StatusBadge status={run.status} />
            {run.currentStep && live ? <span>at {humanize(run.currentStep)}</span> : null}
            {run.detail ? <span>{run.detail}</span> : null}
            {run.waiting && run.waiting !== run.detail ? <WaitingNote text={run.waiting} /> : null}
            {live ? (
              <span className="muted small">
                <span className="live-dot" aria-hidden="true" /> Updating
              </span>
            ) : null}
          </span>
        }
        actions={
          <>
            {data.canCancel ? (
              <button type="button" className="button button--danger-outline" onClick={() => setConfirm("cancel")}>
                <Ban size={15} aria-hidden="true" />
                Cancel run
              </button>
            ) : null}
            {data.canClose ? (
              <button type="button" className="button" onClick={() => setConfirm("close")}>
                <CircleX size={15} aria-hidden="true" />
                Close run
              </button>
            ) : null}
          </>
        }
      />
      <div className="stack stack--large">
        {error ? <ErrorMessage error={error} title="Showing the last data; refreshing failed" /> : null}
        {run.status === "uncertain" ? <UncertainNotice detail={data} onClose={() => setConfirm("close")} /> : null}
        <div className="columns columns--main-side">
          <Card title="Steps">
            <StepTimeline detail={data} />
          </Card>
          <div className="stack stack--large">
            <Card title="Run">
              <RunFacts detail={data} />
            </Card>
            {data.policy ? (
              <Card title="Policy">
                <PolicyView policy={data.policy} />
              </Card>
            ) : null}
            <Card title="Agent labels">
              <div className="stack stack--small">
                <LabelList labels={data.agentLabels} empty="Any agent" />
                {data.agentLabelsSetBy ? <span className="muted small">set by {data.agentLabelsSetBy}</span> : null}
              </div>
            </Card>
          </div>
        </div>
      </div>
      <ConfirmDialog
        open={confirm === "cancel"}
        title={`Cancel run #${number}?`}
        confirmLabel="Cancel the run"
        danger
        pending={cancel.isPending}
        error={cancel.error}
        onClose={() => {
          setConfirm(null);
          cancel.reset();
        }}
        onConfirm={() => cancel.mutate(run.id, { onSuccess: () => setConfirm(null) })}
      >
        <p>Steps that have not started are skipped. A change that is being applied is never interrupted.</p>
      </ConfirmDialog>
      <ConfirmDialog
        open={confirm === "close"}
        title={`Close run #${number}?`}
        confirmLabel="Close the run"
        pending={close.isPending}
        error={close.error}
        onClose={() => {
          setConfirm(null);
          close.reset();
        }}
        onConfirm={() => close.mutate(run.id, { onSuccess: () => setConfirm(null) })}
      >
        <p>{closeExplanation}</p>
      </ConfirmDialog>
    </>
  );
}
