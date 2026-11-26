import { Ban, CircleX, OctagonX, RotateCcw } from "lucide-react";
import { useMemo, useState } from "react";
import { isNotFound } from "../api/client";
import { Link, useNavigate, useParams } from "react-router";
import { isRunFinished, useCancelRun, useCloseRun, useForceStopRun, useRun, useRunAgain, useService } from "../api/queries";
import type { RunDetail } from "../api/types";
import { Callout } from "../components/Callout";
import { ConfirmDialog } from "../components/ConfirmDialog";
import { ServiceIcon } from "../components/ServiceIcon";
import { useDocumentTitle } from "../components/useDocumentTitle";
import { ErrorMessage, NotFound, PageSkeleton } from "../components/States";
import { buildRunOverview } from "../lib/changes";
import { humanize, operationLabel, reasonLabel, runResourcePath } from "../lib/format";
import { RunChanges } from "./run/RunChanges";
import { RunFoot } from "./run/RunFoot";
import { RunTasks } from "./run/RunTasks";
import { RunVerdict } from "./run/RunVerdict";
import { lastSegment } from "./run/steps";
import { StepList } from "./run/StepList";

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

/** A service's own icon, else its letter while the service loads or when it cannot be read. */
function ResourceIcon({ id, name }: { id: string; name: string }) {
  const { data } = useService(id);
  return <ServiceIcon service={{ name, icon: data?.service.icon ?? null }} />;
}

export function RunPage() {
  const { id = "" } = useParams();
  const { data, error, isPending } = useRun(id);
  const cancel = useCancelRun();
  const close = useCloseRun();
  const forceStop = useForceStopRun();
  const again = useRunAgain();
  const navigate = useNavigate();
  const [confirm, setConfirm] = useState<"cancel" | "stop" | "close" | "again" | null>(null);
  const steps = data?.steps;
  const tiles = useMemo(() => (steps ? buildRunOverview(steps) : []), [steps]);
  useDocumentTitle(data ? `Run #${data.run.number}` : undefined);

  if (isPending) return <PageSkeleton rows={3} />;
  if (!data) return isNotFound(error) ? <NotFound what="run" /> : <ErrorMessage error={error} title="The run could not be loaded" />;

  const run = data.run;
  const number = run.number;
  const live = !isRunFinished(run.status);
  const name = lastSegment(run.address);
  // A configure run runs one action of its workflow, which says more than "Configure".
  const preview = run.operation === "configure" ? (data.steps.find((s) => s.configurePreview)?.configurePreview ?? null) : null;
  const hasActions = data.canCancel || data.canForceStop || data.canClose || data.canRunAgain;

  return (
    <div className="run-page">
      <nav className="run-page__crumbs" aria-label="Breadcrumb">
        <Link to="/runs">Runs</Link> / Run #{number}
      </nav>
      <header className="run-head">
        <h1 className="run-head__title">
          {run.resourceKind === "service" ? <ResourceIcon id={run.resourceId} name={name} /> : null}
          <span className="run-head__name">{name}</span>
          <Link to={runResourcePath(run)} className="mono run-head__address">
            {run.address}
          </Link>
        </h1>
        <span className="run-head__why">
          <span>{preview ? humanize(preview.action) : operationLabel(run.operation)}</span>
          {preview ? <span className="mono run-head__playbook"> {preview.playbook}</span> : null} · <span>{reasonLabel(run.reason)}</span>
        </span>
      </header>
      {error ? <ErrorMessage error={error} title="Showing the last data; refreshing failed" /> : null}
      <RunVerdict
        detail={data}
        tiles={tiles}
        live={live}
        notice={run.status === "uncertain" ? <UncertainNotice detail={data} onClose={() => setConfirm("close")} /> : null}
        actions={hasActions ? (
          <>
            {data.canCancel ? (
              <button type="button" className="button button--danger-outline" onClick={() => setConfirm("cancel")}>
                <Ban size={15} aria-hidden="true" />
                Cancel run
              </button>
            ) : null}
            {data.canForceStop ? (
              <button type="button" className="button button--danger-outline" onClick={() => setConfirm("stop")}>
                <OctagonX size={15} aria-hidden="true" />
                Force stop
              </button>
            ) : null}
            {data.canClose ? (
              <button type="button" className="button" onClick={() => setConfirm("close")}>
                <CircleX size={15} aria-hidden="true" />
                Close run
              </button>
            ) : null}
            {data.canRunAgain ? (
              <button type="button" className="button" onClick={() => setConfirm("again")}>
                <RotateCcw size={15} aria-hidden="true" />
                Run again
              </button>
            ) : null}
          </>
        ) : null}
      />
      <RunChanges detail={data} tiles={tiles} finished={!live} />
      <RunTasks detail={data} finished={!live} />
      <section className="run-section" aria-labelledby="run-steps-title">
        <h2 id="run-steps-title" className="run-section__title">{live ? "How it is going" : "How it went"}</h2>
        <StepList detail={data} />
      </section>
      <RunFoot detail={data} />
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
        open={confirm === "stop"}
        title={`Force stop run #${number}?`}
        confirmLabel="Force stop the run"
        danger
        pending={forceStop.isPending}
        error={forceStop.error}
        onClose={() => {
          setConfirm(null);
          forceStop.reset();
        }}
        onConfirm={() => forceStop.mutate(run.id, { onSuccess: () => setConfirm(null) })}
      >
        <p>
          Ask the agent to stop the running tool, without waiting for it. The run becomes uncertain: check the guest and saved
          state before closing the run or planning again. This does not roll back changes or stop work already sent to Proxmox.
        </p>
        <p>
          Older agents keep the tool running. An offline agent cannot receive the stop request. In either case, stop the tool
          on the agent yourself if needed.
        </p>
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
      <ConfirmDialog
        open={confirm === "again"}
        title={`Run #${number} again?`}
        confirmLabel="Start a new run"
        pending={again.isPending}
        error={again.error}
        onClose={() => {
          setConfirm(null);
          again.reset();
        }}
        onConfirm={() =>
          again.mutate(run.id, {
            onSuccess: (started) => {
              setConfirm(null);
              navigate(`/runs/${started.runId}`);
            },
          })
        }
      >
        <p>
          A new run of the same work starts from the latest revision. It plans again and asks for approval as its
          policy says. Nothing of this run is reused.
        </p>
      </ConfirmDialog>
    </div>
  );
}
