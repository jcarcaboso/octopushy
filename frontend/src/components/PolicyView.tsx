import type { Policy } from "../api/types";
import { Badge } from "./Badge";
import { KeyValue } from "./KeyValue";

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
