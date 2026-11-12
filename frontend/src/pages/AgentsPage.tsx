import { Bot, Plus } from "lucide-react";
import { useState } from "react";
import { useAgents, useCreateToken, useUpdateAllAgents } from "../api/queries";
import { Card } from "../components/Card";
import { PageHeader } from "../components/PageHeader";
import { EmptyState, ErrorMessage, PageSkeleton } from "../components/States";
import { plural } from "../lib/format";
import { AgentRow } from "./agents/AgentRow";
import { EnrollDialog } from "./agents/EnrollDialog";
import { newToken } from "./agents/newToken";
import { WaitingTokens } from "./agents/WaitingTokens";

export function AgentsPage() {
  const { data, error, isPending } = useAgents();
  const create = useCreateToken();
  const updateAll = useUpdateAllAgents();
  const [enrolling, setEnrolling] = useState(false);
  const enroll = (
    <button
      type="button"
      className="button button--primary"
      onClick={() => {
        create.mutate(newToken());
        setEnrolling(true);
      }}
    >
      <Plus size={15} aria-hidden="true" />
      Enroll an agent
    </button>
  );
  const agents = data ?? [];
  const connected = agents.filter((a) => a.connected).length;
  const runs = agents.reduce((sum, a) => sum + a.work.length, 0);
  const updates = agents.filter((a) => a.update.state === "available").length;
  const meta =
    agents.length === 0
      ? "Agents that observe Proxmox and run plans and applies."
      : [
          `${connected} of ${agents.length} connected`,
          runs > 0 ? `${plural(runs, "run")} in progress` : null,
          updates > 0 ? `${plural(updates, "update")} available` : null,
        ]
          .filter(Boolean)
          .join(" · ");

  return (
    <>
      <PageHeader
        title="Agents"
        meta={meta}
        actions={
          <>
            {updates >= 2 ? (
              <button type="button" className="button" disabled={updateAll.isPending} onClick={() => updateAll.mutate()}>
                Update all
              </button>
            ) : null}
            {enroll}
          </>
        }
      />
      <div className="stack stack--large">
        <ErrorMessage error={updateAll.error} />
        {isPending ? (
          <PageSkeleton rows={1} />
        ) : !data ? (
          <ErrorMessage error={error} title="The agents could not be loaded" />
        ) : data.length === 0 ? (
          <Card>
            <EmptyState icon={Bot} title="No agents enrolled" actions={enroll}>
              An agent runs OpenTofu and talks to the Proxmox API on Octopushy's behalf. Until one connects, runs wait in
              the queue.
            </EmptyState>
          </Card>
        ) : (
          <section className="agents" aria-label="Agents">
            <div className="agents__head" aria-hidden="true">
              <span>Agent</span>
              <span>Version</span>
              <span>Reaches</span>
              <span>Work</span>
              <span />
            </div>
            <ul className="agents__list">
              {data.map((agent) => (
                <AgentRow key={agent.id} agent={agent} />
              ))}
            </ul>
          </section>
        )}
        <WaitingTokens agents={agents} />
      </div>
      <EnrollDialog open={enrolling} onClose={() => setEnrolling(false)} create={create} />
    </>
  );
}
