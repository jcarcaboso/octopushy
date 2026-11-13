import { Rocket } from "lucide-react";
import { useState } from "react";
import { Link, useNavigate, useParams } from "react-router";
import { isNotFound } from "../api/client";
import { useDeployService, useService, useUptime } from "../api/queries";
import type { ServiceDetail } from "../api/types";
import { StatusBadge } from "../components/Badge";
import { Callout } from "../components/Callout";
import { Card } from "../components/Card";
import { DefinitionSheet, ViewYamlButton } from "../components/DefinitionSheet";
import { Glance } from "../components/Glance";
import { IssueList } from "../components/IssueList";
import { KeyValue } from "../components/KeyValue";
import { Commit } from "../components/Mono";
import { PageHeader } from "../components/PageHeader";
import { PolicyCard } from "../components/PolicyView";
import { RelativeTime } from "../components/RelativeTime";
import { RunTable } from "../components/RunTable";
import { ServiceIcon } from "../components/ServiceIcon";
import { WorkflowLabel } from "../components/WorkflowLabel";
import { ChangeActions, RecentChanges } from "../components/ChangeActions";
import { ErrorMessage, NotFound, PageSkeleton } from "../components/States";
import { formatPercent, isServiceCheck, percentOf } from "../lib/inventory";
import { WaitingRun } from "./run/WaitingRun";
import { IconDialog } from "./services/IconDialog";

/** The service's figures: how its first link answers, its month, where it runs, its workflow and its last deploy. */
function ServiceGlance({ data }: { data: ServiceDetail }) {
  const { data: uptime } = useUptime();
  const { service } = data;
  const checks = uptime?.checks.filter((c) => isServiceCheck(c) && c.resource === service.address) ?? [];
  const first = checks.find((c) => c.url === service.links[0]) ?? checks[0];
  const month = percentOf(checks, "month");
  const guestName = data.guestAddress?.split("/").pop();
  return (
    <Glance
      cells={[
        first
          ? {
              label: "Answers",
              value: first.status === "up" ? "Up" : first.status === "down" ? "Down" : "Not checked yet",
              note: first.status === "up" && first.elapsedMs !== null ? `${first.elapsedMs} ms` : first.detail,
            }
          : null,
        month !== null ? { label: "Uptime, 30 days", value: formatPercent(month), note: checks.length > 1 ? `lowest of ${checks.length} links` : null } : null,
        guestName
          ? {
              label: "Runs on",
              value: data.guestId ? <Link to={`/guests/${data.guestId}`} className="mono">{guestName}</Link> : <span className="mono">{guestName}</span>,
              note: data.guestAddress,
            }
          : null,
        service.workflow
          ? { label: "Workflow", value: <span className="mono">{service.workflow.resolved ?? service.workflow.reference}</span>, note: service.workflow.resolved ? `named ${service.workflow.reference}` : "not resolved" }
          : null,
        data.appliedAt ? { label: "Last deployed", value: <RelativeTime value={data.appliedAt} />, note: <Commit commit={data.appliedCommit} /> } : null,
      ]}
    />
  );
}

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
          <>
            {data.definitionText !== null ? <ViewYamlButton file={data.definitionFile} text={data.definitionText} issues={data.issues} /> : null}
            {missing || data.retiredAt ? null : (
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
            )}
          </>
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
        {service.activeRun?.status === "awaiting-approval" ? <WaitingRun run={service.activeRun} /> : null}
        <ServiceGlance data={data} />
        <div className="columns columns--main-side">
          <div className="stack stack--large">
            <DefinitionSheet
              file={data.definitionFile}
              text={data.definitionText}
              kind="service"
              issues={data.issues}
              // A pipeline applies the inputs and the secrets; links, description and policy run nothing.
              applied={data.appliedDefinitionText ? [{ text: data.appliedDefinitionText, sections: ["inputs", "secrets"] }] : []}
            />
            {data.issues.length > 0 ? (
              <Card title="Issues" meta={data.issues.length} id="issues">
                <IssueList issues={data.issues} />
              </Card>
            ) : null}
            <RecentChanges resourceId={service.id} />
            <Card title="Runs" flush id="runs">
              <RunTable runs={data.runs} label={`Runs of ${service.name}`} showAddress={false} />
            </Card>
          </div>
          <div className="stack stack--large">
            <Card title="Workflow">
              <div className="stack">
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
                <KeyValue
                  items={[
                    {
                      term: "Last success",
                      value: data.appliedAt ? (
                        <span>
                          <Commit commit={data.appliedCommit} /> <RelativeTime value={data.appliedAt} />
                        </span>
                      ) : (
                        <span className="muted">Not deployed yet.</span>
                      ),
                    },
                  ]}
                />
              </div>
            </Card>
            <PolicyCard policy={data.policy} agentLabels={data.agentLabels} agentLabelsSetBy={data.agentLabelsSetBy} />
          </div>
        </div>
      </div>
    </>
  );
}
