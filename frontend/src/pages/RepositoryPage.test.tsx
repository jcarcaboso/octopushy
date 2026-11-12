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

describe("repository page", () => {
  afterEach(() => vi.unstubAllGlobals());

  it("shows validity and source without asking to trust revisions, and opens a revision's issues", async () => {
    const user = userEvent.setup();
    const previous = revision({ id: "revision-old", commit: previousCommit, isCurrentSource: false, warningCount: 1 });
    const { requests } = mockApi({
      "GET /api/repository": () => repository(),
      "GET /api/repository/revisions": () => [revision(), previous],
      "GET /api/repository/revisions/revision-old": () => ({
        revision: previous,
        issues: [{ severity: "warning", file: "labs/home/guest.yaml", line: null, message: "No access.user.", address: null }],
      }),
      "POST /api/repository/sync": () => undefined,
    });
    renderAt(<RepositoryPage />, { path: "/repository", route: "/repository" });

    const current = within(await screen.findByRole("region", { name: "Current revision" }));
    expect(current.getByText("Valid")).toBeVisible();
    expect(screen.getByText("In sync")).toBeVisible();
    expect(screen.queryByText(/trust/i)).not.toBeInTheDocument();

    const rows = within(await screen.findByRole("list", { name: "Revision history" })).getAllByRole("listitem");
    expect(within(rows[0]).getByText("Current")).toBeVisible();
    expect(within(rows[0]).queryByRole("button")).not.toBeInTheDocument();
    const oldRow = within(rows[1]);
    expect(oldRow.getByText("Previous source")).toBeVisible();
    await user.click(oldRow.getByRole("button", { name: "1 warning" }));
    expect(await oldRow.findByText("No access.user.")).toBeVisible();

    await user.click(screen.getByRole("button", { name: "Sync now" }));
    await waitFor(() => expect(requests.some((request) => request.method === "POST")).toBe(true));
    expect(requests.filter((request) => request.method === "POST").map((request) => request.path)).toEqual(["/api/repository/sync"]);
  });

  it("keeps the settings folded once they are set, and saves a check interval when it is chosen", async () => {
    const user = userEvent.setup();
    let stored = { ...repository(), credentialKind: "token", credentialUsername: "octopushy", credentialSet: true };
    const { requests } = mockApi({
      "GET /api/repository": () => stored,
      "GET /api/repository/revisions": () => [revision()],
      "PUT /api/repository": ({ body }) => (stored = { ...stored, pollSeconds: (body as { pollSeconds: number }).pollSeconds }),
    });
    renderAt(<RepositoryPage />, { path: "/repository", route: "/repository" });

    const source = within(await screen.findByRole("region", { name: "Source" }));
    expect(source.getByText("https://git.example.org/lab.git")).toBeVisible();
    expect(source.getByText("Token set")).toBeVisible();
    expect(screen.queryByLabelText("URL")).not.toBeInTheDocument();

    const interval = within(screen.getByRole("group", { name: "Check every" }));
    expect(interval.getByRole("button", { name: "60 s" })).toHaveAttribute("aria-pressed", "true");
    await user.click(interval.getByRole("button", { name: "5 min" }));
    await waitFor(() => expect(interval.getByRole("button", { name: "5 min" })).toHaveAttribute("aria-pressed", "true"));
    expect(requests.find((request) => request.method === "PUT")?.body).toEqual({
      url: "https://git.example.org/lab.git",
      branch: "main",
      pollSeconds: 300,
      credentialKind: "token",
      credentialUsername: "octopushy",
      credentialSecret: null,
    });

    await user.click(source.getByRole("button", { name: "Edit" }));
    expect(screen.getByLabelText("URL")).toHaveValue("https://git.example.org/lab.git");
    await user.click(screen.getByRole("button", { name: "Cancel" }));
    expect(screen.queryByLabelText("URL")).not.toBeInTheDocument();
  });

  it("opens the settings when no repository is connected", async () => {
    mockApi({
      "GET /api/repository": () => ({ ...repository(), url: null, lastFetchAt: null, headCommit: null, revision: null }),
      "GET /api/repository/revisions": () => [],
    });
    renderAt(<RepositoryPage />, { path: "/repository", route: "/repository" });

    expect(await screen.findByLabelText("URL")).toHaveValue("");
    expect(screen.getByText("Not connected")).toBeVisible();
    expect(screen.queryByRole("button", { name: "Cancel" })).not.toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Sync now" })).toBeDisabled();
  });

  it("shows the last fifteen revisions until asked for the rest", async () => {
    const user = userEvent.setup();
    const many = Array.from({ length: 20 }, (_, i) => revision({ id: `revision-${i}`, commit: `${i}`.padStart(40, "0"), subject: `Change ${i}` }));
    mockApi({
      "GET /api/repository": () => repository(many[0]),
      "GET /api/repository/revisions": () => many,
    });
    renderAt(<RepositoryPage />, { path: "/repository", route: "/repository" });

    const history = within(await screen.findByRole("list", { name: "Revision history" }));
    expect(history.getAllByRole("listitem")).toHaveLength(15);
    await user.click(screen.getByRole("button", { name: "Show the last 20" }));
    expect(history.getAllByRole("listitem")).toHaveLength(20);
  });

  it("lists each workflow on one line, with its releases and what uses it folded under it", async () => {
    const user = userEvent.setup();
    const tagged = "aaaaaaaaaa1111111111aaaaaaaaaa1111111111";
    const moved = "bbbbbbbbbb2222222222bbbbbbbbbb2222222222";
    const lab = "git@github.com:jcarcaboso/octopushy-lab.git";
    const issue = `fedora-vm/v1.0 was moved from aaaaaaaaaa to bbbbbbbbbb; the original commit is used. Moved tags are not followed.`;
    const head = revision({ workflowsRepository: lab, workflowsDigest: "d1g35t0000" });
    mockApi({
      "GET /api/repository": () => ({
        ...repository(head),
        workflows: {
          source: "repository",
          repository: lab,
          lastFetchAt: loadedAt,
          lastFetchError: null,
          issues: [issue],
          workflows: [
            {
              id: "fedora-vm",
              releases: [
                { version: "1.1", commit: tagged, firstSeenAt: loadedAt, usable: true, tagDeleted: false, movedTo: null, issue: null },
                { version: "1.0", commit: tagged, firstSeenAt: loadedAt, usable: true, tagDeleted: false, movedTo: moved, issue },
              ],
            },
          ],
          uses: [
            { address: "home/korriban/app", kind: "guest", reference: "fedora-vm@1", version: "1.1", commit: tagged, problem: null, newerMajor: "2.0" },
            { address: "home/korriban/db", kind: "guest", reference: "fedora-vm@1.0", version: "1.0", commit: tagged, problem: null, newerMajor: null },
            { address: "home/korriban/web", kind: "service", reference: "gone@1", version: null, commit: null, problem: "Not resolved in the head revision.", newerMajor: null },
          ],
        },
      }),
      "GET /api/repository/revisions": () => [head, revision({ id: "revision-0", workflowsRepository: lab, workflowsDigest: "0lderd1g35t" })],
    });
    renderAt(<RepositoryPage />, { path: "/repository", route: "/repository?tab=workflows" });

    expect(await screen.findByRole("link", { name: "Workflows 1" })).toHaveAttribute("aria-current", "page");
    expect(screen.getByText(lab)).toBeVisible();
    expect(screen.getAllByText(issue)[0]).toBeVisible();
    const line = screen.getByText("fedora-vm").closest("summary")!;
    expect(within(line).getByText("2 guests")).toBeVisible();
    expect(within(line).getByText("Has issues")).toBeVisible();
    expect(screen.getByText("Tag moved")).not.toBeVisible();
    await user.click(line);
    expect(screen.getByText("Tag moved")).toBeVisible();
    const uses = within(screen.getByRole("list", { name: "What uses fedora-vm" }));
    expect(uses.getByText("fedora-vm 2.0 available, breaking")).toBeVisible();
    expect(uses.getByText("fedora-vm@1.1")).toBeVisible();
    expect(within(screen.getByRole("list", { name: "References to workflows that have no release" })).getByText("gone@1")).toBeVisible();

    // The same commit loaded twice differs only in what its workflows resolved to, so the history tells them apart.
    await user.click(screen.getByRole("link", { name: "History" }));
    const history = within(await screen.findByRole("list", { name: "Revision history" }));
    expect(history.getByText("d1g35t0")).toBeVisible();
    expect(history.getByText("0lderd1")).toBeVisible();
  });
});
