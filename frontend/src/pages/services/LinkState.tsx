import type { UptimeCheck } from "../../api/types";
import { useNow } from "../../components/useNow";
import { elapsed, formatDuration } from "../../lib/time";

/** "Down" and for how long, in the danger tone. */
export function DownFor({ check }: { check: UptimeCheck }) {
  const now = useNow();
  const ms = elapsed(check.since, null, now);
  return <span className="services-down">Down{ms !== null ? ` ${formatDuration(ms)}` : ""}</span>;
}

/** How fast the link answered, or that it is down, or a dash. */
export function AnswersIn({ check }: { check: UptimeCheck | null }) {
  if (check?.status === "down") return <DownFor check={check} />;
  if (check?.status === "up" && check.elapsedMs !== null) return <span className="muted num">{check.elapsedMs} ms</span>;
  return <span className="muted">—</span>;
}
