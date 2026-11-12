import { useState } from "react";
import { useCreateToken, useDeleteToken } from "../../api/queries";
import { CopyButton } from "../../components/CopyButton";
import { Dialog } from "../../components/Dialog";
import { ErrorMessage, Spinner } from "../../components/States";
import { formatAbsolute } from "../../lib/time";
import { newToken } from "./newToken";

const expiries = [
  { minutes: 15, label: "15 minutes" },
  { minutes: 60, label: "1 hour" },
  { minutes: 24 * 60, label: "1 day" },
  { minutes: 7 * 24 * 60, label: "7 days" },
];

interface Props {
  open: boolean;
  onClose: () => void;
  /** The token is made by the button that opens the dialog, so it is ready as the dialog shows. */
  create: ReturnType<typeof useCreateToken>;
}

export function EnrollDialog({ open, onClose, create }: Props) {
  const revoke = useDeleteToken();
  const [more, setMore] = useState(false);
  const [name, setName] = useState("");
  const [expiry, setExpiry] = useState(60);
  const token = create.data;
  const made = create.variables;

  function close() {
    onClose();
    create.reset();
    revoke.reset();
    setMore(false);
    setName("");
    setExpiry(60);
  }

  function again() {
    if (!token) return;
    revoke.mutate(token.id, { onSuccess: () => create.mutate(newToken(name, expiry)) });
  }

  const expiresIn = expiries.find((x) => x.minutes === made?.expiresInMinutes)?.label;
  const busy = create.isPending || revoke.isPending;

  return (
    <Dialog
      open={open}
      onClose={close}
      title="Enroll an agent"
      footer={
        <button type="button" className="button" onClick={close}>
          Done
        </button>
      }
    >
      {create.isPending ? (
        <p className="inline-group muted">
          <Spinner label="Making a token" /> Making a token…
        </p>
      ) : !token ? (
        <div className="stack">
          <ErrorMessage error={create.error} title="The token could not be made" />
          <div>
            <button type="button" className="button" onClick={() => create.mutate(newToken(name, expiry))}>
              Try again
            </button>
          </div>
        </div>
      ) : (
        <div className="enroll">
          <div className="enroll__head">
            <span className="strong break">{made?.name ?? "New token"}</span>
            <span className="muted small" title={formatAbsolute(token.expiresAt)}>
              Expires in {expiresIn ?? "a while"}
            </span>
          </div>
          <code className="enroll__token">{token.token}</code>
          <div className="enroll__copy">
            <CopyButton text={token.token} label="Copy token" variant="primary" />
            <CopyButton text={token.command} label="Copy command" variant="secondary" />
            <CopyButton text={token.caFingerprint} label="Copy the CA fingerprint" variant="quiet" />
          </div>
          <p className="muted small">Shown once. Closing this keeps the token valid until it expires.</p>
          <div>
            <button type="button" className="link-button small" aria-expanded={more} aria-controls="enroll-more" onClick={() => setMore(!more)}>
              Name it or change when it expires
            </button>
          </div>
          {more ? (
            <div id="enroll-more" className="enroll__more">
              <div className="field">
                <label htmlFor="enroll-name">
                  Name <span className="muted">(optional)</span>
                </label>
                <input id="enroll-name" value={name} onChange={(e) => setName(e.target.value)} placeholder="for example site-a-01" />
                <div className="field__hint">Without a name, the agent uses its host name.</div>
              </div>
              <div className="field">
                <span className="field__label" id="enroll-expiry">
                  Expires after
                </span>
                <div className="choice" role="group" aria-labelledby="enroll-expiry">
                  {expiries.map((x) => (
                    <button
                      key={x.minutes}
                      type="button"
                      className="choice__item"
                      aria-pressed={expiry === x.minutes}
                      onClick={() => setExpiry(x.minutes)}
                    >
                      {x.label}
                    </button>
                  ))}
                </div>
              </div>
              <div>
                <button type="button" className="button button--small" disabled={busy} onClick={again}>
                  Make another with these
                </button>
              </div>
              <p className="muted small">The token above is revoked, and a new one takes its place.</p>
              <ErrorMessage error={revoke.error} />
            </div>
          ) : null}
        </div>
      )}
    </Dialog>
  );
}
