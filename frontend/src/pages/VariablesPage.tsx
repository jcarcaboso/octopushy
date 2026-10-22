import { History, KeyRound, Lock, Pencil, Plus, Trash2 } from "lucide-react";
import { useState } from "react";
import { useDeleteVariable, useVariables } from "../api/queries";
import type { Variable } from "../api/types";
import { Badge } from "../components/Badge";
import { Card } from "../components/Card";
import { ConfirmDialog } from "../components/ConfirmDialog";
import { PageHeader } from "../components/PageHeader";
import { RelativeTime } from "../components/RelativeTime";
import { EmptyState, ErrorMessage, PageSkeleton } from "../components/States";
import { Table } from "../components/Table";
import { VariableDialog } from "./variables/VariableDialog";
import { VariableVersionsDialog } from "./variables/VariableVersionsDialog";

type Editing = { existing: Variable | null; presetKey?: string } | null;

function UsedBy({ usedBy }: { usedBy: string[] }) {
  if (usedBy.length === 0) return <span className="muted">Not referenced</span>;
  return (
    <ul className="used-by">
      {usedBy.map((u) => (
        <li key={u} className="mono small">
          {u}
        </li>
      ))}
    </ul>
  );
}

export function VariablesPage() {
  const { data, error, isPending } = useVariables();
  const remove = useDeleteVariable();
  const [editing, setEditing] = useState<Editing>(null);
  const [deleting, setDeleting] = useState<Variable | null>(null);
  const [managing, setManaging] = useState<string | null>(null);
  const entries = data?.entries.filter((entry) => !entry.key.startsWith("system/")) ?? [];
  const missing = data?.missing.filter((entry) => !entry.key.startsWith("system/")) ?? [];
  const managed = entries.find((entry) => entry.key === managing);

  const setButton = (
    <button type="button" className="button button--primary" onClick={() => setEditing({ existing: null })}>
      <Plus size={15} aria-hidden="true" />
      Set a variable
    </button>
  );

  return (
    <>
      <PageHeader
        title="Variables"
        meta="The variable store: values kept outside Git, referenced from definitions by key."
        actions={setButton}
      />
      {isPending ? (
        <PageSkeleton rows={2} />
      ) : !data ? (
        <ErrorMessage error={error} title="The variables could not be loaded" />
      ) : (
        <div className="stack stack--large">
          {missing.length > 0 ? (
            <Card title="Referenced but not set" meta={missing.length} className="card--warning" flush>
              <Table label="Referenced keys that are not set">
                <thead>
                  <tr>
                    <th scope="col">Key</th>
                    <th scope="col">Used by</th>
                    <th scope="col">
                      <span className="visually-hidden">Actions</span>
                    </th>
                  </tr>
                </thead>
                <tbody>
                  {missing.map((m) => (
                    <tr key={m.key}>
                      <th scope="row" className="mono">
                        {m.key}
                      </th>
                      <td>
                        <UsedBy usedBy={m.usedBy} />
                      </td>
                      <td className="cell-actions">
                        <button type="button" className="button button--small button--primary" onClick={() => setEditing({ existing: null, presetKey: m.key })}>
                          Set
                        </button>
                      </td>
                    </tr>
                  ))}
                </tbody>
              </Table>
            </Card>
          ) : null}
          <Card title="Entries" meta={entries.length || undefined} flush>
            {entries.length === 0 ? (
              <EmptyState icon={KeyRound} title="The store is empty" actions={setButton}>
                Set the Proxmox tokens and state-backend credentials your definitions reference.
              </EmptyState>
            ) : (
              <Table label="Variable store entries" className="table--variables">
                <thead>
                  <tr>
                    <th scope="col">Key</th>
                    <th scope="col">Visibility</th>
                    <th scope="col">Description</th>
                    <th scope="col">Value</th>
                    <th scope="col">Active version</th>
                    <th scope="col">Updated</th>
                    <th scope="col">Used by</th>
                    <th scope="col">
                      <span className="visually-hidden">Actions</span>
                    </th>
                  </tr>
                </thead>
                <tbody>
                  {entries.map((v) => (
                    <tr key={v.key}>
                      <th scope="row" className="mono">
                        {v.key}
                      </th>
                      <td>
                        {v.visibility === "secret" ? (
                          <Badge tone="attention" icon={Lock}>
                            Secret
                          </Badge>
                        ) : (
                          <Badge tone="neutral">Plain</Badge>
                        )}
                      </td>
                      <td>{v.description ?? <span className="muted">—</span>}</td>
                      <td className="cell-value">
                        {v.visibility === "plain" ? (
                          <code className="mono value-preview" title={v.value ?? ""}>
                            {v.value}
                          </code>
                        ) : (
                          <span className="muted">Concealed</span>
                        )}
                      </td>
                      <td className="num">
                        v{v.version}
                        {v.latestVersion > v.version ? <div className="muted small">Latest: v{v.latestVersion}</div> : null}
                        {v.revoked ? <Badge tone="danger">Revoked</Badge> : null}
                      </td>
                      <td className="nowrap">
                        <RelativeTime value={v.updatedAt} />
                      </td>
                      <td>
                        <UsedBy usedBy={v.usedBy} />
                      </td>
                      <td className="cell-actions">
                        <div className="button-row button-row--tight">
                          <button type="button" className="button button--small" onClick={() => setEditing({ existing: v })} aria-label={`Update ${v.key}`}>
                            <Pencil size={14} aria-hidden="true" />
                            Update
                          </button>
                          {v.visibility === "secret" ? (
                            <button type="button" className="button button--small" onClick={() => setManaging(v.key)} aria-label={`Versions of ${v.key}`}>
                              <History size={14} aria-hidden="true" />
                              Versions
                            </button>
                          ) : null}
                          <button
                            type="button"
                            className="button button--small button--danger-outline"
                            onClick={() => setDeleting(v)}
                            aria-label={`Delete ${v.key}`}
                          >
                            <Trash2 size={14} aria-hidden="true" />
                            Delete
                          </button>
                        </div>
                      </td>
                    </tr>
                  ))}
                </tbody>
              </Table>
            )}
          </Card>
        </div>
      )}
      {editing ? <VariableDialog existing={editing.existing} presetKey={editing.presetKey} onClose={() => setEditing(null)} /> : null}
      {managed ? <VariableVersionsDialog existing={managed} onClose={() => setManaging(null)} /> : null}
      <ConfirmDialog
        open={deleting !== null}
        title={`Delete ${deleting?.key ?? ""}?`}
        confirmLabel="Delete"
        danger
        pending={remove.isPending}
        error={remove.error}
        onClose={() => {
          setDeleting(null);
          remove.reset();
        }}
        onConfirm={() => deleting && remove.mutate(deleting.key, { onSuccess: () => setDeleting(null) })}
      >
        <p>Every version of the value is removed. A key that definitions or runs still use cannot be deleted.</p>
      </ConfirmDialog>
    </>
  );
}
