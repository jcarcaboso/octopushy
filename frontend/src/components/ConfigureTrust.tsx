import { ShieldCheck } from "lucide-react";
import { useTrustConfigure } from "../api/queries";
import type { GuestTrust } from "../api/types";
import { Callout } from "./Callout";
import { Execution } from "./Execution";
import { ErrorMessage } from "./States";

/** Asks for trust while protected secrets would go to playbooks nobody reviewed. */
export function ConfigureTrust({ id, trust, what }: { id: string; trust: GuestTrust; what: string }) {
  const grant = useTrustConfigure();
  const { keys, codeDigest, execution, trusted } = trust;
  if (keys.length === 0 || trusted) return null;
  return (
    <div id="configure-trust">
      <Callout
        kind="warning"
        title="Waiting for trust"
        actions={
          codeDigest ? (
            <button
              type="button"
              className="button button--primary"
              disabled={grant.isPending}
              onClick={() => grant.mutate({ id, codeDigest })}
            >
              <ShieldCheck size={15} aria-hidden="true" />
              {grant.isPending ? "Trusting…" : "Trust"}
            </button>
          ) : undefined
        }
      >
        <p>
          {what} binds {keys.length === 1 ? "a protected secret" : "protected secrets"}, which only code an operator reviewed may receive:
        </p>
        <ul className="small">
          {keys.map((key) => (
            <li key={key} className="mono">
              {key}
            </li>
          ))}
        </ul>
        <p>
          Review the recipe and its Ansible project in the repository first; this is what you would be trusting. It holds until the code, or how it runs, changes.
        </p>
        <ul className="small">
          <li>
            All of it: <span className="mono break">{codeDigest}</span>
            <Execution of={execution} />
          </li>
        </ul>
        <ErrorMessage error={grant.error} />
      </Callout>
    </div>
  );
}
