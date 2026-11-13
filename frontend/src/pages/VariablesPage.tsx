import { ChevronRight, Eye, EyeOff, FolderTree as FolderTreeIcon, KeyRound, List, Plus, Search, TriangleAlert, X } from "lucide-react";
import { Fragment, useCallback, useEffect, useRef, useState, type KeyboardEvent } from "react";
import { useNavigate, useSearchParams } from "react-router";
import { useRevealVariables, useVariables } from "../api/queries";
import type { Variable } from "../api/types";
import { Callout } from "../components/Callout";
import { ConfirmDialog } from "../components/ConfirmDialog";
import { PageHeader } from "../components/PageHeader";
import { EmptyState, ErrorMessage, PageSkeleton } from "../components/States";
import { EntryList } from "./variables/EntryList";
import { EntryPane } from "./variables/EntryPane";
import { FolderTree } from "./variables/FolderTree";
import { GuardLink } from "./variables/GuardLink";
import { LeaveContext } from "./variables/leave";
import { NewEntryForm } from "./variables/NewEntryForm";
import { entryHref, folderHref, folderTree, foldersOn, lastSegment, listFolder, matchesSearch, parentFolder } from "./variables/folders";

type View = "folders" | "all";
/** A revealed value with the version it belongs to, so a value replaced since is concealed again. */
type Revealed = Record<string, { version: number; value: string | null }>;
/** A new entry being set, valid while the address stays where it was opened. */
type Creating = { initialKey: string; folder: string | null };

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

function FolderPath({ path }: { path: string }) {
  if (!path) return <span className="folder-path">Entries</span>;
  return (
    <span role="navigation" aria-label="Folder" className="folder-path">
      <GuardLink to={folderHref("")}>Entries</GuardLink>
      {foldersOn(path).map((folder) => (
        <Fragment key={folder}>
          <span aria-hidden="true">/</span>
          {folder === path ? <span aria-current="page">{lastSegment(folder)}</span> : <GuardLink to={folderHref(folder)}>{lastSegment(folder)}</GuardLink>}
        </Fragment>
      ))}
    </span>
  );
}

function Missing({ missing, onSet }: { missing: { key: string; usedBy: string[] }[]; onSet: (key: string) => void }) {
  const [open, setOpen] = useState(missing.length <= 3);
  return (
    <section className="vars-missing" aria-label="Referenced keys that are not set">
      <details open={open} onToggle={(event) => setOpen(event.currentTarget.open)}>
        <summary className="vars-missing__summary">
          <TriangleAlert size={14} aria-hidden="true" />
          <span>{missing.length === 1 ? "1 key is referenced but not set" : `${missing.length} keys are referenced but not set`}</span>
          <ChevronRight size={14} aria-hidden="true" className="vars-missing__chevron" />
        </summary>
        <ul className="vars-missing__list">
          {missing.map((m) => (
            <li key={m.key}>
              <code className="vars-missing__key">{m.key}</code>
              <span className="vars-missing__used">{m.usedBy.length ? `Used by ${m.usedBy.join(", ")}` : "Not referenced"}</span>
              <button type="button" className="button button--small" onClick={() => onSet(m.key)} aria-label={`Set ${m.key}`}>
                Set
              </button>
            </li>
          ))}
        </ul>
      </details>
    </section>
  );
}

export function VariablesPage() {
  const { data, error, isPending, refetch } = useVariables();
  const [params, setParams] = useSearchParams();
  const navigate = useNavigate();
  const [preferredView, setPreferredView] = useState(storedView);
  // Kept out of the URL: a value pasted here by mistake must not reach the history.
  const [search, setSearch] = useState("");
  // Held only in this page's state: leaving the page conceals every value again.
  const [revealed, setRevealed] = useState<Revealed>({});
  const [revealing, setRevealing] = useState<ReadonlySet<string>>(new Set());
  const reveal = useRevealVariables();
  const [revealFailed, setRevealFailed] = useState(false);
  const [creating, setCreating] = useState<Creating | null>(null);
  const [full, setFull] = useState(false);
  const [drawer, setDrawer] = useState(false);
  const [pendingLeave, setPendingLeave] = useState<{ run: () => void } | null>(null);

  const dirty = useRef(false);
  const listRef = useRef<HTMLElement>(null);
  const paneRef = useRef<HTMLElement>(null);
  const drawerRef = useRef<HTMLElement>(null);
  const foldersButton = useRef<HTMLButtonElement>(null);
  const focusPane = useRef(false);
  const returnFocus = useRef<string | null>(null);

  const entries = data?.entries.filter((entry) => !entry.key.startsWith("system/")) ?? [];
  const missing = data?.missing.filter((entry) => !entry.key.startsWith("system/")) ?? [];
  const keys = new Set(entries.map((entry) => entry.key));

  const folderParam = params.get("folder");
  const keyParam = params.get("key");
  const view = folderParam === null ? preferredView : "folders";
  const listing = listFolder(entries, folderParam ?? "");
  const query = search.trim().toLowerCase();
  const results = query ? entries.filter((entry) => matchesSearch(entry, query)) : null;
  const flat = view === "all" || results !== null;
  const shown = flat ? (results ?? entries) : listing.entries;
  const shownSecrets = shown.filter((entry) => entry.visibility === "secret");

  const newEntry = creating && creating.folder === folderParam && keyParam === null ? creating : null;
  const selected = newEntry ? undefined : entries.find((entry) => entry.key === keyParam);
  const open = Boolean(newEntry || selected);
  const backLabel = flat ? (query ? "Search results" : "All keys") : listing.path ? lastSegment(listing.path) : "Entries";

  const onDirty = useCallback((value: boolean) => {
    dirty.current = value;
  }, []);

  const leave = useCallback((step: () => void) => {
    if (dirty.current) setPendingLeave({ run: step });
    else step();
  }, []);

  // Reloading or closing the tab asks first, and so does a link outside this page, such as the sidebar. Links inside go through GuardLink.
  useEffect(() => {
    const warn = (event: BeforeUnloadEvent) => {
      if (dirty.current) event.preventDefault();
    };
    const guard = (event: MouseEvent) => {
      if (!dirty.current || event.button !== 0 || event.metaKey || event.ctrlKey || event.shiftKey || event.altKey) return;
      const link = event.target instanceof Element ? event.target.closest("a[href]") : null;
      if (!(link instanceof HTMLAnchorElement) || link.closest(".vars-page")) return;
      if (link.origin !== window.location.origin || link.target === "_blank" || link.hasAttribute("download")) return;
      event.preventDefault();
      leave(() => void navigate(`${link.pathname}${link.search}${link.hash}`));
    };
    window.addEventListener("beforeunload", warn);
    document.addEventListener("click", guard, true);
    return () => {
      window.removeEventListener("beforeunload", warn);
      document.removeEventListener("click", guard, true);
    };
  }, [leave, navigate]);

  /** The list is hidden only on a narrow container with an entry open: one pane at a time. */
  function listHidden() {
    return listRef.current !== null && getComputedStyle(listRef.current).display === "none";
  }

  function setKey(key: string | null) {
    setParams((current) => {
      const next = new URLSearchParams(current);
      if (key === null) next.delete("key");
      else next.set("key", key);
      return next;
    });
  }

  function backToList() {
    returnFocus.current = selected?.key ?? null;
    setCreating(null);
    setFull(false);
    setKey(null);
  }

  useEffect(() => {
    if (!focusPane.current || !selected) return;
    focusPane.current = false;
    if (listHidden()) paneRef.current?.focus();
  }, [selected]);

  useEffect(() => {
    const key = returnFocus.current;
    if (open || key === null) return;
    returnFocus.current = null;
    const row = Array.from(listRef.current?.querySelectorAll<HTMLElement>("[data-key]") ?? []).find((el) => el.dataset.key === key);
    row?.focus();
  }, [open]);

  useEffect(() => {
    if (drawer) drawerRef.current?.querySelector<HTMLElement>("[aria-current], a")?.focus();
  }, [drawer]);

  const revealedValue = (entry: Variable) => {
    const held = revealed[entry.key];
    return held && held.version === entry.version ? held.value : undefined;
  };
  const allRevealed = shownSecrets.length > 0 && shownSecrets.every((entry) => revealedValue(entry) !== undefined);

  // Each request settles on its own: mutate's per-call callbacks run only for the latest of overlapping calls.
  async function revealEntries(targets: Variable[]): Promise<Map<string, string | null> | undefined> {
    const keys = targets.filter((entry) => revealedValue(entry) === undefined && !revealing.has(entry.key)).map((entry) => entry.key);
    if (keys.length === 0) return undefined;
    const versions = new Map(targets.map((entry) => [entry.key, entry.version]));
    setRevealing((current) => new Set([...current, ...keys]));
    try {
      const body = await reveal.mutateAsync(keys);
      setRevealFailed(false);
      const values = new Map(body.values.map(({ key, value }) => [key, value ?? null]));
      setRevealed((current) => {
        const next = { ...current };
        for (const [key, value] of values) next[key] = { version: versions.get(key) ?? 0, value };
        return next;
      });
      return values;
    } catch {
      setRevealFailed(true);
      return undefined;
    } finally {
      setRevealing((current) => {
        const next = new Set(current);
        for (const key of keys) next.delete(key);
        return next;
      });
    }
  }

  async function revealOne(entry: Variable) {
    const held = revealedValue(entry);
    if (held !== undefined) return held;
    return (await revealEntries([entry]))?.get(entry.key);
  }

  function hideEntries(targets: Variable[]) {
    setRevealed((current) => {
      const next = { ...current };
      for (const entry of targets) delete next[entry.key];
      return next;
    });
  }

  function startNew(initialKey: string) {
    leave(() => {
      setDrawer(false);
      setFull(false);
      // The key leaves the address first, so the form is valid at the address it opens at.
      setCreating({ initialKey, folder: folderParam });
      setKey(null);
    });
  }

  function chooseView(next: View) {
    const step = () => {
      storeView(next);
      setPreferredView(next);
      setFull(false);
      setDrawer(false);
      if (next === "all") {
        setParams((current) => {
          const kept = new URLSearchParams(current);
          kept.delete("folder");
          return kept;
        });
      }
    };
    if (open && listHidden()) {
      leave(() => {
        step();
        backToList();
      });
    }
    else step();
  }

  function changeSearch(value: string) {
    setSearch(value);
    setFull(false);
    if (open && listHidden() && !dirty.current) backToList();
  }

  function toggleDrawer() {
    if (drawer) {
      setDrawer(false);
      return;
    }
    if (open && listHidden()) {
      leave(() => {
        backToList();
        setDrawer(true);
      });
    }
    else setDrawer(true);
  }

  function closeDrawer() {
    setDrawer(false);
    foldersButton.current?.focus();
  }

  function onBoxKey(event: KeyboardEvent) {
    if (event.key === "Escape" && drawer) {
      event.stopPropagation();
      closeDrawer();
    }
  }

  const hrefFor = (key: string) => entryHref(key, flat ? folderParam : listing.path);

  const setButton = (
    <button type="button" className="button button--primary" onClick={() => startNew(flat || !listing.path ? "" : `${listing.path}/`)}>
      <Plus size={15} aria-hidden="true" />
      Set a variable
    </button>
  );

  const toolbar = (
    <div className="vars-toolbar">
      <div className="vars-toolbar__where">
        {!flat ? (
          <button
            ref={foldersButton}
            type="button"
            className="button button--small vars-folders-button"
            aria-expanded={drawer}
            aria-controls="vars-tree"
            aria-label="Show the folders"
            onClick={toggleDrawer}
          >
            <FolderTreeIcon size={14} aria-hidden="true" /> Folders
          </button>
        ) : null}
        {flat ? (
          <span className="vars-toolbar__flat">
            <strong>{query ? "Search results" : "All keys"}</strong>
            <span className="muted">{results ? `${results.length} of ${entries.length}` : `${entries.length} keys`}</span>
          </span>
        ) : (
          <FolderPath path={listing.path} />
        )}
      </div>
      <div className="vars-toolbar__tools">
        <label className="vars-search">
          <Search size={14} aria-hidden="true" />
          <input type="search" placeholder="Search all keys" aria-label="Search all keys" value={search} onChange={(e) => changeSearch(e.target.value)} />
        </label>
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
            <FolderTreeIcon size={14} aria-hidden="true" /> Folders
          </button>
          <button type="button" className="segmented__item" aria-current={view === "all" ? "page" : undefined} onClick={() => chooseView("all")}>
            <List size={14} aria-hidden="true" /> All keys
          </button>
        </nav>
      </div>
    </div>
  );

  function pane() {
    if (newEntry) {
      return (
        <NewEntryForm
          initialKey={newEntry.initialKey}
          backLabel={backLabel}
          existingKeys={keys}
          onOpen={(key) =>
            leave(() => {
              setCreating(null);
              void navigate(hrefFor(key));
            })
          }
          onSaved={(key) => {
            setCreating(null);
            void navigate(entryHref(key, flat ? folderParam : parentFolder(key)));
          }}
          onCancel={() => leave(backToList)}
          onDirty={onDirty}
        />
      );
    }
    if (selected) {
      return (
        <EntryPane
          key={selected.key}
          entry={selected}
          backLabel={backLabel}
          full={full}
          onToggleFull={() => setFull((now) => !now)}
          onClose={() => leave(backToList)}
          revealed={revealedValue(selected)}
          revealing={revealing.has(selected.key)}
          onReveal={() => revealOne(selected)}
          onHide={() => hideEntries([selected])}
          onDirty={onDirty}
          onRefresh={() => void refetch()}
          onDeleted={() => {
            hideEntries([selected]);
            setKey(null);
          }}
        />
      );
    }
    return (
      <EmptyState compact icon={KeyRound} title="No entry open">
        Pick a key to see its value, versions and details.
      </EmptyState>
    );
  }

  return (
    <LeaveContext value={leave}>
      <div className="vars-page">
        <PageHeader title="Variables" meta="The variable store: values kept outside Git, referenced from definitions by key." actions={setButton} />
        {isPending ? (
          <PageSkeleton rows={2} />
        ) : !data ? (
          <ErrorMessage error={error} title="The variables could not be loaded" />
        ) : (
          <>
            {missing.length > 0 ? <Missing missing={missing} onSet={startNew} /> : null}
            {revealFailed ? (
              <Callout kind="danger" title="The values could not be revealed." alert>
                Try again, or reload the page if the store changed.
              </Callout>
            ) : null}
            {entries.length === 0 && !newEntry ? (
              <EmptyState icon={KeyRound} title="The store is empty" actions={setButton}>
                Set the Proxmox tokens and state-backend credentials your definitions reference.
              </EmptyState>
            ) : (
              <div className="vars" data-open={open} data-full={full} data-flat={flat} data-drawer={drawer && !flat}>
                {toolbar}
                <div className="vars-box" onKeyDown={onBoxKey}>
                  {!flat ? (
                    <nav id="vars-tree" className="vars-tree" aria-label="Folders" ref={drawerRef}>
                      <div className="vars-tree__head">
                        <span className="vars-label">Folders</span>
                        <button type="button" className="icon-button" onClick={closeDrawer} aria-label="Close the folders" title="Close the folders">
                          <X size={15} aria-hidden="true" />
                        </button>
                      </div>
                      <FolderTree tree={folderTree(entries)} total={entries.length} current={listing.path} onFollow={() => setDrawer(false)} />
                    </nav>
                  ) : null}
                  <section className="vars-list" aria-label="Variable store entries" ref={listRef}>
                    {results?.length === 0 ? (
                      <EmptyState compact title="No key matches this search">
                        Searches keys, descriptions, and what uses them, never values.
                      </EmptyState>
                    ) : (
                      <EntryList
                        folders={flat ? [] : listing.folders}
                        entries={shown}
                        flat={flat}
                        selected={selected?.key ?? null}
                        hrefFor={hrefFor}
                        revealedValue={revealedValue}
                        onPick={() => {
                          focusPane.current = true;
                          setCreating(null);
                        }}
                      />
                    )}
                  </section>
                  <section className="vars-pane" aria-label={newEntry ? "New entry" : "Selected entry"} ref={paneRef} tabIndex={-1}>
                    {pane()}
                  </section>
                </div>
              </div>
            )}
          </>
        )}
        <ConfirmDialog
          open={pendingLeave !== null}
          title="Drop the changes?"
          confirmLabel="Drop them"
          danger
          onClose={() => setPendingLeave(null)}
          onConfirm={() => {
            const step = pendingLeave?.run;
            setPendingLeave(null);
            dirty.current = false;
            step?.();
          }}
        >
          <p>The value you are editing is not saved yet. Leaving it drops your changes.</p>
        </ConfirmDialog>
      </div>
    </LeaveContext>
  );
}
