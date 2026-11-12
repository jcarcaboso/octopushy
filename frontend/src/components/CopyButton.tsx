import { Check, Copy } from "lucide-react";
import { useEffect, useState } from "react";

async function copyText(text: string, button: HTMLElement): Promise<boolean> {
  try {
    if (navigator.clipboard && window.isSecureContext) {
      await navigator.clipboard.writeText(text);
      return true;
    }
  } catch {
    // Fall back below.
  }
  // Plain HTTP on a LAN address has no clipboard API. An open modal dialog makes the rest of the page inert, and an
  // inert textarea copies nothing, so the textarea goes into the button's dialog when there is one.
  const area = document.createElement("textarea");
  area.value = text;
  area.setAttribute("readonly", "");
  area.style.position = "fixed";
  area.style.opacity = "0";
  (button.closest("dialog") ?? document.body).appendChild(area);
  area.select();
  try {
    return document.execCommand("copy");
  } catch {
    return false;
  } finally {
    area.remove();
    button.focus();
  }
}

interface Props {
  text: string;
  label?: string;
  /** Show only the icon; the label stays as the accessible name. */
  compact?: boolean;
  variant?: "primary" | "secondary" | "quiet";
}

export function CopyButton({ text, label = "Copy", compact = false, variant }: Props) {
  const [state, setState] = useState<"idle" | "copied" | "failed">("idle");

  useEffect(() => {
    if (state === "idle") return;
    const t = setTimeout(() => setState("idle"), 2_000);
    return () => clearTimeout(t);
  }, [state]);

  const text_ = state === "copied" ? "Copied" : state === "failed" ? "Copy failed" : label;
  const className = compact
    ? "icon-button"
    : variant === "primary"
      ? "button button--primary"
      : variant === "secondary"
        ? "button"
        : variant === "quiet"
          ? "link-button small"
          : "button button--small";
  return (
    <button
      type="button"
      className={className}
      onClick={async (event) => {
        const button = event.currentTarget;
        setState((await copyText(text, button)) ? "copied" : "failed");
      }}
      aria-label={compact ? text_ : undefined}
      title={compact ? text_ : undefined}
    >
      {variant === "quiet" ? null : state === "copied" ? <Check size={14} aria-hidden="true" /> : <Copy size={14} aria-hidden="true" />}
      {compact ? null : <span>{text_}</span>}
      <span className="visually-hidden" aria-live="polite">
        {state === "copied" ? "Copied to the clipboard" : ""}
      </span>
    </button>
  );
}
