import { Eye, ShieldCheck } from "lucide-react";
import { isNotFound } from "../api/client";
import { Link, useParams } from "react-router";
import { useInstallation, useObserveInstallation, useTrustInstallation } from "../api/queries";
import type { InstallationSummary, ObservedGuest } from "../api/types";
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
import { EmptyRow, Table } from "../components/Table";
import { ErrorMessage, NotFound, PageSkeleton } from "../components/States";
import { useHashScroll } from "../components/useHashScroll";
import { WaitingNote } from "../components/WaitingNote";
import { humanize, plural } from "../lib/format";
import { folderLink } from "./variables/folders";

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

/** Asks for trust while the installation declares an endpoint its credentials were never sent to. */
function TrustCallout({ installation }: { installation: InstallationSummary }) {
  const trust = useTrustInstallation();
  const { endpoints, untrusted, trustedAt, managementChanged } = installation.trust;
  if (!installation.valid || untrusted.length === 0) return null;
  return (
    <div id="trust">
      <Callout
        kind="warning"
        title={managementChanged ? "Now asks to be managed" : trustedAt ? "New Proxmox endpoint" : "New Proxmox installation"}
        actions={
          <button
            type="button"
            className="button button--primary"
            disabled={trust.isPending}
            onClick={() => trust.mutate({ id: installation.id, endpoints })}
          >
            <ShieldCheck size={15} aria-hidden="true" />
            {trust.isPending ? "Trusting…" : "Trust installation"}
          </button>
        }
      >
        <p>
          {managementChanged
            ? "It was trusted while it was observed only. Now that it asks to be managed, plans, applies, and observations wait"
            : installation.managed
              ? "Plans, applies, and observations wait"
              : "Observations wait"}{" "}
          until you trust{" "}
          {untrusted.map((endpoint, i) => (
            <span key={endpoint}>
              {i > 0 ? ", " : null}
              <span className="mono break">{endpoint}</span>
            </span>
          ))}
          . Its Proxmox credentials are sent only to trusted endpoints; later commits need no trust unless an endpoint changes.
        </p>
        <ErrorMessage error={trust.error} />
      </Callout>
    </div>
  );
}

export function InstallationPage() {
  const { id = "" } = useParams();
  const { data, error, isPending } = useInstallation(id);
  const observe = useObserveInstallation();
  useHashScroll(Boolean(data));

  if (isPending) return <PageSkeleton rows={4} />;
  if (!data) {
    return isNotFound(error) ? <NotFound what="installation" /> : <ErrorMessage error={error} title="The installation could not be loaded" />;
  }

  const installation = data.installation;
  const unmanaged = installation.unmanagedCount;
  const observing = installation.observing || observe.isPending;

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
            {installation.managed ? null : <Badge tone="info">Observed only</Badge>}
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
            <button
              type="button"
              className="button button--primary"
              disabled={observing}
              onClick={() => observe.mutate(id)}
            >
              <Eye size={15} aria-hidden="true" />
              {observing ? "Observing…" : "Observe now"}
            </button>
          </>
        }
      />
      <div className="stack stack--large">
        {installation.managed ? null : (
          <Callout kind="info" title="Observed only">
            <p>
              This installation sets <span className="mono">manage: false</span>. Octopushy observes its hosts and VMs and changes nothing
              here: it defines no guests or services, starts no runs, and sends its Proxmox token only to observations.
            </p>
          </Callout>
        )}
        <TrustCallout installation={installation} />
        <ErrorMessage error={observe.error} />
        <WaitingNote text={installation.observeWaiting} />
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
            <Card title="Definition" id="definition">
              <DefinitionView file={data.definitionFile} text={data.definitionText} issues={data.issues} />
            </Card>
          </div>
          <div className="stack stack--large">
            <Card title="Connection">
              <KeyValue
                items={[
                  { term: "Type", value: installation.type ? humanize(installation.type) : "—" },
                  {
                    term: "Management",
                    value: installation.managed ? "Managed" : <Badge tone="info">Observed only</Badge>,
                  },
                  { term: "Endpoint", value: <span className="mono break">{installation.endpoint ?? "—"}</span> },
                  {
                    term: "Trust",
                    value: installation.trust.trustedAt ? (
                      <>
                        {installation.trust.managementChanged ? (
                          <Badge tone="warning">Trusted as observed only</Badge>
                        ) : installation.trust.untrusted.length > 0 ? (
                          <Badge tone="warning">New endpoint</Badge>
                        ) : (
                          <Badge tone="success">Trusted</Badge>
                        )}{" "}
                        by {installation.trust.trustedBy} · <RelativeTime value={installation.trust.trustedAt} />
                      </>
                    ) : (
                      <Badge tone="warning">Not trusted</Badge>
                    ),
                  },
                  {
                    term: "TLS",
                    value:
                      data.verifyTls === null ? "—" : data.verifyTls ? "Verified" : <Badge tone="warning">Not verified</Badge>,
                  },
                  {
                    term: "Token",
                    value: data.tokenKey ? (
                      <Link to={folderLink(data.tokenKey)} className="mono">
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
