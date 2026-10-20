import { X } from "lucide-react";
import { useId, useState, type KeyboardEvent } from "react";

const labelPattern = /^[a-z0-9][a-z0-9._-]*$/;

interface Props {
  labels: string[];
  onChange: (labels: string[]) => void;
  /** The accessible name of the input that adds labels. */
  label: string;
  disabled?: boolean;
  placeholder?: string;
}

/** Labels as removable chips, with an input that adds one on Enter, comma, or space. */
export function LabelEditor({ labels, onChange, label, disabled = false, placeholder = "Add label" }: Props) {
  const [draft, setDraft] = useState("");
  const [problem, setProblem] = useState<string | null>(null);
  const hintId = useId();

  function add(raw: string) {
    const value = raw.trim().toLowerCase();
    if (!value) return true;
    if (!labelPattern.test(value)) {
      setProblem("Use lowercase letters, digits, '.', '_', and '-', starting with a letter or digit.");
      return false;
    }
    setProblem(null);
    if (!labels.includes(value)) onChange([...labels, value].sort());
    return true;
  }

  function onKeyDown(event: KeyboardEvent<HTMLInputElement>) {
    if (event.key === "Enter" || event.key === "," || event.key === " ") {
      event.preventDefault();
      if (add(draft)) setDraft("");
    } else if (event.key === "Backspace" && draft === "" && labels.length > 0) {
      onChange(labels.slice(0, -1));
    }
  }

  return (
    <div className="chips-field">
      <div className={disabled ? "chips chips--disabled" : "chips"}>
        {labels.map((l) => (
          <span className="chip" key={l}>
            <span className="mono">{l}</span>
            <button
              type="button"
              className="chip__remove"
              onClick={() => onChange(labels.filter((x) => x !== l))}
              aria-label={`Remove label ${l}`}
              disabled={disabled}
            >
              <X size={12} aria-hidden="true" />
            </button>
          </span>
        ))}
        <input
          className="chips__input"
          value={draft}
          onChange={(e) => {
            setDraft(e.target.value);
            setProblem(null);
          }}
          onKeyDown={onKeyDown}
          onBlur={() => {
            if (add(draft)) setDraft("");
          }}
          aria-label={label}
          aria-describedby={problem ? hintId : undefined}
          aria-invalid={problem ? true : undefined}
          placeholder={labels.length === 0 ? placeholder : ""}
          disabled={disabled}
          spellCheck={false}
          autoCapitalize="off"
        />
      </div>
      {problem ? (
        <div className="field__error" id={hintId}>
          {problem}
        </div>
      ) : null}
    </div>
  );
}

export function LabelList({ labels, empty = "None" }: { labels: string[]; empty?: string }) {
  if (labels.length === 0) return <span className="muted">{empty}</span>;
  return (
    <span className="chips chips--static">
      {labels.map((l) => (
        <span className="chip" key={l}>
          <span className="mono">{l}</span>
        </span>
      ))}
    </span>
  );
}
