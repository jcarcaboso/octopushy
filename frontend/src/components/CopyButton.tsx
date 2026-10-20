import { Check, Copy } from "lucide-react";
import { useEffect, useState } from "react";

async function copyText(text: string): Promise<boolean> {
  try {
    if (navigator.clipboard && window.isSecureContext) {
      await navigator.clipboard.writeText(text);
      return true;
    }
  } catch {
    // Fall back below.
  }
  // Plain HTTP on a LAN address has no clipboard API.
  const area = document.createElement("textarea");
  area.value = text;
  area.setAttribute("readonly", "");
  area.style.position = "fixed";
  area.style.opacity = "0";
  document.body.appendChild(area);
  area.select();
  try {
    return document.execCommand("copy");
  } catch {
    return false;
  } finally {
    area.remove();
  }
}

interface Props {
  text: string;
  label?: string;
  /** Show only the icon; the label stays as the accessible name. */
  compact?: boolean;
}

export function CopyButton({ text, label = "Copy", compact = false }: Props) {
  const [state, setState] = useState<"idle" | "copied" | "failed">("idle");

  useEffect(() => {
    if (state === "idle") return;
    const t = setTimeout(() => setState("idle"), 2_000);
    return () => clearTimeout(t);
  }, [state]);

  const text_ = state === "copied" ? "Copied" : state === "failed" ? "Copy failed" : label;
  return (
    <button
      type="button"
      className={compact ? "icon-button" : "button button--small"}
      onClick={async () => setState((await copyText(text)) ? "copied" : "failed")}
      aria-label={compact ? text_ : undefined}
      title={compact ? text_ : undefined}
    >
      {state === "copied" ? <Check size={14} aria-hidden="true" /> : <Copy size={14} aria-hidden="true" />}
      {compact ? null : <span>{text_}</span>}
      <span className="visually-hidden" aria-live="polite">
        {state === "copied" ? "Copied to the clipboard" : ""}
      </span>
    </button>
  );
}
