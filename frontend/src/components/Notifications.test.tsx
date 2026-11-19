import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { act, render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { MemoryRouter } from "react-router";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import type { NotificationItem } from "../api/types";
import { keys } from "../api/queries";
import { mockApi } from "../test/utils";
import { Notifications } from "./Notifications";

const told = (id: number, title: string, extra: Partial<NotificationItem> = {}): NotificationItem => ({
  id, kind: "check-down", title, body: null, resourceId: null, resourceKind: null, runId: null, agentId: null, createdAt: "2026-11-16T09:00:00Z",
  runNumber: null, resourceAddress: null, agentName: null, ...extra,
});

describe("notifications", () => {
  let items: NotificationItem[];
  let client: QueryClient;

  beforeEach(() => {
    localStorage.clear();
    items = [
      told(2, "home/pve01/media/jellyfin is down", { body: "https://jellyfin.home.example.org/ · HTTP 502", resourceId: "svc-jellyfin", resourceKind: "service" }),
      told(1, "Provision run failed", { kind: "run-failed", body: "home/pve01/media", runId: "failed" }),
    ];
    mockApi({ "GET /api/notifications": () => ({ items }) });
    client = new QueryClient({ defaultOptions: { queries: { retry: false, refetchOnWindowFocus: false } } });
    render(
      <QueryClientProvider client={client}>
        <MemoryRouter>
          <Notifications />
        </MemoryRouter>
      </QueryClientProvider>,
    );
  });
  afterEach(() => vi.unstubAllGlobals());

  /** What the next poll brings. */
  const arrive = (...more: NotificationItem[]) => {
    items = [...more, ...items];
    return act(() => client.refetchQueries({ queryKey: keys.notifications }));
  };
  const toasts = () => within(screen.getByRole("region", { name: "New notifications" }));

  it("counts what is unread on the bell, lists it with where it leads, and reading it clears the count", async () => {
    const user = userEvent.setup();
    const bell = await screen.findByRole("button", { name: "Notifications, 2 unread" });
    // What was already there on load does not pop up.
    expect(toasts().queryByRole("listitem")).not.toBeInTheDocument();

    await user.click(bell);
    const list = within(screen.getByRole("region", { name: "Notifications" }));
    expect(list.getByRole("link", { name: /jellyfin is down\s*\(unread\).*HTTP 502/ })).toHaveAttribute("href", "/services/svc-jellyfin");
    expect(list.getByRole("link", { name: /Provision run failed/ })).toHaveAttribute("href", "/runs/failed");

    await user.keyboard("{Escape}");
    expect(screen.getByRole("button", { name: "Notifications" })).toHaveFocus();
    expect(localStorage.getItem("octopushy.notifications.seen")).toBe("2");
  });

  it("leads to the inbox from the foot of its list, and following it closes the list and reads it", async () => {
    const user = userEvent.setup();
    await user.click(await screen.findByRole("button", { name: "Notifications, 2 unread" }));
    const all = within(screen.getByRole("region", { name: "Notifications" })).getByRole("link", { name: "All notifications" });
    expect(all).toHaveAttribute("href", "/notifications");
    await user.click(all);
    expect(screen.queryByRole("region", { name: "Notifications" })).not.toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Notifications" })).toBeVisible();
  });

  it("pops up what arrives, three at most with the rest as one line, and each goes away by itself or by hand", async () => {
    const user = userEvent.setup();
    await screen.findByRole("button", { name: "Notifications, 2 unread" });

    await arrive(told(3, "home/pve01/web/app is down"));
    expect(await toasts().findByText("home/pve01/web/app is down")).toBeVisible();
    await user.click(toasts().getByRole("button", { name: "Dismiss: home/pve01/web/app is down" }));
    expect(toasts().queryByRole("listitem")).not.toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Notifications, 3 unread" })).toBeVisible();

    await arrive(...[8, 7, 6, 5, 4].map((id) => told(id, `check ${id} is down`)));
    await toasts().findByText("check 8 is down");
    expect(toasts().getAllByText(/check \d is down/).map((e) => e.textContent)).toEqual(["check 8 is down", "check 7 is down", "check 6 is down"]);
    await user.click(toasts().getByRole("button", { name: "2 more notifications" }));
    // The line opens the list, which now holds them all, and the pop-ups go.
    expect(toasts().queryByRole("listitem")).not.toBeInTheDocument();
    expect(within(screen.getByRole("region", { name: "Notifications" })).getAllByRole("listitem")).toHaveLength(8);
    // Escape closes it from wherever the focus is: the line that opened it is gone.
    await user.keyboard("{Escape}");
    expect(screen.queryByRole("region", { name: "Notifications" })).not.toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Notifications" })).toHaveFocus();
  });

  it("lets a pop-up go after a few seconds, but not while the pointer is on it", async () => {
    await screen.findByRole("button", { name: "Notifications, 2 unread" });
    vi.useFakeTimers({ shouldAdvanceTime: true });
    try {
      const user = userEvent.setup({ advanceTimers: vi.advanceTimersByTime });
      await arrive(told(3, "home/pve01/web/app is down"));
      const text = await toasts().findByText("home/pve01/web/app is down");
      // The bar that shows the time left runs with the timer, for as long as it does.
      const bar = text.closest("li")!.querySelector(".toast__timer")!;
      expect(bar).toHaveClass("toast__timer--running");
      expect(bar).toHaveStyle({ animationDuration: "8000ms" });
      await user.hover(text);
      expect(bar).not.toHaveClass("toast__timer--running");
      await act(() => vi.advanceTimersByTime(20_000));
      expect(toasts().getByText("home/pve01/web/app is down")).toBeVisible();
      await user.unhover(toasts().getByText("home/pve01/web/app is down"));
      expect(bar).toHaveClass("toast__timer--running");
      await act(() => vi.advanceTimersByTime(8_000));
      await waitFor(() => expect(toasts().queryByRole("listitem")).not.toBeInTheDocument());
    } finally {
      vi.useRealTimers();
    }
  });

  it("waits while the focus is in a pop-up though the pointer left, and one dismissed from the keyboard does not hold the next", async () => {
    await screen.findByRole("button", { name: "Notifications, 2 unread" });
    vi.useFakeTimers({ shouldAdvanceTime: true });
    try {
      const user = userEvent.setup({ advanceTimers: vi.advanceTimersByTime });
      await arrive(told(3, "home/pve01/web/app is down"));
      const text = await toasts().findByText("home/pve01/web/app is down");
      act(() => toasts().getByRole("button", { name: "Dismiss: home/pve01/web/app is down" }).focus());
      await user.hover(text);
      await user.unhover(text);
      await act(() => vi.advanceTimersByTime(20_000));
      expect(toasts().getByText("home/pve01/web/app is down")).toBeVisible();

      // The button goes with its pop-up, and nothing says the focus left.
      await user.keyboard("{Enter}");
      expect(toasts().queryByRole("listitem")).not.toBeInTheDocument();
      await arrive(told(4, "home/pve01/web/app is up again", { kind: "check-up" }));
      await toasts().findByText("home/pve01/web/app is up again");
      await act(() => vi.advanceTimersByTime(8_000));
      await waitFor(() => expect(toasts().queryByRole("listitem")).not.toBeInTheDocument());
    } finally {
      vi.useRealTimers();
    }
  });
});
