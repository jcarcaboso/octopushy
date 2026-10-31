import { ChevronDown, ChevronRight, File, FileCode, Folder, FolderOpen } from "lucide-react";
import { useMemo, useState, type CSSProperties } from "react";
import type { DraftChange, DraftFile } from "../../api/types";
import { buildTree, parentFolders, type TreeNode } from "../../lib/fileTree";

const changeLabel: Record<string, { letter: string; label: string }> = {
  added: { letter: "A", label: "Added" },
  modified: { letter: "M", label: "Modified" },
  deleted: { letter: "D", label: "Deleted" },
};

interface Props {
  files: DraftFile[];
  changes: DraftChange[];
  dirty: string[];
  conflicts: string[];
  selected: string | null;
  onSelect: (path: string) => void;
}

/** The draft's files as folders, with what changed marked; a filter lists matching paths flat. */
export function FileTree({ files, changes, dirty, conflicts, selected, onSelect }: Props) {
  const [filter, setFilter] = useState("");
  const tree = useMemo(() => buildTree(files.map((f) => f.path)), [files]);
  const changed = useMemo(() => new Map(changes.map((c) => [c.path, c.change])), [changes]);
  const [open, setOpen] = useState<Set<string>>(
    () => new Set([...changes.map((c) => c.path), ...(selected ? [selected] : [])].flatMap(parentFolders)),
  );
  const toggle = (path: string) =>
    setOpen((current) => {
      const next = new Set(current);
      if (next.has(path)) next.delete(path);
      else next.add(path);
      return next;
    });

  const marks = (path: string) => (
    <>
      {conflicts.includes(path) ? (
        <span className="file-tree__mark file-tree__mark--conflict" title="Has conflicts to resolve">
          !
        </span>
      ) : null}
      {dirty.includes(path) ? (
        <span className="file-tree__mark file-tree__mark--dirty" title="Unsaved edits">
          ●
        </span>
      ) : null}
      {changed.has(path) ? (
        <span className={`file-tree__mark file-tree__mark--${changed.get(path)}`} title={changeLabel[changed.get(path)!]?.label}>
          {changeLabel[changed.get(path)!]?.letter}
        </span>
      ) : null}
    </>
  );

  const fileButton = (path: string, label: string, depth: number) => (
    <button
      type="button"
      className={path === selected ? "file-tree__item file-tree__item--selected" : "file-tree__item"}
      style={{ "--depth": depth } as CSSProperties}
      aria-current={path === selected ? "true" : undefined}
      onClick={() => onSelect(path)}
      title={path}
    >
      {/\.(ya?ml|json|tf|nix|sh|toml|md)$/i.test(path) ? <FileCode size={15} aria-hidden="true" /> : <File size={15} aria-hidden="true" />}
      <span className="file-tree__name">{label}</span>
      {marks(path)}
    </button>
  );

  const render = (nodes: TreeNode[], depth: number) =>
    nodes.map((node) =>
      node.children ? (
        <li key={node.path}>
          <button
            type="button"
            className="file-tree__item file-tree__item--folder"
            style={{ "--depth": depth } as CSSProperties}
            aria-expanded={open.has(node.path)}
            onClick={() => toggle(node.path)}
          >
            {open.has(node.path) ? <ChevronDown size={14} aria-hidden="true" /> : <ChevronRight size={14} aria-hidden="true" />}
            {open.has(node.path) ? <FolderOpen size={15} aria-hidden="true" /> : <Folder size={15} aria-hidden="true" />}
            <span className="file-tree__name">{node.name}</span>
          </button>
          {open.has(node.path) ? <ul className="file-tree__list">{render(node.children, depth + 1)}</ul> : null}
        </li>
      ) : (
        <li key={node.path}>{fileButton(node.path, node.name, depth)}</li>
      ),
    );

  const query = filter.trim().toLowerCase();
  const matches = query ? files.filter((f) => f.path.toLowerCase().includes(query)).slice(0, 200) : [];

  return (
    <div className="file-tree">
      <input
        type="search"
        className="file-tree__filter"
        placeholder="Filter files"
        aria-label="Filter files"
        value={filter}
        onChange={(e) => setFilter(e.target.value)}
      />
      {query ? (
        matches.length === 0 ? (
          <p className="muted small">No file matches.</p>
        ) : (
          <ul className="file-tree__list">
            {matches.map((f) => (
              <li key={f.path}>{fileButton(f.path, f.path, 0)}</li>
            ))}
          </ul>
        )
      ) : (
        <ul className="file-tree__list" aria-label="Repository files">
          {render(tree, 0)}
        </ul>
      )}
    </div>
  );
}
