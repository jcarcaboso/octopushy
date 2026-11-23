import { useQueryClient } from "@tanstack/react-query";
import { useEffect, useState } from "react";
import { keys } from "../../api/queries";
import type { Repository, RepositorySync } from "../../api/types";

/** How long the end of a sync stays in view: "Up to date", or the new revision's highlight. */
const upToDateMs = 2_500;
const freshMs = 6_000;

/** A request is shown as begun until the server reports it, or a fetch after it, or a minute passes. */
const requestMs = 60_000;

export type SyncView = {
  /** The sync under way, or asked for and not reported yet. */
  live: RepositorySync | null;
  /** The commit that was current when the sync began: a fetched head equal to it is nothing new. */
  baseCommit: string | null;
  /** The sync that just ended without a new revision. */
  upToDate: RepositorySync | null;
  /** The revision a sync that just ended loaded. */
  freshId: string | null;
  /** Call when Sync now is pressed: the page locks at once, before the server reports the sync. */
  request: () => void;
  /** Call when asking for a sync failed. */
  cancel: () => void;
};

type Run = { baseId: string | null; baseCommit: string | null; last: RepositorySync };
type Ended = { kind: "up-to-date"; sync: RepositorySync } | { kind: "landed"; id: string };

/** What the page shows of a sync: the server's progress, the gap before it, and what it ended with. */
export function useRepositorySync(repository: Repository | undefined): SyncView {
  const client = useQueryClient();
  const [requested, setRequested] = useState<{ lastFetchAt: string | null } | null>(null);
  const [run, setRun] = useState<Run | null>(null);
  const [ended, setEnded] = useState<Ended | null>(null);
  const server = repository?.sync ?? null;
  const lastFetchAt = repository?.lastFetchAt ?? null;

  // Adjusted while rendering, from what the repository says now (react.dev: "storing information from previous renders").
  let request = requested;
  if (request && (server || lastFetchAt !== request.lastFetchAt)) {
    request = null;
    setRequested(null);
  }
  const live: RepositorySync | null =
    server ?? (request ? { phase: "fetch", startedAt: "", commit: null, subject: null, author: null } : null);
  if (live && !run) {
    setRun({ baseId: repository?.revision?.id ?? null, baseCommit: repository?.revision?.commit ?? null, last: live });
    setEnded(null);
  } else if (live && run && (run.last.phase !== live.phase || run.last.commit !== live.commit)) {
    setRun({ ...run, last: live });
  } else if (!live && run) {
    setRun(null);
    const id = repository?.revision?.id ?? null;
    if (id && id !== run.baseId) setEnded({ kind: "landed", id });
    else if (!repository?.lastFetchError) setEnded({ kind: "up-to-date", sync: run.last });
  }

  useEffect(() => {
    if (!requested) return;
    const timer = setTimeout(() => setRequested(null), requestMs);
    return () => clearTimeout(timer);
  }, [requested]);

  useEffect(() => {
    if (!ended) return;
    if (ended.kind === "landed") void client.invalidateQueries({ queryKey: keys.revisions });
    const timer = setTimeout(() => setEnded(null), ended.kind === "landed" ? freshMs : upToDateMs);
    return () => clearTimeout(timer);
  }, [ended, client]);

  return {
    live,
    baseCommit: run?.baseCommit ?? null,
    upToDate: !live && ended?.kind === "up-to-date" ? ended.sync : null,
    freshId: ended?.kind === "landed" ? ended.id : null,
    request: () => setRequested({ lastFetchAt }),
    cancel: () => setRequested(null),
  };
}
