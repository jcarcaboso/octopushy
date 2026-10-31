import { screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, describe, expect, it, vi } from "vitest";
import type { Repository, Revision } from "../api/types";
import { mockApi, renderAt } from "../test/utils";
import { RepositoryPage } from "./RepositoryPage";

const commit = "1234567890abcdef1234567890abcdef12345678";
const previousCommit = "abcdef1234567890abcdef1234567890abcdef12";
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

describe("repository revisions", () => {
  afterEach(() => vi.unstubAllGlobals());

  it("shows validity and source without asking to trust revisions", async () => {
    const user = userEvent.setup();
    const previous = revision({ id: "revision-old", commit: previousCommit, isCurrentSource: false });
    const { requests } = mockApi({
      "GET /api/repository": () => repository(),
      "GET /api/repository/revisions": () => [revision(), previous],
      "GET /api/repository/revisions/revision-1": () => ({ revision: revision(), issues: [] }),
      "GET /api/repository/revisions/revision-old": () => ({ revision: previous, issues: [] }),
      "POST /api/repository/sync": () => undefined,
    });
    renderAt(<RepositoryPage />, { path: "/repository", route: "/repository" });

    const current = within(await screen.findByRole("region", { name: "Current revision" }));
    expect(current.getByText("Definition validity")).toBeVisible();
    expect(current.getByText("Valid")).toBeVisible();
    expect(screen.queryByText(/trust/i)).not.toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Review revision" })).not.toBeInTheDocument();

    const history = within(await screen.findByRole("region", { name: "Revision history" }));
    const oldRow = within(history.getAllByRole("row")[2]);
    expect(oldRow.getByText("Previous source")).toBeVisible();
    await user.click(oldRow.getByRole("button", { name: "Show issues" }));
    const detail = within(await screen.findByRole("region", { name: "Selected revision" }));
    expect(await detail.findByText("This revision has no issues.")).toBeVisible();

    await user.click(screen.getByRole("button", { name: "Sync now" }));
    await waitFor(() => expect(requests.some((request) => request.method === "POST")).toBe(true));
    expect(requests.filter((request) => request.method === "POST").map((request) => request.path)).toEqual(["/api/repository/sync"]);
  });
});
