export interface TreeNode {
  name: string;
  path: string;
  /** Present on folders only. */
  children?: TreeNode[];
}

/** Folders and files from repository paths; folders first, then by name. */
export function buildTree(paths: string[]): TreeNode[] {
  const root: TreeNode = { name: "", path: "", children: [] };
  for (const path of paths) {
    let node = root;
    const parts = path.split("/");
    parts.forEach((name, index) => {
      const isFile = index === parts.length - 1;
      const childPath = parts.slice(0, index + 1).join("/");
      let child = node.children!.find((c) => c.name === name && !c.children === isFile);
      if (!child) {
        child = isFile ? { name, path: childPath } : { name, path: childPath, children: [] };
        node.children!.push(child);
      }
      node = child;
    });
  }
  const sort = (nodes: TreeNode[]) => {
    nodes.sort((a, b) => Number(!a.children) - Number(!b.children) || a.name.localeCompare(b.name));
    for (const n of nodes) if (n.children) sort(n.children);
  };
  sort(root.children!);
  return root.children!;
}

/** The folders that contain a path, outermost first: "a/b/c.txt" → ["a", "a/b"]. */
export function parentFolders(path: string): string[] {
  const parts = path.split("/").slice(0, -1);
  return parts.map((_, i) => parts.slice(0, i + 1).join("/"));
}
