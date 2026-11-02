namespace Octopushy.Definitions;

/// <summary>Which guests Octopushy reaches with a key of its own, which it generates and keeps.</summary>
public static class ManagedAccess
{
    /// <summary>
    /// A guest Octopushy configures, unless it names a key for a machine Octopushy did not create.
    /// A pipeline stage logs in with the same key and does not widen this: a guest with stages
    /// but no setup recipe has to name one, which the repository checks.
    /// </summary>
    public static bool Wanted(GuestDefinition guest) =>
        guest.SetupRecipe is not null && guest.Json["access"]?["sshKey"] is null;
}
