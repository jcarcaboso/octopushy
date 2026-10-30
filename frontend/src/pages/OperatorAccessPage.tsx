import { useQuery, useQueryClient } from "@tanstack/react-query";
import { useState, type FormEvent } from "react";
import { api, unwrap } from "../api/client";
import { ConfirmDialog } from "../components/ConfirmDialog";
import { ErrorMessage } from "../components/States";
import { useDocumentTitle } from "../components/useDocumentTitle";

const key = ["operator-tokens"] as const;

export function OperatorAccessPage() {
  useDocumentTitle("CLI access");
  const client = useQueryClient();
  const list = useQuery({ queryKey: key, queryFn: () => unwrap(api.GET("/api/auth/operator-tokens")) });
  const [name, setName] = useState("");
  const [days, setDays] = useState(30);
  const [acknowledged, setAcknowledged] = useState(false);
  const [token, setToken] = useState<string | null>(null);
  const [pending, setPending] = useState(false);
  const [error, setError] = useState<unknown>(null);
  const [revoke, setRevoke] = useState<{ id: string; name: string } | null>(null);

  async function create(event: FormEvent) {
    event.preventDefault();
    if (!acknowledged) return;
    setPending(true);
    setError(null);
    try {
      const result = await unwrap(api.POST("/api/auth/operator-tokens", { body: { name, expiresInDays: days } }));
      // Keep the one-time secret out of query/mutation caches and browser storage.
      setToken(result.token);
      setName("");
      setAcknowledged(false);
      await client.invalidateQueries({ queryKey: key });
    } catch (failure) { setError(failure); }
    finally { setPending(false); }
  }

  async function remove() {
    if (!revoke) return;
    setPending(true);
    setError(null);
    try {
      await unwrap(api.DELETE("/api/auth/operator-tokens/{id}", { params: { path: { id: revoke.id } } }));
      setRevoke(null);
      await client.invalidateQueries({ queryKey: key });
    } catch (failure) { setError(failure); }
    finally { setPending(false); }
  }

  return (
    <div className="stack">
      <header className="page-header"><h1>CLI access</h1></header>
      <p>Create a named operator grant for the CLI. It has your account's API access, including explicit approvals.
        It cannot create or manage other grants. Infrastructure credentials remain in Octopushy.</p>
      <p>Grant creation does not approve a plan, trust an installation, or update a machine.</p>
      <form className="form" onSubmit={(event) => { void create(event); }}>
        <div className="field">
          <label htmlFor="operator-name">Grant name</label>
          <input id="operator-name" value={name} maxLength={80} required onChange={(event) => setName(event.target.value)} />
        </div>
        <div className="field">
          <label htmlFor="operator-days">Expires after</label>
          <select id="operator-days" value={days} onChange={(event) => setDays(Number(event.target.value))}>
            <option value={30}>30 days</option><option value={90}>90 days</option><option value={365}>365 days</option>
          </select>
        </div>
        <label><input type="checkbox" checked={acknowledged} onChange={(event) => setAcknowledged(event.target.checked)} />
          {" "}I authorize operator access for this grant.</label>
        <button className="button button--primary" disabled={pending || !acknowledged || token !== null}>Create grant</button>
      </form>
      {token !== null && <section className="stack" aria-label="New operator grant">
        <p>This token is shown only now. Treat it like your password. Pass it to the CLI through private stdin, never a command argument.</p>
        <label htmlFor="operator-token">Operator token</label>
        <textarea id="operator-token" readOnly value={token} spellCheck={false} autoComplete="off" />
        <code>octopushy operator login --server https://your-controller --token-stdin --ca-file /path/to/public-ca.pem</code>
        <button className="button" onClick={() => setToken(null)}>I saved it privately</button>
      </section>}
      <ErrorMessage error={error ?? list.error} />
      <h2>Existing grants</h2>
      {list.data?.length === 0 && <p>No operator grants.</p>}
      <ul className="stack">{list.data?.map((grant) => <li key={grant.id}>
        <strong>{grant.name}</strong>{" · "}expires {new Date(grant.expiresAt).toLocaleString()}
        {grant.revokedAt ? " · revoked" : <button className="button button--small" onClick={() => { setError(null); setRevoke(grant); }}>Revoke {grant.name}</button>}
      </li>)}</ul>
      <ConfirmDialog open={revoke !== null} title="Revoke operator grant" confirmLabel="Revoke grant" danger
        pending={pending} error={error} onConfirm={() => { void remove(); }} onClose={() => setRevoke(null)}>
        <p>Revoke {revoke?.name}? Its CLI requests will be refused immediately. This does not cancel already approved or running work.</p>
      </ConfirmDialog>
    </div>
  );
}
