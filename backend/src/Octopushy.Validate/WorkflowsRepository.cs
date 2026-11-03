using System.Diagnostics;
using Octopushy.Definitions;

namespace Octopushy.Validate;

/// <summary>A workflows repository could not be read.</summary>
public sealed class WorkflowsRepositoryException(string message) : Exception(message);

/// <summary>
/// A workflows repository fetched for one validation: its tags, into a bare repository of its own that is deleted afterwards.
/// Releases are read as the server reads them, with <see cref="WorkflowTag"/> and <see cref="WorkflowResolver"/>. A
/// validation has no history, so each tag is taken where it points now: a moved tag cannot be told from a new one.
/// </summary>
public sealed class WorkflowsRepository : IDisposable
{
    private readonly DirectoryInfo _scratch;
    private readonly string _bare;

    private WorkflowsRepository(string url)
    {
        Url = url;
        _scratch = Directory.CreateTempSubdirectory("octopushy-validate-");
        _bare = Path.Combine(_scratch.FullName, "workflows.git");
    }

    public string Url { get; }

    /// <summary>Fetches every tag of <paramref name="url"/>, with Git's own credentials and no prompts.</summary>
    public static WorkflowsRepository Fetch(string url)
    {
        var repository = new WorkflowsRepository(url);
        try
        {
            repository.Git(["init", "--bare", "--quiet", repository._bare]);
            repository.Git(["-C", repository._bare, "fetch", "--quiet", "--no-tags", url, "+refs/tags/*:refs/tags/*"]);
            var attributes = Path.Combine(repository._bare, "info", "attributes");
            Directory.CreateDirectory(Path.GetDirectoryName(attributes)!);
            File.WriteAllText(attributes, SnapshotArchive.LiteralAttributes);
            return repository;
        }
        catch
        {
            repository.Dispose();
            throw;
        }
    }

    /// <summary>Every release its tags make; one that cannot be used says why, and adds a warning. Other tags are ignored.</summary>
    public IReadOnlyList<WorkflowRelease> Releases(List<string> warnings)
    {
        var output = Text(Git(["-C", _bare, "for-each-ref", "--format=%(refname:strip=2)%00%(objecttype)%00%(objectname)%00%(*objecttype)%00%(*objectname)", "refs/tags"]));
        var releases = new List<WorkflowRelease>();
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Split('\0');
            var (type, commit) = parts[3].Length > 0 ? (parts[3], parts[4]) : (parts[1], parts[2]);
            if (type != "commit" || WorkflowTag.Parse(parts[0]) is not { } tag)
                continue;
            var problem = WorkflowResolver.CheckRelease(tag, FileAt(commit, tag.File));
            if (problem is not null)
                warnings.Add($"The release {tag} at {WorkflowResolver.Short(commit)} is invalid and is never used: {problem}");
            releases.Add(new WorkflowRelease(tag.WorkflowId, tag.Version, commit, problem));
        }

        return releases;
    }

    /// <summary>The files of each commit, extracted as the server extracts a snapshot.</summary>
    public IReadOnlyDictionary<string, string> Trees(IEnumerable<string> commits)
    {
        var trees = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var commit in commits.Distinct(StringComparer.Ordinal))
        {
            var directory = Path.Combine(_scratch.FullName, "trees", commit);
            Directory.CreateDirectory(directory);
            using var archive = new MemoryStream(Git(["-C", _bare, "archive", "--format=tar", commit]));
            SnapshotArchive.Extract(archive, directory);
            trees[commit] = directory;
        }

        return trees;
    }

    public void Dispose()
    {
        if (_scratch.Exists)
            _scratch.Delete(recursive: true);
    }

    private string? FileAt(string commit, string path)
    {
        var (exit, output, _) = Run(["-C", _bare, "cat-file", "blob", $"{commit}:{path}"]);
        return exit == 0 ? Text(output) : null;
    }

    private byte[] Git(IReadOnlyList<string> arguments)
    {
        var (exit, output, error) = Run(arguments);
        if (exit != 0)
            throw new WorkflowsRepositoryException($"git {arguments.FirstOrDefault(a => !a.StartsWith('-') && a != _bare)} failed for {Url}: {error.Trim()}");
        return output;
    }

    private static (int Exit, byte[] Output, string Error) Run(IReadOnlyList<string> arguments)
    {
        var start = new ProcessStartInfo("git") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);
        start.Environment["GIT_TERMINAL_PROMPT"] = "0";
        start.Environment["LC_ALL"] = "C";
        using var process = Process.Start(start) ?? throw new WorkflowsRepositoryException("Git could not be started.");
        var error = process.StandardError.ReadToEndAsync();
        using var output = new MemoryStream();
        process.StandardOutput.BaseStream.CopyTo(output);
        process.WaitForExit();
        return (process.ExitCode, output.ToArray(), error.Result);
    }

    private static string Text(byte[] bytes) => System.Text.Encoding.UTF8.GetString(bytes);
}
