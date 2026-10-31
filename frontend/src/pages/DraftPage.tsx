import {
  CheckCheck,
  Columns2,
  FilePlus,
  GitPullRequestArrow,
  PencilLine,
  RefreshCw,
  RotateCcw,
  Save,
  Trash2,
  Upload,
} from "lucide-react";
import { useCallback, useEffect, useRef, useState, useSyncExternalStore } from "react";
import { useQueryClient } from "@tanstack/react-query";
import { Link, useNavigate, useParams, useSearchParams } from "react-router";
import { errorMessage, isNotFound } from "../api/client";
import {
  keys,
  useChangeDraft,
  useDiscardDraft,
  useDraft,
  useUpdateDraft,
  useValidateDraft,
} from "../api/queries";
import type { DraftDetail } from "../api/types";
import { Badge } from "../components/Badge";
import { Callout } from "../components/Callout";
import { Card } from "../components/Card";
import { ConfirmDialog } from "../components/ConfirmDialog";
import { IssueList } from "../components/IssueList";
import { Commit } from "../components/Mono";
import { PageHeader } from "../components/PageHeader";
import { ErrorMessage, NotFound, PageSkeleton, Spinner } from "../components/States";
import { parentFolders } from "../lib/fileTree";
import { ChangeList } from "./editor/ChangeList";
import { CodeEditor } from "./editor/CodeEditor";
import { draftStatus } from "./editor/draftStatus";
import { FileTree } from "./editor/FileTree";
import { NewFileDialog } from "./editor/NewFileDialog";
import { PublishDialog } from "./editor/PublishDialog";
import { forgetWorkspace, workspaceFor, type Buffer } from "./editor/workspace";

export function DraftPage() {
  const { id = "" } = useParams();
  const { data, error, isPending } = useDraft(id);
  if (isPending) return <PageSkeleton rows={3} />;
  if (!data) return isNotFound(error) ? <NotFound what="draft" /> : <ErrorMessage error={error} title="The draft could not be loaded" />;
  return <Workspace detail={data} />;
}

function Workspace({ detail }: { detail: DraftDetail }) {
  const { draft } = detail;
  const navigate = useNavigate();
  const [params, setParams] = useSearchParams();
  const selected = params.get("file");
  const workspace = workspaceFor(draft.id);
  useSyncExternalStore(workspace.subscribe, () => workspace.version);
  const dirty = workspace.dirtyPaths();
  const editable = detail.editBlockedReason === null;

  const change = useChangeDraft(draft.id);
  const update = useUpdateDraft(draft.id);
  const validate = useValidateDraft(draft.id);
  const discard = useDiscardDraft();
  const [comparing, setComparing] = useState(false);
  const [dialog, setDialog] = useState<"new" | "delete" | "discard" | "publish" | null>(null);
  const [published, setPublished] = useState<string | null>(null);
  const client = useQueryClient();

  const select = useCallback(
    (path: string | null) =>
      setParams(
        (current) => {
          const next = new URLSearchParams(current);
          if (path) next.set("file", path);
          else next.delete("file");
          return next;
        },
        { replace: true },
      ),
    [setParams],
  );

  // Open the selected file; the workspace keeps it once loaded.
  useEffect(() => {
    if (selected) workspace.ensure(selected);
  }, [selected, workspace, workspace.version]);

  // Unsaved edits survive moving around the app, not closing the tab.
  useEffect(() => {
    if (dirty.length === 0) return;
    const warn = (event: BeforeUnloadEvent) => event.preventDefault();
    window.addEventListener("beforeunload", warn);
    return () => window.removeEventListener("beforeunload", warn);
  }, [dirty.length]);

  const saving = useRef(false);
  const save = useCallback(async () => {
    const paths = workspace.dirtyPaths();
    if (!editable || paths.length === 0 || saving.current) return;
    saving.current = true;
    const changes = paths.map((path) => ({ path, content: workspace.get(path)!.model.getValue() }));
    const versions = paths.map((path) => workspace.get(path)!.model.getAlternativeVersionId());
    try {
      // The latest head, also when Ctrl+S runs a callback from an earlier render.
      const expectedHead = client.getQueryData<DraftDetail>(keys.draft(draft.id))?.draft.headCommit ?? draft.headCommit;
      await change.mutateAsync({ expectedHead, changes });
      paths.forEach((path, i) => workspace.markSaved(path, versions[i]));
      validate.reset();
    } catch {
      // Shown from the mutation's error.
    } finally {
      saving.current = false;
    }
  }, [change, client, draft.id, draft.headCommit, editable, validate, workspace]);

  const reload = () => {
    change.reset();
    update.reset();
    workspace.reset();
    void client.invalidateQueries({ queryKey: keys.draft(draft.id) });
  };

  const createFile = (path: string) =>
    change.mutate(
      { expectedHead: draft.headCommit, changes: [{ path, content: "" }] },
      {
        onSuccess: () => {
          setDialog(null);
          select(path);
        },
      },
    );

  const removeFile = (path: string) =>
    change.mutate(
      { expectedHead: draft.headCommit, changes: [{ path, content: null }] },
      {
        onSuccess: () => {
          workspace.close(path);
          setDialog(null);
          select(null);
        },
      },
    );

  const restoreFile = (buffer: Buffer) =>
    change.mutate(
      { expectedHead: draft.headCommit, changes: [{ path: buffer.path, content: buffer.base.getValue() }] },
      {
        onSuccess: () => {
          setComparing(false);
          workspace.close(buffer.path);
        },
      },
    );

  const updateDraft = () =>
    update.mutate(draft.headCommit, {
      // Every file may have changed; reopen from the new head.
      onSuccess: () => workspace.reset(),
    });

  const opened = selected ? workspace.entry(selected) : undefined;
  const buffer = opened?.state === "open" ? opened.buffer : null;
  const status = draftStatus(draft.status);
  const existing = new Set(detail.files.map((f) => f.path));
  const changeError = change.error ?? update.error;

  return (
    <div className="draft-page">
      <PageHeader
        eyebrow={
          <Link to="/editor" className="muted">
            Editor
          </Link>
        }
        title={draft.title}
        documentTitle={`${draft.title} · Editor`}
        meta={
          <span className="draft-page__meta">
            <Badge tone={status.tone}>{status.label}</Badge>
            <span>
              on <code className="mono">{detail.branch}</code> from <Commit commit={draft.baseCommit} />
            </span>
            <span className="num">
              {detail.changes.length} changed {detail.changes.length === 1 ? "file" : "files"}
            </span>
          </span>
        }
        actions={
          <div className="button-row">
            <button
              type="button"
              className="button"
              onClick={() => validate.mutate()}
              disabled={validate.isPending || dirty.length > 0}
              title={dirty.length > 0 ? "Save your edits first" : "Check definitions and file formats"}
            >
              <CheckCheck size={15} aria-hidden="true" />
              {validate.isPending ? "Validating…" : "Validate"}
            </button>
            <button
              type="button"
              className="button button--primary"
              onClick={() => setDialog("publish")}
              disabled={detail.publishBlockedReason !== null || dirty.length > 0}
              title={dirty.length > 0 ? "Save your edits first" : (detail.publishBlockedReason ?? "Push this draft as one commit")}
            >
              <Upload size={15} aria-hidden="true" />
              Publish
            </button>
            {draft.status !== "published" ? (
              <button type="button" className="button button--ghost" onClick={() => setDialog("discard")}>
                <Trash2 size={15} aria-hidden="true" />
                Discard
              </button>
            ) : null}
          </div>
        }
      />

      <div className="stack">
        {published || draft.publishedCommit ? (
          <Callout kind="success" title={`Published as ${(published ?? draft.publishedCommit)!.slice(0, 10)}`}>
            The commit is on <code className="mono">{detail.branch}</code>. Octopushy loads it at the next sync; follow it on
            the <Link to="/repository">Repository</Link> page.
          </Callout>
        ) : null}
        {detail.editBlockedReason && draft.status !== "published" ? <Callout kind="warning">{detail.editBlockedReason}</Callout> : null}
        {draft.behind ? (
          <Callout
            kind="info"
            title="The branch has moved"
            actions={
              <button
                type="button"
                className="button button--small"
                onClick={updateDraft}
                disabled={update.isPending || dirty.length > 0}
                title={dirty.length > 0 ? "Save your edits first" : undefined}
              >
                <GitPullRequestArrow size={15} aria-hidden="true" />
                {update.isPending ? "Updating…" : "Update draft"}
              </button>
            }
          >
            New commits arrived since this draft started. Publishing merges them automatically when they touch other files;
            update the draft to see and resolve overlapping edits here.
          </Callout>
        ) : null}
        {draft.conflictPaths.length > 0 ? (
          <Callout kind="warning" title="Resolve conflicts before publishing">
            These files contain conflict markers. Edit each one, keep the lines you want, remove the markers, and save:{" "}
            {draft.conflictPaths.map((path, i) => (
              <span key={path}>
                {i > 0 ? ", " : null}
                <button type="button" className="link-button mono" onClick={() => select(path)}>
                  {path}
                </button>
              </span>
            ))}
          </Callout>
        ) : null}
        {detail.publishBlockedReason && editable && detail.changes.length > 0 && draft.conflictPaths.length === 0 ? (
          <Callout kind="info">{detail.publishBlockedReason}</Callout>
        ) : null}
        {changeError ? (
          <Callout
            kind="danger"
            title="The draft was not changed"
            alert
            actions={
              <button type="button" className="button button--small" onClick={reload}>
                <RefreshCw size={15} aria-hidden="true" />
                Reload the draft
              </button>
            }
          >
            {errorMessage(changeError)}
          </Callout>
        ) : null}
        {validate.data ? (
          <Card
            title="Validation"
            meta={validate.data.valid ? <Badge tone="success">No new errors</Badge> : <Badge tone="danger">Has errors</Badge>}
          >
            <IssueList
              issues={[...validate.data.newIssues, ...validate.data.formatIssues]}
              emptyText="The draft adds no definition issues, and its changed JSON and YAML files parse."
            />
          </Card>
        ) : validate.error ? (
          <ErrorMessage error={validate.error} title="The draft could not be validated" />
        ) : null}

        <div className="workspace">
          <aside className="workspace__side" aria-label="Files">
            <div className="workspace__side-actions">
              <button
                type="button"
                className="button button--small"
                onClick={() => setDialog("new")}
                disabled={!editable}
              >
                <FilePlus size={15} aria-hidden="true" />
                New file
              </button>
            </div>
            <FileTree
              files={detail.files}
              changes={detail.changes}
              dirty={dirty}
              conflicts={draft.conflictPaths}
              selected={selected}
              onSelect={select}
            />
            <div className="workspace__changes">
              <h2 className="workspace__heading">Changes</h2>
              <ChangeList changes={detail.changes} onSelect={select} />
            </div>
          </aside>

          <section className="workspace__main" aria-label="Editor">
            <div className="workspace__toolbar">
              <code className="mono workspace__path">{selected ?? "No file open"}</code>
              {buffer && workspace.isDirty(buffer.path) ? <Badge tone="attention">Unsaved</Badge> : null}
              {buffer?.deleted ? <Badge tone="danger">Deleted in this draft</Badge> : null}
              <span className="workspace__spacer" />
              {buffer ? (
                <>
                  <nav className="segmented segmented--inline" aria-label="View">
                    <button
                      type="button"
                      className="segmented__item"
                      aria-current={!comparing ? "page" : undefined}
                      onClick={() => setComparing(false)}
                      disabled={buffer.deleted}
                    >
                      <PencilLine size={14} aria-hidden="true" /> Edit
                    </button>
                    <button
                      type="button"
                      className="segmented__item"
                      aria-current={comparing ? "page" : undefined}
                      onClick={() => setComparing(true)}
                    >
                      <Columns2 size={14} aria-hidden="true" /> Compare with base
                    </button>
                  </nav>
                  {buffer.deleted ? (
                    <button type="button" className="button button--small" onClick={() => restoreFile(buffer)} disabled={!editable || change.isPending}>
                      <RotateCcw size={15} aria-hidden="true" />
                      Restore
                    </button>
                  ) : (
                    <>
                      <button
                        type="button"
                        className="button button--small button--ghost"
                        onClick={() => setDialog("delete")}
                        disabled={!editable || change.isPending}
                      >
                        <Trash2 size={15} aria-hidden="true" />
                        Delete
                      </button>
                      <button
                        type="button"
                        className="button button--small button--primary"
                        onClick={() => void save()}
                        disabled={!editable || dirty.length === 0 || change.isPending}
                        title="Save every edited file to the draft (Ctrl+S)"
                      >
                        <Save size={15} aria-hidden="true" />
                        {change.isPending ? "Saving…" : dirty.length > 1 ? `Save ${dirty.length} files` : "Save"}
                      </button>
                    </>
                  )}
                </>
              ) : null}
            </div>
            <div className="workspace__editor">
              {opened?.state === "loading" ? (
                <div className="workspace__placeholder">
                  <Spinner label="Opening the file" />
                </div>
              ) : opened?.state === "unavailable" ? (
                <div className="workspace__placeholder muted">{opened.message}</div>
              ) : !selected ? (
                <div className="workspace__placeholder muted">
                  Choose a file to edit. Saving records it in this draft; nothing reaches Git until you publish.
                </div>
              ) : null}
              <CodeEditor
                model={buffer?.model ?? null}
                original={comparing || buffer?.deleted ? (buffer?.base ?? null) : null}
                readOnly={!editable || Boolean(buffer?.deleted)}
                onSave={() => void save()}
              />
            </div>
          </section>
        </div>
      </div>

      {dialog === "new" ? (
        <NewFileDialog
          open
          folder={selected ? (parentFolders(selected).at(-1) ?? "") : ""}
          existing={existing}
          pending={change.isPending}
          error={change.error}
          onCreate={createFile}
          onClose={() => {
            change.reset();
            setDialog(null);
          }}
        />
      ) : null}
      <ConfirmDialog
        open={dialog === "delete" && buffer !== null}
        title="Delete this file?"
        confirmLabel="Delete from draft"
        danger
        pending={change.isPending}
        error={change.error}
        onConfirm={() => buffer && removeFile(buffer.path)}
        onClose={() => setDialog(null)}
      >
        <p>
          <code className="mono">{buffer?.path}</code> is removed in this draft. It can be restored until the draft is published.
        </p>
      </ConfirmDialog>
      <ConfirmDialog
        open={dialog === "discard"}
        title="Discard this draft?"
        confirmLabel="Discard draft"
        danger
        pending={discard.isPending}
        error={discard.error}
        onConfirm={() =>
          discard.mutate(draft.id, {
            onSuccess: () => {
              forgetWorkspace(draft.id);
              navigate("/editor");
            },
          })
        }
        onClose={() => setDialog(null)}
      >
        <p>Its {detail.changes.length} changed files and any unsaved edits are lost. The repository is not touched.</p>
      </ConfirmDialog>
      {dialog === "publish" ? (
        <PublishDialog
          detail={detail}
          open
          onClose={() => setDialog(null)}
          onPublished={(commit) => {
            forgetWorkspace(draft.id);
            setPublished(commit);
            setDialog(null);
            select(null);
          }}
        />
      ) : null}
    </div>
  );
}
