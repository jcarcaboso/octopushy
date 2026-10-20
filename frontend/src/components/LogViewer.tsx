import { ArrowDown } from "lucide-react";
import { useCallback, useEffect, useLayoutEffect, useRef, useState } from "react";
import { ApiError, api, errorMessage, unwrap } from "../api/client";
import { appendLog, emptyLog, isNearBottom, type LogBuffer } from "../lib/logBuffer";
import { formatClock } from "../lib/time";
import { CopyButton } from "./CopyButton";

interface Props {
  attemptId: string;
  /** The attempt's status: a queued attempt has no log yet, so nothing is fetched. */
  status: string;
  pollMs?: number;
}

const pageSize = 2_000;

/**
 * Follows an attempt's log: polls every `pollMs` until the server says it is finished,
 * appends new lines, and keeps the view at the end unless the user scrolled up.
 */
export function LogViewer({ attemptId, status, pollMs = 1_000 }: Props) {
  const [log, setLog] = useState<LogBuffer>(emptyLog);
  const [error, setError] = useState<string | null>(null);
  const [missing, setMissing] = useState(false);
  // Whether the view follows new lines: true until the user scrolls up, again once they return to the end.
  const [following, setFollowing] = useState(true);
  const scroller = useRef<HTMLDivElement>(null);
  const stick = useRef(true);
  const waiting = status === "queued";

  useEffect(() => {
    if (waiting) return;
    let cancelled = false;
    let timer: ReturnType<typeof setTimeout> | undefined;
    let cursor = -1;

    async function poll() {
      let delay = pollMs;
      try {
        const chunk = await unwrap(
          api.GET("/api/attempts/{id}/log", {
            params: { path: { id: attemptId }, query: { after: cursor, limit: pageSize } },
          }),
        );
        if (cancelled) return;
        setError(null);
        cursor = Math.max(cursor, chunk.next);
        setLog((current) => appendLog(current, chunk));
        const full = chunk.lines.length >= pageSize;
        if (chunk.finished && !full) return;
        if (full) delay = 0;
      } catch (e) {
        if (cancelled) return;
        if (e instanceof ApiError && e.status === 404) {
          // The attempt is gone (or never existed); there is nothing to follow.
          setError(null);
          setMissing(true);
          return;
        }
        setError(errorMessage(e));
        delay = Math.max(pollMs * 3, 3_000);
      }
      timer = setTimeout(poll, delay);
    }

    void poll();
    return () => {
      cancelled = true;
      clearTimeout(timer);
    };
  }, [attemptId, pollMs, waiting]);

  useLayoutEffect(() => {
    const el = scroller.current;
    if (el && stick.current) el.scrollTop = el.scrollHeight;
  }, [log.lines]);

  const onScroll = useCallback(() => {
    const el = scroller.current;
    if (!el) return;
    stick.current = isNearBottom(el);
    setFollowing(stick.current);
  }, []);

  const jump = () => {
    const el = scroller.current;
    if (!el) return;
    stick.current = true;
    el.scrollTop = el.scrollHeight;
    setFollowing(true);
  };

  if (waiting) {
    return <p className="log-empty muted">The log starts when an agent picks up this attempt.</p>;
  }
  if (missing) {
    return <p className="log-empty muted">No log is recorded for this attempt.</p>;
  }

  const live = !log.finished;
  return (
    <div className="log">
      <div className="log__bar">
        <span className="log__state">
          {live ? <span className="live-dot" aria-hidden="true" /> : null}
          {live ? "Live" : "Finished"} · <span className="num">{log.lines.length + log.dropped}</span>{" "}
          {log.lines.length + log.dropped === 1 ? "line" : "lines"}
        </span>
        {log.lines.length > 0 ? (
          <CopyButton text={log.lines.map((l) => l.text).join("\n")} label="Copy log" />
        ) : null}
      </div>
      {error ? (
        <div className="log__error" role="status">
          Could not load the log: {error} Retrying.
        </div>
      ) : null}
      <div className="log__scroll" ref={scroller} onScroll={onScroll} tabIndex={0} role="log" aria-live="off" aria-label="Attempt log">
        {log.dropped > 0 ? <div className="log__line log__line--agent">… {log.dropped} earlier lines not shown</div> : null}
        {log.lines.length === 0 ? (
          <div className="log__placeholder">{live ? "Waiting for output…" : "No log lines."}</div>
        ) : (
          log.lines.map((line) => (
            <div key={String(line.sequence)} className={`log__line log__line--${line.stream}`}>
              <span className="log__time" aria-hidden="true">
                {formatClock(line.unixMs)}
              </span>
              {line.stream === "agent" ? <span className="log__tag">agent</span> : null}
              {line.stream === "stderr" ? <span className="visually-hidden">stderr: </span> : null}
              <span className="log__text">{line.text}</span>
            </div>
          ))
        )}
      </div>
      {!following && log.lines.length > 0 ? (
        <button type="button" className="button button--small log__jump" onClick={jump}>
          <ArrowDown size={14} aria-hidden="true" /> Jump to latest
        </button>
      ) : null}
    </div>
  );
}
