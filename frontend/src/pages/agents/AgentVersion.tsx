import { ArrowUp, Check, LoaderCircle } from "lucide-react";
import { Link } from "react-router";
import { useCancelAgentUpdate, useUpdateAgentVersion } from "../../api/queries";
import type { Agent } from "../../api/types";

function Links({ update }: { update: Agent["update"] }) {
  return (
    <>
      {update.changeId ? <Link to={`/changes/${update.changeId}`}>View change</Link> : null}
      {update.runId ? <Link to={`/runs/${update.runId}`}>View run</Link> : null}
    </>
  );
}

/** The agent's version and what can be done about it: nothing, update it, wait, or try again. */
export function AgentVersion({ agent }: { agent: Agent }) {
  const start = useUpdateAgentVersion();
  const cancel = useCancelAgentUpdate();
  const { update } = agent;
  const error = start.error ?? cancel.error;

  const updateButton = (label: string) => (
    <button type="button" className="button button--small" disabled={start.isPending} onClick={() => start.mutate(agent.id)}>
      <ArrowUp size={14} aria-hidden="true" />
      {label}
    </button>
  );

  return (
    <div className="agent-version">
      <span className="agent-version__line">
        <code className="run-chip run-chip--mono">{agent.version ?? "unknown"}</code>
        {update.state === "current" ? (
          <span className="valid-mark">
            <Check size={14} aria-hidden="true" />
            Latest
          </span>
        ) : update.state === "available" ? (
          updateButton(`Update to ${update.targetVersion}`)
        ) : update.state === "unavailable" ? (
          <span className="agent-version__reason muted small" title={update.reason ?? undefined}>
            {update.reason ?? "Cannot be updated from here"}
          </span>
        ) : update.state === "queued" ? (
          <>
            <span className="muted small">Waiting for another update</span>
            <button type="button" className="link-button small" disabled={cancel.isPending} onClick={() => cancel.mutate(agent.id)}>
              Cancel
            </button>
          </>
        ) : update.state === "updating" ? (
          <>
            <span className="agent-version__progress small">
              <LoaderCircle className="spin" size={14} aria-hidden="true" />
              Updating to {update.targetVersion}
            </span>
            <span className="small agent-version__links">
              <Links update={update} />
            </span>
          </>
        ) : update.state === "failed" ? (
          <>
            <span className="agent-version__failed small">Update failed</span>
            {updateButton("Try again")}
          </>
        ) : null}
      </span>
      {update.state === "failed" ? (
        <span className="agent-version__note small">
          {update.reason ? (
            <span className="agent-version__reason muted" title={update.reason}>
              {update.reason}
            </span>
          ) : null}
          <span className="agent-version__links">
            <Links update={update} />
          </span>
        </span>
      ) : null}
      {(update.state === "available" || update.state === "failed") && agent.work.length > 0 ? (
        <span className="agent-version__note muted small">It finishes its runs first.</span>
      ) : null}
      {error ? (
        <span className="field__error" role="alert">
          {error.message}
        </span>
      ) : null}
    </div>
  );
}
