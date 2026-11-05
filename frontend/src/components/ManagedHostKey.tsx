import type { ManagedHostKey, ManagedHostKeyVersion } from "../api/types";
import { Card } from "./Card";
import { KeyValue } from "./KeyValue";
import { RelativeTime } from "./RelativeTime";

function Fingerprint({ version }: { version: ManagedHostKeyVersion }) {
  return (
    <span className="stack stack--small">
      <span className="mono">{version.fingerprint}</span>
      <code className="mono wrap-anywhere small">{version.publicKey}</code>
    </span>
  );
}

/**
 * The guest's SSH host key, when it is Octopushy's. Configure runs pin the version an apply delivered last; a plan
 * that makes a new machine carries a new one, which becomes the pin when that apply finishes.
 */
export function ManagedHostKeyCard({ hostKey }: { hostKey: ManagedHostKey }) {
  const items = [
    {
      term: "Pinned",
      value: hostKey.pinned ? <Fingerprint version={hostKey.pinned} /> : "Not yet: configure runs wait until an apply delivers it",
    },
  ];
  if (hostKey.pinned?.deliveredAt) items.push({ term: "Delivered", value: <RelativeTime value={hostKey.pinned.deliveredAt} /> });
  if (hostKey.next)
    items.push({
      term: hostKey.pinned ? "Next" : "Carried",
      value: (
        <span className="stack stack--small">
          <Fingerprint version={hostKey.next} />
          <span className="muted small">Pinned once the apply that delivers it finishes.</span>
        </span>
      ),
    });
  return (
    <Card title="Host key" id="host-key">
      <KeyValue items={items} />
    </Card>
  );
}
