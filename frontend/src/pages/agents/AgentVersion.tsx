import { ArrowUp, Check, LoaderCircle, Undo2 } from "lucide-react";
import { Link } from "react-router";
import { useCancelAgentUpdate, useRevertAgentVersion, useUpdateAgentVersion } from "../../api/queries";
import type { Agent } from "../../api/types";

/** What an update in flight is doing, by its phase. */
const phases: Record<string, string> = {
  "rolling-out": "",
  "awaiting-approval": "its run waits for approval",
  blocked: "its run is blocked",
  "waiting-for-idle": "waiting for its work to finish",
  "waiting-for-unconfirmed-change": "waiting for it to say a change whose lease ran out has stopped",
  restarting: "restarting",
};

/** Why an update failed, in a few words, by its failure; the reason says the rest. */
const failures: Record<string, string> = {
  "lost-executors": "lost tools",
  "did-not-come-back": "not back in time",
  "other-version": "came back on another version",
};

function time(at: string) {
  return new Date(at).toLocaleTimeString([], { hour: "2-digit", minute: "2-digit" });
}

function Links({ update }: { update: Agent["update"] }) {
  return (
    <>
      {update.changeId ? <Link to={`/changes/${update.changeId}`}>View change</Link> : null}
      {update.runId ? <Link to={`/runs/${update.runId}`}>View run</Link> : null}
      {update.waitingForRunId ? <Link to={`/runs/${update.waitingForRunId}`}>View the run it waits for</Link> : null}
    </>
  );
}

/** The agent's version and what can be done about it: nothing, update it, wait, or try again. */
export function AgentVersion({ agent }: { agent: Agent }) {
  const start = useUpdateAgentVersion();
  const cancel = useCancelAgentUpdate();
  const revert = useRevertAgentVersion();
  const { update } = agent;
  const error = start.error ?? cancel.error ?? revert.error;
  const verb = update.revert ? "Reverting" : "Updating";
  const phase = update.phase ? phases[update.phase] : "";
  const failure = update.failure ? failures[update.failure] : undefined;
  // A failed update that left the agent on the new version is not tried again; Revert is the way back.
  const retry = update.state === "failed" && !update.revert && update.failure !== "lost-executors";

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
            <span className="muted small">{update.revert ? `Revert to ${update.targetVersion} waits for another update` : "Waiting for another update"}</span>
            <button type="button" className="link-button small" disabled={cancel.isPending} onClick={() => cancel.mutate(agent.id)}>
              Cancel
            </button>
          </>
        ) : update.state === "updating" ? (
          <>
            <span className="agent-version__progress small">
              <LoaderCircle className="spin" size={14} aria-hidden="true" />
              {verb} to {update.targetVersion}
              {phase ? <span className="muted"> · {phase}</span> : null}
              {update.phase === "restarting" && update.deadlineAt ? (
                <span className="muted"> · fails if not back by {time(update.deadlineAt)}</span>
              ) : null}
            </span>
            <span className="small agent-version__links">
              <Links update={update} />
            </span>
          </>
        ) : update.state === "failed" ? (
          <>
            <span className="agent-version__failed small">
              {update.revert ? "Revert failed" : "Update failed"}
              {failure ? `: ${failure}` : null}
            </span>
            {retry ? updateButton("Try again") : null}
          </>
        ) : null}
        {update.canRevert && update.previousVersion ? (
          <button
            type="button"
            className="link-button small"
            disabled={revert.isPending}
            title={`Bring back ${update.previousVersion}, the version it ran before its last update`}
            onClick={() => revert.mutate(agent.id)}
          >
            <Undo2 size={14} aria-hidden="true" />
            Revert to {update.previousVersion}
          </button>
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
