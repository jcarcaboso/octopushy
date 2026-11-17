import { useState, type FormEvent } from "react";
import { Link } from "react-router";
import { useUpdateAgent } from "../../api/queries";
import type { Agent } from "../../api/types";
import { RelativeTime } from "../../components/RelativeTime";
import { useNow } from "../../components/useNow";
import { formatBytes, humanize, operationLabel, plural } from "../../lib/format";
import { formatAbsolute, formatDuration, toDate } from "../../lib/time";
import { ReachMark } from "./ReachMark";
import { entryMark, entryName, entryState, kinds } from "./reach";

const hour = 3600 * 1000;
const day = 24 * hour;

const tools: Record<string, string> = { opentofu: "OpenTofu", ansible: "Ansible", proxmox: "Proxmox", nix: "Nix" };

const places: Record<string, string> = { state: "State", work: "Workspaces", "nix-store": "Nix store", providers: "Provider cache", "nix-cache": "Nix cache" };

/** Why each tool the agent knows is missing, as it said last. Its words, shown only as text. */
function Missing({ agent }: { agent: Agent }) {
  const entries = Object.entries(agent.unavailable ?? {});
  if (entries.length === 0) return null;
  return (
    <div>
      <dt>Without</dt>
      <dd>
        <ul className="agent-detail__reasons" aria-label={`Tools ${agent.name} cannot run`}>
          {entries.map(([executor, reason]) => (
            <li key={executor}>
              <span>{tools[executor] ?? humanize(executor)}</span>
              {reason ? <span className="muted small break"> {reason}</span> : null}
            </li>
          ))}
        </ul>
      </dd>
    </div>
  );
}

/** Free disk and cache sizes from the agent's last report on this connection. Nothing when it has not reported. */
function Storage({ agent }: { agent: Agent }) {
  const storage = agent.storage;
  if (!storage) return null;
  return (
    <>
      {storage.free.length > 0 ? (
        <div>
          <dt>Free disk</dt>
          <dd>
            {storage.free.map((f) => (
              <span key={f.name} className="agent-detail__disk">
                {places[f.name] ?? humanize(f.name)} {formatBytes(f.freeBytes)} of {formatBytes(f.totalBytes)}
              </span>
            ))}
          </dd>
        </div>
      ) : null}
      {storage.caches.length > 0 ? (
        <div>
          <dt>Caches</dt>
          <dd>
            {storage.caches.map((c) => (
              <span key={c.name} className="agent-detail__disk">
                {places[c.name] ?? humanize(c.name)} {c.complete ? "" : "at least "}
                {formatBytes(c.bytes)}
              </span>
            ))}
            <span className="muted small">
              Reported <RelativeTime value={storage.reportedAt} />
            </span>
          </dd>
        </div>
      ) : null}
    </>
  );
}

function Concurrency({ agent }: { agent: Agent }) {
  const update = useUpdateAgent();
  const current = agent.maxConcurrency;
  const [draft, setDraft] = useState<string | null>(null);
  const value = draft ?? String(current);
  const parsed = Number(value);
  const valid = Number.isInteger(parsed) && parsed >= 1 && parsed <= 64;
  const dirty = draft !== null && parsed !== current;

  function save(event: FormEvent) {
    event.preventDefault();
    if (!valid || !dirty) return;
    update.mutate({ id: agent.id, maxConcurrency: parsed }, { onSuccess: () => setDraft(null) });
  }

  return (
    <form className="inline-form" onSubmit={save}>
      <input
        type="number"
        min={1}
        max={64}
        className="input--narrow num"
        value={value}
        onChange={(e) => setDraft(e.target.value)}
        aria-label={`Runs at once for ${agent.name}`}
        aria-invalid={!valid || undefined}
      />
      {dirty ? (
        <button type="submit" className="button button--small button--primary" disabled={!valid || update.isPending}>
          Save
        </button>
      ) : null}
      {update.error ? <span className="field__error">{update.error.message}</span> : null}
    </form>
  );
}

function Certificate({ expiresAt }: { expiresAt: string }) {
  const now = useNow();
  const left = toDate(expiresAt).getTime() - now;
  const text =
    left <= 0 ? "Expired" : left >= 2 * day ? `Renews in ${Math.floor(left / day)} days` : `Renews in ${plural(Math.max(1, Math.round(left / hour)), "hour")}`;
  return (
    <span className={left <= 0 ? "agent-detail__danger" : left < 7 * day ? "agent-detail__warning" : undefined} title={formatAbsolute(expiresAt)}>
      {text}
    </span>
  );
}

function Reaches({ agent }: { agent: Agent }) {
  const entries = agent.reachability ?? [];
  if (!agent.connected)
    return <p className="muted small">What an agent reaches is checked while it is connected. It starts again when this one comes back.</p>;
  if (!agent.reportsReachability)
    return <p className="muted small">This version does not report what it reaches. Work that connects anywhere waits for an agent that does.</p>;
  if (entries.length === 0) return <p className="muted small">Nothing checked yet.</p>;
  return (
    <>
      <ul className="agent-detail__list" aria-label={`Every route ${agent.name} checked`}>
        {entries.map((entry) => (
          <li key={`${entry.kind}|${entry.endpoint}|${entry.context}|${entry.image ?? ""}`} className="reach-line">
            <ReachMark mark={entryMark(entry)} />
            <span className="reach-line__main">
              <span>
                {entry.resourceId ? <Link to={`/guests/${entry.resourceId}`}>{entryName(entry)}</Link> : <span>{entryName(entry)}</span>}{" "}
                <span className="muted">
                  {kinds[entry.kind] ?? humanize(entry.kind)}, {entry.context === "agent" ? "from the agent" : "from its tools"}
                </span>
              </span>
              {entry.detail ? <span className="muted small break">{entry.detail}</span> : null}
            </span>
            <span className="reach-line__state">{entryState(entry)}</span>
            <span className="reach-line__when muted">
              <RelativeTime value={entry.checkedAt} empty="Not yet" />
            </span>
          </li>
        ))}
      </ul>
      <p className="muted small">A check shows a route, not that a login will work. Agents are asked when work needs them.</p>
    </>
  );
}

function Work({ agent }: { agent: Agent }) {
  const now = useNow();
  if (agent.work.length === 0) return <p className="muted small">Nothing right now.</p>;
  return (
    <ul className="agent-detail__list" aria-label={`What ${agent.name} is working on`}>
      {agent.work.map((work) => (
        <li key={`${work.runId}|${work.step}`}>
          <Link to={`/runs/${work.runId}`} className="work-line">
            <span className="work-line__main">
              <span className="break">{work.address}</span>
              <span className="muted small">
                #{work.runNumber} {operationLabel(work.operation)}, {work.status === "assigned" ? `starting ${work.step}` : work.step}
              </span>
            </span>
            <span className="muted small nowrap">{formatDuration(now - toDate(work.since).getTime())}</span>
          </Link>
        </li>
      ))}
    </ul>
  );
}

/** What opens under an agent's row: what it reaches, what it is working on and its settings. */
export function AgentDetail({ agent, id }: { agent: Agent; id: string }) {
  const { update } = agent;
  return (
    <div className="agent-detail" id={id}>
      <section className="agent-detail__block" aria-labelledby={`${id}-reaches`}>
        <h3 id={`${id}-reaches`} className="agent-detail__heading">
          Reaches
        </h3>
        <Reaches agent={agent} />
      </section>
      <section className="agent-detail__block" aria-labelledby={`${id}-work`}>
        <h3 id={`${id}-work`} className="agent-detail__heading">
          Working on
        </h3>
        <Work agent={agent} />
      </section>
      <section className="agent-detail__block" aria-labelledby={`${id}-settings`}>
        <h3 id={`${id}-settings`} className="agent-detail__heading">
          Settings
        </h3>
        <dl className="agent-detail__facts">
          <div>
            <dt>Runs at once</dt>
            <dd>
              <Concurrency agent={agent} />
            </dd>
          </div>
          {agent.guest ? (
            <div>
              <dt>Runs on</dt>
              <dd>
                <Link to={`/guests/${agent.guest.resourceId}`} className="break">
                  {agent.guest.address}
                </Link>
              </dd>
            </div>
          ) : null}
          {(update.state === "unavailable" || update.state === "failed") && update.reason ? (
            <div>
              <dt>{update.state === "failed" ? "Update failed" : "Updates"}</dt>
              <dd>{update.reason}</dd>
            </div>
          ) : null}
          <div>
            <dt>Certificate</dt>
            <dd>
              <Certificate expiresAt={agent.certificateExpiresAt} />
            </dd>
          </div>
          <div>
            <dt>Tools</dt>
            <dd>{agent.executors.length > 0 ? agent.executors.map((e) => tools[e] ?? humanize(e)).join(", ") : <span className="muted">None</span>}</dd>
          </div>
          <Missing agent={agent} />
          <Storage agent={agent} />
          {agent.toolRuntime ? (
            <div>
              <dt>Runtime</dt>
              <dd>{agent.toolRuntime}</dd>
            </div>
          ) : null}
          <div>
            <dt>Enrolled</dt>
            <dd>
              <RelativeTime value={agent.enrolledAt} />
            </dd>
          </div>
        </dl>
      </section>
    </div>
  );
}
