import { describe, expect, it } from "vitest";
import type { Variable } from "../../api/types";
import { folderHref, folderLink, foldersOn, lastSegment, listFolder, matchesSearch } from "./folders";

function entry(key: string, overrides: Partial<Variable> = {}): Variable {
  return {
    key,
    visibility: "secret",
    description: null,
    version: 1,
    latestVersion: 1,
    revoked: false,
    updatedAt: "2026-10-20T12:00:00Z",
    value: null,
    usedBy: [],
    ...overrides,
  };
}

describe("listFolder", () => {
  const all = [
    entry("proxmox/a/token", { latestVersion: 2 }),
    entry("proxmox/a/ca"),
    entry("proxmox/b/deep/token", { revoked: true }),
    entry("proxmox/note"),
    entry("solo"),
  ];

  it("lists the root's folders and the keys with no folder", () => {
    const root = listFolder(all, "");
    expect(root.path).toBe("");
    expect(root.total).toBe(5);
    expect(root.folders).toEqual([{ name: "proxmox", path: "proxmox", entries: 4, revoked: 1, newer: 1 }]);
    expect(root.entries.map((e) => e.key)).toEqual(["solo"]);
  });

  it("lists one level and rolls up every depth below it", () => {
    const folder = listFolder(all, "proxmox");
    expect(folder.total).toBe(4);
    expect(folder.folders).toEqual([
      { name: "a", path: "proxmox/a", entries: 2, revoked: 0, newer: 1 },
      { name: "b", path: "proxmox/b", entries: 1, revoked: 1, newer: 0 },
    ]);
    expect(folder.entries.map((e) => e.key)).toEqual(["proxmox/note"]);
  });

  it("falls back to the deepest ancestor that exists", () => {
    expect(listFolder(all, "proxmox/a/gone").path).toBe("proxmox/a");
    expect(listFolder(all, "gone/a").path).toBe("");
  });

  it("does not treat a key as a folder", () => {
    expect(listFolder(all, "solo").path).toBe("");
    expect(listFolder(all, "proxmox/note").path).toBe("proxmox");
  });

  it("keeps a key that is also a folder as both", () => {
    const folder = listFolder([entry("a/b"), entry("a/b/c")], "a");
    expect(folder.folders.map((f) => f.path)).toEqual(["a/b"]);
    expect(folder.entries.map((e) => e.key)).toEqual(["a/b"]);
    expect(folder.total).toBe(2);
  });

  it("treats case as significant", () => {
    const root = listFolder([entry("Proxmox/a"), entry("proxmox/a")], "Proxmox");
    expect(root.path).toBe("Proxmox");
    expect(root.total).toBe(1);
  });

  it("has nothing to list in an empty store", () => {
    expect(listFolder([], "proxmox")).toEqual({ path: "", folders: [], entries: [], total: 0 });
  });
});

describe("folder paths", () => {
  it("lists the folders on the way to a path", () => {
    expect(foldersOn("")).toEqual([]);
    expect(foldersOn("a")).toEqual(["a"]);
    expect(foldersOn("a/b")).toEqual(["a", "a/b"]);
  });

  it("names the last segment", () => {
    expect(lastSegment("a/b/c")).toBe("c");
    expect(lastSegment("solo")).toBe("solo");
  });

  it("links to a folder, and to the folder that lists a key", () => {
    expect(folderHref("proxmox/site-a")).toBe("/variables?folder=proxmox/site-a");
    expect(folderHref("")).toBe("/variables?folder=");
    expect(folderLink("proxmox/site-a/api-token")).toBe("/variables?folder=proxmox/site-a");
    expect(folderLink("solo")).toBe("/variables?folder=");
  });

  it("escapes what would break a query string", () => {
    expect(folderHref("a&b#c")).toBe("/variables?folder=a%26b%23c");
  });
});

describe("matchesSearch", () => {
  const found = entry("proxmox/token", { description: "Backup user", usedBy: ["guest/krb-dns-01"], visibility: "plain", value: "a-plain-value" });

  it("matches the key, description, and consumers", () => {
    expect(matchesSearch(found, "token")).toBe(true);
    expect(matchesSearch(found, "backup")).toBe(true);
    expect(matchesSearch(found, "krb-dns")).toBe(true);
  });

  it("never matches a value", () => {
    expect(matchesSearch(found, "a-plain-value")).toBe(false);
  });
});
