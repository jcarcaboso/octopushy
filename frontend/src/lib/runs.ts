import type { RunSummary } from "../api/types";
import { statusStyle, type Tone } from "../components/status";
import { humanize } from "./format";
import { elapsed, formatDuration } from "./time";

type Step = RunSummary["steps"][number];

export interface RunTarget {
  /** What the run works on, in the operator's words. */
  type: "Machine" | "Service" | "Node" | "Other";
  name: string;
  /** The machine a service runs on. */
  host: string | null;
  /** The Proxmox installation, the second part of the address. */
  node: string | null;
}

/** A run's target from its address (`lab/node/machine/service`), so a list need not show the whole path. */
export function runTarget(run: Pick<RunSummary, "address" | "resourceKind">): RunTarget {
  const [, node = null, machine, service] = run.address.split("/");
  switch (run.resourceKind) {
    case "service":
      return { type: "Service", name: service ?? machine ?? run.address, host: service ? (machine ?? null) : null, node };
    case "guest":
      return { type: "Machine", name: machine ?? run.address, host: null, node };
    case "installation":
      return { type: "Node", name: node ?? run.address, host: null, node };
    default:
      return { type: "Other", name: run.address, host: null, node: null };
  }
}

export type RunSection = "waiting" | "progress" | "earlier";

/** Where a run sits in the inbox: it needs the operator, it is moving on its own, or it has ended. */
export function runSection(status: string): RunSection {
  if (status === "awaiting-approval" || status === "blocked" || status === "uncertain") return "waiting";
  if (status === "queued" || status === "running") return "progress";
  return "earlier";
}

const shortReasons: Record<string, string> = {
  revision: "Revision",
  manual: "Manual",
  schedule: "Schedule",
  secret: "Secret",
};

/** One fixed word for why a run started. */
export function reasonShort(reason: string): string {
  return shortReasons[reason] ?? humanize(reason);
}

export type SegmentTone = Tone | "done" | "todo";

export interface RunProgress {
  /** One segment per step that was not skipped, in order. */
  segments: SegmentTone[];
  label: string;
  tone: Tone;
  /** A few words after the label: what the plan changes, why the run waits, how long it took. */
  note: string | null;
  /** The whole progress in words, for a screen reader. */
  description: string;
  /** The step waiting for the operator's decision. */
  approval: Step | null;
}

const isDone = (step: Step) => step.status === "succeeded" || step.status === "skipped";

/** What the plans of a run change, added up: a pipeline's approval covers every stage. Null when they change nothing. */
function countsText(steps: Step[]): string | null {
  const text = (["create", "update", "replace", "delete"] as const)
    .map((kind) => [steps.reduce((sum, s) => sum + (s.counts?.[kind] ?? 0), 0), kind] as const)
    .filter(([n]) => n > 0)
    .map(([n, kind]) => `${n} to ${kind}`)
    .join(", ");
  return text || null;
}

/** A run's steps as a track of segments, with the stage it is at and a few words about it. */
export function runProgress(run: RunSummary, now = Date.now()): RunProgress {
  const steps = run.steps.filter((s) => s.status !== "skipped");
  const succeeded = run.status === "succeeded";
  const current = run.steps.find((s) => !isDone(s)) ?? null;
  const status = statusStyle(run.status);
  const segments = steps.map((step): SegmentTone => {
    if (step.status === "succeeded") return succeeded ? "success" : "done";
    if (step.status === "pending") return "todo";
    return statusStyle(step.status).tone;
  });
  const approval = run.steps.find((s) => s.status === "awaiting-approval" && s.kind === "approval") ?? null;
  const stage = current ? humanize(current.name) : null;
  const ended = run.finishedAt !== null;

  let note: string | null;
  if (succeeded) {
    const took = elapsed(run.startedAt, run.finishedAt, now);
    note = took === null ? null : formatDuration(took);
  } else if (approval) {
    note = countsText(run.steps) ?? run.detail;
  } else if (ended) {
    note = run.detail;
  } else {
    note = run.waiting ?? run.detail ?? (current ? statusStyle(current.status).label : null);
  }

  const label = !stage ? status.label : ended ? `${status.label} at ${stage}` : stage;
  const place = current ? `Step ${steps.indexOf(current) + 1} of ${steps.length}: ` : "";
  return {
    segments,
    label,
    tone: !ended && current ? statusStyle(current.status).tone : status.tone,
    note,
    description: `${place}${label}${!ended && current ? `, ${statusStyle(current.status).label.toLowerCase()}` : ""}`,
    approval,
  };
}
