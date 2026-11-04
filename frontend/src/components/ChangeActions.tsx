import { ArrowUpCircle, Pencil, Trash2 } from "lucide-react";
import { Link } from "react-router";
import { useChangeForm, useChanges } from "../api/queries";
import { StatusBadge } from "./Badge";
import { Card } from "./Card";
import { RelativeTime } from "./RelativeTime";

interface Props {
  resourceId: string;
  kind: "guest" | "service";
}

function base({ resourceId, kind }: Props) {
  return `/${kind === "service" ? "services" : "guests"}/${resourceId}/change`;
}

/**
 * Edit, upgrade to a newer major, and for a guest remove: each a change, committed to the lab repository from a form.
 * The form says when it is read-only, and why.
 */
export function ChangeActions(props: Props) {
  const { data: form } = useChangeForm(props.resourceId);
  const upgrade = form?.upgrade ? `${form.upgrade.id}@${form.upgrade.version.split(".")[0]}` : null;
  return (
    <>
      <Link to={base(props)} className="button" title={form?.blockedReason ?? undefined}>
        <Pencil size={15} aria-hidden="true" />
        Edit
      </Link>
      {upgrade ? (
        <Link to={`${base(props)}?mode=upgrade`} className="button" title={`${form!.upgrade!.id} ${form!.upgrade!.version} is a new major: it may break.`}>
          <ArrowUpCircle size={15} aria-hidden="true" />
          Upgrade to {upgrade}
        </Link>
      ) : null}
      {props.kind === "guest" && form?.canRemove ? (
        <Link to={`${base(props)}?mode=remove`} className="button button--danger-outline">
          <Trash2 size={15} aria-hidden="true" />
          Remove
        </Link>
      ) : null}
    </>
  );
}

/** The newest changes made to a resource from its forms. */
export function RecentChanges({ resourceId }: { resourceId: string }) {
  const { data } = useChanges(resourceId);
  if (!data || data.length === 0) return null;
  return (
    <Card title="Changes" id="changes">
      <ul className="change-list">
        {data.map((change) => (
          <li key={change.id} className="change-list__item">
            <StatusBadge status={change.status} />
            <span className="stack stack--tiny">
              <Link to={`/changes/${change.id}`}>{change.message}</Link>
              <span className="muted small">
                {change.author} · <RelativeTime value={change.createdAt} />
              </span>
            </span>
          </li>
        ))}
      </ul>
    </Card>
  );
}
