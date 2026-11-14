import { Play } from "lucide-react";
import { isNotFound } from "../api/client";
import { Link, useNavigate, useParams } from "react-router";
import { usePlanGuest, useGuest, useUptime } from "../api/queries";
import type { GuestDetail } from "../api/types";
import { Badge, StatusBadge } from "../components/Badge";
import { Callout } from "../components/Callout";
import { Card } from "../components/Card";
import { JsonBlock } from "../components/CodeBlock";
import { DefinitionSheet, ViewYamlButton } from "../components/DefinitionSheet";
import { Glance } from "../components/Glance";
import { IssueList } from "../components/IssueList";
import { GuestConfigurationCard } from "../components/GuestConfiguration";
import { ManagedAccessCard } from "../components/ManagedAccess";
import { ManagedHostKeyCard } from "../components/ManagedHostKey";
import { KeyValue } from "../components/KeyValue";
import { LabelList } from "../components/LabelEditor";
import { Commit } from "../components/Mono";
import { PageHeader } from "../components/PageHeader";
import { PolicyCard } from "../components/PolicyView";
import { RelativeTime } from "../components/RelativeTime";
import { RunTable } from "../components/RunTable";
import { ServicesCard } from "../components/ServicesCard";
import { ChangeActions, RecentChanges } from "../components/ChangeActions";
import { WorkflowLabel } from "../components/WorkflowLabel";
import { ErrorMessage, NotFound, PageSkeleton } from "../components/States";
import { useHashScroll } from "../components/useHashScroll";
import { readDefinition, type DefinitionField, type DefinitionSection } from "../lib/definition";
import { formatBytes, humanize, machineNoun } from "../lib/format";
import { formatPercent, percentOf } from "../lib/inventory";
import { formatUptime } from "../lib/time";
import { WaitingRun } from "./run/WaitingRun";

/** What Octopushy knows of the guest beyond its definition: workflow, revisions, identity, and what Proxmox reports. */
function State({ guest, noun }: { guest: GuestDetail; noun: string }) {
  const vm = guest.guest.observed;
  const binding = guest.binding;
  const at = (value: string | null) => (value ? <> <RelativeTime value={value} /></> : null);
  return (
    <KeyValue
      items={[
        { term: "Workflow", value: <WorkflowLabel workflow={guest.guest.workflow} /> },
        { term: "Desired", value: <Commit commit={guest.desiredCommit} /> },
        { term: "Checked", value: <span><Commit commit={guest.checkedCommit} />{at(guest.checkedAt)}</span> },
        { term: "Applied", value: <span><Commit commit={guest.appliedCommit} />{at(guest.appliedAt)}</span> },
        { term: "Verified", value: <RelativeTime value={guest.verifiedAt} empty="Never" /> },
        { term: "In sync", value: guest.inSync ? <Badge tone="success">Yes</Badge> : <Badge tone="neutral">No</Badge> },
        ...(binding
          ? [
              { term: "Installation", value: <span className="mono">{binding.installation}</span> },
              { term: "VMID", value: <span className="num">{binding.vmid}</span> },
              { term: "Bound", value: <RelativeTime value={binding.boundAt} /> },
            ]
          : [
              {
                term: "Bound",
                value: <span className="muted">Not yet. The address binds to its installation and VMID after the first successful apply.</span>,
              },
            ]),
        ...(vm
          ? [
              ...(binding ? [] : [{ term: "VMID", value: <span className="num">{vm.vmid}</span> }]),
              { term: "Name", value: <span className="mono">{vm.name ?? "—"}</span> },
              { term: "Host", value: <span className="mono">{vm.host ?? "—"}</span> },
              { term: "Template", value: vm.template ? "Yes" : "No" },
              { term: "Tags", value: <LabelList labels={vm.tags} /> },
            ]
          : [{ term: `Observed ${noun}`, value: <span className="muted">Not observed: the installation has not reported a {noun} with this VMID.</span> }]),
      ]}
    />
  );
}

function field(sections: DefinitionSection[] | null, section: string, key: string): DefinitionField | undefined {
  return sections?.find((s) => s.id === section)?.fields.find((f) => f.key === key);
}

function text(f: DefinitionField | undefined): string | null {
  return f?.value.kind === "text" ? f.value.text : null;
}

/** The declared disks: one as written, several summed when they are all in GiB. */
function disks(sections: DefinitionSection[] | null): { value: string; note: string | null } | null {
  const all = sections?.find((s) => s.id === "hardware")?.fields.filter((f) => f.key.startsWith("disks")) ?? [];
  const sizes = all.map(text).filter((t): t is string => t !== null);
  if (sizes.length === 0) return null;
  if (sizes.length === 1) return { value: sizes[0], note: all[0].value.kind === "text" ? (all[0].value.raw ?? null) : null };
  const gib = sizes.map((t) => /^(\d+(?:\.\d+)?) GiB$/.exec(t)?.[1]);
  const total = gib.every(Boolean) ? `${gib.reduce((sum, n) => sum + Number(n), 0)} GiB` : sizes[0];
  return { value: total, note: `${sizes.length} disks` };
}

/** The guest's figures: power, size, address and how its SSH answers. */
function GuestGlance({ data, sections }: { data: GuestDetail; sections: DefinitionSection[] | null }) {
  const { data: uptime } = useUptime();
  const { guest } = data;
  const vm = guest.observed;
  const ssh = uptime?.checks.find((c) => c.kind === "guest-ssh" && c.resource === guest.address);
  const cores = vm?.maxCpu ?? text(field(sections, "hardware", "cores"));
  const memory = vm?.maxMemoryBytes ? formatBytes(vm.maxMemoryBytes) : text(field(sections, "hardware", "memory"));
  const disk = disks(sections);
  const network = [text(field(sections, "network", "mode")), text(field(sections, "network", "bridge"))].filter(Boolean).join(", ");
  return (
    <Glance
      cells={[
        {
          label: "Power",
          value: vm ? humanize(vm.status ?? "unknown") : "Not observed",
          note: vm?.uptimeSeconds ? `up ${formatUptime(vm.uptimeSeconds)}` : null,
        },
        cores !== null && cores !== undefined ? { label: "Cores", value: cores, note: vm?.maxCpu ? "observed" : "declared" } : null,
        memory ? { label: "Memory", value: memory, note: vm?.maxMemoryBytes ? "observed" : "declared" } : null,
        disk ? { label: "Disk", value: disk.value, note: disk.note } : null,
        guest.accessAddress ? { label: "Address", value: <span className="mono">{guest.accessAddress}</span>, note: network || null } : null,
        ssh
          ? {
              label: "SSH, 30 days",
              value: formatPercent(percentOf([ssh], "month")),
              note:
                ssh.status === "up" && ssh.elapsedMs !== null
                  ? `answers in ${ssh.elapsedMs} ms`
                  : (ssh.detail ?? (ssh.status === "down" ? "Down" : "Not checked yet")),
            }
          : null,
      ]}
    />
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
  const noun = machineNoun(guest.type, guest.observed?.kind);
  const sections = data.definitionText === null ? null : readDefinition(data.definitionText, "guest");
  // A provision run applies the machine itself, a configure run what goes on it: each is compared with what its last run had.
  const applied = [
    ...(!data.inSync && data.appliedDefinitionText ? [{ text: data.appliedDefinitionText, sections: ["general", "hardware", "network"] }] : []),
    ...(data.configuration && !data.configuration.inSync && data.configuration.appliedDefinitionText
      ? [{ text: data.configuration.appliedDefinitionText, sections: ["inputs", "secrets", "access"] }]
      : []),
  ];

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
          <>
            {data.definitionText !== null ? <ViewYamlButton file={data.definitionFile} text={data.definitionText} issues={data.issues} /> : null}
            {data.missingSince || data.retiredAt ? null : <ChangeActions resourceId={guest.id} kind="guest" />}
            <button
              type="button"
              className="button button--primary"
              onClick={() => plan.mutate(id, { onSuccess: (started) => navigate(`/runs/${started.runId}`) })}
              disabled={plan.isPending}
            >
              <Play size={15} aria-hidden="true" />
              {plan.isPending ? "Starting…" : data.missingSince && !data.retiredAt ? "Plan removal" : "Plan now"}
            </button>
          </>
        }
      />
      <div className="stack stack--large">
        <ErrorMessage error={plan.error} />
        {error ? <ErrorMessage error={error} title="Showing the last data; refreshing failed" /> : null}
        {data.missingSince && !data.retiredAt ? (
          <Callout kind="warning" title={<>Folder deleted <RelativeTime value={data.missingSince} /></>}>
            Octopushy proposes removing the {noun}, using the guest's last applied
            definition and its approval and destructive policy. If no removal run is shown, use Plan removal. Restore the
            folder to keep the guest; that cancels a removal that has not started changing anything.
          </Callout>
        ) : null}
        {data.retiredAt ? (
          <Callout kind="info" title={<>Retired <RelativeTime value={data.retiredAt} /></>}>
            The {noun} was removed and verified absent.
          </Callout>
        ) : null}
        {guest.activeRun?.status === "awaiting-approval" ? <WaitingRun run={guest.activeRun} /> : null}
        <GuestGlance data={data} sections={sections} />
        <div className="columns columns--main-side">
          <div className="stack stack--large">
            <DefinitionSheet file={data.definitionFile} text={data.definitionText} kind="guest" issues={data.issues} applied={applied} />
            {data.issues.length > 0 ? (
              <Card title="Issues" meta={data.issues.length} id="issues">
                <IssueList issues={data.issues} />
              </Card>
            ) : null}
            <RecentChanges resourceId={guest.id} />
            <Card title="Runs" flush id="runs">
              <RunTable runs={data.runs} label={`Runs of ${guest.name}`} showAddress={false} />
            </Card>
          </div>
          <div className="stack stack--large">
            {data.configuration ? <GuestConfigurationCard id={guest.id} configuration={data.configuration} /> : null}
            {data.services.length > 0 ? <ServicesCard services={data.services} /> : null}
            <PolicyCard policy={data.policy} agentLabels={data.agentLabels} agentLabelsSetBy={data.agentLabelsSetBy} />
            {data.managedAccess ? <ManagedAccessCard id={guest.id} access={data.managedAccess} /> : null}
            {data.hostKey ? <ManagedHostKeyCard hostKey={data.hostKey} /> : null}
            <Card title="State">
              <State guest={data} noun={noun} />
            </Card>
            {data.outputs === null || data.outputs === undefined ? null : (
              <Card>
                <details className="outputs">
                  <summary>Outputs</summary>
                  <JsonBlock value={data.outputs} />
                </details>
              </Card>
            )}
          </div>
        </div>
      </div>
    </>
  );
}
