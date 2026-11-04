import { ArrowLeft, GitCommitHorizontal, RotateCcw, Search, Trash2 } from "lucide-react";
import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { Link, useLocation, useNavigate, useParams, useSearchParams } from "react-router";
import { changeRefusal, errorMessage, isNotFound } from "../api/client";
import { useChangeForm, useCommitChange, usePreviewChange } from "../api/queries";
import type { ChangeForm, ChangePreview, ChangeRequest } from "../api/types";
import { Callout } from "../components/Callout";
import { Card } from "../components/Card";
import { CodeBlock } from "../components/CodeBlock";
import { DiffView } from "../components/DiffView";
import { IssueList } from "../components/IssueList";
import { Commit } from "../components/Mono";
import { PageHeader } from "../components/PageHeader";
import { SchemaForm, type FieldIssue } from "../components/SchemaForm";
import { ErrorMessage, NotFound, PageSkeleton } from "../components/States";
import { isObject, readUi, type JsonObject, type Schema } from "../lib/jsonSchema";

type Mode = "edit" | "upgrade" | "remove";

const guestSections = ["hardware", "network", "inputs"] as const;

/** The reference a form upgrades to: the newer release's major, `fedora-vm@2`. */
function upgradeReference(form: ChangeForm): string | null {
  return form.upgrade ? `${form.upgrade.id}@${form.upgrade.version.split(".")[0]}` : null;
}

function sectionsOf(form: ChangeForm): readonly string[] {
  return form.resourceKind === "guest" ? guestSections : ["inputs"];
}

/** The form's starting value: the definition's sections that are there. */
function initialValue(form: ChangeForm): JsonObject {
  const value: JsonObject = {};
  const source = form as unknown as Record<string, unknown>;
  for (const section of sectionsOf(form)) if (isObject(source[section])) value[section] = structuredClone(source[section]) as JsonObject;
  return value;
}

/** Without an inputs schema, each section is edited as YAML. */
function fallbackSchema(form: ChangeForm): Schema {
  return { type: "object", properties: Object.fromEntries(sectionsOf(form).map((s) => [s, {}])) };
}

/**
 * Edits a guest's or service's definition from its workflow's inputs schema, shows the exact diff that will be committed,
 * and commits it as one change. Upgrading to a newer major and removing a guest go through the same review.
 */
export function ChangeEditorPage() {
  const { id = "" } = useParams();
  const [params] = useSearchParams();
  const location = useLocation();
  const kind = location.pathname.startsWith("/services/") ? "service" : "guest";
  const mode: Mode = params.get("mode") === "upgrade" ? "upgrade" : params.get("mode") === "remove" ? "remove" : "edit";
  const { data: form, error, isPending, refetch, isFetching, isFetchedAfterMount } = useChangeForm(id, { fresh: true });

  // The guest's page may have read the form a while ago: the editor starts from the commit it reads now.
  if (isPending || (!isFetchedAfterMount && isFetching)) return <PageSkeleton rows={4} />;
  if (!form) return isNotFound(error) ? <NotFound what={kind} /> : <ErrorMessage error={error} title="The form could not be loaded" />;
  // Each load of the form is its own editor: reloading it starts again from the new commit.
  return <Editor key={form.baseCommit ?? "none"} form={form} mode={mode} reload={() => void refetch()} reloading={isFetching} />;
}

function Editor({ form, mode, reload, reloading }: { form: ChangeForm; mode: Mode; reload: () => void; reloading: boolean }) {
  const navigate = useNavigate();
  const preview = usePreviewChange();
  const commit = useCommitChange();
  const target = mode === "upgrade" ? upgradeReference(form) : null;
  const version = mode === "upgrade" && form.upgrade ? form.upgrade : form.resolved;
  const schema = useMemo(() => (isObject(version?.inputs) ? (version.inputs as Schema) : fallbackSchema(form)), [version, form]);
  const ui = useMemo(() => readUi(version?.ui), [version]);
  const [value, setValue] = useState<JsonObject>(() => initialValue(form));
  const [workflow, setWorkflow] = useState(target ?? form.workflow ?? "");
  const [issues, setIssues] = useState<FieldIssue[]>(mode === "edit" ? form.fieldIssues : []);
  const [reviewed, setReviewed] = useState<ChangePreview | null>(null);
  const [yamlErrors, setYamlErrors] = useState<string[]>([]);
  const [previewIssues, setPreviewIssues] = useState<ChangePreview | null>(null);
  const [notice, setNotice] = useState<{ kind: "warning" | "danger" | "info"; title: string; text: string; stale?: boolean } | null>(null);
  const blocked = form.blockedReason;
  const disabled = Boolean(blocked) || preview.isPending || commit.isPending;
  const kindLabel = form.resourceKind === "service" ? "service" : "guest";
  const back = `/${form.resourceKind === "service" ? "services" : "guests"}/${form.resourceId}`;

  const request = useCallback((): ChangeRequest => {
    if (mode === "remove") return { resourceId: form.resourceId, baseCommit: form.baseCommit!, kind: "remove" };
    const original = form as unknown as Record<string, unknown>;
    const sections: Record<string, unknown> = {};
    for (const section of sectionsOf(form)) {
      // A section left out stays as it is; one cleared from the form is emptied.
      sections[section] = value[section] !== undefined ? value[section] : isObject(original[section]) ? {} : null;
    }
    const reference = workflow.trim();
    return {
      resourceId: form.resourceId,
      baseCommit: form.baseCommit!,
      kind: mode === "upgrade" ? "upgrade" : "edit",
      workflow: reference !== (form.workflow ?? "") ? reference : null,
      ...sections,
    };
  }, [form, mode, value, workflow]);

  const runPreview = useCallback(
    (automatic = false) => {
      setNotice(null);
      preview.mutate(request(), {
        onSuccess: (result) => {
          setIssues(result.fieldIssues);
          if (result.refused) {
            setNotice({ kind: "warning", title: "The form is out of date", text: result.refused, stale: true });
            return;
          }
          if (result.valid) {
            setReviewed(result);
            return;
          }
          setNotice(
            automatic && mode === "upgrade"
              ? { kind: "info", title: `${target} needs changes to the inputs`, text: "Fix them here: they are committed together with the upgrade, in one change." }
              : { kind: "danger", title: "The change does not validate", text: "Fix the fields marked below, then review it again." },
          );
          setPreviewIssues(result);
        },
        onError: (e) => {
          const refusal = changeRefusal(e);
          setNotice({ kind: refusal?.reason === "stale" ? "warning" : "danger", title: "The change cannot be made", text: errorMessage(e), stale: refusal?.reason === "stale" });
          if (refusal) setIssues(refusal.fieldIssues);
        },
      });
    },
    // `preview.mutate` is stable; the rest is what the request is built from.
    // eslint-disable-next-line react-hooks/exhaustive-deps
    [request, mode, target],
  );
  // An upgrade first tries the current values against the new major; a removal has nothing to fill in.
  const started = useRef(false);
  useEffect(() => {
    if (started.current || blocked || mode === "edit" || !form.baseCommit) return;
    started.current = true;
    runPreview(true);
  }, [blocked, mode, form.baseCommit, runPreview]);

  const commitChange = () => {
    setNotice(null);
    commit.mutate(request(), {
      onSuccess: (change) => navigate(`/changes/${change.id}`),
      onError: (e) => {
        const refusal = changeRefusal(e);
        // A push was attempted and is not settled: its change page follows it.
        if (refusal?.changeId && (refusal.status === "uncertain" || refusal.status === "unknown")) {
          navigate(`/changes/${refusal.changeId}`);
          return;
        }
        if (refusal?.reason === "invalid") {
          setIssues(refusal.fieldIssues);
          setReviewed(null);
        }
        setNotice({
          kind: refusal?.reason === "stale" ? "warning" : "danger",
          title: refusal?.reason === "stale" ? "The form is out of date" : "Nothing was committed",
          text: errorMessage(e),
          stale: refusal?.reason === "stale",
        });
      },
    });
  };

  const title =
    mode === "remove" ? `Remove ${form.name}` : mode === "upgrade" && target ? `Upgrade ${form.name} to ${target}` : `Edit ${form.name}`;
  return (
    <>
      <PageHeader
        eyebrow={
          <>
            <Link to="/inventory">Inventory</Link> / <Link to={back}>{form.address}</Link> / {mode === "remove" ? "Remove" : mode === "upgrade" ? "Upgrade" : "Edit"}
          </>
        }
        title={title}
        meta={
          form.file ? (
            <span className="inline-group">
              <span className="mono small">{form.file}</span>
              <span className="muted small">
                loaded at <Commit commit={form.baseCommit} />
              </span>
            </span>
          ) : undefined
        }
        actions={
          <Link to={back} className="button">
            <ArrowLeft size={15} aria-hidden="true" />
            Back to the {kindLabel}
          </Link>
        }
      />
      <div className="stack stack--large">
        {blocked ? (
          <Callout kind="warning" title="This form is read-only">
            {blocked}
          </Callout>
        ) : null}
        {mode === "upgrade" && !target ? (
          <Callout kind="info" title="No newer major">There is no newer major release of this workflow to upgrade to.</Callout>
        ) : null}
        {notice ? (
          <Callout kind={notice.kind} title={notice.title} alert
            actions={notice.stale ? (
              <button type="button" className="button" onClick={reload} disabled={reloading}>
                <RotateCcw size={15} aria-hidden="true" />
                Reload the form
              </button>
            ) : undefined}>
            {notice.text}
          </Callout>
        ) : null}
        {reviewed ? (
          <Review preview={reviewed} remove={mode === "remove"} pending={commit.isPending} onBack={mode === "remove" ? null : () => setReviewed(null)}
            onCommit={commitChange} />
        ) : mode === "remove" ? (
          preview.isPending ? <PageSkeleton rows={2} /> : <ErrorMessage error={preview.error} />
        ) : (
          <Card title="Definition">
            <div className="stack stack--large">
              <div className="field">
                <label htmlFor="change-workflow">Workflow</label>
                <input id="change-workflow" className="mono" value={workflow} disabled={disabled || mode === "upgrade"}
                  onChange={(e) => setWorkflow(e.target.value)} aria-invalid={issues.some((i) => i.pointer === "/workflow")} />
                <span className="field__hint">
                  {version ? `Resolved to ${version.id}@${version.version}.` : "Not resolved: each section is edited as YAML."}{" "}
                  {mode === "edit" ? "A workflow named here is validated when the change is reviewed." : null}
                </span>
                {issues.filter((i) => i.pointer === "/workflow").map((i, n) => (
                  <span key={n} className="field__error" role="alert">{i.message}</span>
                ))}
              </div>
              <SchemaForm schema={schema} ui={ui} value={value} onChange={setValue} disabled={disabled}
                issues={issues.filter((i) => i.pointer !== "/workflow")} onYamlErrors={setYamlErrors} />
              {previewIssues && !previewIssues.valid && previewIssues.issues.length > 0 ? (
                <div className="stack stack--small">
                  <h3 className="schema-form__group-title">What the validation found</h3>
                  <IssueList issues={previewIssues.issues} />
                </div>
              ) : null}
              <div className="button-row">
                <button type="button" className="button button--primary" onClick={() => { setPreviewIssues(null); runPreview(); }}
                  disabled={disabled || yamlErrors.length > 0 || !form.baseCommit}>
                  <Search size={15} aria-hidden="true" />
                  {preview.isPending ? "Checking…" : "Review the change"}
                </button>
                {yamlErrors.length > 0 ? <span className="field__error">Fix the YAML first.</span> : null}
              </div>
            </div>
          </Card>
        )}
      </div>
    </>
  );
}

function Review({ preview, remove, pending, onBack, onCommit }: { preview: ChangePreview; remove: boolean; pending: boolean;
  onBack: (() => void) | null; onCommit: () => void }) {
  return (
    <Card title="Review" meta={`${preview.files.length} file${preview.files.length === 1 ? "" : "s"}`}>
      <div className="stack stack--large">
        <p>
          {remove
            ? "Committing deletes the guest's folder. Octopushy then proposes its retire run, under the guest's approval and destructive policy."
            : "Committing writes this commit to the lab repository as you, syncs it at once, and the normal process runs: plan, approval as the policy says, apply."}
        </p>
        <CodeBlock code={preview.message} label="Commit message" copy={false} wrap />
        <DiffView diff={preview.diff} label="The exact diff that will be committed" />
        {preview.issues.length > 0 ? (
          <div className="stack stack--small">
            <h3 className="schema-form__group-title">Warnings</h3>
            <IssueList issues={preview.issues} />
          </div>
        ) : null}
        <div className="button-row">
          {onBack ? (
            <button type="button" className="button" onClick={onBack} disabled={pending}>
              <ArrowLeft size={15} aria-hidden="true" />
              Back to the form
            </button>
          ) : null}
          <button type="button" className={remove ? "button button--danger" : "button button--primary"} onClick={onCommit} disabled={pending}>
            {remove ? <Trash2 size={15} aria-hidden="true" /> : <GitCommitHorizontal size={15} aria-hidden="true" />}
            {pending ? "Committing…" : remove ? "Commit the removal" : "Commit change"}
          </button>
        </div>
      </div>
    </Card>
  );
}
