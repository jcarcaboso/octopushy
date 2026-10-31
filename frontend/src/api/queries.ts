import { keepPreviousData, useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { api, unwrap } from "./client";
import type {
  AgentUpdate,
  AuthState,
  DraftDetail,
  DraftFileChange,
  DraftPublishRequest,
  RepositoryUpdate,
  RunDetail,
  TokenRequest,
  TrustInstallationRequest,
  VariableCandidate,
  VariablePromotion,
  VariableRevocation,
  VariableUpdate,
  WorkerDraftRequest,
  WorkerDeploymentRequest,
  WorkerPublishRequest,
} from "./types";

export const keys = {
  auth: ["auth"] as const,
  overview: ["overview"] as const,
  inventory: ["inventory"] as const,
  guest: (id: string) => ["guest", id] as const,
  installation: (id: string) => ["installation", id] as const,
  runs: (status: string | null) => ["runs", status ?? "all"] as const,
  run: (id: string) => ["run", id] as const,
  agents: ["agents"] as const,
  workerPreparation: ["worker-preparation"] as const,
  tokens: ["tokens"] as const,
  variables: ["variables"] as const,
  variableVersions: (key: string) => ["variables", "versions", key] as const,
  repository: ["repository"] as const,
  revisions: ["revisions"] as const,
  revision: (id: string) => ["revision", id] as const,
  drafts: ["drafts"] as const,
  draft: (id: string) => ["drafts", id] as const,
};

export const signedOut: AuthState = { setupRequired: false, authenticated: false, username: null };

const terminalRun = new Set(["succeeded", "failed", "cancelled"]);

export function isRunFinished(status: string): boolean {
  return terminalRun.has(status);
}

// Reads

export function useWorkerPreparation() {
  return useQuery({
    queryKey: keys.workerPreparation,
    queryFn: () => unwrap(api.GET("/api/managed-worker-preparation")),
    refetchInterval: 5_000,
  });
}

export function useAuthState() {
  return useQuery({
    queryKey: keys.auth,
    queryFn: () => unwrap(api.GET("/api/auth/state")),
    staleTime: 60_000,
  });
}

export function useOverview() {
  return useQuery({
    queryKey: keys.overview,
    queryFn: () => unwrap(api.GET("/api/overview")),
    refetchInterval: 10_000,
  });
}

export function useInventory() {
  return useQuery({
    queryKey: keys.inventory,
    queryFn: () => unwrap(api.GET("/api/inventory")),
    refetchInterval: 15_000,
  });
}

export function useGuest(id: string) {
  return useQuery({
    queryKey: keys.guest(id),
    queryFn: () => unwrap(api.GET("/api/guests/{id}", { params: { path: { id } } })),
    refetchInterval: (query) => (query.state.data?.guest.activeRun ? 4_000 : 15_000),
  });
}

export function useCreateDiskReporter(id: string) {
  const client = useQueryClient();
  return useMutation({
    gcTime: 0,
    mutationFn: (intervalSeconds: number) => unwrap(api.POST("/api/guests/{id}/disk-usage/token", {
      params: { path: { id } },
      body: { intervalSeconds },
    })),
    onSettled: () => client.invalidateQueries({ queryKey: keys.guest(id) }),
  });
}

export function useRevokeDiskReporter(id: string) {
  const client = useQueryClient();
  return useMutation({
    mutationFn: () => unwrap(api.DELETE("/api/guests/{id}/disk-usage/token", { params: { path: { id } } })),
    onSettled: () => client.invalidateQueries({ queryKey: keys.guest(id) }),
  });
}

export function useInstallation(id: string) {
  return useQuery({
    queryKey: keys.installation(id),
    queryFn: () => unwrap(api.GET("/api/installations/{id}", { params: { path: { id } } })),
    refetchInterval: 10_000,
  });
}

export function useRuns(status: string | null, limit = 100) {
  return useQuery({
    queryKey: keys.runs(status),
    queryFn: () =>
      unwrap(api.GET("/api/runs", { params: { query: { status: status ?? undefined, limit } } })),
    refetchInterval: 5_000,
    placeholderData: keepPreviousData,
  });
}

export function useRun(id: string) {
  return useQuery({
    queryKey: keys.run(id),
    queryFn: () => unwrap(api.GET("/api/runs/{id}", { params: { path: { id } } })),
    // Poll while the run can still change; an uncertain run can still receive a late result.
    refetchInterval: (query) => {
      const data = query.state.data as RunDetail | undefined;
      if (!data) return 2_500;
      return isRunFinished(data.run.status) ? false : 2_500;
    },
  });
}

export function useAgents() {
  return useQuery({
    queryKey: keys.agents,
    queryFn: () => unwrap(api.GET("/api/agents")),
    refetchInterval: 10_000,
  });
}

export function useTokens() {
  return useQuery({
    queryKey: keys.tokens,
    queryFn: () => unwrap(api.GET("/api/agents/tokens")),
    refetchInterval: 15_000,
  });
}

export function useVariables() {
  return useQuery({
    queryKey: keys.variables,
    queryFn: () => unwrap(api.GET("/api/variables")),
  });
}

export function useVariableVersions(key: string) {
  return useQuery({
    queryKey: keys.variableVersions(key),
    queryFn: () => unwrap(api.GET("/api/variables/versions", { params: { query: { key } } })),
    enabled: !key.startsWith("system/"),
  });
}

export function useRepository(options: { fast?: boolean } = {}) {
  return useQuery({
    queryKey: keys.repository,
    queryFn: () => unwrap(api.GET("/api/repository")),
    refetchInterval: options.fast ? 3_000 : 30_000,
  });
}

export function useRevisions(limit = 50) {
  return useQuery({
    queryKey: keys.revisions,
    queryFn: () => unwrap(api.GET("/api/repository/revisions", { params: { query: { limit } } })),
    refetchInterval: 30_000,
  });
}

export function useRevision(id: string | null) {
  return useQuery({
    queryKey: keys.revision(id ?? ""),
    queryFn: () =>
      unwrap(api.GET("/api/repository/revisions/{id}", { params: { path: { id: id! } } })),
    enabled: Boolean(id),
    staleTime: Infinity,
  });
}

// Changes

export function usePrepareWorkerDraft() {
  return useMutation({
    mutationFn: (body: WorkerDraftRequest) => unwrap(api.POST("/api/managed-worker-preparation", { body })),
  });
}

export function useReviewWorkerDeployment() {
  return useMutation({
    mutationFn: (body: WorkerDeploymentRequest) => unwrap(api.POST("/api/managed-worker-preparation/review", { body })),
  });
}

export function usePublishWorkerDeployment() {
  const client = useQueryClient();
  return useMutation({
    mutationFn: (body: WorkerPublishRequest) => unwrap(api.POST("/api/managed-worker-preparation/publish", { body })),
    onSuccess: () => client.invalidateQueries(),
  });
}

export function useLogin() {
  const client = useQueryClient();
  return useMutation({
    mutationFn: (body: { username: string; password: string }) =>
      unwrap(api.POST("/api/auth/login", { body })),
    onSuccess: (state) => client.setQueryData(keys.auth, state),
  });
}

export function useSetup() {
  const client = useQueryClient();
  return useMutation({
    mutationFn: (body: { setupCode: string; username: string; password: string }) =>
      unwrap(api.POST("/api/auth/setup", { body })),
    onSuccess: (state) => client.setQueryData(keys.auth, state),
  });
}

export function useLogout() {
  const client = useQueryClient();
  return useMutation({
    mutationFn: () => unwrap(api.POST("/api/auth/logout")),
    onSettled: () => {
      client.removeQueries({ predicate: (q) => q.queryKey[0] !== keys.auth[0] });
      client.setQueryData(keys.auth, signedOut);
    },
  });
}

/** Invalidates everything that shows runs or statuses. */
function useRefreshRuns() {
  const client = useQueryClient();
  return () =>
    Promise.all(
      ["overview", "inventory", "guest", "installation", "runs", "run"].map((key) =>
        client.invalidateQueries({ queryKey: [key] }),
      ),
    );
}

export function usePlanGuest() {
  const refresh = useRefreshRuns();
  return useMutation({
    mutationFn: (id: string) => unwrap(api.POST("/api/guests/{id}/plan", { params: { path: { id } } })),
    onSuccess: () => refresh(),
  });
}

/** Observes an installation in the background; it is not a run, so only the installation views refresh. */
export function useObserveInstallation() {
  const client = useQueryClient();
  return useMutation({
    mutationFn: (id: string) =>
      unwrap(api.POST("/api/installations/{id}/observe", { params: { path: { id } } })),
    onSuccess: () =>
      Promise.all(["overview", "inventory", "installation"].map((key) => client.invalidateQueries({ queryKey: [key] }))),
  });
}

interface Decision {
  runId: string;
  stepId: string;
  planSha256: string | null;
  note: string | null;
  workerUpdateSha256?: string;
}

export function useApproveStep() {
  const refresh = useRefreshRuns();
  return useMutation({
    mutationFn: ({ runId, stepId, planSha256, note, workerUpdateSha256 }: Decision) =>
      unwrap(
        api.POST("/api/runs/{id}/steps/{stepId}/approve", {
          params: { path: { id: runId, stepId } },
          body: { planSha256, note, ...(workerUpdateSha256 ? { workerUpdateSha256 } : {}) },
        }),
      ),
    onSettled: () => refresh(),
  });
}

export function useRejectStep() {
  const refresh = useRefreshRuns();
  return useMutation({
    mutationFn: ({ runId, stepId, note }: Decision) =>
      unwrap(
        api.POST("/api/runs/{id}/steps/{stepId}/reject", {
          params: { path: { id: runId, stepId } },
          body: { planSha256: null, note },
        }),
      ),
    onSettled: () => refresh(),
  });
}

export function useCancelRun() {
  const refresh = useRefreshRuns();
  return useMutation({
    mutationFn: (id: string) => unwrap(api.POST("/api/runs/{id}/cancel", { params: { path: { id } } })),
    onSettled: () => refresh(),
  });
}

export function useCloseRun() {
  const refresh = useRefreshRuns();
  return useMutation({
    mutationFn: (id: string) => unwrap(api.POST("/api/runs/{id}/close", { params: { path: { id } } })),
    onSettled: () => refresh(),
  });
}

export function useRetryFilesystem() {
  const refresh = useRefreshRuns();
  return useMutation({
    mutationFn: (id: string) => unwrap(api.POST("/api/runs/{id}/retry-filesystem", { params: { path: { id } } })),
    onSettled: () => refresh(),
  });
}

export function useUpdateAgent() {
  const client = useQueryClient();
  return useMutation({
    mutationFn: ({ id, ...update }: Partial<AgentUpdate> & { id: string }) =>
      unwrap(
        api.PATCH("/api/agents/{id}", {
          params: { path: { id } },
          body: {
            labels: update.labels ?? null,
            maxConcurrency: update.maxConcurrency ?? null,
            disabled: update.disabled ?? null,
            draining: update.draining ?? null,
          },
        }),
      ),
    onSettled: () => Promise.all([
      client.invalidateQueries({ queryKey: keys.agents }),
      client.invalidateQueries({ queryKey: keys.workerPreparation }),
    ]),
  });
}

export function useReviewWorkerUpdate() {
  return useMutation({
    mutationFn: (id: string) => unwrap(api.GET("/api/managed-worker-preparation/{id}/update", { params: { path: { id } } })),
  });
}

export function useStartWorkerUpdate() {
  const refresh = useRefreshRuns();
  return useMutation({
    mutationFn: ({ id, generation, archiveSha256 }: { id: string; generation: string; archiveSha256: string }) =>
      unwrap(api.POST("/api/managed-worker-preparation/{id}/update", {
        params: { path: { id } }, body: { generation, archiveSha256 },
      })),
    onSuccess: () => refresh(),
  });
}

export function useDeleteAgent() {
  const client = useQueryClient();
  return useMutation({
    mutationFn: (id: string) => unwrap(api.DELETE("/api/agents/{id}", { params: { path: { id } } })),
    onSettled: () => Promise.all([
      client.invalidateQueries({ queryKey: keys.agents }),
      client.invalidateQueries({ queryKey: keys.workerPreparation }),
      client.invalidateQueries({ queryKey: keys.overview }),
    ]),
  });
}

export function useVerifyWorkerRetirement() {
  const client = useQueryClient();
  const refresh = useRefreshRuns();
  return useMutation({
    mutationFn: ({ id, generation }: { id: string; generation: string }) =>
      unwrap(api.POST("/api/managed-worker-preparation/{id}/retire", {
        params: { path: { id } }, body: { generation, confirmAlreadyRemoved: true },
      })),
    onSettled: () => Promise.all([
      refresh(),
      client.invalidateQueries({ queryKey: keys.workerPreparation }),
    ]),
  });
}

export function useCreateToken() {
  const client = useQueryClient();
  return useMutation({
    mutationFn: (body: TokenRequest) => unwrap(api.POST("/api/agents/tokens", { body })),
    onSettled: () => client.invalidateQueries({ queryKey: keys.tokens }),
  });
}

export function useDeleteToken() {
  const client = useQueryClient();
  return useMutation({
    mutationFn: (id: string) =>
      unwrap(api.DELETE("/api/agents/tokens/{id}", { params: { path: { id } } })),
    onSettled: () => client.invalidateQueries({ queryKey: keys.tokens }),
  });
}

export function useSetVariable() {
  const client = useQueryClient();
  return useMutation({
    gcTime: 0,
    mutationFn: (body: VariableUpdate) => unwrap(api.PUT("/api/variables", { body })),
    onSettled: () => {
      void client.invalidateQueries({ queryKey: keys.variables });
      // A blocked step may continue now.
      void client.invalidateQueries({ queryKey: ["run"] });
    },
  });
}

export function useStageVariable() {
  const client = useQueryClient();
  return useMutation({
    gcTime: 0,
    mutationFn: (body: VariableCandidate) => unwrap(api.POST("/api/variables/candidates", { body })),
    onSettled: () => client.invalidateQueries({ queryKey: keys.variables }),
  });
}

export function usePromoteVariable() {
  const client = useQueryClient();
  return useMutation({
    mutationFn: (body: VariablePromotion) => unwrap(api.POST("/api/variables/promote", { body })),
    onSettled: () => {
      void client.invalidateQueries({ queryKey: keys.variables });
      void client.invalidateQueries({ queryKey: ["run"] });
    },
  });
}

export function useRevokeVariable() {
  const client = useQueryClient();
  return useMutation({
    mutationFn: (body: VariableRevocation) => unwrap(api.POST("/api/variables/revoke", { body })),
    onSettled: () => {
      void client.invalidateQueries({ queryKey: keys.variables });
      void client.invalidateQueries({ queryKey: ["run"] });
    },
  });
}

export function useDeleteVariable() {
  const client = useQueryClient();
  return useMutation({
    mutationFn: (key: string) => unwrap(api.DELETE("/api/variables", { params: { query: { key } } })),
    onSettled: () => client.invalidateQueries({ queryKey: keys.variables }),
  });
}

export function useUpdateRepository() {
  const client = useQueryClient();
  return useMutation({
    mutationFn: (body: RepositoryUpdate) => unwrap(api.PUT("/api/repository", { body })),
    onSuccess: (repository) => client.setQueryData(keys.repository, repository),
    onSettled: () => {
      void client.invalidateQueries({ queryKey: keys.overview });
      void client.invalidateQueries({ queryKey: keys.revisions });
    },
  });
}

export function useSyncRepository() {
  const client = useQueryClient();
  return useMutation({
    mutationFn: () => unwrap(api.POST("/api/repository/sync", { parseAs: "text" })),
    onSuccess: () => {
      // The fetch runs in the background; look again shortly.
      for (const delay of [1_000, 3_000, 6_000]) {
        setTimeout(() => {
          void client.invalidateQueries({ queryKey: keys.repository });
          void client.invalidateQueries({ queryKey: keys.revisions });
        }, delay);
      }
    },
  });
}

/** Lets credentials go to the endpoints an installation declares now; waiting runs and observations continue. */
export function useTrustInstallation() {
  const refreshRuns = useRefreshRuns();
  return useMutation({
    retry: false,
    mutationFn: ({ id, ...body }: TrustInstallationRequest & { id: string }) =>
      unwrap(api.POST("/api/installations/{id}/trust", { params: { path: { id } }, body })),
    onSuccess: () => refreshRuns(),
  });
}

// Repository editor drafts

export function useDrafts() {
  return useQuery({
    queryKey: keys.drafts,
    queryFn: () => unwrap(api.GET("/api/repository/drafts")),
    refetchInterval: 15_000,
  });
}

export function useDraft(id: string) {
  return useQuery({
    queryKey: keys.draft(id),
    queryFn: () => unwrap(api.GET("/api/repository/drafts/{id}", { params: { path: { id } } })),
    // Picks up a moved branch, which offers updating the draft.
    refetchInterval: 30_000,
  });
}

/** Reads one file of a draft, with its content at the draft's base for comparing. */
export function fetchDraftFile(id: string, path: string) {
  return unwrap(api.GET("/api/repository/drafts/{id}/file", { params: { path: { id }, query: { path } } }));
}

function useDraftMutation<T>(id: string, mutationFn: (variables: T) => Promise<DraftDetail>) {
  const client = useQueryClient();
  return useMutation({
    retry: false,
    mutationFn,
    onSuccess: (detail) => {
      client.setQueryData(keys.draft(id), detail);
      void client.invalidateQueries({ queryKey: keys.drafts, exact: true });
    },
  });
}

export function useCreateDraft() {
  const client = useQueryClient();
  return useMutation({
    retry: false,
    mutationFn: (title: string) => unwrap(api.POST("/api/repository/drafts", { body: { title } })),
    onSuccess: (detail) => {
      client.setQueryData(keys.draft(detail.draft.id), detail);
      void client.invalidateQueries({ queryKey: keys.drafts, exact: true });
    },
  });
}

export function useChangeDraft(id: string) {
  return useDraftMutation(id, ({ expectedHead, changes }: { expectedHead: string; changes: DraftFileChange[] }) =>
    unwrap(api.POST("/api/repository/drafts/{id}/changes", { params: { path: { id } }, body: { expectedHead, changes } })),
  );
}

export function useUpdateDraft(id: string) {
  return useDraftMutation(id, (expectedHead: string) =>
    unwrap(api.POST("/api/repository/drafts/{id}/update", { params: { path: { id } }, body: { expectedHead } })),
  );
}

export function useValidateDraft(id: string) {
  return useMutation({
    retry: false,
    mutationFn: () => unwrap(api.POST("/api/repository/drafts/{id}/validate", { params: { path: { id } } })),
  });
}

export function usePublishDraft(id: string) {
  const client = useQueryClient();
  return useMutation({
    retry: false,
    mutationFn: (body: DraftPublishRequest) =>
      unwrap(api.POST("/api/repository/drafts/{id}/publish", { params: { path: { id } }, body })),
    onSuccess: () =>
      Promise.all([
        client.invalidateQueries({ queryKey: keys.drafts }),
        client.invalidateQueries({ queryKey: keys.repository }),
        client.invalidateQueries({ queryKey: keys.revisions }),
      ]),
  });
}

export function useDiscardDraft() {
  const client = useQueryClient();
  return useMutation({
    retry: false,
    mutationFn: (id: string) =>
      unwrap(api.DELETE("/api/repository/drafts/{id}", { params: { path: { id } }, parseAs: "text" })),
    onSuccess: (_result, id) => {
      client.removeQueries({ queryKey: keys.draft(id) });
      return client.invalidateQueries({ queryKey: keys.drafts, exact: true });
    },
  });
}
