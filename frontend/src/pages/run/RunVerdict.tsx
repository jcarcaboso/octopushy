import type { ReactNode } from "react";
import type { RunDetail, Step } from "../../api/types";
import { StatusMark } from "../../components/Badge";
import { RelativeTime } from "../../components/RelativeTime";
import { useNow } from "../../components/useNow";
import { WaitingNote } from "../../components/WaitingNote";
import { statusStyle } from "../../components/status";
import type { Tile } from "../../lib/changes";
import { humanize } from "../../lib/format";
import { headlineImage, imageChanges } from "../../lib/images";
import { elapsed, formatDuration } from "../../lib/time";
import { ApprovalPanel } from "./ApprovalPanel";
import { approvalProps } from "./approval";
import { Emphasized, LevelChip } from "./RunChanges";
import { decisionNote, lastSegment, stepTitle } from "./steps";

const doneTitles: Record<string, string> = {
  pipeline: "Deployed",
  provision: "Applied",
  configure: "Configured",
  retire: "Retired",
  "rotate-access": "Key rotated",
};

function verdictTitle(status: string, operation: string): string {
  if (status === "succeeded") return doneTitles[operation] ?? statusStyle(status).label;
  if (status === "awaiting-approval") return "Waiting for your approval";
  return statusStyle(status).label;
}

/** The most telling before → after of the plans: a destructive one first, else the first. */
function tileHeadline(tiles: Tile[]): Tile | null {
  const transitions = tiles.filter((tile) => tile.value.includes(" → "));
  return transitions.find((tile) => tile.tone === "danger") ?? transitions[0] ?? null;
}

function Headline({ detail, tiles }: { detail: RunDetail; tiles: Tile[] }) {
  const image = detail.images && detail.images.length > 0
    ? headlineImage(imageChanges(detail.images), lastSegment(detail.run.address))
    : null;
  if (image) {
    const { change, others } = image;
    return (
      <div className="verdict__headline">
        <span className="verdict__bump mono">
          {change.before !== null ? (
            <>
              <span className={change.after === null ? "verdict__old verdict__old--gone" : "verdict__old"}>{change.before}</span>
              {change.after !== null ? <span className="verdict__arrow" aria-label="to">→</span> : null}
            </>
          ) : null}
          {change.after !== null ? (
            <span className="verdict__new">
              <Emphasized before={change.before} after={change.after} />
            </span>
          ) : null}
        </span>
        <LevelChip level={change.level} />
        <span className="verdict__what">
          <span className="mono">{lastSegment(change.repository)}</span>
          {others > 0 ? ` and ${others} other ${others === 1 ? "image" : "images"}` : null}
        </span>
      </div>
    );
  }
  const tile = tileHeadline(tiles);
  if (!tile) return null;
  const [before, rest] = tile.value.split(" → ");
  const unit = /^(\S+) ([A-Za-z]+)$/.exec(rest);
  const after = unit ? unit[1] : rest;
  return (
    <div className="verdict__headline">
      <span className="verdict__bump mono">
        <span className="verdict__old">{before}</span>
        <span className="verdict__arrow" aria-label="to">→</span>
        <span className="verdict__new">
          <Emphasized before={before} after={after} />
        </span>
        {unit ? <span className="verdict__unit">{unit[2]}</span> : null}
      </span>
      <span className="verdict__what">
        {tile.label}
        {tile.sub ? ` · ${tile.sub}` : ""}
      </span>
    </div>
  );
}

function Meta({ detail, waitingStep }: { detail: RunDetail; waitingStep: Step | undefined }) {
  const now = useNow();
  const run = detail.run;
  const decided = detail.steps.filter((s) => s.kind === "approval" && s.approval?.decision).at(-1);
  const decision = decided ? decisionNote(decided) : null;
  const duration = elapsed(run.startedAt, run.finishedAt, now);
  let when: ReactNode;
  if (run.finishedAt) {
    when = (
      <>
        finished <RelativeTime value={run.finishedAt} />
        {duration !== null ? <span className="num"> · took {formatDuration(duration)}</span> : null}
      </>
    );
  } else if (waitingStep?.startedAt) {
    when = (
      <>
        asked <RelativeTime value={waitingStep.startedAt} />
      </>
    );
  } else if (run.startedAt) {
    when = (
      <>
        started <RelativeTime value={run.startedAt} />
      </>
    );
  } else {
    when = (
      <>
        created <RelativeTime value={run.createdAt} />
      </>
    );
  }
  return (
    <p className="verdict__meta">
      {when}
      {decision ? <span> · {decision}</span> : null}
    </p>
  );
}

/** The failed step, its detail and the error of its last attempt. */
function Failure({ step }: { step: Step }) {
  const attempt = [...step.attempts].sort((a, b) => a.number - b.number).at(-1);
  const error = attempt?.error ?? null;
  return (
    <div className="verdict__failure">
      <strong>{stepTitle(step)}</strong>
      {step.detail && step.detail !== error ? <p>{step.detail}</p> : null}
      {error ? <pre className="verdict__error">{error}</pre> : null}
    </div>
  );
}

/** The run names its current step by name only; a stage tells apart steps of the same name. */
function currentStepTitle(steps: Step[], name: string): string {
  const named = steps.filter((s) => s.name === name);
  const step = named.find((s) => s.status === "running" || s.status === "assigned" || s.status === "blocked") ?? named[0];
  return step ? stepTitle(step) : humanize(name);
}

interface Props {
  detail: RunDetail;
  tiles: Tile[];
  live: boolean;
  /** The uncertain notice, when there is one. */
  notice?: ReactNode;
  actions: ReactNode;
}

/** The top of a run's page: how it ended or where it stands, the change it makes, and what can be done. */
export function RunVerdict({ detail, tiles, live, notice, actions }: Props) {
  const run = detail.run;
  const tone = statusStyle(run.status).tone;
  const steps = [...detail.steps].sort((a, b) => Number(a.ordinal) - Number(b.ordinal));
  const waitingStep = steps.find((s) => s.kind === "approval" && s.status === "awaiting-approval");
  const failedStep = run.status === "failed" ? steps.find((s) => s.status === "failed") : undefined;
  return (
    <section className={`verdict verdict--${tone}`} aria-label="Summary">
      <div className="verdict__state">
        <StatusMark status={run.status} size={18} decorative />
        <span>{verdictTitle(run.status, run.operation)}</span>
        {run.currentStep && live && !waitingStep ? <span className="verdict__step">at {currentStepTitle(steps, run.currentStep)}</span> : null}
        {live ? (
          <span className="verdict__live muted small">
            <span className="live-dot" aria-hidden="true" /> Updating
          </span>
        ) : null}
      </div>
      <Headline detail={detail} tiles={tiles} />
      {/* The approval below prints the waiting step's detail, which is usually the run's too. */}
      {run.detail && run.detail !== waitingStep?.detail ? <p className="verdict__text">{run.detail}</p> : null}
      {run.waiting && run.waiting !== run.detail ? (
        <p className="verdict__text">
          <WaitingNote text={run.waiting} />
        </p>
      ) : null}
      <Meta detail={detail} waitingStep={waitingStep} />
      {failedStep ? <Failure step={failedStep} /> : null}
      {notice}
      {waitingStep ? (
        <div className="verdict__approval">
          <ApprovalPanel {...approvalProps(detail, waitingStep)} overview={[]} />
        </div>
      ) : null}
      {actions ? <div className="verdict__actions">{actions}</div> : null}
    </section>
  );
}
