import type { ObservedHost } from "../api/types";
import { formatBytes } from "../lib/format";
import { formatUptime } from "../lib/time";
import { Badge } from "./Badge";
import { Meter } from "./Meter";
import { EmptyRow, Table } from "./Table";

function Online({ online }: { online: boolean | null }) {
  if (online === null) return <Badge tone="neutral">Not observed</Badge>;
  return online ? <Badge tone="success">Online</Badge> : <Badge tone="danger">Offline</Badge>;
}

export function HostTable({ hosts, label }: { hosts: ObservedHost[]; label: string }) {
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
        </tr>
      </thead>
      <tbody>
        {hosts.length === 0 ? <EmptyRow colSpan={6}>No hosts declared.</EmptyRow> : null}
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
            </tr>
          );
        })}
      </tbody>
    </Table>
  );
}
