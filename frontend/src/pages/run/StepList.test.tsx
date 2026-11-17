import { screen, within } from "@testing-library/react";
import { expect, it } from "vitest";
import type { RunDetail } from "../../api/types";
import { runDetail } from "../../test/runs";
import { mockApi, renderAt } from "../../test/utils";
import { StepList } from "./StepList";
import { StepTimeline } from "./StepTimeline";

/** The run with its plan step blocked, its detail as given. */
function blockedAt(detail: string, status = "blocked"): RunDetail {
  const run = runDetail({ status: "blocked" });
  const [plan] = run.steps;
  return { ...run, steps: [{ ...plan, status, detail, attempts: [], plan: null }] };
}

const views = [
  ["the step list", (detail: RunDetail) => <StepList detail={detail} />],
  ["the timeline", (detail: RunDetail) => <StepTimeline detail={detail} />],
] as const;

it.each(views)("offers Variables on a blocked step in %s, whatever its words", (_, view) => {
  mockApi({});
  renderAt(view(blockedAt("A credential this step needs is not stored yet.")), { path: "/", route: "/" });
  const steps = screen.getByRole("list", { name: "Steps" });
  expect(within(steps).getByText(/A credential this step needs is not stored yet\./)).toBeInTheDocument();
  expect(within(steps).getByRole("link", { name: "Open Variables" })).toHaveAttribute("href", "/variables");
});

it.each(views)("does not offer Variables on a failed step in %s, whatever its words", (_, view) => {
  mockApi({});
  renderAt(view(blockedAt("Blocked credentials: missing secret home/token.", "failed")), { path: "/", route: "/" });
  expect(screen.queryByRole("link", { name: "Open Variables" })).toBeNull();
});
