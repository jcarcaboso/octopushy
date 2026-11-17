import { useQueryClient } from "@tanstack/react-query";
import { Save, Send, Trash2 } from "lucide-react";
import { useState, type FormEvent } from "react";
import { api, unwrap } from "../api/client";
import { keys, useDeleteWebhook, useTestWebhook, useWebhook } from "../api/queries";
import type { Webhook } from "../api/types";
import { Badge } from "../components/Badge";
import { Callout } from "../components/Callout";
import { Card } from "../components/Card";
import { ConfirmDialog } from "../components/ConfirmDialog";
import { PageHeader } from "../components/PageHeader";
import { RelativeTime } from "../components/RelativeTime";
import { ErrorMessage, PageSkeleton } from "../components/States";

/** What each kind of notification is, as the operator chooses what the webhook sends. */
const kindLabels: Record<string, string> = {
  "run-approval": "A run waits for approval",
  "run-failed": "A run failed",
  "run-uncertain": "A run's outcome is uncertain",
  "agent-offline": "An agent is offline",
  "agent-online": "An agent told offline is back",
  "agent-update-failed": "An agent's update failed",
  "check-down": "An uptime check went down",
  "check-up": "An uptime check came back up",
};

function Status({ webhook }: { webhook: Webhook }) {
  if (!webhook.configured) return <Badge tone="neutral">Not set</Badge>;
  if (!webhook.enabled) return <Badge tone="danger">Stopped</Badge>;
  return <Badge tone="success">Sending</Badge>;
}

/** How the last attempt went, the time of the last delivery and what was given up. */
function LastDelivery({ webhook }: { webhook: Webhook }) {
  if (!webhook.lastAttemptAt) return <p className="muted">Nothing has been sent yet.</p>;
  return (
    <dl className="kv">
      <dt>Last attempt</dt>
      <dd>
        <RelativeTime value={webhook.lastAttemptAt} />
        {" · "}
        {webhook.lastDelivered ? <Badge tone="success">Delivered</Badge> : <Badge tone="danger">Failed</Badge>}{" "}
        <span className="mono">{webhook.lastResult}</span>
      </dd>
      {webhook.lastDeliveredAt && !webhook.lastDelivered ? (
        <>
          <dt>Last delivered</dt>
          <dd>
            <RelativeTime value={webhook.lastDeliveredAt} />
          </dd>
        </>
      ) : null}
      {webhook.nextAttemptAt && webhook.enabled ? (
        <>
          <dt>Next try</dt>
          <dd>
            <RelativeTime value={webhook.nextAttemptAt} />
          </dd>
        </>
      ) : null}
      {webhook.dropped > 0 ? (
        <>
          <dt>Given up</dt>
          <dd>{webhook.dropped} events: too old, too many waiting, or refused by the receiver</dd>
        </>
      ) : null}
    </dl>
  );
}

function WebhookCard({ webhook }: { webhook: Webhook }) {
  const client = useQueryClient();
  const [url, setUrl] = useState("");
  // The kinds ticked here, until saved; the saved ones otherwise.
  const [draft, setDraft] = useState<string[] | null>(null);
  const kinds = draft ?? webhook.kinds;
  const [secret, setSecret] = useState<string | null>(null);
  const [pending, setPending] = useState(false);
  const [error, setError] = useState<unknown>(null);
  const [removing, setRemoving] = useState(false);
  const remove = useDeleteWebhook();
  const test = useTestWebhook();

  async function save(event: FormEvent) {
    event.preventDefault();
    setPending(true);
    setError(null);
    try {
      // The secret is kept in this component only, out of the query and mutation caches.
      const saved = await unwrap(api.PUT("/api/notifications/webhook", { body: { url: url.trim() || null, kinds } }));
      client.setQueryData(keys.webhook, saved.webhook);
      if (saved.secret) setSecret(saved.secret);
      setUrl("");
      setDraft(null);
    } catch (failure) {
      setError(failure);
    } finally {
      setPending(false);
    }
  }

  const toggle = (kind: string) => setDraft(kinds.includes(kind) ? kinds.filter((k) => k !== kind) : [...kinds, kind]);

  return (
    <Card title="Webhook" meta={<Status webhook={webhook} />}>
      <div className="stack">
        <p>
          Octopushy posts a small signed JSON event to one URL for each notification of the kinds chosen below. An event names what
          happened and links to its page. It never carries what a tool printed or a stored value.
        </p>
        {webhook.configured ? (
          <p>
            Sends to <span className="mono">{webhook.host}</span>. The full URL is kept as a secret and never shown again.
          </p>
        ) : null}
        {webhook.configured && !webhook.enabled ? (
          <Callout kind="danger" title="The receiver answered 410 Gone">
            Sending stopped <RelativeTime value={webhook.stoppedAt ?? webhook.lastAttemptAt ?? ""} />. Set the URL again to start
            sending from now.
          </Callout>
        ) : null}
        {!webhook.linksIncluded ? (
          <Callout kind="info">
            Events carry no link to their page: the server's <code>Octopushy:PublicUrl</code> is not set.
          </Callout>
        ) : null}
        {webhook.configured ? <LastDelivery webhook={webhook} /> : null}

        <form className="form" onSubmit={(event) => void save(event)}>
          <div className="field">
            <label htmlFor="webhook-url">{webhook.configured ? "New URL" : "URL"}</label>
            <input
              id="webhook-url"
              className="mono"
              type="url"
              value={url}
              maxLength={2000}
              required={!webhook.configured}
              onChange={(event) => setUrl(event.target.value)}
              placeholder={webhook.configured ? "Leave empty to keep the saved URL" : "https://hooks.example.org/octopushy"}
              autoComplete="off"
              spellCheck={false}
              aria-describedby="webhook-url-hint"
            />
            <div id="webhook-url-hint" className="field__hint">
              Write-only. {webhook.httpAllowed ? "https:// or http://" : "https:// only"}, with no user name or password. Saving a
              URL makes a new signing secret and sends what happens from then on.
            </div>
          </div>
          <fieldset className="field">
            <legend>Send</legend>
            <div className="radio-row">
              {webhook.availableKinds.map((kind) => (
                <label key={kind} className="checkbox">
                  <input type="checkbox" checked={kinds.includes(kind)} onChange={() => toggle(kind)} />
                  <span>
                    {kindLabels[kind] ?? kind} <span className="muted mono small">{kind}</span>
                  </span>
                </label>
              ))}
            </div>
            <div className="field__hint">
              By default, everything that waits on you or went wrong with an agent. Uptime checks are left out: a check that flaps
              would post every minute.
            </div>
          </fieldset>
          <ErrorMessage error={error} />
          <div className="button-row">
            <button type="submit" className="button button--primary" disabled={pending || kinds.length === 0 || (!webhook.configured && !url.trim())}>
              <Save size={15} aria-hidden="true" />
              {pending ? "Saving…" : "Save"}
            </button>
            <button type="button" className="button" disabled={!webhook.configured || test.isPending} onClick={() => test.mutate()}>
              <Send size={15} aria-hidden="true" />
              {test.isPending ? "Sending…" : "Send a test event"}
            </button>
            {webhook.configured ? (
              <button type="button" className="button button--danger" onClick={() => setRemoving(true)}>
                <Trash2 size={15} aria-hidden="true" />
                Remove
              </button>
            ) : null}
          </div>
        </form>

        {test.data ? (
          <Callout kind={test.data.delivered ? "success" : "danger"} title={test.data.delivered ? "The test event was delivered" : "The test event was not delivered"}>
            <span className="mono">{test.data.result}</span>
          </Callout>
        ) : null}
        <ErrorMessage error={test.error} />

        {secret !== null ? (
          <section className="stack" aria-label="New signing secret">
            <p>
              This signing secret is shown only now. The receiver checks each event's <code>X-Octopushy-Signature</code> with it; see
              docs/notifications.md.
            </p>
            <label htmlFor="webhook-secret">Signing secret</label>
            <textarea id="webhook-secret" className="mono" readOnly value={secret} rows={2} spellCheck={false} autoComplete="off" />
            <button type="button" className="button" onClick={() => setSecret(null)}>
              I saved it
            </button>
          </section>
        ) : null}
      </div>
      <ConfirmDialog
        open={removing}
        title="Remove the webhook"
        confirmLabel="Remove webhook"
        danger
        pending={remove.isPending}
        error={remove.error}
        onConfirm={() => remove.mutate(undefined, { onSuccess: () => setRemoving(false) })}
        onClose={() => setRemoving(false)}
      >
        <p>Its URL and signing secret are deleted, and nothing more is sent. The notifications in the app are not affected.</p>
      </ConfirmDialog>
    </Card>
  );
}

export function NotificationsPage() {
  const webhook = useWebhook();
  return (
    <div className="stack">
      <PageHeader title="Notifications" />
      {webhook.data ? <WebhookCard webhook={webhook.data} /> : webhook.error ? <ErrorMessage error={webhook.error} /> : <PageSkeleton />}
    </div>
  );
}
