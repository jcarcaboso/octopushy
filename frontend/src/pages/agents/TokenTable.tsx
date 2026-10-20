import { useState } from "react";
import { useDeleteToken, useTokens } from "../../api/queries";
import type { Agent, EnrollmentToken } from "../../api/types";
import { Badge } from "../../components/Badge";
import { LabelList } from "../../components/LabelEditor";
import { RelativeTime } from "../../components/RelativeTime";
import { useNow } from "../../components/useNow";
import { ErrorMessage, Skeleton } from "../../components/States";
import { EmptyRow, Table } from "../../components/Table";
import { toDate } from "../../lib/time";

function TokenState({ token, agents, now }: { token: EnrollmentToken; agents: Agent[]; now: number }) {
  if (token.usedAt) {
    const agent = agents.find((a) => a.id === token.agentId);
    return (
      <span>
        <Badge tone="success">Used</Badge>{" "}
        <span className="small muted">
          {agent ? <>by {agent.name} </> : null}
          <RelativeTime value={token.usedAt} />
        </span>
      </span>
    );
  }
  if (toDate(token.expiresAt).getTime() <= now) return <Badge tone="muted">Expired</Badge>;
  return <Badge tone="info">Pending</Badge>;
}

export function TokenTable({ agents }: { agents: Agent[] }) {
  const { data, error, isPending } = useTokens();
  const revoke = useDeleteToken();
  const [revoking, setRevoking] = useState<string | null>(null);
  const now = useNow();

  if (isPending) return <Skeleton height={80} />;
  if (!data) return <ErrorMessage error={error} title="The tokens could not be loaded" />;

  return (
    <>
      <ErrorMessage error={revoke.error} />
      <Table label="Enrollment tokens">
        <thead>
          <tr>
            <th scope="col">Name</th>
            <th scope="col">Labels</th>
            <th scope="col">State</th>
            <th scope="col">Created</th>
            <th scope="col">Expires</th>
            <th scope="col">
              <span className="visually-hidden">Actions</span>
            </th>
          </tr>
        </thead>
        <tbody>
          {data.length === 0 ? <EmptyRow colSpan={6}>No enrollment tokens.</EmptyRow> : null}
          {data.map((token) => (
            <tr key={token.id}>
              <td>{token.name ?? <span className="muted">Unnamed</span>}</td>
              <td>
                <LabelList labels={token.labels} />
              </td>
              <td>
                <TokenState token={token} agents={agents} now={now} />
              </td>
              <td className="nowrap">
                <RelativeTime value={token.createdAt} />
              </td>
              <td className="nowrap">
                <RelativeTime value={token.expiresAt} />
              </td>
              <td className="cell-actions">
                {token.usedAt ? null : (
                  <button
                    type="button"
                    className="button button--small button--danger-outline"
                    disabled={revoke.isPending && revoking === token.id}
                    onClick={() => {
                      setRevoking(token.id);
                      revoke.mutate(token.id);
                    }}
                  >
                    {revoke.isPending && revoking === token.id ? "Revoking…" : "Revoke"}
                  </button>
                )}
              </td>
            </tr>
          ))}
        </tbody>
      </Table>
    </>
  );
}
