import { describe, expect, it } from "vitest";
import type { Plan } from "../api/types";
import { classifyActions, summarizePlan } from "./plan";

describe("classifyActions", () => {
  it("recognizes single actions", () => {
    expect(classifyActions(["create"])).toBe("create");
    expect(classifyActions(["update"])).toBe("update");
    expect(classifyActions(["delete"])).toBe("delete");
    expect(classifyActions(["read"])).toBe("read");
    expect(classifyActions(["no-op"])).toBe("no-op");
  });

  it("treats delete and create in either order as a replace", () => {
    expect(classifyActions(["delete", "create"])).toBe("replace");
    expect(classifyActions(["create", "delete"])).toBe("replace");
  });

  it("does not mistake other combinations for a replace", () => {
    expect(classifyActions(["create", "update"])).toBe("other");
    expect(classifyActions(["delete", "delete"])).toBe("other");
  });
});

describe("summarizePlan", () => {
  const plan: Plan = {
    hasChanges: true,
    planSha256: "ab".repeat(32),
    toolVersion: "1.10.6",
    changes: [
      { address: "proxmox_virtual_environment_vm.this", type: "proxmox_virtual_environment_vm", name: "this", actions: ["delete", "create"] },
      { address: "terraform_data.a", type: "terraform_data", name: "a", actions: ["create"] },
      { address: "terraform_data.b", type: "terraform_data", name: "b", actions: ["update"] },
      { address: "terraform_data.c", type: "terraform_data", name: "c", actions: ["create", "delete"] },
    ],
    counts: { create: 1, update: 1, delete: 0, replace: 2 },
  };

  it("classifies every change and keeps the rest of the plan", () => {
    const summary = summarizePlan(plan);
    expect(summary.changes.map((c) => c.kind)).toEqual(["replace", "create", "update", "replace"]);
    expect(summary.counts).toEqual(plan.counts);
    expect(summary.planSha256).toBe(plan.planSha256);
    expect(summary.toolVersion).toBe("1.10.6");
  });
});
