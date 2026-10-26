import { fireEvent, screen, waitFor, within } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";
import type { GuestDiskUsage as Usage } from "../api/types";
import { mockApi, renderAt } from "../test/utils";
import { GuestDiskUsage } from "./GuestDiskUsage";

const gib = 1024 ** 3;
function usage(overrides: Partial<Usage> = {}): Usage {
  return {
    enabled: true,
    intervalSeconds: 3600,
    configuredBytes: 16 * gib,
    usedBytes: 8 * gib,
    receivedAt: new Date(Date.now() - 30_000).toISOString(),
    staleAfter: new Date(Date.now() + 7200_000).toISOString(),
    ...overrides,
  };
}

function show(value = usage(), inactive = false) {
  return renderAt(<GuestDiskUsage id="vm-1" usage={value} inactive={inactive} />, { path: "/", route: "/" });
}

afterEach(() => vi.unstubAllGlobals());

describe("guest disk usage", () => {
  it("compares root usage with configured capacity without claiming filesystem free space", () => {
    show();
    expect(screen.getByRole("meter")).toHaveAttribute("aria-valuenow", "50");
    expect(screen.getByText("Configured capacity remaining")).toBeInTheDocument();
    expect(screen.getByText(/not filesystem-available space/)).toBeInTheDocument();
  });

  it("distinguishes no report, unknown capacity, and a real zero reading", () => {
    const view = show(usage({ usedBytes: null, receivedAt: null, staleAfter: null }));
    expect(screen.getByText("Waiting for the first report.")).toBeInTheDocument();
    expect(screen.queryByRole("meter")).not.toBeInTheDocument();
    view.unmount();
    const unknown = show(usage({ configuredBytes: null }));
    expect(screen.getByText("8.0 GiB")).toBeInTheDocument();
    expect(screen.queryByRole("meter")).not.toBeInTheDocument();
    unknown.unmount();
    show(usage({ usedBytes: 0 }));
    expect(screen.getByRole("meter")).toHaveAttribute("aria-valuenow", "0");
  });

  it("marks stale and disabled reports and does not clamp capacity mismatches to 100%", () => {
    const view = show(usage({ staleAfter: new Date(Date.now() - 60_000).toISOString() }));
    expect(screen.getByText("Stale report")).toBeInTheDocument();
    view.unmount();
    show(usage({ enabled: false, usedBytes: 32 * gib }), true);
    expect(screen.getByText("Reporting disabled")).toBeInTheDocument();
    expect(screen.getByText("Usage exceeds configured capacity")).toBeInTheDocument();
    expect(screen.queryByRole("meter")).not.toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Configure reporter" })).not.toBeInTheDocument();
  });

  it("creates a scoped configuration with the chosen interval and clears the secret on close", async () => {
    const token = "a".repeat(64);
    const { requests } = mockApi({
      "POST /api/guests/vm-1/disk-usage/token": () => ({
        token, reportPath: "/api/guests/vm-1/disk-usage", intervalSeconds: 1800,
      }),
    });
    show();
    fireEvent.click(screen.getByRole("button", { name: "Configure reporter" }));
    const create = screen.getByRole("button", { name: "Create reporting token" });
    fireEvent.change(screen.getByLabelText("Octopushy HTTPS address"), { target: { value: "http://octopushy.test" } });
    expect(create).toBeDisabled();
    fireEvent.change(screen.getByLabelText("Octopushy HTTPS address"), { target: { value: "https://octopushy.test" } });
    fireEvent.change(screen.getByLabelText("Reporting interval (minutes)"), { target: { value: "30" } });
    fireEvent.click(create);
    expect(await screen.findByText(token)).toBeInTheDocument();
    expect(requests[0].body).toEqual({ intervalSeconds: 1800 });
    expect(screen.getByText(/"endpoint": "https:\/\/octopushy.test\/api\/guests\/vm-1\/disk-usage"/)).toBeInTheDocument();
    fireEvent.click(screen.getByRole("button", { name: "Done" }));
    fireEvent.click(screen.getByRole("button", { name: "Configure reporter" }));
    expect(screen.queryByText(token)).not.toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Create reporting token" })).toBeInTheDocument();
  });

  it("requires confirmation to revoke the reporter without erasing the last reading", async () => {
    const { requests } = mockApi({ "DELETE /api/guests/vm-1/disk-usage/token": () => undefined });
    show();
    fireEvent.click(screen.getByRole("button", { name: "Revoke token" }));
    const dialog = screen.getByRole("dialog", { name: "Revoke reporting token?" });
    expect(requests).toHaveLength(0);
    fireEvent.click(within(dialog).getByRole("button", { name: "Revoke token" }));
    await waitFor(() => expect(requests).toHaveLength(1));
    expect(requests[0].method).toBe("DELETE");
    expect(screen.getByRole("meter")).toHaveAttribute("aria-valuenow", "50");
  });
});
