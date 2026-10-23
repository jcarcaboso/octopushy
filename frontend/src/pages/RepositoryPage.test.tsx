import { act, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, describe, expect, it, vi } from "vitest";
import type { Repository, Revision, RevisionReview, RunDetail } from "../api/types";
import { mockApi, problem, renderAt } from "../test/utils";
import { InventoryPage } from "./InventoryPage";
import { RepositoryPage } from "./RepositoryPage";
import { RunPage } from "./RunPage";
import { RunsPage } from "./RunsPage";

const commit = "1234567890abcdef1234567890abcdef12345678";
const baseCommit = "abcdef1234567890abcdef1234567890abcdef12";
const loadedAt = "2026-10-23T08:00:00Z";

function revision(overrides: Partial<Revision> = {}): Revision {
  return {
    id: "revision-1",
    commit,
    subject: "Update lab configuration",
    author: "Alice",
    committedAt: loadedAt,
    loadedAt,
    valid: true,
    errorCount: 0,
    warningCount: 0,
    trustedAt: null,
    trustedBy: null,
    isCurrentSource: true,
    ...overrides,
  };
}

function repository(current = revision()): Repository {
  return {
    url: "https://git.example.org/lab.git",
    branch: "main",
    pollSeconds: 60,
    credentialKind: "none",
    credentialUsername: null,
    credentialSet: false,
    lastFetchAt: loadedAt,
    lastFetchError: null,
    headCommit: commit,
    revision: current,
  };
}

function review(overrides: Partial<RevisionReview> = {}): RevisionReview {
  return {
    revision: revision(),
    baseCommit,
    diff: "diff --git a/main.tf b/main.tf\n+review this configuration",
    truncated: false,
    canTrust: true,
    blockedReason: null,
    ...overrides,
  };
}

function repositoryReads(current = revision()) {
  return {
    "GET /api/repository": () => repository(current),
    "GET /api/repository/revisions": () => [current],
    [`GET /api/repository/revisions/${current.id}`]: () => ({ revision: current, issues: [] }),
  };
}

function renderPage() {
  return renderAt(<RepositoryPage />, { path: "/repository", route: "/repository" });
}

async function openReview(user: ReturnType<typeof userEvent.setup>) {
  await user.click((await screen.findAllByRole("button", { name: "Review revision" }))[0]);
  return screen.getByRole("dialog", { name: "Review execution trust" });
}

describe("repository execution trust", () => {
  afterEach(() => {
    vi.useRealTimers();
    vi.unstubAllGlobals();
  });

  it("requires review and explicit confirmation of the displayed exact commit and trusted base", async () => {
    const user = userEvent.setup();
    let current = revision();
    const { requests } = mockApi({
      "GET /api/repository": () => repository(current),
      "GET /api/repository/revisions": () => [current],
      "GET /api/repository/revisions/revision-1": () => ({ revision: current, issues: [] }),
      "GET /api/repository/revisions/revision-1/review": () => ({
        revision: current,
        baseCommit,
        diff: "diff --git a/main.tf b/main.tf\n+review this configuration",
        truncated: false,
        canTrust: true,
        blockedReason: null,
      }),
      "POST /api/repository/revisions/revision-1/trust": () => {
        current = revision({ trustedAt: loadedAt, trustedBy: "operator" });
        return current;
      },
    });
    renderPage();
    const dialog = within(await openReview(user));

    expect(await dialog.findByText(commit)).toBeVisible();
    expect(dialog.getByText(baseCommit)).toBeVisible();
    expect(dialog.getByText(/including plan and observe/)).toBeVisible();
    expect(dialog.getByText(/does not approve infrastructure apply/i)).toBeVisible();
    expect(dialog.getByText(/automatic-apply policies may proceed without another click/i)).toBeVisible();
    expect(dialog.getByText(/does not guarantee safe code or cover future commits/i)).toBeVisible();
    const confirm = dialog.getByRole("button", { name: "Trust exact revision" });
    expect(confirm).toBeDisabled();
    expect(requests.filter((request) => request.method === "POST")).toEqual([]);

    await user.click(dialog.getByRole("checkbox", { name: /I reviewed the changes/ }));
    expect(confirm).toBeEnabled();
    await user.click(confirm);

    await waitFor(() => expect(screen.queryByRole("dialog")).not.toBeInTheDocument());
    expect(requests.filter((request) => request.method === "POST")).toEqual([
      {
        method: "POST",
        path: "/api/repository/revisions/revision-1/trust",
        search: "",
        body: { commit, baseCommit },
      },
    ]);
    expect(await screen.findAllByText(/by operator/)).not.toHaveLength(0);
  });

  it("keeps validity, execution trust, source eligibility and apply approval distinct without trusting on fetch or selection", async () => {
    const user = userEvent.setup();
    const previous = revision({
      id: "revision-old",
      commit: baseCommit,
      trustedAt: loadedAt,
      trustedBy: "reviewer",
      isCurrentSource: false,
    });
    const { requests } = mockApi({
      "GET /api/repository": () => repository(),
      "GET /api/repository/revisions": () => [revision(), previous],
      "GET /api/repository/revisions/revision-1": () => ({ revision: revision(), issues: [] }),
      "GET /api/repository/revisions/revision-old": () => ({ revision: previous, issues: [] }),
      "POST /api/repository/sync": () => undefined,
    });
    renderPage();
    const current = within(await screen.findByRole("region", { name: "Current revision" }));
    expect(current.getByText("Definition validity")).toBeVisible();
    expect(current.getByText("Valid")).toBeVisible();
    expect(current.getByText("Execution trust")).toBeVisible();
    expect(current.getByText("Untrusted")).toBeVisible();
    expect(current.getByText("Eligible for trust review")).toBeVisible();
    expect(current.getByText("Apply approval")).toBeVisible();
    expect(current.getByText(/Separate decision for each proposed infrastructure apply/)).toBeVisible();

    const history = within(await screen.findByRole("region", { name: "Revision history" }));
    const oldRow = within(history.getAllByRole("row")[2]);
    expect(oldRow.getByText("Not current repository source")).toBeVisible();
    expect(oldRow.getByText(/by reviewer/)).toBeVisible();
    await user.click(oldRow.getByRole("button", { name: "Show issues" }));
    const detail = within(await screen.findByRole("region", { name: "Selected revision" }));
    expect(await detail.findByText("Execution trust")).toBeVisible();
    expect(detail.getByText(/by reviewer/)).toBeVisible();
    expect(detail.getByText(/Trust does not apply to the current repository source/)).toBeVisible();
    expect(detail.getByText(/by reviewer/).querySelector("time")).toHaveAttribute("datetime", "2026-10-23T08:00:00.000Z");

    await user.click(screen.getByRole("button", { name: "Sync now" }));
    await waitFor(() => expect(requests.some((request) => request.method === "POST")).toBe(true));
    expect(requests.filter((request) => request.method === "POST").map((request) => request.path)).toEqual(["/api/repository/sync"]);
    expect(requests.some((request) => request.path.endsWith("/review"))).toBe(false);
  });

  it.each([
    {
      state: "invalid definitions",
      data: review({ revision: revision({ valid: false, errorCount: 1 }) }),
      message: "Definitions have errors. This revision cannot be trusted.",
    },
    {
      state: "a different repository source",
      data: review({ revision: revision({ isCurrentSource: false }) }),
      message: "This revision is not from the current repository source.",
    },
    {
      state: "a truncated diff",
      data: review({ truncated: true }),
      message: "The diff is truncated. A complete review is required.",
    },
    {
      state: "a server-blocked review",
      data: review({ canTrust: false, blockedReason: "The repository head changed. Sync and review again." }),
      message: "The repository head changed. Sync and review again.",
    },
    {
      state: "an already trusted revision",
      data: review({ revision: revision({ trustedAt: loadedAt, trustedBy: "reviewer" }) }),
      message: "This revision is already trusted.",
    },
    {
      state: "a missing review",
      data: null,
      message: "No complete revision review is available. Refresh the review to try again.",
    },
    {
      state: "a missing diff",
      data: review({ diff: undefined }),
      message: "No complete revision review is available. Refresh the review to try again.",
    },
    {
      state: "a Problem response",
      data: problem(409, "The review cannot be loaded while the repository is changing."),
      message: "The review cannot be loaded while the repository is changing.",
    },
  ])("allows inspection but not trust for $state", async ({ data, message }) => {
    const user = userEvent.setup();
    const { requests } = mockApi({
      ...repositoryReads(data && !(data instanceof Response) ? data.revision : revision()),
      "GET /api/repository/revisions/revision-1/review": () => data,
    });
    renderPage();
    const dialog = within(await openReview(user));
    expect(await dialog.findByText(message)).toBeVisible();
    expect(dialog.getByRole("button", { name: "Trust exact revision" })).toBeDisabled();
    expect(dialog.getByRole("checkbox", { name: /I reviewed the changes/ })).toBeDisabled();
    await user.click(dialog.getByRole("button", { name: "Trust exact revision" }));
    expect(requests.filter((request) => request.method === "POST")).toEqual([]);
  });

  it("renders first-trust full repository changes as plain text, including malicious-looking patch content", async () => {
    const user = userEvent.setup();
    const diff = '+<img src=x onerror="alert(1)">\n+<script>alert("patch")</script>\n+[click](javascript:alert(2))';
    const { requests } = mockApi({
      ...repositoryReads(),
      "GET /api/repository/revisions/revision-1/review": () => review({ baseCommit: null, diff }),
      "POST /api/repository/revisions/revision-1/trust": () => revision({ trustedAt: loadedAt, trustedBy: "operator" }),
    });
    renderPage();
    const element = await openReview(user);
    const dialog = within(element);
    expect(await dialog.findByText("First trust: full repository changes")).toBeVisible();
    expect(dialog.getByText(/<img src=x/).textContent).toBe(diff);
    expect(dialog.queryByRole("img")).not.toBeInTheDocument();
    expect(dialog.queryByRole("link")).not.toBeInTheDocument();
    expect(element.querySelector("script")).toBeNull();
    expect(requests.filter((request) => request.method === "POST")).toEqual([]);

    await user.click(dialog.getByRole("checkbox", { name: /I reviewed the changes/ }));
    await user.click(dialog.getByRole("button", { name: "Trust exact revision" }));
    await waitFor(() => expect(requests.some((request) => request.method === "POST")).toBe(true));
    expect(requests.find((request) => request.method === "POST")?.body).toEqual({ commit, baseCommit: null });
  });

  it("submits the captured review and requires a fresh review and confirmation after a stale-basis Problem", async () => {
    const user = userEvent.setup();
    const nextBase = "9999999999abcdef1234567890abcdef12345678";
    let currentReview = review();
    const { requests } = mockApi({
      ...repositoryReads(),
      "GET /api/repository/revisions/revision-1/review": () => currentReview,
      "POST /api/repository/revisions/revision-1/trust": (request) =>
        (request.body as { baseCommit: string }).baseCommit === baseCommit
          ? problem(409, "The trusted base changed. Refresh the review.")
          : revision({ trustedAt: loadedAt, trustedBy: "operator" }),
    });
    renderPage();
    const dialog = within(await openReview(user));
    await dialog.findByText(baseCommit);
    await user.click(dialog.getByRole("checkbox", { name: /I reviewed the changes/ }));
    currentReview = review({ baseCommit: nextBase, diff: "+Additional changes since the new trusted base" });
    await user.click(dialog.getByRole("button", { name: "Trust exact revision" }));

    expect(await dialog.findByText("The trusted base changed. Refresh the review.")).toBeVisible();
    expect(dialog.getByRole("button", { name: "Trust exact revision" })).toBeDisabled();
    await user.click(dialog.getByRole("button", { name: "Trust exact revision" }));
    expect(requests.filter((request) => request.method === "POST").map((request) => request.body)).toEqual([{ commit, baseCommit }]);
    expect(requests.filter((request) => request.path.endsWith("/review"))).toHaveLength(1);
    expect(dialog.getByText(baseCommit)).toBeVisible();
    expect(dialog.queryByText(nextBase)).not.toBeInTheDocument();

    await user.click(dialog.getByRole("button", { name: "Refresh review" }));
    expect(await dialog.findByText(nextBase)).toBeVisible();
    expect(dialog.getByRole("checkbox", { name: /I reviewed the changes/ })).not.toBeChecked();
    expect(dialog.getByRole("button", { name: "Trust exact revision" })).toBeDisabled();
    await user.click(dialog.getByRole("checkbox", { name: /I reviewed the changes/ }));
    await user.click(dialog.getByRole("button", { name: "Trust exact revision" }));
    await waitFor(() => expect(screen.queryByRole("dialog")).not.toBeInTheDocument());
    expect(requests.filter((request) => request.method === "POST").map((request) => request.body)).toEqual([
      { commit, baseCommit },
      { commit, baseCommit: nextBase },
    ]);
  });

  it("cannot confirm while the review is loading, then still requires explicit confirmation", async () => {
    const user = userEvent.setup();
    let finishReview!: (response: Response) => void;
    const pendingReview = new Promise<Response>((resolve) => { finishReview = resolve; });
    const { requests, fetchMock } = mockApi(repositoryReads());
    vi.stubGlobal("fetch", (request: Request) =>
      new URL(request.url).pathname.endsWith("/review") ? pendingReview : fetchMock(request),
    );
    renderPage();
    const dialog = within(await openReview(user));
    expect(dialog.getByRole("status")).toHaveTextContent("Loading revision review");
    expect(dialog.getByRole("button", { name: "Trust exact revision" })).toBeDisabled();
    expect(dialog.getByRole("checkbox", { name: /I reviewed the changes/ })).toBeDisabled();
    expect(requests.filter((request) => request.method === "POST")).toEqual([]);

    finishReview(new Response(JSON.stringify(review()), { headers: { "content-type": "application/json" } }));
    expect(await dialog.findByText(commit)).toBeVisible();
    expect(dialog.getByRole("checkbox", { name: /I reviewed the changes/ })).not.toBeChecked();
    expect(dialog.getByRole("button", { name: "Trust exact revision" })).toBeDisabled();
  });

  it("refreshes repository, revision list/detail, runs and inventory after trust and releases the visible trust block", async () => {
    const user = userEvent.setup();
    let trusted = false;
    const current = () => revision(trusted ? { trustedAt: loadedAt, trustedBy: "operator" } : {});
    function run(): RunDetail {
      return {
        run: {
          id: "run-1",
          number: 1,
          operation: "provision",
          resourceId: "guest-1",
          resourceKind: "guest",
          address: "home/proxmox/site-a/guests/app",
          status: trusted ? "queued" : "blocked",
          detail: null,
          reason: "revision",
          commit,
          createdAt: loadedAt,
          startedAt: null,
          finishedAt: null,
          currentStep: "plan",
          waiting: trusted ? "Waiting for a connected agent" : "Waiting for trust",
        },
        steps: [],
        agentLabels: [],
        agentLabelsSetBy: "default",
        policy: null,
        canCancel: false,
        canClose: false,
      };
    }
    const { requests } = mockApi({
      "GET /api/repository": () => repository(current()),
      "GET /api/repository/revisions": () => [current()],
      "GET /api/repository/revisions/revision-1": () => ({ revision: current(), issues: [] }),
      "GET /api/repository/revisions/revision-1/review": () => review(),
      "POST /api/repository/revisions/revision-1/trust": () => {
        trusted = true;
        return current();
      },
      "GET /api/runs": () => [run().run],
      "GET /api/runs/run-1": () => run(),
      "GET /api/inventory": () => ({ revision: current(), labs: [] }),
    });
    renderAt(<><RepositoryPage /><RunsPage /><RunPage /><InventoryPage /></>, { path: "/runs/:id", route: "/runs/run-1" });
    await waitFor(() => expect(screen.getAllByText("Waiting for trust")).toHaveLength(2));
    await screen.findByText("The repository declares no labs");
    await user.click(await screen.findByRole("button", { name: "Show issues" }));
    const selected = within(await screen.findByRole("region", { name: "Selected revision" }));
    expect(await selected.findByText("Untrusted")).toBeVisible();
    const dialog = within(await openReview(user));
    await dialog.findByText(commit);
    await user.click(dialog.getByRole("checkbox", { name: /I reviewed the changes/ }));
    await user.click(dialog.getByRole("button", { name: "Trust exact revision" }));

    await waitFor(() => expect(screen.queryByRole("dialog")).not.toBeInTheDocument());
    expect(await selected.findByText(/by operator/)).toBeVisible();
    expect(screen.queryByText("Waiting for trust")).not.toBeInTheDocument();
    expect(screen.getAllByText("Waiting for a connected agent")).toHaveLength(2);
    const trustRequest = requests.findIndex((request) => request.method === "POST");
    const refreshed = requests.slice(trustRequest + 1).filter((request) => request.method === "GET").map((request) => request.path);
    expect(refreshed).toEqual(expect.arrayContaining([
      "/api/repository",
      "/api/repository/revisions",
      "/api/repository/revisions/revision-1",
      "/api/runs",
      "/api/runs/run-1",
      "/api/inventory",
    ]));
    expect(requests.filter((request) => request.method === "POST").map((request) => request.path)).toEqual([
      "/api/repository/revisions/revision-1/trust",
    ]);
  });

  it("polls mutable trust metadata in revision details without replacing the displayed review", async () => {
    vi.useFakeTimers({ shouldAdvanceTime: true });
    const user = userEvent.setup({ advanceTimers: vi.advanceTimersByTime });
    let current = revision();
    const { requests } = mockApi({
      "GET /api/repository": () => repository(current),
      "GET /api/repository/revisions": () => [current],
      "GET /api/repository/revisions/revision-1": () => ({ revision: current, issues: [] }),
      "GET /api/repository/revisions/revision-1/review": () => review({ revision: current }),
    });
    renderPage();
    await user.click(await screen.findByRole("button", { name: "Show issues" }));
    const selected = within(await screen.findByRole("region", { name: "Selected revision" }));
    expect(await selected.findByText("Untrusted")).toBeVisible();
    const dialog = within(await openReview(user));
    await dialog.findByText(baseCommit);
    await user.click(dialog.getByRole("checkbox", { name: /I reviewed the changes/ }));
    current = revision({ trustedAt: loadedAt, trustedBy: "another operator" });
    await act(async () => { await vi.advanceTimersByTimeAsync(31_000); });

    expect(await selected.findByText(/by another operator/)).toBeVisible();
    expect(dialog.getByText(baseCommit)).toBeVisible();
    expect(dialog.getByRole("checkbox", { name: /I reviewed the changes/ })).toBeChecked();
    expect(requests.filter((request) => request.path.endsWith("/review"))).toHaveLength(1);
    expect(requests.filter((request) => request.method === "POST")).toEqual([]);
  });
});
