using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Octopushy.Definitions;
using Octopushy.Definitions.Yaml;
using Octopushy.Server.Data;
using Octopushy.Server.Git;

namespace Octopushy.Server.Changes;

/// <summary>Why a change was not committed, with a short code a form can act on (<see cref="ChangeRefusal.Reasons"/>).</summary>
public sealed class ChangeRefusal(string reason, string message, IReadOnlyList<Api.IssueDto>? issues = null, IReadOnlyList<FieldIssueDto>? fieldIssues = null)
    : Exception(message)
{
    public static class Reasons
    {
        /// <summary>Committing from the UI is off, or the repository cannot be pushed to.</summary>
        public const string Disabled = "disabled";
        /// <summary>The resource is under an installation with <c>manage: false</c>.</summary>
        public const string ObservedOnly = "observed-only";
        /// <summary>The resource is not in the repository any more, or is retired.</summary>
        public const string Unavailable = "unavailable";
        /// <summary>A file the change touches changed since the form was loaded.</summary>
        public const string Stale = "stale";
        /// <summary>The result would add errors to the repository.</summary>
        public const string Invalid = "invalid";
        /// <summary>The form changes nothing in the file.</summary>
        public const string NoChange = "no-change";
        /// <summary>The file uses YAML this cannot edit reliably, or the change is too large for one commit here.</summary>
        public const string Unsupported = "unsupported";
        /// <summary>The push itself failed, as when the credential cannot write.</summary>
        public const string PushFailed = "push-failed";
    }

    public string Reason { get; } = reason;

    public IReadOnlyList<Api.IssueDto> Issues { get; } = issues ?? [];

    public IReadOnlyList<FieldIssueDto> FieldIssues { get; } = fieldIssues ?? [];
}

/// <summary>A change as one commit: the files it writes or deletes, the message, and what it touches for compare-and-swap.</summary>
/// <param name="Folder">For a removal, the folder deleted: any file under it that moves since the form was loaded refuses the change.</param>
public sealed record ComposedChange(
    string Kind,
    string ResourceKind,
    string Address,
    string Name,
    string DefinitionFile,
    IReadOnlyList<DraftFileChange> Changes,
    IReadOnlyList<string> Files,
    string? Folder,
    string Subject,
    string Message,
    string? NewText,
    JsonObject? Desired,
    JsonObject? Edited = null)
{
    /// <summary>Whether a path that moved on the branch is one this change touches.</summary>
    public bool Touches(string path) =>
        Files.Contains(path, StringComparer.Ordinal) || Folder is { } folder && path.StartsWith(folder + "/", StringComparison.Ordinal);
}

/// <summary>
/// Turns a form's values into the edit of one resource's own files: <c>guest.yaml</c> or <c>service.yaml</c> rewritten with
/// <see cref="YamlEditor"/> so only the changed values move, or, to remove a guest, its folder deleted. Nothing outside that
/// folder is ever touched, and <c>octopushy.yaml</c> and installation files never are.
/// </summary>
public static class ChangeComposer
{
    /// <summary>The sections of a definition a form edits; a service has only <c>inputs</c>.</summary>
    public static readonly string[] GuestSections = ["hardware", "network", "inputs"];

    private const int SubjectChanges = 3;

    public static ComposedChange Compose(ChangeRequest request, Guid id, string resourceKind, string address, string file, string folder,
        string text, IReadOnlyList<TreeFile> tree)
    {
        var name = address.Split('/')[^1];
        if (request.Kind == ChangeKinds.Remove)
        {
            if (resourceKind != ResourceKinds.Guest)
                throw new ChangeRefusal(ChangeRefusal.Reasons.Unsupported, "Only a guest can be removed here. Removing a service is a later step.");
            var files = tree.Where(f => f.Path.StartsWith(folder + "/", StringComparison.Ordinal)).Select(f => f.Path).ToList();
            if (files.Count > 64)
                throw new ChangeRefusal(ChangeRefusal.Reasons.Unsupported,
                    $"{folder} holds {files.Count} files, more than one change here deletes; remove it in the repository editor or in Git.");
            var subject = $"{name}: remove the guest";
            return new ComposedChange(ChangeKinds.Remove, resourceKind, address, name, file, files.Select(p => new DraftFileChange(p, null)).ToList(),
                files, folder, subject, Message(subject, [], id), null, null);
        }

        if (request.Kind is not (ChangeKinds.Edit or ChangeKinds.Upgrade))
            throw new ChangeRefusal(ChangeRefusal.Reasons.Unsupported, "A change is an edit, an upgrade or a removal.");
        if (resourceKind == ResourceKinds.Service && (request.Hardware is not null || request.Network is not null))
            throw new ChangeRefusal(ChangeRefusal.Reasons.Invalid, "A service has inputs only: no hardware or network.");

        JsonObject original;
        try
        {
            original = YamlDocumentReader.Read(text).Root as JsonObject
                ?? throw new ChangeRefusal(ChangeRefusal.Reasons.Unsupported, $"{file} is not a YAML mapping; fix it in the repository editor.");
        }
        catch (YamlReadException e)
        {
            throw new ChangeRefusal(ChangeRefusal.Reasons.Unsupported, $"{file} cannot be read ({e.Message}); fix it in the repository editor.");
        }

        var desired = original.DeepClone().AsObject();
        var kind = ChangeKinds.Edit;
        if (request.Workflow is { } workflow)
        {
            var wanted = WorkflowReference.Parse(workflow.Trim());
            if (wanted is null)
                throw new ChangeRefusal(ChangeRefusal.Reasons.Invalid, "Name a workflow with its version: <id>@<major> or <id>@<major>.<minor>.",
                    fieldIssues: [new FieldIssueDto("/workflow", "Name a workflow with its version: <id>@<major> or <id>@<major>.<minor>.")]);
            var current = WorkflowReference.Parse(original["workflow"]?.GetValueKind() == JsonValueKind.String ? original["workflow"]!.GetValue<string>() : null);
            var upgrade = current is not null && current.Id == wanted.Id && wanted.Major > current.Major;
            if (request.Kind == ChangeKinds.Upgrade && !upgrade)
                throw new ChangeRefusal(ChangeRefusal.Reasons.Invalid, "An upgrade moves to a newer major of the same workflow.");
            kind = upgrade ? ChangeKinds.Upgrade : ChangeKinds.Edit;
            desired["workflow"] = wanted.ToString();
        }
        else if (request.Kind == ChangeKinds.Upgrade)
        {
            throw new ChangeRefusal(ChangeRefusal.Reasons.Invalid, "An upgrade names the workflow at its new major.");
        }

        Set(desired, original, "hardware", request.Hardware);
        Set(desired, original, "network", request.Network);
        Set(desired, original, "inputs", request.Inputs);

        string edited;
        try
        {
            edited = YamlEditor.Apply(text, desired);
        }
        catch (YamlEditException e)
        {
            throw new ChangeRefusal(ChangeRefusal.Reasons.Unsupported, $"{file}: {e.Message}");
        }

        if (edited == text)
            throw new ChangeRefusal(ChangeRefusal.Reasons.NoChange, $"The form makes no change to {file}.");

        var differences = new List<string>();
        Describe(original["workflow"], desired["workflow"], "workflow", differences);
        foreach (var section in GuestSections)
            Describe(original[section], desired[section], "", differences, section);
        var summary = differences.Count == 0 ? "edit its definition"
            : string.Join(", ", differences.Take(SubjectChanges)) + (differences.Count > SubjectChanges ? $" and {differences.Count - SubjectChanges} more" : "");
        var line = Truncate($"{name}: {summary}", 200);
        // What a later revision has to keep for this change to be the one applied: each section it wrote, a null for one removed.
        var written = new JsonObject();
        foreach (var key in GuestSections.Prepend("workflow").Where(k => !YamlEditor.Same(original[k], desired[k])))
            written[key] = desired[key]?.DeepClone();
        return new ComposedChange(kind, resourceKind, address, name, file, [new DraftFileChange(file, edited)], [file], null, line,
            Message(line, differences.Count > 1 ? differences : [], id), edited, desired, written);
    }

    /// <summary>The commit message: the subject, every difference when there are several, and the trailer that names the change.</summary>
    private static string Message(string subject, IReadOnlyList<string> differences, Guid id)
    {
        var message = new StringBuilder(subject).Append("\n\n");
        foreach (var difference in differences.Take(50))
            message.Append("- ").Append(difference).Append('\n');
        if (differences.Count > 0)
            message.Append('\n');
        return message.Append("Octopushy-Change: ").Append(id).Append('\n').ToString();
    }

    /// <summary>
    /// Sets a section from the form. An empty object for a section the file does not have leaves it out, so opening a form and
    /// committing it changes nothing that was not changed.
    /// </summary>
    private static void Set(JsonObject desired, JsonObject original, string key, JsonObject? value)
    {
        if (value is null)
            return;
        var normalized = YamlEditor.Normalize(value)!.AsObject();
        if (normalized.Count == 0 && !original.ContainsKey(key))
            return;
        desired[key] = normalized;
    }

    /// <summary>What differs between two values, leaf by leaf, as "memory 4096 → 8192".</summary>
    private static void Describe(JsonNode? before, JsonNode? after, string path, List<string> into, string? section = null)
    {
        if (YamlEditor.Same(before, after))
            return;
        if (before is JsonObject b && after is JsonObject a)
        {
            foreach (var key in b.Select(p => p.Key).Concat(a.Select(p => p.Key).Where(k => !b.ContainsKey(k))))
                Describe(b[key], a[key], path.Length == 0 ? key : $"{path}.{key}", into, section);
            return;
        }

        if (before is JsonArray ba && after is JsonArray aa && ba.Count == aa.Count && ba.Concat(aa).Any(n => n is JsonObject))
        {
            for (var i = 0; i < ba.Count; i++)
                Describe(ba[i], aa[i], $"{(path.Length == 0 ? section : path)}[{i}]", into, section);
            return;
        }

        var label = path.Length == 0 ? section ?? "value" : path;
        into.Add(before is null ? $"{label} set to {Render(after)}"
            : after is null ? $"{label} unset"
            : $"{label} {Render(before)} → {Render(after)}");
    }

    private static string Render(JsonNode? value) => Truncate(value switch
    {
        null => "null",
        JsonArray a => "[" + string.Join(", ", a.Select(Render)) + "]",
        JsonObject o => "{" + string.Join(", ", o.Select(p => $"{p.Key}: {Render(p.Value)}")) + "}",
        _ when value.GetValueKind() == JsonValueKind.String => value.GetValue<string>(),
        _ => value.ToJsonString(),
    }, 40);

    private static string Truncate(string text, int length) => text.Length <= length ? text : text[..(length - 1)] + "…";
}
