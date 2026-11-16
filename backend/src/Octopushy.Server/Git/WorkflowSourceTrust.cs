using Octopushy.Definitions;
using Octopushy.Server.Data;

namespace Octopushy.Server.Git;

/// <summary>Confirmation covers the URL and the store reference, including a pinned version.</summary>
public static class WorkflowSourceTrust
{
    public static WorkflowsSetting Confirmed(RepositorySettings settings) => new(settings.ConfirmedWorkflowsRepository,
        settings.ConfirmedWorkflowsCredentialKey is { } key ? new StoreReference(key, settings.ConfirmedWorkflowsCredentialVersion) : null);

    public static WorkflowsSetting Pending(RepositorySettings settings) => new(settings.PendingWorkflowsRepository,
        settings.PendingWorkflowsCredentialKey is { } key ? new StoreReference(key, settings.PendingWorkflowsCredentialVersion) : null);

    public static void ClearPending(RepositorySettings settings)
    {
        settings.PendingWorkflowsRepository = null;
        settings.PendingWorkflowsCredentialKey = null;
        settings.PendingWorkflowsCredentialVersion = null;
        settings.PendingWorkflowsCommit = null;
    }

    public static string? Rejection(WorkflowsSetting source, bool allowLocal) => source.Repository is not { } url ? null
        : GitUrls.Scheme(url) is not { } scheme ? "The workflows repository must use HTTPS or SSH."
        : scheme == GitUrls.File && !allowLocal ? "Server-local workflow sources (absolute paths and file:// URLs) are disabled."
        : source.Credential?.Key.StartsWith(LabRepositoryLoader.SystemScope, StringComparison.Ordinal) == true
            ? "The workflows credential cannot use the system scope."
        : source.Credential is not null && url.StartsWith("http://", StringComparison.Ordinal)
            ? "The workflows repository's credential is not sent over http://. Name it by https:// or SSH."
        : null;
}
