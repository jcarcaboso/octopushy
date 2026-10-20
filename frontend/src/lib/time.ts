const SECOND = 1_000;
const MINUTE = 60 * SECOND;
const HOUR = 60 * MINUTE;
const DAY = 24 * HOUR;

export type DateInput = Date | string | number;

export function toDate(value: DateInput): Date {
  return value instanceof Date ? value : new Date(value);
}

const absoluteFormat = new Intl.DateTimeFormat(undefined, {
  year: "numeric",
  month: "short",
  day: "numeric",
  hour: "2-digit",
  minute: "2-digit",
  second: "2-digit",
});

const dateFormat = new Intl.DateTimeFormat(undefined, { year: "numeric", month: "short", day: "numeric" });

/** The full local date and time, for titles and tooltips. */
export function formatAbsolute(value: DateInput): string {
  return absoluteFormat.format(toDate(value));
}

/**
 * A compact relative time: "just now", "4 min ago", "in 2 h", "3 days ago".
 * Past 30 days it falls back to the date.
 */
export function formatRelative(value: DateInput, now: number = Date.now()): string {
  const date = toDate(value);
  const diff = date.getTime() - now;
  const past = diff <= 0;
  const abs = Math.abs(diff);
  const wrap = (text: string) => (past ? `${text} ago` : `in ${text}`);

  if (Number.isNaN(abs)) return "unknown";
  // Small differences either way (including clock skew with the server) read as now.
  if (abs < 45 * SECOND) return "just now";
  if (abs < 90 * SECOND) return wrap("1 min");
  if (abs < 45 * MINUTE) return wrap(`${Math.round(abs / MINUTE)} min`);
  if (abs < 90 * MINUTE) return wrap("1 h");
  if (abs < 22 * HOUR) return wrap(`${Math.round(abs / HOUR)} h`);
  if (abs < 36 * HOUR) return past ? "yesterday" : "tomorrow";
  if (abs < 30 * DAY) return wrap(`${Math.round(abs / DAY)} days`);
  return dateFormat.format(date);
}

/** A duration such as "850 ms", "12 s", "3 min 4 s", "2 h 5 min", "3 d 4 h". */
export function formatDuration(ms: number): string {
  if (!Number.isFinite(ms) || ms < 0) return "—";
  if (ms < SECOND) return `${Math.round(ms)} ms`;
  if (ms < MINUTE) return `${Math.round(ms / SECOND)} s`;
  if (ms < HOUR) {
    const m = Math.floor(ms / MINUTE);
    const s = Math.round((ms % MINUTE) / SECOND);
    return s ? `${m} min ${s} s` : `${m} min`;
  }
  if (ms < DAY) {
    const h = Math.floor(ms / HOUR);
    const m = Math.round((ms % HOUR) / MINUTE);
    return m ? `${h} h ${m} min` : `${h} h`;
  }
  const d = Math.floor(ms / DAY);
  const h = Math.round((ms % DAY) / HOUR);
  return h ? `${d} d ${h} h` : `${d} d`;
}

export function formatUptime(seconds: number | null): string {
  if (seconds === null) return "—";
  if (seconds < 60) return `${seconds} s`;
  return formatDuration(seconds * SECOND);
}

/** The time between two instants, or until now when the end is missing. */
export function elapsed(start: DateInput | null | undefined, end?: DateInput | null, now = Date.now()) {
  if (!start) return null;
  const until = end ? toDate(end).getTime() : now;
  return until - toDate(start).getTime();
}

export function formatClock(unixMs: number): string {
  const d = new Date(unixMs);
  const pad = (n: number) => String(n).padStart(2, "0");
  return `${pad(d.getHours())}:${pad(d.getMinutes())}:${pad(d.getSeconds())}`;
}
