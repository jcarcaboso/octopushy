import { Check, ChevronRight } from "lucide-react";
import type { WorkflowsSource as Source, WorkflowReleases, WorkflowUse } from "../api/types";
import { plural } from "../lib/format";
import { Badge } from "./Badge";
import { Callout } from "./Callout";
import { Commit } from "./Mono";
import { RelativeTime } from "./RelativeTime";

const idOf = (reference: string) => reference.split("@")[0];

/** What a guest or service's reference resolved to, and a newer major when there is one: offered as text, never taken. */
function Resolution({ use }: { use: WorkflowUse }) {
  const id = idOf(use.reference);
  return (
    <span className="flow__resolved">
      {use.version ? (
        <span>
          <span className="mono">
            {id}@{use.version}
          </span>
          {use.reference !== `${id}@${use.version}` ? <span className="muted small"> from {use.reference.slice(id.length)}</span> : null}
        </span>
      ) : (
        <span>
          <span className="mono">{use.reference}</span> <span className="muted small">not resolved</span>
        </span>
      )}
      {use.problem ? <span className="muted small">{use.problem}</span> : null}
      {use.newerMajor ? (
        <Badge tone="warning">
          {id} {use.newerMajor} available, breaking
        </Badge>
      ) : null}
    </span>
  );
}

function Uses({ uses, label }: { uses: WorkflowUse[]; label: string }) {
  return (
    <ul className="flow__list" aria-label={label}>
      {uses.map((use) => (
        <li key={use.address} className="flow__line">
          <span>
            <span className="mono break">{use.address}</span> <span className="muted small">{use.kind}</span>
          </span>
          <Resolution use={use} />
          <Commit commit={use.commit} length={10} />
        </li>
      ))}
    </ul>
  );
}

function usedBy(uses: WorkflowUse[]): string {
  const guests = uses.filter((use) => use.kind === "guest").length;
  const others = uses.length - guests;
  return [guests ? plural(guests, "guest") : null, others ? plural(others, "service") : null].filter(Boolean).join(", ");
}

/** One workflow as a line: its latest release, the older ones and who uses it. Its releases and references open under it. */
function Workflow({ workflow, uses, external }: { workflow: WorkflowReleases; uses: WorkflowUse[]; external: boolean }) {
  const [latest, ...older] = workflow.releases;
  const troubled = workflow.releases.some((r) => !r.usable || r.movedTo || r.issue) || uses.some((use) => use.problem);
  return (
    <details className="flow">
      <summary className="flow__summary">
        <ChevronRight className="flow__chevron" size={14} aria-hidden="true" />
        <code className="mono">{workflow.id}</code>
        <span>{latest ? <code className="run-chip run-chip--mono">{latest.version}</code> : null}</span>
        <code className="mono muted">{older.map((r) => r.version).join(", ")}</code>
        <span className="muted small">{usedBy(uses) || "Not used"}</span>
        <span>{troubled ? <Badge tone="warning">Has issues</Badge> : null}</span>
      </summary>
      <div className="flow__body">
        <h3 className="flow__heading">Releases</h3>
        <ul className="flow__list" aria-label={`Releases of ${workflow.id}`}>
          {workflow.releases.map((release) => (
            <li key={release.version} className="flow__line">
              <span className="mono num">{release.version}</span>
              <span className="flow__resolved">
                <span className="inline-group">
                  {release.usable ? <Badge tone="success">Usable</Badge> : <Badge tone="danger">Not used</Badge>}
                  {release.movedTo ? <Badge tone="warning">Tag moved</Badge> : null}
                  {release.tagDeleted ? <Badge tone="neutral">Tag deleted</Badge> : null}
                  {external ? (
                    <span className="muted small">
                      first seen <RelativeTime value={release.firstSeenAt} />
                    </span>
                  ) : null}
                </span>
                {release.issue ? <span className="muted small">{release.issue}</span> : null}
              </span>
              <Commit commit={release.commit} length={10} />
            </li>
          ))}
        </ul>
        {uses.length > 0 ? (
          <>
            <h3 className="flow__heading">Used by</h3>
            <Uses uses={uses} label={`What uses ${workflow.id}`} />
          </>
        ) : null}
      </div>
    </details>
  );
}

/**
 * Where the head revision's workflows come from, one line per workflow. What is wrong is said up front; the releases of a
 * workflow, the commit each tag recorded and what each guest and service resolved to open under its line.
 */
export function WorkflowsSource({ workflows }: { workflows: Source }) {
  const external = workflows.source === "repository";
  const known = new Set(workflows.workflows.map((w) => w.id));
  const unknown = workflows.uses.filter((use) => !known.has(idOf(use.reference)));
  const healthy =
    !workflows.lastFetchError &&
    workflows.issues.length === 0 &&
    workflows.uses.every((use) => !use.problem) &&
    workflows.workflows.every((w) => w.releases.every((r) => r.usable));
  return (
    <div className="stack">
      {workflows.lastFetchError ? (
        <Callout kind="danger" title="The last fetch of the workflows repository failed">
          <span className="mono small break">{workflows.lastFetchError}</span>
          <p className="small">The releases already recorded are still used.</p>
        </Callout>
      ) : null}
      {workflows.issues.length > 0 ? (
        <Callout kind="warning" title="Releases with issues">
          <ul>
            {workflows.issues.map((issue) => (
              <li key={issue} className="small">
                {issue}
              </li>
            ))}
          </ul>
        </Callout>
      ) : null}
      <div>
        <div className="flows">
          <div className="flows__inner">
            <div className="flows__head" aria-hidden="true">
              <span />
              <span>Workflow</span>
              <span>Latest</span>
              <span>Older releases</span>
              <span>Used by</span>
              <span />
            </div>
            {workflows.workflows.length === 0 ? (
              <p className="flows__empty muted">{external ? "No release tags <id>/v<M>.<m> yet." : "No workflows."}</p>
            ) : null}
            {workflows.workflows.map((workflow) => (
              <Workflow
                key={workflow.id}
                workflow={workflow}
                uses={workflows.uses.filter((use) => idOf(use.reference) === workflow.id)}
                external={external}
              />
            ))}
            {unknown.length > 0 ? (
              <div className="flow__body flow__body--loose">
                <h3 className="flow__heading">References to workflows that have no release</h3>
                <Uses uses={unknown} label="References to workflows that have no release" />
              </div>
            ) : null}
          </div>
        </div>
        <div className="flows__foot">
          {healthy ? (
            <span className="valid-mark">
              <Check size={14} aria-hidden="true" />
              All usable
            </span>
          ) : null}
          {external ? (
            <>
              <span>
                From <span className="mono break">{workflows.repository}</span>
              </span>
              <span>
                Checked <RelativeTime value={workflows.lastFetchAt} empty="not yet" />
              </span>
            </>
          ) : (
            <span>
              From this repository&apos;s <span className="mono">workflows/</span>
            </span>
          )}
        </div>
      </div>
    </div>
  );
}
