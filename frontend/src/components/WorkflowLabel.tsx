import type { Workflow } from "../api/types";

/**
 * The workflow a guest or service names, at the version it resolved to: `fedora-vm@2.3`, with what was written when that
 * differs (`@2` takes the newest 2.x). A reference that resolved to nothing shows as written, and says so.
 */
export function WorkflowLabel({ workflow }: { workflow: Workflow | null | undefined }) {
  if (!workflow) return <span className="muted">—</span>;
  if (!workflow.resolved)
    return (
      <span>
        <span className="mono">{workflow.reference}</span> <span className="muted small">not resolved</span>
      </span>
    );
  const written = workflow.reference.slice(workflow.reference.indexOf("@"));
  return (
    <span title={`Named as ${workflow.reference} in the definition`}>
      <span className="mono">{workflow.resolved}</span>
      {workflow.resolved !== workflow.reference ? <span className="muted small"> from {written}</span> : null}
    </span>
  );
}
