import { Eye, Hand, ShieldCheck, TriangleAlert } from "lucide-react";
import { isNotFound } from "../api/client";
import { Link, useParams } from "react-router";
import { useInstallation, useObserveInstallation, useTrustInstallation } from "../api/queries";
import type { InstallationDetail, InstallationSummary, ObservedGuest } from "../api/types";
import { Badge, StatusBadge } from "../components/Badge";
import { Callout } from "../components/Callout";
import { Card } from "../components/Card";
import { DefinitionSheet, ViewYamlButton } from "../components/DefinitionSheet";
import { Glance } from "../components/Glance";
import { GuestTable } from "../components/GuestTable";
import { ObserveError } from "../components/ObserveError";
import { HostTable } from "../components/HostTable";
import { IssueList } from "../components/IssueList";
import { KeyValue } from "../components/KeyValue";
import { Meter } from "../components/Meter";
import { LabelList } from "../components/LabelEditor";
import { PageHeader } from "../components/PageHeader";
import { RelativeTime } from "../components/RelativeTime";
import { EmptyRow, Table } from "../components/Table";
import { ErrorMessage, NotFound, PageSkeleton } from "../components/States";
import { useHashScroll } from "../components/useHashScroll";
import { SnippetAccessCard } from "../components/SnippetAccess";
import { WaitingNote } from "../components/WaitingNote";
import { formatBytes, humanize, operationLabel, plural } from "../lib/format";
import { formatUptime } from "../lib/time";
import { folderLink } from "./variables/folders";

function ObservedVms({ vms, observed }: { vms: ObservedGuest[]; observed: boolean }) {
  return (
    <Table label="Observed guests" className="table--observed">
      <thead>
        <tr>
          <th scope="col">VMID</th>
          <th scope="col">Name</th>
          <th scope="col">Kind</th>
          <th scope="col">Host</th>
          <th scope="col">Power</th>
          <th scope="col">Tags</th>
          <th scope="col">Managed by</th>
        </tr>
      </thead>
      <tbody>
        {vms.length === 0 ? (
          <EmptyRow colSpan={7}>
            {observed ? "Proxmox reported no VMs or containers." : "Not observed yet. Observe the installation to list its VMs and containers."}
          </EmptyRow>
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
            <td>{vm.kind === "lxc" ? "Container" : vm.kind === "qemu" ? "VM" : (vm.kind ?? <span className="muted">—</span>)}</td>
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

/** The runs of its guests that wait for approval, and the guests whose last run failed. Nothing when there are none. */
function GuestRuns({ installation }: { installation: InstallationSummary }) {
  const waiting = installation.guests.filter((g) => g.activeRun?.status === "awaiting-approval");
  const failed = installation.guests.filter((g) => g.status === "failed");
  if (waiting.length === 0 && failed.length === 0) return null;
  const title = [
    waiting.length > 0 ? `${plural(waiting.length, "guest")} ${waiting.length === 1 ? "waits" : "wait"} for your approval` : null,
    failed.length > 0 ? (waiting.length > 0 ? `${failed.length} failed` : `${plural(failed.length, "guest")} failed`) : null,
  ].filter(Boolean).join(", ");
  const Icon = waiting.length > 0 ? Hand : TriangleAlert;
  const rows = [...waiting.map((guest) => ({ guest, run: guest.activeRun!, failed: false })), ...failed.map((guest) => ({ guest, run: guest.lastRun, failed: true }))];
  return (
    <section className={waiting.length > 0 ? "wait" : "wait wait--danger"} aria-label="Runs of its guests">
      <header className="wait__head">
        <h2 className="wait__title">
          <Icon size={18} aria-hidden="true" />
          {title}
        </h2>
      </header>
      <ul className="wait__body wait-list">
        {rows.map(({ guest, run, failed }) => (
          <li key={guest.id}>
            <Link to={`/guests/${guest.id}`} className="mono">
              {guest.name}
            </Link>
            {run ? (
              <span>
                {operationLabel(run.operation)} #{run.number}
                {failed ? " failed" : null}
              </span>
            ) : null}
            <span className="wait-list__detail">{failed ? (guest.statusDetail ?? run?.detail) : (run?.detail ?? run?.waiting)}</span>
            {run ? (
              <>
                <span className="small muted">
                  <RelativeTime value={run.createdAt} />
                </span>
                <Link to={`/runs/${run.id}`} className="small">
                  Open run
                </Link>
              </>
            ) : null}
          </li>
        ))}
      </ul>
    </section>
  );
}

/** The installation's figures: its hosts, what they use, its guests and its Proxmox version. */
function InstallationGlance({ data }: { data: InstallationDetail }) {
  const { installation } = data;
  const hosts = installation.hosts;
  const online = hosts.filter((h) => h.online).length;
  const sum = (values: (number | null)[]) => values.reduce<number>((total, v) => total + (v ?? 0), 0);
  const cores = sum(hosts.map((h) => h.maxCpu));
  const busy = sum(hosts.map((h) => (h.cpu !== null && h.maxCpu !== null ? h.cpu * h.maxCpu : null)));
  const memory = sum(hosts.map((h) => h.maxMemoryBytes));
  const used = sum(hosts.map((h) => h.memoryBytes));
  const host = hosts.length === 1 ? hosts[0] : null;
  return (
    <Glance
      cells={[
        host
          ? {
              label: "Host",
              value: host.online === null ? "Not observed" : host.online ? "Online" : "Offline",
              note: host.uptimeSeconds ? `up ${formatUptime(host.uptimeSeconds)}` : host.name,
            }
          : hosts.length > 1
            ? { label: "Hosts", value: `${online} of ${hosts.length} online`, note: hosts.map((h) => h.name).join(", ") }
            : null,
        cores > 0
          ? { label: "CPU", value: plural(cores, "core"), note: hosts.some((h) => h.cpu !== null) ? <Meter label="CPU in use" fraction={busy / cores} /> : null }
          : null,
        memory > 0
          ? {
              label: "Memory",
              value: formatBytes(memory),
              note: hosts.some((h) => h.memoryBytes !== null) ? <Meter label="Memory in use" fraction={used / memory} text={`${formatBytes(used)} used`} /> : null,
            }
          : null,
        installation.managed || installation.guests.length > 0
          ? { label: "Guests", value: installation.guests.length, note: `managed · ${installation.unmanagedCount} unmanaged` }
          : null,
        installation.proxmoxVersion
          ? {
              label: "Proxmox VE",
              value: installation.proxmoxVersion,
              note: installation.observedAt ? <>observed <RelativeTime value={installation.observedAt} /></> : null,
            }
          : null,
      ]}
    />
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
            {data.definitionText !== null ? <ViewYamlButton file={data.definitionFile} text={data.definitionText} issues={data.issues} /> : null}
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
        <GuestRuns installation={installation} />
        <InstallationGlance data={data} />
        <div className="columns columns--main-side">
          <div className="stack stack--large">
            <Card title="Guests" meta={installation.guests.length} flush>
              <GuestTable guests={installation.guests} label="Declared guests" />
            </Card>
            <Card
              title="Observed guests"
              meta={unmanaged > 0 ? `${data.observedGuests.length} · ${plural(unmanaged, "unmanaged")}` : data.observedGuests.length || undefined}
              id="observed"
              flush
            >
              <ObservedVms vms={data.observedGuests} observed={Boolean(installation.observedAt)} />
            </Card>
            {installation.hosts.length === 1 && installation.hosts[0].declared ? null : (
              <Card title="Hosts" flush>
                <HostTable hosts={installation.hosts} label="Hosts" />
              </Card>
            )}
            <DefinitionSheet file={data.definitionFile} text={data.definitionText} kind="installation" issues={data.issues} />
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
            {data.snippets ? <SnippetAccessCard id={installation.id} snippets={data.snippets} /> : null}
            <Card title="Issues" meta={data.issues.length || undefined} id="issues">
              <IssueList issues={data.issues} />
            </Card>
          </div>
        </div>
      </div>
    </>
  );
}
