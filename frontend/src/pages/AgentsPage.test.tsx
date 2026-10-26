import { screen } from "@testing-library/react";
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
