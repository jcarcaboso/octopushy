using System.Diagnostics;

namespace Octopushy.Tests;

/// <summary>
/// A workflows repository in a temporary Git repository, released by tags as <c>octopushy-lab</c> is: <c>test-vm</c>, the
/// workflow of <see cref="TestRepository.Minimal"/>, at <c>1.0</c> and tagged <c>test-vm/v1.0</c> unless a test says otherwise.
/// </summary>
public sealed class TestWorkflows : IDisposable
{
    public const string Workflow = "workflows/test-vm/workflow.yaml";
    public const string Module = "modules/opentofu/test-vm/main.tf";

    private TestWorkflows()
    {
        Files = new TestRepository();
        Git("init", "--quiet", "-b", "main");
    }

    public TestRepository Files { get; }

    public string Root => Files.Root;

    /// <summary>The repository, committed and tagged at 1.0.</summary>
    public static TestWorkflows Create(bool tag = true)
    {
        var workflows = new TestWorkflows();
        workflows.WriteVersion("1.0");
        workflows.Files.Write(Module, """
            variable "guest" {
              type = any
            }

            resource "terraform_data" "guest" {
              input = var.guest
            }
            """);
        workflows.Commit("test-vm 1.0");
        if (tag)
            workflows.Tag("test-vm/v1.0");
        return workflows;
    }

    /// <summary>The workflow.yaml of <c>test-vm</c>, declaring <paramref name="version"/>.</summary>
    public void WriteVersion(string version, string description = "A test VM.") => Files.Write(Workflow, $$"""
        schemaVersion: 1
        version: "{{version}}"
        manages: vm
        description: {{description}}
        stages:
          - name: machine
            executor: opentofu
            root: ../../modules/opentofu/test-vm
            verify: proxmox
        """);

    /// <summary>Commits everything and returns the commit.</summary>
    public string Commit(string message)
    {
        Git("add", "-A");
        Git("-c", "user.name=Test", "-c", "user.email=test@example.invalid", "commit", "--quiet", "--allow-empty", "-m", message);
        return Head;
    }

    public string Head => Git("rev-parse", "HEAD").Trim();

    /// <summary>Tags the head, or with <paramref name="force"/>, moves an existing tag to it.</summary>
    public void Tag(string name, bool force = false)
    {
        if (force)
            Git("tag", "-f", name);
        else
            Git("tag", name);
    }

    public void DeleteTag(string name) => Git("tag", "-d", name);

    /// <summary>A lab repository that names this one and holds no workflows of its own.</summary>
    public TestRepository Config(TestRepository? config = null)
    {
        config ??= TestRepository.Minimal();
        config.Delete("workflows");
        config.Delete("modules");
        config.Write("octopushy.yaml", $"schemaVersion: 1\nworkflows:\n  repository: {Root}\n");
        return config;
    }

    public string Git(params string[] arguments) => Run(Root, arguments);

    public static string Run(string directory, params string[] arguments)
    {
        var start = new ProcessStartInfo("git") { WorkingDirectory = directory, RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        start.Environment["GIT_CONFIG_GLOBAL"] = "/dev/null";
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"git {string.Join(' ', arguments)}: {error}");
        return output;
    }

    public void Dispose() => Files.Dispose();
}
