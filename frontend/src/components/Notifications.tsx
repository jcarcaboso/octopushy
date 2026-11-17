import { Bell, X } from "lucide-react";
import { useCallback, useEffect, useRef, useState } from "react";
import { createPortal } from "react-dom";
import { Link } from "react-router";
import { useNotifications } from "../api/queries";
import type { NotificationItem } from "../api/types";
import { plural, runResourcePath } from "../lib/format";
import { StatusMark } from "./Badge";
import { RelativeTime } from "./RelativeTime";

/** At most this many pop-ups at once; the rest of a burst is one line that opens the list. */
const MAX_TOASTS = 3;
const TOAST_MS = 8_000;
/** Polls are ten seconds apart. A longer gap means the tab was away, and what happened meanwhile is not news any more. */
const AWAY_MS = 60_000;
// ponytail: what was read is kept per browser; move it to the user's row if the count should follow them across devices.
const SEEN_KEY = "octopushy.notifications.seen";

/** Each kind borrows the mark of the status it reads as; a kind this build does not know gets the plain dot. */
const marks: Record<string, string> = {
  "check-down": "failed",
  "check-up": "succeeded",
  "run-approval": "awaiting-approval",
  "run-failed": "failed",
  "run-uncertain": "uncertain",
};

function pathOf(item: NotificationItem): string | null {
  if (item.runId) return `/runs/${item.runId}`;
  if (item.resourceId && item.resourceKind) return runResourcePath({ resourceKind: item.resourceKind, resourceId: item.resourceId });
  return null;
}

/** One notification as a line that leads to its run or resource, when it still has one. */
function Notice({ item, time = false, unread = false, onFollow }: { item: NotificationItem; time?: boolean; unread?: boolean; onFollow: () => void }) {
  const path = pathOf(item);
  const content = (
    <>
      <StatusMark status={marks[item.kind] ?? item.kind} decorative />
      <span className="notice__text">
        <span className="notice__title">
          {item.title}
          {unread ? <span className="visually-hidden"> (unread)</span> : null}
        </span>
        {item.body ? <span className="notice__body">{item.body}</span> : null}
      </span>
      {time ? <RelativeTime value={item.createdAt} /> : null}
    </>
  );
  const className = unread ? "notice notice--unread" : "notice";
  return path ? (
    <Link to={path} className={className} onClick={onFollow}>
      {content}
    </Link>
  ) : (
    <div className={className}>{content}</div>
  );
}

function Toast({ item, paused, onClose }: { item: NotificationItem; paused: boolean; onClose: (id: number) => void }) {
  useEffect(() => {
    if (paused) return;
    const timer = setTimeout(() => onClose(item.id), TOAST_MS);
    return () => clearTimeout(timer);
  }, [paused, onClose, item.id]);

  return (
    <li className="toast">
      <Notice item={item} onFollow={() => onClose(item.id)} />
      <button type="button" className="icon-button" aria-label={`Dismiss: ${item.title}`} onClick={() => onClose(item.id)}>
        <X size={15} aria-hidden="true" />
      </button>
      {/* The bar runs for as long as the timer above does. A pause restarts both, so it stands full meanwhile. */}
      <span
        className={paused ? "toast__timer" : "toast__timer toast__timer--running"}
        style={{ animationDuration: `${TOAST_MS}ms` }}
        aria-hidden="true"
      />
    </li>
  );
}

const noToasts: { shown: NotificationItem[]; more: number } = { shown: [], more: 0 };

/**
 * The bell with the latest notifications, and the pop-ups for those that arrive while the page is open. Nothing pops up
 * for what was already there on load or happened while the tab was away: the count on the bell says so.
 */
export function Notifications() {
  const { data, dataUpdatedAt } = useNotifications();
  const items = data?.items ?? [];
  const latest = items[0]?.id ?? 0;
  const [open, setOpen] = useState(false);
  const [seen, setSeen] = useState(() => Number(localStorage.getItem(SEEN_KEY)) || 0);
  const [toasts, setToasts] = useState(noToasts);
  // The pop-ups wait while the pointer is on them or the focus is in them, whichever came first and whichever is left.
  const [hovered, setHovered] = useState(false);
  const [focused, setFocused] = useState(false);
  const [last, setLast] = useState<{ at: number; ids: Set<number> } | null>(null);
  const ref = useRef<HTMLDivElement>(null);

  // Each answer from the server is compared with the one before it, while rendering: what it adds pops up.
  if (data && last?.at !== dataUpdatedAt) {
    setLast({ at: dataUpdatedAt, ids: new Set(items.map((item) => item.id)) });
    const fresh = last && dataUpdatedAt - last.at <= AWAY_MS ? items.filter((item) => !last.ids.has(item.id)) : [];
    if (fresh.length > 0 && !open) {
      const all = [...fresh, ...toasts.shown];
      setToasts({ shown: all.slice(0, MAX_TOASTS), more: toasts.more + Math.max(0, all.length - MAX_TOASTS) });
    }
  }

  const dismiss = useCallback((id: number) => {
    // A pop-up that goes takes the focus with it, and no blur says so.
    setFocused(false);
    setToasts((current) => {
      const shown = current.shown.filter((item) => item.id !== id);
      return shown.length > 0 ? { shown, more: current.more } : noToasts;
    });
  }, []);

  const show = () => {
    setOpen(true);
    setFocused(false);
    setToasts(noToasts);
  };
  // Closing is what marks them read, so the unread ones stay marked while the list is open.
  const close = useCallback(() => {
    setOpen(false);
    setSeen(latest);
    localStorage.setItem(SEEN_KEY, String(latest));
  }, [latest]);

  useEffect(() => {
    if (!open) return;
    const outside = (event: PointerEvent) => {
      if (!ref.current?.contains(event.target as Node)) close();
    };
    // On the document, since the line under the pop-ups opens the list with the focus outside it.
    const escape = (event: KeyboardEvent) => {
      if (event.key !== "Escape") return;
      close();
      ref.current?.querySelector<HTMLButtonElement>(".notifications__toggle")?.focus();
    };
    document.addEventListener("pointerdown", outside);
    document.addEventListener("keydown", escape);
    return () => {
      document.removeEventListener("pointerdown", outside);
      document.removeEventListener("keydown", escape);
    };
  }, [open, close]);

  const unread = open ? 0 : items.filter((item) => item.id > seen).length;

  return (
    <div className="notifications" ref={ref}>
      <button
        type="button"
        className="icon-button notifications__toggle"
        aria-expanded={open}
        aria-controls="notifications-panel"
        aria-label={unread > 0 ? `Notifications, ${unread} unread` : "Notifications"}
        onClick={open ? close : show}
      >
        <Bell size={17} aria-hidden="true" />
        {unread > 0 ? (
          <span className="notifications__count" aria-hidden="true">
            {unread > 9 ? "9+" : unread}
          </span>
        ) : null}
      </button>
      <section id="notifications-panel" className="notifications__panel" aria-label="Notifications" hidden={!open}>
        <h2 className="notifications__heading">Notifications</h2>
        {items.length === 0 ? (
          <p className="notifications__empty muted">Nothing yet. A service that stops answering, a run that fails or one that awaits approval shows up here.</p>
        ) : (
          <ul className="notifications__list">
            {items.map((item) => (
              <li key={item.id}>
                <Notice item={item} time unread={item.id > seen} onFollow={close} />
              </li>
            ))}
          </ul>
        )}
      </section>
      {createPortal(
        <section
          className="toasts"
          aria-label="New notifications"
          aria-live="polite"
          // A touch leaves no pointer behind to leave again, so only a mouse holds them.
          onPointerEnter={(event) => setHovered(event.pointerType === "mouse")}
          onPointerLeave={() => setHovered(false)}
          onFocus={() => setFocused(true)}
          onBlur={() => setFocused(false)}
        >
          <ul>
            {toasts.shown.map((item) => (
              <Toast key={item.id} item={item} paused={hovered || focused} onClose={dismiss} />
            ))}
            {toasts.more > 0 ? (
              <li>
                <button type="button" className="toast toast--more" onClick={show}>
                  {plural(toasts.more, "more notification")}
                </button>
              </li>
            ) : null}
          </ul>
        </section>,
        document.body,
      )}
    </div>
  );
}
