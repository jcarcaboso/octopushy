import { screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, expect, it, vi } from "vitest";
import type { Webhook } from "../api/types";
import { mockApi, renderAt } from "../test/utils";
import { NotificationsPage } from "./NotificationsPage";

afterEach(() => vi.unstubAllGlobals());

const defaults = ["run-approval", "run-failed", "run-uncertain", "agent-offline", "agent-online", "agent-update-failed"];

const unset: Webhook = {
  configured: false, host: null, enabled: false, kinds: defaults, defaultKinds: defaults,
  availableKinds: ["check-down", "check-up", ...defaults], httpAllowed: false, linksIncluded: true,
  lastAttemptAt: null, lastDelivered: null, lastStatus: null, lastResult: null, lastDeliveredAt: null, nextAttemptAt: null, dropped: 0,
  stoppedAt: null,
};

const set: Webhook = {
  ...unset, configured: true, enabled: true, host: "hooks.example.org",
  lastAttemptAt: "2026-11-17T09:00:00Z", lastDelivered: false, lastStatus: 503, lastResult: "HTTP 503: the receiver had an error.",
};

it("sets the URL with the kinds chosen and shows the signing secret only until dismissed", async () => {
  const { requests } = mockApi({
    "GET /api/notifications/webhook": () => unset,
    "PUT /api/notifications/webhook": () => ({ webhook: set, secret: "whsec_synthetic" }),
  });
  const user = userEvent.setup();
  renderAt(<NotificationsPage />, { path: "/notifications", route: "/notifications" });
  expect(await screen.findByText("Not set")).toBeVisible();
  expect(screen.getByRole("button", { name: "Send a test event" })).toBeDisabled();
  // Uptime checks are not sent unless chosen.
  expect(screen.getByRole("checkbox", { name: /An uptime check went down/ })).not.toBeChecked();
  expect(screen.getByRole("checkbox", { name: /A run failed/ })).toBeChecked();

  await user.type(screen.getByLabelText("URL"), "https://hooks.example.org/T0KEN");
  await user.click(screen.getByRole("checkbox", { name: /An agent told offline is back/ }));
  await user.click(screen.getByRole("button", { name: "Save" }));

  const put = requests.find((r) => r.method === "PUT")!;
  expect(put.body).toEqual({ url: "https://hooks.example.org/T0KEN", kinds: defaults.filter((k) => k !== "agent-online") });
  expect(await screen.findByLabelText("Signing secret")).toHaveValue("whsec_synthetic");
  expect(screen.getByText("hooks.example.org")).toBeVisible();
  expect(screen.getByLabelText("New URL")).toHaveValue("");
  await user.click(screen.getByRole("button", { name: "I saved it" }));
  expect(screen.queryByLabelText("Signing secret")).not.toBeInTheDocument();
  expect(window.localStorage.length).toBe(0);
});

it("sends a test event and shows what the receiver said, with the last delivery", async () => {
  const test = vi.fn(() => ({ delivered: false, status: 500, result: "HTTP 500: the receiver had an error." }));
  mockApi({
    "GET /api/notifications/webhook": () => set,
    "POST /api/notifications/webhook/test": test,
  });
  const user = userEvent.setup();
  renderAt(<NotificationsPage />, { path: "/notifications", route: "/notifications" });
  expect(await screen.findByText("HTTP 503: the receiver had an error.")).toBeVisible();
  await user.click(screen.getByRole("button", { name: "Send a test event" }));
  expect(test).toHaveBeenCalledOnce();
  expect(await screen.findByText("The test event was not delivered")).toBeVisible();
  expect(screen.getByText("HTTP 500: the receiver had an error.")).toBeVisible();
});

it("says when the receiver answered gone and sending stopped", async () => {
  mockApi({ "GET /api/notifications/webhook": () => ({ ...set, enabled: false, stoppedAt: "2026-11-17T09:00:00Z", lastStatus: 410 }) });
  renderAt(<NotificationsPage />, { path: "/notifications", route: "/notifications" });
  expect(await screen.findByText("The receiver answered 410 Gone")).toBeVisible();
  expect(screen.getByText("Stopped")).toBeVisible();
});
