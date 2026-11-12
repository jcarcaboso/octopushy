import { ChevronRight, CircleCheck, CircleDashed, CircleMinus, CircleX, ExternalLink, GitBranch, Layers, Monitor, Package, Server, TriangleAlert } from "lucide-react";
import { useRef, type KeyboardEvent, type ReactNode } from "react";
import { Link, useSearchParams } from "react-router";
import { useInventory, useUptime } from "../api/queries";
import type { UptimeCheck } from "../api/types";
import { Badge, StatusBadge } from "../components/Badge";
import { Callout } from "../components/Callout";
import { IssueList } from "../components/IssueList";
import { Meter } from "../components/Meter";
import { ObserveError } from "../components/ObserveError";
import { Commit } from "../components/Mono";
import { PageHeader } from "../components/PageHeader";
import { RelativeTime } from "../components/RelativeTime";
import { EmptyState, ErrorMessage, PageSkeleton } from "../components/States";
import { formatBytes, plural } from "../lib/format";
import {
  barState,
  buildTree,
  counts,
  filterTree,
  formatPercent,
  guestKind,
  installationNote,
  isProblem,
  nodeEndpoint,
  percentOf,
  proxmoxGuestUrl,
  proxmoxTab,
  windows,
  type Liveness,
  type MachineEntry,
  type NodeEntry,
  type ServiceEntry,
  type UnmanagedEntry,
  type UptimeWindow,
} from "../lib/inventory";
import { formatDuration, formatUptime } from "../lib/time";

const livenessStyle: Record<Liveness, { label: string; tone: "success" | "warning" | "danger" | "muted"; icon: typeof CircleCheck }> = {
  up: { label: "Up", tone: "success", icon: CircleCheck },
  degraded: { label: "Degraded", tone: "warning", icon: TriangleAlert },
  down: { label: "Down", tone: "danger", icon: CircleX },
  pending: { label: "Not checked yet", tone: "muted", icon: CircleDashed },
  none: { label: "No check", tone: "muted", icon: CircleMinus },
};

function LivenessIcon({ liveness }: { liveness: Liveness }) {
  const style = livenessStyle[liveness];
  const Icon = style.icon;
  return <Icon className={`liveness liveness--${style.tone}`} size={14} strokeWidth={2.25} role="img" aria-label={style.label} />;
}

function LivenessBadge({ liveness, since }: { liveness: Liveness; since?: string | null }) {
  const style = livenessStyle[liveness];
  return (
    <Badge tone={style.tone} icon={style.icon}>
      {style.label}
      {since && (liveness === "up" || liveness === "down") ? (
        <span className="inv-since">
          {" "}
          since <RelativeTime value={since} />
        </span>
      ) : null}
    </Badge>
  );
}

/** The worst state of a quarter hour or a day across several checks. */
function merged(checks: UptimeCheck[], span: UptimeWindow): { state: "u" | "d" | "p" | "-"; title: string }[] {
  if (span === "day") {
    return Array.from({ length: 96 }, (_, i) => {
      const states = checks.map((c) => c.beats[i] ?? "-").filter((s) => s !== "-");
      const state = states.length === 0 ? "-" : states.every((s) => s === "u") ? "u" : states.every((s) => s === "d") ? "d" : "p";
      const minutes = (95 - i) * 15;
      const when = minutes === 0 ? "This quarter hour" : `${formatDuration(minutes * 60_000)} ago`;
      return { state, title: `${when}: ${{ u: "up", d: "down", p: "partly down", "-": "no answers" }[state]}` };
    });
  }
  const days = span === "week" ? 7 : 30;
  return Array.from({ length: days }, (_, i) => {
    const index = 30 - days + i;
    const values = checks.map((c) => c.daily[index]).filter((v): v is number => v !== null && v !== undefined);
    const value = values.length === 0 ? null : Math.min(...values);
    const state = barState(value);
    const ago = days - 1 - i;
    return { state, title: `${ago === 0 ? "Last 24 hours" : `${ago + 1} days ago`}: ${value === null ? "no answers" : `${formatPercent(value)} up`}` };
  });
}

function UptimeStrip({ checks, span, large = false }: { checks: UptimeCheck[]; span: UptimeWindow; large?: boolean }) {
  const bars = merged(checks, span);
  const percent = percentOf(checks, span);
  return (
    <div
      className={large ? "uptime-strip uptime-strip--large" : "uptime-strip"}
      role="img"
      aria-label={`${formatPercent(percent)} up over the last ${windows.find((w) => w.value === span)!.label}`}
    >
      {bars.map((bar, i) => (
        <span key={i} className={`uptime-strip__bar uptime-strip__bar--${bar.state}`} title={bar.title} />
      ))}
    </div>
  );
}

function LatencyChart({ check }: { check: UptimeCheck }) {
  const values = check.latency;
  const known = values.filter((v): v is number => v !== null);
  if (known.length < 2) return null;
  const max = Math.max(...known, 1);
  const width = 96 * 4;
  const height = 48;
  const segments: string[] = [];
  let current = "";
  values.forEach((v, i) => {
    if (v === null) {
      if (current) segments.push(current);
      current = "";
      return;
    }
    const x = i * 4 + 2;
    const y = height - 2 - (v / max) * (height - 6);
    current += `${current ? "L" : "M"}${x.toFixed(1)} ${y.toFixed(1)} `;
  });
  if (current) segments.push(current);
  return (
    <figure className="latency">
      <svg viewBox={`0 0 ${width} ${height}`} preserveAspectRatio="none" role="img" aria-label={`Response time, up to ${max} ms over the last 24 hours`}>
        {segments.map((d, i) => (
          <path key={i} d={d} className="latency__line" vectorEffect="non-scaling-stroke" />
        ))}
      </svg>
      <figcaption className="latency__caption">
        <span>Response time, last 24 hours</span>
        <span className="num">up to {max} ms</span>
      </figcaption>
    </figure>
  );
}

function UptimeFigures({ checks }: { checks: UptimeCheck[] }) {
  return (
    <dl className="uptime-figures">
      {windows.map((w) => (
        <div key={w.value}>
          <dt>{w.label}</dt>
          <dd className="num">{formatPercent(percentOf(checks, w.value))}</dd>
        </div>
      ))}
    </dl>
  );
}

/** One check: what it reaches, what it says, and since when. */
function CheckRow({ check, span }: { check: UptimeCheck; span: UptimeWindow }) {
  const liveness: Liveness = check.status === "up" ? "up" : check.status === "down" ? "down" : "pending";
  return (
    <li className="check-row">
      <LivenessIcon liveness={liveness} />
      <div className="check-row__main">
        <span className="check-row__target mono">{check.url}</span>
        <span className="muted small">
          {check.detail ?? livenessStyle[liveness].label}
          {check.since ? (
            <>
              {" · "}
              {check.status === "up" ? "up" : "down"} since <RelativeTime value={check.since} />
            </>
          ) : null}
          {check.elapsedMs !== null && check.status === "up" ? <> · {check.elapsedMs} ms</> : null}
        </span>
      </div>
      <UptimeStrip checks={[check]} span={span} />
      <span className="num check-row__percent">{formatPercent(percentOf([check], span))}</span>
    </li>
  );
}

function UptimeSection({ title, checks, span, empty }: { title: string; checks: UptimeCheck[]; span: UptimeWindow; empty: ReactNode }) {
  return (
    <section className="inv-section">
      <h3 className="inv-section__title">{title}</h3>
      {checks.length === 0 ? (
        <p className="muted small">{empty}</p>
      ) : (
        <>
          <UptimeFigures checks={checks} />
          <UptimeStrip checks={checks} span={span} large />
          {checks.length === 1 ? <LatencyChart check={checks[0]} /> : null}
          <ul className="check-list">
            {checks.map((c) => (
              <CheckRow key={c.key} check={c} span={span} />
            ))}
          </ul>
        </>
      )}
    </section>
  );
}

function ProxmoxLink({ node, href, children }: { node: NodeEntry; href: string; children: ReactNode }) {
  return (
    // No rel: noopener, which noreferrer implies, would open a new tab each time instead of going back to the named one.
    <a className="button button--small" href={href} target={proxmoxTab(node)} referrerPolicy="no-referrer">
      {children} <ExternalLink size={13} aria-hidden="true" />
    </a>
  );
}

function DetailHeader({ icon: Icon, kind, title, badges, actions }: { icon: typeof Server; kind: string; title: ReactNode; badges: ReactNode; actions: ReactNode }) {
  return (
    <header className="inv-detail__header">
      <span className="inv-detail__icon" aria-hidden="true">
        <Icon size={20} />
      </span>
      <div className="inv-detail__heading">
        <span className="inv-detail__kind">{kind}</span>
        <h2 className="inv-detail__title">{title}</h2>
        <div className="inv-detail__badges">{badges}</div>
      </div>
      <div className="inv-detail__actions">{actions}</div>
    </header>
  );
}

function NodeDetail({ node, span }: { node: NodeEntry; span: UptimeWindow }) {
  const host = node.host;
  const endpoint = nodeEndpoint(node);
  return (
    <>
      <DetailHeader
        icon={Server}
        kind={node.installation.hosts.length > 1 ? `Node of ${node.installation.name}` : "Node"}
        title={host.name}
        badges={
          <>
            <LivenessBadge liveness={node.liveness} since={node.checks[0]?.since} />
            {host.online === false ? <Badge tone="danger">Offline in Proxmox</Badge> : null}
            {node.installation.valid ? null : <StatusBadge status="invalid" />}
            {node.installation.managed ? null : <Badge tone="info">Observed only</Badge>}
            {node.installation.valid && node.installation.trust.untrusted.length > 0 ? (
              <Link to={`/installations/${node.installation.id}#trust`}>
                <Badge tone="warning">Needs trust</Badge>
              </Link>
            ) : null}
            {node.installation.issueCount > 0 ? (
              <Link to={`/installations/${node.installation.id}#issues`}>
                <Badge tone="danger">{plural(node.installation.issueCount, "error")}</Badge>
              </Link>
            ) : null}
            {node.installation.proxmoxVersion ? <Badge>Proxmox VE {node.installation.proxmoxVersion}</Badge> : null}
          </>
        }
        actions={
          <>
            {endpoint ? (
              <ProxmoxLink node={node} href={endpoint}>
                Open Proxmox
              </ProxmoxLink>
            ) : null}
            <Link className="button button--small" to={`/installations/${node.installation.id}`}>
              Installation
            </Link>
          </>
        }
      />
      <ObserveError installation={node.installation} />
      <section className="inv-section">
        <h3 className="inv-section__title">Resources</h3>
        {host.online === null && host.maxCpu === null ? (
          <p className="muted small">Not observed yet.</p>
        ) : (
          <dl className="inv-facts">
            <dt>CPU</dt>
            <dd>
              <Meter label="CPU" fraction={host.cpu} text={host.maxCpu ? `${host.maxCpu} cores` : undefined} />
            </dd>
            <dt>Memory</dt>
            <dd>
              <Meter
                label="Memory"
                fraction={host.memoryBytes !== null && host.maxMemoryBytes ? host.memoryBytes / host.maxMemoryBytes : null}
                text={`${formatBytes(host.memoryBytes)} of ${formatBytes(host.maxMemoryBytes)}`}
              />
            </dd>
            <dt>Up for</dt>
            <dd>{host.uptimeSeconds !== null ? formatUptime(host.uptimeSeconds) : "—"}</dd>
            <dt>Observed</dt>
            <dd>
              <RelativeTime value={node.installation.observedAt} empty="Not yet" />
            </dd>
          </dl>
        )}
      </section>
      <UptimeSection title="Proxmox API" checks={node.checks} span={span} empty="Not checked: the installation is not trusted, or it declares no https endpoint." />
      <section className="inv-section">
        <h3 className="inv-section__title">On this node</h3>
        <p className="small">
          {plural(node.machines.length, "machine")} · {plural(node.machines.reduce((n, m) => n + m.services.length, 0), "service")}
          {node.unmanaged.length > 0 ? <> · {plural(node.unmanaged.length, "unmanaged guest")}</> : null}
        </p>
      </section>
    </>
  );
}

function MachineDetail({ node, machine, span }: { node: NodeEntry; machine: MachineEntry; span: UptimeWindow }) {
  const guest = machine.guest;
  const endpoint = nodeEndpoint(node);
  return (
    <>
      <DetailHeader
        icon={guest.type === "container" ? Package : Monitor}
        kind={guest.type === "container" ? "Container" : "Virtual machine"}
        title={guest.name}
        badges={
          <>
            <LivenessBadge liveness={machine.liveness} since={machine.checks[0]?.since} />
            <StatusBadge status={guest.status} title={guest.statusDetail ?? undefined} />
          </>
        }
        actions={
          <>
            {endpoint && guest.vmid !== null ? (
              <ProxmoxLink node={node} href={proxmoxGuestUrl(endpoint, guestKind(guest), guest.vmid)}>
                Open in Proxmox
              </ProxmoxLink>
            ) : null}
            <Link className="button button--small" to={`/guests/${guest.id}`}>
              Machine page
            </Link>
          </>
        }
      />
      <section className="inv-section">
        <dl className="inv-facts">
          <dt>VMID</dt>
          <dd className="num">{guest.vmid ?? "—"}</dd>
          <dt>Address</dt>
          <dd className="mono">{guest.accessAddress ?? "—"}</dd>
          <dt>Power</dt>
          <dd>{guest.observed?.status ?? "Not in Proxmox"}</dd>
          <dt>Workflow</dt>
          <dd className="mono">{guest.workflow?.resolved ?? guest.workflow?.reference ?? "—"}</dd>
          {guest.observed?.uptimeSeconds ? (
            <>
              <dt>Up for</dt>
              <dd>{formatUptime(guest.observed.uptimeSeconds)}</dd>
            </>
          ) : null}
        </dl>
      </section>
      <UptimeSection title="SSH" checks={machine.checks} span={span} empty="Not checked: no address is known for this machine." />
    </>
  );
}

function ServiceDetail({ service, span }: { service: ServiceEntry; span: UptimeWindow }) {
  const s = service.service;
  return (
    <>
      <DetailHeader
        icon={Layers}
        kind="Service"
        title={s.name}
        badges={
          <>
            <LivenessBadge liveness={service.liveness} />
            <StatusBadge status={s.status} title={s.statusDetail ?? undefined} />
          </>
        }
        actions={
          <>
            {s.links[0] ? (
              <a className="button button--small button--primary" href={s.links[0]} target="_blank" rel="noreferrer">
                Open <ExternalLink size={13} aria-hidden="true" />
              </a>
            ) : null}
            <Link className="button button--small" to={`/services/${s.id}`}>
              Service page
            </Link>
          </>
        }
      />
      <section className="inv-section">
        <h3 className="inv-section__title">Links</h3>
        {s.links.length === 0 ? (
          <p className="muted small">
            No links. Add <code>links</code> to its <code>service.yaml</code> to open it from here and check that it answers.
          </p>
        ) : (
          <ul className="link-list">
            {s.links.map((link) => (
              <li key={link}>
                <a href={link} target="_blank" rel="noreferrer" className="mono">
                  {link}
                </a>
              </li>
            ))}
          </ul>
        )}
      </section>
      <UptimeSection title="Uptime" checks={service.checks} span={span} empty="Nothing is checked without links." />
    </>
  );
}

function UnmanagedDetail({ node, entry }: { node: NodeEntry; entry: UnmanagedEntry }) {
  const g = entry.guest;
  const endpoint = nodeEndpoint(node);
  return (
    <>
      <DetailHeader
        icon={g.kind === "lxc" ? Package : Monitor}
        kind={g.template ? "Template, not managed" : "Not managed"}
        title={g.name ?? `VMID ${g.vmid}`}
        badges={<Badge tone="muted">{g.status ?? "unknown"}</Badge>}
        actions={
          endpoint ? (
            <ProxmoxLink node={node} href={proxmoxGuestUrl(endpoint, g.kind ?? "qemu", g.vmid)}>
              Open in Proxmox
            </ProxmoxLink>
          ) : null
        }
      />
      <section className="inv-section">
        <p className="muted small">Proxmox lists it, but no definition manages it: Octopushy shows it and never touches it, and nothing checks it.</p>
        <dl className="inv-facts">
          <dt>VMID</dt>
          <dd className="num">{g.vmid}</dd>
          <dt>Size</dt>
          <dd>
            {g.maxCpu ?? "—"} cores · {formatBytes(g.maxMemoryBytes)}
          </dd>
          {g.tags.length > 0 ? (
            <>
              <dt>Tags</dt>
              <dd>{g.tags.join(", ")}</dd>
            </>
          ) : null}
        </dl>
      </section>
    </>
  );
}

interface RowProps {
  column: number;
  selected: boolean;
  liveness?: Liveness;
  icon: typeof Server;
  name: string;
  note?: string | null;
  value?: string;
  more?: boolean;
  muted?: boolean;
  onSelect: () => void;
}

function Row({ column, selected, liveness, icon: Icon, name, note, value, more, muted, onSelect }: RowProps) {
  return (
    <li>
      <button
        type="button"
        className={`inv-row${selected ? " inv-row--selected" : ""}${muted ? " inv-row--muted" : ""}`}
        data-column={column}
        aria-current={selected ? "true" : undefined}
        tabIndex={selected ? 0 : -1}
        onClick={onSelect}
      >
        {liveness ? <LivenessIcon liveness={liveness} /> : <span className="inv-row__spacer" />}
        <Icon className="inv-row__icon" size={14} aria-hidden="true" />
        <span className="inv-row__name">
          {name}
          {note ? <span className="inv-row__note">{note}</span> : null}
        </span>
        {value ? <span className="inv-row__value num">{value}</span> : null}
        <ChevronRight className="inv-row__more" size={14} aria-hidden="true" style={{ visibility: more ? "visible" : "hidden" }} />
      </button>
    </li>
  );
}

function Column({ label, count, children }: { label: string; count?: number; children: ReactNode }) {
  return (
    <section className="inv-column" aria-label={label}>
      <header className="inv-column__head">
        <span>{label}</span>
        {count !== undefined ? <span className="num">{count}</span> : null}
      </header>
      <ul className="inv-column__list">{children}</ul>
    </section>
  );
}

export function InventoryPage() {
  const { data, error, isPending } = useInventory();
  const uptime = useUptime();
  const [params, setParams] = useSearchParams();
  const search = params.get("q") ?? "";
  const problems = params.get("show") === "problems";
  // The Overview links here with the guest statuses to show, as "status=running,queued".
  const statuses = (params.get("status") ?? "").split(",").filter(Boolean);
  const span = (windows.find((w) => w.value === params.get("window"))?.value ?? "day") as UptimeWindow;
  const columnsRef = useRef<HTMLDivElement>(null);

  if (isPending) return <PageSkeleton />;
  if (!data) return <ErrorMessage error={error} title="The inventory could not be loaded" />;

  const tree = buildTree(data, uptime.data);
  const shown = filterTree(tree, { text: search, problems, statuses });
  const total = counts(tree);

  const with_ = (changes: Record<string, string | null>) => {
    const next = new URLSearchParams(params);
    for (const [key, value] of Object.entries(changes)) {
      if (value) next.set(key, value);
      else next.delete(key);
    }
    return next;
  };
  const href = (changes: Record<string, string | null>) => `?${with_(changes)}`;
  const select = (changes: Record<string, string | null>) => setParams(with_(changes), { replace: true });

  // The selection, or the first node with a problem, or the first node.
  const node = shown.find((n) => n.key === params.get("node")) ?? shown.find((n) => isProblem(n.liveness)) ?? shown[0];
  const machineKey = params.get("machine");
  const machine = node?.machines.find((m) => m.key === machineKey);
  const unmanaged = node?.unmanaged.find((u) => u.key === machineKey);
  const service = machine?.services.find((s) => s.key === params.get("service"));
  const showServices = Boolean(machine && machine.services.length > 0);

  // Arrows move within a column and between columns, as in a file browser.
  const onKeyDown = (event: KeyboardEvent<HTMLDivElement>) => {
    const row = (event.target as HTMLElement).closest<HTMLButtonElement>(".inv-row");
    if (!row || !columnsRef.current) return;
    const column = Number(row.dataset.column);
    const rows = (c: number) => [...columnsRef.current!.querySelectorAll<HTMLButtonElement>(`.inv-row[data-column="${c}"]`)];
    const here = rows(column);
    const index = here.indexOf(row);
    let target: HTMLButtonElement | undefined;
    if (event.key === "ArrowDown") target = here[index + 1];
    else if (event.key === "ArrowUp") target = here[index - 1];
    else if (event.key === "ArrowRight") target = rows(column + 1).find((r) => r.getAttribute("aria-current")) ?? rows(column + 1)[0];
    else if (event.key === "ArrowLeft") target = rows(column - 1).find((r) => r.getAttribute("aria-current"));
    else return;
    event.preventDefault();
    if (!target) return;
    if (event.key === "ArrowDown" || event.key === "ArrowUp" || !target.getAttribute("aria-current")) target.click();
    requestAnimationFrame(() => columnsRef.current?.querySelector<HTMLButtonElement>(`.inv-row[data-column="${Number(target.dataset.column)}"][aria-current]`)?.focus() ?? target.focus());
  };

  const revision = data.revision;
  const filtered = Boolean(search.trim() || problems || statuses.length > 0);
  return (
    <>
      <PageHeader
        title="Inventory"
        meta={
          <span>
            {plural(total.nodes, "node")} · {plural(total.machines, "machine")} · {plural(total.services, "service")}
            {total.problems > 0 ? <span className="inv-problems"> · {plural(total.problems, "problem")}</span> : null}
            {revision ? (
              <>
                {" · "}revision <Commit commit={revision.commit} />
              </>
            ) : null}
          </span>
        }
        actions={
          <div className="runs-filters">
            <nav className="segmented" aria-label="Show">
              <Link to={href({ show: null })} replace className="segmented__item" aria-current={!problems ? "page" : undefined}>
                All
              </Link>
              <Link to={href({ show: "problems" })} replace className="segmented__item" aria-current={problems ? "page" : undefined}>
                Problems
              </Link>
            </nav>
            <nav className="segmented" aria-label="Uptime over">
              {windows.map((w) => (
                <Link
                  key={w.value}
                  to={href({ window: w.value === "day" ? null : w.value })}
                  replace
                  className="segmented__item"
                  aria-current={span === w.value ? "page" : undefined}
                >
                  {w.label}
                </Link>
              ))}
            </nav>
            <input
              type="search"
              className="runs-search"
              aria-label="Filter nodes, machines and services"
              placeholder="Node, machine, service or link"
              value={search}
              onChange={(e) => select({ q: e.target.value || null })}
            />
          </div>
        }
      />
      {error ? <ErrorMessage error={error} title="Showing the last data; refreshing failed" /> : null}
      {statuses.length > 0 ? (
        <div className="filter-note" role="status">
          Only machines that are
          {statuses.map((status) => (
            <StatusBadge key={status} status={status} />
          ))}
          <Link to={href({ status: null })} replace>
            Show all
          </Link>
        </div>
      ) : null}
      {data.labs
        .filter((lab) => lab.issues.length > 0)
        .map((lab) => (
          <Callout key={lab.id} kind={lab.valid ? "warning" : "danger"} title={`Lab ${lab.name}`}>
            <IssueList issues={lab.issues} />
          </Callout>
        ))}
      {uptime.data && !uptime.data.checking ? (
        <Callout kind="warning" title="Nothing checks uptime right now">
          No connected agent answers uptime checks, so the history has a gap from now until one does. Update an agent to this release.
        </Callout>
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
      ) : shown.length === 0 ? (
        <EmptyState
          title={filtered ? "Nothing matches these filters" : "No nodes yet"}
          actions={
            filtered ? (
              <Link to="/inventory" className="button button--small">
                Clear the filters
              </Link>
            ) : undefined
          }
        >
          {filtered ? null : "Nodes appear once an installation is observed."}
        </EmptyState>
      ) : (
        <div
          ref={columnsRef}
          className={showServices ? "inv inv--services" : "inv"}
          onKeyDown={onKeyDown}
        >
          <Column label="Nodes" count={shown.length}>
            {shown.map((n) => (
              <Row
                key={n.key}
                column={0}
                selected={n === node}
                liveness={n.liveness}
                icon={Server}
                name={n.host.name}
                note={[n.installation.hosts.length > 1 ? n.installation.name : null, installationNote(n.installation)].filter(Boolean).join(" · ") || null}
                value={formatPercent(percentOf(n.checks, span))}
                more
                onSelect={() => select({ node: n.key, machine: null, service: null })}
              />
            ))}
          </Column>
          <Column label="Machines" count={node?.machines.length}>
            {node?.machines.map((m) => (
              <Row
                key={m.key}
                column={1}
                selected={m === machine}
                liveness={m.liveness}
                icon={m.guest.type === "container" ? Package : Monitor}
                name={m.guest.name}
                value={formatPercent(percentOf(m.checks, span))}
                more={m.services.length > 0}
                onSelect={() => select({ node: node.key, machine: m.key, service: null })}
              />
            ))}
            {node && node.machines.length === 0 ? <li className="inv-column__empty">No machines {filtered ? "match" : "on this node"}.</li> : null}
            {node && node.unmanaged.length > 0 ? (
              <li>
                <details className="inv-unmanaged" open={Boolean(unmanaged) || (search.trim() !== "" && node.unmanaged.length > 0) || undefined}>
                  <summary>
                    <ChevronRight className="inv-unmanaged__chevron" size={14} aria-hidden="true" />
                    Unmanaged <span className="num">{node.unmanaged.length}</span>
                  </summary>
                  <ul className="inv-column__list">
                    {node.unmanaged.map((u) => (
                      <Row
                        key={u.key}
                        column={1}
                        selected={u === unmanaged}
                        icon={u.guest.kind === "lxc" ? Package : Monitor}
                        name={u.guest.name ?? `VMID ${u.guest.vmid}`}
                        note={u.guest.template ? "template" : u.guest.status}
                        muted
                        onSelect={() => select({ node: node.key, machine: u.key, service: null })}
                      />
                    ))}
                  </ul>
                </details>
              </li>
            ) : null}
          </Column>
          {showServices && machine ? (
            <Column label="Services" count={machine.services.length}>
              {machine.services.map((s) => (
                <Row
                  key={s.key}
                  column={2}
                  selected={s === service}
                  liveness={s.liveness}
                  icon={Layers}
                  name={s.service.name}
                  value={s.checks.length > 0 ? formatPercent(percentOf(s.checks, span)) : undefined}
                  onSelect={() => select({ node: node!.key, machine: machine.key, service: s.key })}
                />
              ))}
            </Column>
          ) : null}
          <section className="inv-detail" aria-label="Details" aria-live="polite">
            {node ? (
              <nav className="inv-path" aria-label="Selection">
                <button type="button" onClick={() => select({ node: node.key, machine: null, service: null })}>
                  {node.host.name}
                </button>
                {machine || unmanaged ? (
                  <>
                    <ChevronRight size={13} aria-hidden="true" />
                    <button type="button" onClick={() => select({ service: null })}>
                      {machine?.guest.name ?? unmanaged?.guest.name ?? `VMID ${unmanaged?.guest.vmid}`}
                    </button>
                  </>
                ) : null}
                {service ? (
                  <>
                    <ChevronRight size={13} aria-hidden="true" />
                    <span>{service.service.name}</span>
                  </>
                ) : null}
              </nav>
            ) : null}
            {service ? (
              <ServiceDetail service={service} span={span} />
            ) : machine && node ? (
              <MachineDetail node={node} machine={machine} span={span} />
            ) : unmanaged && node ? (
              <UnmanagedDetail node={node} entry={unmanaged} />
            ) : node ? (
              <NodeDetail node={node} span={span} />
            ) : null}
          </section>
        </div>
      )}
    </>
  );
}
