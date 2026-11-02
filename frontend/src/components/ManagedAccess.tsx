import type { ManagedAccess } from "../api/types";
import { Card } from "./Card";
import { KeyValue } from "./KeyValue";
import { RelativeTime } from "./RelativeTime";

/** The key Octopushy logs in to a guest with. Its private half never leaves the server's store. */
export function ManagedAccessCard({ access }: { access: ManagedAccess }) {
  return (
    <Card title="Octopushy's key" id="managed-access">
      <KeyValue
        items={[
          { term: "Fingerprint", value: <span className="mono">{access.fingerprint}</span> },
          { term: "Public key", value: <code className="mono wrap-anywhere">{access.publicKey}</code> },
          { term: "On the machine", value: access.provisionedAt ? <RelativeTime value={access.provisionedAt} /> : "Not yet" },
          { term: "Last used by a run", value: access.lastUsedAt ? <RelativeTime value={access.lastUsedAt} /> : "Never" },
        ]}
      />
    </Card>
  );
}
