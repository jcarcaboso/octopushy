import { ArrowLeft, Download, Eye, EyeOff, Lock, Maximize2, Minimize2, Pencil, Replace, Trash2, X } from "lucide-react";
import { useEffect, useState, type ReactNode } from "react";
import { ApiError } from "../../api/client";
import { useDeleteVariable, useSetVariable, useStageVariable } from "../../api/queries";
import type { Variable } from "../../api/types";
import { Callout } from "../../components/Callout";
import { ConfirmDialog } from "../../components/ConfirmDialog";
import { CopyButton } from "../../components/CopyButton";
import { RelativeTime } from "../../components/RelativeTime";
import { ConflictNote } from "./ConflictNote";
import { lastSegment, parentFolder } from "./folders";
import { FormatLine, ValueEditor, ValueViewer } from "./ValueText";
import { VersionsTab } from "./VersionsTab";

export type Tab = "value" | "versions" | "details";
/** An edit keeps the version it started from: a refetch must not move what the save is checked against. */
type Edit = { mode: "modify" | "update"; base?: string; text: string; version: number };

interface Props {
  entry: Variable;
  /** Where the back link returns to, on a narrow screen. */
  backLabel: string;
  full: boolean;
  onToggleFull: () => void;
  onClose: () => void;
  /** The secret's value once revealed: null when it has none that may be used, undefined while concealed. */
  revealed: string | null | undefined;
  revealing: boolean;
  /** Reveals the value through the logged endpoint and resolves with it; undefined when that failed. */
  onReveal: () => Promise<string | null | undefined>;
  onHide: () => void;
  onDirty: (dirty: boolean) => void;
  onRefresh: () => void;
  onDeleted: () => void;
}

function download(name: string, value: string) {
  const url = URL.createObjectURL(new Blob([value], { type: "text/plain" }));
  const link = document.createElement("a");
  link.href = url;
  link.download = name;
  link.click();
  setTimeout(() => URL.revokeObjectURL(url), 1_000);
}

const plural = (n: number, one: string, many: string) => (n === 1 ? `1 ${one}` : `${n} ${many}`);

export function UsedBy({ usedBy }: { usedBy: string[] }) {
  if (usedBy.length === 0) return <span className="muted">Not referenced</span>;
  return (
    <ul className="used-by">
      {usedBy.map((u) => (
        <li key={u} className="mono small">
          {u}
        </li>
      ))}
    </ul>
  );
}

function Chips({ entry, lines }: { entry: Variable; lines: number | null }) {
  return (
    <span className="vars-chips">
      {entry.visibility === "secret" ? (
        <span className="run-chip">
          <Lock size={12} aria-hidden="true" />
          Secret
        </span>
      ) : (
        <span className="run-chip">Plain</span>
      )}
      {lines !== null && lines > 1 ? <span className="run-chip">{plural(lines, "line", "lines")}</span> : null}
      {entry.revoked ? (
        <span className="run-chip vars-chip--danger">v{entry.version} revoked</span>
      ) : (
        <span className="run-chip vars-chip--success">v{entry.version} active</span>
      )}
      {entry.latestVersion > entry.version ? <span className="run-chip vars-chip--warning">Latest v{entry.latestVersion}</span> : null}
    </span>
  );
}

/** One entry: its value, its versions and its details. Revealed values stay in the page's state only. */
export function EntryPane({ entry, backLabel, full, onToggleFull, onClose, revealed, revealing, onReveal, onHide, onDirty, onRefresh, onDeleted }: Props) {
  const secret = entry.visibility === "secret";
  const name = lastSegment(entry.key);
  const folder = parentFolder(entry.key);
  const shown = secret ? revealed : entry.value;
  // The server counts the lines of the active value, so the count is the same before and after a reveal.
  const lines = entry.lines;
  const multi = lines !== null && lines > 1;

  const [tab, setTab] = useState<Tab>("value");
  const [edit, setEdit] = useState<Edit | null>(null);
  const [note, setNote] = useState("");
  const [opening, setOpening] = useState(false);
  const [notice, setNotice] = useState<string | null>(null);
  const [description, setDescription] = useState<string | null>(null);
  const [deleting, setDeleting] = useState(false);
  const save = useSetVariable();
  const stage = useStageVariable();
  const describe = useSetVariable();
  const remove = useDeleteVariable();

  const changed = edit ? (edit.base === undefined ? edit.text !== "" : edit.text !== edit.base) : false;
  const stale = edit !== null && edit.version !== entry.version;
  const dirty = changed || (description !== null && description !== (entry.description ?? ""));
  useEffect(() => onDirty(dirty), [dirty, onDirty]);
  useEffect(() => () => onDirty(false), [onDirty]);

  function stopEditing() {
    setEdit(null);
    setNote("");
    save.reset();
    stage.reset();
  }

  async function modify() {
    setNotice(null);
    if (!secret) {
      setEdit({ mode: "modify", base: entry.value ?? "", text: entry.value ?? "", version: entry.version });
      return;
    }
    let value = revealed;
    if (typeof value !== "string") {
      setOpening(true);
      value = await onReveal();
      setOpening(false);
    }
    if (typeof value === "string") setEdit({ mode: "modify", base: value, text: value, version: entry.version });
  }

  function saved() {
    stopEditing();
    onHide();
  }

  function saveActive() {
    if (!edit || !changed || edit.text === "" || save.isPending || stale) return;
    save.mutate(
      {
        key: entry.key,
        value: edit.text,
        visibility: entry.visibility,
        description: entry.description,
        note: note.trim() || null,
        expectedCurrentVersion: edit.version,
      },
      { onSuccess: saved },
    );
  }

  function saveCandidate() {
    if (!edit || !changed || edit.text === "" || stage.isPending) return;
    stage.mutate(
      { key: entry.key, value: edit.text, description: entry.description },
      {
        onSuccess: (body) => {
          saved();
          setNotice(`Saved as v${body.version}, a candidate. Make it active from Versions.`);
        },
      },
    );
  }

  const tools = (
    <span className="vars-tools">
      {multi && typeof shown === "string" ? (
        <>
          {secret ? (
            <button type="button" className="button button--small" onClick={onHide}>
              <EyeOff size={14} aria-hidden="true" /> Hide
            </button>
          ) : null}
          <CopyButton text={shown} label="Copy" />
          <button type="button" className="button button--small" onClick={() => download(name, shown)}>
            <Download size={14} aria-hidden="true" /> Download
          </button>
          <span className="vars-tools__sep" aria-hidden="true" />
        </>
      ) : null}
      {!secret || entry.lines !== null ? (
        <button type="button" className="button button--small" onClick={() => void modify()} disabled={opening || revealing}>
          <Pencil size={14} aria-hidden="true" /> Modify
        </button>
      ) : null}
      {secret ? (
        <button
          type="button"
          className="button button--small"
          onClick={() => {
            setNotice(null);
            setEdit({ mode: "update", text: "", version: entry.version });
          }}
        >
          <Replace size={14} aria-hidden="true" /> Update
        </button>
      ) : null}
    </span>
  );

  function concealedValue(): ReactNode {
    if (entry.lines === null) return <div className="vars-value vars-value--quiet">No usable version.</div>;
    const reveal = (
      <button type="button" className="button button--small" onClick={() => void onReveal()} disabled={revealing}>
        <Eye size={14} aria-hidden="true" /> Reveal
      </button>
    );
    if (multi) {
      return (
        <div className="vars-concealed">
          <span className="muted">Concealed. {plural(entry.lines, "line", "lines")}.</span>
          {reveal}
        </div>
      );
    }
    return (
      <div className="vars-value">
        <span className="muted">Concealed</span>
        {reveal}
      </div>
    );
  }

  function value(): ReactNode {
    if (secret && shown === undefined) return concealedValue();
    if (shown === null || shown === undefined) {
      return (
        <div className="vars-value vars-value--quiet">
          <span>No usable version.</span>
          {secret ? (
            <button type="button" className="icon-button" onClick={onHide} aria-label={`Hide ${entry.key}`} title="Hide">
              <EyeOff size={14} aria-hidden="true" />
            </button>
          ) : null}
        </div>
      );
    }
    if (multi) return <ValueViewer value={shown} label={`Value of ${entry.key}`} />;
    return (
      <div className="vars-value">
        <code className="vars-value__text">{shown}</code>
        <CopyButton text={shown} label={`Copy the value of ${entry.key}`} compact />
        {secret ? (
          <button type="button" className="icon-button" onClick={onHide} aria-label={`Hide ${entry.key}`} title="Hide">
            <EyeOff size={14} aria-hidden="true" />
          </button>
        ) : null}
      </div>
    );
  }

  function editor(current: Edit) {
    const error = save.error || stage.error;
    const pending = save.isPending || stage.isPending;
    const blocked = !changed || current.text === "" || pending;
    const conflict = error instanceof ApiError && error.status === 409 ? error.message : null;
    return (
      <div className="vars-edit">
        <p className="muted small">
          {current.mode === "modify" ? (
            <>
              <strong className="vars-strong">Modify.</strong> Starts from v{current.version}
              {stale ? "" : ", the active value"}. Change the lines you need.
            </>
          ) : (
            <>
              <strong className="vars-strong">Update.</strong> Replaces the whole value. Paste it, or drop a file on the box.
            </>
          )}
        </p>
        <ValueEditor
          label="Value"
          value={current.text}
          onChange={(text) => setEdit({ ...current, text })}
          placeholder={current.mode === "update" ? "Paste the new value" : undefined}
          minRows={multi ? 10 : 3}
          autoFocus
          describedBy="vars-edit-format"
        />
        <FormatLine id="vars-edit-format" value={current.text} base={current.base} />
        <div className="vars-save">
          <input
            className="vars-save__note"
            aria-label="Note, optional"
            placeholder="Note, optional"
            value={note}
            onChange={(event) => setNote(event.target.value)}
            autoComplete="off"
          />
          <span className="vars-save__buttons">
            {secret ? (
              <button type="button" className="link-button small" onClick={saveCandidate} disabled={blocked}>
                Save as a candidate
              </button>
            ) : null}
            <button type="button" className="button" onClick={stopEditing} disabled={pending}>
              Cancel
            </button>
            <button type="button" className="button button--primary" onClick={saveActive} disabled={blocked || stale}>
              {save.isPending ? "Saving…" : "Save and make active"}
            </button>
          </span>
        </div>
        <p className="muted small">
          Saved as v{entry.latestVersion + 1} and active right away.
          {entry.usedBy.length > 0 ? " Guests and services that follow this key get a setup run." : null}
        </p>
        {stale ? (
          <Callout
            kind="warning"
            title={conflict ?? `v${entry.version} became active while you were editing.`}
            alert
            actions={
              <button
                type="button"
                className="button button--small"
                onClick={() => {
                  save.reset();
                  stage.reset();
                  setEdit({ ...current, version: entry.version });
                }}
              >
                Continue from v{entry.version}
              </button>
            }
          >
            Your draft started from v{current.version} and holds nothing of what v{entry.version} changed. Continue only if it should replace
            v{entry.version}.
          </Callout>
        ) : conflict !== null ? (
          <ConflictNote
            message={conflict}
            onRefresh={() => {
              save.reset();
              stage.reset();
              onRefresh();
            }}
          />
        ) : null}
        {error && conflict === null ? (
          <Callout kind="danger" title={stage.error ? "The candidate could not be saved." : "The value could not be saved."} alert>
            Try again or reload the page. Submitted values are not shown in error messages.
          </Callout>
        ) : null}
      </div>
    );
  }

  const tabs: [Tab, string][] = secret
    ? [
        ["value", "Value"],
        ["versions", "Versions"],
        ["details", "Details"],
      ]
    : [
        ["value", "Value"],
        ["details", "Details"],
      ];

  return (
    <div className="vars-entry">
      <div className="vars-pane__back">
        <button type="button" className="link-button" onClick={onClose}>
          <ArrowLeft size={14} aria-hidden="true" /> {backLabel}
        </button>
      </div>
      <div className="vars-pane__head">
        <div className="vars-pane__title">
          <div className="vars-pane__name">
            <h2 className="mono">{name}</h2>
            <CopyButton text={entry.key} label={`Copy ${entry.key}`} compact />
            <Chips entry={entry} lines={lines} />
          </div>
          <code className="vars-pane__path">{folder ? `${folder}/` : "No folder"}</code>
        </div>
        <span className="vars-pane__corner">
          <button
            type="button"
            className="icon-button vars-pane__full"
            onClick={onToggleFull}
            aria-label={full ? "Show the list again" : "Give the entry the whole width"}
            title={full ? "Show the list again" : "Give the entry the whole width"}
          >
            {full ? <Minimize2 size={15} aria-hidden="true" /> : <Maximize2 size={15} aria-hidden="true" />}
          </button>
          <button type="button" className="icon-button vars-pane__close" onClick={onClose} aria-label="Close the entry" title="Close the entry">
            <X size={16} aria-hidden="true" />
          </button>
        </span>
      </div>

      <div className="vars-tabrow">
        <div className="choice" role="group" aria-label="Section">
          {tabs.map(([id, label]) => (
            <button key={id} type="button" className="choice__item" aria-pressed={tab === id} onClick={() => setTab(id)}>
              {label}
            </button>
          ))}
        </div>
        {tab === "value" && !edit ? tools : null}
      </div>

      {tab === "value" ? (
        <div className="stack">
          {entry.revoked && !edit ? (
            <Callout kind="danger" title="The active version is revoked">
              New runs need a version that is not revoked. Make another version active from Versions, or save a new value.
            </Callout>
          ) : null}
          {notice ? (
            <Callout kind="success" alert>
              {notice}
            </Callout>
          ) : null}
          {edit ? (
            editor(edit)
          ) : (
            <>
              {value()}
              <p className="muted small">
                v{entry.version}, saved <RelativeTime value={entry.updatedAt} />.
                {entry.latestVersion > entry.version ? ` v${entry.latestVersion} is newer and not active. See Versions.` : null}
              </p>
            </>
          )}
        </div>
      ) : null}

      {tab === "versions" && secret ? <VersionsTab entry={entry} onRefresh={onRefresh} /> : null}

      {tab === "details" ? (
        <div className="vars-details">
          <section className="vars-details__section" aria-labelledby="vars-description">
            <h3 id="vars-description" className="vars-label">
              Description
            </h3>
            {description === null ? (
              <div className="vars-details__line">
                <span className={entry.description ? undefined : "muted"}>{entry.description ?? "No description."}</span>
                <button
                  type="button"
                  className="link-button small"
                  onClick={() => setDescription(entry.description ?? "")}
                  aria-label="Edit the description"
                >
                  Edit
                </button>
              </div>
            ) : (
              <form
                className="vars-details__edit"
                onSubmit={(event) => {
                  event.preventDefault();
                  describe.mutate(
                    { key: entry.key, value: null, visibility: null, description: description.trim() || null },
                    { onSuccess: () => setDescription(null) },
                  );
                }}
              >
                <input aria-label="Description" value={description} onChange={(event) => setDescription(event.target.value)} autoFocus />
                <button
                  type="button"
                  className="button button--small"
                  onClick={() => {
                    setDescription(null);
                    describe.reset();
                  }}
                >
                  Cancel
                </button>
                <button type="submit" className="button button--small button--primary" disabled={describe.isPending}>
                  {describe.isPending ? "Saving…" : "Save"}
                </button>
              </form>
            )}
            {describe.error ? (
              <Callout kind="danger" title="The description could not be saved." alert>
                Try again or reload the page.
              </Callout>
            ) : null}
          </section>
          <section className="vars-details__section" aria-labelledby="vars-used-by">
            <h3 id="vars-used-by" className="vars-label">
              Used by
            </h3>
            <UsedBy usedBy={entry.usedBy} />
            {entry.usedBy.length > 0 ? <p className="muted small">A new active version starts one setup run for each of these.</p> : null}
          </section>
          <div className="vars-details__delete">
            <button
              type="button"
              className="button button--small button--danger-outline"
              onClick={() => setDeleting(true)}
              aria-label={`Delete ${entry.key}`}
            >
              <Trash2 size={14} aria-hidden="true" /> Delete
            </button>
            <span className="muted small">Removes the key and all its versions.</span>
          </div>
        </div>
      ) : null}

      <ConfirmDialog
        open={deleting}
        title={`Delete ${entry.key}?`}
        confirmLabel="Delete"
        danger
        pending={remove.isPending}
        error={remove.error}
        onClose={() => {
          setDeleting(false);
          remove.reset();
        }}
        onConfirm={() =>
          remove.mutate(entry.key, {
            onSuccess: () => {
              setDeleting(false);
              onDeleted();
            },
          })
        }
      >
        <p>
          Every version of its value is removed, and the key leaves this list. What each run was given stays on record, and if the key is set
          again, its versions continue after the last one.
        </p>
        {entry.usedBy.length > 0 ? (
          <>
            <p>It is still used by these, so it can be deleted only once they no longer use it:</p>
            <UsedBy usedBy={entry.usedBy} />
          </>
        ) : (
          <p className="muted small">It is refused while a definition, an open change or a run that has not finished still uses it.</p>
        )}
      </ConfirmDialog>
    </div>
  );
}
