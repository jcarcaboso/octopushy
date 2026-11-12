import { Link } from "react-router";
import type { OverviewCounts } from "../../api/types";
import { StatusMark } from "../../components/Badge";
import { statusStyle } from "../../components/status";
import { Tile } from "./Tile";

const states: { key: keyof OverviewCounts; status: string; filter: string; label?: string }[] = [
  { key: "inSync", status: "in-sync", filter: "in-sync" },
  { key: "pending", status: "pending", filter: "pending" },
  { key: "running", status: "running", filter: "running,queued", label: "Queued or running" },
  { key: "awaitingApproval", status: "awaiting-approval", filter: "awaiting-approval" },
  { key: "blocked", status: "blocked", filter: "blocked" },
  { key: "failed", status: "failed", filter: "failed" },
  { key: "uncertain", status: "uncertain", filter: "uncertain" },
  { key: "invalid", status: "invalid", filter: "invalid" },
  { key: "missing", status: "missing", filter: "missing" },
];

const SIZE = 132;
const STROKE = 12;
const RADIUS = (SIZE - STROKE) / 2;
const CIRCUMFERENCE = 2 * Math.PI * RADIUS;

/** One arc per state, in the order of the legend. Decoration: the legend carries the numbers. */
function Ring({ parts, total }: { parts: { tone: string; value: number }[]; total: number }) {
  const gap = parts.length > 1 ? 4 : 0;
  // Where each arc starts: the length of the arcs before it.
  const starts = parts.map((_, i) => parts.slice(0, i).reduce((sum, p) => sum + (CIRCUMFERENCE * p.value) / total, 0));
  return (
    <svg className="guest-ring__svg" width={SIZE} height={SIZE} viewBox={`0 0 ${SIZE} ${SIZE}`} aria-hidden="true">
      <circle className="guest-ring__track" cx={SIZE / 2} cy={SIZE / 2} r={RADIUS} strokeWidth={STROKE} />
      {parts.map((part, i) => {
        const length = Math.max((CIRCUMFERENCE * part.value) / total - gap, 1);
        return (
          <circle
            key={i}
            className={`guest-ring__arc guest-ring__arc--${part.tone}`}
            cx={SIZE / 2}
            cy={SIZE / 2}
            r={RADIUS}
            strokeWidth={STROKE}
            strokeDasharray={`${length} ${CIRCUMFERENCE - length}`}
            strokeDashoffset={-starts[i]}
          />
        );
      })}
    </svg>
  );
}

export function GuestsTile({ counts }: { counts: OverviewCounts }) {
  const shown = states.filter((s) => s.key === "inSync" || counts[s.key] > 0);
  const parts = shown.filter((s) => counts[s.key] > 0).map((s) => ({ tone: statusStyle(s.status).tone, value: counts[s.key] }));
  return (
    <Tile id="guests" title="Guests" link={{ to: "/inventory", label: "Inventory" }}>
      {counts.guests === 0 ? (
        <p className="muted">No guests yet. They appear once the lab repository declares some.</p>
      ) : (
        <div className="guest-summary">
          <div className="guest-ring">
            <Ring parts={parts} total={counts.guests} />
            <p className="guest-ring__centre">
              <span className="guest-ring__value num">
                {counts.inSync} of {counts.guests}
              </span>
              <span className="guest-ring__caption">in sync</span>
            </p>
          </div>
          <ul className="guest-legend">
            {shown.map((s) => {
              const label = s.label ?? statusStyle(s.status).label;
              const value = counts[s.key];
              return (
                <li key={s.key}>
                  <Link
                    to={`/inventory?status=${s.filter}`}
                    className="guest-legend__item"
                    aria-label={`${value} ${value === 1 ? "guest" : "guests"} ${label}`.toLowerCase()}
                  >
                    <StatusMark status={s.status} decorative size={14} />
                    <span className="guest-legend__label">{label}</span>
                    <span className="num">{value}</span>
                  </Link>
                </li>
              );
            })}
          </ul>
        </div>
      )}
    </Tile>
  );
}
