import { describe, expect, it } from "vitest";
import { buildTree, parentFolders } from "./fileTree";

describe("buildTree", () => {
  it("nests paths with folders before files", () => {
    const tree = buildTree(["octopushy.yaml", "labs/home/lab.yaml", "labs/home/proxmox/a.yaml", "README.md"]);
    expect(tree.map((n) => n.name)).toEqual(["labs", "octopushy.yaml", "README.md"]);
    const home = tree[0].children![0];
    expect(home.path).toBe("labs/home");
    expect(home.children!.map((n) => [n.name, Boolean(n.children)])).toEqual([
      ["proxmox", true],
      ["lab.yaml", false],
    ]);
  });
});

describe("parentFolders", () => {
  it("lists every containing folder", () => {
    expect(parentFolders("a/b/c.txt")).toEqual(["a", "a/b"]);
    expect(parentFolders("top.txt")).toEqual([]);
  });
});
