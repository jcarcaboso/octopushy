import { Badge } from "../../components/Badge";
import type { Tone } from "../../components/status";
import { EmptyRow, Table } from "../../components/Table";
import type { ChangeKind, PlanCounts, PlanSummary } from "../../lib/plan";

const kinds: Record<ChangeKind, { label: string; tone: Tone; sign: string }> = {
  create: { label: "Create", tone: "success", sign: "+" },
  update: { label: "Update", tone: "info", sign: "~" },
  delete: { label: "Delete", tone: "danger", sign: "−" },
  replace: { label: "Replace", tone: "uncertain", sign: "±" },
  read: { label: "Read", tone: "neutral", sign: "" },
  "no-op": { label: "No change", tone: "muted", sign: "" },
  other: { label: "Other", tone: "neutral", sign: "" },
};

export function ChangeBadge({ kind, actions }: { kind: ChangeKind; actions?: string[] }) {
  const style = kinds[kind];
  return (
    <Badge tone={style.tone} title={actions?.length ? `OpenTofu actions: ${actions.join(", ")}` : undefined}>
      {style.sign ? (
        <span className="change-sign" aria-hidden="true">
          {style.sign}
        </span>
      ) : null}
      {kind === "other" && actions?.length ? actions.join(", ") : style.label}
    </Badge>
  );
}

export function PlanCountsLine({ counts }: { counts: PlanCounts }) {
  const parts: [keyof PlanCounts, string][] = [
    ["create", "to create"],
    ["update", "to update"],
    ["replace", "to replace"],
    ["delete", "to delete"],
  ];
  return (
    <ul className="plan-counts" aria-label="Planned changes">
      {parts.map(([kind, text]) => (
        <li key={kind} className={counts[kind] > 0 ? `plan-counts__item plan-counts__item--${kind}` : "plan-counts__item plan-counts__item--zero"}>
          <span className="num strong">{counts[kind]}</span> {text}
        </li>
      ))}
    </ul>
  );
}

function ToolVersion({ version }: { version: string | null }) {
  return version ? <span className="muted small">OpenTofu {version}</span> : null;
}

export function PlanChanges({ plan }: { plan: PlanSummary }) {
  if (!plan.hasChanges) {
    return (
      <p className="muted">
        No changes: the infrastructure already matches the definition. <ToolVersion version={plan.toolVersion} />
      </p>
    );
  }
  return (
    <div className="stack stack--small">
      <div className="plan-summary">
        <PlanCountsLine counts={plan.counts} />
        <ToolVersion version={plan.toolVersion} />
      </div>
      <Table label="Planned resource changes" className="table--changes">
        <thead>
          <tr>
            <th scope="col">Resource</th>
            <th scope="col">Type</th>
            <th scope="col">Action</th>
          </tr>
        </thead>
        <tbody>
          {plan.changes.length === 0 ? <EmptyRow colSpan={3}>The plan lists no resource changes.</EmptyRow> : null}
          {plan.changes.map((change) => (
            <tr key={change.address} className={`change change--${change.kind}`}>
              <td className="mono change__address">{change.address}</td>
              <td className="mono muted change__type">{change.type ?? ""}</td>
              <td>
                <ChangeBadge kind={change.kind} actions={change.actions} />
              </td>
            </tr>
          ))}
        </tbody>
      </Table>
    </div>
  );
}
