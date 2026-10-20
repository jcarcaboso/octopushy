import type { ReactNode } from "react";
import { Dialog } from "./Dialog";
import { ErrorMessage } from "./States";

interface Props {
  open: boolean;
  title: string;
  children: ReactNode;
  confirmLabel: string;
  danger?: boolean;
  pending?: boolean;
  error?: unknown;
  onConfirm: () => void;
  onClose: () => void;
}

export function ConfirmDialog({ open, title, children, confirmLabel, danger, pending, error, onConfirm, onClose }: Props) {
  return (
    <Dialog
      open={open}
      onClose={onClose}
      title={title}
      size="small"
      footer={
        <>
          <button type="button" className="button" onClick={onClose} data-autofocus>
            Keep as is
          </button>
          <button
            type="button"
            className={danger ? "button button--danger" : "button button--primary"}
            onClick={onConfirm}
            disabled={pending}
          >
            {pending ? "Working…" : confirmLabel}
          </button>
        </>
      }
    >
      <div className="stack">
        {children}
        <ErrorMessage error={error} />
      </div>
    </Dialog>
  );
}
