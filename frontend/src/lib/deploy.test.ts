import { describe, expect, it } from "vitest";
import type { WorkflowSummary, WorkflowVersion } from "../api/types";
import { defaultsOf, newestPerMajor, readDeployHints, referenceOf, valueAt } from "./deploy";

function version(v: string, usable = true): WorkflowVersion {
  return { id: "fedora-vm", version: v, commit: null, usable, issue: null, manages: "vm", description: null, inputs: null, ui: null, secrets: [], setup: false };
}

describe("deploy helpers", () => {
  it("offers the newest usable release of each major, newest major first", () => {
    const workflow: WorkflowSummary = { id: "fedora-vm", manages: "vm", description: null,
      versions: [version("2.0", false), version("1.10"), version("1.2"), version("2.1")] };
    expect(newestPerMajor(workflow).map((v) => v.version)).toEqual(["2.1", "1.10"]);
    expect(referenceOf(version("1.10"), false)).toBe("fedora-vm@1");
    expect(referenceOf(version("1.10"), true)).toBe("fedora-vm@1.10");
  });

  it("starts a form from the schema's defaults and what it requires", () => {
    const schema = {
      type: "object",
      properties: {
        hardware: {
          type: "object",
          properties: { cores: { type: "integer", default: 2 }, memory: { type: "integer" }, disks: { type: "array", minItems: 1, items: { type: "object", properties: { size: { type: "integer", default: 32 } } } } },
          required: ["memory", "disks"],
        },
        inputs: { type: "object", properties: { channel: { $ref: "#/$defs/channel" }, note: { type: "string" } } },
      },
      $defs: { channel: { enum: ["stable", "beta"], default: "stable" } },
    };
    expect(defaultsOf(schema)).toEqual({ hardware: { cores: 2, disks: [{ size: 32 }] }, inputs: { channel: "stable" } });
  });

  it("reads the generators a workflow asks for, and ignores what it does not know", () => {
    const hints = readDeployHints({ vmid: { generate: "next-vmid", from: 200 }, template: "fedora-44", access: { user: "ops" },
      secrets: { token: { generate: "agent-enrollment-token", labels: "inputs.labels", other: 1 }, bad: "x" }, unknown: true });
    expect(hints).toEqual({ vmid: { generate: "next-vmid", from: 200 }, template: "fedora-44", accessUser: "ops",
      secrets: { token: { generate: "agent-enrollment-token", labels: "inputs.labels" } } });
    expect(valueAt({ inputs: { labels: ["a"] } }, "inputs.labels")).toEqual(["a"]);
    expect(valueAt({ inputs: {} }, "inputs.labels.x")).toBeUndefined();
  });
});
