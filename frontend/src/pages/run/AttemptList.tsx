import { Clock } from "lucide-react";
import { useState } from "react";
import type { Attempt } from "../../api/types";
import { Badge, StatusBadge } from "../../components/Badge";
import { LogViewer } from "../../components/LogViewer";
import { RelativeTime } from "../../components/RelativeTime";
import { useNow } from "../../components/useNow";
import { elapsed, formatDuration } from "../../lib/time";

const openByDefault = new Set(["assigned", "running", "failed", "uncertain"]);

function AttemptItem({ attempt, latest }: { attempt: Attempt; latest: boolean }) {
  const [open, setOpen] = useState(latest && openByDefault.has(attempt.status));
  const now = useNow();
  const duration = elapsed(attempt.startedAt ?? attempt.assignedAt, attempt.finishedAt, now);
  return (
    <li className="attempt">
      <div className="attempt__header">
        <span className="attempt__title">Attempt {attempt.number}</span>
        <StatusBadge status={attempt.status} />
        {attempt.lateResult ? (
          <Badge tone="info" title="The result arrived after the attempt was marked uncertain; it is recorded.">
            Late result
          </Badge>
        ) : null}
        <span className="muted small">
          {attempt.agentName ? (
            <>
              on <strong className="mono">{attempt.agentName}</strong>
            </>
          ) : attempt.agentId ? (
            <>on a removed agent</>
          ) : (
            <>no agent yet</>
          )}
        </span>
        <code className="mono muted small">
          {attempt.executor} · {attempt.action}
        </code>
        <span className="attempt__times muted small">
          created <RelativeTime value={attempt.createdAt} />
          {duration !== null ? <span className="num"> · {formatDuration(duration)}</span> : null}
        </span>
      </div>
      {attempt.waitingReason ? (
        <p className="attempt__waiting">
          <Clock size={14} aria-hidden="true" /> {attempt.waitingReason}
        </p>
      ) : null}
      {attempt.error ? <pre className="attempt__error">{attempt.error}</pre> : null}
      <details className="attempt__log" open={open} onToggle={(e) => setOpen(e.currentTarget.open)}>
        <summary>
          Log{attempt.logLines > 0 ? <span className="muted num"> · {attempt.logLines} lines</span> : null}
        </summary>
        {open ? <LogViewer attemptId={attempt.id} status={attempt.status} /> : null}
      </details>
    </li>
  );
}

export function AttemptList({ attempts }: { attempts: Attempt[] }) {
  if (attempts.length === 0) return null;
  const sorted = [...attempts].sort((a, b) => a.number - b.number);
  const latest = sorted[sorted.length - 1]?.id;
  return (
    <ol className="attempts" aria-label="Attempts">
      {sorted.map((attempt) => (
        <AttemptItem key={attempt.id} attempt={attempt} latest={attempt.id === latest} />
      ))}
    </ol>
  );
}
