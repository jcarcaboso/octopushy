import { Activity, Bell, CheckCheck, ChevronDown, ChevronRight, Search, Webhook } from "lucide-react";
import { useEffect, useMemo, useState } from "react";
import { Link, useSearchParams } from "react-router";
import { useNotificationInbox, useNotifications } from "../api/queries";
import type { NotificationItem } from "../api/types";
import { StatusMark } from "../components/Badge";
import { PageHeader } from "../components/PageHeader";
import { EmptyState, ErrorMessage, PageSkeleton, Spinner } from "../components/States";
import { plural } from "../lib/format";
import { byDay, clockTime, foldChecks, kindGroups, markSeen, marks, pathOf, targetLabel, useSeen, type KindGroup } from "../lib/notifications";
import { formatAbsolute } from "../lib/time";

const groups: { value: KindGroup | null; label: string }[] = [
  { value: null, label: "All" },
  { value: "runs", label: "Runs" },
  { value: "uptime", label: "Uptime" },
  { value: "agents", label: "Agents" },
];

const noKinds: readonly string[] = [];
/** Typing waits this long before the server is asked. */
const SEARCH_MS = 300;

function Time({ value }: { value: string }) {
  return (
    <time className="inbox-row__time" dateTime={value} title={formatAbsolute(value)}>
      {clockTime(value)}
    </time>
  );
}

function Row({ item, unread }: { item: NotificationItem; unread: boolean }) {
  const path = pathOf(item);
  const label = targetLabel(item);
  // A check that went down says why after its URL.
  const split = item.kind === "check-down" && item.body ? item.body.indexOf(" · ") : -1;
  const detail = split >= 0 ? item.body!.slice(split + 3) : null;
  const where = item.resourceAddress ?? item.agentName;
  const content = (
    <>
      <Time value={item.createdAt} />
      <StatusMark status={marks[item.kind] ?? item.kind} decorative />
      <span className="inbox-row__what">
        <span className="inbox-row__title">{item.title}</span>
        {unread ? (
          <span className="inbox-row__dot">
            <span className="visually-hidden">(unread)</span>
          </span>
        ) : null}
        {detail ? <span className="inbox-row__detail">{detail}</span> : null}
      </span>
      <span className="inbox-row__where">
        {where ? (
          <span className="chip mono" title={where}>
            {where}
          </span>
        ) : null}
      </span>
      {path && label ? (
        <span className="inbox-row__go">
          {label}
          <ChevronRight size={14} aria-hidden="true" />
        </span>
      ) : (
        <span className="inbox-row__go inbox-row__go--none" title="What this was about is gone">
          Gone
        </span>
      )}
    </>
  );
  const className = unread ? "inbox-row inbox-row--unread" : "inbox-row";
  return path ? (
    <Link to={path} className={className}>
      {content}
    </Link>
  ) : (
    <div className={className}>{content}</div>
  );
}

/** A check that went down again and again, as one line that opens in place on its notifications. */
function Fold({ run, seen }: { run: NotificationItem[]; seen: number }) {
  const [open, setOpen] = useState(false);
  const newest = run[0];
  const oldest = run[run.length - 1];
  const downs = run.filter((item) => item.kind === "check-down").length;
  const unread = run.some((item) => item.id > seen);
  const address = newest.resourceAddress ?? "A check";
  const id = `fold-${newest.id}`;
  return (
    <li>
      <button
        type="button"
        className={unread ? "inbox-row inbox-fold inbox-row--unread" : "inbox-row inbox-fold"}
        aria-expanded={open}
        aria-controls={id}
        onClick={() => setOpen(!open)}
      >
        <Time value={newest.createdAt} />
        <Activity className="status-mark status-mark--warning" size={15} strokeWidth={2.25} aria-hidden="true" />
        <span className="inbox-row__what">
          <span className="inbox-row__title">
            {address} went down {downs} times
          </span>
          {unread ? (
            <span className="inbox-row__dot">
              <span className="visually-hidden">(unread)</span>
            </span>
          ) : null}
          <span className="inbox-row__detail">
            {clockTime(oldest.createdAt)} to {clockTime(newest.createdAt)}, {newest.kind === "check-up" ? "up now" : "down now"}
          </span>
        </span>
        <span className="inbox-row__where">
          {newest.resourceAddress ? (
            <span className="chip mono" title={newest.resourceAddress}>
              {newest.resourceAddress}
            </span>
          ) : null}
        </span>
        <span className="inbox-row__go">
          {run.length}
          <ChevronDown className="inbox-fold__chevron" size={14} aria-hidden="true" />
        </span>
      </button>
      <ul id={id} className="inbox-fold__rows" hidden={!open}>
        {run.map((item) => (
          <li key={item.id}>
            <Row item={item} unread={item.id > seen} />
          </li>
        ))}
      </ul>
    </li>
  );
}

/**
 * Everything Octopushy told in the 31 days it keeps notifications, newest first under a heading per day. The filters
 * are the server's, so they cover every day and not only the pages loaded.
 */
export function NotificationsPage() {
  const [params, setParams] = useSearchParams();
  const kindParam = params.get("kind");
  const group = kindParam && kindParam in kindGroups ? (kindParam as KindGroup) : null;
  const unreadOnly = params.get("unread") === "1";
  const q = params.get("q") ?? "";
  const [text, setText] = useState(q);
  const seen = useSeen();

  const set = (key: string, value: string | null) =>
    setParams(
      (current) => {
        const next = new URLSearchParams(current);
        if (value) next.set(key, value);
        else next.delete(key);
        return next;
      },
      { replace: true },
    );

  // What is typed goes to the server once typing stops.
  useEffect(() => {
    const wanted = text.trim();
    if (wanted === q) return;
    const timer = setTimeout(
      () =>
        setParams(
          (current) => {
            const next = new URLSearchParams(current);
            if (wanted) next.set("q", wanted);
            else next.delete("q");
            return next;
          },
          { replace: true },
        ),
      SEARCH_MS,
    );
    return () => clearTimeout(timer);
  }, [text, q, setParams]);

  const filter = useMemo(
    () => ({ kinds: group ? kindGroups[group] : noKinds, q, after: unreadOnly ? seen : null }),
    [group, q, unreadOnly, seen],
  );
  // The bell's own poll of the latest: it says how many are unread and when something new arrived.
  const latest = useNotifications().data;
  const latestId = latest?.items[0]?.id ?? 0;
  const inbox = useNotificationInbox(filter, latestId);
  const items = useMemo(() => inbox.data?.pages.flatMap((page) => page.items) ?? [], [inbox.data]);
  const days = useMemo(() => byDay(items), [items]);

  const latestUnread = latest?.items.filter((item) => item.id > seen).length ?? 0;
  // The bell's list holds the latest fifty; when all of them are unread there may be more.
  const more = latest !== undefined && latestUnread === latest.items.length && latest.nextBefore !== null;
  const newest = Math.max(latestId, items[0]?.id ?? 0);
  const filtered = Boolean(group || unreadOnly || q);

  const clear = () => {
    setText("");
    setParams(new URLSearchParams(), { replace: true });
  };

  return (
    <div className="inbox">
      <PageHeader
        title="Notifications"
        meta={latestUnread > 0 ? <strong className="inbox__unread">{more ? `${latestUnread}+` : latestUnread} unread</strong> : null}
        actions={
          <>
            <button type="button" className="button" disabled={newest <= seen} onClick={() => markSeen(newest)}>
              <CheckCheck size={15} aria-hidden="true" />
              Mark all read
            </button>
            <Link to="/notifications/webhook" className="button button--ghost">
              <Webhook size={15} aria-hidden="true" />
              Webhook
            </Link>
          </>
        }
      />

      <div className="inbox__toolbar">
        <div className="segmented" role="group" aria-label="Kind">
          {groups.map((g) => (
            <button
              key={g.label}
              type="button"
              className="segmented__item"
              aria-pressed={group === g.value}
              onClick={() => set("kind", g.value)}
            >
              {g.label}
            </button>
          ))}
        </div>
        <label className="inbox__check">
          <input type="checkbox" checked={unreadOnly} onChange={(event) => set("unread", event.target.checked ? "1" : null)} />
          Unread only
        </label>
        {inbox.isFetching && inbox.isPlaceholderData ? <Spinner label="Loading notifications" /> : null}
        <label className="inbox__search">
          <Search size={15} aria-hidden="true" />
          <input
            type="search"
            aria-label="Search notifications"
            placeholder="Address or agent"
            value={text}
            maxLength={200}
            onChange={(event) => setText(event.target.value)}
          />
        </label>
      </div>

      {inbox.isPending ? (
        <PageSkeleton rows={2} />
      ) : !inbox.data ? (
        <ErrorMessage error={inbox.error} title="The notifications could not be loaded" />
      ) : items.length === 0 ? (
        <div className="inbox__list">
          {filtered ? (
            <EmptyState
              compact
              title="Nothing matches."
              actions={
                <button type="button" className="button button--small" onClick={clear}>
                  Clear filters
                </button>
              }
            >
              {unreadOnly && !group && !q ? "Everything is read." : null}
            </EmptyState>
          ) : (
            <EmptyState compact icon={Bell} title="No notifications yet">
              A service that stops answering, a run that fails or one that awaits approval shows up here.
            </EmptyState>
          )}
        </div>
      ) : (
        <div className="inbox__days">
          {days.map((day) => (
            <section key={day.key} className="inbox__day" aria-labelledby={`day-${day.key}`}>
              <div className="inbox__day-head">
                <h2 id={`day-${day.key}`}>{day.label}</h2>
                <span className="muted small">{plural(day.items.length, "notification")}</span>
              </div>
              <ul className="inbox__list">
                {foldChecks(day.items).map((line) =>
                  "fold" in line ? (
                    <Fold key={line.fold[0].id} run={line.fold} seen={seen} />
                  ) : (
                    <li key={line.item.id}>
                      <Row item={line.item} unread={line.item.id > seen} />
                    </li>
                  ),
                )}
              </ul>
            </section>
          ))}
        </div>
      )}

      {inbox.data ? (
        <>
          <ErrorMessage error={inbox.isFetchNextPageError ? inbox.error : null} title="Older notifications could not be loaded" />
          {inbox.hasNextPage ? (
            <div className="inbox__foot">
              <button type="button" className="button" disabled={inbox.isFetchingNextPage} onClick={() => void inbox.fetchNextPage()}>
                {inbox.isFetchingNextPage ? "Loading…" : "Load older"}
              </button>
            </div>
          ) : (
            <p className="inbox__foot muted small">Notifications are kept for 31 days.</p>
          )}
        </>
      ) : null}
    </div>
  );
}
