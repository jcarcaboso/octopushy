using Octopushy.Definitions;

namespace Octopushy.Validate;

/// <summary>
/// What to validate. <see cref="Repository"/> is a lab repository on disk; its workflows are its own <c>workflows/</c>.
/// A source for a separate workflows repository is meant to be one more option here, <c>--workflows &lt;git url|path&gt;</c>,
/// that picks another <see cref="IWorkflowSource"/>.
/// </summary>
public sealed record ValidateOptions(string Repository)
{
    public const string Usage = """
        Usage: octopushy-validate <repository>

        Reads a lab repository the way Octopushy does, without a server, a database or any credentials,
        and prints what is wrong with it. Workflows are read from the repository's own workflows/.

          <repository>  the directory that holds octopushy.yaml

        Exits 0 when there is no error (warnings are printed), 1 when there is one, and 2 for a usage error.
        """;

    /// <summary>The options, or why the arguments are not usable.</summary>
    public static (ValidateOptions? Options, string? Error, bool Help) Parse(IReadOnlyList<string> args)
    {
        string? repository = null;
        foreach (var arg in args)
        {
            switch (arg)
            {
                case "-h" or "--help":
                    return (null, null, true);
                case { } option when option.StartsWith('-'):
                    return (null, $"Unknown option {option}.", false);
                default:
                    if (repository is not null)
                        return (null, "Name one repository.", false);
                    repository = arg;
                    break;
            }
        }

        if (repository is null)
            return (null, "Name the repository to validate.", false);
        return (new ValidateOptions(repository), null, false);
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

        var repository = LabRepositoryLoader.Load(options.Repository);
        return Report(repository, output);
    }

    /// <summary>Prints every issue, then what each guest and service resolved to, then a summary. Errors make it <see cref="Invalid"/>.</summary>
    public static int Report(LabRepository repository, TextWriter output)
    {
        var issues = repository.Issues
            .OrderBy(i => i.File, StringComparer.Ordinal)
            .ThenBy(i => i.Line ?? 0)
            .ToList();
        foreach (var issue in issues)
        {
            var where = issue.Line is { } line ? $"{issue.File}:{line}" : issue.File;
            var severity = issue.Severity == IssueSeverity.Error ? "error" : "warning";
            var address = issue.Address is null ? "" : $" [{issue.Address}]";
            output.WriteLine($"{where}: {severity}: {issue.Message}{address}");
        }

        var resolved = repository.Guests.Select(g => (g.Address, Reference: g.WorkflowReference.ToString(), g.Workflow))
            .Concat(repository.Services.Select(s => (s.Address, Reference: s.WorkflowReference.ToString(), s.Workflow)))
            .OrderBy(r => r.Address, StringComparer.Ordinal)
            .ToList();
        if (resolved.Count > 0)
        {
            if (issues.Count > 0)
                output.WriteLine();
            output.WriteLine("Resolved:");
            foreach (var (address, reference, workflow) in resolved)
            {
                var valid = repository.IsValid(address) ? "" : " (invalid)";
                output.WriteLine($"  {address}: {reference} -> {workflow?.Label ?? "nothing"}{valid}");
            }
        }

        var errors = issues.Count(i => i.Severity == IssueSeverity.Error);
        var warnings = issues.Count - errors;
        var workflows = repository.Workflows.Select(w => w.Valid ? w.Label : $"{w.Label} (invalid)").Order(StringComparer.Ordinal).ToList();
        output.WriteLine();
        output.WriteLine($"Workflows: {(workflows.Count == 0 ? "none" : string.Join(", ", workflows))}");
        output.WriteLine($"{Count(repository.Guests.Count, "guest")}, {Count(repository.Services.Count, "service")}: "
            + $"{Count(errors, "error")}, {Count(warnings, "warning")}.");
        return errors > 0 ? Invalid : Valid;
    }

    private static string Count(int count, string what) => count == 1 ? $"1 {what}" : $"{count} {what}s";
}
