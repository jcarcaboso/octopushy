import type { ManagedHostKey } from "../api/types";
import { Card } from "./Card";
import { KeyValue } from "./KeyValue";
import { RelativeTime } from "./RelativeTime";

/** The guest's SSH host key, when it is Octopushy's. Configure runs pin it once an apply has delivered it. */
export function ManagedHostKeyCard({ hostKey }: { hostKey: ManagedHostKey }) {
  return (
    <Card title="Host key" id="host-key">
      <KeyValue
        items={[
          { term: "Fingerprint", value: <span className="mono">{hostKey.fingerprint}</span> },
          { term: "Public key", value: <code className="mono wrap-anywhere">{hostKey.publicKey}</code> },
          {
            term: "Delivered",
            value: hostKey.provisionedAt ? <RelativeTime value={hostKey.provisionedAt} /> : "Not yet: configure runs wait for it",
          },
        ]}
      />
    </Card>
  );
}
