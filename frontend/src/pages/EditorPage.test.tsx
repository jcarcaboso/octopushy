import { screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, describe, expect, it, vi } from "vitest";
import type { DraftDetail, DraftSummary, Repository } from "../api/types";
import { mockApi, problem, renderAt } from "../test/utils";
import { EditorPage } from "./EditorPage";

// jsdom cannot run Monaco; a model is plain text with a version, and the editor a textarea.
vi.mock("./editor/monaco", () => {
  const createModel = (initial: string) => {
    let value = initial;
    let version = 1;
    const listeners: (() => void)[] = [];
    return {
      getValue: () => value,
      setValue: (next: string) => {
        value = next;
        version++;
        listeners.forEach((l) => l());
      },
      getAlternativeVersionId: () => version,
      onDidChangeContent: (listener: () => void) => listeners.push(listener),
      dispose: () => {},
    };
  };
  return {
    monaco: { editor: { createModel }, Uri: { from: () => ({}) } },
    disposeModels: (models: { dispose(): void }[]) => models.forEach((m) => m.dispose()),
  };
});

vi.mock("./editor/CodeEditor", () => ({
  CodeEditor: ({ model, readOnly }: { model: { getValue(): string; setValue(v: string): void } | null; readOnly: boolean }) =>
    model ? (
      <textarea aria-label="File content" readOnly={readOnly} defaultValue={model.getValue()} onChange={(e) => model.setValue(e.target.value)} />
    ) : null,
}));

const { DraftPage } = await import("./DraftPage");

const base = "1111111111111111111111111111111111111111";
const head = "2222222222222222222222222222222222222222";
const next = "3333333333333333333333333333333333333333";

function summary(overrides: Partial<DraftSummary> = {}): DraftSummary {
  return {
    id: "d1",
    title: "Tidy the lab",
    status: "open",
    baseCommit: base,
    headCommit: head,
    changedFiles: 0,
    behind: false,
    conflictPaths: [],
    createdAt: "2026-10-28T09:00:00Z",
    updatedAt: "2026-10-28T09:00:00Z",
    publishedCommit: null,
    publishedAt: null,
    publishedBy: null,
    ...overrides,
  };
}

function detail(overrides: Partial<DraftDetail> = {}, draft: Partial<DraftSummary> = {}): DraftDetail {
  return {
    draft: summary(draft),
    branch: "main",
    editBlockedReason: null,
    publishBlockedReason: "The draft has no changes yet.",
    changes: [],
    files: [
      { path: "octopushy.yaml", size: 10, kind: "file" },
      { path: "labs/home/lab.yaml", size: 20, kind: "file" },
    ],
    ...overrides,
  };
}

const repository = { url: "https://git.example.org/lab.git", branch: "main", headCommit: base } as Repository;

afterEach(() => vi.unstubAllGlobals());

describe("EditorPage", () => {
  it("lists drafts and starts a new one from the current commit", async () => {
    const { requests } = mockApi({
      "GET /api/repository": () => repository,
      "GET /api/repository/drafts": () => [summary({ changedFiles: 2, behind: true }), summary({ id: "d2", title: "Old", status: "published", publishedCommit: next })],
      "POST /api/repository/drafts": () => detail({}, { id: "d3", title: "New work" }),
      "GET /api/repository/drafts/d3": () => detail({}, { id: "d3", title: "New work" }),
    });
    renderAt(<EditorPage />, { path: "/editor", route: "/editor" });

    expect(await screen.findByRole("link", { name: "Tidy the lab" })).toHaveAttribute("href", "/editor/d1");
    expect(screen.getByText("Behind")).toBeInTheDocument();
    expect(screen.getByRole("region", { name: "Published drafts" })).toHaveTextContent("Old");

    await userEvent.type(screen.getByLabelText("Title"), "New work");
    await userEvent.click(screen.getByRole("button", { name: "Start a draft" }));
    await waitFor(() => expect(requests.some((r) => r.method === "POST")).toBe(true));
    expect(requests.find((r) => r.method === "POST")!.body).toEqual({ title: "New work" });
  });
});

describe("DraftPage", () => {
  it("saves edits to the draft against its head and keeps publishing explicit", async () => {
    let current = detail();
    const { requests } = mockApi({
      "GET /api/repository/drafts/d1": () => current,
      "GET /api/repository/drafts/d1/file": () => ({
        path: "labs/home/lab.yaml",
        headCommit: head,
        exists: true,
        kind: "file",
        size: 20,
        content: "name: Home\n",
        baseExists: true,
        baseContent: "name: Home\n",
        editable: true,
      }),
      "POST /api/repository/drafts/d1/changes": () => {
        current = detail(
          { changes: [{ path: "labs/home/lab.yaml", change: "modified" }], publishBlockedReason: null },
          { headCommit: next, changedFiles: 1 },
        );
        return current;
      },
    });
    renderAt(<DraftPage />, { path: "/editor/:id", route: "/editor/d1?file=labs/home/lab.yaml" });

    const content = await screen.findByLabelText("File content");
    expect(screen.getByRole("button", { name: "Publish" })).toBeDisabled();
    await userEvent.type(content, "x");
    expect(await screen.findByText("Unsaved")).toBeInTheDocument();
    await userEvent.click(screen.getByRole("button", { name: "Save" }));

    await waitFor(() => expect(screen.queryByText("Unsaved")).not.toBeInTheDocument());
    const save = requests.find((r) => r.method === "POST")!;
    expect(save.body).toEqual({ expectedHead: head, changes: [{ path: "labs/home/lab.yaml", content: "name: Home\nx" }] });
    expect(screen.getByRole("button", { name: "Publish" })).toBeEnabled();
    expect(screen.getAllByText("labs/home/lab.yaml").length).toBeGreaterThan(0);
  });

  it("shows why a stale save was refused and offers reloading", async () => {
    mockApi({
      "GET /api/repository/drafts/d1": () => detail(),
      "GET /api/repository/drafts/d1/file": () => ({
        path: "octopushy.yaml",
        headCommit: head,
        exists: true,
        kind: "file",
        size: 10,
        content: "a: 1\n",
        baseExists: true,
        baseContent: "a: 1\n",
        editable: true,
      }),
      "POST /api/repository/drafts/d1/changes": () => problem(409, "The draft changed since it was loaded. Reload it and make the edit again."),
    });
    renderAt(<DraftPage />, { path: "/editor/:id", route: "/editor/d1?file=octopushy.yaml" });
    await userEvent.type(await screen.findByLabelText("File content"), "b");
    await userEvent.click(screen.getByRole("button", { name: "Save" }));
    expect(await screen.findByRole("alert")).toHaveTextContent("The draft changed since it was loaded");
    expect(screen.getByRole("button", { name: "Reload the draft" })).toBeInTheDocument();
  });
});
