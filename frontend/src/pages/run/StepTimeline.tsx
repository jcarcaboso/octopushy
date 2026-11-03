import { Link } from "react-router";
import type { RunDetail, Step } from "../../api/types";
import { useRetryFilesystem, useRetryStage } from "../../api/queries";
import { ErrorMessage } from "../../components/States";
import { StatusBadge } from "../../components/Badge";
import { JsonBlock } from "../../components/CodeBlock";
import { RelativeTime } from "../../components/RelativeTime";
import { useNow } from "../../components/useNow";
import { statusStyle } from "../../components/status";
import { humanize } from "../../lib/format";
import { summarizePlan } from "../../lib/plan";
import { elapsed, formatDuration } from "../../lib/time";
import { ApprovalPanel } from "./ApprovalPanel";
import { AttemptList } from "./AttemptList";
import { ConfigurePreviewView } from "./ConfigurePreview";
import { PlanChanges } from "./PlanChanges";

const stepNames: Record<string, string> = {
  plan: "Plan",
  approval: "Approval",
  unprotect: "Remove protection",
  apply: "Apply",
  reprotect: "Restore protection",
  verify: "Verify",
  "verify-absent": "Verify absent",
  "grow-root": "Expand guest root filesystem",
  "verify-root": "Verify guest capacity",
  preview: "Preview",
  playbook: "Run the playbook",
  "outcome-approval": "Decide on the outcome",
  "playbook-approved": "Run the playbook again",
};

function describe(step: Step, operation: string): string | null {
  switch (step.name) {
    case "plan":
      return operation === "retire"
        ? "OpenTofu plans destroying the VM; the saved plan is what gets applied."
        : "OpenTofu plans the change; the saved plan is what gets applied.";
    case "approval":
      return operation === "pipeline"
        ? "One approval, by policy or by you, of every stage's plan and preview at once."
        : operation === "configure"
        ? "The preview is approved by policy or by you."
        : "The saved plan is approved by policy or by you.";
    case "preview":
      return "Checks the playbook and lists its tasks, with no secrets and no contact with the guest. It shows what will run, not what will change.";
    case "playbook":
      return "Runs the playbook on the guest over SSH, against the pinned host key, with exactly what the preview listed.";
    case "outcome-approval":
      return "The playbook reported something that needs your decision. It ran once; this would run it once more.";
    case "playbook-approved":
      return "Runs the same commit and preview once more, with what the recipe says approving adds.";
    case "unprotect":
      return "Removes the VM's protection so it can be deleted.";
    case "apply":
      return "OpenTofu applies exactly the approved plan.";
    case "reprotect":
      return "Puts the VM's protection back, because the plan was not applied in full.";
    case "verify":
      return "Checks the VM through the Proxmox API.";
    case "verify-absent":
      return "Checks through the Proxmox API that the VM is gone.";
    case "grow-root":
      return "Checks the supported NixOS layout and expands its root partition and ext4 filesystem online if needed.";
    case "verify-root":
      return "Independently measures guest disk, partition, and filesystem capacity.";
    default:
      return null;
  }
}

function StepBody({ step, detail }: { step: Step; detail: RunDetail }) {
  const retry = useRetryFilesystem();
  const retryStage = useRetryStage();
  const lostChange = detail.steps.some((s) => s.attempts.some((a) => a.status === "uncertain" && a.mutating));
  const canRetryStage = detail.run.operation === "pipeline" && detail.run.status === "failed" && step.status === "failed"
    && Boolean(step.stage) && ["apply", "playbook"].includes(step.name) && !lostChange;
  const canRetry = detail.run.status === "failed" && step.status === "failed"
    && ["verify", "grow-root", "verify-root"].includes(step.name)
    && !lostChange;
  const plan = step.plan ? summarizePlan(step.plan) : null;
  if (step.kind === "approval") {
    const planStep = detail.steps.find((s) => s.name === "plan");
    return (
      <ApprovalPanel
        runId={detail.run.id}
        step={step}
        plan={planStep?.plan && detail.run.operation !== "pipeline" ? summarizePlan(planStep.plan) : null}
        configure={detail.run.operation === "configure" || detail.run.operation === "rotate-access"}
        pipeline={detail.run.operation === "pipeline"}
        rotation={detail.run.operation === "rotate-access"}
      />
    );
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
      {canRetry ? (
        <div className="stack stack--small">
          <p className="muted small">Retry this guest step without repeating the successful infrastructure steps. Stale runs are refused.</p>
          <ErrorMessage error={retry.error} />
          <button type="button" className="button" onClick={() => retry.mutate(detail.run.id)} disabled={retry.isPending}>
            {retry.isPending ? "Retrying…" : "Retry guest step"}
          </button>
        </div>
      ) : null}
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
            {stepNames[step.name] ?? humanize(step.name)}
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
