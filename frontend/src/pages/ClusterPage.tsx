import { useState } from "react";
import { Play, RefreshCw, ShieldCheck } from "lucide-react";
import { Link, useNavigate, useParams } from "react-router";
import { isNotFound } from "../api/client";
import { useAcknowledgeDetach, useCluster, usePlanCluster, useReadClusterCertificates, useTrustCluster } from "../api/queries";
import type { ClusterDetail, ClusterSummary } from "../api/types";
import { Badge, StatusBadge } from "../components/Badge";
import { Callout } from "../components/Callout";
import { Card } from "../components/Card";
import { DefinitionView } from "../components/DefinitionView";
import { IssueList } from "../components/IssueList";
import { KeyValue } from "../components/KeyValue";
import { Commit } from "../components/Mono";
import { PageHeader } from "../components/PageHeader";
import { PolicyView } from "../components/PolicyView";
import { RelativeTime } from "../components/RelativeTime";
import { RunTable } from "../components/RunTable";
import { EmptyRow, Table } from "../components/Table";
import { ErrorMessage, NotFound, PageSkeleton } from "../components/States";
import { useHashScroll } from "../components/useHashScroll";
import { humanize } from "../lib/format";

function shortened(digest: string | null | undefined) {
  return digest ? `${digest.slice(0, 16)}…` : "—";
}

/** Asks for trust while the admin password would go somewhere new, to code nobody saw, or to an unknown certificate. */
function TrustCallout({ cluster }: { cluster: ClusterSummary }) {
  const trust = useTrustCluster();
  const read = useReadClusterCertificates();
  const { endpoints, untrusted, codeChanged, codeDigest, trustedAt, certificates, passwordProtected } = cluster.trust;
  const unseen = certificates.filter((c) => !c.sha256);
  const changed = certificates.filter((c) => c.sha256 && !c.trusted);
  const needed = !trustedAt || untrusted.length > 0 || codeChanged || changed.length > 0;
  if (!cluster.valid || !codeDigest || !(needed || unseen.length > 0)) return null;
  const replaced = changed.filter((c) => c.trustedSha256);
  const shown = Object.fromEntries(certificates.filter((c) => c.sha256).map((c) => [c.endpoint, c.sha256 as string]));
  return (
    <div id="trust">
      <Callout
        kind={replaced.length > 0 ? "danger" : "warning"}
        title={
          replaced.length > 0
            ? "A member presents another certificate"
            : !trustedAt
              ? "New application cluster"
              : untrusted.length > 0
                ? "New member endpoint"
                : codeChanged
                  ? "Members or code changed"
                  : "Certificates to trust"
        }
        actions={
          <>
            <button type="button" className="button" disabled={read.isPending} onClick={() => read.mutate(cluster.id)}>
              <RefreshCw size={15} aria-hidden="true" />
              {read.isPending ? "Reading…" : "Read certificates"}
            </button>
            <button
              type="button"
              className="button button--primary"
              disabled={trust.isPending}
              onClick={() => trust.mutate({ id: cluster.id, endpoints, codeDigest, certificates: shown })}
            >
              <ShieldCheck size={15} aria-hidden="true" />
              {trust.isPending ? "Trusting…" : "Trust cluster"}
            </button>
          </>
        }
      >
        {replaced.length > 0 ? (
          <p>
            Either the member was rebuilt or something else is answering at its address. It receives nothing until you trust this. Compare
            the fingerprint with the member's own, from the Proxmox host, as docs/technitium-lxc.md describes.
          </p>
        ) : null}
        {untrusted.length > 0 ? (
          <p>
            The cluster's admin password is sent only to trusted endpoints. Runs wait until you trust{" "}
            {untrusted.map((endpoint, i) => (
              <span key={endpoint}>
                {i > 0 ? ", " : null}
                <span className="mono break">{endpoint}</span>
              </span>
            ))}
            .
          </p>
        ) : null}
        {codeChanged || !trustedAt ? (
          <>
            <p>
              The password reaches this code, in a plan or an apply, before any person approves it, so trust covers it. Review the recipes
              and roots in the repository first; this is what you would be trusting.
            </p>
            <ul className="small">
              <li>
                All of it: <span className="mono break">{codeDigest}</span>
              </li>
              {cluster.members.map((member) => (
                <li key={member.guest}>
                  <span className="mono">{member.guest}</span>: <span className="mono">{shortened(member.codeDigest)}</span>
                  <Execution of={member.execution} />
                </li>
              ))}
            </ul>
          </>
        ) : null}
        <Table label="Member certificates">
          <thead>
            <tr>
              <th scope="col">Endpoint</th>
              <th scope="col">Presents (SHA-256)</th>
              <th scope="col">Trusted</th>
            </tr>
          </thead>
          <tbody>
            {certificates.map((c) => (
              <tr key={c.endpoint}>
                <td className="mono">{c.endpoint}</td>
                <td className="mono break">{c.sha256 ?? <span className="muted">Not read yet</span>}</td>
                <td>{c.trusted ? <Badge tone="success">Trusted</Badge> : <Badge tone="warning">Not trusted</Badge>}</td>
              </tr>
            ))}
          </tbody>
        </Table>
        {unseen.length > 0 ? (
          <p className="small muted">
            A member's certificate can be read once it is installed. Trust the code now so that the guests can install, and come back to trust
            their certificates.
          </p>
        ) : null}
        {passwordProtected ? null : (
          <p className="small">
            The password's store entry is not protected, so a commit could reach it some other way. Mark it protected under Variables.
          </p>
        )}
        <ErrorMessage error={trust.error ?? read.error} />
      </Callout>
    </div>
  );
}

/**
 * A login key as an operator reads it: its type, the end of the key itself, and who it says it
 * belongs to. The end, because every key of a type starts alike and the comment says what it likes.
 */
function describeKey(key: string) {
  const [type, body = "", ...comment] = key.trim().split(/\s+/);
  return `${type} ${body.length > 24 ? `…${body.slice(-24)}` : body} ${comment.join(" ")}`.trim();
}

/** How a member would use the password beyond its code. Each of these is covered by trust. */
function Execution({ of }: { of: unknown }) {
  if (typeof of !== "object" || of === null) return null;
  const run = of as {
    backend?: { type?: string; config?: unknown; env?: unknown };
    secrets?: Record<string, string>;
    agentLabels?: string[];
    proxmox?: {
      endpoint?: string | null;
      verifyTls?: boolean | null;
      readOnlyToken?: { store?: string } | null;
      privilegedToken?: { store?: string } | null;
      caCertificate?: { store?: string } | null;
    };
    template?: { source?: { url?: string } } | null;
    guest?: {
      vmid?: number;
      host?: string;
      access?: { sshAuthorizedKeys?: string[] };
      hardware?: { mountPoints?: { path?: string; volume?: string }[] };
    };
    host?: { datastores?: Record<string, string>; bridges?: string[] } | null;
  };
  const backend = run.backend ?? {};
  const keys = run.guest?.access?.sshAuthorizedKeys ?? [];
  const volumes = (run.guest?.hardware?.mountPoints ?? []).filter((mount) => mount.volume);
  const tokens = [
    run.proxmox?.readOnlyToken?.store ? `read-only ${run.proxmox.readOnlyToken.store}` : null,
    run.proxmox?.privilegedToken?.store ? `privileged ${run.proxmox.privilegedToken.store}` : null,
    run.proxmox?.caCertificate?.store ? `CA ${run.proxmox.caCertificate.store}` : null,
  ].filter(Boolean);
  return (
    <ul className="small">
      <li>
        State: <span className="mono">{backend.type ?? "?"}</span>
        {backend.config || backend.env ? <span className="mono break"> {JSON.stringify({ config: backend.config, env: backend.env })}</span> : null}
      </li>
      <li>
        Variables from the store:{" "}
        <span className="mono">
          {Object.entries(run.secrets ?? {})
            .map(([variable, key]) => `${variable} = ${key}`)
            .join(", ") || "none"}
        </span>
      </li>
      <li>
        Agents: <span className="mono">{(run.agentLabels ?? []).join(", ") || "any"}</span>
      </li>
      <li>
        Proxmox: <span className="mono">{run.proxmox?.endpoint ?? "?"}</span>
        {run.proxmox?.verifyTls === false ? ", its certificate not verified" : ""}
        {tokens.length > 0 ? <span className="mono break">, credentials {tokens.join(", ")}</span> : null}
      </li>
      {run.guest?.vmid !== undefined ? (
        <li>
          Guest: <span className="mono">{run.guest.vmid}</span> on <span className="mono">{run.guest.host ?? "?"}</span>
          {run.host?.datastores ? <span className="mono break">, datastores {Object.entries(run.host.datastores).map(([use, name]) => `${use} = ${name}`).join(", ")}</span> : null}
          {run.host?.bridges ? <span className="mono">, bridges {run.host.bridges.join(", ")}</span> : null}
          {volumes.length > 0 ? <span className="mono break">, volumes {volumes.map((mount) => `${mount.path} = ${mount.volume}`).join(", ")}</span> : null}
        </li>
      ) : null}
      <li>
        {keys.length > 0 ? (
          <>
            <strong>Can log in as root:</strong>
            <ul>
              {keys.map((key) => (
                <li key={key} className="mono break">
                  {describeKey(key)}
                </li>
              ))}
            </ul>
          </>
        ) : (
          <>Login keys: none</>
        )}
      </li>
      {run.template?.source?.url ? (
        <li>
          Template: <span className="mono break">{run.template.source.url}</span>
        </li>
      ) : null}
    </ul>
  );
}

function Leaving({ cluster, leaving }: { cluster: ClusterSummary; leaving: ClusterDetail["leaving"] }) {
  const acknowledge = useAcknowledgeDetach();
  const [note, setNote] = useState("");
  if (leaving.length === 0) return null;
  return (
    <Callout kind="warning" title="Guests still joined that the cluster no longer lists">
      <p>
        Each can be retired once it has left. A cluster run takes out a member removed from <span className="mono">members</span>. If this one
        left another way, or the cluster's definition is gone, say so here; that is your statement, and nothing checks it.
      </p>
      <label>
        How it left{" "}
        <input value={note} onChange={(e) => setNote(e.target.value)} aria-label="How it left" />
      </label>
      <ul>
        {leaving.map((guest) => (
          <li key={guest.guestId}>
            <span className="mono">{guest.guest}</span> ({guest.address}){" "}
            {guest.unconfirmed ? (
              <>The primary no longer lists it, but nothing saw it leave, so it may still believe it is a member. Check it first. </>
            ) : null}
            <button
              type="button"
              className="button button--small"
              disabled={acknowledge.isPending || note.trim() === ""}
              onClick={() => acknowledge.mutate({ id: cluster.id, guestId: guest.guestId, note })}
            >
              It has left
            </button>
          </li>
        ))}
      </ul>
      <ErrorMessage error={acknowledge.error} />
    </Callout>
  );
}

function Members({ cluster, verification }: { cluster: ClusterSummary; verification: ClusterDetail["verification"] }) {
  const latest = verification as {
    ok?: boolean;
    error?: string;
    members?: { guest: string; ok: boolean; version?: string; recovery?: string; checks?: { name: string; ok: boolean; detail: string }[] }[];
  } | null;
  const reports = latest?.members ?? [];
  return (
    <Table label="Members">
      <thead>
        <tr>
          <th scope="col">Guest</th>
          <th scope="col">Role</th>
          <th scope="col">Address</th>
          <th scope="col">Guest status</th>
          <th scope="col">Last check</th>
        </tr>
      </thead>
      <tbody>
        {cluster.members.length === 0 ? <EmptyRow colSpan={5}>No members declared.</EmptyRow> : null}
        {cluster.members.map((member) => {
          const report = reports.find((r) => r.guest === member.guest);
          const failed = report?.checks?.filter((c) => !c.ok) ?? [];
          return (
            <tr key={member.guest}>
              <td className="mono">
                {member.guestId ? <Link to={`/guests/${member.guestId}`}>{member.guest}</Link> : member.guest}
              </td>
              <td>{humanize(member.role)}</td>
              <td className="mono">{member.address ?? <span className="muted">—</span>}</td>
              <td>{member.guestStatus ? <StatusBadge status={member.guestStatus} /> : <span className="muted">—</span>}</td>
              <td>
                {!report ? (
                  latest?.ok === false ? (
                    <Badge tone="warning">Unknown: the last check failed</Badge>
                  ) : (
                    <span className="muted">Not checked yet</span>
                  )
                ) : report.ok ? (
                  <Badge tone="success">Passed</Badge>
                ) : (
                  <>
                    <Badge tone="danger">Failed</Badge>
                    {failed.map((check) => (
                      <div className="small" key={check.name + check.detail}>
                        {check.detail}
                      </div>
                    ))}
                    {report.recovery ? <div className="small muted">{report.recovery}</div> : null}
                  </>
                )}
              </td>
            </tr>
          );
        })}
      </tbody>
    </Table>
  );
}

export function ClusterPage() {
  const { id = "" } = useParams();
  const { data, error, isPending } = useCluster(id);
  const plan = usePlanCluster();
  const navigate = useNavigate();
  useHashScroll(Boolean(data));

  if (isPending) return <PageSkeleton rows={4} />;
  if (!data) return isNotFound(error) ? <NotFound what="cluster" /> : <ErrorMessage error={error} title="The cluster could not be loaded" />;

  const { cluster } = data;
  return (
    <>
      <PageHeader
        eyebrow={
          <>
            <Link to="/inventory">Inventory</Link> / Application cluster
          </>
        }
        title={<span className="mono">{cluster.address}</span>}
        documentTitle={cluster.name}
        meta={
          <span className="inline-group">
            <StatusBadge status={cluster.status} />
            {cluster.statusDetail ? <span>{cluster.statusDetail}</span> : null}
            {cluster.activeRun ? <Link to={`/runs/${cluster.activeRun.id}`}>Run #{cluster.activeRun.number}</Link> : null}
          </span>
        }
        actions={
          <button
            type="button"
            className="button button--primary"
            onClick={() => plan.mutate(id, { onSuccess: (started) => navigate(`/runs/${started.runId}`) })}
            disabled={plan.isPending || Boolean(data.missingSince)}
          >
            <Play size={15} aria-hidden="true" />
            {plan.isPending ? "Starting…" : "Plan now"}
          </button>
        }
      />
      <div className="stack stack--large">
        <TrustCallout cluster={cluster} />
        <Leaving cluster={cluster} leaving={data.leaving} />
        <ErrorMessage error={plan.error} />
        {cluster.observeError ? (
          <Callout kind="danger" title="The last background check failed">
            {cluster.observeError}
          </Callout>
        ) : null}
        {data.issues.length > 0 ? (
          <Card title="Issues" id="issues">
            <IssueList issues={data.issues} />
          </Card>
        ) : null}
        <div className="columns columns--main-side">
          <div className="stack stack--large">
            <Card title="Members" meta={cluster.members.length} flush>
              <Members cluster={cluster} verification={data.verification} />
            </Card>
            <Card title="Runs" flush>
              <RunTable runs={data.runs} label={`Runs of ${cluster.name}`} showAddress={false} />
            </Card>
            <Card title="Definition" id="definition">
              <DefinitionView file={data.definitionFile} text={data.definitionText} issues={data.issues} />
            </Card>
          </div>
          <div className="stack stack--large">
            <Card title="State">
              <KeyValue
                items={[
                  { term: "Domain", value: <span className="mono">{cluster.clusterDomain ?? "—"}</span> },
                  { term: "Desired", value: data.desiredCommit ? <Commit commit={data.desiredCommit} /> : "—" },
                  { term: "Applied", value: data.appliedCommit ? <Commit commit={data.appliedCommit} /> : "Not yet" },
                  { term: "Verified", value: cluster.verifiedAt ? <RelativeTime value={cluster.verifiedAt} /> : "Not yet" },
                  { term: "Checked in the background", value: cluster.observedAt ? <RelativeTime value={cluster.observedAt} /> : "Not yet" },
                  {
                    term: "Trust",
                    value: cluster.trust.trustedAt ? (
                      <>
                        {cluster.trust.untrusted.length > 0 || cluster.trust.codeChanged || cluster.trust.certificates.some((c) => !c.trusted) ? (
                          <Badge tone="warning">Changed</Badge>
                        ) : (
                          <Badge tone="success">Trusted</Badge>
                        )}{" "}
                        by {cluster.trust.trustedBy} · <RelativeTime value={cluster.trust.trustedAt} />
                      </>
                    ) : (
                      <Badge tone="warning">Not trusted</Badge>
                    ),
                  },
                ]}
              />
            </Card>
            <Card title="Policy">
              <PolicyView policy={data.policy} />
            </Card>
          </div>
        </div>
      </div>
    </>
  );
}
