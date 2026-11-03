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
  /** The run configures a guest, so this names a preview rather than a plan. */
  configure?: boolean;
  /** The run is a service's pipeline, so this names one document covering every stage. */
  pipeline?: boolean;
  /** The run rotates a guest's key: approving does the stage described, and nothing else. */
  rotation?: boolean;
}

function fingerprintLabel(setupOnly: boolean, configure: boolean, pipeline: boolean) {
  return pipeline ? "Pipeline document SHA-256" : configure ? "Preview SHA-256" : setupOnly ? "Setup approval fingerprint" : "Plan SHA-256";
}

function Decided({ step, setupOnly, configure, pipeline }: { step: Step; setupOnly: boolean; configure: boolean; pipeline: boolean }) {
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
          {fingerprintLabel(setupOnly, configure, pipeline)} <Hash value={approval.planSha256} label="Copy the approval fingerprint" />
        </div>
      ) : null}
    </div>
  );
}

export function ApprovalPanel({ runId, step, plan, configure = false, pipeline = false, rotation = false }: Props) {
  const approve = useApproveStep();
  const reject = useRejectStep();
  const [note, setNote] = useState("");
  const noteId = useId();
  const approval = step.approval;
  const setupOnly = plan?.hasChanges === false;

  if (approval?.decision) return <Decided step={step} setupOnly={setupOnly} configure={configure} pipeline={pipeline} />;
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
        {step.detail ?? "This guest's policy asks for approval before applying."}{" "}
        {pipeline
          ? "Approving covers every stage above, as planned and previewed, and applies them in order. Nothing has changed yet. If a stage fails, the ones before it stay applied."
          : rotation
          ? "Approving does this stage of the rotation and nothing else. It only ever chooses Octopushy's own keys on the guest."
          : configure
          ? "Approving runs this playbook as root on the guest, with the secrets and prepared values listed in the preview. Octopushy cannot show what it will change."
          : setupOnly
          ? "Infrastructure is unchanged. Approving authorizes the pending guest setup with the configuration recorded in this run."
          : "Approving applies exactly this saved plan and any guest setup recorded in this run; if anything changes, a new plan needs a new approval."}
      </Callout>
      {plan ? <PlanCountsLine counts={plan.counts} /> : null}
      <div className="inline-group">
        <span className="muted">{fingerprintLabel(setupOnly, configure, pipeline)}</span>
        {planSha256 ? <Hash value={planSha256} label="Copy the approval fingerprint" /> : <span className="muted">not recorded</span>}
      </div>
      <div className="field">
        <label htmlFor={noteId}>
          Note <span className="muted">(optional)</span>
        </label>
        <textarea id={noteId} rows={2} value={note} onChange={(e) => setNote(e.target.value)} maxLength={1000} />
      </div>
      {!planSha256 ? (
        <Callout kind="warning">The approval fingerprint is missing, so this run cannot be approved. Plan again.</Callout>
      ) : null}
      <ErrorMessage error={approve.error ?? reject.error} />
      <div className="button-row">
        <button type="button" className="button button--primary" onClick={() => decision("approve")}
          disabled={pending || !planSha256}>
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
