import { Eye, EyeOff, Folder as FolderIcon, History, Lock, Pencil, Trash2 } from "lucide-react";
import { Link } from "react-router";
import type { Variable } from "../../api/types";
import { Badge } from "../../components/Badge";
import { CopyButton } from "../../components/CopyButton";
import { RelativeTime } from "../../components/RelativeTime";
import { Table } from "../../components/Table";
import { folderHref, lastSegment, type Folder } from "./folders";

export function UsedBy({ usedBy }: { usedBy: string[] }) {
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

function FolderRow({ folder }: { folder: Folder }) {
  return (
    <tr>
      <th scope="row" className="mono">
        <Link to={folderHref(folder.path)} className="folder-link">
          <FolderIcon size={15} aria-hidden="true" />
          {folder.name}
        </Link>
      </th>
      <td colSpan={7}>
        <div className="folder-summary">
          <span className="muted">{folder.entries === 1 ? "1 entry" : `${folder.entries} entries`}</span>
          {folder.revoked > 0 ? <Badge tone="danger">{folder.revoked} revoked</Badge> : null}
          {folder.newer > 0 ? <Badge tone="warning">{folder.newer} with a newer version</Badge> : null}
        </div>
      </td>
    </tr>
  );
}

interface Props {
  /** Listed first; a key that is also a folder appears as both. */
  folders?: Folder[];
  entries: Variable[];
  /** Show entries by name, for a folder whose path is already on screen, instead of by full key. */
  byName?: boolean;
  onUpdate: (entry: Variable) => void;
  onVersions: (entry: Variable) => void;
  onDelete: (entry: Variable) => void;
  /** A secret's value once revealed: null when it has none that may be used, undefined while concealed. */
  revealedValue: (entry: Variable) => string | null | undefined;
  /** Keys whose values are being fetched. */
  revealing: ReadonlySet<string>;
  onReveal: (entry: Variable) => void;
  onHide: (entry: Variable) => void;
}

function SecretValue({ entry, value, pending, onReveal, onHide }: {
  entry: Variable;
  value: string | null | undefined;
  pending: boolean;
  onReveal: () => void;
  onHide: () => void;
}) {
  if (value === undefined) {
    return (
      <span className="secret-value">
        <span className="muted">Concealed</span>
        <button type="button" className="icon-button" onClick={onReveal} disabled={pending} aria-label={`Reveal ${entry.key}`} title="Reveal">
          <Eye size={14} aria-hidden="true" />
        </button>
      </span>
    );
  }
  return (
    <span className="secret-value">
      {value === null ? (
        <span className="muted">No usable version</span>
      ) : (
        <>
          <code className="mono value-preview" title={value}>
            {value}
          </code>
          <CopyButton text={value} label={`Copy the value of ${entry.key}`} compact />
        </>
      )}
      <button type="button" className="icon-button" onClick={onHide} aria-label={`Hide ${entry.key}`} title="Hide">
        <EyeOff size={14} aria-hidden="true" />
      </button>
    </span>
  );
}

export function EntriesTable({ folders = [], entries, byName = false, onUpdate, onVersions, onDelete, revealedValue, revealing, onReveal, onHide }: Props) {
  return (
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
        {folders.map((folder) => (
          <FolderRow key={`folder:${folder.path}`} folder={folder} />
        ))}
        {entries.map((v) => (
          <tr key={`entry:${v.key}`}>
            <th scope="row" className="mono">
              <span className="entry-key">
                <span title={v.key}>{byName ? lastSegment(v.key) : v.key}</span>
                <CopyButton text={v.key} label={`Copy ${v.key}`} compact />
              </span>
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
                <SecretValue
                  entry={v}
                  value={revealedValue(v)}
                  pending={revealing.has(v.key)}
                  onReveal={() => onReveal(v)}
                  onHide={() => onHide(v)}
                />
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
                <button type="button" className="button button--small" onClick={() => onUpdate(v)} aria-label={`Update ${v.key}`}>
                  <Pencil size={14} aria-hidden="true" />
                  Update
                </button>
                {v.visibility === "secret" ? (
                  <button type="button" className="button button--small" onClick={() => onVersions(v)} aria-label={`Versions of ${v.key}`}>
                    <History size={14} aria-hidden="true" />
                    Versions
                  </button>
                ) : null}
                <button
                  type="button"
                  className="button button--small button--danger-outline"
                  onClick={() => onDelete(v)}
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
  );
}
