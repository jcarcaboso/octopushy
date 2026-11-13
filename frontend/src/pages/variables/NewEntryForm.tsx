import { ArrowLeft, Lock, X } from "lucide-react";
import { useEffect, useState, type FormEvent } from "react";
import { ApiError } from "../../api/client";
import { useSetVariable } from "../../api/queries";
import { Callout } from "../../components/Callout";
import { ConflictNote } from "./ConflictNote";
import { FormatLine, ValueEditor } from "./ValueText";

// The server's pattern, and the storeRef schema's.
const keyPattern = /^[A-Za-z0-9][A-Za-z0-9._-]*(\/[A-Za-z0-9][A-Za-z0-9._-]*)*$/;

interface Props {
  /** The key to start from: the folder being browsed plus "/", or a key that is referenced but not set. */
  initialKey: string;
  backLabel: string;
  existingKeys: ReadonlySet<string>;
  onOpen: (key: string) => void;
  onSaved: (key: string) => void;
  onCancel: () => void;
  onDirty: (dirty: boolean) => void;
}

/** Sets a new key. Rendered only while open, so each opening starts empty. */
export function NewEntryForm({ initialKey, backLabel, existingKeys, onOpen, onSaved, onCancel, onDirty }: Props) {
  const save = useSetVariable();
  const [key, setKey] = useState(initialKey);
  const [visibility, setVisibility] = useState<"secret" | "plain">("secret");
  const [description, setDescription] = useState("");
  const [value, setValue] = useState("");

  const trimmed = key.trim();
  const keyValid = keyPattern.test(trimmed) && !trimmed.startsWith("system/");
  const keyWrong = !keyValid && key !== "" && key !== initialKey;
  const exists = existingKeys.has(trimmed);
  const dirty = key !== initialKey || value !== "" || description !== "";
  useEffect(() => onDirty(dirty), [dirty, onDirty]);
  useEffect(() => () => onDirty(false), [onDirty]);

  function submit(event: FormEvent) {
    event.preventDefault();
    if (!keyValid || exists || value === "" || save.isPending) return;
    save.mutate(
      { key: trimmed, value, visibility, description: description.trim() || null, expectedCurrentVersion: 0 },
      {
        onSuccess: () => {
          onDirty(false);
          onSaved(trimmed);
        },
      },
    );
  }

  return (
    <form className="vars-entry vars-new" onSubmit={submit} aria-labelledby="vars-new-title">
      <div className="vars-pane__back">
        <button type="button" className="link-button" onClick={onCancel}>
          <ArrowLeft size={14} aria-hidden="true" /> {backLabel}
        </button>
      </div>
      <div className="vars-pane__head">
        <h2 id="vars-new-title" className="vars-new__title">
          Set a variable
        </h2>
        <span className="vars-pane__corner">
          <button type="button" className="icon-button vars-pane__close" onClick={onCancel} aria-label="Close the entry" title="Close the entry">
            <X size={16} aria-hidden="true" />
          </button>
        </span>
      </div>

      <div className="field">
        <label htmlFor="vars-new-key">Key</label>
        <input
          id="vars-new-key"
          className="mono"
          value={key}
          onChange={(event) => setKey(event.target.value)}
          onFocus={(event) => event.currentTarget.setSelectionRange(key.length, key.length)}
          autoFocus
          spellCheck={false}
          autoCapitalize="off"
          autoComplete="off"
          placeholder="proxmox/site-a/api-token"
          aria-invalid={keyWrong ? true : undefined}
          aria-describedby="vars-new-key-hint"
        />
        <div id="vars-new-key-hint" className={keyWrong ? "field__error" : "field__hint"}>
          Each / starts a folder, and the last part is the name a definition asks for. Parts hold letters, digits, '.', '_' or '-' and start
          with a letter or digit. Keys under system/ belong to the server.
        </div>
      </div>
      {exists ? (
        <Callout
          kind="info"
          title="This key is already set."
          actions={
            <button type="button" className="button button--small" onClick={() => onOpen(trimmed)}>
              Open it
            </button>
          }
        >
          Open it to modify its value or save a new version.
        </Callout>
      ) : null}

      <div className="vars-new__pair">
        <div className="field">
          <span className="field__label" id="vars-new-visibility">
            Visibility
          </span>
          <div className="choice" role="group" aria-labelledby="vars-new-visibility">
            <button type="button" className="choice__item" aria-pressed={visibility === "secret"} onClick={() => setVisibility("secret")}>
              <Lock size={12} aria-hidden="true" /> Secret
            </button>
            <button type="button" className="choice__item" aria-pressed={visibility === "plain"} onClick={() => setVisibility("plain")}>
              Plain
            </button>
          </div>
        </div>
        <div className="field vars-new__description">
          <label htmlFor="vars-new-description">
            Description <span className="muted">(optional)</span>
          </label>
          <input id="vars-new-description" value={description} onChange={(event) => setDescription(event.target.value)} autoComplete="off" />
        </div>
      </div>
      <p className="muted small">
        {visibility === "secret"
          ? "A secret is concealed once saved, given to the runs that use it, and shown only when revealed."
          : "A plain value is shown in the interface."}
      </p>

      <div className="field">
        <span className="field__label">Value</span>
        <ValueEditor
          label="Value"
          value={value}
          onChange={setValue}
          placeholder="Paste a value, an env file or YAML. Or drop a file on the box."
          minRows={4}
          describedBy="vars-new-format"
        />
        <FormatLine id="vars-new-format" value={value} />
      </div>

      {save.error instanceof ApiError && save.error.status === 409 ? (
        <ConflictNote message={save.error.message} onRefresh={() => save.reset()} />
      ) : save.error ? (
        <Callout kind="danger" title="The variable could not be saved." alert>
          Try again or reload the page. Submitted values are not shown in error messages.
        </Callout>
      ) : null}

      <div className="vars-new__bar">
        <span className="muted small">Saved as v1 and active right away.</span>
        <span className="vars-save__buttons">
          <button type="button" className="button" onClick={onCancel}>
            Cancel
          </button>
          <button type="submit" className="button button--primary" disabled={!keyValid || exists || value === "" || save.isPending}>
            {save.isPending ? "Saving…" : "Save"}
          </button>
        </span>
      </div>
    </form>
  );
}
