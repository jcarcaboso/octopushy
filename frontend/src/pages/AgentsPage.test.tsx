import { screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import type { Agent } from "../api/types";
import { afterEach, expect, it, vi } from "vitest";
import { mockApi, renderAt } from "../test/utils";
import { AgentsPage } from "./AgentsPage";

afterEach(() => vi.unstubAllGlobals());

it("offers worker preparation separately from manual enrollment, not a live Deploy action", async () => {
  mockApi({
    "GET /api/agents": () => [],
    "GET /api/agents/tokens": () => [],
  });
  renderAt(<AgentsPage />, { path: "/agents", route: "/agents" });
  expect(await screen.findByRole("link", { name: "Prepare worker deployment" })).toHaveAttribute("href", "/agents/prepare");
  expect(screen.queryByRole("button", { name: /^Deploy/ })).not.toBeInTheDocument();
  expect(screen.getAllByRole("button", { name: "Enroll an agent" }).length).toBeGreaterThan(0);
});

it("drains scheduling separately from disabling an agent", async () => {
  const user = userEvent.setup();
  const agent: Agent = {
    id: "agent-1", name: "worker", labels: ["home"], maxConcurrency: 1,
    disabled: false, draining: false, connected: true, version: "test",
    executors: ["proxmox"], toolRuntime: "podman", runningAttempts: 0,
    enrolledAt: "2026-10-27T10:00:00Z", lastSeenAt: "2026-10-27T10:00:00Z",
    certificateExpiresAt: "2027-01-01T00:00:00Z",
  };
  const { requests } = mockApi({
    "GET /api/agents": () => [agent],
    "GET /api/agents/tokens": () => [],
    "PATCH /api/agents/agent-1": () => { agent.draining = true; },
  });
  renderAt(<AgentsPage />, { path: "/agents", route: "/agents" });
  await user.click(await screen.findByRole("button", { name: "Drain" }));
  await waitFor(() => expect(requests.some((r) => r.method === "PATCH")).toBe(true));
  expect(requests.find((r) => r.method === "PATCH")?.body).toMatchObject({ draining: true, disabled: null });
  expect(await screen.findByRole("button", { name: "Resume scheduling" })).toBeVisible();
  expect(screen.getByText("Connected")).toBeVisible();
});
