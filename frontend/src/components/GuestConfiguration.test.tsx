import { screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { expect, it } from "vitest";
import type { GuestConfiguration } from "../api/types";
import { mockApi, problem, renderAt } from "../test/utils";
import { GuestConfigurationCard } from "./GuestConfiguration";

const configuration: GuestConfiguration = {
  recipe: "ai-server",
  actions: [
    { id: "configure", description: null, destructive: false, onChange: true },
    { id: "update-system", description: "Update the OS.", destructive: false, onChange: false },
  ],
  inSync: false,
  appliedAction: null,
  appliedCommit: null,
  appliedAt: null,
  hasSnapshot: false,
};

it("starts the chosen action", async () => {
  const { requests } = mockApi({ "POST /api/guests/guest-1/configure": () => ({ runId: "run-9" }) });
  renderAt(<GuestConfigurationCard id="guest-1" configuration={configuration} />, { path: "/guests/:id", route: "/guests/guest-1" });
  await userEvent.click(screen.getByRole("button", { name: "update-system" }));
  await waitFor(() => expect(requests.some((r) => r.method === "POST")).toBe(true));
  expect(requests.find((r) => r.method === "POST")?.body).toEqual({ action: "update-system" });
});

it("shows why an action was refused", async () => {
  mockApi({ "POST /api/guests/guest-1/configure": () => problem(409, "This guest has none yet.") });
  renderAt(<GuestConfigurationCard id="guest-1" configuration={configuration} />, { path: "/guests/:id", route: "/guests/guest-1" });
  await userEvent.click(screen.getByRole("button", { name: "configure" }));
  expect(await screen.findByText(/This guest has none yet/)).toBeTruthy();
});
