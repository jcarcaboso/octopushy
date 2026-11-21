import { ChevronDown } from "lucide-react";
import { useEffect, useId, useRef, useState } from "react";
import { usePurgeVariables } from "../../api/queries";
import type { Variable } from "../../api/types";
import { ConfirmDialog } from "../../components/ConfirmDialog";
import { Dialog } from "../../components/Dialog";

/** Actions on the whole store. Purge deletes the secrets nothing uses; the server checks each one again before it goes. */
export function OptionsMenu({ entries }: { entries: Variable[] }) {
  const purge = usePurgeVariables();
  const [open, setOpen] = useState(false);
  // The keys as the dialog opened with them: a refetch while it is open must not add one the operator did not see.
  const [candidates, setCandidates] = useState<string[] | null>(null);
  const ref = useRef<HTMLDivElement>(null);
  const menuId = useId();
  const unused = entries.filter((entry) => entry.visibility === "secret" && entry.usedBy.length === 0).map((entry) => entry.key);

  useEffect(() => {
    if (!open) return;
    const outside = (event: PointerEvent) => {
      if (!ref.current?.contains(event.target as Node)) setOpen(false);
    };
    document.addEventListener("pointerdown", outside);
    return () => document.removeEventListener("pointerdown", outside);
  }, [open]);

  const result = purge.data;
  const confirming = candidates ?? [];
  const close = () => {
    if (!purge.isPending) setCandidates(null);
  };

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
            setCandidates(unused);
          }}
        >
          <span>Purge</span>
          <span className="options-menu__hint">
            {unused.length === 0
              ? "Every secret is in use."
              : unused.length === 1
                ? "Deletes the 1 secret nothing uses."
                : `Deletes the ${unused.length} secrets nothing uses.`}
          </span>
        </button>
      </div>
      <ConfirmDialog
        open={candidates !== null && !result}
        title={confirming.length === 1 ? "Purge 1 unused secret?" : `Purge ${confirming.length} unused secrets?`}
        confirmLabel="Purge"
        danger
        pending={purge.isPending}
        error={purge.error}
        onClose={close}
        onConfirm={() => purge.mutate(confirming)}
      >
        <p>No definition and no unfinished run uses these. Their values and every version are deleted. Who received them stays on record.</p>
        <ul className="purge-list">
          {confirming.map((key) => (
            <li key={key}>
              <code>{key}</code>
            </li>
          ))}
        </ul>
      </ConfirmDialog>
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
