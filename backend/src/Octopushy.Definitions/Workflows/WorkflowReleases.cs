using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Octopushy.Definitions.Yaml;

namespace Octopushy.Definitions;

/// <summary>
/// A release tag of a workflows repository, <c>&lt;id&gt;/v&lt;M&gt;.&lt;m&gt;</c>, such as <c>fedora-vm/v2.3</c>. Any other tag
/// is not a release and is ignored.
/// </summary>
public sealed partial record WorkflowTag(string WorkflowId, WorkflowVersion Version)
{
    public string Name => $"{WorkflowId}/v{Version}";

    /// <summary>The file a release's commit has to hold, declaring the tag's version.</summary>
    public string File => $"workflows/{WorkflowId}/workflow.yaml";

    public override string ToString() => Name;

    public static WorkflowTag? Parse(string? name)
    {
        if (name is null || TagPattern().Match(name) is not { Success: true } match
            || !WorkflowVersion.TryParse(match.Groups[2].Value, out var version))
            return null;
        return new WorkflowTag(match.Groups[1].Value, version);
    }

    [GeneratedRegex(@"^([a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?)/v([0-9.]+)\z")]
    private static partial Regex TagPattern();
}

/// <summary>
/// A workflow at one version and the commit its tag pointed to the first time it was seen. <paramref name="Problem"/> says
/// why the release cannot be used, such as a <c>workflow.yaml</c> that declares another version; such a release is never
/// resolved to. <paramref name="Available"/> is false when the commit is no longer in the mirror, as after its tag was
/// deleted and the mirror rebuilt; a reference that resolves to it is invalid.
/// </summary>
public sealed record WorkflowRelease(string WorkflowId, WorkflowVersion Version, string Commit, string? Problem = null, bool Available = true)
{
    public WorkflowTag Tag => new(WorkflowId, Version);
}

/// <summary>
/// What one reference resolved to in a workflows repository: the workflow, its version and the commit to read it from, or
/// why there is none. A config revision records one for every reference it holds.
/// </summary>
public sealed record ResolvedWorkflow(string Reference, string WorkflowId, string? Version, string? Commit, string? Problem)
{
    [System.Text.Json.Serialization.JsonIgnore]
    public string? Label => Version is null ? null : $"{WorkflowId}@{Version}";
}

/// <summary>
/// How references are resolved against the releases of a workflows repository. The server and the offline validator both
/// resolve through this, so they always agree.
/// </summary>
public static class WorkflowResolver
{
    /// <summary>
    /// <c>@M</c> takes the highest minor among the usable releases of major <c>M</c>; <c>@M.m</c> takes exactly that release.
    /// A release with a problem is never taken. A release whose commit is gone is taken, and the reference says why it
    /// cannot be read.
    /// </summary>
    public static ResolvedWorkflow Resolve(WorkflowReference reference, IEnumerable<WorkflowRelease> releases, string repository)
    {
        var text = reference.ToString();
        var all = releases.Where(r => r.WorkflowId == reference.Id).ToList();
        if (all.Count == 0)
            return new(text, reference.Id, null, null,
                $"{reference} not found: {repository} has no release of {reference.Id}, a tag {reference.Id}/v<major>.<minor>.");

        var usable = all.Where(r => r.Problem is null).ToList();
        if (reference.Minor is not null && all.FirstOrDefault(r => reference.Matches(r.Version)) is { Problem: { } problem } broken)
            return new(text, reference.Id, null, null, $"{reference} cannot be used: the release {broken.Tag} is invalid. {problem}");
        if (usable.Where(r => reference.Matches(r.Version)).MaxBy(r => r.Version) is not { } match)
        {
            var available = usable.Count == 0 ? "no usable release" : "available " + string.Join(", ", usable.Select(r => r.Version).Order().Select(v => v.ToString()));
            return new(text, reference.Id, null, null, $"{reference} not found in {repository}; {available}.");
        }

        return match.Available
            ? new(text, reference.Id, match.Version.ToString(), match.Commit, null)
            : new(text, reference.Id, match.Version.ToString(), match.Commit,
                $"{reference} resolved to {reference.Id}@{match.Version}, whose commit {Short(match.Commit)} is no longer in the mirror of {repository}. "
                + $"Its tag {match.Tag} was deleted; restore the tag at that commit, or name another version.");
    }

    public static IReadOnlyList<ResolvedWorkflow> ResolveAll(IEnumerable<WorkflowReference> references, IReadOnlyList<WorkflowRelease> releases, string repository) =>
        references.Select(r => r.ToString()).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)
            .Select(text => Resolve(WorkflowReference.Parse(text)!, releases, repository))
            .ToList();

    /// <summary>
    /// The digest of a revision's resolutions. Together with the config commit it is the revision's identity, so a new tag
    /// that changes what a reference resolves to makes a new revision. It covers what was resolved, not the wording of a
    /// problem: the same tags give the same digest whatever a message says.
    /// </summary>
    public static string Digest(string repository, IEnumerable<ResolvedWorkflow> resolutions) => Canonical.Hash(new JsonObject
    {
        ["repository"] = repository,
        ["resolutions"] = new JsonArray(resolutions.OrderBy(r => r.Reference, StringComparer.Ordinal)
            .Select(r => (JsonNode)new JsonObject
            {
                ["reference"] = r.Reference,
                ["version"] = r.Version,
                ["commit"] = r.Commit,
                ["usable"] = r.Problem is null,
            })
            .ToArray()),
    });

    /// <summary>
    /// Why a tagged commit is not a release of <paramref name="tag"/>, or null when it is: its <c>workflow.yaml</c>, given as
    /// <paramref name="workflowYaml"/> (null when the file does not exist there), must declare exactly the tag's version.
    /// </summary>
    public static string? CheckRelease(WorkflowTag tag, string? workflowYaml)
    {
        if (workflowYaml is null)
            return $"{tag.File} does not exist at the tagged commit.";
        JsonNode? root;
        try
        {
            root = YamlDocumentReader.Read(workflowYaml).Root;
        }
        catch (YamlReadException e)
        {
            return $"{tag.File} is not valid YAML at the tagged commit: {e.Message}";
        }

        if (root?["version"] is JsonValue value && !value.TryGetValue<string>(out _))
            return $"{tag.File} declares version {value.ToJsonString()} unquoted, which YAML reads as a number: quote it.";
        var declared = root?["version"]?.GetValue<string>();
        if (!WorkflowVersion.TryParse(declared, out var version) || version != tag.Version)
            return $"{tag.File} declares version {(declared is null ? "nothing" : $"\"{declared}\"")}, and the tag says {tag.Version}.";
        return null;
    }

    public static string Short(string commit) => commit[..Math.Min(10, commit.Length)];
}

/// <summary>Every workflow reference a config repository's guests and services write, found without reading anything else.</summary>
public static class WorkflowReferences
{
    /// <summary>
    /// The references in <c>guest.yaml</c> and <c>service.yaml</c> files, where the loader looks for them. A file that cannot be
    /// read is skipped: the loader reports it.
    /// </summary>
    public static IReadOnlyList<WorkflowReference> In(string root)
    {
        var found = new SortedSet<string>(StringComparer.Ordinal);
        void Read(string file)
        {
            if (!File.Exists(file))
                return;
            try
            {
                if (YamlDocumentReader.Read(File.ReadAllText(file)).Root?["workflow"] is JsonValue value
                    && value.TryGetValue<string>(out var text) && WorkflowReference.Parse(text) is not null)
                    found.Add(text);
            }
            catch (YamlReadException)
            {
            }
        }

        foreach (var lab in DefinitionFiles.SubDirectories(Path.Combine(root, "labs")))
        foreach (var installation in DefinitionFiles.SubDirectories(Path.Combine(lab, "proxmox")))
        foreach (var guest in DefinitionFiles.SubDirectories(Path.Combine(installation, "guests")))
        {
            Read(Path.Combine(guest, "guest.yaml"));
            foreach (var service in DefinitionFiles.SubDirectories(Path.Combine(guest, "services")))
                Read(Path.Combine(service, "service.yaml"));
        }

        return found.Select(text => WorkflowReference.Parse(text)!).ToList();
    }
}

/// <summary>
/// Where <c>octopushy.yaml</c> says workflows come from: <c>workflows: { source: local }</c>, the default, or
/// <c>workflows: { repository: &lt;url or path&gt;, credential: { store: &lt;key&gt; } }</c>. Read leniently, before the
/// repository is loaded; the loader checks the file against its schema.
/// </summary>
public sealed record WorkflowsSetting(string? Repository, StoreReference? Credential)
{
    public static readonly WorkflowsSetting Local = new(null, null);

    public bool External => Repository is not null;

    public static WorkflowsSetting Read(string root)
    {
        var file = Path.Combine(root, "octopushy.yaml");
        if (!File.Exists(file))
            return Local;
        try
        {
            var workflows = YamlDocumentReader.Read(File.ReadAllText(file)).Root?["workflows"];
            if (workflows?["repository"] is not JsonValue value || !value.TryGetValue<string>(out var repository) || string.IsNullOrWhiteSpace(repository))
                return Local;
            var credential = workflows["credential"]?["store"] is JsonValue key && key.TryGetValue<string>(out var store)
                ? new StoreReference(store, workflows["credential"]?["version"] is JsonValue v && v.TryGetValue<int>(out var number) ? number : null)
                : null;
            return new WorkflowsSetting(repository.Trim(), credential);
        }
        catch (Exception e) when (e is YamlReadException or InvalidOperationException or FormatException)
        {
            return Local;
        }
    }
}

/// <summary>The kinds of Git URL a repository can be named by, which decide whether a credential can be reused.</summary>
public static partial class GitUrls
{
    public const string Https = "https";
    public const string Ssh = "ssh";
    public const string File = "file";

    /// <summary><see cref="Https"/>, <see cref="Ssh"/> or <see cref="File"/>, or null for anything Octopushy does not fetch from.</summary>
    public static string? Scheme(string url)
    {
        if (url.StartsWith('-'))
            return null;
        if (url.StartsWith("https://", StringComparison.Ordinal) || url.StartsWith("http://", StringComparison.Ordinal))
            return Https;
        if (url.StartsWith("ssh://", StringComparison.Ordinal) || ScpLike().IsMatch(url))
            return Ssh;
        if (url.StartsWith("file://", StringComparison.Ordinal) || url.StartsWith('/'))
            return File;
        return null;
    }

    [GeneratedRegex("^[A-Za-z0-9._-]+@[A-Za-z0-9.-]+:[^-].*$")]
    private static partial Regex ScpLike();
}
