import { KeyRound, Lock, Pencil, Plus, Trash2 } from "lucide-react";
import { useState } from "react";
import { useDeleteVariable, useVariables } from "../api/queries";
import type { Variable } from "../api/types";
import { Badge } from "../components/Badge";
import { Card } from "../components/Card";
import { ConfirmDialog } from "../components/ConfirmDialog";
import { PageHeader } from "../components/PageHeader";
import { RelativeTime } from "../components/RelativeTime";
import { EmptyState, ErrorMessage, PageSkeleton } from "../components/States";
import { EmptyRow, Table } from "../components/Table";
import { VariableDialog } from "./variables/VariableDialog";

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
          {data.missing.length > 0 ? (
            <Card title="Referenced but not set" meta={data.missing.length} className="card--warning" flush>
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
                  {data.missing.map((m) => (
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
          <Card title="Entries" meta={data.entries.length || undefined} flush>
            {data.entries.length === 0 ? (
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
                    <th scope="col">Version</th>
                    <th scope="col">Updated</th>
                    <th scope="col">Used by</th>
                    <th scope="col">
                      <span className="visually-hidden">Actions</span>
                    </th>
                  </tr>
                </thead>
                <tbody>
                  {data.entries.map((v) => (
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
                      <td className="num">{v.version}</td>
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
          <Card title="System" meta={data.system.length || undefined}>
            <p className="muted small">Octopushy's own credentials, such as the agent CA key and the repository credential. Managed by the server; definitions cannot reference them.</p>
            <Table label="System keys">
              <thead>
                <tr>
                  <th scope="col">Key</th>
                  <th scope="col">Visibility</th>
                  <th scope="col">Updated</th>
                </tr>
              </thead>
              <tbody>
                {data.system.length === 0 ? <EmptyRow colSpan={3}>None yet.</EmptyRow> : null}
                {data.system.map((s) => (
                  <tr key={s.key}>
                    <td className="mono">{s.key}</td>
                    <td className="muted">{s.visibility}</td>
                    <td className="nowrap">
                      <RelativeTime value={s.updatedAt} />
                    </td>
                  </tr>
                ))}
              </tbody>
            </Table>
          </Card>
        </div>
      )}
      {editing ? <VariableDialog existing={editing.existing} presetKey={editing.presetKey} onClose={() => setEditing(null)} /> : null}
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
