import { describe, expect, it } from "vitest";
import { elapsed, formatDuration, formatRelative, formatUptime } from "./time";

const now = Date.parse("2026-10-20T12:00:00Z");
const ago = (ms: number) => new Date(now - ms).toISOString();
const ahead = (ms: number) => new Date(now + ms).toISOString();
const s = 1000;
const min = 60 * s;
const h = 60 * min;
const d = 24 * h;

describe("formatRelative", () => {
  it("says just now for small differences in either direction", () => {
    expect(formatRelative(ago(10 * s), now)).toBe("just now");
    expect(formatRelative(ahead(20 * s), now)).toBe("just now");
  });

  it("uses minutes, hours, and days", () => {
    expect(formatRelative(ago(60 * s), now)).toBe("1 min ago");
    expect(formatRelative(ago(5 * min), now)).toBe("5 min ago");
    expect(formatRelative(ago(44 * min), now)).toBe("44 min ago");
    expect(formatRelative(ago(60 * min), now)).toBe("1 h ago");
    expect(formatRelative(ago(5 * h), now)).toBe("5 h ago");
    expect(formatRelative(ago(26 * h), now)).toBe("yesterday");
    expect(formatRelative(ago(3 * d), now)).toBe("3 days ago");
  });

  it("handles the future", () => {
    expect(formatRelative(ahead(10 * min), now)).toBe("in 10 min");
    expect(formatRelative(ahead(3 * h), now)).toBe("in 3 h");
    expect(formatRelative(ahead(30 * h), now)).toBe("tomorrow");
    expect(formatRelative(ahead(20 * d), now)).toBe("in 20 days");
  });

  it("falls back to the date after a month", () => {
    const text = formatRelative(ago(60 * d), now);
    expect(text).not.toMatch(/ago/);
    expect(text).toMatch(/2026/);
  });

  it("accepts dates and numbers", () => {
    expect(formatRelative(new Date(now - 2 * h), now)).toBe("2 h ago");
    expect(formatRelative(now - 2 * h, now)).toBe("2 h ago");
  });
});

describe("formatDuration", () => {
  it("formats compactly", () => {
    expect(formatDuration(850)).toBe("850 ms");
    expect(formatDuration(12 * s)).toBe("12 s");
    expect(formatDuration(3 * min + 4 * s)).toBe("3 min 4 s");
    expect(formatDuration(2 * h)).toBe("2 h");
    expect(formatDuration(2 * h + 5 * min)).toBe("2 h 5 min");
    expect(formatDuration(3 * d + 4 * h)).toBe("3 d 4 h");
    expect(formatDuration(-1)).toBe("—");
  });

  it("formats uptime from seconds", () => {
    expect(formatUptime(null)).toBe("—");
    expect(formatUptime(42)).toBe("42 s");
    expect(formatUptime(12 * 86_400 + 3 * 3_600)).toBe("12 d 3 h");
  });

  it("measures elapsed time until now when unfinished", () => {
    expect(elapsed(null)).toBeNull();
    expect(elapsed(ago(5 * s), undefined, now)).toBe(5 * s);
    expect(elapsed(ago(5 * s), ago(2 * s), now)).toBe(3 * s);
  });
});
