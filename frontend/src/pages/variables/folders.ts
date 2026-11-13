import type { Variable } from "../../api/types";
import { buildTree, parentFolders, type TreeNode } from "../../lib/fileTree";

export interface Folder {
  name: string;
  path: string;
  /** Entries at any depth below the folder; the two counts after it cover the same entries. */
  entries: number;
  /** Entries whose active version is revoked. */
  revoked: number;
  /** Entries with a version newer than the active one that is not revoked. */
  newer: number;
}

export interface FolderListing {
  /** The folder shown: the one requested, or its deepest ancestor that still exists. */
  path: string;
  folders: Folder[];
  entries: Variable[];
  /** Entries at any depth below the folder. */
  total: number;
}

/**
 * One folder of the store. Folders are the "/" prefixes keys already share: nothing stores
 * them, so a folder exists exactly while a key below it does, and a key may be a folder too.
 */
export function listFolder(all: Variable[], requested: string): FolderListing {
  const byKey = new Map(all.map((entry) => [entry.key, entry]));
  const below = (nodes: TreeNode[]): Variable[] =>
    nodes.flatMap((node) => (node.children ? below(node.children) : [byKey.get(node.path)!]));

  let path = "";
  let nodes = buildTree(all.map((entry) => entry.key));
  for (const name of requested.split("/")) {
    const next = nodes.find((node) => node.children && node.name === name);
    if (!next) break;
    path = next.path;
    nodes = next.children!;
  }

  return {
    path,
    folders: nodes
      .filter((node) => node.children)
      .map((node) => {
        const entries = below(node.children!);
        return {
          name: node.name,
          path: node.path,
          entries: entries.length,
          revoked: entries.filter((entry) => entry.revoked).length,
          newer: entries.filter((entry) => entry.newerVersion).length,
        };
      }),
    entries: nodes.filter((node) => !node.children).map((node) => byKey.get(node.path)!),
    total: below(nodes).length,
  };
}

/** The folders on the way to a path, outermost first: "a/b" → ["a", "a/b"]. */
export function foldersOn(path: string): string[] {
  return path ? parentFolders(`${path}/`) : [];
}

export function lastSegment(path: string): string {
  return path.slice(path.lastIndexOf("/") + 1);
}

/** A link to a folder; "" is the root, and any folder link opens the folder view. */
export function folderHref(path: string): string {
  return `/variables?folder=${encodeURIComponent(path).replaceAll("%2F", "/")}`;
}

/** A link that opens an entry, keeping the folder being browsed; null keeps the stored view. */
export function entryHref(key: string, folder: string | null): string {
  const query = new URLSearchParams();
  if (folder !== null) query.set("folder", folder);
  query.set("key", key);
  return `/variables?${query.toString().replaceAll("%2F", "/")}`;
}

/** The folder a key sits in: "a/b/c" → "a/b", "solo" → "". */
export function parentFolder(key: string): string {
  return parentFolders(key).at(-1) ?? "";
}

export interface TreeFolder {
  name: string;
  path: string;
  /** Entries at any depth below it. */
  count: number;
  folders: TreeFolder[];
}

/** Every folder of the store, nested, with how many entries each holds at any depth. */
export function folderTree(all: Variable[]): TreeFolder[] {
  const walk = (nodes: TreeNode[]): TreeFolder[] =>
    nodes
      .filter((node) => node.children)
      .map((node) => ({ name: node.name, path: node.path, count: leaves(node.children!), folders: walk(node.children!) }));
  const leaves = (nodes: TreeNode[]): number => nodes.reduce((n, node) => n + (node.children ? leaves(node.children) : 1), 0);
  return walk(buildTree(all.map((entry) => entry.key)));
}

/** A link to the folder that lists a key. */
export function folderLink(key: string): string {
  return folderHref(parentFolders(key).at(-1) ?? "");
}

/** A lowercase substring of the key, description, or a consumer. Values are never searched. */
export function matchesSearch(entry: Variable, query: string): boolean {
  return [entry.key, entry.description ?? "", ...entry.usedBy].some((text) => text.toLowerCase().includes(query));
}
