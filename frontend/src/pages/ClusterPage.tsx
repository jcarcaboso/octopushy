import { Play, ShieldCheck } from "lucide-react";
import { Link, useNavigate, useParams } from "react-router";
import { isNotFound } from "../api/client";
import { useCluster, usePlanCluster, useTrustCluster } from "../api/queries";
import type { ClusterDetail, ClusterSummary } from "../api/types";
import { Badge, StatusBadge } from "../components/Badge";
import { Callout } from "../components/Callout";
import { Card } from "../components/Card";
import { DefinitionView } from "../components/DefinitionView";
import { IssueList } from "../components/IssueList";
import { KeyValue } from "../components/KeyValue";
import { Commit } from "../components/Mono";
import { PageHeader } from "../components/PageHeader";
import { PolicyView } from "../components/PolicyView";
import { RelativeTime } from "../components/RelativeTime";
import { RunTable } from "../components/RunTable";
import { EmptyRow, Table } from "../components/Table";
import { ErrorMessage, NotFound, PageSkeleton } from "../components/States";
import { useHashScroll } from "../components/useHashScroll";
import { humanize } from "../lib/format";

/** Asks for trust while the admin password would go somewhere new, or to code nobody has seen. */
function TrustCallout({ cluster }: { cluster: ClusterSummary }) {
  const trust = useTrustCluster();
  const { endpoints, untrusted, codeChanged, codeDigest, trustedAt } = cluster.trust;
  if (!cluster.valid || !codeDigest || (untrusted.length === 0 && !codeChanged)) return null;
  return (
    <div id="trust">
      <Callout
        kind="warning"
        title={!trustedAt ? "New application cluster" : untrusted.length > 0 ? "New member endpoint" : "Recipe code changed"}
        actions={
          <button
            type="button"
            className="button button--primary"
            disabled={trust.isPending}
            onClick={() => trust.mutate({ id: cluster.id, endpoints, codeDigest })}
          >
            <ShieldCheck size={15} aria-hidden="true" />
            {trust.isPending ? "Trusting…" : "Trust cluster"}
          </button>
        }
      >
        {untrusted.length > 0 ? (
          <p>
            The cluster's admin password is sent only to trusted endpoints. Runs wait until you trust{" "}
            {untrusted.map((endpoint, i) => (
              <span key={endpoint}>
                {i > 0 ? ", " : null}
                <span className="mono break">{endpoint}</span>
              </span>
            ))}
            .
          </p>
        ) : null}
        {codeChanged || !trustedAt ? (
          <p>
            The recipe's OpenTofu code runs with the admin password before anyone approves a plan, so trust covers it too. Review it
            first: <span className="mono break">{codeDigest}</span>
          </p>
        ) : null}
        <ErrorMessage error={trust.error} />
      </Callout>
    </div>
  );
}

function Members({ cluster, verification }: { cluster: ClusterSummary; verification: ClusterDetail["verification"] }) {
  const reports = (verification as { members?: { guest: string; ok: boolean; version?: string; recovery?: string; checks?: { name: string; ok: boolean; detail: string }[] }[] } | null)?.members ?? [];
  return (
    <Table label="Members">
      <thead>
        <tr>
          <th scope="col">Guest</th>
          <th scope="col">Role</th>
          <th scope="col">Address</th>
          <th scope="col">Guest status</th>
          <th scope="col">Last check</th>
        </tr>
      </thead>
      <tbody>
        {cluster.members.length === 0 ? <EmptyRow colSpan={5}>No members declared.</EmptyRow> : null}
        {cluster.members.map((member) => {
          const report = reports.find((r) => r.guest === member.guest);
          const failed = report?.checks?.filter((c) => !c.ok) ?? [];
          return (
            <tr key={member.guest}>
              <td className="mono">
                {member.guestId ? <Link to={`/guests/${member.guestId}`}>{member.guest}</Link> : member.guest}
              </td>
              <td>{humanize(member.role)}</td>
              <td className="mono">{member.address ?? <span className="muted">—</span>}</td>
              <td>{member.guestStatus ? <StatusBadge status={member.guestStatus} /> : <span className="muted">—</span>}</td>
              <td>
                {!report ? (
                  <span className="muted">Not checked yet</span>
                ) : report.ok ? (
                  <Badge tone="success">Passed</Badge>
                ) : (
                  <>
                    <Badge tone="danger">Failed</Badge>
                    {failed.map((check) => (
                      <div className="small" key={check.name + check.detail}>
                        {check.detail}
                      </div>
                    ))}
                    {report.recovery ? <div className="small muted">{report.recovery}</div> : null}
                  </>
                )}
              </td>
            </tr>
          );
        })}
      </tbody>
    </Table>
  );
}

export function ClusterPage() {
  const { id = "" } = useParams();
  const { data, error, isPending } = useCluster(id);
  const plan = usePlanCluster();
  const navigate = useNavigate();
  useHashScroll(Boolean(data));

  if (isPending) return <PageSkeleton rows={4} />;
  if (!data) return isNotFound(error) ? <NotFound what="cluster" /> : <ErrorMessage error={error} title="The cluster could not be loaded" />;

  const { cluster } = data;
  return (
    <>
      <PageHeader
        eyebrow={
          <>
            <Link to="/inventory">Inventory</Link> / Application cluster
          </>
        }
        title={<span className="mono">{cluster.address}</span>}
        documentTitle={cluster.name}
        meta={
          <span className="inline-group">
            <StatusBadge status={cluster.status} />
            {cluster.statusDetail ? <span>{cluster.statusDetail}</span> : null}
            {cluster.activeRun ? <Link to={`/runs/${cluster.activeRun.id}`}>Run #{cluster.activeRun.number}</Link> : null}
          </span>
        }
        actions={
          <button
            type="button"
            className="button button--primary"
            onClick={() => plan.mutate(id, { onSuccess: (started) => navigate(`/runs/${started.runId}`) })}
            disabled={plan.isPending || Boolean(data.missingSince)}
          >
            <Play size={15} aria-hidden="true" />
            {plan.isPending ? "Starting…" : "Plan now"}
          </button>
        }
      />
      <div className="stack stack--large">
        <TrustCallout cluster={cluster} />
        <ErrorMessage error={plan.error} />
        {cluster.observeError ? (
          <Callout kind="danger" title="The last background check failed">
            {cluster.observeError}
          </Callout>
        ) : null}
        {data.issues.length > 0 ? (
          <Card title="Issues" id="issues">
            <IssueList issues={data.issues} />
          </Card>
        ) : null}
        <div className="columns columns--main-side">
          <div className="stack stack--large">
            <Card title="Members" meta={cluster.members.length} flush>
              <Members cluster={cluster} verification={data.verification} />
            </Card>
            <Card title="Runs" flush>
              <RunTable runs={data.runs} label={`Runs of ${cluster.name}`} showAddress={false} />
            </Card>
            <Card title="Definition" id="definition">
              <DefinitionView file={data.definitionFile} text={data.definitionText} issues={data.issues} />
            </Card>
          </div>
          <div className="stack stack--large">
            <Card title="State">
              <KeyValue
                items={[
                  { term: "Domain", value: <span className="mono">{cluster.clusterDomain ?? "—"}</span> },
                  { term: "Desired", value: data.desiredCommit ? <Commit commit={data.desiredCommit} /> : "—" },
                  { term: "Applied", value: data.appliedCommit ? <Commit commit={data.appliedCommit} /> : "Not yet" },
                  { term: "Verified", value: cluster.verifiedAt ? <RelativeTime value={cluster.verifiedAt} /> : "Not yet" },
                  { term: "Checked in the background", value: cluster.observedAt ? <RelativeTime value={cluster.observedAt} /> : "Not yet" },
                  {
                    term: "Trust",
                    value: cluster.trust.trustedAt ? (
                      <>
                        {cluster.trust.untrusted.length > 0 || cluster.trust.codeChanged ? (
                          <Badge tone="warning">Changed</Badge>
                        ) : (
                          <Badge tone="success">Trusted</Badge>
                        )}{" "}
                        by {cluster.trust.trustedBy} · <RelativeTime value={cluster.trust.trustedAt} />
                      </>
                    ) : (
                      <Badge tone="warning">Not trusted</Badge>
                    ),
                  },
                ]}
              />
            </Card>
            <Card title="Policy">
              <PolicyView policy={data.policy} />
            </Card>
          </div>
        </div>
      </div>
    </>
  );
}
