import { Unlink } from "lucide-react";
import { useState } from "react";
import { useDetachGuest } from "../api/queries";
import { ConfirmDialog } from "./ConfirmDialog";

/** Detaches a guest whose folder was deleted, in place of the removal Octopushy proposed for it. */
export function DetachGuest({ id, address, noun }: { id: string; address: string; noun: string }) {
  const [open, setOpen] = useState(false);
  const [typed, setTyped] = useState("");
  const detach = useDetachGuest();
  return (
    <>
      <button type="button" className="button" onClick={() => {
        setTyped("");
        detach.reset();
        setOpen(true);
      }}>
        <Unlink size={15} aria-hidden="true" />
        Detach instead
      </button>
      <ConfirmDialog
        open={open}
        title={`Detach ${address}?`}
        confirmLabel="Detach, keep the machine"
        pending={detach.isPending}
        disabled={typed !== address}
        error={detach.error}
        onClose={() => { if (!detach.isPending) setOpen(false); }}
        onConfirm={() => detach.mutate({ id, address }, { onSuccess: () => setOpen(false) })}
      >
        <p>
          Octopushy forgets this guest in place of removing it: its proposed removal is cancelled, and its binding, OpenTofu
          state and Octopushy's keys for it are deleted. Nothing is sent to Proxmox. The {noun} keeps running as it is,
          protection included, and the installation lists it as one Octopushy does not manage. Its runs stay on record.
        </p>
        <label className="field">
          <span className="field__label">Type {address} to confirm</span>
          <input value={typed} onChange={(event) => setTyped(event.target.value)} autoComplete="off" spellCheck={false} />
        </label>
      </ConfirmDialog>
    </>
  );
}
