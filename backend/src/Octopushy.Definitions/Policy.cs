namespace Octopushy.Definitions;

/// <summary>A resolved setting and the place it was set, shown so the user can find it.</summary>
public sealed record ResolvedSetting<T>(T Value, string SetBy);

/// <summary>The approval and destructive settings that apply to one resource.</summary>
public sealed record EffectivePolicy(ResolvedSetting<string> Approval, ResolvedSetting<string> Destructive)
{
    public bool ApprovalRequired => Approval.Value == "manual";

    public bool DestructiveApprovalRequired => Destructive.Value == "manual";
}

/// <summary>
/// Settings flow down the tree (octopushy.yaml defaults, lab, installation, guest) and the
/// nearest one wins. An unset destructive setting takes the resource's approval setting.
/// </summary>
public static class PolicyResolver
{
    public const string Automatic = "automatic";
    public const string Manual = "manual";

    public static EffectivePolicy For(LabRepository repository, GuestDefinition guest) =>
        Resolve(Chain(repository, guest.Lab, repository.Installation(guest.InstallationAddress))
            .Append((guest.Address, guest.Policy)));

    public static EffectivePolicy For(LabRepository repository, InstallationDefinition installation) =>
        Resolve(Chain(repository, installation.Lab, installation));

    /// <summary>The labels an agent needs to act on the guest; empty means any agent.</summary>
    public static ResolvedSetting<IReadOnlyList<string>> AgentLabels(LabRepository repository, GuestDefinition guest) =>
        ResolveLabels(LabelChain(repository, guest.Lab, repository.Installation(guest.InstallationAddress))
            .Append((guest.Address, guest.AgentLabels)));

    public static ResolvedSetting<IReadOnlyList<string>> AgentLabels(LabRepository repository, InstallationDefinition installation) =>
        ResolveLabels(LabelChain(repository, installation.Lab, installation));

    private static IEnumerable<(string SetBy, PolicySettings? Policy)> Chain(
        LabRepository repository,
        string lab,
        InstallationDefinition? installation)
    {
        yield return ("octopushy.yaml", repository.Settings?.DefaultPolicy);
        yield return (lab, repository.Lab(lab)?.Policy);
        if (installation is not null)
        {
            yield return (installation.Address, installation.Policy);
        }
    }

    private static IEnumerable<(string SetBy, IReadOnlyList<string>? Labels)> LabelChain(
        LabRepository repository,
        string lab,
        InstallationDefinition? installation)
    {
        yield return ("octopushy.yaml", repository.Settings?.DefaultAgentLabels);
        yield return (lab, repository.Lab(lab)?.AgentLabels);
        if (installation is not null)
        {
            yield return (installation.Address, installation.AgentLabels);
        }
    }

    private static EffectivePolicy Resolve(IEnumerable<(string SetBy, PolicySettings? Policy)> chain)
    {
        var approval = new ResolvedSetting<string>(Automatic, "default");
        ResolvedSetting<string>? destructive = null;
        foreach (var (setBy, policy) in chain)
        {
            if (policy?.Approval is { } a)
            {
                approval = new ResolvedSetting<string>(a, setBy);
            }

            if (policy?.Destructive is { } d)
            {
                destructive = new ResolvedSetting<string>(d, setBy);
            }
        }

        return new EffectivePolicy(
            approval,
            destructive ?? new ResolvedSetting<string>(approval.Value, $"{approval.SetBy}, following approval"));
    }

    private static ResolvedSetting<IReadOnlyList<string>> ResolveLabels(IEnumerable<(string SetBy, IReadOnlyList<string>? Labels)> chain)
    {
        var result = new ResolvedSetting<IReadOnlyList<string>>([], "default");
        foreach (var (setBy, labels) in chain)
        {
            if (labels is not null)
            {
                result = new ResolvedSetting<IReadOnlyList<string>>(labels, setBy);
            }
        }

        return result;
    }
}
