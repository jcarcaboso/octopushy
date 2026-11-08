import { screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import type { Agent } from "../api/types";
import { afterEach, expect, it, vi } from "vitest";
import { mockApi, renderAt } from "../test/utils";
import { AgentsPage } from "./AgentsPage";

afterEach(() => vi.unstubAllGlobals());

it("offers manual enrollment, not a live Deploy action", async () => {
  mockApi({
    "GET /api/agents": () => [],
    "GET /api/agents/tokens": () => [],
  });
  renderAt(<AgentsPage />, { path: "/agents", route: "/agents" });
  expect((await screen.findAllByRole("button", { name: "Enroll an agent" })).length).toBeGreaterThan(0);
  expect(screen.queryByRole("button", { name: /^Deploy/ })).not.toBeInTheDocument();
  expect(screen.getAllByRole("button", { name: "Enroll an agent" }).length).toBeGreaterThan(0);
});

it("drains scheduling separately from disabling an agent", async () => {
  const user = userEvent.setup();
  const agent: Agent = {
    id: "agent-1", name: "worker", labels: ["home"], maxConcurrency: 1,
    disabled: false, draining: false, connected: true, version: "test",
    executors: ["proxmox"], toolRuntime: "podman", runningAttempts: 0, reportsReachability: true,
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

it("shows what each agent reaches, why not, and which agents need an update", async () => {
  const base = {
    labels: ["home"], maxConcurrency: 1, disabled: false, draining: false, connected: true, version: "test",
    executors: ["proxmox", "opentofu"], runningAttempts: 0, enrolledAt: "2026-10-27T10:00:00Z",
    lastSeenAt: "2026-10-27T10:00:00Z", certificateExpiresAt: "2027-01-01T00:00:00Z",
  };
  const remote: Agent = {
    ...base, id: "b", name: "site-b", toolRuntime: "podman", reportsReachability: true,
    reachability: [
      { kind: "proxmox-api", endpoint: "https://10.1.0.10:8006", context: "agent", image: null, state: "reachable",
        detail: "TLS handshake completed", checkedAt: new Date().toISOString(), expiresAt: null, usable: true, probing: false,
        leafSha256: "0123456789abcdef0123" },
      { kind: "guest-ssh", endpoint: "ssh://10.1.0.31:22", context: "tool", image: "localhost/octopushy-ansible:1",
        state: "unsupported", detail: "the image has neither python3 nor nc", checkedAt: new Date().toISOString(),
        expiresAt: null, usable: false, probing: false, leafSha256: null },
      { kind: "proxmox-api", endpoint: "https://10.1.0.10:8006", context: "tool", image: "ghcr.io/opentofu/opentofu:1.13.1",
        state: "timeout", detail: "connecting timed out", checkedAt: new Date().toISOString(), expiresAt: null,
        usable: false, probing: true, leafSha256: null },
    ],
  };
  const old: Agent = { ...base, id: "a", name: "site-a", toolRuntime: "local", reportsReachability: false, reachability: [] };
  mockApi({
    "GET /api/agents": () => [old, remote],
    "GET /api/agents/tokens": () => [],
  });
  renderAt(<AgentsPage />, { path: "/agents", route: "/agents" });
  const table = await screen.findByRole("region", { name: "Reachability" });
  expect(table).toHaveTextContent("Guest SSH");
  expect(table).toHaveTextContent("Unsupported");
  expect(table).toHaveTextContent("the image has neither python3 nor nc");
  expect(table).toHaveTextContent("Timeout · probing");
  expect(table).toHaveTextContent("Reachable");
  expect(table).toHaveTextContent("Agent process");
  expect(table).toHaveTextContent("localhost/octopushy-ansible:1");
  expect(screen.getByRole("note")).toHaveTextContent("site-a does not report reachability");
  expect(screen.getByText(/does not check the host key/)).toBeVisible();
});
