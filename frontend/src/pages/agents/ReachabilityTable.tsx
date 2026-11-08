import type { Agent } from "../../api/types";
import { Badge } from "../../components/Badge";
import { RelativeTime } from "../../components/RelativeTime";
import type { Tone } from "../../components/status";
import { Table } from "../../components/Table";

type Entry = NonNullable<Agent["reachability"]>[number];

const kinds: Record<string, string> = {
  "proxmox-api": "Proxmox API",
  "snippets-ssh": "Snippet upload SSH",
  "guest-ssh": "Guest SSH",
};

function state(entry: Entry): { tone: Tone; label: string } {
  if (entry.usable) return { tone: "success", label: entry.probing ? "Reachable · refreshing" : "Reachable" };
  if (entry.state === "pending") return { tone: "info", label: "Probing" };
  if (entry.state === "reachable") return { tone: "muted", label: entry.probing ? "Expired · probing" : "Expired" };
  if (entry.state === "unsupported") return { tone: "attention", label: "Unsupported" };
  const label = entry.state.replace("-", " ");
  return { tone: "warning", label: `${label[0]!.toUpperCase()}${label.slice(1)}${entry.probing ? " · probing" : ""}` };
}

/**
 * What each connected agent was found to reach, as the controller routes work by it. Evidence lives with the agent's
 * connection: a reconnect starts from nothing.
 */
export function ReachabilityTable({ agents }: { agents: Agent[] }) {
  const connected = agents.filter((a) => a.connected && !a.disabled);
  const legacy = connected.filter((a) => !a.reportsReachability);
  const rows = connected.flatMap((a) => (a.reachability ?? []).map((entry) => ({ agent: a, entry })));

  return (
    <div className="stack">
      <p className="small muted">
        Work that connects to a Proxmox API, a snippet host or a guest goes only to an agent with fresh evidence that it
        reaches that endpoint from where the step connects: the agent process, or the tool runtime with the step's image.
        A probe uses no credential and shows a route and a protocol, not that a login or a change will succeed. An SSH
        probe does not check the host key; the step itself still connects to the pinned one. Labels still restrict
        which agents are considered.
      </p>
      {legacy.length > 0 ? (
        <p className="small" role="note">
          <Badge tone="warning">Update needed</Badge>{" "}
          {legacy.map((a) => a.name).join(", ")} {legacy.length === 1 ? "does" : "do"} not report reachability. Work that
          connects anywhere waits for an agent that does; work that connects to nothing still runs there.
        </p>
      ) : null}
      {rows.length === 0 ? (
        <p className="small muted">No connection has been probed yet. Agents are asked when queued work needs them.</p>
      ) : (
        <Table label="Reachability">
          <thead>
            <tr>
              <th scope="col">Agent</th>
              <th scope="col">Connection</th>
              <th scope="col">From</th>
              <th scope="col">State</th>
              <th scope="col">Checked</th>
              <th scope="col">Detail</th>
            </tr>
          </thead>
          <tbody>
            {rows.map(({ agent, entry }) => {
              const shown = state(entry);
              return (
                <tr key={`${agent.id}|${entry.kind}|${entry.endpoint}|${entry.context}|${entry.image ?? ""}`}>
                  <th scope="row" className="strong">{agent.name}</th>
                  <td>
                    <div className="small">{kinds[entry.kind] ?? entry.kind}</div>
                    <div className="small mono break">{entry.endpoint}</div>
                  </td>
                  <td>
                    <div className="small">{entry.context === "agent" ? "Agent process" : "Tool runtime"}</div>
                    {entry.image ? <div className="small muted mono break">{entry.image}</div> : null}
                  </td>
                  <td className="nowrap">
                    <Badge tone={shown.tone}>{shown.label}</Badge>
                  </td>
                  <td className="nowrap">
                    <RelativeTime value={entry.checkedAt} empty="Not yet" />
                  </td>
                  <td className="small">
                    {entry.detail ?? ""}
                    {entry.leafSha256 ? (
                      <div className="muted mono break" title="SHA-256 of the presented certificate">
                        {entry.leafSha256.slice(0, 16)}…
                      </div>
                    ) : null}
                  </td>
                </tr>
              );
            })}
          </tbody>
        </Table>
      )}
    </div>
  );
}
