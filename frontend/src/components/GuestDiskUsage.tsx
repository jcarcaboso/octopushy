import { useState } from "react";
import { useCreateDiskReporter, useRevokeDiskReporter } from "../api/queries";
import type { GuestDiskUsage as DiskUsage } from "../api/types";
import { formatBytes } from "../lib/format";
import { Badge } from "./Badge";
import { Callout } from "./Callout";
import { Card } from "./Card";
import { CodeBlock, JsonBlock } from "./CodeBlock";
import { ConfirmDialog } from "./ConfirmDialog";
import { Dialog } from "./Dialog";
import { KeyValue } from "./KeyValue";
import { Meter } from "./Meter";
import { RelativeTime } from "./RelativeTime";
import { ErrorMessage } from "./States";
import { useNow } from "./useNow";

export function GuestDiskUsage({ id, usage, inactive }: { id: string; usage: DiskUsage; inactive: boolean }) {
  const now = useNow();
  const [configuring, setConfiguring] = useState(false);
  const [revoking, setRevoking] = useState(false);
  const [server, setServer] = useState(window.location.origin);
  const [minutes, setMinutes] = useState(String(usage.intervalSeconds / 60));
  const create = useCreateDiskReporter(id);
  const revoke = useRevokeDiskReporter(id);
  const stale = usage.staleAfter !== null && now >= Date.parse(usage.staleAfter);
  const used = usage.usedBytes;
  const capacity = usage.configuredBytes;
  const fits = used !== null && capacity !== null && used <= capacity;
  const validMinutes = Number.isInteger(Number(minutes)) && Number(minutes) >= 1 && Number(minutes) <= 10080;
  let serverUrl: URL | null = null;
  try {
    const url = new URL(server);
    if (url.protocol === "https:" && !url.username && !url.password && !url.search && !url.hash && url.pathname === "/") serverUrl = url;
  } catch { /* The field explains the expected HTTPS origin. */ }

  function close() {
    setConfiguring(false);
    create.reset();
  }

  return (
    <Card title="System disk">
      <div className="stack">
        <div className="inline-group">
          <span className="mono">/</span>
          {!usage.enabled ? <Badge tone="neutral">Reporting disabled</Badge> : stale ? <Badge tone="warning">Stale report</Badge> : null}
        </div>
        {used === null ? (
          <p className="muted">{usage.enabled ? "Waiting for the first report." : "No disk usage reported. If automatic reporting is enabled on the server, provisioning installs it after verification; use Plan now to install or repair it on an existing VM."}</p>
        ) : (
          <>
            <KeyValue items={[
              { term: "Used in root filesystem", value: formatBytes(used) },
              { term: "Configured system disk", value: formatBytes(capacity) },
              { term: "Configured capacity remaining", value: fits ? formatBytes(capacity - used) : "—" },
              { term: "Last received", value: <RelativeTime value={usage.receivedAt} /> },
              { term: "Reporting interval", value: `${usage.intervalSeconds / 60} minutes` },
            ]} />
            {fits ? <Meter label="Used versus configured system disk capacity" fraction={used / capacity} /> : null}
            {capacity !== null && used > capacity ? (
              <Callout kind="warning" title="Usage exceeds configured capacity">
                Check the disk definition and root filesystem layout.
              </Callout>
            ) : null}
          </>
        )}
        <p className="muted small">
          Capacity comes from the first disk in the current guest definition. Remaining capacity is an estimate,
          not filesystem-available space. Separate filesystems and data disks are not measured.
        </p>
        {!inactive ? (
          <div className="inline-group">
            <button type="button" className="button" onClick={() => setConfiguring(true)}>Configure reporter</button>
            {usage.enabled ? <button type="button" className="button" onClick={() => setRevoking(true)}>Revoke token</button> : null}
          </div>
        ) : null}
      </div>
      <Dialog open={configuring} onClose={close} title="Install disk reporter">
        <div className="stack">
          {create.data && serverUrl ? (
            <>
              <Callout kind="warning" title="Save this token now">
                It is shown once and can only submit this VM's disk usage. Any previous token has been revoked.
                Keep it out of Git and shared VM images.
              </Callout>
              <JsonBlock label="config.json" value={{
                endpoint: new URL(create.data.reportPath, serverUrl).href,
                intervalSeconds: create.data.intervalSeconds,
              }} />
              <CodeBlock label="token (save as a file, mode 0600)" code={create.data.token} wrap />
              <p>
                Copy the release binary and <code>agent/packaging/disk-reporter/</code> files to the VM.
                Save the two files above alongside them, then run:
              </p>
              <CodeBlock code="sudo sh install.sh ./octopushy-disk-reporter ./config.json ./token" wrap />
              <p className="muted small">
                The installer enables the timer. Updating the interval requires creating a new configuration
                and running the installer again. No process stays running between reports.
              </p>
              <button type="button" className="button" onClick={close}>Done</button>
            </>
          ) : (
            <form className="form" onSubmit={(event) => {
              event.preventDefault();
              if (serverUrl && validMinutes) create.mutate(Number(minutes) * 60);
            }}>
              <p>
                A one-shot Linux reporter measures the root filesystem and pushes used bytes over HTTPS.
                This is the manual installation option; automatic provisioning does not need this form.
              </p>
              {usage.enabled ? <Callout kind="warning" title="This replaces the current token">Reinstall the configuration on the VM after generating a new token.</Callout> : null}
              <div className="field">
                <label htmlFor="disk-server">Octopushy HTTPS address</label>
                <input id="disk-server" type="url" value={server} onChange={(event) => setServer(event.target.value)}
                  placeholder="https://octopushy.example" aria-invalid={!serverUrl || undefined} disabled={create.isPending} required />
                <span className="field__hint">The VM must be able to reach this address. Use the web/API address, not the execution-agent port.</span>
              </div>
              <div className="field">
                <label htmlFor="disk-interval">Reporting interval (minutes)</label>
                <input id="disk-interval" type="number" min={1} max={10080} step={1} value={minutes}
                  onChange={(event) => setMinutes(event.target.value)} disabled={create.isPending}
                  aria-invalid={!validMinutes || undefined} required />
              </div>
              <ErrorMessage error={create.error} />
              <button type="submit" className="button button--primary" disabled={create.isPending || !serverUrl || !validMinutes}>
                {create.isPending ? "Creating…" : "Create reporting token"}
              </button>
            </form>
          )}
        </div>
      </Dialog>
      <ConfirmDialog open={revoking} onClose={() => setRevoking(false)} title="Revoke reporting token?"
        confirmLabel="Revoke token" danger pending={revoke.isPending} error={revoke.error}
        onConfirm={() => revoke.mutate(undefined, { onSuccess: () => setRevoking(false) })}>
        <p>New reports will be rejected. The last reading remains visible; disable the timer on the VM if it is no longer needed.</p>
        <p>Automatic provisioning can enable reporting again. To keep it disabled, set <code>setup.diskReporter.enabled: false</code> in the guest definition too.</p>
      </ConfirmDialog>
    </Card>
  );
}
