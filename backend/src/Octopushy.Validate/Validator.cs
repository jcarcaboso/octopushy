using Octopushy.Definitions;

namespace Octopushy.Validate;

/// <summary>
/// What to validate: a lab repository on disk, with the workflows its <c>octopushy.yaml</c> names or those of
/// <see cref="Workflows"/>; or, with <see cref="WorkflowsOnly"/>, a workflows repository's own workflows and nothing else.
/// </summary>
public sealed record ValidateOptions(string Repository, string? Workflows, bool WorkflowsOnly)
{
    public const string Usage = """
        Usage: octopushy-validate <config-dir> [--workflows <git url|path>]
               octopushy-validate --workflows-only <dir>

        Reads a lab repository the way Octopushy does, without a server, a database or any credentials of its own,
        and prints what is wrong with it.

          <config-dir>             the directory that holds octopushy.yaml
          --workflows <url|path>   where workflows come from, instead of what octopushy.yaml says:
                                     a Git URL (https://, ssh://, user@host:path, file://) or a bare repository:
                                       its release tags <id>/v<M>.<m>, resolved exactly as the server does;
                                     a directory with workflows/: each workflow at the version its workflow.yaml
                                       declares, as it is on disk, such as a workflows repository's checkout in its CI.
          --workflows-only <dir>   checks every workflows/*/workflow.yaml in <dir>, and the paths it names, with no
                                   lab repository: for a workflows repository's own CI.

        Without --workflows, workflows are the repository's own workflows/, or the tags of the workflows repository
        its octopushy.yaml names, fetched with Git's own credentials.

        Exits 0 when there is no error (warnings are printed), 1 when there is one, and 2 for a usage error.
        """;

    /// <summary>The options, or why the arguments are not usable.</summary>
    public static (ValidateOptions? Options, string? Error, bool Help) Parse(IReadOnlyList<string> args)
    {
        string? repository = null;
        string? workflows = null;
        string? workflowsOnly = null;
        for (var i = 0; i < args.Count; i++)
        {
            switch (args[i])
            {
                case "-h" or "--help":
                    return (null, null, true);
                case "--workflows" or "--workflows-only" when i + 1 >= args.Count || args[i + 1].StartsWith('-'):
                    return (null, $"{args[i]} needs a value.", false);
                case "--workflows" when workflows is not null:
                case "--workflows-only" when workflowsOnly is not null:
                    return (null, $"{args[i]} is given twice.", false);
                case "--workflows":
                    workflows = args[++i];
                    break;
                case "--workflows-only":
                    workflowsOnly = args[++i];
                    break;
                case { } option when option.StartsWith('-'):
                    return (null, $"Unknown option {option}.", false);
                default:
                    if (repository is not null)
                        return (null, "Name one repository.", false);
                    repository = args[i];
                    break;
            }
        }

        if (workflowsOnly is not null)
            return repository is null && workflows is null
                ? (new ValidateOptions(workflowsOnly, null, true), null, false)
                : (null, "--workflows-only checks workflows alone: name no repository and no --workflows.", false);
        if (repository is null)
            return (null, "Name the repository to validate.", false);
        return (new ValidateOptions(repository, workflows, false), null, false);
    }
}

/// <summary>The offline validator: reads a repository with <see cref="LabRepositoryLoader"/> and reports its issues.</summary>
public static class Validator
{
    public const int Valid = 0;
    public const int Invalid = 1;
    public const int UsageError = 2;

    public static int Run(IReadOnlyList<string> args, TextWriter output, TextWriter error)
    {
        var (options, problem, help) = ValidateOptions.Parse(args);
        if (help)
        {
            output.WriteLine(ValidateOptions.Usage);
            return Valid;
        }

        if (options is null)
        {
            error.WriteLine(problem);
            error.WriteLine();
            error.WriteLine(ValidateOptions.Usage);
            return UsageError;
        }

        if (!Directory.Exists(options.Repository))
        {
            error.WriteLine($"{options.Repository} is not a directory.");
            return UsageError;
        }

        if (options.WorkflowsOnly)
            return ReportWorkflows(new LocalWorkflowSource(options.Repository, options.Repository), output);

        // What octopushy.yaml names is always read as the server reads it, by its tags; a directory given here may be a
        // working tree, read as it is.
        var source = options.Workflows ?? WorkflowsSetting.Read(options.Repository).Repository;
        if (source is null)
            return Report(LabRepositoryLoader.Load(options.Repository), output);
        if (options.Workflows is not null && Directory.Exists(source) && Directory.Exists(Path.Combine(source, "workflows")))
            return Report(LabRepositoryLoader.Load(options.Repository, new LocalWorkflowSource(source, source)), output);
        var url = Directory.Exists(source) ? Path.GetFullPath(source) : source;
        if (GitUrls.Scheme(url) is null)
        {
            error.WriteLine($"{source} is neither a Git URL nor a directory.");
            return UsageError;
        }

        try
        {
            using var workflows = WorkflowsRepository.Fetch(url);
            var warnings = new List<string>();
            var releases = workflows.Releases(warnings);
            var resolutions = WorkflowResolver.ResolveAll(WorkflowReferences.In(options.Repository), releases, url);
            var trees = workflows.Trees(resolutions.Where(r => r.Problem is null).Select(r => r.Commit!));
            return Report(LabRepositoryLoader.Load(options.Repository, new GitTagWorkflowSource(url, resolutions, trees)), output, warnings);
        }
        catch (WorkflowsRepositoryException e)
        {
            error.WriteLine(e.Message);
            return Invalid;
        }
    }

    /// <summary>
    /// Prints every issue, then what each guest and service resolved to, then a summary. Errors make it <see cref="Invalid"/>.
    /// <paramref name="sourceWarnings"/> are about the workflows repository, such as an invalid release, and do not fail it.
    /// </summary>
    public static int Report(LabRepository repository, TextWriter output, IReadOnlyList<string>? sourceWarnings = null)
    {
        var issues = repository.Issues
            .OrderBy(i => i.File, StringComparer.Ordinal)
            .ThenBy(i => i.Line ?? 0)
            .ToList();
        foreach (var warning in sourceWarnings ?? [])
        {
            output.WriteLine($"{repository.WorkflowsRepository}: warning: {warning}");
        }

        Print(issues, output);
        var resolved = repository.Guests.Select(g => (g.Address, Reference: g.WorkflowReference.ToString(), g.Workflow))
            .Concat(repository.Services.Select(s => (s.Address, Reference: s.WorkflowReference.ToString(), s.Workflow)))
            .OrderBy(r => r.Address, StringComparer.Ordinal)
            .ToList();
        if (resolved.Count > 0)
        {
            if (issues.Count + (sourceWarnings?.Count ?? 0) > 0)
                output.WriteLine();
            output.WriteLine("Resolved:");
            foreach (var (address, reference, workflow) in resolved)
            {
                var valid = repository.IsValid(address) ? "" : " (invalid)";
                var at = workflow?.Commit is { } commit ? $" at {WorkflowResolver.Short(commit)}" : "";
                output.WriteLine($"  {address}: {reference} -> {workflow?.Label ?? "nothing"}{at}{valid}");
            }
        }

        var errors = issues.Count(i => i.Severity == IssueSeverity.Error);
        var warnings = issues.Count - errors + (sourceWarnings?.Count ?? 0);
        var workflows = repository.Workflows.Select(w => w.Valid ? w.Label : $"{w.Label} (invalid)").Distinct().Order(StringComparer.Ordinal).ToList();
        output.WriteLine();
        if (repository.WorkflowsRepository is { } source)
            output.WriteLine($"Workflows from {source}");
        output.WriteLine($"Workflows: {(workflows.Count == 0 ? "none" : string.Join(", ", workflows))}");
        output.WriteLine($"{Count(repository.Guests.Count, "guest")}, {Count(repository.Services.Count, "service")}: "
            + $"{Count(errors, "error")}, {Count(warnings, "warning")}.");
        return errors > 0 ? Invalid : Valid;
    }

    /// <summary>Every workflow of a workflows repository, checked with its paths, with no lab repository to name them.</summary>
    public static int ReportWorkflows(LocalWorkflowSource source, TextWriter output)
    {
        var issues = source.Issues.OrderBy(i => i.File, StringComparer.Ordinal).ThenBy(i => i.Line ?? 0).ToList();
        Print(issues, output);
        var errors = issues.Count(i => i.Severity == IssueSeverity.Error);
        var workflows = source.Workflows.Select(w => w.Valid ? w.Label : $"{w.Label} (invalid)").Order(StringComparer.Ordinal).ToList();
        if (issues.Count > 0)
            output.WriteLine();
        output.WriteLine($"Workflows: {(workflows.Count == 0 ? "none" : string.Join(", ", workflows))}");
        output.WriteLine($"{Count(workflows.Count, "workflow")}: {Count(errors, "error")}, {Count(issues.Count - errors, "warning")}.");
        return errors > 0 || workflows.Count == 0 ? Invalid : Valid;
    }

    private static void Print(IEnumerable<DefinitionIssue> issues, TextWriter output)
    {
        foreach (var issue in issues)
        {
            var where = issue.Line is { } line ? $"{issue.File}:{line}" : issue.File;
            var severity = issue.Severity == IssueSeverity.Error ? "error" : "warning";
            var address = issue.Address is null ? "" : $" [{issue.Address}]";
            output.WriteLine($"{where}: {severity}: {issue.Message}{address}");
        }
    }

    private static string Count(int count, string what) => count == 1 ? $"1 {what}" : $"{count} {what}s";
}
