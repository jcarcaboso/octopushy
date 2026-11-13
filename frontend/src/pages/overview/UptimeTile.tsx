import { CircleCheck, TriangleAlert } from "lucide-react";
import { Link } from "react-router";
import { useUptime } from "../../api/queries";
import type { UptimeCheck } from "../../api/types";
import { DayStrip } from "../../components/DayStrip";
import { RelativeTime } from "../../components/RelativeTime";
import { Skeleton } from "../../components/States";
import { plural } from "../../lib/format";
import { formatPercent, isServiceCheck } from "../../lib/inventory";
import { Figure, Tile } from "./Tile";

const kinds = [
  { kind: "proxmox-api", label: "Nodes", of: (c: UptimeCheck) => c.kind === "proxmox-api" },
  { kind: "guest-ssh", label: "Machines", of: (c: UptimeCheck) => c.kind === "guest-ssh" },
  { kind: "service", label: "Services", of: isServiceCheck },
];

const mean = (values: number[]) => (values.length === 0 ? null : values.reduce((a, b) => a + b, 0) / values.length);

/** What a check is about, in one word: the service, the machine, or the node. */
function checkName(check: UptimeCheck): string {
  return check.resource?.split("/").pop() ?? check.host ?? check.installation.split("/").pop() ?? check.installation;
}

function KindRow({ label, checks }: { label: string; checks: UptimeCheck[] }) {
  const up = checks.filter((c) => c.status === "up").length;
  const down = checks.filter((c) => c.status === "down").length;
  const pending = checks.filter((c) => c.status === "pending").length;
  const answered = checks.length - pending;
  const text = [answered > 0 ? `${up} of ${answered} up` : null, pending > 0 ? `${pending} not answered yet` : null].filter(Boolean).join(", ");
  const Icon = down > 0 ? TriangleAlert : CircleCheck;
  return (
    <li className="uptime-kind">
      <div className="uptime-kind__head">
        <span className="uptime-kind__label">{label}</span>
        <span className={down > 0 ? "uptime-kind__state uptime-kind__state--warning" : "uptime-kind__state"}>
          {answered > 0 ? <Icon size={13} strokeWidth={2.25} aria-hidden="true" /> : null}
          <span className="num">{text}</span>
        </span>
      </div>
      <DayStrip label={label} checks={checks} />
    </li>
  );
}

export function UptimeTile() {
  const { data, error, isPending } = useUptime();
  const checks = data?.checks ?? [];
  const month = mean(checks.map((c) => c.uptime.month).filter((v): v is number => v !== null && v !== undefined));
  const down = checks.filter((c) => c.status === "down");

  let body;
  if (isPending) body = <Skeleton height={96} />;
  else if (!data) body = <p className="muted">Uptime could not be loaded.</p>;
  else if (checks.length === 0)
    body = <p className="muted">{data.checking ? "Nothing is checked yet." : "Nothing checks uptime right now: no connected agent answers uptime checks."}</p>;
  else
    body = (
      <>
        {!data.checking ? <p className="uptime-off">Nothing checks uptime right now: no connected agent answers uptime checks.</p> : null}
        <Figure value={formatPercent(month)} caption={plural(checks.length, "check")} />
        <ul className="uptime-kinds">
          {kinds.map(({ kind, label, of }) => {
            const own = checks.filter(of);
            return own.length > 0 ? <KindRow key={kind} label={label} checks={own} /> : null;
          })}
        </ul>
        {down.length > 0 ? (
          <ul className="uptime-down">
            {down.slice(0, 3).map((check) => (
              <li key={check.key}>
                <Link to="/inventory" className="uptime-down__item">
                  <span className="uptime-down__name">{checkName(check)}</span>{" "}
                  <span className="muted">
                    {check.detail ?? "is down"}
                    {check.since ? (
                      <>
                        , down since <RelativeTime value={check.since} />
                      </>
                    ) : null}
                  </span>
                </Link>
              </li>
            ))}
            {down.length > 3 ? <li className="muted small">and {down.length - 3} more</li> : null}
          </ul>
        ) : null}
      </>
    );

  return (
    <Tile id="uptime" title="Up, 30 days" link={{ to: "/inventory", label: "Inventory" }}>
      {error && data ? <p className="muted small">Showing the last data; refreshing failed.</p> : null}
      {body}
    </Tile>
  );
}
