import { useState, type FormEvent } from "react";
import { ApiError } from "../../api/client";
import {
  useDeleteVariableVersion,
  usePromoteVariable,
  useRevealVariableVersion,
  useRevokeVariable,
  useVariableVersions,
} from "../../api/queries";
import type { Variable, VariableVersion } from "../../api/types";
import { Callout } from "../../components/Callout";
import { ConfirmDialog } from "../../components/ConfirmDialog";
import { RelativeTime } from "../../components/RelativeTime";
import { ErrorMessage, Spinner } from "../../components/States";
import { ConflictNote } from "./ConflictNote";
import { diffLines, summarize } from "./format";
import { ValueViewer } from "./ValueText";

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

/** The lines `before` and `after` differ in, with the lines they share around them, as one block. */
function DiffViewer({ before, after, label }: { before: string; after: string; label: string }) {
  const lines = diffLines(before, after);
  const mark = { same: " ", added: "+", removed: "-" } as const;
  return (
    <div className="vars-code vars-diff" role="group" aria-label={label} tabIndex={0}>
      <pre className="vars-diff__text">
        {lines.length === 0 ? <span className="muted">Both are empty.</span> : null}
        {lines.map((line, i) => (
          <span key={i} className={`vars-diff__line vars-diff__line--${line.kind}`}>
            <span className="vars-diff__mark" aria-hidden="true">
              {mark[line.kind]}
            </span>
            {line.kind === "same" ? null : <span className="visually-hidden">{line.kind === "added" ? "Added: " : "Removed: "}</span>}
            {line.text}
            {"\n"}
          </span>
        ))}
      </pre>
    </div>
  );
}

/** What a shown version holds: its value alone, or what changes from another version to it. */
function ValuePanel({
  version,
  against,
  others,
  values,
  onCompare,
}: {
  version: number;
  against: number | null;
  others: number[];
  values: Record<number, string>;
  onCompare: (against: number | null) => void;
}) {
  const value = values[version];
  const base = against === null ? undefined : values[against];
  const id = `vars-version-compare-${version}`;
  return (
    <div className="vars-version__value">
      {others.length > 0 ? (
        <div className="vars-version__compare">
          <label htmlFor={id} className="muted small">
            Compare with
          </label>
          <select id={id} value={against ?? ""} onChange={(event) => onCompare(event.target.value === "" ? null : Number(event.target.value))}>
            <option value="">Nothing, show the value</option>
            {others.map((other) => (
              <option key={other} value={other}>
                v{other}
              </option>
            ))}
          </select>
        </div>
      ) : null}
      {value === undefined || (against !== null && base === undefined) ? (
        <Spinner label="Loading the value" />
      ) : against === null || base === undefined ? (
        <ValueViewer value={value} label={`Value of v${version}`} />
      ) : (
        <>
          <p className="muted small">
            From v{against} to v{version}: {summarize(base, value) || "the values are the same."}
          </p>
          <DiffViewer before={base} after={value} label={`Changes from v${against} to v${version}`} />
        </>
      )}
    </div>
  );
}

/**
 * Every version with its metadata and usage. A version's value is read only when asked, through the logged endpoint, and kept in
 * this tab's state alone, to see it or compare it with another version.
 */
export function VersionsTab({ entry, onRefresh }: { entry: Variable; onRefresh: () => void }) {
  const versions = useVariableVersions(entry.key);
  const promote = usePromoteVariable();
  const revoke = useRevokeVariable();
  const reveal = useRevealVariableVersion();
  const remove = useDeleteVariableVersion();
  const [action, setAction] = useState<Action | null>(null);
  const [note, setNote] = useState("");
  /** The versions shown, each with the version it is compared with, or null to show the value alone. */
  const [shown, setShown] = useState<Record<number, number | null>>({});
  const [values, setValues] = useState<Record<number, string>>({});
  const [deleting, setDeleting] = useState<number | null>(null);
  const pending = promote.isPending || revoke.isPending;
  const error = promote.error || revoke.error;
  const numbers = versions.data?.map((version) => version.version) ?? [];

  async function load(...wanted: (number | null)[]) {
    for (const version of wanted) {
      if (version === null || version in values) continue;
      try {
        const revealed = await reveal.mutateAsync({ key: entry.key, version });
        setValues((current) => ({ ...current, [version]: revealed.value }));
      } catch {
        // The error is shown from the mutation.
      }
    }
  }

  /** Values no shown version needs leave the page's state. */
  function keep(next: Record<number, number | null>) {
    setShown(next);
    const needed = new Set(Object.entries(next).flatMap(([version, against]) => (against === null ? [Number(version)] : [Number(version), against])));
    setValues((current) => Object.fromEntries(Object.entries(current).filter(([version]) => needed.has(Number(version)))));
  }

  function show(version: number) {
    // A version that is not the active one is compared with the active one at first; the active one is shown alone.
    const against = version !== entry.version && numbers.includes(entry.version) ? entry.version : null;
    setShown((current) => ({ ...current, [version]: against }));
    reveal.reset();
    void load(version, against);
  }

  function hide(version: number) {
    const next = { ...shown };
    delete next[version];
    keep(next);
  }

  function compare(version: number, against: number | null) {
    keep({ ...shown, [version]: against });
    void load(against);
  }

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

  function deleteVersion(version: number) {
    remove.mutate(
      { key: entry.key, version },
      {
        onSuccess: () => {
          setDeleting(null);
          keep(
            Object.fromEntries(
              Object.entries(shown)
                .filter(([shownVersion]) => Number(shownVersion) !== version)
                .map(([shownVersion, against]) => [shownVersion, against === version ? null : against]),
            ),
          );
        },
      },
    );
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
                    <button
                      type="button"
                      className="link-button small"
                      onClick={() => (version.version in shown ? hide(version.version) : show(version.version))}
                      aria-expanded={version.version in shown}
                      aria-label={`${version.version in shown ? "Hide the value of" : "Show the value of"} v${version.version}`}
                    >
                      {version.version in shown ? "Hide value" : "Show value"}
                    </button>
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
                    {version.version === entry.version ? null : (
                      <button
                        type="button"
                        className="link-button small vars-danger-link"
                        disabled={pending || action !== null}
                        onClick={() => setDeleting(version.version)}
                        aria-label={`Delete v${version.version}`}
                      >
                        Delete
                      </button>
                    )}
                  </span>
                )}
              </div>
              {form(version)}
              {version.version in shown ? (
                <ValuePanel
                  version={version.version}
                  against={shown[version.version]}
                  others={numbers.filter((other) => other !== version.version)}
                  values={values}
                  onCompare={(against) => compare(version.version, against)}
                />
              ) : null}
            </li>
          ))}
        </ol>
      ) : null}
      <ErrorMessage error={reveal.error} title="The value could not be shown." />
      <div>
        <button type="button" className="button button--small" disabled={pending || versions.isFetching} onClick={refresh}>
          Reload versions
        </button>
      </div>
      <ConfirmDialog
        open={deleting !== null}
        title={`Delete v${deleting} of ${entry.key}?`}
        confirmLabel={`Delete v${deleting}`}
        danger
        pending={remove.isPending}
        error={remove.error}
        onClose={() => {
          setDeleting(null);
          remove.reset();
        }}
        onConfirm={() => deleting !== null && deleteVersion(deleting)}
      >
        <p>
          The value of v{deleting} is removed for good, and its number is never given to another value. What each run was given stays
          on record.
        </p>
        <p className="muted small">
          It is refused while a definition pins this version, or a run that has not finished pinned it or was given it.
        </p>
      </ConfirmDialog>
    </div>
  );
}
