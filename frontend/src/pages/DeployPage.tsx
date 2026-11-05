import { ArrowLeft, ArrowRight, Rocket, RotateCcw, Search, Wand2 } from "lucide-react";
import { useMemo, useState } from "react";
import { Link, useNavigate } from "react-router";
import { changeRefusal, errorMessage } from "../api/client";
import { useDeploy, useDeployTargets, useNextVmid, useObserveInstallation, usePreviewDeploy, useVariables, useWorkflows } from "../api/queries";
import type {
  ChangePreview,
  DeployInstallation,
  DeployRequest,
  DeployTargets,
  SecretChoice,
  WorkflowSummary,
  WorkflowVersion,
} from "../api/types";
import { Badge } from "../components/Badge";
import { Callout } from "../components/Callout";
import { Card } from "../components/Card";
import { CodeBlock } from "../components/CodeBlock";
import { DiffView } from "../components/DiffView";
import { IssueList } from "../components/IssueList";
import { Commit } from "../components/Mono";
import { PageHeader } from "../components/PageHeader";
import { SchemaForm, type FieldIssue } from "../components/SchemaForm";
import { ErrorMessage, PageSkeleton } from "../components/States";
import {
  defaultsOf,
  generators,
  majorOf,
  namePattern,
  newestPerMajor,
  readDeployHints,
  referenceOf,
  requiredByForm,
  valueAt,
  type DeployHints,
} from "../lib/deploy";
import { isObject, readUi, type JsonObject, type Schema } from "../lib/jsonSchema";

const steps = [
  { id: "workflow", label: "Workflow" },
  { id: "where", label: "Where" },
  { id: "form", label: "Form" },
  { id: "secrets", label: "Secrets" },
  { id: "review", label: "Review" },
  { id: "deploy", label: "Deploy" },
] as const;

type Step = (typeof steps)[number]["id"];

const generatorLabels: Record<string, string> = {
  [generators.agentEnrollmentToken]: "a one-time agent enrollment token",
  [generators.randomPassword]: "a random password",
};

interface Chosen {
  workflow: WorkflowSummary;
  version: WorkflowVersion;
  pinned: boolean;
}

interface SecretState {
  mode: "store" | "generate" | "none";
  store: string;
  labels: string;
  agentName: string;
}

/** The sections a workflow's form edits: a guest's hardware, network and inputs, a service's inputs. */
function sectionsOf(version: WorkflowVersion): string[] {
  return version.manages === "service" ? ["inputs"] : ["hardware", "network", "inputs"];
}

function schemaOf(version: WorkflowVersion): Schema {
  if (isObject(version.inputs)) return version.inputs as Schema;
  // Without an inputs schema, each section is edited as YAML.
  return { type: "object", properties: Object.fromEntries(sectionsOf(version).map((s) => [s, {}])) };
}

/** The form's starting value: the schema's defaults, in the sections the workflow's form edits. */
function startingValue(version: WorkflowVersion): JsonObject {
  const defaults = defaultsOf(schemaOf(version));
  const value: JsonObject = {};
  for (const section of sectionsOf(version)) {
    const part = isObject(defaults) ? defaults[section] : undefined;
    if (isObject(part)) value[section] = part;
  }
  return value;
}

/** Every secret the deploy has to settle: those the workflow requires, then those its `ui.secrets` describe. */
function secretNames(version: WorkflowVersion, hints: DeployHints): string[] {
  return [...version.secrets, ...Object.keys(hints.secrets).filter((n) => !version.secrets.includes(n))];
}

function labelsOf(text: string): string[] {
  return text
    .split(/[\s,]+/)
    .map((l) => l.trim())
    .filter((l) => l.length > 0);
}

/**
 * The workflow wizard: pick a workflow, then where it goes, fill in its form, settle its secrets, review the exact file, and
 * deploy. A deploy is a change like any other: one commit to the lab repository, synced at once, then the normal plan,
 * approval and apply, followed on the change's page. See docs/changes-from-the-ui.md.
 */
export function DeployPage() {
  const targets = useDeployTargets();
  const workflows = useWorkflows();
  if (targets.isPending || workflows.isPending) return <PageSkeleton rows={4} />;
  if (!targets.data) return <ErrorMessage error={targets.error} title="Where to deploy could not be loaded" />;
  if (!workflows.data) return <ErrorMessage error={workflows.error} title="The workflows could not be loaded" />;
  return (
    <Wizard
      key={targets.data.baseCommit ?? "none"}
      targets={targets.data}
      workflows={workflows.data.workflows}
      reload={() => void targets.refetch()}
      reloading={targets.isFetching}
    />
  );
}

function Wizard({ targets, workflows, reload, reloading }: { targets: DeployTargets; workflows: WorkflowSummary[]; reload: () => void; reloading: boolean }) {
  const navigate = useNavigate();
  const nextVmid = useNextVmid();
  const preview = usePreviewDeploy();
  const deploy = useDeploy();
  const variables = useVariables();
  const observe = useObserveInstallation();

  const [step, setStep] = useState<Step>("workflow");
  const [chosen, setChosen] = useState<Chosen | null>(null);
  const [installation, setInstallation] = useState("");
  const [host, setHost] = useState("");
  const [guest, setGuest] = useState("");
  const [name, setName] = useState("");
  const [vmid, setVmid] = useState("");
  const [vmidProblem, setVmidProblem] = useState<string | null>(null);
  const [template, setTemplate] = useState("");
  const [value, setValue] = useState<JsonObject>({});
  const [yamlErrors, setYamlErrors] = useState<string[]>([]);
  const [secrets, setSecrets] = useState<Record<string, SecretState>>({});
  const [accessUser, setAccessUser] = useState("");
  const [hostKey, setHostKey] = useState("");
  const [approve, setApprove] = useState(false);
  const [issues, setIssues] = useState<FieldIssue[]>([]);
  const [reviewed, setReviewed] = useState<ChangePreview | null>(null);
  const [notice, setNotice] = useState<{
    kind: "warning" | "danger";
    title: string;
    text: string;
    nextVmid?: number | null;
    stale?: boolean;
    observe?: boolean;
  } | null>(null);

  const version = chosen?.version ?? null;
  const isService = version?.manages === "service";
  const hints = useMemo(() => readDeployHints(version?.ui), [version]);
  const ui = useMemo(() => readUi(version?.ui), [version]);
  const schema = useMemo(() => (version ? schemaOf(version) : {}), [version]);
  const target = targets.installations.find((i) => i.address === installation) ?? null;
  const address = isService ? `${guest}/${name}` : `${installation}/${name}`;
  const blocked = targets.blockedReason;
  const busy = preview.isPending || deploy.isPending;

  const issueAt = (pointer: string) => issues.filter((i) => i.pointer === pointer || i.pointer.startsWith(`${pointer}/`));

  const chooseWorkflow = (workflow: WorkflowSummary, picked: WorkflowVersion, pinned: boolean) => {
    const changed = chosen?.version.id !== picked.id || chosen.version.version !== picked.version;
    setChosen({ workflow, version: picked, pinned });
    if (changed) {
      // A new workflow is a new form: its defaults, its secrets, its hints.
      setValue(startingValue(picked));
      setSecrets({});
      setTemplate(readDeployHints(picked.ui).template ?? "");
      setAccessUser(readDeployHints(picked.ui).accessUser ?? "");
      setReviewed(null);
      setIssues([]);
    }
  };

  const proposeVmid = (address: string) => {
    if (!address) return;
    nextVmid.mutate(
      { installation: address, from: hints.vmid?.from ?? null },
      {
        onSuccess: (next) => {
          // Without a recent, complete observation, no number is offered: the problem says what to do.
          setVmidProblem(next.problem ?? null);
          setVmid(next.vmid === null ? "" : String(next.vmid));
        },
      },
    );
  };

  const chooseInstallation = (choice: DeployInstallation) => {
    setInstallation(choice.address);
    const declared = choice.hosts.filter((h) => h.declared);
    setHost(declared.length === 1 ? declared[0]!.name : "");
    setGuest("");
    setVmidProblem(choice.vmidProblem ?? null);
    if (!isService && hints.vmid?.generate === generators.nextVmid) proposeVmid(choice.address);
  };

  /** The secrets' starting state: generated where the workflow asks for it, labels and name from the form or the lab. */
  const enterSecrets = () => {
    if (!version) return;
    const next: Record<string, SecretState> = {};
    for (const secret of secretNames(version, hints)) {
      if (secrets[secret]) {
        next[secret] = secrets[secret]!;
        continue;
      }
      const hint = hints.secrets[secret] ?? {};
      const labels = valueAt(value, hint.labels);
      const agentName = valueAt(value, hint.name);
      next[secret] = {
        mode: hint.generate ? "generate" : version.secrets.includes(secret) || requiredByForm(hint, value) ? "store" : "none",
        store: "",
        labels: (Array.isArray(labels) ? labels.filter((l): l is string => typeof l === "string") : (target?.agentLabels ?? [])).join(", "),
        agentName: typeof agentName === "string" && agentName ? agentName : name,
      };
    }
    setSecrets(next);
    // A workflow that installs Octopushy's own host key needs none, where the installation is set up for snippet uploads.
    if (version.setup && !hostKey && !version.managedHostKey) setHostKey(`guests/${address}/host-key`);
    setStep("secrets");
  };

  const request = (overrides: Partial<DeployRequest> = {}): DeployRequest => {
    const choices: Record<string, SecretChoice> = {};
    for (const [secret, state] of Object.entries(secrets)) {
      if (state.mode === "store") choices[secret] = { store: state.store.trim() };
      else if (state.mode === "generate") {
        const generate = hints.secrets[secret]?.generate ?? "";
        choices[secret] =
          generate === generators.agentEnrollmentToken ? { generate, labels: labelsOf(state.labels), name: state.agentName.trim() || null } : { generate };
      }
    }
    const section = (key: string) => (isObject(value[key]) ? (value[key] as Record<string, unknown>) : null);
    return {
      kind: isService ? "create-service" : "create-guest",
      baseCommit: targets.baseCommit!,
      parent: isService ? guest : installation,
      name: name.trim(),
      workflow: referenceOf(version!, chosen!.pinned),
      ...(isService
        ? { inputs: section("inputs") }
        : {
            host,
            vmid: Number.parseInt(vmid, 10),
            template: template || null,
            hardware: section("hardware"),
            network: section("network"),
            inputs: section("inputs"),
            accessUser: version!.setup ? accessUser.trim() || null : null,
            hostKey: version!.setup ? hostKey.trim() || null : null,
          }),
      secrets: Object.keys(choices).length > 0 ? choices : null,
      approveAtDeploy: approve,
      reviewedRelease: reviewed?.reviewedRelease ?? null,
      reviewDigest: reviewed?.reviewDigest ?? null,
      ...overrides,
    };
  };

  const review = (overrides: Partial<DeployRequest> = {}) => {
    setNotice(null);
    setReviewed(null);
    setStep("review");
    preview.mutate(request(overrides), {
      onSuccess: (result) => {
        setIssues(result.fieldIssues);
        if (result.refused) {
          setNotice({ kind: "warning", title: "This deploy would be refused now", text: result.refused, nextVmid: result.nextVmid });
          return;
        }
        if (!result.valid) {
          setNotice({ kind: "danger", title: "The deploy does not validate", text: "Fix what is marked, then review it again." });
        }
        setReviewed(result);
      },
      onError: (e) => {
        const refusal = changeRefusal(e);
        if (refusal) setIssues(refusal.fieldIssues);
        setNotice({ kind: refusal?.reason === "stale" ? "warning" : "danger", title: "This cannot be deployed", text: errorMessage(e),
          nextVmid: refusal?.nextVmid, stale: refusal?.reason === "stale", observe: refusal?.reason === "vmids-unknown" });
      },
    });
  };

  const applyVmid = (next: number) => {
    setVmid(String(next));
    review({ vmid: next });
  };

  const commit = () => {
    setNotice(null);
    deploy.mutate(request(), {
      onSuccess: (change) => navigate(`/changes/${change.id}`),
      onError: (e) => {
        const refusal = changeRefusal(e);
        if (refusal) setIssues(refusal.fieldIssues);
        setNotice({
          kind: refusal?.reason === "stale" || refusal?.reason === "vmid-taken" ? "warning" : "danger",
          title: "Nothing was deployed",
          text: errorMessage(e),
          nextVmid: refusal?.nextVmid,
          stale: refusal?.reason === "stale",
          observe: refusal?.reason === "vmids-unknown",
        });
      },
    });
  };

  const observeNow = () => {
    if (!target?.id) return;
    observe.mutate(target.id);
  };

  /** Whether a secret must be given: the workflow requires it, or the form chose what needs it. */
  const isRequired = (secret: string) => Boolean(version?.secrets.includes(secret)) || requiredByForm(hints.secrets[secret], value);

  const whereReady =
    namePattern.test(name.trim()) &&
    (isService ? Boolean(guest) : Boolean(target?.managed) && Boolean(host) && Number.parseInt(vmid, 10) >= 100);
  const secretsReady =
    version !== null &&
    Object.entries(secrets).every(([secret, state]) => (state.mode === "store" ? state.store.trim().length > 0 : state.mode === "generate" || !isRequired(secret)));
  const reached = steps.findIndex((s) => s.id === step);

  return (
    <>
      <PageHeader
        title="Deploy"
        meta={
          <span className="muted small">
            A new guest or service from a workflow, committed to the lab repository; loaded at <Commit commit={targets.baseCommit} />
          </span>
        }
      />
      <div className="stack stack--large">
        {blocked ? (
          <Callout kind="warning" title="Nothing can be deployed now">
            {blocked}
          </Callout>
        ) : null}
        <ol className="wizard-steps" aria-label="Steps">
          {steps.map((s, i) => (
            <li key={s.id} className={`wizard-steps__step wizard-steps__step--${i < reached ? "done" : i === reached ? "current" : "todo"}`}
              aria-current={i === reached ? "step" : undefined}>
              {i < reached && !busy ? (
                <button type="button" className="link-button" onClick={() => setStep(s.id)}>
                  {i + 1}. {s.label}
                </button>
              ) : (
                <span>
                  {i + 1}. {s.label}
                </span>
              )}
            </li>
          ))}
        </ol>
        {notice ? (
          <Callout
            kind={notice.kind}
            title={notice.title}
            alert
            actions={
              notice.nextVmid ? (
                <button type="button" className="button" onClick={() => applyVmid(notice.nextVmid!)} disabled={busy}>
                  <Wand2 size={15} aria-hidden="true" />
                  Use VMID {notice.nextVmid} and review again
                </button>
              ) : notice.observe && target?.id ? (
                <button type="button" className="button" onClick={observeNow} disabled={observe.isPending}>
                  <RotateCcw size={15} aria-hidden="true" />
                  {observe.isPending ? "Asking…" : `Observe ${installation} now`}
                </button>
              ) : notice.stale ? (
                <button type="button" className="button" onClick={reload} disabled={reloading}>
                  <RotateCcw size={15} aria-hidden="true" />
                  Start again from the head
                </button>
              ) : undefined
            }
          >
            {notice.text}
          </Callout>
        ) : null}

        {step === "workflow" ? (
          <Card title="Choose a workflow" meta={`${workflows.length} available`}>
            {workflows.length === 0 ? (
              <p className="muted">No workflow is released yet. See Repository for the workflows source.</p>
            ) : (
              <div className="workflow-cards">
                {workflows.map((workflow) => (
                  <WorkflowCard key={workflow.id} workflow={workflow} chosen={chosen?.workflow.id === workflow.id ? chosen : null}
                    onChoose={(picked, pinned) => chooseWorkflow(workflow, picked, pinned)} />
                ))}
              </div>
            )}
            <div className="button-row">
              <button type="button" className="button button--primary" disabled={!chosen} onClick={() => setStep("where")}>
                Next: where
                <ArrowRight size={15} aria-hidden="true" />
              </button>
            </div>
          </Card>
        ) : null}

        {step === "where" && version ? (
          <Card title={isService ? "Which guest it runs on" : "Where it runs"} meta={referenceOf(version, chosen!.pinned)}>
            <div className="stack stack--large">
              <fieldset className="field">
                <legend>Installation</legend>
                <div className="radio-row">
                  {targets.installations.map((choice) => (
                    <label key={choice.address} className={choice.managed ? "radio" : "radio radio--disabled"}>
                      <input type="radio" name="installation" value={choice.address} checked={installation === choice.address}
                        disabled={!choice.managed || Boolean(blocked)} onChange={() => chooseInstallation(choice)} />
                      <span className="stack stack--tiny">
                        <span>
                          <span className="mono">{choice.address}</span> <span className="muted small">{choice.name} · {choice.type}</span>
                          {!choice.managed ? <> <Badge tone="muted">observed only</Badge></> : null}
                        </span>
                        {choice.reason ? <span className="field__hint">{choice.reason}</span> : null}
                      </span>
                    </label>
                  ))}
                </div>
                {targets.installations.length === 0 ? <span className="field__hint">The repository defines no installation.</span> : null}
              </fieldset>

              {target && !isService ? (
                <div className="form__row">
                  <div className="field">
                    <label htmlFor="deploy-host">Host</label>
                    <select id="deploy-host" value={host} onChange={(e) => setHost(e.target.value)} aria-invalid={issueAt("/host").length > 0}>
                      <option value="">Choose a host</option>
                      {target.hosts.map((h) => (
                        <option key={h.name} value={h.name} disabled={!h.declared}>
                          {h.name}
                          {h.observed ? (h.status ? ` (${h.status})` : " (observed)") : " (not observed yet)"}
                          {h.declared ? "" : " (not declared in installation.yaml)"}
                        </option>
                      ))}
                    </select>
                    <span className="field__hint">
                      {target.observedAt ? "Hosts as declared and as last observed." : "Not observed yet: hosts as declared."}
                    </span>
                    <FieldErrors issues={issueAt("/host")} />
                  </div>
                  <div className="field">
                    <label htmlFor="deploy-vmid">VMID</label>
                    <div className="inline-form">
                      <input id="deploy-vmid" className="input--narrow mono" inputMode="numeric" value={vmid} onChange={(e) => setVmid(e.target.value.replace(/\D/g, ""))}
                        aria-invalid={issueAt("/vmid").length > 0} />
                      <button type="button" className="button" onClick={() => proposeVmid(installation)} disabled={nextVmid.isPending}>
                        <Wand2 size={15} aria-hidden="true" />
                        {nextVmid.isPending ? "Finding…" : "Next free"}
                      </button>
                    </div>
                    <span className="field__hint">
                      {hints.vmid?.generate === generators.nextVmid ? "The next free VMID, proposed by the workflow. " : ""}
                      Free means not observed anywhere on {installation}, not declared, and not held by another deploy. It is checked again when deployed.
                    </span>
                    {vmidProblem ? (
                      <span className="field__error" role="alert">
                        {vmidProblem}{" "}
                        {target.id ? (
                          <button type="button" className="link-button" onClick={observeNow} disabled={observe.isPending}>
                            {observe.isSuccess ? "Observation asked for; try Next free in a moment" : observe.isPending ? "Asking…" : "Observe now"}
                          </button>
                        ) : null}
                      </span>
                    ) : null}
                    <FieldErrors issues={issueAt("/vmid")} />
                  </div>
                  {targets.templates.length > 0 ? (
                    <div className="field">
                      <label htmlFor="deploy-template">Template</label>
                      <select id="deploy-template" value={template} onChange={(e) => setTemplate(e.target.value)}>
                        <option value="">None</option>
                        {targets.templates.map((t) => (
                          <option key={t} value={t}>
                            {t}
                          </option>
                        ))}
                      </select>
                      <FieldErrors issues={issueAt("/template")} />
                    </div>
                  ) : null}
                </div>
              ) : null}

              {target && isService ? (
                <div className="field">
                  <label htmlFor="deploy-guest">Guest</label>
                  <select id="deploy-guest" value={guest} onChange={(e) => setGuest(e.target.value)}>
                    <option value="">Choose a guest</option>
                    {target.guests.map((g) => (
                      <option key={g.address} value={g.address}>
                        {g.name}
                        {g.workflow ? ` (${g.workflow})` : ""}
                      </option>
                    ))}
                  </select>
                </div>
              ) : null}

              <div className="field">
                <label htmlFor="deploy-name">Name</label>
                <input id="deploy-name" className="mono" value={name} onChange={(e) => setName(e.target.value)} aria-invalid={issueAt("/name").length > 0}
                  aria-describedby="deploy-name-hint" />
                <span className="field__hint" id="deploy-name-hint">
                  Its folder and its id: lowercase letters, digits and hyphens.{" "}
                  {name && (target || guest) ? <span className="mono">{address}</span> : null}
                </span>
                {name && !namePattern.test(name.trim()) ? <span className="field__error">Lowercase letters, digits and hyphens, starting and ending with a letter or digit.</span> : null}
                <FieldErrors issues={issueAt("/name")} />
              </div>
              <div className="button-row">
                <button type="button" className="button" onClick={() => setStep("workflow")}>
                  <ArrowLeft size={15} aria-hidden="true" />
                  Back
                </button>
                <button type="button" className="button button--primary" disabled={!whereReady} onClick={() => setStep("form")}>
                  Next: form
                  <ArrowRight size={15} aria-hidden="true" />
                </button>
              </div>
            </div>
          </Card>
        ) : null}

        {step === "form" && version ? (
          <Card title="Form" meta={`${version.id}@${version.version}`}>
            <div className="stack stack--large">
              {!isObject(version.inputs) ? (
                <Callout kind="info" title="No inputs schema">This workflow has no inputs schema, so each section is edited as YAML.</Callout>
              ) : null}
              <SchemaForm schema={schema} ui={ui} value={value} onChange={setValue} issues={issues.filter((i) => /^\/(hardware|network|inputs)(\/|$)/.test(i.pointer))}
                onYamlErrors={setYamlErrors} />
              <div className="button-row">
                <button type="button" className="button" onClick={() => setStep("where")}>
                  <ArrowLeft size={15} aria-hidden="true" />
                  Back
                </button>
                <button type="button" className="button button--primary" disabled={yamlErrors.length > 0} onClick={enterSecrets}>
                  Next: secrets
                  <ArrowRight size={15} aria-hidden="true" />
                </button>
                {yamlErrors.length > 0 ? <span className="field__error">Fix the YAML first.</span> : null}
              </div>
            </div>
          </Card>
        ) : null}

        {step === "secrets" && version ? (
          <Card title="Secrets" meta={Object.keys(secrets).length === 0 ? "none" : `${Object.keys(secrets).length}`}>
            <div className="stack stack--large">
              <p className="muted">
                Values live in the variable store, never in Git. The definition names the store key. A generated value is made when you
                deploy, is never shown, and is deleted again if nothing is committed.
              </p>
              {Object.keys(secrets).length === 0 ? <p>This workflow takes no secrets.</p> : null}
              {Object.entries(secrets).map(([secret, state]) => (
                <SecretField key={secret} secret={secret} state={state} required={isRequired(secret)} hint={hints.secrets[secret] ?? {}}
                  keys={(variables.data?.entries ?? []).map((v) => v.key)} storeKey={`${isService ? "services" : "guests"}/${address}/${secret}`}
                  issues={issueAt(`/secrets/${secret}`)} onChange={(next) => setSecrets((current) => ({ ...current, [secret]: next }))} />
              ))}
              {version.setup ? (
                <fieldset className="field">
                  <legend>Access for the setup stage</legend>
                  <div className="form__row">
                    <div className="field">
                      <label htmlFor="deploy-access-user">Login</label>
                      <input id="deploy-access-user" className="mono" value={accessUser} onChange={(e) => setAccessUser(e.target.value)} />
                      <span className="field__hint">The user the setup stage logs in as. Octopushy makes and installs its own key for this guest.</span>
                    </div>
                    <div className="field">
                      <label htmlFor="deploy-host-key">Host key, as a store key</label>
                      <input id="deploy-host-key" className="mono" value={hostKey} onChange={(e) => setHostKey(e.target.value)} aria-invalid={issueAt("/hostKey").length > 0} />
                      <span className="field__hint">
                        {version.managedHostKey
                          ? "Leave it empty: this workflow gives the guest a host key of Octopushy's, delivered when it is created and pinned on its own. That needs the installation to list the guest's host under snippets.hosts; validation says if it does not."
                          : "A plain entry you set from the guest's console after its first boot. Setup waits for it: Octopushy never scans a host."}
                      </span>
                      <FieldErrors issues={issueAt("/hostKey")} />
                    </div>
                  </div>
                </fieldset>
              ) : null}
              <div className="button-row">
                <button type="button" className="button" onClick={() => setStep("form")}>
                  <ArrowLeft size={15} aria-hidden="true" />
                  Back
                </button>
                <button type="button" className="button button--primary" disabled={!secretsReady || Boolean(blocked) || busy} onClick={() => review()}>
                  <Search size={15} aria-hidden="true" />
                  Review the deploy
                </button>
              </div>
            </div>
          </Card>
        ) : null}

        {step === "review" ? (
          preview.isPending ? (
            <PageSkeleton rows={3} />
          ) : reviewed ? (
            <Card title="Review" meta={`${reviewed.files.length} file${reviewed.files.length === 1 ? "" : "s"}`}>
              <div className="stack stack--large">
                <p>Deploying writes this commit to the lab repository as you, syncs it at once, and the normal process runs: plan, approval, apply.</p>
                <CodeBlock code={reviewed.message} label="Commit message" copy={false} wrap />
                {reviewed.generated && reviewed.generated.length > 0 ? (
                  <div className="stack stack--small">
                    <h3 className="schema-form__group-title">Generated when you deploy</h3>
                    <ul className="plain-list">
                      {reviewed.generated.map((g) => (
                        <li key={g.key}>
                          <span className="mono">{g.secret}</span>: {g.detail}, stored as <span className="mono">{g.key}</span>. Never shown.
                        </li>
                      ))}
                    </ul>
                  </div>
                ) : null}
                <DiffView diff={reviewed.diff} label="The exact files that will be committed" />
                {reviewed.issues.length > 0 ? (
                  <div className="stack stack--small">
                    <h3 className="schema-form__group-title">{reviewed.valid ? "Warnings" : "What the validation found"}</h3>
                    <IssueList issues={reviewed.issues} />
                  </div>
                ) : null}
                <div className="button-row">
                  <button type="button" className="button" onClick={() => setStep(reviewed.valid ? "secrets" : "form")}>
                    <ArrowLeft size={15} aria-hidden="true" />
                    {reviewed.valid ? "Back" : "Back to the form"}
                  </button>
                  <button type="button" className="button button--primary" disabled={!reviewed.valid || Boolean(reviewed.refused)} onClick={() => setStep("deploy")}>
                    Next: deploy
                    <ArrowRight size={15} aria-hidden="true" />
                  </button>
                </div>
              </div>
            </Card>
          ) : (
            <div className="button-row">
              <button type="button" className="button" onClick={() => setStep("secrets")}>
                <ArrowLeft size={15} aria-hidden="true" />
                Back
              </button>
            </div>
          )
        ) : null}

        {step === "deploy" && reviewed && version ? (
          <Card title="Deploy" meta={address}>
            <div className="stack stack--large">
              <p>
                <span className="mono">{referenceOf(version, chosen!.pinned)}</span> as <span className="mono">{address}</span>
                {isService ? null : (
                  <>
                    {" "}
                    on host <span className="mono">{host}</span>, VMID <span className="mono">{vmid}</span>
                  </>
                )}
                .
              </p>
              <label className="checkbox">
                <input type="checkbox" checked={approve} onChange={(e) => setApprove(e.target.checked)} />
                <span className="stack stack--tiny">
                  <span>Approve the plan in the same click</span>
                  <span className="field__hint">
                    Approves the first plan of this deploy as you, only if it does nothing but create, is of this commit, and every check an
                    approval meets holds. Otherwise it waits for your approval and says why. It never covers a later plan, a retire or a
                    deletion.
                  </span>
                </span>
              </label>
              <div className="button-row">
                <button type="button" className="button" onClick={() => setStep("review")} disabled={deploy.isPending}>
                  <ArrowLeft size={15} aria-hidden="true" />
                  Back
                </button>
                <button type="button" className="button button--primary" onClick={commit} disabled={deploy.isPending || Boolean(blocked)}>
                  <Rocket size={15} aria-hidden="true" />
                  {deploy.isPending ? "Deploying…" : approve ? "Deploy and approve" : "Deploy"}
                </button>
              </div>
            </div>
          </Card>
        ) : null}
        <p className="muted small">
          A deploy is a commit: what it writes can be changed later from the {isService ? "service's" : "guest's"} form, the <Link to="/editor">editor</Link> or Git.
        </p>
      </div>
    </>
  );
}

function FieldErrors({ issues }: { issues: FieldIssue[] }) {
  return (
    <>
      {issues.map((issue, i) => (
        <span key={i} className="field__error" role="alert">
          {issue.message}
        </span>
      ))}
    </>
  );
}

/** A workflow as a card: what it is, what it manages, the newest release of each major, and the major to use. */
function WorkflowCard({ workflow, chosen, onChoose }: { workflow: WorkflowSummary; chosen: Chosen | null;
  onChoose: (version: WorkflowVersion, pinned: boolean) => void }) {
  const majors = newestPerMajor(workflow);
  const selected = chosen !== null;
  const id = `workflow-${workflow.id}`;
  return (
    <div className={`workflow-card${selected ? " workflow-card--selected" : ""}${majors.length === 0 ? " workflow-card--disabled" : ""}`}>
      <label className="radio">
        <input type="radio" name="workflow" checked={selected} disabled={majors.length === 0} onChange={() => onChoose(majors[0]!, false)}
          aria-describedby={`${id}-description`} />
        <span className="stack stack--tiny">
          <span className="inline-group">
            <span className="mono workflow-card__title">{workflow.id}</span>
            <Badge tone="muted">{workflow.manages === "service" ? "service" : workflow.manages === "vm" ? "VM" : (workflow.manages ?? "?")}</Badge>
          </span>
          <span className="small" id={`${id}-description`}>
            {workflow.description ?? <span className="muted">No description.</span>}
          </span>
        </span>
      </label>
      {majors.length === 0 ? (
        <span className="field__hint">No usable release.</span>
      ) : (
        <div className="stack stack--tiny">
          <span className="field__hint">Newest per major: {majors.map((v) => v.version).join(", ")}</span>
          {selected ? (
            <div className="inline-form">
              <label htmlFor={`${id}-major`} className="small">
                Major
              </label>
              <select id={`${id}-major`} value={String(majorOf(chosen.version.version))}
                onChange={(e) => onChoose(majors.find((v) => String(majorOf(v.version)) === e.target.value)!, chosen.pinned)}>
                {majors.map((v) => (
                  <option key={v.version} value={String(majorOf(v.version))}>
                    @{majorOf(v.version)} (newest {v.version})
                  </option>
                ))}
              </select>
              <label className="checkbox small">
                <input type="checkbox" checked={chosen.pinned} onChange={(e) => onChoose(chosen.version, e.target.checked)} />
                <span>Pin @{chosen.version.version}</span>
              </label>
              <span className="mono small">{referenceOf(chosen.version, chosen.pinned)}</span>
            </div>
          ) : null}
        </div>
      )}
    </div>
  );
}

function SecretField({ secret, state, required, hint, keys, storeKey, issues, onChange }: {
  secret: string;
  state: SecretState;
  required: boolean;
  hint: { generate?: string; label?: string; help?: string };
  keys: string[];
  storeKey: string;
  issues: FieldIssue[];
  onChange: (next: SecretState) => void;
}) {
  const id = `secret-${secret}`;
  const listId = `${id}-keys`;
  return (
    <fieldset className="field">
      <legend>
        <span className="mono">{hint.label ?? secret}</span> {required ? <span className="muted small">required</span> : <span className="muted small">optional</span>}
      </legend>
      {hint.help ? <span className="field__hint">{hint.help}</span> : null}
      <div className="radio-row">
        {hint.generate ? (
          <label className="radio">
            <input type="radio" name={id} checked={state.mode === "generate"} onChange={() => onChange({ ...state, mode: "generate" })} />
            <span>
              Generate {generatorLabels[hint.generate] ?? hint.generate}, stored as <span className="mono">{storeKey}</span>
            </span>
          </label>
        ) : null}
        <label className="radio">
          <input type="radio" name={id} checked={state.mode === "store"} onChange={() => onChange({ ...state, mode: "store" })} />
          <span>Use a key already in the store</span>
        </label>
        {!required ? (
          <label className="radio">
            <input type="radio" name={id} checked={state.mode === "none"} onChange={() => onChange({ ...state, mode: "none" })} />
            <span>Leave it out</span>
          </label>
        ) : null}
      </div>
      {state.mode === "store" ? (
        <div className="field">
          <label htmlFor={`${id}-store`}>Store key</label>
          <input id={`${id}-store`} className="mono" list={listId} value={state.store} onChange={(e) => onChange({ ...state, store: e.target.value })}
            aria-invalid={issues.length > 0} />
          <datalist id={listId}>
            {keys.map((k) => (
              <option key={k} value={k} />
            ))}
          </datalist>
        </div>
      ) : null}
      {state.mode === "generate" && hint.generate === generators.agentEnrollmentToken ? (
        <div className="form__row">
          <div className="field">
            <label htmlFor={`${id}-name`}>Agent name</label>
            <input id={`${id}-name`} className="mono" value={state.agentName} onChange={(e) => onChange({ ...state, agentName: e.target.value })} />
          </div>
          <div className="field">
            <label htmlFor={`${id}-labels`}>Labels</label>
            <input id={`${id}-labels`} className="mono" value={state.labels} onChange={(e) => onChange({ ...state, labels: e.target.value })} />
            <span className="field__hint">The agent gets exactly these: they say which work it may take. Comma-separated.</span>
          </div>
        </div>
      ) : null}
      <FieldErrors issues={issues} />
    </fieldset>
  );
}
