import { ChevronRight, HardDrive, Layers, Server, type LucideIcon } from "lucide-react";
import { useState, type MouseEvent } from "react";
import { Link, useNavigate, useSearchParams } from "react-router";
import { useApproveStep, useCancelRun, useRejectStep, useRuns } from "../api/queries";
import type { RunSummary } from "../api/types";
import { ConfirmDialog } from "../components/ConfirmDialog";
import { PageHeader } from "../components/PageHeader";
import { RelativeTime } from "../components/RelativeTime";
import { EmptyState, ErrorMessage, PageSkeleton, Spinner } from "../components/States";
import { runStatuses, statusStyle } from "../components/status";
import { useNow } from "../components/useNow";
import { operationLabel } from "../lib/format";
import { reasonShort, runProgress, runSection, runTarget, type RunProgress, type RunSection, type RunTarget } from "../lib/runs";

const sections: { id: RunSection; title: string }[] = [
  { id: "waiting", title: "Waiting for you" },
  { id: "progress", title: "In progress" },
  { id: "earlier", title: "Earlier" },
];

const types: { value: string | null; label: string; type?: RunTarget["type"] }[] = [
  { value: null, label: "All" },
  { value: "machine", label: "Machines", type: "Machine" },
  { value: "service", label: "Services", type: "Service" },
];

const typeIcons: Record<RunTarget["type"], LucideIcon | null> = { Machine: Server, Service: Layers, Node: HardDrive, Other: null };

type Decision = { kind: "approve" | "reject" | "cancel"; run: RunSummary; progress: RunProgress };

function StageTrack({ progress }: { progress: RunProgress }) {
  return (
    <div className="run-progress">
      {progress.segments.length > 0 ? (
        <div className="track" role="img" aria-label={progress.description}>
          {progress.segments.map((tone, i) => (
            <span key={i} className={`track__segment track__segment--${tone}`} />
          ))}
        </div>
      ) : null}
      <div className="run-progress__text">
        <span className={`run-progress__label run-progress__label--${progress.tone}`}>{progress.label}</span>
        {progress.note ? (
          <span className="run-progress__note" title={progress.note}>
            {progress.note}
          </span>
        ) : null}
      </div>
    </div>
  );
}

function RunRow({ run, now, onDecide }: { run: RunSummary; now: number; onDecide: (decision: Decision) => void }) {
  const target = runTarget(run);
  const progress = runProgress(run, now);
  const Icon = typeIcons[target.type];
  const decide = (kind: Decision["kind"]) => onDecide({ kind, run, progress });
  const navigate = useNavigate();
  // The whole row opens the run, unless the click was on a control or ended a text selection.
  const open = (event: MouseEvent<HTMLLIElement>) => {
    if ((event.target as Element).closest("a, button") || window.getSelection()?.toString()) return;
    navigate(`/runs/${run.id}`);
  };
  return (
    <li className="runs-row" onClick={open}>
      <Link to={`/runs/${run.id}`} className="runs-row__link">
        <span className="runs-row__number">#{run.number}</span> {operationLabel(run.operation)}
      </Link>
      <span>
        <span className={`run-chip run-chip--${target.type.toLowerCase()}`}>
          {Icon ? <Icon size={12} aria-hidden="true" /> : null}
          {target.type}
        </span>
      </span>
      <span className="runs-row__target">
        <span className="runs-row__name">{target.name}</span>
        {target.host ? <span className="runs-row__host">on {target.host}</span> : null}
      </span>
      <span>{target.node ? <code className="run-chip run-chip--mono">{target.node}</code> : null}</span>
      <StageTrack progress={progress} />
      <span className="muted small">{reasonShort(run.reason)}</span>
      <span className="small">
        <RelativeTime value={run.createdAt} />
      </span>
      <span className="runs-row__actions">
        {progress.approval ? (
          <>
            <button type="button" className="button button--small" onClick={() => decide("reject")}>
              Reject
            </button>
            <button
              type="button"
              className="button button--small button--primary"
              onClick={() => decide("approve")}
              disabled={!progress.approval.planSha256}
              title={progress.approval.planSha256 ? undefined : "The approval fingerprint is missing. Plan again."}
            >
              Approve
            </button>
          </>
        ) : run.canCancel ? (
          <button type="button" className="button button--small button--danger-outline" onClick={() => decide("cancel")}>
            Cancel
          </button>
        ) : null}
      </span>
    </li>
  );
}

const decisionCopy = {
  approve: { verb: "Approve", confirm: "Approve", body: "Approving applies what this run planned or previewed, exactly as recorded." },
  reject: { verb: "Reject", confirm: "Reject the run", body: "The run is cancelled and nothing is applied." },
  cancel: {
    verb: "Cancel",
    confirm: "Cancel the run",
    body: "Steps that have not started are skipped. A change that is being applied is never interrupted.",
  },
} as const;

export function RunsPage() {
  const [params, setParams] = useSearchParams();
  const status = params.get("status");
  const type = params.get("type");
  const node = params.get("node");
  const search = params.get("q") ?? "";
  const { data, error, isPending, isFetching, isPlaceholderData } = useRuns(status);
  const now = useNow();
  const [closed, setClosed] = useState<Partial<Record<RunSection, boolean>>>({});
  const [decision, setDecision] = useState<Decision | null>(null);
  const approve = useApproveStep();
  const reject = useRejectStep();
  const cancel = useCancelRun();
  const mutation = decision ? { approve, reject, cancel }[decision.kind] : null;

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
  const withType = (value: string | null) => {
    const next = new URLSearchParams(params);
    if (value) next.set("type", value);
    else next.delete("type");
    return `?${next}`;
  };

  const runs = data ?? [];
  const nodes = [...new Set(runs.map((r) => runTarget(r).node).filter((n): n is string => n !== null))].sort();
  const wanted = types.find((t) => t.value === type)?.type;
  const text = search.trim().toLowerCase();
  const shown = runs.filter((run) => {
    const target = runTarget(run);
    return (!wanted || target.type === wanted) && (!node || target.node === node) && (!text || run.address.toLowerCase().includes(text));
  });
  const filtered = Boolean(status || wanted || node || text);

  const close = () => {
    setDecision(null);
    mutation?.reset();
  };
  const confirm = () => {
    if (!decision) return;
    const { kind, run, progress } = decision;
    const done = { onSuccess: () => setDecision(null) };
    if (kind === "cancel") cancel.mutate(run.id, done);
    else {
      const step = progress.approval!;
      (kind === "approve" ? approve : reject).mutate({ runId: run.id, stepId: step.id, planSha256: step.planSha256, note: null }, done);
    }
  };

  return (
    <>
      <PageHeader
        title="Runs"
        actions={
          <div className="runs-filters">
            <nav className="segmented" aria-label="Filter runs by type">
              {types.map((t) => (
                <Link key={t.label} to={withType(t.value)} replace className="segmented__item" aria-current={type === t.value ? "page" : undefined}>
                  {t.label}
                </Link>
              ))}
            </nav>
            <label className="runs-filter">
              <span>Node</span>
              <select value={node ?? ""} onChange={(e) => set("node", e.target.value || null)}>
                <option value="">All</option>
                {node && !nodes.includes(node) ? <option value={node}>{node}</option> : null}
                {nodes.map((n) => (
                  <option key={n} value={n}>
                    {n}
                  </option>
                ))}
              </select>
            </label>
            <label className="runs-filter">
              <span>Status</span>
              <select value={status ?? ""} onChange={(e) => set("status", e.target.value || null)}>
                <option value="">Any</option>
                {runStatuses.map((s) => (
                  <option key={s} value={s}>
                    {statusStyle(s).label}
                  </option>
                ))}
              </select>
            </label>
            <input
              type="search"
              className="runs-search"
              aria-label="Filter by machine or service"
              placeholder="Machine or service"
              value={search}
              onChange={(e) => set("q", e.target.value || null)}
            />
            {isFetching && isPlaceholderData ? <Spinner label="Loading runs" /> : null}
          </div>
        }
      />
      {isPending ? (
        <PageSkeleton rows={1} />
      ) : !data ? (
        <ErrorMessage error={error} title="The runs could not be loaded" />
      ) : shown.length === 0 ? (
        <EmptyState
          title={filtered ? "No runs match these filters" : "No runs yet"}
          actions={
            filtered ? (
              <Link to="/runs" className="button button--small">
                Clear the filters
              </Link>
            ) : undefined
          }
        >
          {filtered ? null : "Runs start when a revision changes a guest, or by hand with Plan now."}
        </EmptyState>
      ) : (
        <div className="runs">
          <div className="runs__inner">
            <div className="runs-head" aria-hidden="true">
              <span>Run</span>
              <span>Type</span>
              <span>Target</span>
              <span>Node</span>
              <span>Progress</span>
              <span>Reason</span>
              <span>Created</span>
            </div>
            {sections.map((section) => {
              const rows = shown.filter((run) => runSection(run.status) === section.id);
              if (rows.length === 0) return null;
              return (
                <details
                  key={section.id}
                  className="runs-section"
                  open={!closed[section.id]}
                  onToggle={(e) => {
                    const open = e.currentTarget.open;
                    setClosed((current) => ({ ...current, [section.id]: !open }));
                  }}
                >
                  <summary className="runs-section__summary">
                    <ChevronRight className="runs-section__chevron" size={14} aria-hidden="true" />
                    {section.title}
                    <span className={section.id === "waiting" ? "runs-section__count runs-section__count--waiting" : "runs-section__count"}>
                      {rows.length}
                    </span>
                  </summary>
                  <ul className="runs-list" aria-label={section.title}>
                    {rows.map((run) => (
                      <RunRow key={run.id} run={run} now={now} onDecide={setDecision} />
                    ))}
                  </ul>
                </details>
              );
            })}
          </div>
        </div>
      )}
      {data && error ? <ErrorMessage error={error} title="Showing the last data; refreshing failed" /> : null}
      {decision ? (
        <ConfirmDialog
          open
          title={`${decisionCopy[decision.kind].verb} run #${decision.run.number}?`}
          confirmLabel={decisionCopy[decision.kind].confirm}
          danger={decision.kind !== "approve"}
          pending={mutation?.isPending}
          error={mutation?.error}
          onClose={close}
          onConfirm={confirm}
        >
          <p>
            <strong>
              {operationLabel(decision.run.operation)} {runTarget(decision.run).name}
            </strong>
            {decision.kind !== "cancel" && decision.progress.note ? <>: {decision.progress.note}</> : null}
          </p>
          <p>{decisionCopy[decision.kind].body}</p>
          {decision.kind === "approve" ? (
            <p>
              <Link to={`/runs/${decision.run.id}`}>Open the run</Link> to read it in full first.
            </p>
          ) : null}
        </ConfirmDialog>
      ) : null}
    </>
  );
}
