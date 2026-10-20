import { useNow } from "./useNow";
import { formatAbsolute, formatRelative, toDate, type DateInput } from "../lib/time";

interface Props {
  value: DateInput | null | undefined;
  /** Shown when there is no value. */
  empty?: string;
}

export function RelativeTime({ value, empty = "—" }: Props) {
  const current = useNow();
  if (value === null || value === undefined || value === "") {
    return <span className="muted">{empty}</span>;
  }
  const date = toDate(value);
  return (
    <time className="reltime" dateTime={date.toISOString()} title={formatAbsolute(date)}>
      {formatRelative(date, current)}
    </time>
  );
}
