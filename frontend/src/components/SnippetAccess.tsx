import { KeyRound } from "lucide-react";
import { Link } from "react-router";
import { useCreateSnippetsKey } from "../api/queries";
import type { SnippetAccess } from "../api/types";
import { folderLink } from "../pages/variables/folders";
import { Badge } from "./Badge";
import { Card } from "./Card";
import { KeyValue } from "./KeyValue";
import { RelativeTime } from "./RelativeTime";
import { ErrorMessage } from "./States";

/**
 * How Octopushy uploads guests' cloud-init snippets to this installation's hosts, so a guest it
 * creates gets a host key of Octopushy's. The private half of the upload key never leaves the
 * server: what is shown is the public half and the command that installs it on a host.
 */
export function SnippetAccessCard({ id, snippets }: { id: string; snippets: SnippetAccess }) {
  const create = useCreateSnippetsKey(id);
  return (
    <Card title="Snippet uploads" id="snippets">
      <div className="stack stack--small">
        <p className="muted small">
          Guests whose workflow&apos;s machine stage has <code>hostKey: managed</code> get an SSH host key of Octopushy&apos;s,
          delivered in a cloud-init snippet. Each host needs a one-time setup, run as root on it.
        </p>
        <KeyValue
          items={[
            { term: "Datastore", value: <span className="mono">{snippets.datastore}</span> },
            { term: "Account", value: <span className="mono">{snippets.user}</span> },
            {
              term: "Upload key",
              value: snippets.publicKey ? (
                <span className="stack stack--small">
                  <span className="mono">{snippets.fingerprint}</span>
                  {snippets.createdAt ? (
                    <span className="muted small">
                      made <RelativeTime value={snippets.createdAt} />
                    </span>
                  ) : null}
                </span>
              ) : (
                "None yet"
              ),
            },
            {
              term: "In the definition",
              value: snippets.declared ? <Badge tone="success">Declared</Badge> : <Badge tone="neutral">Not declared</Badge>,
            },
          ]}
        />
        {snippets.command ? (
          <div className="stack stack--small">
            <span className="small">On each host, as root, for example in its shell in the Proxmox web interface:</span>
            <code className="mono wrap-anywhere" aria-label="Setup command">
              {snippets.command}
            </code>
          </div>
        ) : null}
        {snippets.hosts.length > 0 && (
          <ul className="stack stack--small" aria-label="Hosts set up for uploads">
            {snippets.hosts.map((host) => (
              <li key={host.host}>
                <span className="mono">{host.host}</span>{" "}
                <span className="muted">
                  {host.address}:{host.port}, pinned to{" "}
                  <Link to={folderLink(host.hostKeyStore)} className="mono">
                    {host.hostKeyStore}
                  </Link>
                </span>{" "}
                {host.hostKeySet ? <Badge tone="success">Key set</Badge> : <Badge tone="warning">Key not set</Badge>}
              </li>
            ))}
          </ul>
        )}
        <ErrorMessage error={create.error} />
        {!snippets.publicKey && (
          <div className="button-row">
            <button type="button" className="button" disabled={create.isPending} onClick={() => create.mutate()}>
              <KeyRound size={15} aria-hidden="true" />
              Create upload key
            </button>
          </div>
        )}
      </div>
    </Card>
  );
}
