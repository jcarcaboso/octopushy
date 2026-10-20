import { Power, PowerOff, Trash2 } from "lucide-react";
import { useState, type FormEvent } from "react";
import { useDeleteAgent, useUpdateAgent } from "../../api/queries";
import type { Agent } from "../../api/types";
import { Badge } from "../../components/Badge";
import { ConfirmDialog } from "../../components/ConfirmDialog";
import { LabelEditor } from "../../components/LabelEditor";
import { RelativeTime } from "../../components/RelativeTime";
import { useNow } from "../../components/useNow";
import { ErrorMessage } from "../../components/States";
import { toDate } from "../../lib/time";

const week = 7 * 24 * 3600 * 1000;

function Certificate({ expiresAt }: { expiresAt: string }) {
  const now = useNow();
  const left = toDate(expiresAt).getTime() - now;
  return (
    <span className="stack stack--tiny">
      <RelativeTime value={expiresAt} />
      {left <= 0 ? <Badge tone="danger">Expired</Badge> : left < week ? <Badge tone="warning">Renews soon</Badge> : null}
    </span>
  );
}

function Concurrency({ agent }: { agent: Agent }) {
  const update = useUpdateAgent();
  const current = agent.maxConcurrency;
  const [draft, setDraft] = useState<string | null>(null);
  const value = draft ?? String(current);
  const parsed = Number(value);
  const valid = Number.isInteger(parsed) && parsed >= 1 && parsed <= 64;
  const dirty = draft !== null && parsed !== current;

  function save(event: FormEvent) {
    event.preventDefault();
    if (!valid || !dirty) return;
    update.mutate({ id: agent.id, maxConcurrency: parsed }, { onSuccess: () => setDraft(null) });
  }

  return (
    <form className="inline-form" onSubmit={save}>
      <input
        type="number"
        min={1}
        max={64}
        className="input--narrow num"
        value={value}
        onChange={(e) => setDraft(e.target.value)}
        aria-label={`Maximum concurrent attempts for ${agent.name}`}
        aria-invalid={!valid || undefined}
      />
      {dirty ? (
        <button type="submit" className="button button--small button--primary" disabled={!valid || update.isPending}>
          Save
        </button>
      ) : null}
      {update.error ? <ErrorMessage error={update.error} /> : null}
    </form>
  );
}

function Labels({ agent }: { agent: Agent }) {
  const update = useUpdateAgent();
  // Show the labels being saved until the refreshed list arrives.
  const shown = update.isPending && update.variables?.labels ? update.variables.labels : agent.labels;
  return (
    <div className="stack stack--tiny">
      <LabelEditor
        labels={shown}
        onChange={(labels) => update.mutate({ id: agent.id, labels })}
        label={`Add a label to ${agent.name}`}
        disabled={update.isPending}
      />
      {update.error ? <ErrorMessage error={update.error} /> : null}
    </div>
  );
}

export function AgentRow({ agent }: { agent: Agent }) {
  const toggle = useUpdateAgent();
  const remove = useDeleteAgent();
  const [confirm, setConfirm] = useState(false);
  const running = agent.runningAttempts;

  return (
    <tr className={agent.disabled ? "row--disabled" : undefined}>
      <th scope="row">
        <div className="agent-name">
          <span
            className={agent.connected ? "conn conn--on" : "conn conn--off"}
            aria-hidden="true"
          />
          <span className="strong">{agent.name}</span>
        </div>
        <div className="small muted">
          {agent.connected ? "Connected" : "Not connected"}
          {agent.disabled ? (
            <>
              {" · "}
              <Badge tone="muted">Disabled</Badge>
            </>
          ) : null}
        </div>
        <div className="small muted mono">{agent.version ?? "version unknown"}</div>
      </th>
      <td className="cell-labels">
        <Labels agent={agent} />
      </td>
      <td>
        <Concurrency agent={agent} />
      </td>
      <td>
        <div className="small">{agent.executors.length ? agent.executors.join(", ") : <span className="muted">—</span>}</div>
        <div className="small muted mono break">{agent.toolRuntime ?? ""}</div>
      </td>
      <td className="nowrap">
        <RelativeTime value={agent.lastSeenAt} empty="Never" />
      </td>
      <td className="nowrap">
        <Certificate expiresAt={agent.certificateExpiresAt} />
      </td>
      <td className="num">{running}</td>
      <td>
        <div className="button-row button-row--tight">
          <button
            type="button"
            className="button button--small"
            disabled={toggle.isPending}
            onClick={() => toggle.mutate({ id: agent.id, disabled: !agent.disabled })}
          >
            {agent.disabled ? <Power size={14} aria-hidden="true" /> : <PowerOff size={14} aria-hidden="true" />}
            {agent.disabled ? "Enable" : "Disable"}
          </button>
          <button type="button" className="button button--small button--danger-outline" onClick={() => setConfirm(true)}>
            <Trash2 size={14} aria-hidden="true" />
            Remove
          </button>
        </div>
        {toggle.error ? <ErrorMessage error={toggle.error} /> : null}
        <ConfirmDialog
          open={confirm}
          title={`Remove ${agent.name}?`}
          confirmLabel="Remove the agent"
          danger
          pending={remove.isPending}
          error={remove.error}
          onClose={() => {
            setConfirm(false);
            remove.reset();
          }}
          onConfirm={() => remove.mutate(agent.id, { onSuccess: () => setConfirm(false) })}
        >
          <p>
            The agent is disconnected and its certificate is no longer accepted. To use the machine again, enroll it with
            a new token.
          </p>
          {running > 0 ? (
            <p>
              It is running {running} {running === 1 ? "attempt" : "attempts"}; disable it and wait for them to finish first.
            </p>
          ) : null}
        </ConfirmDialog>
      </td>
    </tr>
  );
}
