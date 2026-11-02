import { RefreshCw } from "lucide-react";
import { useNavigate } from "react-router";
import { useRotateGuestAccess } from "../api/queries";
import type { ManagedAccess } from "../api/types";
import { Badge } from "./Badge";
import { Card } from "./Card";
import { KeyValue } from "./KeyValue";
import { RelativeTime } from "./RelativeTime";
import { ErrorMessage } from "./States";

const states: Record<string, string> = {
  "in-use": "In use",
  candidate: "Candidate",
  old: "Old, still on the guest",
  removed: "Removed",
};

/** The key Octopushy logs in to a guest with. Its private half never leaves the server's store. */
export function ManagedAccessCard({ id, access }: { id: string; access: ManagedAccess }) {
  const rotate = useRotateGuestAccess(id);
  const navigate = useNavigate();
  const earlier = access.keys.filter((key) => key.version !== access.version);
  return (
    <Card title="Octopushy's key" id="managed-access">
      <div className="stack stack--small">
        <KeyValue
          items={[
            { term: "Fingerprint", value: <span className="mono">{access.fingerprint}</span> },
            { term: "Public key", value: <code className="mono wrap-anywhere">{access.publicKey}</code> },
            { term: "Version", value: access.version },
            { term: "On the machine", value: access.provisionedAt ? <RelativeTime value={access.provisionedAt} /> : "Not yet" },
            { term: "Last used by a run", value: access.lastUsedAt ? <RelativeTime value={access.lastUsedAt} /> : "Never" },
          ]}
        />
        {earlier.length > 0 && (
          <ul className="stack stack--small" aria-label="Other versions">
            {earlier.map((key) => (
              <li key={key.version}>
                <Badge tone={key.state === "old" ? "warning" : "neutral"}>{states[key.state] ?? key.state}</Badge>{" "}
                <span className="mono">
                  v{key.version} {key.fingerprint}
                </span>
              </li>
            ))}
          </ul>
        )}
        <ErrorMessage error={rotate.error} />
        <div className="button-row">
          <button
            type="button"
            className="button"
            disabled={rotate.isPending}
            onClick={() => rotate.mutate(undefined, { onSuccess: (started) => navigate(`/runs/${started.runId}`) })}
          >
            <RefreshCw size={15} aria-hidden="true" />
            Rotate key
          </button>
        </div>
      </div>
    </Card>
  );
}
