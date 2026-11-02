import { Wrench } from "lucide-react";
import { Link, useNavigate, useParams } from "react-router";
import { isNotFound } from "../api/client";
import { useForgetService, useRunService, useService } from "../api/queries";
import { Badge, StatusBadge } from "../components/Badge";
import { Callout } from "../components/Callout";
import { Card } from "../components/Card";
import { ConfigureTrust } from "../components/ConfigureTrust";
import { DefinitionView } from "../components/DefinitionView";
import { IssueList } from "../components/IssueList";
import { KeyValue } from "../components/KeyValue";
import { LabelList } from "../components/LabelEditor";
import { Commit } from "../components/Mono";
import { PageHeader } from "../components/PageHeader";
import { PolicyView } from "../components/PolicyView";
import { RelativeTime } from "../components/RelativeTime";
import { RunTable } from "../components/RunTable";
import { ErrorMessage, NotFound, PageSkeleton } from "../components/States";
import { portLabel, type ServicePort } from "../lib/format";

/** One service: its own status, what it waits for, what can be run, and its history. */
export function ServicePage() {
  const { id = "" } = useParams();
  const { data, error, isPending } = useService(id);
  const run = useRunService(id);
  const forget = useForgetService(id);
  const navigate = useNavigate();

  if (isPending) return <PageSkeleton rows={4} />;
  if (!data) return isNotFound(error) ? <NotFound what="service" /> : <ErrorMessage error={error} title="The service could not be loaded" />;

  const { service } = data;
  const ports = (service.ports ?? []) as unknown as ServicePort[];
  const missing = data.missingSince && !data.retiredAt;

  return (
    <>
      <PageHeader
        eyebrow={
          <>
            <Link to="/inventory">Inventory</Link> /{" "}
            {data.guestId ? <Link to={`/guests/${data.guestId}`}>{data.guestAddress}</Link> : data.guestAddress} / Service
          </>
        }
        title={<span className="mono">{service.address}</span>}
        documentTitle={service.name}
        meta={<StatusBadge status={service.status} />}
      />
      <div className="stack stack--large">
        {error ? <ErrorMessage error={error} title="Showing the last data; refreshing failed" /> : null}
        {service.statusDetail ? <p className="muted">{service.statusDetail}</p> : null}
        {missing ? (
          <Callout
            kind="warning"
            title={<>Folder deleted <RelativeTime value={data.missingSince} /></>}
            actions={
              service.activeRun ? undefined : (
                <>
                  {data.hasRemoval ? (
                    <button
                      type="button"
                      className="button"
                      disabled={run.isPending}
                      onClick={() => run.mutate("", { onSuccess: (started) => navigate(`/runs/${started.runId}`) })}
                    >
                      Run removal
                    </button>
                  ) : null}
                  <button type="button" className="button" disabled={forget.isPending} onClick={() => forget.mutate()}>
                    Forget
                  </button>
                </>
              )
            }
          >
            {data.hasRemoval
              ? "Octopushy proposes the recipe's removal action, from the definition last applied. It waits for a person unless destructive is set to automatic explicitly. Run removal proposes it again after it was rejected or failed."
              : "The recipe declares no removal, so nothing runs: the service keeps running on the guest, and its data stays."}{" "}
            Forget stops tracking it and changes nothing on the guest. Restore the folder to keep it.
            <ErrorMessage error={run.error} />
            <ErrorMessage error={forget.error} />
          </Callout>
        ) : null}
        <ConfigureTrust id={service.id} trust={data.trust} what="This service" />
        <div className="columns columns--main-side">
          <div className="stack stack--large">
            <Card title="Definition" id="definition">
              <DefinitionView file={data.definitionFile} text={data.definitionText} issues={data.issues} />
            </Card>
            <Card title="Issues" meta={data.issues.length || undefined} id="issues">
              <IssueList issues={data.issues} />
            </Card>
            <Card title="Runs" flush id="runs">
              <RunTable runs={data.runs} label={`Runs of ${service.name}`} showAddress={false} />
            </Card>
          </div>
          <div className="stack stack--large">
            <Card title="Deployment">
              <div className="stack stack--small">
                <KeyValue
                  items={[
                    { term: "Recipe", value: <span className="mono">{service.recipe ?? "—"}</span> },
                    {
                      term: "Last success",
                      value: data.appliedAt ? (
                        <span>
                          <span className="mono">{data.appliedAction}</span> <Commit commit={data.appliedCommit} /> <RelativeTime value={data.appliedAt} />
                        </span>
                      ) : (
                        "Never"
                      ),
                    },
                    {
                      term: "Ports",
                      value: ports.length ? <span className="mono small">{ports.map(portLabel).join("; ")}</span> : "None declared",
                    },
                  ]}
                />
                <ErrorMessage error={run.error} />
                {missing ? null : (
                  <div className="button-row">
                    {data.actions.map((action) => (
                      <button
                        key={action.id}
                        type="button"
                        className="button"
                        title={action.description ?? undefined}
                        disabled={run.isPending}
                        onClick={() => run.mutate(action.id, { onSuccess: (started) => navigate(`/runs/${started.runId}`) })}
                      >
                        <Wrench size={15} aria-hidden="true" />
                        {action.id}
                        {action.destructive ? <Badge tone="warning">Destructive</Badge> : null}
                      </button>
                    ))}
                  </div>
                )}
              </div>
            </Card>
            <Card title="Policy">
              <PolicyView policy={data.policy} />
            </Card>
            <Card title="Agent labels">
              <div className="stack stack--small">
                <LabelList labels={data.agentLabels} empty="Any agent" />
                {data.agentLabelsSetBy ? <span className="muted small">set by {data.agentLabelsSetBy}</span> : null}
              </div>
            </Card>
          </div>
        </div>
      </div>
    </>
  );
}
