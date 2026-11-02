import { Play, ShieldCheck } from "lucide-react";
import { isNotFound } from "../api/client";
import { Link, useNavigate, useParams } from "react-router";
import { usePlanGuest, useGuest, useTrustGuest } from "../api/queries";
import type { GuestDetail, ObservedGuest } from "../api/types";
import { Badge, StatusBadge } from "../components/Badge";
import { Callout } from "../components/Callout";
import { Card } from "../components/Card";
import { JsonBlock } from "../components/CodeBlock";
import { DefinitionView } from "../components/DefinitionView";
import { Execution } from "../components/Execution";
import { IssueList } from "../components/IssueList";
import { GuestConfigurationCard } from "../components/GuestConfiguration";
import { GuestDiskUsage } from "../components/GuestDiskUsage";
import { KeyValue } from "../components/KeyValue";
import { LabelList } from "../components/LabelEditor";
import { Commit } from "../components/Mono";
import { PageHeader } from "../components/PageHeader";
import { PolicyView } from "../components/PolicyView";
import { RelativeTime } from "../components/RelativeTime";
import { RunTable } from "../components/RunTable";
import { ErrorMessage, NotFound, PageSkeleton } from "../components/States";
import { useHashScroll } from "../components/useHashScroll";
import { formatBytes } from "../lib/format";
import { formatUptime } from "../lib/time";

function ObservedVm({ vm }: { vm: ObservedGuest | null }) {
  if (!vm) return <p className="muted">Not observed: the installation has not reported a guest with this VMID.</p>;
  const memory = vm.maxMemoryBytes;
  const cpu = vm.maxCpu;
  return (
    <KeyValue
      items={[
        { term: "VMID", value: <span className="num">{vm.vmid}</span> },
        { term: "Type", value: vm.kind === "lxc" ? "Container" : vm.kind === "qemu" ? "VM" : "—" },
        { term: "Name", value: <span className="mono">{vm.name ?? "—"}</span> },
        { term: "Host", value: <span className="mono">{vm.host ?? "—"}</span> },
        { term: "Power", value: vm.status ?? "—" },
        { term: "CPU / memory", value: <span className="num">{cpu ?? "—"} vCPU · {formatBytes(memory)}</span> },
        { term: "Uptime", value: <span className="num">{formatUptime(vm.uptimeSeconds)}</span> },
        { term: "Template", value: vm.template ? "Yes" : "No" },
        { term: "Tags", value: <LabelList labels={vm.tags} /> },
      ]}
    />
  );
}

function Revisions({ guest }: { guest: GuestDetail }) {
  return (
    <KeyValue
      items={[
        { term: "Desired", value: <Commit commit={guest.desiredCommit} /> },
        {
          term: "Checked",
          value: (
            <span>
              <Commit commit={guest.checkedCommit} /> {guest.checkedAt ? <RelativeTime value={guest.checkedAt} /> : null}
            </span>
          ),
        },
        {
          term: "Applied",
          value: (
            <span>
              <Commit commit={guest.appliedCommit} /> {guest.appliedAt ? <RelativeTime value={guest.appliedAt} /> : null}
            </span>
          ),
        },
        { term: "Verified", value: <RelativeTime value={guest.verifiedAt} empty="Never" /> },
        {
          term: "In sync",
          value: guest.inSync ? <Badge tone="success">Yes</Badge> : <Badge tone="neutral">No</Badge>,
        },
      ]}
    />
  );
}

function Identity({ guest }: { guest: GuestDetail }) {
  if (!guest.binding) {
    return (
      <p className="muted">
        Not bound yet. The address binds to its installation and VMID after the first successful apply.
      </p>
    );
  }
  return (
    <KeyValue
      items={[
        { term: "Installation", value: <span className="mono">{guest.binding.installation}</span> },
        { term: "VMID", value: <span className="num">{guest.binding.vmid}</span> },
        { term: "Bound", value: <RelativeTime value={guest.binding.boundAt} /> },
        ...(guest.stateful
          ? [{
              term: "Data",
              value: guest.dataGuarded
                ? "Stateful: a plan that would replace or delete this guest stops before approval."
                : "Stateful: guarded once its first deployment is verified.",
            }]
          : []),
      ]}
    />
  );
}

/** Asks for trust while protected secrets would go to code nobody saw, on a guest no cluster looks after. */
function TrustCallout({ guest }: { guest: GuestDetail }) {
  const trust = useTrustGuest();
  const { keys, codeDigest, execution, trusted, cluster, refusal } = guest.trust;
  if (keys.length === 0 || trusted) return null;
  return (
    <div id="trust">
      <Callout
        kind={refusal ? "danger" : "warning"}
        title="Waiting for trust"
        actions={
          cluster || refusal || !codeDigest ? undefined : (
            <button
              type="button"
              className="button button--primary"
              disabled={trust.isPending}
              onClick={() => trust.mutate({ id: guest.guest.id, codeDigest })}
            >
              <ShieldCheck size={15} aria-hidden="true" />
              {trust.isPending ? "Trusting…" : "Trust guest"}
            </button>
          )
        }
      >
        <p>
          This guest binds {keys.length === 1 ? "a protected secret" : "protected secrets"}, which only code an operator reviewed may
          receive:
        </p>
        <ul className="small">
          {keys.map((key) => (
            <li key={key} className="mono">
              {key}
            </li>
          ))}
        </ul>
        {cluster ? (
          <p>
            It is a member of <span className="mono">{cluster}</span>: Trust cluster, on the cluster's page, covers it with the rest.
          </p>
        ) : refusal ? (
          <p>{refusal}</p>
        ) : (
          <>
            <p>
              The secrets reach this code without anyone having to approve it, in the apply, and in the plan for a Proxmox token, so trust covers it. Review the recipe and
              its roots in the repository first; this is what you would be trusting. It holds until the code, or how the guest runs, changes.
            </p>
            <ul className="small">
              <li>
                All of it: <span className="mono break">{codeDigest}</span>
                <Execution of={execution} />
              </li>
            </ul>
          </>
        )}
        <ErrorMessage error={trust.error} />
      </Callout>
    </div>
  );
}

export function GuestPage() {
  const { id = "" } = useParams();
  const { data, error, isPending } = useGuest(id);
  const plan = usePlanGuest();
  const navigate = useNavigate();
  useHashScroll(Boolean(data));

  if (isPending) return <PageSkeleton rows={4} />;
  if (!data) return isNotFound(error) ? <NotFound what="guest" /> : <ErrorMessage error={error} title="The guest could not be loaded" />;

  const { guest } = data;

  return (
    <>
      <PageHeader
        eyebrow={
          <>
            <Link to="/inventory">Inventory</Link> / Guest
          </>
        }
        title={<span className="mono">{guest.address}</span>}
        documentTitle={guest.name}
        meta={
          <span className="inline-group">
            <StatusBadge status={guest.status} />
            {guest.statusDetail ? <span>{guest.statusDetail}</span> : null}
            {guest.activeRun ? (
              <Link to={`/runs/${guest.activeRun.id}`}>Run #{guest.activeRun.number}</Link>
            ) : null}
          </span>
        }
        actions={
          <button
            type="button"
            className="button button--primary"
            onClick={() => plan.mutate(id, { onSuccess: (started) => navigate(`/runs/${started.runId}`) })}
            disabled={plan.isPending}
          >
            <Play size={15} aria-hidden="true" />
            {plan.isPending ? "Starting…" : data.missingSince && !data.retiredAt ? "Plan removal" : "Plan now"}
          </button>
        }
      />
      <div className="stack stack--large">
        <ErrorMessage error={plan.error} />
        {error ? <ErrorMessage error={error} title="Showing the last data; refreshing failed" /> : null}
        {data.missingSince && !data.retiredAt ? (
          <Callout kind="warning" title={<>Folder deleted <RelativeTime value={data.missingSince} /></>}>
            Octopushy proposes removing the guest, using its last applied
            definition and its approval and destructive policy. If no removal run is shown, use Plan removal. Restore the
            folder to keep the guest; that cancels a removal that has not started changing anything.
          </Callout>
        ) : null}
        <TrustCallout guest={data} />
        {data.retiredAt ? (
          <Callout kind="info" title={<>Retired <RelativeTime value={data.retiredAt} /></>}>
            The guest was removed and verified absent.
          </Callout>
        ) : null}
        <div className="columns columns--main-side">
          <div className="stack stack--large">
            <Card title="Definition" id="definition">
              <DefinitionView file={data.definitionFile} text={data.definitionText} issues={data.issues} />
            </Card>
            <Card title="Issues" meta={data.issues.length || undefined} id="issues">
              <IssueList issues={data.issues} />
            </Card>
            <Card title="Runs" flush id="runs">
              <RunTable runs={data.runs} label={`Runs of ${guest.name}`} showAddress={false} />
            </Card>
          </div>
          <div className="stack stack--large">
            <Card title="Policy">
              <PolicyView policy={data.policy} />
            </Card>
            <Card title="Agent labels">
              <div className="stack stack--small">
                <LabelList labels={data.agentLabels} empty="Any agent" />
                {data.agentLabelsSetBy ? <span className="muted small">set by {data.agentLabelsSetBy}</span> : null}
              </div>
            </Card>
            {data.configuration ? <GuestConfigurationCard id={guest.id} configuration={data.configuration} /> : null}
            <Card title="Identity">
              <Identity guest={data} />
            </Card>
            <Card title="Revisions">
              <Revisions guest={data} />
            </Card>
            <Card title="Observed guest">
              <ObservedVm vm={guest.observed} />
            </Card>
            <GuestDiskUsage key={guest.id} id={guest.id} usage={data.diskUsage} inactive={Boolean(data.retiredAt || data.missingSince)} container={guest.type === "container"} />
            <Card title="Outputs">
              {data.outputs === null || data.outputs === undefined ? (
                <p className="muted">No outputs recorded yet. They appear after a successful apply.</p>
              ) : (
                <JsonBlock value={data.outputs} />
              )}
            </Card>
          </div>
        </div>
      </div>
    </>
  );
}
