import { useState, type FormEvent } from "react";
import { ApiError } from "../../api/client";
import { usePromoteVariable, useRevokeVariable, useVariableVersions } from "../../api/queries";
import type { Variable, VariableVersion } from "../../api/types";
import { Badge } from "../../components/Badge";
import { Callout } from "../../components/Callout";
import { Card } from "../../components/Card";
import { Dialog } from "../../components/Dialog";
import { KeyValue } from "../../components/KeyValue";
import { RelativeTime } from "../../components/RelativeTime";
import { ErrorMessage, Spinner } from "../../components/States";

interface Props {
  existing: Variable;
  onClose: () => void;
}

type Action = { kind: "promote" | "revoke"; version: number; expectedCurrentVersion: number };

function History({ version }: { version: VariableVersion }) {
  return (
    <KeyValue
      items={[
        { term: "Created", value: <RelativeTime value={version.createdAt} /> },
        ...(version.activatedAt ? [
          { term: "Activated", value: <RelativeTime value={version.activatedAt} /> },
          { term: "Activated by", value: version.activatedBy ?? "—" },
          { term: "Operator confirmation", value: version.activationNote ?? "—" },
        ] : []),
        ...(version.revokedAt ? [
          { term: "Revoked", value: <RelativeTime value={version.revokedAt} /> },
          { term: "Revoked by", value: version.revokedBy ?? "—" },
          { term: "Revocation reason", value: version.revocationNote ?? "—" },
        ] : []),
        {
          term: "Used by",
          value: version.usedBy.length ? (
            <ul className="used-by">
              {version.usedBy.map((usage) => <li className="mono small" key={usage}>{usage}</li>)}
            </ul>
          ) : <span className="muted">Not referenced</span>,
        },
      ]}
    />
  );
}

/** No credential values are fetched or displayed by this dialog. */
export function VariableVersionsDialog({ existing, onClose }: Props) {
  const versions = useVariableVersions(existing.key);
  const promote = usePromoteVariable();
  const revoke = useRevokeVariable();
  const [action, setAction] = useState<Action | null>(null);
  const [note, setNote] = useState("");
  const pending = promote.isPending || revoke.isPending;
  const error = promote.error || revoke.error;

  function clearAction() {
    setAction(null);
    setNote("");
    promote.reset();
    revoke.reset();
  }

  function close() {
    clearAction();
    onClose();
  }

  function submit(event: FormEvent) {
    event.preventDefault();
    if (!action || !note.trim() || pending) return;
    if (action.kind === "promote") {
      promote.mutate(
        {
          key: existing.key,
          version: action.version,
          expectedCurrentVersion: action.expectedCurrentVersion,
          verificationNote: note.trim(),
        },
        { onSuccess: clearAction },
      );
    } else {
      revoke.mutate(
        { key: existing.key, version: action.version, reason: note.trim() },
        { onSuccess: clearAction },
      );
    }
  }

  return (
    <Dialog
      open
      size="large"
      title={`Versions of ${existing.key}`}
      onClose={close}
      description="Staging a candidate does not change the active version. History contains metadata and usage, never credential values."
      footer={
        <>
          <button
            type="button"
            className="button"
            disabled={pending || versions.isFetching}
            onClick={() => {
              clearAction();
              void versions.refetch();
            }}
          >
            Reload versions
          </button>
          <button type="button" className="button" onClick={close}>Done</button>
        </>
      }
    >
      <div className="stack">
        <p>Active version: <strong>v{existing.version}</strong>. Latest version: <strong>v{existing.latestVersion}</strong>.</p>
        {existing.revoked ? (
          <Callout kind="danger" title="The active version is revoked">
            New runs need a non-revoked active version. Existing runs pinned to the revoked version remain blocked even after another version is promoted.
          </Callout>
        ) : null}
        {action ? (
          <form className="form" onSubmit={submit}>
            <h3>{action.kind === "promote" ? "Promote" : "Revoke"} v{action.version}</h3>
            {action.kind === "promote" ? (
              <Callout kind="info" title="Operator confirmation, not automated verification">
                Promotion records your confirmation of externally performed checks. Octopushy does not automatically verify the credential.
                Promoting v{action.version} will replace active v{action.expectedCurrentVersion} for runs that have not pinned credentials yet. Existing runs keep their selected versions.
              </Callout>
            ) : (
              <Callout kind="warning" title="Revocation limits">
                Revocation blocks future assignments of this version. It cannot retract already delivered credentials and does not revoke the upstream provider token.
              </Callout>
            )}
            <div className="field">
              <label htmlFor="variable-version-note">
                {action.kind === "promote" ? "Operator confirmation note" : "Revocation reason"} <span className="muted">(required)</span>
              </label>
              <textarea
                id="variable-version-note"
                rows={3}
                value={note}
                onChange={(event) => setNote(event.target.value)}
                autoFocus
                required
                aria-describedby="variable-version-note-hint"
              />
              <div id="variable-version-note-hint" className="field__hint">
                {action.kind === "promote" ? "Describe the external checks you performed. " : "Explain why this version must no longer be assigned. "}
                This note is recorded with your signed-in identity. Do not include credential values.
              </div>
            </div>
            {error ? (
              <Callout kind="danger" title={action.kind === "promote" ? "The version could not be promoted." : "The version could not be revoked."} alert>
                {error instanceof ApiError && error.status === 409
                  ? "The version state conflicts with this change. Reload versions and review the current state before trying again."
                  : "Try again or reload versions to review the current state."}
              </Callout>
            ) : null}
            <div className="button-row">
              <button type="button" className="button" disabled={pending} onClick={clearAction}>Cancel</button>
              <button
                type="submit"
                className={action.kind === "promote" ? "button button--primary" : "button button--danger"}
                disabled={pending || !note.trim()}
              >
                {pending ? "Working…" : action.kind === "promote" ? "Confirm promotion" : "Confirm revocation"}
              </button>
            </div>
          </form>
        ) : null}
        <ErrorMessage error={versions.error} title="Version history could not be loaded" />
        {versions.isPending ? <Spinner label="Loading version history" /> : null}
        {versions.data?.length === 0 ? <p className="muted">No versions found.</p> : null}
        {versions.data?.map((version) => (
          <Card
            key={version.version}
            title={`Version ${version.version}`}
            meta={
              <Badge tone={version.status === "revoked" ? "danger" : version.status === "active" ? "success" : "neutral"}>
                {version.status}
              </Badge>
            }
            actions={
              <div className="button-row button-row--tight">
                {version.status === "candidate" || version.status === "previous" ? (
                  <button
                    type="button"
                    className="button button--small button--primary"
                    disabled={pending || action !== null}
                    onClick={() => setAction({ kind: "promote", version: version.version, expectedCurrentVersion: existing.version })}
                  >
                    Promote v{version.version}
                  </button>
                ) : null}
                {["candidate", "active", "previous"].includes(version.status) ? (
                  <button
                    type="button"
                    className="button button--small button--danger-outline"
                    disabled={pending || action !== null}
                    onClick={() => setAction({ kind: "revoke", version: version.version, expectedCurrentVersion: existing.version })}
                  >
                    Revoke v{version.version}
                  </button>
                ) : null}
              </div>
            }
          >
            <History version={version} />
          </Card>
        ))}
      </div>
    </Dialog>
  );
}
