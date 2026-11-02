import { screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { describe, expect, it } from "vitest";
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
  schedules: [],
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

describe("a guest's schedules", () => {
  const schedule = {
    action: "update-tools",
    cron: "0 6 * * *",
    timezone: "UTC",
    nextAt: "2026-11-03T06:00:00Z",
    lastRunId: null,
    lastRunNumber: null,
    lastRunStatus: null,
    lastRunDetail: null,
    note: null,
    noteAt: null,
  };

  it("shows what is next, and says nothing about a run that has not happened", () => {
    renderAt(<GuestConfigurationCard id="guest-1" configuration={{ ...configuration, schedules: [schedule] }} />, {
      path: "/guests/:id",
      route: "/guests/guest-1",
    });
    expect(screen.getByText("update-tools", { selector: "li *" })).toBeInTheDocument();
    expect(screen.getByText("0 6 * * *")).toBeInTheDocument();
    expect(screen.getByText(/Next:/)).toBeInTheDocument();
    expect(screen.queryByText(/Last:/)).not.toBeInTheDocument();
  });

  it("keeps a failure and a skipped firing in view", () => {
    const failed = {
      ...schedule,
      lastRunId: "r1",
      lastRunNumber: 12,
      lastRunStatus: "failed",
      lastRunDetail: "the task failed",
      note: "Skipped: Run #12 is uncertain.",
      noteAt: "2026-11-02T06:00:00Z",
    };
    renderAt(<GuestConfigurationCard id="guest-1" configuration={{ ...configuration, schedules: [failed] }} />, {
      path: "/guests/:id",
      route: "/guests/guest-1",
    });
    expect(screen.getByRole("link", { name: "#12" })).toHaveAttribute("href", "/runs/r1");
    expect(screen.getByText("failed")).toBeInTheDocument();
    expect(screen.getByText("the task failed")).toBeInTheDocument();
    expect(screen.getByText(/Skipped: Run #12 is uncertain./)).toBeInTheDocument();
  });
});
