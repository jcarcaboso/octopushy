import { GitBranch, Server } from "lucide-react";
import { Link, useSearchParams } from "react-router";
import { useInventory } from "../api/queries";
import type { InstallationSummary, Lab } from "../api/types";
import { Badge, StatusBadge } from "../components/Badge";
import { Card } from "../components/Card";
import { GuestTable } from "../components/GuestTable";
import { ObserveError } from "../components/ObserveError";
import { HostTable } from "../components/HostTable";
import { IssueList } from "../components/IssueList";
import { Commit } from "../components/Mono";
import { PageHeader } from "../components/PageHeader";
import { RelativeTime } from "../components/RelativeTime";
import { EmptyState, ErrorMessage, PageSkeleton } from "../components/States";
import { statusStyle } from "../components/status";
import { humanize, plural } from "../lib/format";

function Observed({ installation }: { installation: InstallationSummary }) {
  return (
    <span className="observed">
      {installation.observedAt ? (
        <>
          Observed <RelativeTime value={installation.observedAt} />
        </>
      ) : (
        <span className="muted">Not observed yet</span>
      )}
    </span>
  );
}

function InstallationCard({ installation, filter }: { installation: InstallationSummary; filter: string[] | null }) {
  const unmanaged = installation.unmanagedCount;
  const issues = installation.issueCount;
  const guests = filter ? installation.guests.filter((g) => filter.includes(g.status)) : installation.guests;
  return (
    <Card
      title={
        <Link to={`/installations/${installation.id}`} className="card__title-link">
          {installation.name}
        </Link>
      }
      meta={installation.type ? humanize(installation.type) : undefined}
      actions={<Observed installation={installation} />}
      className="installation"
    >
      <div className="installation__facts">
        <span className="mono muted break">{installation.endpoint ?? "No endpoint"}</span>
        {installation.proxmoxVersion ? <span className="muted">Proxmox VE {installation.proxmoxVersion}</span> : null}
        {installation.valid ? null : <StatusBadge status="invalid" />}
        {issues > 0 ? (
          <Link to={`/installations/${installation.id}#issues`}>
            <Badge tone="danger">{plural(issues, "error")}</Badge>
          </Link>
        ) : null}
      </div>
      <ObserveError installation={installation} />
      <h3 className="subhead">Hosts</h3>
      <HostTable hosts={installation.hosts} label={`Hosts of ${installation.name}`} />
      <h3 className="subhead">
        Guests{" "}
        <span className="muted small num">
          {filter ? `${guests.length} of ${installation.guests.length}` : installation.guests.length}
        </span>
      </h3>
      <GuestTable
        guests={guests}
        label={`Guests of ${installation.name}`}
        emptyText={filter ? "No guests with this status." : "No guests declared."}
      />
      {unmanaged > 0 ? (
        <p className="installation__unmanaged">
          <Link to={`/installations/${installation.id}#observed`}>
            {plural(unmanaged, "unmanaged VM")}
          </Link>{" "}
          <span className="muted">on this installation (shown, never touched).</span>
        </p>
      ) : null}
    </Card>
  );
}

function LabSection({ lab, filter }: { lab: Lab; filter: string[] | null }) {
  const installations = filter
    ? lab.installations.filter((i) => i.guests.some((g) => filter.includes(g.status)))
    : lab.installations;
  if (filter && installations.length === 0) return null;
  return (
    <section className="lab" aria-labelledby={`lab-${lab.id}`}>
      <header className="lab__header">
        <h2 id={`lab-${lab.id}`}>
          {lab.name} <span className="mono muted small">{lab.address}</span>
        </h2>
        {lab.valid ? null : <StatusBadge status="invalid" />}
      </header>
      {lab.issues.length > 0 ? (
        <Card title="Lab issues">
          <IssueList issues={lab.issues} />
        </Card>
      ) : null}
      {installations.length === 0 ? (
        <EmptyState compact icon={Server} title="No Proxmox installations in this lab" />
      ) : (
        <div className="stack stack--large">
          {installations.map((installation) => (
            <InstallationCard key={installation.id} installation={installation} filter={filter} />
          ))}
        </div>
      )}
    </section>
  );
}

export function InventoryPage() {
  const { data, error, isPending } = useInventory();
  const [params, setParams] = useSearchParams();
  const filterParam = params.get("status");
  const filter = filterParam ? filterParam.split(",").filter(Boolean) : null;

  if (isPending) return <PageSkeleton />;
  if (!data) return <ErrorMessage error={error} title="The inventory could not be loaded" />;

  const revision = data.revision;
  return (
    <>
      <PageHeader
        title="Inventory"
        meta={
          revision ? (
            <span>
              From revision <Commit commit={revision.commit} /> {revision.subject}
            </span>
          ) : undefined
        }
      />
      {error ? <ErrorMessage error={error} title="Showing the last data; refreshing failed" /> : null}
      {filter ? (
        <div className="filter-note" role="status">
          Showing guests that are{" "}
          {filter.map((s, i) => (
            <span key={s}>
              {i > 0 ? " or " : null}
              <StatusBadge status={s} />
            </span>
          ))}
          <button type="button" className="button button--small button--ghost" onClick={() => setParams({})}>
            Show all
          </button>
        </div>
      ) : null}
      {data.labs.length === 0 ? (
        <EmptyState
          icon={GitBranch}
          title={revision ? "The repository declares no labs" : "No inventory yet"}
          actions={
            <Link to="/repository" className="button button--primary">
              Repository settings
            </Link>
          }
        >
          {revision
            ? "Add a lab under labs/ in the lab repository."
            : "Connect the lab repository; its labs, installations, and guests appear here once a revision loads."}
        </EmptyState>
      ) : (
        <div className="stack stack--xl">
          {data.labs.map((lab) => (
            <LabSection key={lab.id} lab={lab} filter={filter} />
          ))}
          {filter && data.labs.every((lab) => lab.installations.every((i) => !i.guests.some((g) => filter.includes(g.status)))) ? (
            <EmptyState compact title={`No guests are ${filter.map((s) => statusStyle(s).label.toLowerCase()).join(" or ")}`} />
          ) : null}
        </div>
      )}
    </>
  );
}
