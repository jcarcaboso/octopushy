import type { Plan } from "../api/types";

type PlanChangeDto = Plan["changes"][number];

export type PlanCounts = Plan["counts"];

export type ChangeKind = "create" | "update" | "delete" | "replace" | "read" | "no-op" | "other";

export interface PlanChange extends PlanChangeDto {
  kind: ChangeKind;
}

export interface PlanSummary extends Omit<Plan, "changes"> {
  changes: PlanChange[];
}

/** OpenTofu's action lists: a replace is a delete and a create, in either order. */
export function classifyActions(actions: readonly string[]): ChangeKind {
  const key = actions.join(",");
  switch (key) {
    case "create":
      return "create";
    case "update":
      return "update";
    case "delete":
      return "delete";
    case "delete,create":
    case "create,delete":
      return "replace";
    case "read":
      return "read";
    case "no-op":
    case "":
      return "no-op";
    default:
      return "other";
  }
}

/** A plan step's plan, with each change classified. */
export function summarizePlan(plan: Plan): PlanSummary {
  return { ...plan, changes: plan.changes.map((change) => ({ ...change, kind: classifyActions(change.actions) })) };
}
