import { screen, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { expect, it } from "vitest";
import type { RunSummary } from "../api/types";
import { renderAt } from "../test/utils";
import { RunTable } from "./RunTable";

const at = (day: number) => `2026-10-${String(day).padStart(2, "0")}T08:00:00Z`;

function run(number: number, overrides: Partial<RunSummary> = {}): RunSummary {
  return {
    id: `run-${number}`,
    number,
    operation: "configure",
    action: "update-tools",
    resourceId: "guest-1",
    resourceKind: "guest",
    address: "home/pve01/bb",
    status: "succeeded",
    detail: null,
    reason: "schedule",
    commit: "e6b1282aa4d1c0ffee",
    createdAt: at(number),
    startedAt: at(number),
    finishedAt: `2026-10-${String(number).padStart(2, "0")}T08:02:31Z`,
    currentStep: null,
    waiting: null,
    steps: [],
    canCancel: false,
    changes: [],
    ...overrides,
  };
}

const show = (runs: RunSummary[]) => renderAt(<RunTable runs={runs} label="Runs" showAddress={false} />, { path: "/", route: "/" });

const rowOf = (name: string) => screen.getByRole("link", { name }).closest("tr")!;

it("names the action of a configure run, and the operation of any other run", () => {
  show([run(20, { changes: null }), run(19, { operation: "provision", action: null, changes: null })]);
  expect(screen.getByRole("link", { name: "#20" }).closest("td")).toHaveTextContent("#20 Update tools");
  expect(screen.getByRole("link", { name: "#19" }).closest("td")).toHaveTextContent("#19 Provision");
  expect(screen.getAllByRole("columnheader").map((h) => h.textContent)).toEqual(["Run", "Status", "What changed", "Took", "Reason", "Commit", "Created"]);
});

it("names the first two changes with the part that differs bright, counts the rest, and puts the duration under Took", () => {
  show([
    run(20, {
      changes: [
        { name: "t3", before: "v0.1.12-nightly.20261123.5177", after: "v0.1.12-nightly.20261124.5178" },
        { name: "claude_code", before: "v2.1.49", after: "v2.1.52" },
        { name: "codex", before: "v0.161.0", after: null },
      ],
    }),
  ]);
  const row = rowOf("#20");
  expect(row).toHaveTextContent("t3v0.1.12-nightly.20261124.5178");
  expect(row).toHaveTextContent("claude_codev2.1.52");
  expect(row).toHaveTextContent("+1 more");
  expect(row).not.toHaveTextContent("codex");
  expect([...row.querySelectorAll("b")].map((b) => b.textContent)).toEqual(["20261124", "5178", "52"]);
  expect(row).toHaveTextContent("2 min 31 s");
});

it("says Nothing new for a run that compared and found the same, and nothing for one that compared nothing", () => {
  show([run(20, { action: "setup", changes: [] }), run(19, { changes: null })]);
  expect(within(rowOf("#20")).getByText("Nothing new")).toBeInTheDocument();
  expect(rowOf("#19").querySelector(".cell-detail")).toBeEmptyDOMElement();
});

it("shows a finished run's detail instead of its changes", () => {
  show([run(20, { status: "failed", detail: "The playbook failed at Install tools.", changes: [{ name: "t3", before: "1", after: "2" }] })]);
  const row = rowOf("#20");
  expect(row).toHaveTextContent("The playbook failed at Install tools.");
  expect(row).not.toHaveTextContent("t3");
});

it("shows what a run awaiting approval will change instead of its approval policy, and no duration", () => {
  show([
    run(20, {
      operation: "provision",
      action: null,
      status: "awaiting-approval",
      finishedAt: null,
      currentStep: "approval",
      detail: "Approval is manual (set by guest.yaml).",
      changes: [{ name: "disk scsi0", before: "32 GiB", after: "64 GiB" }],
    }),
    run(19, { operation: "provision", action: null, status: "awaiting-approval", finishedAt: null, currentStep: "approval", detail: "Approval is manual.", changes: null }),
  ]);
  const row = rowOf("#20");
  expect(row).toHaveTextContent("Approval");
  expect(row).toHaveTextContent("disk scsi064 GiB");
  expect(row).not.toHaveTextContent("Approval is manual");
  expect(row.querySelector("td.num")).toBeEmptyDOMElement();
  expect(rowOf("#19")).toHaveTextContent("Approval is manual.");
});

it("folds neighbouring runs that found nothing new into one row that opens in place", async () => {
  const user = userEvent.setup();
  show([
    run(20, { changes: [{ name: "t3", before: "1", after: "2" }] }),
    run(19),
    run(18),
    run(17),
    run(16, { changes: [{ name: "codex", before: "1", after: "2" }] }),
    run(15),
  ]);
  const fold = screen.getByRole("button", { name: /3 runs of Update tools found nothing new/ });
  expect(fold).toHaveAttribute("aria-expanded", "false");
  expect(fold).toHaveTextContent("#17 to #19 · scheduled");
  expect(screen.queryByRole("link", { name: "#18" })).not.toBeInTheDocument();
  // A single run that found nothing new stays an ordinary row.
  expect(within(rowOf("#15")).getByText("Nothing new")).toBeInTheDocument();

  fold.focus();
  await user.keyboard("{Enter}");
  expect(fold).toHaveAttribute("aria-expanded", "true");
  for (const name of ["#19", "#18", "#17"]) expect(rowOf(name)).toHaveTextContent("Nothing new");

  await user.click(fold);
  expect(fold).toHaveAttribute("aria-expanded", "false");
  expect(screen.queryByRole("link", { name: "#18" })).not.toBeInTheDocument();
});

it("does not fold runs with another title, another reason, or that failed", () => {
  show([
    run(20),
    run(19, { action: "setup" }),
    run(18),
    run(17, { reason: "manual" }),
    run(16),
    run(15, { status: "failed", detail: "Failed." }),
    run(14),
  ]);
  expect(screen.queryByRole("button")).not.toBeInTheDocument();
  expect(screen.getAllByRole("link", { name: /^#\d+$/ })).toHaveLength(7);
});
