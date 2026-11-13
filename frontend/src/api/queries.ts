import { keepPreviousData, useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { api, unwrap } from "./client";
import type {
  AgentUpdate,
  Change,
  ChangeRequest,
  DeployRequest,
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
} from "./types";

export const keys = {
  auth: ["auth"] as const,
  overview: ["overview"] as const,
  inventory: ["inventory"] as const,
  uptime: ["uptime"] as const,
  guest: (id: string) => ["guest", id] as const,
  service: (id: string) => ["service", id] as const,
  icons: ["icons"] as const,
  installation: (id: string) => ["installation", id] as const,
  runs: (status: string | null) => ["runs", status ?? "all"] as const,
  run: (id: string) => ["run", id] as const,
  agents: ["agents"] as const,
  tokens: ["tokens"] as const,
  variables: ["variables"] as const,
  variableVersions: (key: string) => ["variables", "versions", key] as const,
  repository: ["repository"] as const,
  revisions: ["revisions"] as const,
  revision: (id: string) => ["revision", id] as const,
  drafts: ["drafts"] as const,
  changeForm: (resourceId: string) => ["changes", "form", resourceId] as const,
  change: (id: string) => ["changes", "one", id] as const,
  changes: (resourceId: string) => ["changes", "list", resourceId] as const,
  workflows: ["workflows"] as const,
  deployTargets: ["deploy", "targets"] as const,
  draft: (id: string) => ["drafts", id] as const,
};

export const signedOut: AuthState = { setupRequired: false, authenticated: false, username: null };

const terminalRun = new Set(["succeeded", "failed", "cancelled"]);

export function isRunFinished(status: string): boolean {
  return terminalRun.has(status);
}

// Reads

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

/** What agents answered about each uptime check; they check about once a minute. */
export function useUptime() {
  return useQuery({
    queryKey: keys.uptime,
    queryFn: () => unwrap(api.GET("/api/uptime")),
    refetchInterval: 30_000,
  });
}

export function useGuest(id: string) {
  return useQuery({
    queryKey: keys.guest(id),
    queryFn: () => unwrap(api.GET("/api/guests/{id}", { params: { path: { id } } })),
    refetchInterval: (query) => (query.state.data?.guest.activeRun ? 4_000 : 15_000),
  });
}

export function useService(id: string) {
  return useQuery({
    queryKey: keys.service(id),
    queryFn: () => unwrap(api.GET("/api/services/{id}", { params: { path: { id } } })),
    refetchInterval: (query) => (query.state.data?.service.activeRun ? 4_000 : 15_000),
  });
}

/** The predefined icon files the operator mapped into the backend; they change only when that folder does. */
export function useIcons() {
  return useQuery({
    queryKey: keys.icons,
    queryFn: () => unwrap(api.GET("/api/icons")),
    staleTime: 30_000,
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
      ["overview", "inventory", "guest", "service", "installation", "runs", "run"].map((key) =>
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

export function useConfigureGuest(id: string) {
  const refresh = useRefreshRuns();
  return useMutation({
    mutationFn: (action: string) =>
      unwrap(api.POST("/api/guests/{id}/configure", { params: { path: { id } }, body: { action } })),
    onSuccess: () => refresh(),
  });
}

export function useDeployService(id: string) {
  const refresh = useRefreshRuns();
  return useMutation({
    mutationFn: () => unwrap(api.POST("/api/services/{id}/deploy", { params: { path: { id } } })),
    onSuccess: () => refresh(),
  });
}

/** A new icon for a service: an uploaded picture, a predefined file by name, or back to the automatic match. */
export type IconChange = { picture: File } | { predefined: string } | "default";

export function useChangeServiceIcon(id: string) {
  const client = useQueryClient();
  return useMutation({
    mutationFn: (change: IconChange) => {
      const path = { params: { path: { id } } };
      if (change === "default") return unwrap(api.DELETE("/api/services/{id}/icon", path));
      if ("predefined" in change) return unwrap(api.PUT("/api/services/{id}/icon/predefined", { ...path, body: { name: change.predefined } }));
      // The picture goes as it is, under its own type, not as JSON.
      return unwrap(
        api.PUT("/api/services/{id}/icon/picture", {
          ...path,
          body: change.picture as unknown as string,
          bodySerializer: (body) => body,
          headers: { "Content-Type": change.picture.type },
        }),
      );
    },
    onSuccess: () =>
      Promise.all([client.invalidateQueries({ queryKey: keys.inventory }), client.invalidateQueries({ queryKey: keys.service(id) })]),
  });
}

/** Starts a staged rotation of the key Octopushy logs in to a guest with. */
/** Makes the installation's snippet upload key, or returns the one it has. */
export function useCreateSnippetsKey(id: string) {
  const client = useQueryClient();
  return useMutation({
    mutationFn: () => unwrap(api.POST("/api/installations/{id}/snippets-key", { params: { path: { id } } })),
    onSuccess: () => client.invalidateQueries({ queryKey: ["installation"] }),
  });
}

export function useRotateGuestAccess(id: string) {
  const refresh = useRefreshRuns();
  return useMutation({
    mutationFn: () => unwrap(api.POST("/api/guests/{id}/access/rotate", { params: { path: { id } } })),
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
}

export function useApproveStep() {
  const refresh = useRefreshRuns();
  return useMutation({
    mutationFn: ({ runId, stepId, planSha256, note }: Decision) =>
      unwrap(
        api.POST("/api/runs/{id}/steps/{stepId}/approve", {
          params: { path: { id: runId, stepId } },
          body: { planSha256, note },
        }),
      ),
    onSettled: () => refresh(),
  });
}

/**
 * Approves several runs one after the other, each with the fingerprint the operator saw.
 * One refusal does not stop the others; the error names every run that was not approved.
 */
export function useApproveSteps() {
  const refresh = useRefreshRuns();
  return useMutation({
    mutationFn: async (decisions: (Decision & { label: string })[]) => {
      const failed: string[] = [];
      for (const { runId, stepId, planSha256, note, label } of decisions) {
        try {
          await unwrap(
            api.POST("/api/runs/{id}/steps/{stepId}/approve", {
              params: { path: { id: runId, stepId } },
              body: { planSha256, note },
            }),
          );
        } catch (error) {
          failed.push(`${label}: ${error instanceof Error ? error.message : String(error)}`);
        }
      }
      if (failed.length > 0) {
        throw new Error(`${failed.length} of ${decisions.length} were not approved. ${failed.join(" ")}`);
      }
    },
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

export function useForceStopRun() {
  const refresh = useRefreshRuns();
  return useMutation({
    mutationFn: (id: string) => unwrap(api.POST("/api/runs/{id}/force-stop", { params: { path: { id } } })),
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

export function useRetryStage() {
  const refresh = useRefreshRuns();
  return useMutation({
    mutationFn: (id: string) => unwrap(api.POST("/api/runs/{id}/retry-stage", { params: { path: { id } } })),
    onSettled: () => refresh(),
  });
}

/** Starts a new run of a failed run's work; it plans again and takes its own approval. */
export function useRunAgain() {
  const refresh = useRefreshRuns();
  return useMutation({
    mutationFn: (id: string) => unwrap(api.POST("/api/runs/{id}/run-again", { params: { path: { id } } })),
    onSuccess: () => refresh(),
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
    onSettled: () => client.invalidateQueries({ queryKey: keys.agents }),
  });
}

export function useDeleteAgent() {
  const client = useQueryClient();
  return useMutation({
    mutationFn: (id: string) => unwrap(api.DELETE("/api/agents/{id}", { params: { path: { id } } })),
    onSettled: () => Promise.all([
      client.invalidateQueries({ queryKey: keys.agents }),
      client.invalidateQueries({ queryKey: keys.overview }),
    ]),
  });
}

/** Updates one agent to the latest version; the server queues it behind an update already going. */
export function useUpdateAgentVersion() {
  const client = useQueryClient();
  return useMutation({
    mutationFn: (id: string) => unwrap(api.POST("/api/agents/{id}/update", { params: { path: { id } } })),
    onSettled: () => client.invalidateQueries({ queryKey: keys.agents }),
  });
}

/** Takes a queued agent out of the update queue. */
export function useCancelAgentUpdate() {
  const client = useQueryClient();
  return useMutation({
    mutationFn: (id: string) => unwrap(api.DELETE("/api/agents/{id}/update", { params: { path: { id } } })),
    onSettled: () => client.invalidateQueries({ queryKey: keys.agents }),
  });
}

/** Updates every agent that can be, one after another. */
export function useUpdateAllAgents() {
  const client = useQueryClient();
  return useMutation({
    mutationFn: () => unwrap(api.POST("/api/agents/update")),
    onSettled: () => client.invalidateQueries({ queryKey: keys.agents }),
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

/** Values come back in the response only: they are never kept in the query cache. */
export function useRevealVariables() {
  return useMutation({
    gcTime: 0,
    mutationFn: (keys: string[]) => unwrap(api.POST("/api/variables/reveal", { body: { keys } })),
  });
}

/** One version's value, whatever its state, for comparing versions. Like a reveal, it is never kept in the query cache. */
export function useRevealVariableVersion() {
  return useMutation({
    gcTime: 0,
    mutationFn: (body: { key: string; version: number }) => unwrap(api.POST("/api/variables/versions/reveal", { body })),
  });
}

export function useDeleteVariableVersion() {
  const client = useQueryClient();
  return useMutation({
    mutationFn: ({ key, version }: { key: string; version: number }) =>
      unwrap(api.DELETE("/api/variables/versions", { params: { query: { key, version } } })),
    onSettled: (_, __, { key }) => {
      void client.invalidateQueries({ queryKey: keys.variables });
      void client.invalidateQueries({ queryKey: keys.variableVersions(key) });
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

// Changes from the UI: each is a commit to the lab repository, followed through its revision and runs.

const finalChange = new Set(["applied", "failed", "refused", "no-change", "superseded"]);

/**
 * Whether a status can no longer change. A dismissed change can: a dismissal only stops the waiting, and a commit of it that
 * arrives later moves it on. Pages follow the API's `final` flag; this is the same list, for when only a status is known.
 */
export function isChangeFinished(status: string): boolean {
  return finalChange.has(status);
}

/** What the form for a guest or service starts from, read at the head commit. */
export function useChangeForm(resourceId: string, options: { fresh?: boolean } = {}) {
  return useQuery({
    queryKey: keys.changeForm(resourceId),
    queryFn: () => unwrap(api.GET("/api/changes/form/{resourceId}", { params: { path: { resourceId } } })),
    // A form keeps the commit it was loaded from; it is read again only when asked, or, with fresh, each time it opens.
    staleTime: Infinity,
    refetchOnMount: options.fresh ? "always" : true,
    refetchOnWindowFocus: false,
  });
}

export function useChange(id: string) {
  return useQuery({
    queryKey: keys.change(id),
    queryFn: () => unwrap(api.GET("/api/changes/{id}", { params: { path: { id } } })),
    refetchInterval: (query) => {
      const data = query.state.data as Change | undefined;
      return data?.final ? false : 2_500;
    },
  });
}

export function useChanges(resourceId: string, limit = 5) {
  return useQuery({
    queryKey: keys.changes(resourceId),
    queryFn: () => unwrap(api.GET("/api/changes", { params: { query: { resourceId, limit } } })),
    refetchInterval: 15_000,
  });
}

/** Stops waiting for a change whose push is unsettled. It proves nothing; refused while its commit is on the branch. */
export function useDismissChange() {
  const client = useQueryClient();
  return useMutation({
    retry: false,
    mutationFn: (id: string) => unwrap(api.POST("/api/changes/{id}/dismiss", { params: { path: { id } } })),
    onSuccess: (change) => {
      client.setQueryData(keys.change(change.id), change);
      void client.invalidateQueries({ queryKey: ["changes"] });
    },
  });
}

export function useWorkflows() {
  return useQuery({
    queryKey: keys.workflows,
    queryFn: () => unwrap(api.GET("/api/workflows")),
    staleTime: 60_000,
  });
}

type Body = import("./schema").components["schemas"]["ChangeRequest"];

/** What committing would do: the exact diff, the message and the validation, with nothing committed. */
export function usePreviewChange() {
  return useMutation({
    retry: false,
    mutationFn: (request: ChangeRequest) => unwrap(api.POST("/api/changes/preview", { body: request as unknown as Body })),
  });
}

export function useCommitChange() {
  const client = useQueryClient();
  return useMutation({
    retry: false,
    mutationFn: (request: ChangeRequest) => unwrap(api.POST("/api/changes", { body: request as unknown as Body })),
    onSuccess: (change) => {
      client.setQueryData(keys.change(change.id), change);
      void client.invalidateQueries({ queryKey: ["changes"] });
      void client.invalidateQueries({ queryKey: keys.repository });
      void client.invalidateQueries({ queryKey: keys.revisions });
      void client.invalidateQueries({ queryKey: keys.inventory });
      void client.invalidateQueries({ queryKey: [change.resourceKind === "service" ? "service" : "guest", change.resourceId] });
    },
  });
}

// The workflow wizard: where a guest or service can be deployed, and the deploy itself, which is a change like any other.

/** Where the wizard can deploy, read at the head commit it starts from. */
export function useDeployTargets() {
  return useQuery({
    queryKey: keys.deployTargets,
    queryFn: () => unwrap(api.GET("/api/deploy/targets")),
    // The wizard keeps the commit it was loaded from; it is read again when it opens.
    staleTime: Infinity,
    refetchOnMount: "always",
    refetchOnWindowFocus: false,
  });
}

/** The next free VMID on an installation, asked for when one is chosen. */
export function useNextVmid() {
  return useMutation({
    retry: false,
    mutationFn: ({ installation, from }: { installation: string; from?: number | null }) =>
      unwrap(api.GET("/api/deploy/vmid", { params: { query: { installation, from: from ?? undefined } } })),
  });
}

type DeployBody = import("./schema").components["schemas"]["DeployRequest"];

/** What deploying would commit: the exact file and diff, the generated values and the validation, with nothing committed. */
export function usePreviewDeploy() {
  return useMutation({
    retry: false,
    mutationFn: (request: DeployRequest) => unwrap(api.POST("/api/deploy/preview", { body: request as unknown as DeployBody })),
  });
}

export function useDeploy() {
  const client = useQueryClient();
  return useMutation({
    retry: false,
    mutationFn: (request: DeployRequest) => unwrap(api.POST("/api/deploy", { body: request as unknown as DeployBody })),
    onSuccess: (change) => {
      client.setQueryData(keys.change(change.id), change);
      void client.invalidateQueries({ queryKey: ["changes"] });
      void client.invalidateQueries({ queryKey: keys.repository });
      void client.invalidateQueries({ queryKey: keys.revisions });
      void client.invalidateQueries({ queryKey: keys.inventory });
      void client.invalidateQueries({ queryKey: keys.deployTargets });
    },
  });
}
