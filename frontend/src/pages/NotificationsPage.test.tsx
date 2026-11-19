import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { act, render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { MemoryRouter, Route, Routes } from "react-router";
import { afterEach, beforeEach, expect, it, vi } from "vitest";
import { keys } from "../api/queries";
import type { NotificationItem } from "../api/types";
import { Notifications } from "../components/Notifications";
import { mockApi, type RecordedRequest } from "../test/utils";
import { NotificationsPage } from "./NotificationsPage";

/** A local time on November 2026, so Today and Yesterday do not depend on when the test runs. */
const at = (day: number, hour: number, minute = 0) => new Date(2026, 10, day, hour, minute).toISOString();

const told = (id: number, createdAt: string, title: string, extra: Partial<NotificationItem> = {}): NotificationItem => ({
  id, kind: "check-down", title, body: null, resourceId: null, resourceKind: null, runId: null, agentId: null, createdAt,
  runNumber: null, resourceAddress: null, agentName: null, ...extra,
});

const dns = { resourceId: "dns", resourceKind: "guest", resourceAddress: "home/pve01/dns", body: "tcp://10.0.0.53:53 · No answer" };

const month: NotificationItem[] = [
  told(20, at(19, 14, 48), "Provision run awaits approval", { kind: "run-approval", runId: "r49", runNumber: 49, body: "home/pve01/bb", resourceId: "bb", resourceKind: "guest", resourceAddress: "home/pve01/bb" }),
  told(19, at(19, 14, 10), "home/pve01/media/jellyfin is down", { body: "https://jellyfin.example.org/ · HTTP 502", resourceId: "svc-jellyfin", resourceKind: "service", resourceAddress: "home/pve01/media/jellyfin" }),
  told(18, at(19, 11, 20), "home/pve01/dns is up again", { ...dns, kind: "check-up", body: "tcp://10.0.0.53:53" }),
  told(17, at(19, 11, 15), "home/pve01/dns is down", dns),
  told(16, at(19, 10, 40), "home/pve01/dns is up again", { ...dns, kind: "check-up", body: "tcp://10.0.0.53:53" }),
  told(15, at(19, 10, 30), "home/pve01/dns is down", dns),
  told(14, at(18, 22, 5), "Agent laptop is offline", { kind: "agent-offline", agentId: "agent-laptop", agentName: "laptop" }),
  told(13, at(17, 9, 0), "home/pve01/old-proxy is down", { body: "https://proxy.example.org/ · HTTP 502" }),
];

const page = (items: NotificationItem[], nextBefore: number | null = null) => ({ items, nextBefore });
const inboxRequests = (requests: RecordedRequest[]) =>
  requests.filter((r) => r.path === "/api/notifications" && new URLSearchParams(r.search).has("limit")).map((r) => new URLSearchParams(r.search));

let client: QueryClient;

function show(handlers: Parameters<typeof mockApi>[0]) {
  const api = mockApi(handlers);
  client = new QueryClient({ defaultOptions: { queries: { retry: false, refetchOnWindowFocus: false } } });
  render(
    <QueryClientProvider client={client}>
      <MemoryRouter initialEntries={["/notifications"]}>
        <Routes>
          <Route
            path="/notifications"
            element={
              <>
                <Notifications />
                <NotificationsPage />
              </>
            }
          />
        </Routes>
      </MemoryRouter>
    </QueryClientProvider>,
  );
  return api;
}

/** The bell reads the latest without parameters; the inbox always names its page size. */
const bellOr = (bell: () => unknown, inbox: (request: RecordedRequest) => unknown) => (request: RecordedRequest) =>
  new URLSearchParams(request.search).has("limit") ? inbox(request) : bell();

beforeEach(() => {
  localStorage.clear();
  vi.useFakeTimers({ toFake: ["Date"] });
  vi.setSystemTime(new Date(2026, 10, 19, 15, 0));
});
afterEach(() => {
  vi.useRealTimers();
  vi.unstubAllGlobals();
});

it("lists the month under a heading per day, one line per notification in fixed columns", async () => {
  localStorage.setItem("octopushy.notifications.seen", "19");
  show({ "GET /api/notifications": () => page(month) });

  const today = within(await screen.findByRole("region", { name: "Today" }));
  expect(screen.getByRole("region", { name: "Yesterday" })).toBeVisible();
  expect(screen.getByRole("region", { name: "Tuesday 17 November" })).toBeVisible();
  expect(today.getByText("6 notifications")).toBeVisible();

  const approval = today.getByRole("link", { name: /Provision run awaits approval/ });
  expect(approval).toHaveAttribute("href", "/runs/r49");
  expect(approval).toHaveTextContent(/^14:48Provision run awaits approval\(unread\)home\/pve01\/bbRun #49$/);
  const down = today.getByRole("link", { name: /jellyfin is down/ });
  expect(down).toHaveAttribute("href", "/services/svc-jellyfin");
  // Only the detail of a check that went down, not its URL; read, so no dot.
  expect(within(down).getByText("HTTP 502")).toBeVisible();
  expect(down).not.toHaveTextContent("https://");
  expect(down).not.toHaveTextContent("(unread)");
  expect(within(down).getByText("Service")).toBeVisible();

  const agent = within(screen.getByRole("region", { name: "Yesterday" })).getByRole("link", { name: /Agent laptop is offline/ });
  expect(agent).toHaveAttribute("href", "/agents");
  expect(within(agent).getByText("laptop")).toHaveClass("chip");

  // A resource since removed leads nowhere.
  const gone = within(screen.getByRole("region", { name: "Tuesday 17 November" })).getByText("home/pve01/old-proxy is down").closest(".inbox-row")!;
  expect(gone.tagName).toBe("DIV");
  expect(gone).toHaveTextContent(/Gone$/);
  expect(screen.getByText("Notifications are kept for 31 days.")).toBeVisible();
  expect(screen.queryByRole("button", { name: "Load older" })).not.toBeInTheDocument();
});

it("folds a check that went down twice or more in a row into one line that opens in place", async () => {
  const user = userEvent.setup();
  show({ "GET /api/notifications": () => page(month) });

  const fold = await screen.findByRole("button", { name: /home\/pve01\/dns went down 2 times/ });
  expect(fold).toHaveAttribute("aria-expanded", "false");
  expect(fold).toHaveTextContent("10:30 to 11:20, up now");
  expect(screen.queryByRole("link", { name: /home\/pve01\/dns is down/ })).not.toBeInTheDocument();

  await user.click(fold);
  expect(fold).toHaveAttribute("aria-expanded", "true");
  expect(screen.getAllByRole("link", { name: /home\/pve01\/dns is (down|up again)/ })).toHaveLength(4);
  await user.click(fold);
  expect(screen.queryByRole("link", { name: /home\/pve01\/dns is down/ })).not.toBeInTheDocument();
});

it("asks the server for the kinds chosen, what is unread and what is typed", async () => {
  const user = userEvent.setup();
  localStorage.setItem("octopushy.notifications.seen", "17");
  const { requests } = show({ "GET /api/notifications": () => page(month) });
  await screen.findByRole("region", { name: "Today" });
  expect(inboxRequests(requests)[0].toString()).toBe("limit=100");

  await user.click(screen.getByRole("button", { name: "Runs" }));
  await waitFor(() => expect(inboxRequests(requests).at(-1)!.getAll("kind")).toEqual(["run-approval", "run-failed", "run-uncertain"]));
  expect(screen.getByRole("button", { name: "Runs" })).toHaveAttribute("aria-pressed", "true");

  await user.click(screen.getByRole("button", { name: "Uptime" }));
  await waitFor(() => expect(inboxRequests(requests).at(-1)!.getAll("kind")).toEqual(["check-down", "check-up"]));
  await user.click(screen.getByRole("button", { name: "Agents" }));
  await waitFor(() => expect(inboxRequests(requests).at(-1)!.getAll("kind")).toEqual(["agent-offline", "agent-online", "agent-update-failed"]));
  await user.click(screen.getByRole("button", { name: "All" }));

  await user.click(screen.getByRole("checkbox", { name: "Unread only" }));
  await waitFor(() => expect(inboxRequests(requests).at(-1)!.get("after")).toBe("17"));
  expect(inboxRequests(requests).at(-1)!.has("kind")).toBe(false);

  const before = requests.length;
  await user.type(screen.getByRole("searchbox", { name: "Search notifications" }), "jelly");
  await waitFor(() => expect(inboxRequests(requests).at(-1)!.get("q")).toBe("jelly"));
  // Typing waits until it stops: one request for the five letters.
  expect(inboxRequests(requests.slice(before)).length).toBe(1);
});

it("says when nothing matches, and clears the filters", async () => {
  const user = userEvent.setup();
  show({ "GET /api/notifications": bellOr(() => page(month), (r) => page(new URLSearchParams(r.search).has("q") ? [] : month)) });
  await screen.findByRole("region", { name: "Today" });
  await user.type(screen.getByRole("searchbox", { name: "Search notifications" }), "nothing");
  expect(await screen.findByText("Nothing matches.")).toBeVisible();
  await user.click(screen.getByRole("button", { name: "Clear filters" }));
  expect(await screen.findByRole("region", { name: "Today" })).toBeVisible();
  expect(screen.getByRole("searchbox", { name: "Search notifications" })).toHaveValue("");
});

it("loads older notifications a page at a time with before", async () => {
  const user = userEvent.setup();
  const { requests } = show({
    "GET /api/notifications": bellOr(
      () => page(month),
      (r) => (new URLSearchParams(r.search).get("before") === "14" ? page(month.slice(6)) : page(month.slice(0, 6), 14)),
    ),
  });
  await screen.findByRole("region", { name: "Today" });
  expect(screen.queryByRole("region", { name: "Yesterday" })).not.toBeInTheDocument();
  expect(screen.queryByText("Notifications are kept for 31 days.")).not.toBeInTheDocument();

  await user.click(screen.getByRole("button", { name: "Load older" }));
  expect(await screen.findByRole("region", { name: "Yesterday" })).toBeVisible();
  expect(inboxRequests(requests).at(-1)!.get("before")).toBe("14");
  expect(screen.queryByRole("button", { name: "Load older" })).not.toBeInTheDocument();
  expect(screen.getByText("Notifications are kept for 31 days.")).toBeVisible();
});

it("marks all read, and the bell's count goes at once", async () => {
  const user = userEvent.setup();
  localStorage.setItem("octopushy.notifications.seen", "18");
  show({ "GET /api/notifications": () => page(month) });

  expect(await screen.findByRole("button", { name: "Notifications, 2 unread" })).toBeVisible();
  expect(await screen.findByText("2 unread")).toBeVisible();
  await user.click(screen.getByRole("button", { name: "Mark all read" }));
  expect(screen.getByRole("button", { name: "Notifications" })).toBeVisible();
  expect(screen.queryByText("2 unread")).not.toBeInTheDocument();
  expect(screen.getByRole("button", { name: "Mark all read" })).toBeDisabled();
  expect(localStorage.getItem("octopushy.notifications.seen")).toBe("20");
  expect(screen.queryByText("(unread)")).not.toBeInTheDocument();
});

it("puts what the bell's poll brings on top, fetching only what came after the newest listed", async () => {
  let latest = month;
  const above = (r: RecordedRequest) => Number(new URLSearchParams(r.search).get("after") ?? 0);
  const { requests } = show({ "GET /api/notifications": bellOr(() => page(latest), (r) => page(latest.filter((item) => item.id > above(r)))) });
  await screen.findByRole("region", { name: "Today" });
  const fresh = told(21, at(19, 14, 59), "home/pve01/web/app is down", { resourceId: "web-app", resourceKind: "service", resourceAddress: "home/pve01/web/app" });
  latest = [fresh, ...month];
  const asked = inboxRequests(requests).length;

  await act(() => client.refetchQueries({ queryKey: keys.notifications, exact: true }));
  expect(await screen.findByRole("link", { name: /home\/pve01\/web\/app is down/ })).toBeVisible();
  const after = inboxRequests(requests).slice(asked);
  expect(after.map((query) => query.toString())).toEqual(["limit=200&after=20"]);
});
