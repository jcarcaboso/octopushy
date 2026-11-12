import { Bot } from "lucide-react";
import { Link } from "react-router";
import { useAgents } from "../../api/queries";
import type { Overview } from "../../api/types";
import { RelativeTime } from "../../components/RelativeTime";
import { EmptyState } from "../../components/States";
import { plural } from "../../lib/format";
import { Figure, Tile } from "./Tile";

export function AgentsTile({ overview }: { overview: Overview }) {
  const { data: agents = [] } = useAgents();
  // Blocked agents are refused on purpose, so they are not missing.
  const missing = agents.filter((a) => !a.connected && !a.disabled);
  const updates = agents.filter((a) => a.update.state === "available").length;
  return (
    <Tile id="agents" title="Agents" link={{ to: "/agents", label: "Agents" }}>
      {overview.agentsTotal === 0 ? (
        <EmptyState
          compact
          icon={Bot}
          title="No agents enrolled"
          actions={
            <Link to="/agents" className="button button--small">
              Enroll an agent
            </Link>
          }
        >
          Runs wait until an agent connects.
        </EmptyState>
      ) : (
        <>
          <Figure value={`${overview.agentsConnected} of ${overview.agentsTotal}`} caption="connected" />
          {missing.length > 0 || updates > 0 ? (
            <ul className="agents-notes">
              {missing.slice(0, 3).map((agent) => (
                <li key={agent.id} className="muted">
                  <span className="strong">{agent.name}</span>{" "}
                  {agent.lastSeenAt ? (
                    <>
                      was last seen <RelativeTime value={agent.lastSeenAt} />
                    </>
                  ) : (
                    "has not connected yet"
                  )}
                </li>
              ))}
              {missing.length > 3 ? <li className="muted">and {missing.length - 3} more not connected</li> : null}
              {updates > 0 ? (
                <li>
                  <Link to="/agents">{plural(updates, "update")} available</Link>
                </li>
              ) : null}
            </ul>
          ) : null}
        </>
      )}
    </Tile>
  );
}
