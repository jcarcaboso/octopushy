import { Eye } from "lucide-react";
import { useState } from "react";
import { isNotFound } from "../api/client";
import { Link, useParams } from "react-router";
import { useInstallation, useObserveInstallation } from "../api/queries";
import type { ObservedGuest } from "../api/types";
import { Badge, StatusBadge } from "../components/Badge";
import { Callout } from "../components/Callout";
import { Card } from "../components/Card";
import { DefinitionView } from "../components/DefinitionView";
import { GuestTable } from "../components/GuestTable";
import { ObserveError } from "../components/ObserveError";
import { HostTable } from "../components/HostTable";
import { IssueList } from "../components/IssueList";
import { KeyValue } from "../components/KeyValue";
import { LabelList } from "../components/LabelEditor";
import { PageHeader } from "../components/PageHeader";
import { RelativeTime } from "../components/RelativeTime";
import { RunTable } from "../components/RunTable";
import { EmptyRow, Table } from "../components/Table";
import { ErrorMessage, NotFound, PageSkeleton } from "../components/States";
import { useHashScroll } from "../components/useHashScroll";
import { humanize, plural } from "../lib/format";

function ObservedVms({ vms, observed }: { vms: ObservedGuest[]; observed: boolean }) {
  return (
    <Table label="Observed VMs" className="table--observed">
      <thead>
        <tr>
          <th scope="col">VMID</th>
          <th scope="col">Name</th>
          <th scope="col">Host</th>
          <th scope="col">Power</th>
          <th scope="col">Tags</th>
          <th scope="col">Managed by</th>
        </tr>
      </thead>
      <tbody>
        {vms.length === 0 ? (
          <EmptyRow colSpan={6}>{observed ? "Proxmox reported no VMs." : "Not observed yet. Observe the installation to list its VMs."}</EmptyRow>
        ) : null}
        {vms.map((vm) => (
          <tr key={String(vm.vmid)}>
            <td className="num">{vm.vmid}</td>
            <td className="mono">
              {vm.name ?? <span className="muted">—</span>}
              {vm.template ? (
                <>
                  {" "}
                  <Badge tone="neutral">Template</Badge>
                </>
              ) : null}
            </td>
            <td className="mono">{vm.host ?? "—"}</td>
            <td>{vm.status ?? <span className="muted">—</span>}</td>
            <td>
              <LabelList labels={vm.tags} empty="" />
            </td>
            <td>
              {vm.managedById ? (
                <Link to={`/guests/${vm.managedById}`} className="mono">
                  {vm.managedBy}
                </Link>
              ) : vm.template ? (
                <span className="muted">Template</span>
              ) : (
                <Badge tone="neutral">Unmanaged</Badge>
              )}
            </td>
          </tr>
        ))}
      </tbody>
    </Table>
  );
}

export function InstallationPage() {
  const { id = "" } = useParams();
  const { data, error, isPending } = useInstallation(id);
  const observe = useObserveInstallation();
  const [startedRun, setStartedRun] = useState<string | null>(null);
  useHashScroll(Boolean(data));

  if (isPending) return <PageSkeleton rows={4} />;
  if (!data) {
    return isNotFound(error) ? <NotFound what="installation" /> : <ErrorMessage error={error} title="The installation could not be loaded" />;
  }

  const installation = data.installation;
  const unmanaged = installation.unmanagedCount;

  return (
    <>
      <PageHeader
        eyebrow={
          <>
            <Link to="/inventory">Inventory</Link> / Proxmox installation
          </>
        }
        title={installation.name}
        meta={
          <span className="inline-group">
            <span className="mono">{installation.address}</span>
            {installation.valid ? null : <StatusBadge status="invalid" />}
            {installation.observedAt ? (
              <span>
                Observed <RelativeTime value={installation.observedAt} />
              </span>
            ) : (
              <span>Not observed yet</span>
            )}
          </span>
        }
        actions={
          <>
            <Link className="button" to={`/agents/prepare?installation=${encodeURIComponent(installation.address)}`}>
              Prepare worker deployment
            </Link>
            <button
              type="button"
              className="button button--primary"
              disabled={observe.isPending}
              onClick={() => observe.mutate(id, { onSuccess: (started) => setStartedRun(started.runId) })}
            >
              <Eye size={15} aria-hidden="true" />
              {observe.isPending ? "Starting…" : "Observe now"}
            </button>
          </>
        }
      />
      <div className="stack stack--large">
        <ErrorMessage error={observe.error} />
        {startedRun ? (
          <Callout kind="success" title="Observe run started">
            <Link to={`/runs/${startedRun}`}>Follow the run</Link>. It needs a connected agent that can reach the
            Proxmox API.
          </Callout>
        ) : null}
        <ObserveError installation={installation} />
        <div className="columns columns--main-side">
          <div className="stack stack--large">
            <Card title="Hosts" flush>
              <HostTable hosts={installation.hosts} label="Hosts" />
            </Card>
            <Card title="Guests" meta={installation.guests.length} flush>
              <GuestTable guests={installation.guests} label="Declared guests" />
            </Card>
            <Card
              title="Observed VMs"
              meta={unmanaged > 0 ? `${data.observedGuests.length} · ${plural(unmanaged, "unmanaged")}` : data.observedGuests.length || undefined}
              id="observed"
              flush
            >
              <ObservedVms vms={data.observedGuests} observed={Boolean(installation.observedAt)} />
            </Card>
            <Card title="Runs" flush>
              <RunTable runs={data.runs} label="Observe runs" showAddress={false} emptyText="No observe runs yet." />
            </Card>
            <Card title="Definition" id="definition">
              <DefinitionView file={data.definitionFile} text={data.definitionText} issues={data.issues} />
            </Card>
          </div>
          <div className="stack stack--large">
            <Card title="Connection">
              <KeyValue
                items={[
                  { term: "Type", value: installation.type ? humanize(installation.type) : "—" },
                  { term: "Endpoint", value: <span className="mono break">{installation.endpoint ?? "—"}</span> },
                  {
                    term: "TLS",
                    value:
                      data.verifyTls === null ? "—" : data.verifyTls ? "Verified" : <Badge tone="warning">Not verified</Badge>,
                  },
                  {
                    term: "Token",
                    value: data.tokenKey ? (
                      <Link to="/variables" className="mono">
                        {data.tokenKey}
                      </Link>
                    ) : (
                      "—"
                    ),
                  },
                  { term: "Proxmox VE", value: installation.proxmoxVersion ?? <span className="muted">Not observed yet</span> },
                  { term: "Cluster", value: data.clusterName ?? <span className="muted">—</span> },
                ]}
              />
            </Card>
            <Card title="Issues" meta={data.issues.length || undefined} id="issues">
              <IssueList issues={data.issues} />
            </Card>
          </div>
        </div>
      </div>
    </>
  );
}
