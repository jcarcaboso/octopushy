import { Bot, Plus } from "lucide-react";
import { useState } from "react";
import { Link } from "react-router";
import { useAgents } from "../api/queries";
import { Card } from "../components/Card";
import { PageHeader } from "../components/PageHeader";
import { EmptyState, ErrorMessage, PageSkeleton } from "../components/States";
import { Table } from "../components/Table";
import { AgentRow } from "./agents/AgentRow";
import { EnrollDialog } from "./agents/EnrollDialog";
import { TokenTable } from "./agents/TokenTable";

export function AgentsPage() {
  const { data, error, isPending } = useAgents();
  const [enrolling, setEnrolling] = useState(false);
  const enroll = (
    <button type="button" className="button button--primary" onClick={() => setEnrolling(true)}>
      <Plus size={15} aria-hidden="true" />
      Enroll an agent
    </button>
  );
  const connected = data?.filter((a) => a.connected).length ?? 0;
  const running = data?.reduce((sum, a) => sum + a.runningAttempts, 0) ?? 0;

  return (
    <>
      <PageHeader
        title="Agents"
        meta={data && data.length > 0 ? `${connected} of ${data.length} connected · ${running} running ${running === 1 ? "attempt" : "attempts"}` : "Workers that observe Proxmox and run plans and applies."}
        actions={
          <>
            <Link className="button" to="/agents/prepare">Prepare worker deployment</Link>
            {enroll}
          </>
        }
      />
      <div className="stack stack--large">
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
          <Card flush>
            <Table label="Agents" className="table--agents">
              <thead>
                <tr>
                  <th scope="col">Agent</th>
                  <th scope="col">Labels</th>
                  <th scope="col">Max concurrency</th>
                  <th scope="col">Executors / tool runtime</th>
                  <th scope="col">Last seen</th>
                  <th scope="col">Certificate expires</th>
                  <th scope="col">Running</th>
                  <th scope="col">
                    <span className="visually-hidden">Actions</span>
                  </th>
                </tr>
              </thead>
              <tbody>
                {data.map((agent) => (
                  <AgentRow key={agent.id} agent={agent} />
                ))}
              </tbody>
            </Table>
          </Card>
        )}
        <Card title="Enrollment tokens" flush>
          <TokenTable agents={data ?? []} />
        </Card>
      </div>
      <EnrollDialog open={enrolling} onClose={() => setEnrolling(false)} />
    </>
  );
}
