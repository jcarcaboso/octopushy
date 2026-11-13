import { screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { expect, it } from "vitest";
import { runDetail, sha } from "../../test/runs";
import { mockApi, renderAt } from "../../test/utils";
import { WaitingRun } from "./WaitingRun";

const summary = runDetail().run;
const route = { path: "/", route: "/" };

it("shows what the waiting run changes and approves it with the fingerprint shown", async () => {
  const { requests } = mockApi({
    "GET /api/runs/run-1": () => runDetail(),
    "POST /api/runs/run-1/steps/step-approval/approve": () => undefined,
  });
  renderAt(<WaitingRun run={summary} />, route);
  const panel = screen.getByRole("region", { name: "Provision #12 waits for your approval" });
  expect(within(panel).getByRole("link", { name: "Open run #12" })).toHaveAttribute("href", "/runs/run-1");
  // The plan replaces the VM: its tiles are red, and so is the panel.
  expect(await within(panel).findByRole("group", { name: "What changes" })).toBeInTheDocument();
  expect(panel).toHaveClass("wait--danger");
  await userEvent.click(within(panel).getByRole("button", { name: "Approve" }));
  await waitFor(() => expect(requests.find((r) => r.method === "POST")?.body).toEqual({ planSha256: sha, note: null }));
});

it("keeps the link to the run when the run cannot be loaded", async () => {
  mockApi({});
  renderAt(<WaitingRun run={summary} />, route);
  await waitFor(() => expect(screen.queryByRole("status", { name: "Loading the run" })).toBeNull());
  expect(screen.getByRole("link", { name: "Open run #12" })).toBeInTheDocument();
  expect(screen.queryByRole("button", { name: "Approve" })).toBeNull();
});
