import { humanize } from "../lib/format";

export type Tone = "neutral" | "info" | "success" | "warning" | "danger" | "attention" | "uncertain" | "muted";

export type StatusIcon =
  | "check"
  | "clock"
  | "spinner"
  | "hand"
  | "ban"
  | "x"
  | "question"
  | "alert"
  | "missing"
  | "archive"
  | "minus"
  | "dot";

export interface StatusStyle {
  label: string;
  tone: Tone;
  icon: StatusIcon;
}

/**
 * Every status the API reports for guests, runs, steps, and attempts, with one label,
 * tone, and icon each, so the same word always looks the same.
 */
const statuses: Record<string, StatusStyle> = {
  "in-sync": { label: "In sync", tone: "success", icon: "check" },
  succeeded: { label: "Succeeded", tone: "success", icon: "check" },
  pending: { label: "Pending", tone: "neutral", icon: "clock" },
  queued: { label: "Queued", tone: "neutral", icon: "clock" },
  assigned: { label: "Assigned", tone: "info", icon: "spinner" },
  running: { label: "Running", tone: "info", icon: "spinner" },
  "awaiting-approval": { label: "Awaiting approval", tone: "attention", icon: "hand" },
  blocked: { label: "Blocked", tone: "warning", icon: "ban" },
  failed: { label: "Failed", tone: "danger", icon: "x" },
  uncertain: { label: "Uncertain", tone: "uncertain", icon: "question" },
  invalid: { label: "Invalid", tone: "danger", icon: "alert" },
  missing: { label: "Missing", tone: "warning", icon: "missing" },
  retired: { label: "Retired", tone: "muted", icon: "archive" },
  cancelled: { label: "Cancelled", tone: "muted", icon: "minus" },
  skipped: { label: "Skipped", tone: "muted", icon: "minus" },
};

export function statusStyle(status: string): StatusStyle {
  return statuses[status] ?? { label: humanize(status), tone: "neutral", icon: "dot" };
}

export const guestStatuses = [
  "in-sync",
  "pending",
  "queued",
  "running",
  "awaiting-approval",
  "blocked",
  "failed",
  "uncertain",
  "invalid",
  "missing",
  "retired",
] as const;

export const runStatuses = [
  "queued",
  "running",
  "awaiting-approval",
  "blocked",
  "succeeded",
  "failed",
  "uncertain",
  "cancelled",
] as const;
