import { useState, type FormEvent } from "react";
import { Link, useSearchParams } from "react-router";
import { usePrepareWorkerDraft, useWorkerPreparation, useReviewWorkerDeployment, usePublishWorkerDeployment } from "../api/queries";
import type { WorkerPreparation, WorkerDeploymentRequest } from "../api/types";
import { Callout } from "../components/Callout";
import { Card } from "../components/Card";
import { DefinitionView } from "../components/DefinitionView";
import { PageHeader } from "../components/PageHeader";
import { ErrorMessage, PageSkeleton } from "../components/States";

function DraftForm({ data, initialInstallation }: { data: WorkerPreparation; initialInstallation: string | null }) {
  const [installationId, setInstallation] = useState(initialInstallation ?? data.installations[0]?.address ?? "");
  const [hostId, setHost] = useState("");
  const prepare = usePrepareWorkerDraft();
  const review = useReviewWorkerDeployment();
  const publish = usePublishWorkerDeployment();
  const [captured, setCaptured] = useState<WorkerDeploymentRequest | null>(null);
  const [confirmed, setConfirmed] = useState(false);
  const [mustReview, setMustReview] = useState(false);
  const installation = data.installations.find((i) => i.address === installationId);
  const host = installation?.hosts.find((h) => h.id === hostId) ?? installation?.hosts[0];
  const datastores = [...new Set(Object.values(host?.datastores ?? {}).filter((d) => d.trim()))];
  const bridges = host?.bridges.filter((b) => b.trim()) ?? [];
  const canPrepare = Boolean(data.revision?.isCurrentSource && data.revision.valid && installation && host && datastores.length && bridges.length);

  function submit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    if (!canPrepare || !data.revision || !installation || !host || !event.currentTarget.reportValidity()) return;
    const form = new FormData(event.currentTarget);
    const text = (key: string) => String(form.get(key) ?? "");
    const number = (key: string) => Number(text(key));
    const selection = {
      revisionId: data.revision.id,
      installation: installation.address,
      host: host.id,
      name: text("name"),
      vmid: number("vmid"),
      sockets: number("sockets"),
      cores: number("cores"),
      memoryMiB: number("memoryMiB"),
      bootDiskGiB: number("bootDiskGiB"),
      stateDiskGiB: number("stateDiskGiB"),
      datastore: text("datastore"),
      bridge: text("bridge"),
      vlan: text("vlan") === "" ? null : number("vlan"),
    };
    const action = (event.nativeEvent as SubmitEvent).submitter?.getAttribute("value");
    if (action === "deploy") {
      if (data.deploymentBlockedReason) return;
      const draft = { selection, stateBackend: text("stateBackend") };
      setCaptured(draft);
      setConfirmed(false);
      setMustReview(false);
      publish.reset();
      review.mutate(draft);
    } else {
      prepare.mutate(selection);
    }
  }

  return (
    <>
      <form className="form" onSubmit={submit} onChange={() => {
        prepare.reset(); review.reset(); publish.reset(); setCaptured(null); setConfirmed(false);
      }}>
        <fieldset className="field" disabled={prepare.isPending || review.isPending || publish.isPending}>
          <div className="form">
            <div className="form__row">
              <div className="field">
                <label htmlFor="worker-installation">Proxmox installation</label>
                <select id="worker-installation" value={installation?.address ?? ""} required onChange={(e) => {
                  setInstallation(e.target.value);
                  setHost("");
                }}>
                  {!installation ? <option value="">Select a configured installation</option> : null}
                  {data.installations.map((i) => <option key={i.address} value={i.address}>{i.name} · {i.address}</option>)}
                </select>
              </div>
              <div className="field">
                <label htmlFor="worker-host">Proxmox host</label>
                <select id="worker-host" value={host?.id ?? ""} required onChange={(e) => setHost(e.target.value)}>
                  {installation?.hosts.map((h) => <option key={h.id} value={h.id}>{h.id}</option>)}
                </select>
              </div>
            </div>
            <p className="muted">
              Configured API route: <code>{host?.endpoint ?? "None"}</code> · PVE TLS verification{" "}
              {installation?.verifyTls ? "enabled" : "disabled (configured homelab setting)"} ·{" "}
              {installation?.sharedToken ? "Shared Proxmox credential" : "Split Proxmox credentials"}.
              {" "}Credential availability and privileges are not checked. Worker mTLS is still required.
            </p>
            <div className="form__row">
              <div className="field">
                <label htmlFor="worker-name">Guest name</label>
                <input id="worker-name" name="name" required maxLength={63} pattern="[a-z0-9]([a-z0-9\-]{0,61}[a-z0-9])?" defaultValue="octopushy-worker-01" />
                <span className="field__hint">Lowercase letters, digits and hyphens; unique within the installation.</span>
              </div>
              <div className="field">
                <label htmlFor="worker-vmid">VMID</label>
                <input id="worker-vmid" name="vmid" type="number" required min={100} max={999999999} step={1} defaultValue={901} />
                <span className="field__hint">Checked against Git declarations only, not live or reserved VMIDs.</span>
              </div>
            </div>
            <div className="form__row">
              {[
                { name: "sockets", label: "CPU sockets", value: 1, min: 1, max: 16 },
                { name: "cores", label: "Cores per socket", value: 2, min: 1, max: 128 },
                { name: "memoryMiB", label: "RAM (MiB)", value: 2048, min: 512, max: 1048576 },
                { name: "bootDiskGiB", label: "Boot disk (GiB)", value: 8, min: 8, max: 65536 },
                { name: "stateDiskGiB", label: "Agent state disk (GiB)", value: 16, min: 16, max: 65536 },
              ].map((field) => (
                <div className="field" key={field.name}>
                  <label htmlFor={`worker-${field.name}`}>{field.label}</label>
                  <input id={`worker-${field.name}`} name={field.name} type="number" required min={field.min} max={field.max} step={1} defaultValue={field.value} />
                </div>
              ))}
            </div>
            <p className="muted">
              At most 128 total vCPUs (sockets × cores). Small defaults are starting assumptions, not benchmarked limits.
              Boot and private agent state use separate disks; missing state storage must block future bootstrap.
            </p>
            <div className="form__row" key={`${installation?.address}/${host?.id}`}>
              <div className="field">
                <label htmlFor="worker-datastore">Disk datastore</label>
                <select id="worker-datastore" name="datastore" required defaultValue={host?.datastores.disks ?? datastores[0] ?? ""}>
                  {datastores.map((d) => <option key={d} value={d}>{d}</option>)}
                </select>
                <span className="field__hint">For both disks. Storage content types and capacity are not checked.</span>
              </div>
              <div className="field">
                <label htmlFor="worker-bridge">Ethernet bridge</label>
                <select id="worker-bridge" name="bridge" required defaultValue={bridges[0] ?? ""}>
                  {bridges.map((b) => <option key={b} value={b}>{b}</option>)}
                </select>
                <span className="field__hint">DHCP; connectivity from the future guest is not qualified.</span>
              </div>
              <div className="field">
                <label htmlFor="worker-vlan">VLAN (optional)</label>
                <input id="worker-vlan" name="vlan" type="number" min={1} max={4094} step={1} />
              </div>
            </div>
            {!canPrepare ? (
              <Callout kind="warning">
                {!installation
                  ? "Select a configured installation from the current source revision."
                  : "The selected host needs configured datastores and Ethernet bridges before a draft can be prepared."}
              </Callout>
            ) : null}
            <div>
              <div className="field">
                <label htmlFor="worker-state-backend">OpenTofu state backend</label>
                <select id="worker-state-backend" name="stateBackend" defaultValue={data.stateBackends?.[0] ?? ""}>
                  {(data.stateBackends ?? []).map((backend) => <option key={backend}>{backend}</option>)}
                </select>
                <span className="field__hint">Used by the independent control worker to create this VM. The new worker gets a dedicated routing label until its workload endpoints are qualified.</span>
              </div>
              {data.deploymentBlockedReason ? <Callout kind="warning">{data.deploymentBlockedReason}</Callout> : null}
              <button type="submit" name="action" value="deploy" className="button button--primary"
                disabled={!canPrepare || Boolean(data.deploymentBlockedReason) || !data.stateBackends?.length || review.isPending}>
                {review.isPending ? "Reviewing…" : "Review deployment"}
              </button>
              <button type="submit" name="action" value="draft" className="button" disabled={!canPrepare || prepare.isPending}>
                {prepare.isPending ? "Generating…" : "Generate review draft"}
              </button>
            </div>
          </div>
        </fieldset>
        <ErrorMessage error={prepare.error} />
        <ErrorMessage error={review.error} />
      </form>
      {review.data && captured ? (
        <Card title={review.data.alreadyExists ? "Worker already exists" : "Review repository addition"}>
          <p><code>{review.data.address}</code> · Generation <code>{review.data.generation}</code></p>
          <p>Branch <code>{review.data.branch}</code> · Exact base <code>{review.data.baseCommit}</code></p>
          {review.data.alreadyExists ? (
            <p>No replacement will be created. <Link to="/inventory">Open inventory</Link> or <Link to="/agents">check enrollment</Link>.</p>
          ) : (
            <div className="stack">
              {Object.entries(review.data.files).map(([file, text]) => <DefinitionView key={file} file={file} text={text} />)}
              <Callout kind="warning">This writes the displayed files to the configured repository branch. It does not grant revision trust or approve infrastructure changes. Existing files will never be overwritten.</Callout>
              <label>
                <input type="checkbox" checked={confirmed} disabled={mustReview || publish.isPending || Boolean(publish.data)} onChange={(e) => setConfirmed(e.target.checked)} />
                {" "}I reviewed these exact files and want to add this worker to the repository.
              </label>
              <button type="button" className="button button--primary"
                disabled={!confirmed || mustReview || publish.isPending || Boolean(publish.data)}
                onClick={() => publish.mutate({ draft: captured, reviewSha256: review.data.reviewSha256, confirmRepositoryWrite: true }, {
                  onError: () => { setConfirmed(false); setMustReview(true); },
                })}>
                {publish.isPending ? "Publishing…" : "Add worker to repository"}
              </button>
              <ErrorMessage error={publish.error} />
              {mustReview ? <Callout kind="warning">Publication was not confirmed. Reload the current source and review again before retrying; never assume a failed response means no commit was written.</Callout> : null}
            </div>
          )}
        </Card>
      ) : null}
      {publish.data ? (
        <Callout kind="success" title={publish.data.alreadyPresent ? "Worker definition already present" : "Worker definition published"}>
          <p>Commit <code>{publish.data.commit}</code>. Sync has been requested.</p>
          <p><Link to="/repository">Review and trust the new revision</Link>, then <Link to="/runs">review its saved deployment plan</Link> and approve it. Enrollment readiness appears under Agents after bootstrap.</p>
        </Callout>
      ) : null}
      {prepare.data ? (
        <div className="stack">
          <Callout kind="info" title="Review artifact generated — not deployed">
            No Git write, trust, run, enrollment, reservation or apply occurred. This is not a guest definition.
            Save under <code>docs/worker-drafts/</code> for Git review, never as <code>guest.yaml</code>.
          </Callout>
          <a className="button" download={prepare.data.fileName} href={`data:application/json;charset=utf-8,${encodeURIComponent(prepare.data.content)}`}>
            Download review draft
          </a>
          <DefinitionView file={prepare.data.fileName} text={prepare.data.content} />
        </div>
      ) : null}
    </>
  );
}

export function WorkerPreparationPage() {
  const { data, error, isPending, isFetching, refetch } = useWorkerPreparation();
  const [search] = useSearchParams();
  if (isPending) return <PageSkeleton rows={3} />;
  if (!data) return <ErrorMessage error={error} title="Worker preparation could not be loaded" />;
  const revision = data.revision;
  return (
    <>
      <PageHeader
        eyebrow={<Link to="/agents">Agents</Link>}
        title="Deploy a NixOS worker"
        meta="Configure once, review the repository addition, then approve the saved VM plan. Existing workers are never replaced."
        actions={<button type="button" className="button" disabled={isFetching} onClick={() => void refetch()}>Reload preparation</button>}
      />
      <div className="stack stack--large">
        <ErrorMessage error={error} />
        <Card title="Current repository source">
          <p><code>{data.sourceUrl ?? "Not configured"}</code> · Branch <code>{data.branch ?? "—"}</code></p>
          <p>Loaded source head: <code>{data.headCommit ?? "Not loaded"}</code></p>
          {data.lastFetchError ? <Callout kind="warning" title="Last sync failed">{data.lastFetchError} This draft can only use the last loaded source head.</Callout> : null}
          <p>
            {revision?.isCurrentSource && revision.trustedAt && revision.trustedBy
              ? "Exact current-source revision trusted. This is not deployment approval or sandboxing."
              : "Exact current-source revision is not trusted. Preparation does not grant trust."}
            {" "}<Link to="/repository">Review source and revision trust</Link>.
          </p>
        </Card>
        <Card title="Deployment and safety checks">
          <ul>{data.deploymentGates.map((gate) => <li key={gate}>{gate}</li>)}</ul>
        </Card>
        {data.existingWorkers?.length ? (
          <Card title="Existing managed workers">
            <ul>{data.existingWorkers.map((worker) => (
              <li key={worker.generation}>
                <code>{worker.address}</code> · {worker.host} · VMID {worker.vmid} · {worker.status ?? "Declared"}
                {" · "}{worker.runId ? <Link to={`/runs/${worker.runId}`}>Deployment run</Link> : null}
                {" "}<Link to="/agents">Check agent</Link>
              </li>
            ))}</ul>
            <p>These guests already have a generation. Use their existing run and enrollment status; the wizard does not recreate them.</p>
          </Card>
        ) : null}
        <Card title="Worker configuration">
          {data.preparationBlockedReason ? (
            <Callout kind="warning">{data.preparationBlockedReason}</Callout>
          ) : !data.installations.length ? (
            <p>No valid configured Proxmox installations in the current source revision. Fix definitions and sync first.</p>
          ) : (
            <>
              <p className="muted">Only valid installations and their declared hosts from this exact source revision are listed.</p>
              <DraftForm key={revision?.id} data={data} initialInstallation={search.get("installation")} />
            </>
          )}
        </Card>
      </div>
    </>
  );
}
