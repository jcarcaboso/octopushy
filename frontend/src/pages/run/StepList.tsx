import { ChevronRight } from "lucide-react";
import { useId, useState } from "react";
import { Link } from "react-router";
import type { RunDetail, Step } from "../../api/types";
import { Badge, StatusBadge, StatusMark } from "../../components/Badge";
import { RelativeTime } from "../../components/RelativeTime";
import { useNow } from "../../components/useNow";
import type { Tone } from "../../components/status";
import type { PlanCounts } from "../../lib/plan";
import { elapsed, formatDuration } from "../../lib/time";
import { StepBody } from "./StepTimeline";
import { decisionNote, describe, stepName, stepTitle } from "./steps";

/**
 * Steps that open on their own: the ones that need a look. A step that awaits approval is not one of them,
 * because the approval itself is in the summary at the top of the page.
 */
const openByDefault = new Set(["failed", "uncertain", "blocked", "running", "assigned"]);
const quietStatuses = new Set(["pending", "skipped", "cancelled"]);

/** The few words after a step's name in its row. */
function stepNote(step: Step): string | null {
  if (step.kind === "approval") {
    const decided = decisionNote(step);
    if (decided) return decided;
    if (step.status === "awaiting-approval") return "waits for you";
  }
  if (step.status === "running" || step.status === "assigned") return "running now";
  return step.detail;
}

const countParts: [keyof PlanCounts, string, Tone][] = [
  ["create", "to create", "success"],
  ["update", "to update", "info"],
  ["replace", "to replace", "uncertain"],
  ["delete", "to delete", "danger"],
];

function StepRow({ step, detail, open, onToggle }: { step: Step; detail: RunDetail; open: boolean; onToggle: () => void }) {
  const now = useNow();
  const bodyId = useId();
  const duration = step.kind === "approval" ? null : elapsed(step.startedAt, step.finishedAt, now);
  const quiet = quietStatuses.has(step.status);
  const description = describe(step, detail.run.operation);
  // Open, the step's detail is printed in full below, so the row does not repeat it.
  const note = open && stepNote(step) === step.detail ? null : stepNote(step);
  const counts = step.plan?.counts;
  const strong = step.status === "failed" || step.status === "blocked" || step.status === "uncertain";
  return (
    <li className={`step-row${quiet ? " step-row--quiet" : ""}`} data-status={step.status}>
      <button type="button" className="step-row__head" aria-expanded={open} aria-controls={bodyId} onClick={onToggle}>
        <StatusMark status={step.status} />
        <span className="step-row__stage mono">{step.stage ?? ""}</span>
        <span className="step-row__name">
          <span className="step-row__title">{stepName(step)}</span>
          {note ? <span className={strong ? "step-row__note step-row__note--strong" : "step-row__note"}>{note}</span> : null}
        </span>
        <span className="step-row__chips">
          {counts
            ? countParts.filter(([kind]) => counts[kind] > 0).map(([kind, text, tone]) => (
                <Badge key={kind} tone={tone}>{counts[kind]} {text}</Badge>
              ))
            : null}
        </span>
        <span className="step-row__time num">{duration !== null ? formatDuration(duration) : ""}</span>
        <ChevronRight className="run-chevron" size={15} aria-hidden="true" />
      </button>
      {open ? (
        <div id={bodyId} className="step-row__body" role="region" aria-label={stepTitle(step)}>
          <div className="step-row__meta">
            <StatusBadge status={step.status} />
            {step.startedAt ? (
              <span className="muted small">
                started <RelativeTime value={step.startedAt} />
                {duration !== null ? <span className="num"> · {formatDuration(duration)}</span> : null}
              </span>
            ) : null}
          </div>
          {description ? <p className="muted small">{description}</p> : null}
          {step.detail && !(step.kind === "approval" && step.status === "awaiting-approval") ? (
            <p className={strong ? "step-row__detail step-row__detail--strong" : "step-row__detail"}>
              {step.detail}
              {step.status === "blocked" ? (
                <>
                  {" "}
                  <Link to="/variables">Open Variables</Link>
                </>
              ) : null}
            </p>
          ) : null}
          <StepBody step={step} detail={detail} approvalAbove />
        </div>
      ) : null}
    </li>
  );
}

/**
 * The run's steps, one row each, opening in place. A step is open while it needs a look, so a failed step shows its
 * log at once and a running one closes when it is done. A step the user opened or closed stays as they left it.
 */
export function StepList({ detail }: { detail: RunDetail }) {
  const steps = [...detail.steps].sort((a, b) => Number(a.ordinal) - Number(b.ordinal));
  const [chosen, setChosen] = useState<Record<string, boolean>>({});
  const isOpen = (step: Step) => chosen[step.id] ?? openByDefault.has(step.status);

  const staged = steps.some((s) => s.stage);
  return (
    <ol className={staged ? "run-list step-list" : "run-list step-list step-list--flat"} aria-label="Steps">
      {steps.map((step) => (
        <StepRow key={step.id} step={step} detail={detail} open={isOpen(step)} onToggle={() => setChosen((current) => ({ ...current, [step.id]: !isOpen(step) }))} />
      ))}
    </ol>
  );
}
