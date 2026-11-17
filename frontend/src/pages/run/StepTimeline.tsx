import { Link } from "react-router";
import type { RunDetail, Step } from "../../api/types";
import { useRetryStage } from "../../api/queries";
import { ErrorMessage } from "../../components/States";
import { StatusBadge } from "../../components/Badge";
import { JsonBlock } from "../../components/CodeBlock";
import { RelativeTime } from "../../components/RelativeTime";
import { useNow } from "../../components/useNow";
import { statusStyle } from "../../components/status";
import { summarizePlan } from "../../lib/plan";
import { elapsed, formatDuration } from "../../lib/time";
import { ApprovalPanel } from "./ApprovalPanel";
import { approvalProps } from "./approval";
import { AttemptList } from "./AttemptList";
import { ConfigurePreviewView } from "./ConfigurePreview";
import { PlanChanges } from "./PlanChanges";
import { describe, stepName } from "./steps";

/**
 * What a step prints under its title: plan, preview, outputs, attempts with their logs, and Retry this stage.
 * `approvalAbove` says the page shows the open approval elsewhere, so it is not shown twice.
 */
export function StepBody({ step, detail, approvalAbove = false }: { step: Step; detail: RunDetail; approvalAbove?: boolean }) {
  const retryStage = useRetryStage();
  const lostChange = detail.steps.some((s) => s.attempts.some((a) => a.status === "uncertain" && a.mutating));
  const canRetryStage = detail.run.operation === "pipeline" && detail.run.status === "failed" && step.status === "failed"
    && Boolean(step.stage) && ["apply", "playbook"].includes(step.name) && !lostChange;
  const plan = step.plan ? summarizePlan(step.plan) : null;
  if (step.kind === "approval") {
    if (approvalAbove && step.status === "awaiting-approval" && !step.approval?.decision) {
      return <p className="small">Approve or reject it in the summary at the top of the page.</p>;
    }
    return <ApprovalPanel {...approvalProps(detail, step)} />;
  }
  return (
    <>
      {plan ? <PlanChanges plan={plan} /> : null}
      {step.configurePreview ? <ConfigurePreviewView preview={step.configurePreview} /> : null}
      {!plan && !step.configurePreview && step.outputs !== null && step.outputs !== undefined ? (
        <details className="outputs">
          <summary>Outputs</summary>
          <JsonBlock value={step.outputs} />
        </details>
      ) : null}
      <AttemptList attempts={step.attempts} />
      {step.networkAccess ? <p className="muted small">{step.networkAccess}</p> : null}
      {canRetryStage ? (
        <div className="stack stack--small">
          <p className="muted small">Continue from this stage under the same approval. The stages before it are not repeated, and nothing is rolled back. It is refused if the inputs changed.</p>
          <ErrorMessage error={retryStage.error} />
          <button type="button" className="button" onClick={() => retryStage.mutate(detail.run.id)} disabled={retryStage.isPending}>
            {retryStage.isPending ? "Retrying…" : "Retry this stage"}
          </button>
        </div>
      ) : null}
    </>
  );
}

function StepItem({ step, detail }: { step: Step; detail: RunDetail }) {
  const now = useNow();
  const tone = statusStyle(step.status).tone;
  const duration = step.kind === "approval" ? null : elapsed(step.startedAt, step.finishedAt, now);
  const quiet = step.status === "pending" || step.status === "skipped";
  const description = describe(step, detail.run.operation);
  return (
    <li className={`timeline__item timeline__item--${tone}${quiet ? " timeline__item--quiet" : ""}`}>
      <span className="timeline__marker" aria-hidden="true" />
      <div className="timeline__content">
        <div className="timeline__header">
          <h3 className="timeline__title">
            {step.stage ? <span className="mono timeline__stage">{step.stage}</span> : null}
            {stepName(step)}
          </h3>
          <StatusBadge status={step.status} />
          <span className="muted small timeline__times">
            {step.startedAt ? (
              <>
                started <RelativeTime value={step.startedAt} />
              </>
            ) : null}
            {duration !== null ? <span className="num"> · {formatDuration(duration)}</span> : null}
          </span>
        </div>
        {description && !quiet ? <p className="muted small timeline__description">{description}</p> : null}
        {step.detail && !(step.kind === "approval" && step.status === "awaiting-approval") ? (
          <p className={step.status === "failed" || step.status === "blocked" || step.status === "uncertain" ? "timeline__detail timeline__detail--strong" : "timeline__detail"}>
            {step.detail}
            {step.status === "blocked" && /missing secret/i.test(step.detail) ? (
              <>
                {" "}
                <Link to="/variables">Open Variables</Link>
              </>
            ) : null}
          </p>
        ) : null}
        <StepBody step={step} detail={detail} />
      </div>
    </li>
  );
}

export function StepTimeline({ detail }: { detail: RunDetail }) {
  const steps = [...detail.steps].sort((a, b) => Number(a.ordinal) - Number(b.ordinal));
  return (
    <ol className="timeline" aria-label="Steps">
      {steps.map((step) => (
        <StepItem key={step.id} step={step} detail={detail} />
      ))}
    </ol>
  );
}
