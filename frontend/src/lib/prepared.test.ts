import { describe, expect, it } from "vitest";
import { comparePrepared, shortValue } from "./prepared";

const release = (name: string, version: string) => ({
  url: `https://example.org/${name}/releases/download/v${version}/${name}-${version}-linux-x64.tar.gz`,
  sha256: (version.replace(/\D/g, "") + "0123456789abcdef".repeat(4)).slice(0, 64),
});

describe("comparePrepared", () => {
  it("makes one line of an address and its checksum, named without what the things share", () => {
    const result = comparePrepared(
      { machine_resolved_t3: release("t3", "0.1.12-nightly.5178"), machine_resolved_codex: release("codex", "0.167.0") },
      { machine_resolved_t3: release("t3", "0.1.12-nightly.5177"), machine_resolved_codex: release("codex", "0.167.0") },
    )!;
    expect(result.compared).toBe(true);
    expect(result.items.map((item) => [item.mark, item.name, item.path, item.before, item.after])).toEqual([
      ["~", "t3", "machine_resolved_t3", "v0.1.12-nightly.5177", "v0.1.12-nightly.5178"],
      ["", "codex", "machine_resolved_codex", "codex-0.167.0-linux-x64.tar.gz", "codex-0.167.0-linux-x64.tar.gz"],
    ]);
    // The whole values stay behind the line, under their own names.
    expect(result.items[0].leaves.map((leaf) => [leaf.mark, leaf.name, leaf.path])).toEqual([
      ["~", "url", "machine_resolved_t3.url"],
      ["~", "sha256", "machine_resolved_t3.sha256"],
    ]);
    expect(result.items[0].leaves[0].after).toBe(release("t3", "0.1.12-nightly.5178").url);
  });

  it("leads with a version when the thing has one", () => {
    const { items } = comparePrepared(
      { tool: { version: "2.1.52", url: "https://example.org/a/2.1.52/tool.tgz", sha256: "b".repeat(64) } },
      { tool: { version: "2.1.49", url: "https://example.org/a/2.1.49/tool.tgz", sha256: "a".repeat(64) } },
    )!;
    expect(items).toMatchObject([{ mark: "~", name: "tool", before: "2.1.49", after: "2.1.52" }]);
    expect(items[0].leaves).toHaveLength(3);
  });

  it("says which values changed when the telling one did not", () => {
    const url = "https://example.org/a/latest/tool.tgz";
    const { items } = comparePrepared({ tool: { url, sha256: "b".repeat(64) } }, { tool: { url, sha256: "a".repeat(64) } })!;
    expect(items).toMatchObject([{ mark: "~", name: "tool", before: "tool.tgz", after: "tool.tgz", note: "sha256 changed" }]);
  });

  it("marks a thing that is new or gone as a whole", () => {
    const { items } = comparePrepared({ kept: release("kept", "1.0.0"), added: release("added", "1.0.0") },
      { kept: release("kept", "1.0.0"), dropped: release("dropped", "2.0.0") })!;
    expect(items.map((item) => [item.mark, item.name, item.before, item.after])).toEqual([
      ["+", "added", undefined, "added-1.0.0-linux-x64.tar.gz"],
      ["−", "dropped", "dropped-2.0.0-linux-x64.tar.gz", undefined],
      ["", "kept", "kept-1.0.0-linux-x64.tar.gz", "kept-1.0.0-linux-x64.tar.gz"],
    ]);
  });

  it("keeps values that are not one thing as a line each, under their whole path", () => {
    const { items } = comparePrepared(
      { release: "2.1.0", tools: { codex: "0.167.0", fzf: "0.66.1" }, web: { port: 80, homepage: "https://example.org/" } },
      { release: "2.0.3", tools: { codex: "0.161.0", neofetch: "7.1.0" }, web: { port: 80, homepage: "https://example.org/" } },
    )!;
    expect(items.map((item) => [item.mark, item.name, item.leaves.length])).toEqual([
      ["~", "release", 0],
      ["~", "tools.codex", 0],
      ["+", "tools.fzf", 0],
      ["−", "tools.neofetch", 0],
      ["", "web.port", 0],
      ["", "web.homepage", 0],
    ]);
  });

  it("compares nothing on a first run, and is null when nothing was prepared", () => {
    for (const previous of [undefined, null, "not an object"]) {
      const result = comparePrepared({ a: 1, tool: release("tool", "1.0.0") }, previous)!;
      expect(result.compared).toBe(false);
      expect(result.items.map((item) => [item.mark, item.name, item.after])).toEqual([["", "a", "1"], ["", "tool", "tool-1.0.0-linux-x64.tar.gz"]]);
    }
    expect(comparePrepared(null, { a: 1 })).toBeNull();
  });

  it("shortens names only at a separator, and never to nothing", () => {
    const names = (prepared: Record<string, unknown>) => comparePrepared(prepared, null)!.items.map((item) => item.name);
    expect(names({ tool_alpha: release("a", "1"), tool_alps: release("b", "1") })).toEqual(["alpha", "alps"]);
    expect(names({ alpha: release("a", "1"), alps: release("b", "1") })).toEqual(["alpha", "alps"]);
    expect(names({ tool_: release("a", "1"), tool_b: release("b", "1") })).toEqual(["tool_", "tool_b"]);
    expect(names({ machine_resolved_t3: release("a", "1") })).toEqual(["machine_resolved_t3"]);
  });
});

describe("shortValue", () => {
  it("cuts a checksum to its first twelve characters and leaves other text alone", () => {
    expect(shortValue("b46a4919aa31babac22d6741a6f5e1f75b0c20d1eaad8dcaa1")).toBe("b46a4919aa31…");
    expect(shortValue(`sha256:${"ab".repeat(32)}`)).toBe("sha256:abababababab…");
    expect(shortValue("0.1.12-nightly.20261124.5178")).toBe("0.1.12-nightly.20261124.5178");
    expect(shortValue("deadbeef")).toBe("deadbeef");
  });
});
