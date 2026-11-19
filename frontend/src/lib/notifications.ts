import { useSyncExternalStore } from "react";
import type { NotificationItem } from "../api/types";
import { runResourcePath } from "./format";

/** Each kind borrows the mark of the status it reads as; a kind this build does not know gets the plain dot. */
export const marks: Record<string, string> = {
  "check-down": "failed",
  "check-up": "succeeded",
  "run-approval": "awaiting-approval",
  "run-failed": "failed",
  "run-uncertain": "uncertain",
  "agent-offline": "failed",
  "agent-online": "succeeded",
  "agent-update-failed": "failed",
};

/** The inbox's filters, each the kinds the server is asked for. */
export const kindGroups = {
  runs: ["run-approval", "run-failed", "run-uncertain"],
  uptime: ["check-down", "check-up"],
  agents: ["agent-offline", "agent-online", "agent-update-failed"],
} as const satisfies Record<string, readonly string[]>;

export type KindGroup = keyof typeof kindGroups;

/** Where a notification leads: its run, else its resource, else the Agents page; nothing once the resource is gone. */
export function pathOf(item: NotificationItem): string | null {
  if (item.runId) return `/runs/${item.runId}`;
  if (item.resourceId && item.resourceKind) return runResourcePath({ resourceKind: item.resourceKind, resourceId: item.resourceId });
  if (item.agentId) return "/agents";
  return null;
}

const resourceWords: Record<string, string> = { service: "Service", guest: "Machine", installation: "Node" };

/** What `pathOf` leads to, in a word or two, or null when nothing. */
export function targetLabel(item: NotificationItem): string | null {
  if (item.runId) return item.runNumber === null ? "Run" : `Run #${item.runNumber}`;
  if (item.resourceId && item.resourceKind) return resourceWords[item.resourceKind] ?? "Inventory";
  if (item.agentId) return "Agents";
  return null;
}

// What this browser has read: every notification up to this id. One store, so the bell and the page agree at once,
// and other tabs follow through the storage event.
// ponytail: what was read is kept per browser; move it to the user's row if the count should follow them across devices.
const SEEN_KEY = "octopushy.notifications.seen";
const listeners = new Set<() => void>();

function readSeen(): number {
  return Number(localStorage.getItem(SEEN_KEY)) || 0;
}

function subscribe(listener: () => void) {
  listeners.add(listener);
  const fromOtherTab = (event: StorageEvent) => {
    if (event.key === SEEN_KEY || event.key === null) listener();
  };
  window.addEventListener("storage", fromOtherTab);
  return () => {
    listeners.delete(listener);
    window.removeEventListener("storage", fromOtherTab);
  };
}

/** Marks everything up to `id` read. */
export function markSeen(id: number) {
  localStorage.setItem(SEEN_KEY, String(id));
  listeners.forEach((listener) => listener());
}

/** The id of the newest notification read in this browser, 0 when none. */
export function useSeen(): number {
  return useSyncExternalStore(subscribe, readSeen, () => 0);
}

const isCheck = (item: NotificationItem) => item.kind === "check-down" || item.kind === "check-up";

/** One line of a day: a notification, or a check that went down twice or more in a row, folded with its ups. */
export type InboxLine = { item: NotificationItem } | { fold: NotificationItem[] };

/** Folds each run of consecutive check notifications for one resource that went down twice or more. Newest first in, newest first out. */
export function foldChecks(items: NotificationItem[]): InboxLine[] {
  const lines: InboxLine[] = [];
  for (let i = 0; i < items.length; ) {
    const first = items[i];
    let end = i + 1;
    if (isCheck(first) && first.resourceId)
      while (end < items.length && isCheck(items[end]) && items[end].resourceId === first.resourceId) end++;
    const run = items.slice(i, end);
    if (run.filter((item) => item.kind === "check-down").length >= 2) lines.push({ fold: run });
    else lines.push(...run.map((item) => ({ item })));
    i = end;
  }
  return lines;
}

const pad = (n: number) => String(n).padStart(2, "0");

/** The local time of day, as 09:05. */
export function clockTime(value: string): string {
  const date = new Date(value);
  return `${pad(date.getHours())}:${pad(date.getMinutes())}`;
}

const weekdays = ["Sunday", "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday"];
const months = ["January", "February", "March", "April", "May", "June", "July", "August", "September", "October", "November", "December"];
/** As Tuesday 17 November, the same in every locale and runtime, as the rest of the interface is English. */
const dayName = (date: Date) => `${weekdays[date.getDay()]} ${date.getDate()} ${months[date.getMonth()]}`;

/** Notifications under their local day, newest day first, each labelled Today, Yesterday or as Tuesday 17 November. */
export function byDay(items: NotificationItem[], now = new Date()): { key: string; label: string; items: NotificationItem[] }[] {
  const keyOf = (date: Date) => `${date.getFullYear()}-${pad(date.getMonth() + 1)}-${pad(date.getDate())}`;
  const today = keyOf(now);
  const yesterday = keyOf(new Date(now.getFullYear(), now.getMonth(), now.getDate() - 1));
  const days: { key: string; label: string; items: NotificationItem[] }[] = [];
  for (const item of items) {
    const date = new Date(item.createdAt);
    const key = keyOf(date);
    let day = days[days.length - 1];
    if (day?.key !== key) {
      day = { key, label: key === today ? "Today" : key === yesterday ? "Yesterday" : dayName(date), items: [] };
      days.push(day);
    }
    day.items.push(item);
  }
  return days;
}
