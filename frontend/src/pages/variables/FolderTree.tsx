import { ChevronRight, Folder } from "lucide-react";
import { useState } from "react";
import { GuardLink } from "./GuardLink";
import { folderHref, type TreeFolder } from "./folders";

interface Props {
  tree: TreeFolder[];
  total: number;
  /** The folder being browsed. */
  current: string;
  onFollow?: () => void;
}

const within = (path: string, current: string) => current === path || current.startsWith(`${path}/`);

/** Every folder, nested. The folders on the way to the current one start open. */
export function FolderTree({ tree, total, current, onFollow }: Props) {
  const [toggled, setToggled] = useState<ReadonlyMap<string, boolean>>(new Map());
  const isOpen = (path: string) => toggled.get(path) ?? within(path, current);

  function row(name: string, path: string, count: number, depth: number, children: TreeFolder[]) {
    const open = children.length > 0 && isOpen(path);
    return (
      <li key={path || "/"}>
        <div className="vars-tree__row" style={{ paddingLeft: `${depth * 0.875}rem` }}>
          {children.length > 0 ? (
            <button
              type="button"
              className="vars-tree__toggle"
              aria-expanded={open}
              aria-label={`${open ? "Collapse" : "Expand"} ${name}`}
              onClick={() => setToggled((now) => new Map(now).set(path, !open))}
            >
              <ChevronRight size={14} aria-hidden="true" />
            </button>
          ) : (
            <span className="vars-tree__spacer" />
          )}
          <GuardLink to={folderHref(path)} className="vars-tree__link" aria-current={current === path ? "page" : undefined} onFollow={onFollow}>
            <Folder size={14} aria-hidden="true" />
            <span className="vars-tree__name">{name}</span>{" "}
            <span className="vars-tree__count">{count}</span>
          </GuardLink>
        </div>
        {open ? <ul>{children.map((child) => row(child.name, child.path, child.count, depth + 1, child.folders))}</ul> : null}
      </li>
    );
  }

  return (
    <ul className="vars-tree__list">
      <li>
        <div className="vars-tree__row">
          <span className="vars-tree__spacer" />
          <GuardLink to={folderHref("")} className="vars-tree__link" aria-current={current === "" ? "page" : undefined} onFollow={onFollow}>
            <Folder size={14} aria-hidden="true" />
            <span className="vars-tree__name">Entries</span>{" "}
            <span className="vars-tree__count">{total}</span>
          </GuardLink>
        </div>
      </li>
      {tree.map((node) => row(node.name, node.path, node.count, 0, node.folders))}
    </ul>
  );
}
