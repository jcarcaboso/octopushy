import { Check, ChevronDown, GitCommitHorizontal, RefreshCw, Save } from "lucide-react";
import { useEffect, useState, type FormEvent } from "react";
import { Link, useSearchParams } from "react-router";
import { useRepository, useRevision, useRevisions, useSyncRepository, useUpdateRepository } from "../api/queries";
import type { Repository, Revision } from "../api/types";
import { Badge } from "../components/Badge";
import { Callout } from "../components/Callout";
import { IssueList } from "../components/IssueList";
import { Commit } from "../components/Mono";
import { PageHeader } from "../components/PageHeader";
import { RelativeTime } from "../components/RelativeTime";
import { EmptyState, ErrorMessage, PageSkeleton, Skeleton } from "../components/States";
import { WorkflowsSource } from "../components/WorkflowsSource";
import { plural } from "../lib/format";

type Kind = "none" | "token" | "ssh";

/** How many revisions the history shows before it is asked for the rest. */
const recent = 15;

const intervals = [
  { seconds: 10, label: "10 s" },
  { seconds: 60, label: "60 s" },
  { seconds: 300, label: "5 min" },
  { seconds: 3_600, label: "1 h" },
];

/** The settings as they are saved now, to send back with the one that changes. */
function saved(repository: Repository) {
  return {
    url: repository.url,
    branch: repository.branch,
    pollSeconds: repository.pollSeconds,
    credentialKind: repository.credentialKind,
    credentialUsername: repository.credentialUsername,
    credentialSecret: null,
  };
}

function credentialSummary(repository: Repository): string {
  if (repository.credentialKind === "none") return "No credential";
  const what = repository.credentialKind === "ssh" ? "SSH key" : "Token";
  return `${what} ${repository.credentialSet ? "set" : "not set"}`;
}

/** "2 errors, 1 warning", or nothing for a valid revision without warnings. */
function issueSummary(revision: Revision): string {
  const parts = [
    revision.errorCount > 0 ? plural(revision.errorCount, "error") : revision.valid ? null : "Has errors",
    revision.warningCount > 0 ? plural(revision.warningCount, "warning") : null,
  ];
  return parts.filter(Boolean).join(", ");
}

function FetchStatus({ repository }: { repository: Repository }) {
  const [tone, label] = !repository.url
    ? ["muted", "Not connected"]
    : repository.lastFetchError
      ? ["danger", "Last fetch failed"]
      : repository.lastFetchAt
        ? ["success", "In sync"]
        : ["muted", "Not fetched yet"];
  return (
    <span className="repo-status">
      <span className="repo-status__label">
        <span className={`repo-status__dot repo-status__dot--${tone}`} aria-hidden="true" />
        {label}
      </span>
      {repository.lastFetchAt ? (
        <span>
          Checked <RelativeTime value={repository.lastFetchAt} />
        </span>
      ) : null}
    </span>
  );
}

function SourceForm({ repository, onSaved, onCancel }: { repository: Repository; onSaved: (next: Repository) => void; onCancel?: () => void }) {
  const update = useUpdateRepository();
  const [url, setUrl] = useState(repository.url ?? "");
  const [branch, setBranch] = useState(repository.branch);
  const [kind, setKind] = useState<Kind>((["none", "token", "ssh"].includes(repository.credentialKind) ? repository.credentialKind : "none") as Kind);
  const [username, setUsername] = useState(repository.credentialUsername ?? "");
  const [secret, setSecret] = useState("");
  const credentialSet = repository.credentialSet;

  function submit(event: FormEvent) {
    event.preventDefault();
    if (!branch.trim()) return;
    update.mutate(
      {
        ...saved(repository),
        url: url.trim() || null,
        branch: branch.trim(),
        credentialKind: kind,
        credentialUsername: kind === "token" ? username.trim() || null : null,
        credentialSecret: kind !== "none" && secret ? secret : null,
      },
      { onSuccess: onSaved },
    );
  }

  return (
    <form className="form" onSubmit={submit}>
      <div className="field">
        <label htmlFor="repo-url">URL</label>
        <input
          id="repo-url"
          className="mono"
          value={url}
          onChange={(e) => setUrl(e.target.value)}
          placeholder="https://git.example.org/homelab/lab.git"
          spellCheck={false}
          autoCapitalize="off"
          aria-describedby="repo-url-hint"
        />
        <div id="repo-url-hint" className="field__hint">
          HTTPS, SSH, or a path on the server. Leave empty to disconnect.
        </div>
      </div>
      <div className="field">
        <label htmlFor="repo-branch">Branch</label>
        <input id="repo-branch" className="mono" value={branch} onChange={(e) => setBranch(e.target.value)} required spellCheck={false} />
      </div>
      <fieldset className="field">
        <legend>Credential</legend>
        <div className="radio-row radio-row--inline">
          {(["none", "token", "ssh"] as const).map((k) => (
            <label className="radio" key={k}>
              <input type="radio" name="credential-kind" value={k} checked={kind === k} onChange={() => setKind(k)} />
              <span>{k === "none" ? "None" : k === "token" ? "Token (HTTPS)" : "SSH key"}</span>
            </label>
          ))}
        </div>
      </fieldset>
      {kind === "token" ? (
        <div className="field">
          <label htmlFor="repo-user">User name</label>
          <input id="repo-user" value={username} onChange={(e) => setUsername(e.target.value)} autoComplete="off" spellCheck={false} />
        </div>
      ) : null}
      {kind !== "none" ? (
        <div className="field">
          <label htmlFor="repo-secret">
            {kind === "token" ? "Token" : "Private key"}{" "}
            {credentialSet ? <Badge tone="success">Set</Badge> : <Badge tone="neutral">Not set</Badge>}
          </label>
          {kind === "token" ? (
            <input
              id="repo-secret"
              type="password"
              value={secret}
              onChange={(e) => setSecret(e.target.value)}
              autoComplete="new-password"
              placeholder={credentialSet ? "Leave empty to keep the saved credential" : ""}
              aria-describedby="repo-secret-hint"
            />
          ) : (
            <textarea
              id="repo-secret"
              className="mono"
              rows={4}
              value={secret}
              onChange={(e) => setSecret(e.target.value)}
              autoComplete="off"
              spellCheck={false}
              placeholder={credentialSet ? "Leave empty to keep the saved key" : "-----BEGIN OPENSSH PRIVATE KEY-----"}
              aria-describedby="repo-secret-hint"
            />
          )}
          <div id="repo-secret-hint" className="field__hint">
            Write-only: it is stored as a secret and never shown again.
          </div>
        </div>
      ) : null}
      <ErrorMessage error={update.error} />
      <div className="button-row">
        <button type="submit" className="button button--primary" disabled={update.isPending || !branch.trim()}>
          <Save size={15} aria-hidden="true" />
          {update.isPending ? "Saving…" : "Save"}
        </button>
        {onCancel ? (
          <button type="button" className="button" onClick={onCancel}>
            Cancel
          </button>
        ) : null}
      </div>
    </form>
  );
}

/** Where the lab repository is read from. Once it is set it is a few words; the form opens only to change it. */
function Source({ repository, onSaved }: { repository: Repository; onSaved: () => void }) {
  const [editing, setEditing] = useState(false);
  return (
    <section className="repo-side__section" aria-labelledby="repo-source">
      <h2 id="repo-source" className="repo-side__heading">
        Source
      </h2>
      {editing || !repository.url ? (
        <SourceForm
          repository={repository}
          onSaved={() => {
            setEditing(false);
            onSaved();
          }}
          onCancel={repository.url ? () => setEditing(false) : undefined}
        />
      ) : (
        <>
          <span className="mono break">{repository.url}</span>
          <span className="inline-group">
            <code className="run-chip run-chip--mono">{repository.branch}</code>
            <span className="run-chip">{credentialSummary(repository)}</span>
          </span>
          <button type="button" className="link-button small repo-side__action" onClick={() => setEditing(true)}>
            Edit
          </button>
        </>
      )}
    </section>
  );
}

/** How often the repository is checked: the one setting worth keeping in view. A usual interval saves when it is chosen. */
function CheckInterval({ repository }: { repository: Repository }) {
  const update = useUpdateRepository();
  const [other, setOther] = useState(false);
  const [seconds, setSeconds] = useState(String(repository.pollSeconds));
  const custom = other || !intervals.some((interval) => interval.seconds === repository.pollSeconds);
  const value = Number(seconds);
  const valid = Number.isInteger(value) && value >= 10 && value <= 86_400;
  const save = (pollSeconds: number) => update.mutate({ ...saved(repository), pollSeconds }, { onSuccess: () => setOther(false) });

  return (
    <section className="repo-side__section" aria-labelledby="repo-interval">
      <h2 id="repo-interval" className="repo-side__heading">
        Check every
      </h2>
      <div className="choice" role="group" aria-labelledby="repo-interval">
        {intervals.map((interval) => (
          <button
            key={interval.seconds}
            type="button"
            className="choice__item"
            aria-pressed={!custom && repository.pollSeconds === interval.seconds}
            disabled={update.isPending}
            onClick={() => (interval.seconds === repository.pollSeconds ? setOther(false) : save(interval.seconds))}
          >
            {interval.label}
          </button>
        ))}
        <button
          type="button"
          className="choice__item"
          aria-pressed={custom}
          disabled={update.isPending}
          onClick={() => {
            setSeconds(String(repository.pollSeconds));
            setOther(true);
          }}
        >
          Other
        </button>
      </div>
      {custom ? (
        <form
          className="repo-interval"
          onSubmit={(event) => {
            event.preventDefault();
            if (valid) save(value);
          }}
        >
          <input
            type="number"
            min={10}
            max={86_400}
            className="input--narrow num"
            value={seconds}
            onChange={(e) => setSeconds(e.target.value)}
            aria-label="Seconds between checks"
            aria-invalid={!valid || undefined}
            aria-describedby="repo-interval-hint"
          />
          <button type="submit" className="button" disabled={update.isPending || !valid || value === repository.pollSeconds}>
            {update.isPending ? "Saving…" : "Save"}
          </button>
          <div id="repo-interval-hint" className={valid ? "field__hint" : "field__error"}>
            Seconds, from 10 to once a day (86400).
          </div>
        </form>
      ) : null}
      <ErrorMessage error={update.error} />
    </section>
  );
}

function CurrentRevision({ repository }: { repository: Repository }) {
  const revision = repository.revision;
  const issues = revision ? issueSummary(revision) : "";
  return (
    <section className="repo-side__section" aria-labelledby="repo-current">
      <h2 id="repo-current" className="repo-side__heading">
        Current revision
      </h2>
      {revision ? (
        <>
          <span className="repo-current__commit">
            <GitCommitHorizontal size={16} aria-hidden="true" />
            <Commit commit={revision.commit} length={10} />
          </span>
          <span>{revision.subject ?? <span className="muted">(no subject)</span>}</span>
          <span className="muted small">
            {revision.author ? `${revision.author}, loaded ` : "Loaded "}
            <RelativeTime value={revision.loadedAt} />
          </span>
          {issues ? (
            <span className={revision.valid ? "repo-current__issues" : "repo-current__issues repo-current__issues--danger"}>{issues}</span>
          ) : (
            <span className="valid-mark">
              <Check size={14} aria-hidden="true" />
              Valid
            </span>
          )}
        </>
      ) : (
        <span className="muted">{repository.url ? "None yet: the first fetch loads one." : "None yet."}</span>
      )}
    </section>
  );
}

function RevisionIssues({ id }: { id: string }) {
  const { data, error, isPending } = useRevision(id);
  if (isPending) return <Skeleton height={32} />;
  if (!data) return <ErrorMessage error={error} />;
  return <IssueList issues={data.issues} emptyText="This revision has no issues." />;
}

function History({ latestId, connected }: { latestId: string | null; connected: boolean }) {
  const { data, error, isPending } = useRevisions();
  const [params, setParams] = useSearchParams();
  const [all, setAll] = useState(false);
  const selected = params.get("revision");

  if (isPending) return <Skeleton height={120} />;
  if (!data) return <ErrorMessage error={error} title="The revision history could not be loaded" />;
  if (data.length === 0) {
    return (
      <EmptyState compact title="No revisions loaded yet">
        {connected ? "The first fetch loads one." : "Save a repository URL under Source to load the first revision."}
      </EmptyState>
    );
  }
  // A linked revision further back than the recent ones still has to be on the page.
  const far = data.findIndex((r) => r.id === selected) >= recent;
  const rows = all || far ? data : data.slice(0, recent);

  return (
    <div>
      <div className="revs">
        <div className="revs__inner">
          <div className="revs__head" aria-hidden="true">
            <span>Commit</span>
            <span>Change</span>
            <span>Author</span>
            <span>Loaded</span>
            <span>Definition</span>
          </div>
          <ul className="revs__list" aria-label="Revision history">
            {rows.map((r, index) => {
              const issues = issueSummary(r);
              const open = r.id === selected && issues !== "";
              // The same commit twice means its workflows resolved differently; only then does the digest tell them apart.
              const twin = rows[index - 1]?.commit === r.commit || rows[index + 1]?.commit === r.commit;
              return (
                <li key={r.id} className="revs__item">
                  <div className="revs__row">
                    <span>
                      <Commit commit={r.commit} />
                      {twin && r.workflowsDigest ? (
                        <span className="muted small" title={`A revision is its commit and what its workflow references resolved to (${r.workflowsDigest})`}>
                          {" "}
                          + <code className="mono">{r.workflowsDigest.slice(0, 7)}</code>
                        </span>
                      ) : null}
                    </span>
                    <span className="revs__change">
                      <span className="revs__subject" title={r.subject ?? undefined}>
                        {r.subject ?? <span className="muted">—</span>}
                      </span>
                      {r.id === latestId ? <span className="run-chip run-chip--service">Current</span> : null}
                      {r.isCurrentSource ? null : <span className="run-chip">Previous source</span>}
                    </span>
                    <span className="muted small">{r.author ?? "—"}</span>
                    <span className="muted small nowrap">
                      <RelativeTime value={r.loadedAt} />
                    </span>
                    <span>
                      {issues ? (
                        <button
                          type="button"
                          className={r.valid ? "rev-issues rev-issues--warning" : "rev-issues rev-issues--danger"}
                          aria-expanded={open}
                          onClick={() => setParams(open ? {} : { revision: r.id }, { replace: true })}
                        >
                          {issues}
                          <ChevronDown size={12} aria-hidden="true" />
                        </button>
                      ) : (
                        <span className="valid-mark">
                          <Check size={14} aria-hidden="true" />
                          Valid
                        </span>
                      )}
                    </span>
                  </div>
                  {open ? (
                    <div className="revs__issues">
                      <RevisionIssues id={r.id} />
                    </div>
                  ) : null}
                </li>
              );
            })}
          </ul>
        </div>
      </div>
      {data.length > recent ? (
        <div className="revs__foot">
          <span>Showing the last {rows.length} revisions</span>
          <button
            type="button"
            className="link-button"
            onClick={() => {
              if (far) setParams({}, { replace: true });
              setAll(rows.length <= recent);
            }}
          >
            {rows.length > recent ? `Show only the last ${recent}` : `Show the last ${data.length}`}
          </button>
        </div>
      ) : null}
    </div>
  );
}

export function RepositoryPage() {
  const [watching, setWatching] = useState(false);
  const { data, error, isPending } = useRepository({ fast: watching });
  const sync = useSyncRepository();
  const [params] = useSearchParams();

  useEffect(() => {
    if (!watching) return;
    const t = setTimeout(() => setWatching(false), 30_000);
    return () => clearTimeout(t);
  }, [watching]);

  if (isPending) return <PageSkeleton rows={2} />;
  if (!data) return <ErrorMessage error={error} title="The repository settings could not be loaded" />;

  const workflows = data.workflows ?? null;
  const tab = workflows && params.get("tab") === "workflows" ? "workflows" : "history";
  return (
    <>
      <PageHeader
        title="Repository"
        meta={<FetchStatus repository={data} />}
        actions={
          <button
            type="button"
            className="button"
            disabled={!data.url || sync.isPending}
            onClick={() => sync.mutate(undefined, { onSuccess: () => setWatching(true) })}
          >
            <RefreshCw size={15} aria-hidden="true" className={watching ? "spin" : undefined} />
            Sync now
          </button>
        }
      />
      <div className="stack stack--large">
        <ErrorMessage error={sync.error} />
        {data.lastFetchError ? (
          <Callout kind="danger" title="The last fetch failed">
            <span className="mono small break">{data.lastFetchError}</span>
          </Callout>
        ) : null}
        <div className="repo">
          <div className="repo__main">
            <nav className="tabs" aria-label="Repository sections">
              <Link to={{ search: "" }} replace className="tabs__item" aria-current={tab === "history" ? "page" : undefined}>
                History
              </Link>
              {workflows ? (
                <Link to={{ search: "?tab=workflows" }} replace className="tabs__item" aria-current={tab === "workflows" ? "page" : undefined}>
                  Workflows <span className="tabs__count">{workflows.workflows.length}</span>
                </Link>
              ) : null}
            </nav>
            {tab === "workflows" && workflows ? (
              <WorkflowsSource workflows={workflows} />
            ) : (
              <History latestId={data.revision?.id ?? null} connected={Boolean(data.url)} />
            )}
          </div>
          <aside className="repo-side" aria-label="Repository settings">
            <Source repository={data} onSaved={() => setWatching(true)} />
            <CheckInterval repository={data} />
            <CurrentRevision repository={data} />
          </aside>
        </div>
      </div>
    </>
  );
}
