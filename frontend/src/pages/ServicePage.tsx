import { Rocket } from "lucide-react";
import { useState } from "react";
import { Link, useNavigate, useParams } from "react-router";
import { isNotFound } from "../api/client";
import { useDeployService, useService } from "../api/queries";
import { StatusBadge } from "../components/Badge";
import { Callout } from "../components/Callout";
import { Card } from "../components/Card";
import { DefinitionView } from "../components/DefinitionView";
import { IssueList } from "../components/IssueList";
import { KeyValue } from "../components/KeyValue";
import { LabelList } from "../components/LabelEditor";
import { Commit } from "../components/Mono";
import { PageHeader } from "../components/PageHeader";
import { PolicyView } from "../components/PolicyView";
import { RelativeTime } from "../components/RelativeTime";
import { RunTable } from "../components/RunTable";
import { ServiceIcon } from "../components/ServiceIcon";
import { WorkflowLabel } from "../components/WorkflowLabel";
import { ChangeActions, RecentChanges } from "../components/ChangeActions";
import { ErrorMessage, NotFound, PageSkeleton } from "../components/States";
import { IconDialog } from "./services/IconDialog";

/** One service: its status, what it waits for, its stages, and its history. Its definition is read from the repository. */
export function ServicePage() {
  const { id = "" } = useParams();
  const { data, error, isPending } = useService(id);
  const deploy = useDeployService(id);
  const navigate = useNavigate();
  const [changingIcon, setChangingIcon] = useState(false);

  if (isPending) return <PageSkeleton rows={4} />;
  if (!data) return isNotFound(error) ? <NotFound what="service" /> : <ErrorMessage error={error} title="The service could not be loaded" />;

  const { service } = data;
  const missing = Boolean(data.missingSince && !data.retiredAt);

  return (
    <>
      <PageHeader
        eyebrow={
          <>
            <Link to="/inventory">Inventory</Link> /{" "}
            {data.guestId ? <Link to={`/guests/${data.guestId}`}>{data.guestAddress}</Link> : data.guestAddress} / Service
          </>
        }
        title={
          <span className="service-heading">
            <ServiceIcon service={service} large />
            <span className="mono">{service.address}</span>
          </span>
        }
        documentTitle={service.name}
        meta={
          <span className="inline-group">
            <StatusBadge status={service.status} />
            {service.statusDetail ? <span>{service.statusDetail}</span> : null}
            {service.activeRun ? <Link to={`/runs/${service.activeRun.id}`}>Run #{service.activeRun.number}</Link> : null}
            <button type="button" className="link-button" onClick={() => setChangingIcon(true)}>
              Change icon
            </button>
          </span>
        }
        actions={
          missing || data.retiredAt ? undefined : (
            <>
              <ChangeActions resourceId={service.id} kind="service" />
              <button
                type="button"
                className="button button--primary"
                onClick={() => deploy.mutate(undefined, { onSuccess: (started) => navigate(`/runs/${started.runId}`) })}
                disabled={deploy.isPending}
              >
                <Rocket size={15} aria-hidden="true" />
                {deploy.isPending ? "Starting…" : "Deploy"}
              </button>
            </>
          )
        }
      />
      {changingIcon ? <IconDialog service={service} onClose={() => setChangingIcon(false)} /> : null}
      <div className="stack stack--large">
        <ErrorMessage error={deploy.error} />
        {error ? <ErrorMessage error={error} title="Showing the last data; refreshing failed" /> : null}
        {missing ? (
          <Callout kind="warning" title={<>Folder deleted <RelativeTime value={data.missingSince} /></>}>
            Nothing was removed, and nothing will be: removing a service is a later step. What it deployed is still on
            the guest. Restore the folder to keep managing it.
          </Callout>
        ) : null}
        <div className="columns columns--main-side">
          <div className="stack stack--large">
            <Card title="Definition" id="definition">
              <DefinitionView file={data.definitionFile} text={data.definitionText} issues={data.issues} />
            </Card>
            <Card title="Issues" meta={data.issues.length || undefined} id="issues">
              <IssueList issues={data.issues} />
            </Card>
            <RecentChanges resourceId={service.id} />
            <Card title="Runs" flush id="runs">
              <RunTable runs={data.runs} label={`Runs of ${service.name}`} showAddress={false} />
            </Card>
          </div>
          <div className="stack stack--large">
            <Card title="Workflow">
              <div className="stack stack--small">
                <WorkflowLabel workflow={service.workflow} />
                {data.stages.length === 0 ? (
                  <p className="muted">The repository shows no stages for this service now.</p>
                ) : (
                  <KeyValue
                    items={data.stages.map((stage) => ({
                      term: stage.name,
                      value: <span className="mono small">{stage.executor}</span>,
                    }))}
                  />
                )}
              </div>
            </Card>
            <Card title="Last success">
              {data.appliedAt ? (
                <span>
                  <Commit commit={data.appliedCommit} /> <RelativeTime value={data.appliedAt} />
                </span>
              ) : (
                <p className="muted">Not deployed yet.</p>
              )}
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
