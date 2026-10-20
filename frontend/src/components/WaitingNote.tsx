import { Clock } from "lucide-react";

/** Why an unfinished run is not progressing, such as waiting for an agent or for capacity. */
export function WaitingNote({ text, small = false }: { text: string | null | undefined; small?: boolean }) {
  if (!text) return null;
  return (
    <span className={small ? "waiting-note small" : "waiting-note"}>
      <Clock size={small ? 13 : 14} aria-hidden="true" /> {text}
    </span>
  );
}
