namespace Octopushy.Definitions;

/// <summary>Which guests Octopushy reaches with a key of its own, which it generates and keeps.</summary>
public static class ManagedAccess
{
    /// <summary>
    /// A guest Octopushy provisions that runs any Ansible work, from a setup recipe or a stage of one of its
    /// services alike, unless it names a key itself. The key follows who manages the machine, not what kind of
    /// work runs on it, and <c>access.sshKey</c> always wins. A managed worker is configured by its image.
    /// </summary>
    public static bool Wanted(LabRepository repository, GuestDefinition guest) =>
        guest.Json["access"]?["sshKey"] is null
        && guest.ManagedWorker is null
        && (guest.SetupRecipe is not null || RunsPlaybooks(repository, guest));

    /// <summary>Whether a service on the guest has a stage that runs on it: a recipe with <c>operation: configure</c>.</summary>
    public static bool RunsPlaybooks(LabRepository repository, GuestDefinition guest) =>
        repository.Services.Any(s => s.GuestAddress == guest.Address
            && s.Stages.Any(stage => repository.Recipes.GetValueOrDefault(stage.Recipe)?.Configure is not null));
}
