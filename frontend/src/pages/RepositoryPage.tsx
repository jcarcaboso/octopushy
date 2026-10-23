import { RefreshCw, Save } from "lucide-react";
import { useEffect, useState, type FormEvent } from "react";
import { useSearchParams } from "react-router";
import { useRepository, useRevision, useRevisions, useSyncRepository, useUpdateRepository } from "../api/queries";
import type { Repository, Revision } from "../api/types";
import { Badge } from "../components/Badge";
import { Callout } from "../components/Callout";
import { Card } from "../components/Card";
import { IssueList } from "../components/IssueList";
import { KeyValue } from "../components/KeyValue";
import { Commit } from "../components/Mono";
import { PageHeader } from "../components/PageHeader";
import { RelativeTime } from "../components/RelativeTime";
import { EmptyState, ErrorMessage, PageSkeleton, Skeleton } from "../components/States";
import { EmptyRow, Table } from "../components/Table";
import { RevisionReviewDialog } from "./repository/RevisionReviewDialog";

type Kind = "none" | "token" | "ssh";

function RepositoryForm({ repository, onSaved }: { repository: Repository; onSaved: () => void }) {
  const update = useUpdateRepository();
  const [url, setUrl] = useState(repository.url ?? "");
  const [branch, setBranch] = useState(repository.branch);
  const [pollSeconds, setPollSeconds] = useState(String(repository.pollSeconds ?? 60));
  const [kind, setKind] = useState<Kind>((["none", "token", "ssh"].includes(repository.credentialKind) ? repository.credentialKind : "none") as Kind);
  const [username, setUsername] = useState(repository.credentialUsername ?? "");
  const [secret, setSecret] = useState("");
  const [saved, setSaved] = useState(false);

  const poll = Number(pollSeconds);
  const pollValid = Number.isInteger(poll) && poll >= 10 && poll <= 86_400;
  const credentialSet = repository.credentialSet;

  function submit(event: FormEvent) {
    event.preventDefault();
    if (!pollValid || !branch.trim()) return;
    setSaved(false);
    update.mutate(
      {
        url: url.trim() || null,
        branch: branch.trim(),
        pollSeconds: poll,
        credentialKind: kind,
        credentialUsername: kind === "token" ? username.trim() || null : null,
        credentialSecret: kind !== "none" && secret ? secret : null,
      },
      {
        onSuccess: () => {
          setSecret("");
          setSaved(true);
          onSaved();
        },
      },
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
      <div className="form__row">
        <div className="field">
          <label htmlFor="repo-branch">Branch</label>
          <input id="repo-branch" className="mono" value={branch} onChange={(e) => setBranch(e.target.value)} required spellCheck={false} />
        </div>
        <div className="field">
          <label htmlFor="repo-poll">Poll every (seconds)</label>
          <input
            id="repo-poll"
            type="number"
            min={10}
            max={86_400}
            className="input--narrow num"
            value={pollSeconds}
            onChange={(e) => setPollSeconds(e.target.value)}
            aria-invalid={!pollValid || undefined}
            aria-describedby="repo-poll-hint"
          />
          <div id="repo-poll-hint" className={pollValid ? "field__hint" : "field__error"}>
            Between 10 seconds and once a day (86400).
          </div>
        </div>
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
      {saved && !update.error ? (
        <Callout kind="success" title="Saved">
          The repository is fetched now and then every {poll} seconds.
        </Callout>
      ) : null}
      <div className="button-row">
        <button type="submit" className="button button--primary" disabled={update.isPending || !pollValid || !branch.trim()}>
          <Save size={15} aria-hidden="true" />
          {update.isPending ? "Saving…" : "Save"}
        </button>
      </div>
    </form>
  );
}

function RevisionSummary({ revision }: { revision: Revision }) {
  return (
    <KeyValue
      items={[
        {
          term: "Commit",
          value: <Commit commit={revision.commit} length={10} />,
        },
        { term: "Definition validity", value: revision.valid ? <Badge tone="success">Valid</Badge> : <Badge tone="danger">Has errors</Badge> },
        { term: "Execution trust", value: <ExecutionTrust revision={revision} /> },
        { term: "Apply approval", value: "Separate decision for each proposed infrastructure apply." },
        { term: "Subject", value: revision.subject ?? <span className="muted">—</span> },
        { term: "Author", value: revision.author ?? <span className="muted">—</span> },
        { term: "Committed", value: <RelativeTime value={revision.committedAt} /> },
        { term: "Loaded", value: <RelativeTime value={revision.loadedAt} /> },
        {
          term: "Issues",
          value: (
            <span className="num">
              {revision.errorCount} errors · {revision.warningCount} warnings
            </span>
          ),
        },
      ]}
    />
  );
}

function ExecutionTrust({ revision }: { revision: Revision }) {
  return (
    <div className="stack stack--small">
      <div>
        <Badge tone={revision.trustedAt ? "success" : "warning"}>{revision.trustedAt ? "Trusted" : "Untrusted"}</Badge>
        {revision.trustedAt ? (
          <> by {revision.trustedBy ?? "Unknown operator"} · <RelativeTime value={revision.trustedAt} /></>
        ) : null}
      </div>
      <span className="muted small">{revision.isCurrentSource ? "Current repository source" : "Not current repository source"}</span>
      {revision.trustedAt && !revision.isCurrentSource ? (
        <span className="muted small">Trust does not apply to the current repository source.</span>
      ) : !revision.trustedAt ? (
        <span className="muted small">{revision.valid && revision.isCurrentSource ? "Eligible for trust review" : "Not eligible for trust"}</span>
      ) : null}
    </div>
  );
}

function RevisionIssues({ id, summary = false }: { id: string; summary?: boolean }) {
  const { data, error, isPending } = useRevision(id);
  if (isPending) return <Skeleton height={48} />;
  if (!data) return <ErrorMessage error={error} />;
  return (
    <div className="stack">
      {summary ? <RevisionSummary revision={data.revision} /> : null}
      <IssueList issues={data.issues} emptyText="This revision has no issues." />
    </div>
  );
}

function History({ latestId, onReview }: { latestId: string | null; onReview: (id: string) => void }) {
  const { data, error, isPending } = useRevisions();
  const [params, setParams] = useSearchParams();
  const selected = params.get("revision");

  if (isPending) return <Skeleton height={120} />;
  if (!data) return <ErrorMessage error={error} title="The revision history could not be loaded" />;
  const chosen = data.find((r) => r.id === selected) ?? null;

  return (
    <div className="stack">
      <Table label="Revision history">
        <thead>
          <tr>
            <th scope="col">Commit</th>
            <th scope="col">Subject</th>
            <th scope="col">Author</th>
            <th scope="col">Loaded</th>
            <th scope="col">Definition validity</th>
            <th scope="col">Execution trust</th>
            <th scope="col">
              <span className="visually-hidden">Actions</span>
            </th>
          </tr>
        </thead>
        <tbody>
          {data.length === 0 ? <EmptyRow colSpan={7}>No revisions loaded yet.</EmptyRow> : null}
          {data.map((r) => (
            <tr key={r.id} className={r.id === selected ? "row--selected" : undefined}>
              <td>
                <Commit commit={r.commit} />
                {r.id === latestId ? (
                  <>
                    {" "}
                    <Badge tone="info">Current</Badge>
                  </>
                ) : null}
              </td>
              <td>{r.subject ?? <span className="muted">—</span>}</td>
              <td className="muted">{r.author ?? "—"}</td>
              <td className="nowrap">
                <RelativeTime value={r.loadedAt} />
              </td>
              <td className="nowrap">
                {r.valid ? <Badge tone="success">Valid</Badge> : <Badge tone="danger">{r.errorCount} errors</Badge>}
                {r.warningCount > 0 ? (
                  <>
                    {" "}
                    <Badge tone="warning">{r.warningCount} warnings</Badge>
                  </>
                ) : null}
              </td>
              <td><ExecutionTrust revision={r} /></td>
              <td className="cell-actions">
                <button type="button" className="button button--small" onClick={() => onReview(r.id)}>
                  Review revision
                </button>
                <button
                  type="button"
                  className="button button--small"
                  aria-pressed={r.id === selected}
                  onClick={() => setParams(r.id === selected ? {} : { revision: r.id }, { replace: true })}
                >
                  {r.id === selected ? "Hide issues" : "Show issues"}
                </button>
              </td>
            </tr>
          ))}
        </tbody>
      </Table>
      {chosen ? (
        <section className="revision-issues" aria-label="Selected revision">
          <h3 className="subhead">
            Issues in <Commit commit={chosen.commit} /> {chosen.subject}
          </h3>
          <RevisionIssues id={chosen.id} summary />
        </section>
      ) : null}
    </div>
  );
}

export function RepositoryPage() {
  const [watching, setWatching] = useState(false);
  const [reviewId, setReviewId] = useState<string | null>(null);
  const { data, error, isPending } = useRepository({ fast: watching });
  const sync = useSyncRepository();

  useEffect(() => {
    if (!watching) return;
    const t = setTimeout(() => setWatching(false), 30_000);
    return () => clearTimeout(t);
  }, [watching]);

  if (isPending) return <PageSkeleton rows={2} />;
  if (!data) return <ErrorMessage error={error} title="The repository settings could not be loaded" />;

  const revision = data.revision;
  return (
    <>
      <PageHeader
        title="Repository"
        meta="The lab repository: Octopushy's source of truth for labs, installations, and guests."
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
        <div className="columns columns--halves">
          <Card title="Settings">
            <RepositoryForm repository={data} onSaved={() => setWatching(true)} />
          </Card>
          <div className="stack stack--large">
            <Card title="Fetch status">
              {data.lastFetchError ? (
                <Callout kind="danger" title="The last fetch failed">
                  <span className="mono small break">{data.lastFetchError}</span>
                </Callout>
              ) : null}
              <KeyValue
                items={[
                  { term: "Last fetch", value: <RelativeTime value={data.lastFetchAt} empty={data.url ? "Not fetched yet" : "No repository"} /> },
                  { term: "Head commit", value: <Commit commit={data.headCommit} length={10} /> },
                  { term: "Branch", value: <span className="mono">{data.branch}</span> },
                ]}
              />
            </Card>
            <Card title="Current revision" id="current-revision">
              {revision ? (
                <div className="stack">
                  <RevisionSummary revision={revision} />
                  <button type="button" className="button" onClick={() => setReviewId(revision.id)}>
                    Review revision
                  </button>
                  <h3 className="subhead">Issues</h3>
                  <RevisionIssues id={revision.id} />
                </div>
              ) : (
                <EmptyState compact title="No revision loaded yet">
                  {data.url ? "The first fetch loads one." : "Save a repository URL to load the first revision."}
                </EmptyState>
              )}
            </Card>
          </div>
        </div>
        <Card title="History">
          <History latestId={revision?.id ?? null} onReview={setReviewId} />
        </Card>
      </div>
      {reviewId ? <RevisionReviewDialog key={reviewId} id={reviewId} onClose={() => setReviewId(null)} /> : null}
    </>
  );
}
