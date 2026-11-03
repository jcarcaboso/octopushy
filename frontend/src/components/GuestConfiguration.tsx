import { Wrench } from "lucide-react";
import { Link, useNavigate } from "react-router";
import { useConfigureGuest } from "../api/queries";
import type { FollowedSecret, GuestConfiguration, GuestSchedule } from "../api/types";
import { Badge } from "./Badge";
import { Callout } from "./Callout";
import { Card } from "./Card";
import { KeyValue } from "./KeyValue";
import { Commit } from "./Mono";
import { RelativeTime } from "./RelativeTime";
import { ErrorMessage } from "./States";

const troubled = new Set(["failed", "uncertain", "blocked"]);

/** One schedule: when it next runs, and what the last firing did. A problem stays visible here. */
function ScheduleRow({ schedule }: { schedule: GuestSchedule }) {
  const { lastRunId, lastRunNumber, lastRunStatus, lastRunDetail, note, noteAt } = schedule;
  return (
    <li className="stack stack--small">
      <span>
        <span className="mono">{schedule.action}</span>{" "}
        <span className="muted">
          <span className="mono">{schedule.cron}</span> {schedule.timezone}
        </span>
      </span>
      <span>
        Next: {schedule.nextAt ? <RelativeTime value={schedule.nextAt} /> : "never"}
        {lastRunId && (
          <>
            {" · "}Last: <Link to={`/runs/${lastRunId}`}>#{lastRunNumber}</Link>{" "}
            <Badge tone={troubled.has(lastRunStatus ?? "") ? "danger" : "neutral"}>{lastRunStatus}</Badge>
          </>
        )}
      </span>
      {lastRunDetail && <span className="muted">{lastRunDetail}</span>}
      {note && (
        <span className="muted">
          {note} {noteAt && <RelativeTime value={noteAt} />}
        </span>
      )}
    </li>
  );
}

/**
 * A store value the setup takes at its active version. A newer one than the onChange action last
 * started with is owed one run, which starts once nothing else holds the guest.
 */
function FollowedSecretRow({ secret }: { secret: FollowedSecret }) {
  const version = (value: number | null | undefined) => (value == null ? "not set" : `v${value}`);
  return (
    <li>
      <span className="mono">{secret.key}</span>{" "}
      <span className="muted">
        active {version(secret.activeVersion)}, last attempted with {version(secret.startedVersion)}
      </span>{" "}
      {secret.pending && <Badge tone="neutral">Run pending</Badge>}
    </li>
  );
}

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
        {configuration.refused && (
          <Callout kind="warning" title="Setup does not start">
            {configuration.refused}
          </Callout>
        )}
        {configuration.schedules.length > 0 && (
          <ul className="stack stack--small" aria-label="Schedules">
            {configuration.schedules.map((schedule) => (
              <ScheduleRow key={`${schedule.action} ${schedule.cron}`} schedule={schedule} />
            ))}
          </ul>
        )}
        {configuration.secrets.length > 0 && (
          <ul className="stack stack--small" aria-label="Store values">
            {configuration.secrets.map((secret) => (
              <FollowedSecretRow key={secret.key} secret={secret} />
            ))}
          </ul>
        )}
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
