import { ChevronDown, Search, Shield } from "lucide-react";
import { useEffect, useId, useRef, useState } from "react";
import { usePurgeVariables, useSetPurgeExclusion } from "../../api/queries";
import type { Variable } from "../../api/types";
import { Dialog } from "../../components/Dialog";
import { RelativeTime } from "../../components/RelativeTime";
import { ErrorMessage } from "../../components/States";

/** Up to the node, as in `proxmox/ziost`; a shorter key groups under its folder, or under none. */
function nodeOf(key: string): { node: string; rest: string } {
  const segments = key.split("/");
  const depth = Math.min(2, segments.length - 1);
  return { node: segments.slice(0, depth).join("/"), rest: segments.slice(depth).join("/") };
}

function Key({ path }: { path: string }) {
  const cut = path.lastIndexOf("/") + 1;
  return (
    <code className="purge-key">
      <span className="purge-key__dir">{path.slice(0, cut)}</span>
      {path.slice(cut)}
    </code>
  );
}

/** Actions on the whole store. Purge deletes the secrets nothing uses; the server checks each one again before it goes. */
export function OptionsMenu({ entries }: { entries: Variable[] }) {
  const purge = usePurgeVariables();
  const exclusion = useSetPurgeExclusion();
  const [open, setOpen] = useState(false);
  // The keys as the dialog opened with them: a refetch while it is open must not add one the operator did not see.
  const [candidates, setCandidates] = useState<string[] | null>(null);
  const [picked, setPicked] = useState<Set<string>>(new Set());
  const [query, setQuery] = useState("");
  const ref = useRef<HTMLDivElement>(null);
  const menuId = useId();
  const unused = entries.filter((entry) => entry.visibility === "secret" && entry.usedBy.length === 0);
  const purgeable = unused.filter((entry) => !entry.purgeExcluded).length;

  useEffect(() => {
    if (!open) return;
    const outside = (event: PointerEvent) => {
      if (!ref.current?.contains(event.target as Node)) setOpen(false);
    };
    document.addEventListener("pointerdown", outside);
    return () => document.removeEventListener("pointerdown", outside);
  }, [open]);

  const result = purge.data;
  const byKey = new Map(entries.map((entry) => [entry.key, entry]));
  const excluded = (key: string) => byKey.get(key)?.purgeExcluded ?? false;
  const listed = candidates ?? [];
  const needle = query.trim().toLowerCase();
  const shown = listed.filter((key) => key.toLowerCase().includes(needle));
  const selectable = (keys: string[]) => keys.filter((key) => !excluded(key));
  const chosen = listed.filter((key) => picked.has(key) && !excluded(key));
  const excludedCount = listed.filter(excluded).length;
  const groups = new Map<string, string[]>();
  for (const key of shown) {
    const { node } = nodeOf(key);
    groups.set(node, [...(groups.get(node) ?? []), key]);
  }

  const close = () => {
    if (!purge.isPending) setCandidates(null);
  };
  const choose = (keys: string[], on: boolean) =>
    setPicked((current) => {
      const next = new Set(current);
      for (const key of keys) {
        if (on) next.add(key);
        else next.delete(key);
      }
      return next;
    });
  const setExcluded = (key: string, value: boolean) => {
    // An excluded key is unticked, and one included again stays unticked until the operator chooses it.
    choose([key], false);
    exclusion.mutate({ key, excluded: value });
  };
  const toggling = exclusion.isPending ? exclusion.variables?.key : undefined;

  return (
    <div
      className="options-menu"
      ref={ref}
      onKeyDown={(event) => {
        if (event.key !== "Escape" || !open) return;
        setOpen(false);
        ref.current?.querySelector<HTMLButtonElement>("button")?.focus();
      }}
    >
      <button type="button" className="button" aria-expanded={open} aria-controls={menuId} onClick={() => setOpen(!open)}>
        Options
        <ChevronDown size={15} aria-hidden="true" />
      </button>
      <div id={menuId} className="options-menu__list" hidden={!open}>
        <button
          type="button"
          className="options-menu__item options-menu__item--danger"
          disabled={unused.length === 0}
          onClick={() => {
            setOpen(false);
            purge.reset();
            exclusion.reset();
            setQuery("");
            setPicked(new Set(unused.filter((entry) => !entry.purgeExcluded).map((entry) => entry.key)));
            setCandidates(unused.map((entry) => entry.key));
          }}
        >
          <span>Purge</span>
          <span className="options-menu__hint">
            {unused.length === 0
              ? "Every secret is in use."
              : purgeable === 0
                ? "Every unused secret is excluded."
                : purgeable === 1
                  ? "Deletes the 1 secret nothing uses."
                  : `Deletes the ${purgeable} secrets nothing uses.`}
          </span>
        </button>
      </div>
      <Dialog
        open={candidates !== null && !result}
        onClose={close}
        title="Review unused secrets"
        description="No definition and no unfinished run uses these. Purged secrets lose their values and every version. Who received them stays on record."
        size="large"
        footer={
          <>
            <span className="purge-summary">
              {chosen.length} to purge · {listed.length - chosen.length - excludedCount} kept this time · {excludedCount} excluded
            </span>
            <button type="button" className="button" onClick={close} data-autofocus>
              Keep as is
            </button>
            <button
              type="button"
              className="button button--danger"
              onClick={() => purge.mutate(chosen)}
              disabled={purge.isPending || exclusion.isPending || chosen.length === 0}
            >
              {purge.isPending ? "Working…" : chosen.length === 1 ? "Purge 1 secret" : chosen.length === 0 ? "Purge" : `Purge ${chosen.length} secrets`}
            </button>
          </>
        }
      >
        <div className="stack">
          <div className="purge-toolbar">
            <label className="vars-search">
              <Search size={14} aria-hidden="true" />
              <input type="search" placeholder="Filter keys" aria-label="Filter keys" value={query} onChange={(event) => setQuery(event.target.value)} />
            </label>
            <button type="button" className="button button--small" onClick={() => choose(selectable(shown), true)}>
              {needle ? "Select shown" : "Select all"}
            </button>
            <button type="button" className="button button--small" onClick={() => choose(shown, false)}>
              {needle ? "Clear shown" : "Clear all"}
            </button>
          </div>
          {groups.size === 0 ? <p className="muted">No unused secret matches “{query}”.</p> : null}
          {[...groups].map(([node, keys]) => {
            const includable = selectable(keys);
            const ticked = includable.filter((key) => picked.has(key)).length;
            const label = node || "Top level";
            return (
              <section key={node} className="purge-group" aria-label={label}>
                <label className="purge-group__head">
                  <input
                    type="checkbox"
                    aria-label={`Purge every secret in ${label}`}
                    checked={includable.length > 0 && ticked === includable.length}
                    disabled={includable.length === 0}
                    ref={(box) => {
                      if (box) box.indeterminate = ticked > 0 && ticked < includable.length;
                    }}
                    onChange={(event) => choose(includable, event.target.checked)}
                  />
                  <code className="purge-group__name">{label}</code>
                  <span className="purge-group__count">
                    {ticked} of {includable.length} to purge{keys.length > includable.length ? ` · ${keys.length - includable.length} excluded` : ""}
                  </span>
                </label>
                <ul className="purge-group__rows">
                  {keys.map((key) => {
                    const entry = byKey.get(key);
                    const meta = entry ? (
                      <span className="purge-row__meta">
                        v{entry.version} · updated <RelativeTime value={entry.updatedAt} />
                      </span>
                    ) : null;
                    if (excluded(key)) {
                      return (
                        <li key={key} className="purge-row purge-row--excluded">
                          <Shield size={14} aria-hidden="true" className="purge-row__shield" />
                          <span>
                            <Key path={nodeOf(key).rest} /> <span className="badge badge--info">Excluded</span>
                            <br />
                            {meta}
                          </span>
                          <button
                            type="button"
                            className="button button--small"
                            aria-label={`Include ${key} in purge again`}
                            disabled={toggling === key}
                            onClick={() => setExcluded(key, false)}
                          >
                            Include again
                          </button>
                        </li>
                      );
                    }
                    return (
                      <li key={key} className={picked.has(key) ? "purge-row" : "purge-row purge-row--off"}>
                        <input
                          type="checkbox"
                          id={`${menuId}-${key}`}
                          aria-label={`Purge ${key}`}
                          checked={picked.has(key)}
                          onChange={(event) => choose([key], event.target.checked)}
                        />
                        <label htmlFor={`${menuId}-${key}`}>
                          <Key path={nodeOf(key).rest} />
                          <br />
                          {meta}
                        </label>
                        <button
                          type="button"
                          className="button button--small button--ghost purge-row__exclude"
                          aria-label={`Exclude ${key} from purge`}
                          title="Never purge this secret"
                          disabled={toggling === key}
                          onClick={() => setExcluded(key, true)}
                        >
                          <Shield size={14} aria-hidden="true" /> Exclude
                        </button>
                      </li>
                    );
                  })}
                </ul>
              </section>
            );
          })}
          <ErrorMessage error={purge.error ?? exclusion.error} />
        </div>
      </Dialog>
      <Dialog
        open={candidates !== null && Boolean(result)}
        onClose={close}
        title="Purge finished"
        size="small"
        footer={
          <button type="button" className="button button--primary" onClick={close} data-autofocus>
            Done
          </button>
        }
      >
        <p>{result?.deleted.length === 1 ? "Deleted 1 secret." : `Deleted ${result?.deleted.length ?? 0} secrets.`}</p>
        {result && result.deleted.length > 0 ? (
          <ul className="purge-list">
            {result.deleted.map((key) => (
              <li key={key}>
                <code>{key}</code>
              </li>
            ))}
          </ul>
        ) : null}
        {result && result.kept.length > 0 ? (
          <>
            <p>Kept:</p>
            <ul className="purge-list">
              {result.kept.map((kept) => (
                <li key={kept.key}>
                  <code>{kept.key}</code>: {kept.reason}
                </li>
              ))}
            </ul>
          </>
        ) : null}
      </Dialog>
    </div>
  );
}
