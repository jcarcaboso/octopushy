import { X } from "lucide-react";
import { useEffect, useId, useRef, type ReactNode } from "react";

interface DialogProps {
  open: boolean;
  onClose: () => void;
  title: string;
  description?: ReactNode;
  children: ReactNode;
  footer?: ReactNode;
  size?: "small" | "medium" | "large";
}

/**
 * A modal dialog on the native <dialog> element: showModal() makes the rest of the page
 * inert (so focus stays inside), and Escape closes it. The element marked
 * `data-autofocus` (or else the first field) gets focus when it opens.
 */
export function Dialog({ open, onClose, title, description, children, footer, size = "medium" }: DialogProps) {
  const ref = useRef<HTMLDialogElement>(null);
  const titleId = useId();
  const descriptionId = useId();
  const returnFocus = useRef<Element | null>(null);

  useEffect(() => {
    const dialog = ref.current;
    if (!dialog) return;
    if (open && !dialog.open) {
      returnFocus.current = document.activeElement;
      dialog.showModal();
      // React's autoFocus runs before showModal(), so focus the intended element here.
      const target =
        dialog.querySelector<HTMLElement>("[data-autofocus]") ??
        dialog.querySelector<HTMLElement>(".dialog__body :is(input, textarea, select):not([disabled]):not([type=hidden])");
      target?.focus();
    } else if (!open && dialog.open) {
      dialog.close();
      if (returnFocus.current instanceof HTMLElement) returnFocus.current.focus();
    }
  }, [open]);

  useEffect(() => {
    const dialog = ref.current;
    return () => {
      if (dialog?.open) dialog.close();
    };
  }, []);

  return (
    <dialog
      ref={ref}
      className={`dialog dialog--${size}`}
      aria-labelledby={titleId}
      aria-describedby={description ? descriptionId : undefined}
      onCancel={(event) => {
        event.preventDefault();
        onClose();
      }}
    >
      {open ? (
        <div className="dialog__frame">
          <header className="dialog__header">
            <h2 id={titleId}>{title}</h2>
            <button type="button" className="icon-button" onClick={onClose} aria-label="Close">
              <X size={18} aria-hidden="true" />
            </button>
          </header>
          {description ? (
            <div id={descriptionId} className="dialog__description">
              {description}
            </div>
          ) : null}
          <div className="dialog__body">{children}</div>
          {footer ? <footer className="dialog__footer">{footer}</footer> : null}
        </div>
      ) : null}
    </dialog>
  );
}
