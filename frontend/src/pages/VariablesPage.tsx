import { Eye, EyeOff, FolderTree, KeyRound, List, Plus } from "lucide-react";
import { Fragment, useState } from "react";
import { Link, useSearchParams } from "react-router";
import { useDeleteVariable, useRevealVariables, useVariables } from "../api/queries";
import type { Variable } from "../api/types";
import { Callout } from "../components/Callout";
import { Card } from "../components/Card";
import { ConfirmDialog } from "../components/ConfirmDialog";
import { PageHeader } from "../components/PageHeader";
import { EmptyState, ErrorMessage, PageSkeleton } from "../components/States";
import { Table } from "../components/Table";
import { EntriesTable, UsedBy } from "./variables/EntriesTable";
import { folderHref, foldersOn, lastSegment, listFolder, matchesSearch } from "./variables/folders";
import { VariableDialog } from "./variables/VariableDialog";
import { VariableVersionsDialog } from "./variables/VariableVersionsDialog";

type Editing = { existing: Variable | null; presetKey?: string } | null;
type View = "folders" | "all";
/** A revealed value with the version it belongs to, so a value replaced since is concealed again. */
type Revealed = Record<string, { version: number; value: string | null }>;

const viewStorageKey = "octopushy.variables.view";

function storedView(): View {
  try {
    return window.localStorage.getItem(viewStorageKey) === "all" ? "all" : "folders";
  } catch {
    return "folders";
  }
}

function storeView(view: View) {
  try {
    window.localStorage.setItem(viewStorageKey, view);
  } catch {
    // The choice then lasts only until the page is left.
  }
}

/** Phrasing content, because a card title is a heading. */
function FolderPath({ path }: { path: string }) {
  if (!path) return "Entries";
  return (
    <span role="navigation" aria-label="Folder" className="folder-path">
      <Link to={folderHref("")}>Entries</Link>
      {foldersOn(path).map((folder) => (
        <Fragment key={folder}>
          <span aria-hidden="true">/</span>
          {folder === path ? <span aria-current="page">{lastSegment(folder)}</span> : <Link to={folderHref(folder)}>{lastSegment(folder)}</Link>}
        </Fragment>
      ))}
    </span>
  );
}

export function VariablesPage() {
  const { data, error, isPending } = useVariables();
  const remove = useDeleteVariable();
  const [params, setParams] = useSearchParams();
  const [preferredView, setPreferredView] = useState(storedView);
  // Kept out of the URL: a value pasted here by mistake must not reach the history.
  const [search, setSearch] = useState("");
  const [editing, setEditing] = useState<Editing>(null);
  const [deleting, setDeleting] = useState<Variable | null>(null);
  const [managing, setManaging] = useState<string | null>(null);
  // Held only in this page's state: leaving the page conceals every value again.
  const [revealed, setRevealed] = useState<Revealed>({});
  const [revealing, setRevealing] = useState<ReadonlySet<string>>(new Set());
  const reveal = useRevealVariables();
  const [revealFailed, setRevealFailed] = useState(false);
  const entries = data?.entries.filter((entry) => !entry.key.startsWith("system/")) ?? [];
  const missing = data?.missing.filter((entry) => !entry.key.startsWith("system/")) ?? [];
  const managed = entries.find((entry) => entry.key === managing);

  const folderParam = params.get("folder");
  const view = folderParam === null ? preferredView : "folders";
  const listing = listFolder(entries, folderParam ?? "");
  const query = search.trim().toLowerCase();
  const results = query ? entries.filter((entry) => matchesSearch(entry, query)) : null;
  const browsing = view === "folders" && !results;

  function chooseView(next: View) {
    storeView(next);
    setPreferredView(next);
    if (next === "all") setParams({});
  }

  const shown = browsing ? listing.entries : (results ?? entries);
  const shownSecrets = shown.filter((entry) => entry.visibility === "secret");
  const revealedValue = (entry: Variable) => {
    const held = revealed[entry.key];
    return held && held.version === entry.version ? held.value : undefined;
  };
  const allRevealed = shownSecrets.length > 0 && shownSecrets.every((entry) => revealedValue(entry) !== undefined);

  // Each request settles on its own: mutate's per-call callbacks run only for the latest of overlapping calls.
  async function revealEntries(targets: Variable[]) {
    const keys = targets.filter((entry) => revealedValue(entry) === undefined && !revealing.has(entry.key)).map((entry) => entry.key);
    if (keys.length === 0) return;
    const versions = new Map(targets.map((entry) => [entry.key, entry.version]));
    setRevealing((current) => new Set([...current, ...keys]));
    try {
      const body = await reveal.mutateAsync(keys);
      setRevealFailed(false);
      setRevealed((current) => {
        const next = { ...current };
        for (const { key, value } of body.values) next[key] = { version: versions.get(key) ?? 0, value: value ?? null };
        return next;
      });
    } catch {
      setRevealFailed(true);
    } finally {
      setRevealing((current) => {
        const next = new Set(current);
        for (const key of keys) next.delete(key);
        return next;
      });
    }
  }

  function hideEntries(targets: Variable[]) {
    setRevealed((current) => {
      const next = { ...current };
      for (const entry of targets) delete next[entry.key];
      return next;
    });
  }

  const setButton = (
    <button type="button" className="button button--primary" onClick={() => setEditing({ existing: null })}>
      <Plus size={15} aria-hidden="true" />
      Set a variable
    </button>
  );

  const tools = (
    <div className="variable-tools">
      <input
        type="search"
        placeholder="Search all keys"
        aria-label="Search all keys"
        value={search}
        onChange={(e) => setSearch(e.target.value)}
      />
      {shownSecrets.length > 0 ? (
        allRevealed ? (
          <button type="button" className="button button--small" onClick={() => hideEntries(shownSecrets)}>
            <EyeOff size={14} aria-hidden="true" /> Hide all
          </button>
        ) : (
          <button
            type="button"
            className="button button--small"
            onClick={() => void revealEntries(shownSecrets)}
            disabled={shownSecrets.some((entry) => revealing.has(entry.key))}
            aria-label={shownSecrets.length === 1 ? "Reveal the 1 secret listed" : `Reveal all ${shownSecrets.length} secrets listed`}
          >
            <Eye size={14} aria-hidden="true" /> Reveal all
          </button>
        )
      ) : null}
      <nav className="segmented segmented--inline" aria-label="View">
        <button type="button" className="segmented__item" aria-current={view === "folders" ? "page" : undefined} onClick={() => chooseView("folders")}>
          <FolderTree size={14} aria-hidden="true" /> Folders
        </button>
        <button type="button" className="segmented__item" aria-current={view === "all" ? "page" : undefined} onClick={() => chooseView("all")}>
          <List size={14} aria-hidden="true" /> All keys
        </button>
      </nav>
    </div>
  );

  const rowActions = {
    onUpdate: (existing: Variable) => setEditing({ existing }),
    onVersions: (entry: Variable) => setManaging(entry.key),
    onDelete: setDeleting,
    revealedValue,
    revealing,
    onReveal: (entry: Variable) => void revealEntries([entry]),
    onHide: (entry: Variable) => hideEntries([entry]),
  };

  return (
    <>
      <PageHeader
        title="Variables"
        meta="The variable store: values kept outside Git, referenced from definitions by key."
        actions={setButton}
      />
      {isPending ? (
        <PageSkeleton rows={2} />
      ) : !data ? (
        <ErrorMessage error={error} title="The variables could not be loaded" />
      ) : (
        <div className="stack stack--large">
          {missing.length > 0 ? (
            <Card title="Referenced but not set" meta={missing.length} className="card--warning" flush>
              <Table label="Referenced keys that are not set">
                <thead>
                  <tr>
                    <th scope="col">Key</th>
                    <th scope="col">Used by</th>
                    <th scope="col">
                      <span className="visually-hidden">Actions</span>
                    </th>
                  </tr>
                </thead>
                <tbody>
                  {missing.map((m) => (
                    <tr key={m.key}>
                      <th scope="row" className="mono">
                        {m.key}
                      </th>
                      <td>
                        <UsedBy usedBy={m.usedBy} />
                      </td>
                      <td className="cell-actions">
                        <button type="button" className="button button--small button--primary" onClick={() => setEditing({ existing: null, presetKey: m.key })}>
                          Set
                        </button>
                      </td>
                    </tr>
                  ))}
                </tbody>
              </Table>
            </Card>
          ) : null}
          {revealFailed ? (
            <Callout kind="danger" title="The values could not be revealed." alert>
              Try again, or reload the page if the store changed.
            </Callout>
          ) : null}
          <Card
            title={browsing ? <FolderPath path={listing.path} /> : "Entries"}
            meta={results ? `${results.length} of ${entries.length}` : (browsing ? listing.total : entries.length) || undefined}
            actions={entries.length > 0 ? tools : undefined}
            flush
          >
            {entries.length === 0 ? (
              <EmptyState icon={KeyRound} title="The store is empty" actions={setButton}>
                Set the Proxmox tokens and state-backend credentials your definitions reference.
              </EmptyState>
            ) : results?.length === 0 ? (
              <EmptyState compact title="No key matches this search">
                Searches keys, descriptions, and what uses them, never values.
              </EmptyState>
            ) : browsing ? (
              <EntriesTable folders={listing.folders} entries={listing.entries} byName {...rowActions} />
            ) : (
              <EntriesTable entries={results ?? entries} {...rowActions} />
            )}
          </Card>
        </div>
      )}
      {editing ? (
        <VariableDialog
          existing={editing.existing}
          presetKey={editing.presetKey}
          folder={browsing ? listing.path : undefined}
          onClose={() => setEditing(null)}
        />
      ) : null}
      {managed ? <VariableVersionsDialog existing={managed} onClose={() => setManaging(null)} /> : null}
      <ConfirmDialog
        open={deleting !== null}
        title={`Delete ${deleting?.key ?? ""}?`}
        confirmLabel="Delete"
        danger
        pending={remove.isPending}
        error={remove.error}
        onClose={() => {
          setDeleting(null);
          remove.reset();
        }}
        onConfirm={() => deleting && remove.mutate(deleting.key, { onSuccess: () => setDeleting(null) })}
      >
        <p>
          Every version of its value is removed, and the key leaves this list. What each run was given stays on record, and if
          the key is set again, its versions continue after the last one.
        </p>
        {deleting && deleting.usedBy.length > 0 ? (
          <>
            <p>It is still used by these, so it can be deleted only once they no longer use it:</p>
            <UsedBy usedBy={deleting.usedBy} />
          </>
        ) : (
          <p className="muted small">It is refused while a definition, an open change or a run that has not finished still uses it.</p>
        )}
      </ConfirmDialog>
    </>
  );
}
