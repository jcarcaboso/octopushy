import { Power } from "lucide-react";
import { useState } from "react";
import { useWakeHost } from "../api/queries";
import type { ObservedHost } from "../api/types";
import { plural } from "../lib/format";
import { canWake } from "../lib/wake";
import { ConfirmDialog } from "./ConfirmDialog";

/** Wake for a host that may need waking (see canWake); nothing otherwise. */
export function WakeHostButton({ installationId, host, observeFailed, small }: {
  installationId: string;
  host: ObservedHost;
  observeFailed: boolean;
  small?: boolean;
}) {
  const [open, setOpen] = useState(false);
  const wake = useWakeHost();
  if (!canWake(host, observeFailed)) return null;
  const close = () => {
    setOpen(false);
    wake.reset();
  };
  return (
    <>
      <button type="button" className={small ? "button button--small" : "button"} onClick={() => setOpen(true)}>
        <Power size={small ? 13 : 15} aria-hidden="true" />
        Wake {host.name}
      </button>
      <ConfirmDialog
        open={open}
        title={`Wake ${host.name}?`}
        confirmLabel="Send magic packet"
        pending={wake.isPending}
        disabled={wake.isSuccess}
        error={wake.error}
        onConfirm={() => wake.mutate({ id: installationId, host: host.name })}
        onClose={close}
      >
        {wake.data ? (
          <p>
            Sent through {plural(wake.data.agents, "agent")}. If {host.name} is set up for Wake-on-LAN, it boots and shows as online at
            the next observation.
          </p>
        ) : (
          <p>
            Every connected agent sends a Wake-on-LAN packet for <span className="mono">{host.wakeOnLanMac}</span>. An agent on the
            host's network segment wakes it.
          </p>
        )}
      </ConfirmDialog>
    </>
  );
}
