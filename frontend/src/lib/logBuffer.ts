import type { LogLine } from "../api/types";

export interface LogBuffer {
  lines: LogLine[];
  /** The sequence to ask for lines after; -1 before the first line. */
  next: number;
  finished: boolean;
  /** Lines dropped from the front to keep the buffer bounded. */
  dropped: number;
}

export const emptyLog: LogBuffer = { lines: [], next: -1, finished: false, dropped: 0 };

const maxLogLines = 20_000;

interface Chunk {
  lines: LogLine[];
  next: number;
  finished: boolean;
}

/**
 * Adds a chunk from `GET /api/attempts/{id}/log?after=N`. Lines at or before the last
 * sequence already held are ignored, so a repeated or overlapping poll never duplicates.
 */
export function appendLog(buffer: LogBuffer, chunk: Chunk, max = maxLogLines): LogBuffer {
  const last = buffer.lines.length > 0 ? buffer.lines[buffer.lines.length - 1].sequence : buffer.next;
  const fresh = chunk.lines.filter((line) => line.sequence > last);
  const next = Math.max(buffer.next, chunk.next, fresh.length ? fresh[fresh.length - 1].sequence : -1);
  if (fresh.length === 0) {
    return next === buffer.next && chunk.finished === buffer.finished ? buffer : { ...buffer, next, finished: chunk.finished };
  }
  let lines = buffer.lines.concat(fresh);
  let dropped = buffer.dropped;
  if (lines.length > max) {
    dropped += lines.length - max;
    lines = lines.slice(lines.length - max);
  }
  return { lines, next, finished: chunk.finished, dropped };
}

/** Whether a scroll container is at (or within a few pixels of) its end. */
export function isNearBottom(
  el: { scrollTop: number; clientHeight: number; scrollHeight: number },
  threshold = 24,
): boolean {
  return el.scrollHeight - el.scrollTop - el.clientHeight <= threshold;
}
