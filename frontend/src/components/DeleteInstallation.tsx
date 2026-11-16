import { Trash2 } from "lucide-react";
import { useState } from "react";
import { useNavigate } from "react-router";
import { useDeleteInstallation } from "../api/queries";
import type { InstallationSummary } from "../api/types";
import { ConfirmDialog } from "./ConfirmDialog";

export function DeleteInstallation({ installation }: { installation: InstallationSummary }) {
  const [open, setOpen] = useState(false);
  const [address, setAddress] = useState("");
  const deletion = useDeleteInstallation();
  const navigate = useNavigate();
  return (
    <>
      <button type="button" className="button button--danger button--small" onClick={() => {
        setAddress("");
        deletion.reset();
        setOpen(true);
      }}>
        <Trash2 size={15} aria-hidden="true" />
        Force delete server
      </button>
      <ConfirmDialog
        open={open}
        title={`Force delete ${installation.name}?`}
        confirmLabel="Delete stored data"
        danger
        pending={deletion.isPending}
        disabled={address !== installation.address}
        error={deletion.error}
        onClose={() => { if (!deletion.isPending) setOpen(false); }}
        onConfirm={() => deletion.mutate({ id: installation.id, address }, {
          onSuccess: () => { setOpen(false); navigate("/inventory", { replace: true }); },
        })}
      >
        <p>
          Permanently delete this entire installation and all its nodes, VMs, containers, services, runs, logs,
          OpenTofu state, observations, uptime history, and Octopushy-managed keys from Octopushy.
          This cannot be undone.
        </p>
        <p>
          This does not connect to Proxmox or destroy anything on the server. User variables and shared credentials stay.
          Remove the installation's folder from Git and sync it first so it will not be imported again.
        </p>
        <label className="field">
          <span className="field__label">Type {installation.address} to confirm</span>
          <input value={address} onChange={(event) => setAddress(event.target.value)} autoComplete="off" spellCheck={false} />
        </label>
      </ConfirmDialog>
    </>
  );
}
