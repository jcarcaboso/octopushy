import type { WorkflowsSource as Source, WorkflowUse } from "../api/types";
import { Badge } from "./Badge";
import { Callout } from "./Callout";
import { KeyValue } from "./KeyValue";
import { Commit } from "./Mono";
import { RelativeTime } from "./RelativeTime";
import { EmptyRow, Table } from "./Table";

/** What a guest or service's reference resolved to, and a newer major when there is one: offered as text, never taken. */
function Resolution({ use }: { use: WorkflowUse }) {
  const id = use.reference.slice(0, use.reference.indexOf("@"));
  return (
    <div className="stack stack--small">
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
        <span className="small">
          <Badge tone="warning">
            {id} {use.newerMajor} available, breaking
          </Badge>
        </span>
      ) : null}
    </div>
  );
}

/**
 * Where the head revision's workflows come from. For a workflows repository: its last fetch, the releases of each workflow
 * with the commit each tag recorded, what is wrong with any of them, and what each guest and service resolved to.
 */
export function WorkflowsSource({ workflows }: { workflows: Source }) {
  const external = workflows.source === "repository";
  return (
    <div className="stack">
      {workflows.lastFetchError ? (
        <Callout kind="danger" title="The last fetch of the workflows repository failed">
          <span className="mono small break">{workflows.lastFetchError}</span>
          <p className="small">The releases already recorded are still used.</p>
        </Callout>
      ) : null}
      <KeyValue
        items={[
          {
            term: "Source",
            value: external ? (
              <span className="mono break">{workflows.repository}</span>
            ) : (
              <span>
                This repository&apos;s <span className="mono">workflows/</span>
              </span>
            ),
          },
          ...(external
            ? [{ term: "Last fetch", value: <RelativeTime value={workflows.lastFetchAt} empty="Not fetched yet" /> }]
            : []),
        ]}
      />
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
      <Table label="Workflow releases">
        <thead>
          <tr>
            <th scope="col">Workflow</th>
            <th scope="col">Version</th>
            <th scope="col">Commit</th>
            {external ? <th scope="col">First seen</th> : null}
            <th scope="col">State</th>
          </tr>
        </thead>
        <tbody>
          {workflows.workflows.length === 0 ? (
            <EmptyRow colSpan={external ? 5 : 4}>{external ? "No release tags <id>/v<M>.<m> yet." : "No workflows."}</EmptyRow>
          ) : null}
          {workflows.workflows.flatMap((workflow) =>
            workflow.releases.map((release, index) => (
              <tr key={`${workflow.id}@${release.version}`}>
                <td className="mono">{index === 0 ? workflow.id : ""}</td>
                <td className="mono num">{release.version}</td>
                <td>
                  <Commit commit={release.commit} length={10} />
                </td>
                {external ? (
                  <td className="nowrap">
                    <RelativeTime value={release.firstSeenAt} />
                  </td>
                ) : null}
                <td>
                  <div className="stack stack--small">
                    <span className="inline-group">
                      {release.usable ? <Badge tone="success">Usable</Badge> : <Badge tone="danger">Not used</Badge>}
                      {release.movedTo ? <Badge tone="warning">Tag moved</Badge> : null}
                      {release.tagDeleted ? <Badge tone="neutral">Tag deleted</Badge> : null}
                    </span>
                    {release.issue ? <span className="muted small">{release.issue}</span> : null}
                  </div>
                </td>
              </tr>
            )),
          )}
        </tbody>
      </Table>
      <h3 className="subhead">Guests and services</h3>
      <Table label="What each reference resolved to">
        <thead>
          <tr>
            <th scope="col">Resource</th>
            <th scope="col">Resolved</th>
            <th scope="col">Commit</th>
          </tr>
        </thead>
        <tbody>
          {workflows.uses.length === 0 ? <EmptyRow colSpan={3}>No guest or service names a workflow.</EmptyRow> : null}
          {workflows.uses.map((use) => (
            <tr key={use.address}>
              <td>
                <span className="mono break">{use.address}</span> <span className="muted small">{use.kind}</span>
              </td>
              <td>
                <Resolution use={use} />
              </td>
              <td>
                <Commit commit={use.commit} length={10} />
              </td>
            </tr>
          ))}
        </tbody>
      </Table>
    </div>
  );
}
