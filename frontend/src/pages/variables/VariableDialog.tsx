import { useState, type FormEvent } from "react";
import { useSetVariable, useStageVariable } from "../../api/queries";
import type { Variable } from "../../api/types";
import { Callout } from "../../components/Callout";
import { Dialog } from "../../components/Dialog";

interface Props {
  /** The entry being updated, or null to set a new one. */
  existing: Variable | null;
  /** A key to start with, for a referenced key that is not set yet. */
  presetKey?: string;
  onClose: () => void;
}

const keyPattern = /^[A-Za-z0-9._-]+(\/[A-Za-z0-9._-]+)*$/;

/** Rendered only while open, so every opening starts from the entry's current state. */
export function VariableDialog({ existing, presetKey = "", onClose }: Props) {
  const save = useSetVariable();
  const stage = useStageVariable();
  const wasSecret = existing?.visibility === "secret";
  const [key, setKey] = useState(existing?.key ?? presetKey);
  const [value, setValue] = useState(existing && !wasSecret ? (existing.value ?? "") : "");
  const [visibility, setVisibility] = useState<"secret" | "plain">(existing?.visibility === "plain" ? "plain" : "secret");
  const [description, setDescription] = useState(existing?.description ?? "");

  const keyValid = keyPattern.test(key.trim()) && !key.trim().startsWith("system/");
  const valueChanged = existing ? (wasSecret ? value !== "" : value !== (existing.value ?? "")) : true;
  const visibilityChanged = existing ? visibility !== existing.visibility : true;
  const sendsValue = !existing || valueChanged || visibilityChanged;
  const valueMissing = !existing && value === "";
  const staging = wasSecret && value !== "";
  const pending = save.isPending || stage.isPending;

  function close() {
    setValue("");
    save.reset();
    stage.reset();
    onClose();
  }

  function submit(event: FormEvent) {
    event.preventDefault();
    if (!keyValid || valueMissing || pending) return;
    if (staging) {
      stage.mutate(
        { key: key.trim(), value, description: description.trim() || null },
        { onSuccess: close },
      );
      return;
    }
    save.mutate(
      {
        key: key.trim(),
        value: sendsValue ? value : null,
        visibility: sendsValue ? visibility : null,
        description: description.trim() || null,
      },
      { onSuccess: close },
    );
  }

  return (
    <Dialog
      open
      onClose={close}
      title={existing ? `Update ${existing.key}` : "Set a variable"}
      footer={
        <>
          <button type="button" className="button" onClick={close}>
            Cancel
          </button>
          <button type="submit" form="variable-form" className="button button--primary" disabled={pending || !keyValid || valueMissing}>
            {pending ? (staging ? "Staging…" : "Saving…") : staging ? "Stage candidate" : "Save"}
          </button>
        </>
      }
    >
      <form id="variable-form" className="form" onSubmit={submit}>
        <div className="field">
          <label htmlFor="variable-key">Key</label>
          <input
            id="variable-key"
            className="mono"
            value={key}
            onChange={(e) => setKey(e.target.value)}
            disabled={Boolean(existing)}
            data-autofocus={!existing && !presetKey ? true : undefined}
            spellCheck={false}
            autoCapitalize="off"
            placeholder="proxmox/site-a/api-token"
            aria-invalid={key !== "" && !keyValid ? true : undefined}
            aria-describedby="variable-key-hint"
          />
          <div id="variable-key-hint" className={key !== "" && !keyValid ? "field__error" : "field__hint"}>
            Segments of letters, digits, '.', '_', or '-', separated by '/'. Keys under system/ belong to the server.
          </div>
        </div>
        <div className="field">
          <label htmlFor="variable-value">Value</label>
          <textarea
            id="variable-value"
            className="mono"
            rows={4}
            value={value}
            onChange={(e) => setValue(e.target.value)}
            data-autofocus={existing || presetKey ? true : undefined}
            spellCheck={false}
            autoComplete="off"
            placeholder={wasSecret ? "Leave empty to keep the current value" : undefined}
            aria-describedby="variable-value-hint"
          />
          <div id="variable-value-hint" className="field__hint">
            {wasSecret
              ? "The current value is concealed. Enter a replacement to stage a candidate; the active version stays unchanged until you promote it in Versions. Leave empty to change only the description."
              : existing
                ? "Unchanged, only the description is saved."
                : "Multi-line values are kept as entered."}
          </div>
        </div>
        <fieldset className="field">
          <legend>Visibility</legend>
          <div className="radio-row">
            <label className="radio">
              <input type="radio" name="visibility" value="secret" checked={visibility === "secret"} onChange={() => setVisibility("secret")} />
              <span>
                <strong>Secret</strong>
                <span className="muted small"> · concealed once saved; delivered only to operations that reference it</span>
              </span>
            </label>
            <label className={wasSecret ? "radio radio--disabled" : "radio"}>
              <input
                type="radio"
                name="visibility"
                value="plain"
                checked={visibility === "plain"}
                onChange={() => setVisibility("plain")}
                disabled={wasSecret}
                aria-describedby={wasSecret ? "variable-plain-hint" : undefined}
              />
              <span>
                <strong>Plain</strong>
                <span className="muted small"> · shown in the interface</span>
              </span>
            </label>
          </div>
          {wasSecret ? (
            <div id="variable-plain-hint" className="field__hint">
              A secret cannot be made plain.
            </div>
          ) : null}
        </fieldset>
        <div className="field">
          <label htmlFor="variable-description">
            Description <span className="muted">(optional)</span>
          </label>
          <input id="variable-description" value={description} onChange={(e) => setDescription(e.target.value)} />
        </div>
        {save.error || stage.error ? (
          <Callout kind="danger" title={stage.error ? "The candidate could not be staged." : "The variable could not be saved."} alert>
            Try again or reload the page. Submitted values are not shown in error messages.
          </Callout>
        ) : null}
      </form>
    </Dialog>
  );
}
