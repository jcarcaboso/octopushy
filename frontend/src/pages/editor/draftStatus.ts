import type { Tone } from "../../components/status";

const statuses: Record<string, { label: string; tone: Tone }> = {
  open: { label: "Open", tone: "info" },
  conflicted: { label: "Conflicted", tone: "warning" },
  stale: { label: "Stale", tone: "muted" },
  published: { label: "Published", tone: "success" },
};

export function draftStatus(status: string) {
  return statuses[status] ?? { label: status, tone: "neutral" as Tone };
}
