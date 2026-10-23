import { useState } from "react";
import { useRevisionReview, useTrustRevision } from "../../api/queries";
import type { RevisionReview } from "../../api/types";
import { Callout } from "../../components/Callout";
import { CodeBlock } from "../../components/CodeBlock";
import { Dialog } from "../../components/Dialog";
import { KeyValue } from "../../components/KeyValue";
import { ErrorMessage, Spinner } from "../../components/States";

function trustBlockedReason(id: string, data: RevisionReview | undefined): string | null {
  if (
    !data || data.revision?.id !== id || !data.revision.commit || typeof data.diff !== "string" ||
    !(data.baseCommit === null || typeof data.baseCommit === "string")
  ) return "No complete revision review is available. Refresh the review to try again.";
  if (data.blockedReason) return data.blockedReason;
  if (!data.revision.isCurrentSource) return "This revision is not from the current repository source.";
  if (!data.revision.valid) return "Definitions have errors. This revision cannot be trusted.";
  if (data.truncated !== false) return "The diff is truncated. A complete review is required.";
  if (data.revision.trustedAt) return "This revision is already trusted.";
  if (!data.canTrust) return "This revision is not eligible for trust.";
  return null;
}

export function RevisionReviewDialog({ id, onClose }: { id: string; onClose: () => void }) {
  const review = useRevisionReview(id);
  const trust = useTrustRevision();
  const [reviewed, setReviewed] = useState(false);
  const data = review.data;
  const blockedReason = trustBlockedReason(id, data);
  const ready = !blockedReason && !review.isFetching && !review.error && !trust.isPending && !trust.isError;

  function close() {
    if (!trust.isPending) onClose();
  }

  return (
    <Dialog
      open
      size="large"
      title="Review execution trust"
      onClose={close}
      description="Trust only the exact revision whose repository changes you have reviewed."
      footer={
        <>
          <button
            type="button"
            className="button"
            disabled={review.isFetching || trust.isPending}
            onClick={() => {
              setReviewed(false);
              trust.reset();
              void review.refetch();
            }}
          >
            Refresh review
          </button>
          <button
            type="button"
            className="button button--primary"
            disabled={!ready || !reviewed}
            onClick={() => {
              if (!ready || !reviewed || !data) return;
              trust.mutate(
                { id, commit: data.revision.commit, baseCommit: data.baseCommit },
                { onSuccess: onClose },
              );
            }}
          >
            {trust.isPending ? "Trusting…" : "Trust exact revision"}
          </button>
        </>
      }
    >
      <div className="stack">
        <Callout kind="warning" title="Execution trust is not apply approval">
          <p>Trust permits secret-bearing execution, including plan and observe.</p>
          <p>It does not approve infrastructure apply. Apply approval is a separate decision.</p>
          <p>Existing automatic-apply policies may proceed without another click after trust is granted.</p>
          <p>It does not guarantee safe code or cover future commits.</p>
        </Callout>
        {review.isFetching ? <Spinner label="Loading revision review" /> : null}
        <ErrorMessage error={review.error} />
        {data?.revision ? (
          <>
            <KeyValue
              items={[
                { term: "Exact target commit", value: <code className="mono break">{data.revision.commit}</code> },
                {
                  term: "Previous trusted base",
                  value: data.baseCommit === null
                    ? "First trust: full repository changes"
                    : <code className="mono break">{data.baseCommit}</code>,
                },
              ]}
            />
            <CodeBlock code={data.diff} label="Repository changes (plain text)" copy={false} />
          </>
        ) : null}
        {blockedReason && !review.isFetching && !review.error ? (
          <Callout kind="warning" title="This revision cannot be trusted">{blockedReason}</Callout>
        ) : null}
        <ErrorMessage error={trust.error} />
        {trust.isError ? <p>Refresh the review and review the changes again before confirming.</p> : null}
        <label className="radio">
          <input
            type="checkbox"
            checked={reviewed}
            disabled={!ready}
            onChange={(event) => setReviewed(event.target.checked)}
          />
          <span>I reviewed the changes and trust this exact revision for secret-bearing execution.</span>
        </label>
      </div>
    </Dialog>
  );
}
