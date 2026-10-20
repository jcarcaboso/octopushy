import { describe, expect, it } from "vitest";
import type { LogLine } from "../api/types";
import { appendLog, emptyLog, isNearBottom } from "./logBuffer";

const line = (sequence: number, text = `line ${sequence}`, stream = "stdout"): LogLine => ({
  sequence,
  unixMs: 1_760_000_000_000 + sequence,
  stream,
  text,
});

describe("appendLog", () => {
  it("appends lines and advances the cursor", () => {
    const a = appendLog(emptyLog, { lines: [line(0), line(1)], next: 1, finished: false });
    expect(a.lines.map((l) => l.sequence)).toEqual([0, 1]);
    expect(a.next).toBe(1);
    const b = appendLog(a, { lines: [line(2)], next: 2, finished: true });
    expect(b.lines.map((l) => l.sequence)).toEqual([0, 1, 2]);
    expect(b).toMatchObject({ next: 2, finished: true });
  });

  it("ignores lines it already has, so overlapping polls never duplicate", () => {
    const a = appendLog(emptyLog, { lines: [line(0), line(1), line(2)], next: 2, finished: false });
    const b = appendLog(a, { lines: [line(1), line(2), line(3)], next: 3, finished: false });
    expect(b.lines.map((l) => l.sequence)).toEqual([0, 1, 2, 3]);
  });

  it("returns the same buffer when nothing changed", () => {
    const a = appendLog(emptyLog, { lines: [line(0)], next: 0, finished: false });
    expect(appendLog(a, { lines: [], next: 0, finished: false })).toBe(a);
  });

  it("records that the log finished even without new lines", () => {
    const a = appendLog(emptyLog, { lines: [line(0)], next: 0, finished: false });
    expect(appendLog(a, { lines: [], next: 0, finished: true }).finished).toBe(true);
  });

  it("drops the oldest lines past the limit and counts them", () => {
    const lines = Array.from({ length: 8 }, (_, i) => line(i));
    const a = appendLog(emptyLog, { lines, next: 7, finished: false }, 5);
    expect(a.lines.map((l) => l.sequence)).toEqual([3, 4, 5, 6, 7]);
    expect(a.dropped).toBe(3);
  });
});

describe("isNearBottom", () => {
  it("is true at the end and within the threshold", () => {
    expect(isNearBottom({ scrollTop: 600, clientHeight: 400, scrollHeight: 1000 })).toBe(true);
    expect(isNearBottom({ scrollTop: 580, clientHeight: 400, scrollHeight: 1000 })).toBe(true);
  });

  it("is false once the user scrolled up", () => {
    expect(isNearBottom({ scrollTop: 300, clientHeight: 400, scrollHeight: 1000 })).toBe(false);
  });

  it("is true when everything fits", () => {
    expect(isNearBottom({ scrollTop: 0, clientHeight: 400, scrollHeight: 300 })).toBe(true);
  });
});
