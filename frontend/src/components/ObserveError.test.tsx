import { screen } from "@testing-library/react";
import { expect, it } from "vitest";
import type { InstallationSummary } from "../api/types";
import { inventoryOf } from "../test/inventory";
import { renderAt } from "../test/utils";
import { ObserveError } from "./ObserveError";

const installation = (more: Partial<InstallationSummary>): InstallationSummary => ({
  ...inventoryOf([]).labs[0].installations[0],
  observeErrorAt: null,
  ...more,
});

const render = (value: InstallationSummary) => renderAt(<ObserveError installation={value} />, { path: "/", route: "/" });

it("shows an observation held for credentials as blocked, with Variables to fix it, whatever its words", () => {
  render(installation({ observeError: "The read-only token of home/pve01 is not there yet.", observeBlockReason: "credentials" }));
  expect(screen.getByText("Observing is blocked")).toBeInTheDocument();
  expect(screen.getByText("The read-only token of home/pve01 is not there yet.")).toBeInTheDocument();
  expect(screen.getByRole("link", { name: "Open Variables" })).toHaveAttribute("href", "/variables");
  expect(screen.queryByText("The last observation failed")).toBeNull();
});

it("shows the words of a blocked observation without the reason as a failure", () => {
  render(installation({ observeError: "Blocked credentials: proxmox/pve01/read-only-token: missing.", observeBlockReason: null }));
  expect(screen.getByText("The last observation failed")).toBeInTheDocument();
  expect(screen.getByText("Blocked credentials: proxmox/pve01/read-only-token: missing.")).toBeInTheDocument();
  expect(screen.queryByRole("link", { name: "Open Variables" })).toBeNull();
  expect(screen.queryByText("Observing is blocked")).toBeNull();
});

it("shows nothing while the latest observation succeeded", () => {
  const { container } = render(installation({ observeError: null }));
  expect(container).toBeEmptyDOMElement();
});
