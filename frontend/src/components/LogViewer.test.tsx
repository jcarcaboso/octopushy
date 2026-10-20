import { act, render, screen } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { mockApi } from "../test/utils";
import { LogViewer } from "./LogViewer";

const at = 1_760_000_000_000;

describe("LogViewer", () => {
  beforeEach(() => vi.useFakeTimers({ shouldAdvanceTime: true }));
  afterEach(() => {
    vi.useRealTimers();
    vi.unstubAllGlobals();
  });

  it("polls with the cursor, appends lines, and stops when the log is finished", async () => {
    let call = 0;
    const { requests } = mockApi({
      "GET /api/attempts/a1/log": () => {
        call++;
        if (call === 1) {
          return {
            lines: [
              { sequence: 0, unixMs: at, stream: "agent", text: "Starting tofu plan" },
              { sequence: 1, unixMs: at + 1, stream: "stdout", text: "Plan: 1 to add" },
            ],
            next: 1,
            finished: false,
          };
        }
        return { lines: [{ sequence: 2, unixMs: at + 2, stream: "stderr", text: "Warning: deprecated" }], next: 2, finished: true };
      },
    });

    render(<LogViewer attemptId="a1" status="running" pollMs={1000} />);
    expect(await screen.findByText("Plan: 1 to add")).toBeInTheDocument();
    expect(screen.getByText("Starting tofu plan").closest(".log__line")).toHaveClass("log__line--agent");
    expect(screen.getByText("Live", { exact: false })).toBeInTheDocument();

    await act(async () => {
      await vi.advanceTimersByTimeAsync(1100);
    });
    expect(await screen.findByText("Warning: deprecated")).toBeInTheDocument();
    expect(screen.getByText("Warning: deprecated").closest(".log__line")).toHaveClass("log__line--stderr");
    expect(screen.getByText("Finished", { exact: false })).toBeInTheDocument();

    expect(requests.map((r) => new URLSearchParams(r.search).get("after"))).toEqual(["-1", "1"]);

    await act(async () => {
      await vi.advanceTimersByTimeAsync(5000);
    });
    expect(requests).toHaveLength(2);
    expect(screen.getAllByText(/line|Plan|Warning|Starting/).filter((el) => el.classList.contains("log__text"))).toHaveLength(3);
  });

  it("stops when the attempt has no log", async () => {
    const { requests } = mockApi({});
    render(<LogViewer attemptId="gone" status="failed" pollMs={1000} />);
    expect(await screen.findByText("No log is recorded for this attempt.")).toBeInTheDocument();
    await act(async () => {
      await vi.advanceTimersByTimeAsync(5000);
    });
    expect(requests).toHaveLength(1);
  });

  it("does not fetch for a queued attempt", () => {
    const { requests } = mockApi({});
    render(<LogViewer attemptId="a2" status="queued" />);
    expect(screen.getByText(/starts when an agent picks up/)).toBeInTheDocument();
    expect(requests).toHaveLength(0);
  });
});
