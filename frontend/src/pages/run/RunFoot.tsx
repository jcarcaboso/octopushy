import { useId, useState } from "react";
import { Link } from "react-router";
import type { RunDetail } from "../../api/types";
import { KeyValue } from "../../components/KeyValue";
import { LabelList } from "../../components/LabelEditor";
import { Commit } from "../../components/Mono";
import { PolicyView } from "../../components/PolicyView";
import { RelativeTime } from "../../components/RelativeTime";
import { useNow } from "../../components/useNow";
import { humanize, operationLabel, reasonLabel, runResourcePath } from "../../lib/format";
import { elapsed, formatDuration } from "../../lib/time";

function RunFacts({ detail }: { detail: RunDetail }) {
  const run = detail.run;
  const now = useNow();
  const duration = elapsed(run.startedAt, run.finishedAt, now);
  return (
    <KeyValue
      items={[
        {
          term: humanize(run.resourceKind),
          value: (
            <Link to={runResourcePath(run)} className="mono break">
              {run.address}
            </Link>
          ),
        },
        { term: "Operation", value: operationLabel(run.operation) },
        {
          term: "Reason",
          value: run.reasonDetail ? (
            <span className="stack stack--small">
              <span>{reasonLabel(run.reason)}</span>
              <span className="muted">{run.reasonDetail}</span>
            </span>
          ) : (
            reasonLabel(run.reason)
          ),
        },
        { term: "Commit", value: <Commit commit={run.commit} length={10} /> },
        ...(detail.baselineCommit
          ? [{ term: "Compared with", value: <Commit commit={detail.baselineCommit} length={10} /> }]
          : []),
        ...(detail.workflow
          ? [
              {
                term: "Workflow",
                value: (
                  <span className="stack stack--small">
                    <span title={`Named as ${detail.workflow.reference}`}>
                      <span className="mono">
                        {detail.workflow.id}@{detail.workflow.version}
                      </span>{" "}
                      at <Commit commit={detail.workflow.commit} length={10} />
                    </span>
                    <span className="muted small break">{detail.workflow.repository ?? "from this repository"}</span>
                  </span>
                ),
              },
            ]
          : []),
        { term: "Created", value: <RelativeTime value={run.createdAt} /> },
        { term: "Started", value: <RelativeTime value={run.startedAt} empty="Not yet" /> },
        { term: "Finished", value: <RelativeTime value={run.finishedAt} empty="Not yet" /> },
        {
          term: "Duration",
          value: duration === null ? <span className="muted">—</span> : <span className="num">{formatDuration(duration)}</span>,
        },
      ]}
    />
  );
}

/** The agent that took the run's latest attempt. */
function lastAgent(detail: RunDetail): string | null {
  const attempts = detail.steps.flatMap((s) => s.attempts).filter((a) => a.agentName);
  attempts.sort((a, b) => Date.parse(a.createdAt) - Date.parse(b.createdAt));
  return attempts.at(-1)?.agentName ?? null;
}

/** One quiet line of facts, and everything else about the run behind "All details". */
export function RunFoot({ detail }: { detail: RunDetail }) {
  const [open, setOpen] = useState(false);
  const panelId = useId();
  const agent = lastAgent(detail);
  const parts = [
    <span key="commit" className="inline-group">
      commit <Commit commit={detail.run.commit} />
    </span>,
    detail.workflow ? (
      <span key="workflow" className="mono">
        {detail.workflow.id}@{detail.workflow.version}
      </span>
    ) : null,
    agent ? (
      <span key="agent">
        agent <span className="mono">{agent}</span>
      </span>
    ) : null,
    detail.policy ? <span key="policy">approval {detail.policy.approval}</span> : null,
  ].filter(Boolean);
  return (
    <footer className="run-foot">
      <div className="run-foot__line">
        {parts.map((part, i) => (
          <span key={i} className="run-foot__part">
            {i > 0 ? <span aria-hidden="true">·</span> : null}
            {part}
          </span>
        ))}
        <button type="button" className="link-button run-foot__toggle" aria-expanded={open} aria-controls={panelId} onClick={() => setOpen((on) => !on)}>
          {open ? "Hide details" : "All details"}
        </button>
      </div>
      {open ? (
        <div id={panelId} className="run-foot__details" role="region" aria-label="All details">
          <section>
            <h3 className="run-foot__heading">Run</h3>
            <RunFacts detail={detail} />
          </section>
          {detail.policy ? (
            <section>
              <h3 className="run-foot__heading">Policy</h3>
              <PolicyView policy={detail.policy} />
            </section>
          ) : null}
          <section>
            <h3 className="run-foot__heading">Agent labels</h3>
            <div className="stack stack--small">
              <LabelList labels={detail.agentLabels} empty="Any agent" />
              {detail.agentLabelsSetBy ? <span className="muted small">set by {detail.agentLabelsSetBy}</span> : null}
            </div>
          </section>
        </div>
      ) : null}
    </footer>
  );
}
