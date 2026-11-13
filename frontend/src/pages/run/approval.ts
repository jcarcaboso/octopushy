import type { RunDetail, Step } from "../../api/types";
import { buildRunOverview } from "../../lib/changes";
import { summarizePlan } from "../../lib/plan";
import type { ApprovalPanelProps } from "./ApprovalPanel";

/** What the approval panel needs for a step of a run: its plan, its tiles, and what kind of run asks. */
export function approvalProps(detail: RunDetail, step: Step): ApprovalPanelProps {
  const planStep = detail.steps.find((s) => s.name === "plan");
  const operation = detail.run.operation;
  return {
    runId: detail.run.id,
    step,
    plan: planStep?.plan && operation !== "pipeline" ? summarizePlan(planStep.plan) : null,
    overview: step.status === "awaiting-approval" ? buildRunOverview(detail.steps) : [],
    configure: operation === "configure" || operation === "rotate-access",
    pipeline: operation === "pipeline",
    rotation: operation === "rotate-access",
  };
}
