import { FilePen, Plus } from "lucide-react";
import { useState } from "react";
import { Link, useNavigate } from "react-router";
import { useCreateDraft, useDrafts, useRepository } from "../api/queries";
import type { DraftSummary } from "../api/types";
import { Badge } from "../components/Badge";
import { Callout } from "../components/Callout";
import { Card } from "../components/Card";
import { Commit } from "../components/Mono";
import { PageHeader } from "../components/PageHeader";
import { RelativeTime } from "../components/RelativeTime";
import { EmptyState, ErrorMessage, PageSkeleton } from "../components/States";
import { Table } from "../components/Table";
import { draftStatus } from "./editor/draftStatus";

function NewDraft({ disabled }: { disabled: boolean }) {
  const create = useCreateDraft();
  const navigate = useNavigate();
  const [title, setTitle] = useState("");
  return (
    <form
      className="stack"
      onSubmit={(e) => {
        e.preventDefault();
        create.mutate(title.trim(), { onSuccess: (detail) => navigate(`/editor/${detail.draft.id}`) });
      }}
    >
      <div className="form__row">
        <div className="field">
          <label htmlFor="draft-title">Title</label>
          <input
            id="draft-title"
            value={title}
            maxLength={120}
            onChange={(e) => setTitle(e.target.value)}
            placeholder="Raise the memory of the apps VM"
            disabled={disabled}
          />
        </div>
      </div>
      <ErrorMessage error={create.error} title="The draft was not started" />
      <div className="button-row">
        <button type="submit" className="button button--primary" disabled={disabled || !title.trim() || create.isPending}>
          <Plus size={15} aria-hidden="true" />
          {create.isPending ? "Starting…" : "Start a draft"}
        </button>
      </div>
    </form>
  );
}

export function EditorPage() {
  const { data, error, isPending } = useDrafts();
  const { data: repository } = useRepository();
  const ready = Boolean(repository?.url && repository.headCommit);
  const active = data?.filter((d) => d.status !== "published") ?? [];
  const published = data?.filter((d) => d.status === "published") ?? [];

  return (
    <>
      <PageHeader
        title="Editor"
        meta="Edit any file of the lab repository in a draft, then publish it as one commit."
      />
      <div className="stack stack--large">
        <Card title="New draft">
          {repository && !ready ? (
            <Callout kind="info">
              Connect the lab repository on the <Link to="/repository">Repository</Link> page and wait for its first sync.
            </Callout>
          ) : (
            <p className="muted">
              A draft starts from the current commit of <code className="mono">{repository?.branch ?? "the branch"}</code>.
              Saving keeps edits in Octopushy; only publishing pushes to Git.
            </p>
          )}
          <NewDraft disabled={!ready} />
        </Card>
        {isPending ? (
          <PageSkeleton rows={2} />
        ) : !data ? (
          <ErrorMessage error={error} title="The drafts could not be loaded" />
        ) : (
          <>
            <Card title="Drafts" meta={active.length} flush>
              {active.length === 0 ? (
                <EmptyState icon={FilePen} title="No drafts" compact>
                  Start a draft to edit the repository.
                </EmptyState>
              ) : (
                <DraftTable drafts={active} label="Drafts" />
              )}
            </Card>
            {published.length > 0 ? (
              <Card title="Published" meta={published.length} flush>
                <DraftTable drafts={published} label="Published drafts" published />
              </Card>
            ) : null}
          </>
        )}
      </div>
    </>
  );
}

function DraftTable({ drafts, label, published = false }: { drafts: DraftSummary[]; label: string; published?: boolean }) {
  return (
    <Table label={label}>
      <thead>
        <tr>
          <th scope="col">Title</th>
          <th scope="col">Status</th>
          <th scope="col">Changed files</th>
          <th scope="col">{published ? "Commit" : "Base"}</th>
          <th scope="col">{published ? "Published" : "Updated"}</th>
        </tr>
      </thead>
      <tbody>
        {drafts.map((d) => {
          const status = draftStatus(d.status);
          return (
            <tr key={d.id}>
              <th scope="row">
                <Link to={`/editor/${d.id}`}>{d.title}</Link>
              </th>
              <td>
                <span className="button-row button-row--tight">
                  <Badge tone={status.tone}>{status.label}</Badge>
                  {d.behind ? <Badge tone="attention">Behind</Badge> : null}
                </span>
              </td>
              <td className="num">{d.changedFiles ?? "—"}</td>
              <td>
                <Commit commit={d.publishedCommit ?? d.baseCommit} />
              </td>
              <td>
                <RelativeTime value={d.publishedAt ?? d.updatedAt} />
              </td>
            </tr>
          );
        })}
      </tbody>
    </Table>
  );
}
