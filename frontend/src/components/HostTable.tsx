import type { ObservedHost } from "../api/types";
import { formatBytes } from "../lib/format";
import { formatUptime } from "../lib/time";
import { canWake } from "../lib/wake";
import { Badge } from "./Badge";
import { Meter } from "./Meter";
import { EmptyRow, Table } from "./Table";
import { WakeHostButton } from "./WakeHost";

function Online({ online }: { online: boolean | null }) {
  if (online === null) return <Badge tone="neutral">Not observed</Badge>;
  return online ? <Badge tone="success">Online</Badge> : <Badge tone="danger">Offline</Badge>;
}

/** With <paramref name="installationId"/>, a host that may need waking gets its Wake button. */
export function HostTable({ hosts, label, installationId, observeFailed = false }: {
  hosts: ObservedHost[];
  label: string;
  installationId?: string;
  observeFailed?: boolean;
}) {
  const wakeable = installationId !== undefined && hosts.some((h) => canWake(h, observeFailed));
  return (
    <Table label={label} className="table--hosts">
      <thead>
        <tr>
          <th scope="col">Host</th>
          <th scope="col">State</th>
          <th scope="col">CPU</th>
          <th scope="col">Memory</th>
          <th scope="col">Uptime</th>
          <th scope="col" title="limits.concurrentOperations">
            Concurrency limit
          </th>
          {wakeable ? (
            <th scope="col">
              <span className="visually-hidden">Power</span>
            </th>
          ) : null}
        </tr>
      </thead>
      <tbody>
        {hosts.length === 0 ? <EmptyRow colSpan={wakeable ? 7 : 6}>No hosts declared.</EmptyRow> : null}
        {hosts.map((host) => {
          const cpu = host.cpu;
          const cores = host.maxCpu;
          const memory = host.memoryBytes;
          const maxMemory = host.maxMemoryBytes;
          const limit = host.concurrentOperations;
          return (
            <tr key={host.name}>
              <th scope="row" className="mono">
                {host.name}
                {host.declared ? null : (
                  <>
                    {" "}
                    <Badge tone="warning" title="Proxmox reports this host, but installation.yaml does not declare it.">
                      Not declared
                    </Badge>
                  </>
                )}
              </th>
              <td>
                <Online online={host.online} />
              </td>
              <td className="cell-meter">
                <Meter label={`CPU on ${host.name}`} fraction={cpu} text={cores ? `${cores} ${cores === 1 ? "core" : "cores"}` : undefined} />
              </td>
              <td className="cell-meter">
                <Meter
                  label={`Memory on ${host.name}`}
                  fraction={memory !== null && maxMemory ? memory / maxMemory : null}
                  text={maxMemory ? `${formatBytes(memory)} of ${formatBytes(maxMemory)}` : undefined}
                />
              </td>
              <td className="num nowrap">{formatUptime(host.uptimeSeconds)}</td>
              <td className="num">{limit ?? <span className="muted">None</span>}</td>
              {wakeable ? (
                <td className="num">
                  <WakeHostButton installationId={installationId} host={host} observeFailed={observeFailed} small />
                </td>
              ) : null}
            </tr>
          );
        })}
      </tbody>
    </Table>
  );
}
