/**
 * A login key as an operator reads it: its type, the end of the key itself, and who it says it
 * belongs to. The end, because every key of a type starts alike and the comment says what it likes.
 */
function describeKey(key: string) {
  const [type, body = "", ...comment] = key.trim().split(/\s+/);
  return `${type} ${body.length > 24 ? `…${body.slice(-24)}` : body} ${comment.join(" ")}`.trim();
}

/** How a member would use the password beyond its code. Each of these is covered by trust. */
export function Execution({ of }: { of: unknown }) {
  if (typeof of !== "object" || of === null) return null;
  const run = of as {
    backend?: { type?: string; config?: unknown; env?: unknown };
    secrets?: Record<string, string>;
    agentLabels?: string[];
    proxmox?: {
      endpoint?: string | null;
      verifyTls?: boolean | null;
      readOnlyToken?: { store?: string } | null;
      privilegedToken?: { store?: string } | null;
      caCertificate?: { store?: string } | null;
    };
    template?: { source?: { url?: string } } | null;
    guest?: {
      vmid?: number;
      host?: string;
      access?: { sshAuthorizedKeys?: string[] };
      hardware?: { mountPoints?: { path?: string; volume?: string }[] };
    };
    host?: { datastores?: Record<string, string>; bridges?: string[] } | null;
  };
  const backend = run.backend ?? {};
  const keys = run.guest?.access?.sshAuthorizedKeys ?? [];
  const volumes = (run.guest?.hardware?.mountPoints ?? []).filter((mount) => mount.volume);
  const tokens = [
    run.proxmox?.readOnlyToken?.store ? `read-only ${run.proxmox.readOnlyToken.store}` : null,
    run.proxmox?.privilegedToken?.store ? `privileged ${run.proxmox.privilegedToken.store}` : null,
    run.proxmox?.caCertificate?.store ? `CA ${run.proxmox.caCertificate.store}` : null,
  ].filter(Boolean);
  return (
    <ul className="small">
      <li>
        State: <span className="mono">{backend.type ?? "?"}</span>
        {backend.config || backend.env ? <span className="mono break"> {JSON.stringify({ config: backend.config, env: backend.env })}</span> : null}
      </li>
      <li>
        Variables from the store:{" "}
        <span className="mono">
          {Object.entries(run.secrets ?? {})
            .map(([variable, key]) => `${variable} = ${key}`)
            .join(", ") || "none"}
        </span>
      </li>
      <li>
        Agents: <span className="mono">{(run.agentLabels ?? []).join(", ") || "any"}</span>
      </li>
      <li>
        Proxmox: <span className="mono">{run.proxmox?.endpoint ?? "?"}</span>
        {run.proxmox?.verifyTls === false ? ", its certificate not verified" : ""}
        {tokens.length > 0 ? <span className="mono break">, credentials {tokens.join(", ")}</span> : null}
      </li>
      {run.guest?.vmid !== undefined ? (
        <li>
          Guest: <span className="mono">{run.guest.vmid}</span> on <span className="mono">{run.guest.host ?? "?"}</span>
          {run.host?.datastores ? <span className="mono break">, datastores {Object.entries(run.host.datastores).map(([use, name]) => `${use} = ${name}`).join(", ")}</span> : null}
          {run.host?.bridges ? <span className="mono">, bridges {run.host.bridges.join(", ")}</span> : null}
          {volumes.length > 0 ? <span className="mono break">, volumes {volumes.map((mount) => `${mount.path} = ${mount.volume}`).join(", ")}</span> : null}
        </li>
      ) : null}
      <li>
        {keys.length > 0 ? (
          <>
            <strong>Can log in as root:</strong>
            <ul>
              {keys.map((key) => (
                <li key={key} className="mono break">
                  {describeKey(key)}
                </li>
              ))}
            </ul>
          </>
        ) : (
          <>Login keys: none</>
        )}
      </li>
      {run.template?.source?.url ? (
        <li>
          Template: <span className="mono break">{run.template.source.url}</span>
        </li>
      ) : null}
    </ul>
  );
}
