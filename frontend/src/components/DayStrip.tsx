import type { UptimeCheck } from "../api/types";
import { plural } from "../lib/format";
import { barState, dailyMean } from "../lib/inventory";

/** Thirty bars, oldest day first: the mean of the checks' days. */
export function DayStrip({ label, checks }: { label: string; checks: UptimeCheck[] }) {
  const states = dailyMean(checks).map(barState);
  const count = (state: string) => states.filter((s) => s === state).length;
  const parts = [
    [count("u"), "up"],
    [count("p"), "partly up"],
    [count("d"), "down"],
    [count("-"), "without answers"],
  ] as const;
  const text = parts
    .filter(([n]) => n > 0)
    .map(([n, word]) => `${plural(n, "day")} ${word}`)
    .join(", ");
  return (
    <div className="uptime-strip uptime-strip--days" role="img" aria-label={`${label}, the last 30 days: ${text}`}>
      {states.map((state, i) => (
        <span key={i} className={`uptime-strip__bar uptime-strip__bar--${state}`} />
      ))}
    </div>
  );
}
