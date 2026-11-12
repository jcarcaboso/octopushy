import { useState } from "react";
import { useDeleteToken, useTokens } from "../../api/queries";
import type { Agent, EnrollmentToken } from "../../api/types";
import { RelativeTime } from "../../components/RelativeTime";
import { ErrorMessage } from "../../components/States";
import { useNow } from "../../components/useNow";
import { toDate } from "../../lib/time";

function TokenLine({ token, agents, now }: { token: EnrollmentToken; agents: Agent[]; now: number }) {
  const revoke = useDeleteToken();
  const agent = token.usedAt ? agents.find((a) => a.id === token.agentId) : undefined;
  const expired = toDate(token.expiresAt).getTime() <= now;
  return (
    <li className="token-line">
      <span className={token.name ? "break" : "muted"}>{token.name ?? "Unnamed"}</span>
      <span className="muted small">
        Made <RelativeTime value={token.createdAt} />,{" "}
        {token.usedAt ? (
          <>
            used {agent ? <>by {agent.name} </> : null}
            <RelativeTime value={token.usedAt} />
          </>
        ) : (
          <>
            {expired ? "expired" : "expires"} <RelativeTime value={token.expiresAt} />
          </>
        )}
      </span>
      <span className="token-line__action">
        {token.usedAt ? null : (
          <button type="button" className="link-button small" disabled={revoke.isPending} onClick={() => revoke.mutate(token.id)}>
            {revoke.isPending ? "Revoking…" : "Revoke"}
          </button>
        )}
      </span>
      {revoke.error ? (
        <span className="field__error token-line__error" role="alert">
          {revoke.error.message}
        </span>
      ) : null}
    </li>
  );
}

/** Tokens nobody has enrolled with yet; used and expired ones are a click away. */
export function WaitingTokens({ agents }: { agents: Agent[] }) {
  const { data, error } = useTokens();
  const [showAll, setShowAll] = useState(false);
  const now = useNow();

  if (!data) return error ? <ErrorMessage error={error} title="The tokens could not be loaded" /> : null;
  const waiting = data.filter((t) => !t.usedAt && toDate(t.expiresAt).getTime() > now);
  const rest = data.filter((t) => !waiting.includes(t));
  if (waiting.length === 0 && rest.length === 0) return null;

  return (
    <section className="tokens" aria-labelledby="tokens-title">
      <h2 id="tokens-title" className="tokens__title">
        Waiting to enroll
      </h2>
      {waiting.length > 0 || showAll ? (
        <ul className="tokens__list">
          {(showAll ? [...waiting, ...rest] : waiting).map((token) => (
            <TokenLine key={token.id} token={token} agents={agents} now={now} />
          ))}
        </ul>
      ) : (
        <p className="muted small">No token is waiting.</p>
      )}
      {rest.length > 0 ? (
        <button type="button" className="link-button small tokens__more" aria-expanded={showAll} onClick={() => setShowAll(!showAll)}>
          {showAll ? "Hide used or expired tokens" : `Show ${rest.length} used or expired ${rest.length === 1 ? "token" : "tokens"}`}
        </button>
      ) : null}
    </section>
  );
}
