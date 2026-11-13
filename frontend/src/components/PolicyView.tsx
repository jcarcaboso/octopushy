import type { Policy } from "../api/types";
import { Badge } from "./Badge";
import { Card } from "./Card";
import { KeyValue } from "./KeyValue";
import { LabelList } from "./LabelEditor";

function SetBy({ value }: { value: string }) {
  return <span className="muted small">set by {value}</span>;
}

function Mode({ value }: { value: string }) {
  return <Badge tone={value === "manual" ? "attention" : "neutral"}>{value === "manual" ? "Manual" : value === "automatic" ? "Automatic" : value}</Badge>;
}

export function PolicyView({ policy }: { policy: Policy | null }) {
  if (!policy) return <p className="muted">Not known: the repository has errors or the guest is not in the current revision.</p>;
  return (
    <KeyValue
      items={[
        {
          term: "Approval",
          value: (
            <span className="inline-group">
              <Mode value={policy.approval} /> <SetBy value={policy.approvalSetBy} />
            </span>
          ),
        },
        ...("actions" in policy && policy.actions ? policy.actions : []).map((action) => ({
          term: `Approval of ${action.action}`,
          value: (
            <span className="inline-group">
              <Mode value={action.approval} /> <SetBy value={action.setBy} />
            </span>
          ),
        })),
        {
          term: "Destructive",
          value: (
            <span className="inline-group">
              <Mode value={policy.destructive} /> <SetBy value={policy.destructiveSetBy} />
            </span>
          ),
        },
      ]}
    />
  );
}

/** The policy card of a guest or a service, with the agent labels its runs need. */
export function PolicyCard({ policy, agentLabels, agentLabelsSetBy }: { policy: Policy | null; agentLabels: string[]; agentLabelsSetBy: string | null }) {
  return (
    <Card title="Policy">
      <div className="stack">
        <PolicyView policy={policy} />
        <KeyValue
          items={[
            {
              term: "Agent labels",
              value: (
                <span className="inline-group">
                  <LabelList labels={agentLabels} empty="Any agent" />
                  {agentLabelsSetBy ? <SetBy value={agentLabelsSetBy} /> : null}
                </span>
              ),
            },
          ]}
        />
      </div>
    </Card>
  );
}
