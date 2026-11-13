import { useState, type FormEvent } from "react";
import { ApiError } from "../../api/client";
import { usePromoteVariable, useRevokeVariable, useVariableVersions } from "../../api/queries";
import type { Variable, VariableVersion } from "../../api/types";
import { Callout } from "../../components/Callout";
import { RelativeTime } from "../../components/RelativeTime";
import { ErrorMessage, Spinner } from "../../components/States";
import { ConflictNote } from "./ConflictNote";

type Action = { kind: "promote" | "revoke"; version: number };

const tones: Record<string, string> = { active: "success", candidate: "info", revoked: "danger" };

function Status({ status }: { status: string }) {
  return <span className={`run-chip vars-chip--${tones[status] ?? "neutral"}`}>{status.charAt(0).toUpperCase() + status.slice(1)}</span>;
}

function Facts({ version }: { version: VariableVersion }) {
  return (
    <div className="vars-version__facts">
      {version.activatedAt ? (
        <div>
          Active since <RelativeTime value={version.activatedAt} />
          {version.activatedBy ? <> by {version.activatedBy}</> : null}
          {version.activationNote ? <span className="vars-version__note">{version.activationNote}</span> : null}
        </div>
      ) : (
        <div>
          Saved <RelativeTime value={version.createdAt} />
        </div>
      )}
      {version.revokedAt ? (
        <div className="vars-version__revoked">
          Revoked <RelativeTime value={version.revokedAt} />
          {version.revokedBy ? <> by {version.revokedBy}</> : null}
          {version.revocationNote ? <span className="vars-version__note">{version.revocationNote}</span> : null}
        </div>
      ) : null}
      {version.usedBy.length > 0 ? (
        <div>
          Used by{" "}
          {version.usedBy.map((usage, i) => (
            <span key={usage}>
              {i > 0 ? ", " : null}
              <code>{usage}</code>
            </span>
          ))}
        </div>
      ) : null}
    </div>
  );
}

/** Metadata and usage of every version, never a value. */
export function VersionsTab({ entry, onRefresh }: { entry: Variable; onRefresh: () => void }) {
  const versions = useVariableVersions(entry.key);
  const promote = usePromoteVariable();
  const revoke = useRevokeVariable();
  const [action, setAction] = useState<Action | null>(null);
  const [note, setNote] = useState("");
  const pending = promote.isPending || revoke.isPending;
  const error = promote.error || revoke.error;

  function clear() {
    setAction(null);
    setNote("");
    promote.reset();
    revoke.reset();
  }

  function refresh() {
    clear();
    onRefresh();
    void versions.refetch();
  }

  function submit(event: FormEvent) {
    event.preventDefault();
    if (!action || pending) return;
    if (action.kind === "promote") {
      promote.mutate(
        { key: entry.key, version: action.version, expectedCurrentVersion: entry.version, verificationNote: note.trim() || null },
        { onSuccess: clear },
      );
    } else if (note.trim()) {
      revoke.mutate({ key: entry.key, version: action.version, reason: note.trim() }, { onSuccess: clear });
    }
  }

  function form(version: VariableVersion) {
    if (!action || action.version !== version.version) return null;
    const promoting = action.kind === "promote";
    const id = `vars-version-note-${version.version}`;
    return (
      <form className="vars-version__form" onSubmit={submit}>
        {promoting ? (
          <p className="muted small">
            v{version.version} replaces v{entry.version} for runs that have not been given their values yet. Runs that have keep theirs.
            {entry.usedBy.length > 0 ? " Guests and services that follow this key get a setup run." : null}
          </p>
        ) : (
          <Callout kind="warning" title="What revoking does">
            Revocation blocks future assignments of this version. It cannot retract already delivered credentials and does not revoke the
            upstream provider token.
          </Callout>
        )}
        <div className="vars-version__form-row">
          <input
            id={id}
            aria-label={promoting ? "Note, optional" : "Reason for revoking"}
            placeholder={promoting ? "Note, optional" : "Reason for revoking"}
            value={note}
            onChange={(event) => setNote(event.target.value)}
            autoComplete="off"
            autoFocus
            required={!promoting}
          />
          <button type="button" className="button button--small" disabled={pending} onClick={clear}>
            Cancel
          </button>
          <button
            type="submit"
            className={promoting ? "button button--small button--primary" : "button button--small button--danger"}
            disabled={pending || (!promoting && !note.trim())}
          >
            {pending ? "Working…" : promoting ? `Make v${version.version} active` : `Revoke v${version.version}`}
          </button>
        </div>
        {error instanceof ApiError && error.status === 409 ? (
          <ConflictNote message={error.message} onRefresh={refresh} />
        ) : (
          <ErrorMessage error={error} title={promoting ? "The version could not be made active." : "The version could not be revoked."} />
        )}
      </form>
    );
  }

  return (
    <div className="stack">
      {entry.revoked ? (
        <Callout kind="danger" title="The active version is revoked">
          New runs need a version that is not revoked. Runs pinned to the revoked version stay blocked even after another version becomes
          active.
        </Callout>
      ) : null}
      <ErrorMessage error={versions.error} title="The versions could not be loaded" />
      {versions.isPending ? <Spinner label="Loading the versions" /> : null}
      {versions.data?.length === 0 ? <p className="muted">No versions yet.</p> : null}
      {versions.data && versions.data.length > 0 ? (
        <ol className="vars-versions" aria-label={`Versions of ${entry.key}`}>
          {versions.data.map((version) => (
            <li key={version.version} className="vars-version">
              <div className="vars-version__head">
                <span className="vars-version__number">v{version.version}</span>
                <Status status={version.status} />
                <Facts version={version} />
                {action?.version === version.version ? null : (
                  <span className="vars-version__actions">
                    {version.status === "candidate" || version.status === "previous" ? (
                      <button
                        type="button"
                        className="link-button small"
                        disabled={pending || action !== null}
                        onClick={() => setAction({ kind: "promote", version: version.version })}
                        aria-label={`${version.status === "candidate" ? "Make active" : "Make active again"} v${version.version}`}
                      >
                        {version.status === "candidate" ? "Make active" : "Make active again"}
                      </button>
                    ) : null}
                    {["candidate", "active", "previous"].includes(version.status) ? (
                      <button
                        type="button"
                        className="link-button small vars-danger-link"
                        disabled={pending || action !== null}
                        onClick={() => setAction({ kind: "revoke", version: version.version })}
                        aria-label={`Revoke v${version.version}`}
                      >
                        Revoke
                      </button>
                    ) : null}
                  </span>
                )}
              </div>
              {form(version)}
            </li>
          ))}
        </ol>
      ) : null}
      <div>
        <button type="button" className="button button--small" disabled={pending || versions.isFetching} onClick={refresh}>
          Reload versions
        </button>
      </div>
    </div>
  );
}
