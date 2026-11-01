import { Wrench } from "lucide-react";
import { useNavigate } from "react-router";
import { useConfigureGuest } from "../api/queries";
import type { GuestConfiguration } from "../api/types";
import { Badge } from "./Badge";
import { Card } from "./Card";
import { KeyValue } from "./KeyValue";
import { Commit } from "./Mono";
import { RelativeTime } from "./RelativeTime";
import { ErrorMessage } from "./States";

/** A guest's configure recipe: where it stands, and the actions that can be started by hand. */
export function GuestConfigurationCard({ id, configuration }: { id: string; configuration: GuestConfiguration }) {
  const configure = useConfigureGuest(id);
  const navigate = useNavigate();
  return (
    <Card title="Configuration" id="configuration">
      <div className="stack stack--small">
        <KeyValue
          items={[
            { term: "Recipe", value: <span className="mono">{configuration.recipe}</span> },
            {
              term: "Last success",
              value: configuration.appliedAt ? (
                <span>
                  <span className="mono">{configuration.appliedAction}</span> <Commit commit={configuration.appliedCommit} />{" "}
                  <RelativeTime value={configuration.appliedAt} />
                </span>
              ) : (
                "Never"
              ),
            },
            {
              term: "In sync",
              value: configuration.inSync ? <Badge tone="success">Yes</Badge> : <Badge tone="neutral">No</Badge>,
            },
          ]}
        />
        <ErrorMessage error={configure.error} />
        <div className="button-row">
          {configuration.actions.map((action) => (
            <button
              key={action.id}
              type="button"
              className="button"
              title={action.description ?? undefined}
              disabled={configure.isPending}
              onClick={() => configure.mutate(action.id, { onSuccess: (started) => navigate(`/runs/${started.runId}`) })}
            >
              <Wrench size={15} aria-hidden="true" />
              {action.id}
            </button>
          ))}
        </div>
      </div>
    </Card>
  );
}
