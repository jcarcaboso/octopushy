import { describe, expect, it } from "vitest";
import { safeNext } from "./safeNext";

describe("safeNext", () => {
  it("keeps same-site paths", () => {
    expect(safeNext("/runs/abc?x=1")).toBe("/runs/abc?x=1");
  });

  it("refuses anything that could leave the site", () => {
    expect(safeNext(null)).toBe("/");
    expect(safeNext("https://evil.example")).toBe("/");
    expect(safeNext("//evil.example")).toBe("/");
    expect(safeNext("/\\evil.example")).toBe("/");
  });
});
