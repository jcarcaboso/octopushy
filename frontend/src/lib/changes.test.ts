import { describe, expect, it } from "vitest";
import type { ConfigurePreview, Plan, Step } from "../api/types";
import { buildOverview, buildRunOverview, emphasize, flatten } from "./changes";
import { summarizePlan } from "./plan";

type Change = Plan["changes"][number];
type Attribute = Change["attributes"][number];

const vm = "proxmox_virtual_environment_vm.bb";

function attr(name: string, before: unknown, after: unknown, extra: Partial<Attribute> = {}): Attribute {
  return { name, before, after, unknown: false, sensitive: false, ...extra };
}

function change(address: string, actions: string[], attributes: Attribute[] = []): Change {
  return { address, type: address.split(".")[0], name: address.split(".")[1], actions, attributes };
}

function plan(...changes: Change[]): Plan {
  return { hasChanges: true, changes, counts: { create: 0, update: 0, delete: 0, replace: 0 }, planSha256: null, toolVersion: null };
}

function preview(overrides: Partial<ConfigurePreview> = {}): ConfigurePreview {
  return {
    action: "update-tools", playbook: "playbooks/update-tools.yml", commit: null, image: null, host: null, toolVersion: null,
    destructive: false, tasks: [], changedFiles: null, vars: null, secrets: [], prepared: null, hostKeyFingerprints: [],
    previewSha256: null, ...overrides,
  };
}

const tiles = (plans: Plan[], previews: ConfigurePreview[] = []) => buildOverview({ plans: plans.map(summarizePlan), previews });

describe("buildOverview: VM attributes", () => {
  it("shows a disk grow, memory in GiB and a CPU change as one tile each", () => {
    const result = tiles([plan(change(vm, ["update"], [
      attr("disk.scsi0.size", 32, 64), attr("memory.dedicated", 4096, 8192), attr("cpu.cores", 2, 4),
    ]))]);
    expect(result.map((t) => [t.label, t.value, t.sub, t.tone])).toEqual([
      ["Disk", "32 → 64 GiB", "scsi0 · grow", "neutral"],
      ["Memory", "4 → 8 GiB", "dedicated", "neutral"],
      ["CPU", "2 → 4", "cores", "neutral"],
    ]);
    expect(result[0].rows).toEqual([{ mark: "~", name: "scsi0 size", before: "32 GiB", after: "64 GiB", note: "grow" }]);
    expect(result[0].heading).toBe(`${vm} · update in place`);
  });

  it("keeps memory in MiB when a side is not whole GiB", () => {
    const [tile] = tiles([plan(change(vm, ["update"], [attr("memory.dedicated", 4096, 6000)]))]);
    expect(tile.value).toBe("4096 → 6000 MiB");
  });

  it("marks a disk shrink and a replaced VM as dangerous", () => {
    const [shrink] = tiles([plan(change(vm, ["update"], [attr("disk.scsi0.size", 64, 32)]))]);
    expect(shrink.tone).toBe("danger");
    expect(shrink.sub).toBe("scsi0 · shrink");
    const [replaced] = tiles([plan(change(vm, ["delete", "create"], [attr("cpu.cores", 2, 4)]))]);
    expect(replaced.tone).toBe("danger");
    expect(replaced.rows[0].note).toBe("replace");
  });

  it("lists added and removed tags, and keeps the rest as unchanged", () => {
    const [tile] = tiles([plan(change(vm, ["update"], [attr("tags", ["homelab", "old"], ["agents", "homelab"])]))]);
    expect(tile).toMatchObject({ label: "Tags", value: "+agents −old", sub: "homelab kept" });
    expect(tile.rows.map((r) => [r.mark, r.name])).toEqual([["+", "agents"], ["−", "old"]]);
    expect(tile.unchanged.map((r) => r.name)).toEqual(["homelab"]);
  });

  it("names a bridge change as Network", () => {
    const [tile] = tiles([plan(change(vm, ["update"], [attr("network_device.0.bridge", "vmbr0", "vmbr1")]))]);
    expect(tile).toMatchObject({ label: "Network", value: "vmbr0 → vmbr1", sub: "net0 bridge" });
  });

  it("says known after apply for an unknown value and shows no values for a sensitive one", () => {
    const [unknown, sensitive] = tiles([plan(change(vm, ["update"], [
      attr("disk.scsi0.size", 32, null, { unknown: true }), attr("cpu.cores", null, null, { sensitive: true }),
    ]))]);
    expect(unknown).toMatchObject({ value: "known after apply", tone: "warn" });
    expect(unknown.rows[0]).toMatchObject({ before: "32 GiB", after: "known after apply" });
    expect(sensitive.value).toBe("sensitive");
    expect(sensitive.rows[0].before).toBeUndefined();
    expect(sensitive.rows[0].after).toBeUndefined();
  });

  it("shows a created VM's values without an old side", () => {
    const [tile] = tiles([plan(change(vm, ["create"], [attr("disk.scsi0.size", null, 32)]))]);
    expect(tile.value).toBe("32 GiB");
    expect(tile.rows[0]).toMatchObject({ mark: "+", after: "32 GiB" });
  });
});

describe("buildOverview: resources", () => {
  it("counts changes without attributes by action, and ignores no-ops", () => {
    const result = tiles([plan(
      change("terraform_data.a", ["create"]),
      change("terraform_data.b", ["update"]),
      change("terraform_data.c", ["delete"]),
      change("terraform_data.d", ["delete", "create"]),
      change("terraform_data.e", ["no-op"]),
      change(vm, ["update"], [attr("cpu.cores", 2, 4)]),
    )]);
    const resources = result.find((t) => t.key === "resources")!;
    expect(resources).toMatchObject({ label: "Resources", value: "+1 ~1 −1 ±1", tone: "danger" });
    expect(resources.rows.map((r) => [r.mark, r.name, r.note])).toEqual([
      ["+", "terraform_data.a", "create"],
      ["~", "terraform_data.b", "update in place"],
      ["−", "terraform_data.c", "delete"],
      ["±", "terraform_data.d", "replace"],
    ]);
  });

  it("makes no resources tile when nothing changes", () => {
    expect(tiles([plan(change("terraform_data.e", ["no-op"]))])).toEqual([]);
  });
});

describe("buildOverview: prepared values, files and secrets", () => {
  it("diffs prepared values against the previous run's", () => {
    const [tile] = tiles([], [preview({
      prepared: { release: "2.1.0", channel: "stable", tools: ["fzf", "jq"], extra: { port: 22 } },
      previousPrepared: { release: "2.0.0", channel: "stable", tools: ["jq"], gone: true },
    })]);
    expect(tile).toMatchObject({ label: "Prepared", value: "2 +1 −1", sub: "changed · added · removed" });
    expect(tile.rows).toEqual([
      { mark: "~", name: "release", before: "2.0.0", after: "2.1.0" },
      { mark: "~", name: "tools", before: "jq", after: "fzf, jq" },
      { mark: "+", name: "extra.port", after: "22" },
      { mark: "−", name: "gone", before: "true" },
    ]);
    expect(tile.unchanged).toEqual([{ mark: "", name: "channel", before: "stable" }]);
  });

  it("says when prepared values match the last run", () => {
    const [tile] = tiles([], [preview({ prepared: { a: 1 }, previousPrepared: { a: 1 } })]);
    expect(tile).toMatchObject({ value: "no change", rows: [], unchanged: [{ name: "a" }] });
  });

  it("lists prepared values as a first run when there is no baseline", () => {
    for (const previousPrepared of [undefined, null, "not an object"]) {
      const [tile] = tiles([], [preview({ prepared: { a: 1, b: "x" }, previousPrepared })]);
      expect(tile).toMatchObject({ value: "2", sub: "first run · nothing to compare", unchanged: [] });
      expect(tile.rows).toEqual([{ mark: "", name: "a", after: "1" }, { mark: "", name: "b", after: "x" }]);
    }
  });

  it("makes tiles for changed recipe files and secret references", () => {
    const result = tiles([], [preview({
      changedFiles: ["M modules/a/site.yml", "A modules/a/new.yml", "D modules/a/old.yml"],
      secrets: [{ name: "ansible.ssh-key", reference: "access/pve01-ai/ssh-key v2" }],
    })]);
    expect(result.map((t) => [t.label, t.value, t.tone])).toEqual([["Recipe files", "3", "neutral"], ["Secrets", "1", "neutral"]]);
    expect(result[0].rows.map((r) => r.mark)).toEqual(["~", "+", "−"]);
    expect(result[1].rows).toEqual([{ mark: "", name: "ansible.ssh-key", after: "access/pve01-ai/ssh-key v2" }]);
  });

  it("makes no files tile when no files changed", () => {
    expect(tiles([], [preview({ changedFiles: [] })])).toEqual([]);
  });
});

describe("buildRunOverview", () => {
  const step = (id: string, ordinal: number, stage: string | null, fields: Partial<Step>): Step => ({
    id, ordinal, name: "plan", kind: "agent", status: "succeeded", detail: null, startedAt: null, finishedAt: null,
    outputs: null, plan: null, approval: null, attempts: [], stage, ...fields,
  });

  it("prefixes the stage only when there are several", () => {
    const one = buildRunOverview([step("p", 0, "infra", { plan: plan(change("terraform_data.a", ["create"])) })]);
    expect(one.map((t) => t.label)).toEqual(["Resources"]);
    const two = buildRunOverview([
      step("p", 0, "infra", { plan: plan(change("terraform_data.a", ["create"])) }),
      step("v", 1, "deploy", { name: "preview", configurePreview: preview({ secrets: [{ name: "k", reference: "r v1" }] }) }),
    ]);
    expect(two.map((t) => [t.key, t.label])).toEqual([["infra/resources", "infra · Resources"], ["deploy/secrets:0", "deploy · Secrets"]]);
  });
});

describe("flatten", () => {
  it("keeps lists of plain values as one leaf and indexes lists of objects", () => {
    expect([...flatten({ a: [1, "b"], c: [{ d: 1 }], e: {}, f: null })]).toEqual([
      ["a", "1, b"], ["c[0].d", "1"], ["e", "{}"], ["f", "null"],
    ]);
  });
});

describe("emphasize", () => {
  it("marks only the tokens of the new value that differ", () => {
    expect(emphasize("2.0.0", "2.1.0")).toEqual([
      { text: "2.", changed: false }, { text: "1", changed: true }, { text: ".0", changed: false },
    ]);
    expect(emphasize("1:9.2p1-2+deb12u3", "1:9.2p1-2+deb12u5")).toEqual([
      { text: "1:9.2p1-2+deb12u", changed: false }, { text: "5", changed: true },
    ]);
  });

  it("marks the whole middle when the token counts differ", () => {
    expect(emphasize("jq", "fzf, jq")).toEqual([{ text: "fzf, ", changed: true }, { text: "jq", changed: false }]);
  });
});
