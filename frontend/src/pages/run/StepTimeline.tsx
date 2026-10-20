import { Link } from "react-router";
import type { RunDetail, Step } from "../../api/types";
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
import { PlanChanges } from "./PlanChanges";

const stepNames: Record<string, string> = {
  plan: "Plan",
  approval: "Approval",
  unprotect: "Remove protection",
  apply: "Apply",
  verify: "Verify",
  "verify-absent": "Verify absent",
  observe: "Observe",
};

function describe(step: Step, operation: string): string | null {
  switch (step.name) {
    case "plan":
      return operation === "retire"
        ? "OpenTofu plans destroying the VM; the saved plan is what gets applied."
        : "OpenTofu plans the change; the saved plan is what gets applied.";
    case "approval":
      return "The saved plan is approved by policy or by you.";
    case "unprotect":
      return "Removes the VM's protection so it can be deleted.";
    case "apply":
      return "OpenTofu applies exactly the approved plan.";
    case "verify":
      return "Checks the VM through the Proxmox API.";
    case "verify-absent":
      return "Checks through the Proxmox API that the VM is gone.";
    case "observe":
      return "Reads hosts and VMs from the Proxmox API.";
    default:
      return null;
  }
}

function StepBody({ step, detail }: { step: Step; detail: RunDetail }) {
  const plan = step.plan ? summarizePlan(step.plan) : null;
  if (step.kind === "approval") {
    const planStep = detail.steps.find((s) => s.name === "plan");
    return <ApprovalPanel runId={detail.run.id} step={step} plan={planStep?.plan ? summarizePlan(planStep.plan) : null} />;
  }
  return (
    <>
      {plan ? <PlanChanges plan={plan} /> : null}
      {!plan && step.outputs !== null && step.outputs !== undefined ? (
        <details className="outputs">
          <summary>Outputs</summary>
          <JsonBlock value={step.outputs} />
        </details>
      ) : null}
      <AttemptList attempts={step.attempts} />
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
          <h3 className="timeline__title">{stepNames[step.name] ?? humanize(step.name)}</h3>
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
