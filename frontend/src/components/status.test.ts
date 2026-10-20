import { describe, expect, it } from "vitest";
import { guestStatuses, runStatuses, statusStyle } from "./status";

describe("statusStyle", () => {
  it("gives every guest and run status its own text label", () => {
    for (const status of [...guestStatuses, ...runStatuses, "skipped", "assigned"]) {
      const style = statusStyle(status);
      expect(style.label).not.toBe("");
      expect(style.label).not.toBe(status);
    }
  });

  it("maps the statuses to the expected tones", () => {
    expect(statusStyle("in-sync")).toMatchObject({ label: "In sync", tone: "success" });
    expect(statusStyle("succeeded").tone).toBe("success");
    expect(statusStyle("running")).toMatchObject({ tone: "info", icon: "spinner" });
    expect(statusStyle("assigned").tone).toBe("info");
    expect(statusStyle("awaiting-approval")).toMatchObject({ label: "Awaiting approval", tone: "attention" });
    expect(statusStyle("blocked").tone).toBe("warning");
    expect(statusStyle("failed").tone).toBe("danger");
    expect(statusStyle("invalid").tone).toBe("danger");
    expect(statusStyle("uncertain")).toMatchObject({ label: "Uncertain", tone: "uncertain" });
    expect(statusStyle("retired").tone).toBe("muted");
    expect(statusStyle("cancelled").tone).toBe("muted");
    expect(statusStyle("skipped").tone).toBe("muted");
  });

  it("keeps uncertain distinct from failed and blocked", () => {
    const tones = new Set(["uncertain", "failed", "blocked"].map((s) => statusStyle(s).tone));
    expect(tones.size).toBe(3);
  });

  it("falls back to a readable label for unknown statuses", () => {
    expect(statusStyle("waiting-for-capacity")).toEqual({ label: "Waiting for capacity", tone: "neutral", icon: "dot" });
  });
});
