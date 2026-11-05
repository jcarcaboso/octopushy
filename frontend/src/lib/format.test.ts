import { describe, expect, it } from "vitest";
import { machineNoun } from "./format";

describe("machineNoun", () => {
  it("names a guest by its definition's type first", () => {
    expect(machineNoun("container")).toBe("container");
    expect(machineNoun("vm", "lxc")).toBe("VM");
    expect(machineNoun("container", "qemu")).toBe("container");
  });

  it("falls back to how Proxmox lists it, and to a VM", () => {
    expect(machineNoun(null, "lxc")).toBe("container");
    expect(machineNoun(undefined, "qemu")).toBe("VM");
    expect(machineNoun(null, null)).toBe("VM");
  });
});
