import { Hand, TriangleAlert } from "lucide-react";
import { Link } from "react-router";
import { useRun } from "../../api/queries";
import type { RunSummary } from "../../api/types";
import { Skeleton } from "../../components/States";
import { operationLabel } from "../../lib/format";
import { ApprovalPanel } from "./ApprovalPanel";
import { approvalProps } from "./approval";

/**
 * A run of this resource that waits for approval, on the resource's own page: the same tiles, note, fingerprint,
 * Approve and Reject as on the run's page. Red when what it changes deletes or replaces something.
 */
export function WaitingRun({ run }: { run: RunSummary }) {
  const { data, isPending } = useRun(run.id);
  const approvals = data?.steps.filter((s) => s.kind === "approval") ?? [];
  // The step that waits; once decided, until the page refreshes, the one that was.
  const step = approvals.find((s) => s.status === "awaiting-approval") ?? approvals.findLast((s) => s.approval?.decision);
  const props = data && step ? approvalProps(data, step) : null;
  const danger = props?.overview?.some((tile) => tile.tone === "danger") ?? false;
  const Icon = danger ? TriangleAlert : Hand;
  const title = `${operationLabel(run.operation)} #${run.number} waits for your approval`;
  return (
    <section className={danger ? "wait wait--danger" : "wait"} aria-label={title}>
      <header className="wait__head">
        <h2 className="wait__title">
          <Icon size={18} aria-hidden="true" />
          {title}
        </h2>
        <Link to={`/runs/${run.id}`} className="small">
          Open run #{run.number}
        </Link>
      </header>
      {isPending ? (
        <div className="wait__body stack stack--small" role="status" aria-label="Loading the run">
          <Skeleton height={64} />
          <Skeleton height={14} width="60%" />
        </div>
      ) : props ? (
        <div className="wait__body">
          <ApprovalPanel {...props} />
        </div>
      ) : null}
    </section>
  );
}
