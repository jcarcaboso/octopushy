import type { components } from "./schema";

type Schemas = components["schemas"];

export type AuthState = Schemas["AuthState"];
export type Overview = Schemas["OverviewDto"];
export type OverviewCounts = Schemas["OverviewCounts"];
export type Inventory = Schemas["InventoryDto"];
export type Lab = Schemas["LabDto"];
export type InstallationSummary = Schemas["InstallationSummaryDto"];
export type InstallationDetail = Schemas["InstallationDetailDto"];
export type GuestSummary = Schemas["GuestSummaryDto"];
export type GuestDetail = Schemas["GuestDetailDto"];
export type ServiceSummary = Schemas["ServiceSummaryDto"];
export type ServiceDetail = Schemas["ServiceDetailDto"];
export type Workflow = Schemas["WorkflowDto"];
export type GuestConfiguration = Schemas["GuestConfigurationDto"];
export type GuestSchedule = Schemas["GuestScheduleDto"];
export type FollowedSecret = Schemas["FollowedSecretDto"];
export type ManagedAccess = Schemas["ManagedAccessDto"];
export type ManagedHostKey = Schemas["ManagedHostKeyDto"];
export type ManagedHostKeyVersion = Schemas["ManagedHostKeyVersionDto"];
export type SnippetAccess = Schemas["SnippetAccessDto"];
export type ConfigurePreview = Schemas["ConfigurePreviewDto"];
export type ObservedHost = Schemas["ObservedHostDto"];
export type ObservedGuest = Schemas["ObservedGuestDto"];
export type Uptime = Schemas["UptimeDto"];
export type UptimeCheck = Schemas["UptimeCheckDto"];
export type ObserveFailure = Schemas["ObserveErrorDto"];
export type Issue = Schemas["IssueDto"];
export type Problem = Schemas["ProblemDetails"];
export type Plan = Schemas["PlanDto"];
export type Policy = Schemas["PolicyDto"] | Schemas["RunPolicyDto"];
export type RunSummary = Schemas["RunSummaryDto"];
export type RunDetail = Schemas["RunDetailDto"];
export type Step = Schemas["StepDto"];
export type Attempt = Schemas["AttemptDto"];
export type Approval = Schemas["ApprovalDto"];
export type LogChunk = Schemas["LogDto"];
export type LogLine = Schemas["LogLineRecord"];
export type Repository = Schemas["RepositoryDto"];
export type RepositoryUpdate = Schemas["RepositoryUpdate"];
export type Revision = Schemas["RevisionDto"];
export type WorkflowsSource = Schemas["WorkflowsSourceDto"];
export type WorkflowReleases = Schemas["WorkflowReleasesDto"];
export type WorkflowRelease = Schemas["WorkflowReleaseDto"];
export type WorkflowUse = Schemas["WorkflowUseDto"];
export type RunWorkflow = Schemas["RunWorkflowDto"];
export type RevisionDetail = Schemas["RevisionDetailDto"];
export type InstallationTrust = Schemas["InstallationTrustDto"];
export type TrustInstallationRequest = Schemas["TrustInstallationRequest"];
export type Agent = Schemas["AgentDto"];
export type AgentUpdate = Schemas["AgentUpdate"];
export type EnrollmentToken = Schemas["TokenDto"];
export type TokenRequest = Schemas["TokenRequest"];
export type CreatedToken = Schemas["CreatedToken"];
export type Variables = Schemas["VariablesDto"];
export type Variable = Schemas["VariableDto"];
export type MissingVariable = Schemas["MissingVariableDto"];
export type VariableUpdate = Schemas["VariableUpdate"];
export type VariableCandidate = Schemas["VariableCandidateRequest"];
export type VariablePromotion = Schemas["VariablePromotionRequest"];
export type VariableRevocation = Schemas["VariableRevocationRequest"];
export type VariableVersion = Schemas["VariableVersionDto"];
export type DraftSummary = Schemas["DraftSummaryDto"];
export type DraftDetail = Schemas["DraftDetailDto"];
export type DraftFile = Schemas["DraftFileDto"];
export type DraftFileContent = Schemas["DraftFileContentDto"];
export type DraftChange = Schemas["ChangedPath"];
export type DraftFileChange = Schemas["DraftFileChange"];
export type DraftValidation = Schemas["DraftValidationDto"];
export type DraftPublishRequest = Schemas["DraftPublishRequest"];
export type ChangeForm = Schemas["ChangeFormDto"];
export type ChangePreview = Schemas["ChangePreviewDto"];
export type ChangeRefused = Schemas["ChangeRefusedDto"];
export type Change = Schemas["ChangeDto"];
export type FieldIssue = Schemas["FieldIssueDto"];
export type WorkflowVersion = Schemas["WorkflowVersionDto"];
export type WorkflowCatalog = Schemas["WorkflowCatalogDto"];

/** A change as a form sends it; the sections are plain JSON objects. */
export interface ChangeRequest {
  resourceId: string;
  baseCommit: string;
  kind: "edit" | "upgrade" | "remove";
  workflow?: string | null;
  hardware?: Record<string, unknown> | null;
  network?: Record<string, unknown> | null;
  inputs?: Record<string, unknown> | null;
}
export type WorkflowSummary = Schemas["WorkflowSummaryDto"];
export type DeployTargets = Schemas["DeployTargetsDto"];
export type DeployInstallation = Schemas["DeployInstallationDto"];
export type NextVmid = Schemas["NextVmidDto"];
export type GeneratedValue = Schemas["GeneratedValueDto"];

/** How a new guest's or service's secret is provided: an existing store key, or a value Octopushy generates. */
export interface SecretChoice {
  store?: string | null;
  generate?: string | null;
  labels?: string[] | null;
  name?: string | null;
}

/** A deploy from the workflow wizard, as it sends it; the sections are plain JSON objects. */
export interface DeployRequest {
  kind: "create-guest" | "create-service";
  baseCommit: string;
  parent: string;
  name: string;
  workflow: string;
  host?: string | null;
  vmid?: number | null;
  template?: string | null;
  hardware?: Record<string, unknown> | null;
  network?: Record<string, unknown> | null;
  inputs?: Record<string, unknown> | null;
  secrets?: Record<string, SecretChoice> | null;
  accessUser?: string | null;
  hostKey?: string | null;
  approveAtDeploy?: boolean;
  /** What the review returned: a deploy whose workflow release or files differ from it is refused. */
  reviewedRelease?: string | null;
  reviewDigest?: string | null;
}
