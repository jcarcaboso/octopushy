import { useState, type FormEvent } from "react";
import { useCreateToken } from "../../api/queries";
import type { CreatedToken } from "../../api/types";
import { Callout } from "../../components/Callout";
import { CodeBlock } from "../../components/CodeBlock";
import { CopyButton } from "../../components/CopyButton";
import { Dialog } from "../../components/Dialog";
import { LabelEditor } from "../../components/LabelEditor";
import { RelativeTime } from "../../components/RelativeTime";
import { ErrorMessage } from "../../components/States";
import { formatAbsolute } from "../../lib/time";

const expiries = [
  { minutes: 15, label: "15 minutes" },
  { minutes: 60, label: "1 hour" },
  { minutes: 24 * 60, label: "1 day" },
  { minutes: 7 * 24 * 60, label: "7 days" },
];

function Created({ token }: { token: CreatedToken }) {
  return (
    <div className="stack">
      <Callout kind="warning" title="The token is shown once">
        Copy the command now and run it on the agent's machine. If you lose it, revoke the token and create another.
      </Callout>
      <CodeBlock code={token.command} label="Enrollment command" wrap />
      <dl className="kv">
        <div className="kv__item">
          <dt>Expires</dt>
          <dd>
            <RelativeTime value={token.expiresAt} /> <span className="muted small">({formatAbsolute(token.expiresAt)})</span>
          </dd>
        </div>
        <div className="kv__item">
          <dt>CA fingerprint</dt>
          <dd className="inline-group">
            <code className="mono break small">{token.caFingerprint}</code>
            <CopyButton text={token.caFingerprint} label="Copy the CA fingerprint" compact />
          </dd>
        </div>
      </dl>
      <p className="muted small">
        The agent checks the server's CA certificate against this fingerprint before it sends anything. The token
        carries the same fingerprint.
      </p>
    </div>
  );
}

export function EnrollDialog({ open, onClose }: { open: boolean; onClose: () => void }) {
  const create = useCreateToken();
  const [name, setName] = useState("");
  const [labels, setLabels] = useState<string[]>([]);
  const [maxConcurrency, setMaxConcurrency] = useState("2");
  const [expiry, setExpiry] = useState(60);

  const parsed = Number(maxConcurrency);
  const valid = Number.isInteger(parsed) && parsed >= 1 && parsed <= 64;

  function close() {
    onClose();
    create.reset();
    setName("");
    setLabels([]);
    setMaxConcurrency("2");
    setExpiry(60);
  }

  function submit(event: FormEvent) {
    event.preventDefault();
    if (!valid) return;
    create.mutate({ name: name.trim() || null, labels, maxConcurrency: parsed, expiresInMinutes: expiry });
  }

  const token = create.data;
  return (
    <Dialog
      open={open}
      onClose={close}
      title="Enroll an agent"
      description={token ? undefined : "Create a one-time token. Labels and the concurrency limit are set here, on the server; the agent never claims them."}
      footer={
        token ? (
          <button type="button" className="button button--primary" onClick={close}>
            Done
          </button>
        ) : (
          <>
            <button type="button" className="button" onClick={close}>
              Cancel
            </button>
            <button type="submit" form="enroll-form" className="button button--primary" disabled={create.isPending || !valid}>
              {create.isPending ? "Creating…" : "Create token"}
            </button>
          </>
        )
      }
    >
      {token ? (
        <Created token={token} />
      ) : (
        <form id="enroll-form" className="form" onSubmit={submit}>
          <div className="field">
            <label htmlFor="enroll-name">
              Name <span className="muted">(optional)</span>
            </label>
            <input id="enroll-name" value={name} onChange={(e) => setName(e.target.value)} data-autofocus placeholder="for example site-a-01" />
            <div className="field__hint">Without a name, the agent uses its host name.</div>
          </div>
          <div className="field">
            <span className="field__label" id="enroll-labels-label">
              Labels
            </span>
            <LabelEditor labels={labels} onChange={setLabels} label="Add a label" />
            <div className="field__hint">Runs whose definitions ask for labels go only to agents that have all of them.</div>
          </div>
          <div className="form__row">
            <div className="field">
              <label htmlFor="enroll-max">Maximum concurrent attempts</label>
              <input
                id="enroll-max"
                type="number"
                min={1}
                max={64}
                className="input--narrow num"
                value={maxConcurrency}
                onChange={(e) => setMaxConcurrency(e.target.value)}
                aria-invalid={!valid || undefined}
              />
            </div>
            <div className="field">
              <label htmlFor="enroll-expiry">Token expires after</label>
              <select id="enroll-expiry" value={expiry} onChange={(e) => setExpiry(Number(e.target.value))}>
                {expiries.map((x) => (
                  <option key={x.minutes} value={x.minutes}>
                    {x.label}
                  </option>
                ))}
              </select>
            </div>
          </div>
          <ErrorMessage error={create.error} />
        </form>
      )}
    </Dialog>
  );
}
