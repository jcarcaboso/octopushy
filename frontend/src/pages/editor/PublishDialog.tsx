import { Upload } from "lucide-react";
import { useState } from "react";
import { usePublishDraft } from "../../api/queries";
import type { DraftDetail } from "../../api/types";
import { Dialog } from "../../components/Dialog";
import { ErrorMessage } from "../../components/States";
import { ChangeList } from "./ChangeList";

interface Props {
  detail: DraftDetail;
  open: boolean;
  onClose: () => void;
  onPublished: (commit: string) => void;
}

/** Writes the commit message and confirms the push; the server validates and merges first. */
export function PublishDialog({ detail, open, onClose, onPublished }: Props) {
  const publish = usePublishDraft(detail.draft.id);
  const [message, setMessage] = useState(detail.draft.title);
  const [confirmed, setConfirmed] = useState(false);
  const valid = message.trim().length > 0 && confirmed;

  const submit = () =>
    publish.mutate(
      { expectedHead: detail.draft.headCommit, message: message.trim(), confirmRepositoryWrite: true },
      { onSuccess: (result) => onPublished(result.commit) },
    );

  return (
    <Dialog
      open={open}
      onClose={onClose}
      title="Publish draft"
      description={
        <>
          Octopushy validates the draft, merges it onto the latest <code className="mono">{detail.branch}</code>, and pushes one
          commit. Octopushy loads it at the next sync and proposes the runs it implies.
        </>
      }
      footer={
        <>
          <button type="button" className="button" onClick={onClose}>
            Cancel
          </button>
          <button type="button" className="button button--primary" onClick={submit} disabled={!valid || publish.isPending}>
            <Upload size={15} aria-hidden="true" />
            {publish.isPending ? "Publishing…" : `Push to ${detail.branch}`}
          </button>
        </>
      }
    >
      <div className="stack">
        <div className="field">
          <label htmlFor="publish-message">Commit message</label>
          <textarea id="publish-message" rows={4} value={message} onChange={(e) => setMessage(e.target.value)} />
        </div>
        <div className="field">
          <span className="field__label">Changes</span>
          <ChangeList changes={detail.changes} />
        </div>
        <label className="checkbox">
          <input type="checkbox" checked={confirmed} onChange={(e) => setConfirmed(e.target.checked)} />
          Push this commit to <code className="mono">{detail.branch}</code> with the configured repository credential
        </label>
        <ErrorMessage error={publish.error} title="The draft was not published" />
      </div>
    </Dialog>
  );
}
