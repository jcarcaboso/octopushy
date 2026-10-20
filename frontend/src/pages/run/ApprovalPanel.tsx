import { Check, X } from "lucide-react";
import { useId, useState } from "react";
import { useApproveStep, useRejectStep } from "../../api/queries";
import type { Step } from "../../api/types";
import { Callout } from "../../components/Callout";
import { Hash } from "../../components/Mono";
import { RelativeTime } from "../../components/RelativeTime";
import { ErrorMessage } from "../../components/States";
import type { PlanSummary } from "../../lib/plan";
import { PlanCountsLine } from "./PlanChanges";

interface Props {
  runId: string;
  step: Step;
  plan: PlanSummary | null;
}

function Decided({ step }: { step: Step }) {
  const approval = step.approval!;
  const byPolicy = approval.decidedBy === "policy";
  const rejected = approval.decision === "rejected";
  return (
    <div className="decision">
      <div>
        {rejected ? (
          <strong>Rejected by {approval.decidedBy}</strong>
        ) : byPolicy ? (
          <strong>Approved by policy</strong>
        ) : (
          <strong>Approved by {approval.decidedBy}</strong>
        )}{" "}
        {approval.decidedAt ? <RelativeTime value={approval.decidedAt} /> : null}
      </div>
      {approval.note ? <p className="decision__note">{approval.note}</p> : null}
      {approval.planSha256 ? (
        <div className="small muted inline-group">
          Plan SHA-256 <Hash value={approval.planSha256} label="Copy the plan's SHA-256" />
        </div>
      ) : null}
    </div>
  );
}

export function ApprovalPanel({ runId, step, plan }: Props) {
  const approve = useApproveStep();
  const reject = useRejectStep();
  const [note, setNote] = useState("");
  const noteId = useId();
  const approval = step.approval;

  if (approval?.decision) return <Decided step={step} />;
  if (step.status !== "awaiting-approval") return null;

  const planSha256 = approval?.planSha256 ?? null;
  const pending = approve.isPending || reject.isPending;
  const decision = (kind: "approve" | "reject") => {
    const input = { runId, stepId: step.id, planSha256, note: note.trim() || null };
    if (kind === "approve") {
      reject.reset();
      approve.mutate(input);
    } else {
      approve.reset();
      reject.mutate(input);
    }
  };

  return (
    <div className="approval">
      <Callout kind="info" title="Your approval is needed">
        {step.detail ?? "This guest's policy asks for approval before applying."} Approving applies exactly this
        saved plan; if anything changes, a new plan needs a new approval.
      </Callout>
      {plan ? <PlanCountsLine counts={plan.counts} /> : null}
      <div className="inline-group">
        <span className="muted">Plan SHA-256</span>
        {planSha256 ? <Hash value={planSha256} label="Copy the plan's SHA-256" /> : <span className="muted">not recorded</span>}
      </div>
      <div className="field">
        <label htmlFor={noteId}>
          Note <span className="muted">(optional)</span>
        </label>
        <textarea id={noteId} rows={2} value={note} onChange={(e) => setNote(e.target.value)} maxLength={1000} />
      </div>
      {!planSha256 ? (
        <Callout kind="warning">The plan's hash is missing, so it cannot be approved. Plan again.</Callout>
      ) : null}
      <ErrorMessage error={approve.error ?? reject.error} />
      <div className="button-row">
        <button type="button" className="button button--primary" onClick={() => decision("approve")} disabled={pending || !planSha256}>
          <Check size={15} aria-hidden="true" />
          {approve.isPending ? "Approving…" : "Approve"}
        </button>
        <button type="button" className="button button--danger-outline" onClick={() => decision("reject")} disabled={pending}>
          <X size={15} aria-hidden="true" />
          {reject.isPending ? "Rejecting…" : "Reject"}
        </button>
      </div>
    </div>
  );
}
