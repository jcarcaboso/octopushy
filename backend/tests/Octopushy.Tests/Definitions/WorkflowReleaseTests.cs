using Octopushy.Definitions;

namespace Octopushy.Tests.Definitions;

/// <summary>Releases of a workflows repository: tags, resolving references against them, and what a revision is read with.</summary>
public class WorkflowReleaseTests
{
    private const string Repository = "https://git.example.org/octopushy-lab.git";
    private const string Guest = "home/korriban/krb-app-01";
    private const string GuestFile = "labs/home/proxmox/korriban/guests/krb-app-01/guest.yaml";

    private static string CommitOf(char c) => new(c, 40);

    private static WorkflowRelease Release(string version, char commit, string? problem = null, bool available = true)
    {
        WorkflowVersion.TryParse(version, out var parsed);
        return new WorkflowRelease("fedora-vm", parsed, CommitOf(commit), problem, available);
    }

    private static ResolvedWorkflow Resolve(string reference, params WorkflowRelease[] releases) =>
        WorkflowResolver.Resolve(WorkflowReference.Parse(reference)!, releases, Repository);

    [Theory]
    [InlineData("fedora-vm/v2.3", "fedora-vm", 2, 3)]
    [InlineData("a/v0.0", "a", 0, 0)]
    [InlineData("technitium-dns/v10.12", "technitium-dns", 10, 12)]
    public void A_release_tag_is_id_slash_v_major_dot_minor(string name, string id, int major, int minor)
    {
        var tag = WorkflowTag.Parse(name);
        Assert.Equal(new WorkflowTag(id, new WorkflowVersion(major, minor)), tag);
        Assert.Equal(name, tag!.Name);
        Assert.Equal($"workflows/{id}/workflow.yaml", tag.File);
    }

    [Theory]
    [InlineData("fedora-vm/v02.3")]
    [InlineData("fedora-vm/v2.03")]
    [InlineData("fedora-vm/2.3")]
    [InlineData("fedora-vm/v2")]
    [InlineData("fedora-vm/v2.3.1")]
    [InlineData("fedora-vm/v2.3-rc1")]
    [InlineData("Fedora-vm/v2.3")]
    [InlineData("v2.3")]
    [InlineData("labs/fedora-vm/v2.3")]
    [InlineData("fedora-vm/v-1.0")]
    public void Any_other_tag_is_not_a_release(string name) => Assert.Null(WorkflowTag.Parse(name));

    [Fact]
    public void At_major_takes_the_highest_minor_of_that_major_and_at_major_minor_exactly_that_one()
    {
        WorkflowRelease[] releases = [Release("2.0", 'a'), Release("2.3", 'b'), Release("2.10", 'c'), Release("3.0", 'd')];
        Assert.Equal(new ResolvedWorkflow("fedora-vm@2", "fedora-vm", "2.10", CommitOf('c'), null), Resolve("fedora-vm@2", releases));
        Assert.Equal(new ResolvedWorkflow("fedora-vm@2.3", "fedora-vm", "2.3", CommitOf('b'), null), Resolve("fedora-vm@2.3", releases));
        Assert.Equal("3.0", Resolve("fedora-vm@3", releases).Version);
        Assert.Equal($"fedora-vm@4 not found in {Repository}; available 2.0, 2.3, 2.10, 3.0.", Resolve("fedora-vm@4", releases).Problem);
        Assert.Equal($"fedora-vm@2.4 not found in {Repository}; available 2.0, 2.3, 2.10, 3.0.", Resolve("fedora-vm@2.4", releases).Problem);
        Assert.StartsWith("web@1 not found: ", Resolve("web@1", releases).Problem);
    }

    [Fact]
    public void An_invalid_release_is_never_taken_and_naming_it_exactly_says_why()
    {
        WorkflowRelease[] releases = [Release("2.3", 'b'), Release("2.4", 'c', "workflows/fedora-vm/workflow.yaml declares version \"2.3\", and the tag says 2.4.")];
        Assert.Equal("2.3", Resolve("fedora-vm@2", releases).Version);
        var exact = Resolve("fedora-vm@2.4", releases);
        Assert.Null(exact.Version);
        Assert.Equal("fedora-vm@2.4 cannot be used: the release fedora-vm/v2.4 is invalid. workflows/fedora-vm/workflow.yaml declares version \"2.3\", and the tag says 2.4.",
            exact.Problem);
    }

    [Fact]
    public void A_release_whose_commit_is_gone_is_still_taken_and_the_reference_says_it_cannot_be_read()
    {
        var resolved = Resolve("fedora-vm@2", Release("2.3", 'b'), Release("2.4", 'c', available: false));
        Assert.Equal("2.4", resolved.Version);
        Assert.Equal(CommitOf('c'), resolved.Commit);
        Assert.Contains("is no longer in the mirror", resolved.Problem);
        Assert.Contains("fedora-vm/v2.4 was deleted", resolved.Problem);
    }

    [Theory]
    [InlineData("schemaVersion: 1\nversion: \"2.3\"\n", null)]
    [InlineData("schemaVersion: 1\nversion: \"2.4\"\n", "workflows/fedora-vm/workflow.yaml declares version \"2.4\", and the tag says 2.3.")]
    [InlineData("schemaVersion: 1\nversion: 2.3\n", "workflows/fedora-vm/workflow.yaml declares version 2.3 unquoted, which YAML reads as a number: quote it.")]
    [InlineData("schemaVersion: 1\n", "workflows/fedora-vm/workflow.yaml declares version nothing, and the tag says 2.3.")]
    [InlineData(null, "workflows/fedora-vm/workflow.yaml does not exist at the tagged commit.")]
    public void A_release_needs_its_workflow_yaml_to_declare_the_tags_version(string? text, string? problem)
    {
        Assert.Equal(problem, WorkflowResolver.CheckRelease(WorkflowTag.Parse("fedora-vm/v2.3")!, text));
    }

    [Fact]
    public void The_digest_follows_what_was_resolved_and_not_how_a_problem_is_worded()
    {
        var one = new ResolvedWorkflow("fedora-vm@2", "fedora-vm", "2.3", CommitOf('b'), null);
        var digest = WorkflowResolver.Digest(Repository, [one]);
        Assert.Equal(digest, WorkflowResolver.Digest(Repository, [one with { }]));
        Assert.NotEqual(digest, WorkflowResolver.Digest(Repository, [one with { Version = "2.4", Commit = CommitOf('c') }]));
        Assert.NotEqual(digest, WorkflowResolver.Digest("https://elsewhere.example/lab.git", [one]));
        var missing = new ResolvedWorkflow("fedora-vm@3", "fedora-vm", null, null, "not found");
        Assert.Equal(WorkflowResolver.Digest(Repository, [missing]), WorkflowResolver.Digest(Repository, [missing with { Problem = "worded otherwise" }]));
    }

    [Fact]
    public void A_lab_repository_with_workflows_from_elsewhere_holds_no_workflows_or_modules_of_its_own()
    {
        using var workflows = TestWorkflows.Create();
        using var config = TestRepository.Minimal();
        config.Write("octopushy.yaml", $"schemaVersion: 1\nworkflows:\n  repository: {workflows.Root}\n");
        var repository = LabRepositoryLoader.Load(config.Root);
        Assert.False(repository.RepositoryValid);
        var issues = repository.Issues.Where(i => i.Address is null && i.Severity == IssueSeverity.Error).ToList();
        Assert.Contains(issues, i => i.File == "workflows" && i.Message == $"Workflows come from {workflows.Root}; remove workflows/ and modules/ here.");
        Assert.Contains(issues, i => i.File == "modules");

        // Without them, it is data only; a plain load has no releases to resolve against.
        workflows.Config(config);
        repository = LabRepositoryLoader.Load(config.Root);
        Assert.True(repository.RepositoryValid);
        Assert.Equal(workflows.Root, repository.WorkflowsRepository);
        Assert.False(repository.IsValid(Guest));
        Assert.Contains($"workflows come from {workflows.Root}", repository.IssuesFor(Guest).Single(i => i.File == GuestFile).Message);
    }

    [Theory]
    [InlineData("workflows: { source: local, repository: /srv/lab }", "workflows: name either source: local or a repository, not both.")]
    [InlineData("workflows: { repository: lab.git }", "workflows.repository: 'lab.git' is not a repository Octopushy can fetch")]
    [InlineData("workflows: { source: local, credential: { store: git/lab } }", "workflows: Some required property dependencies are missing: [\"repository\"]")]
    public void The_workflows_setting_names_one_source(string setting, string message)
    {
        using var config = TestRepository.Minimal();
        config.Write("octopushy.yaml", $"schemaVersion: 1\n{setting}\n");
        var repository = LabRepositoryLoader.Load(config.Root);
        Assert.False(repository.RepositoryValid);
        Assert.Contains(repository.Issues, i => i.File == "octopushy.yaml" && i.Message.Contains(message, StringComparison.Ordinal));
    }

    [Fact]
    public void A_release_reads_from_its_commits_files_and_the_same_files_at_another_commit_hash_the_same()
    {
        using var workflows = TestWorkflows.Create();
        using var config = workflows.Config();
        using var copy = new TestRepository();
        foreach (var file in new[] { TestWorkflows.Workflow, TestWorkflows.Module })
            copy.Write(file, File.ReadAllText(Path.Combine(workflows.Root, file)).TrimEnd('\n'));

        LabRepository Read(string commit, string tree) => LabRepositoryLoader.Load(config.Root, new GitTagWorkflowSource(Repository,
            [new ResolvedWorkflow("test-vm@1", "test-vm", "1.0", commit, null)], new Dictionary<string, string> { [commit] = tree }));
        string Hash(LabRepository repository) => ProvisionInputs.Build(repository, repository.Guest(Guest)!, "c").Hash;

        var first = Read(CommitOf('a'), workflows.Root);
        Assert.True(first.IsValid(Guest), string.Join("\n", first.Issues.Select(i => i.Message)));
        var workflow = first.Guest(Guest)!.Workflow!;
        Assert.Equal((Repository, CommitOf('a'), "test-vm@1.0"), (workflow.Repository, workflow.Commit, workflow.Label));
        Assert.Equal(workflows.Root, workflow.SourceRoot);
        Assert.Equal([new ResolvedWorkflow("test-vm@1", "test-vm", "1.0", CommitOf('a'), null)], first.WorkflowResolutions);

        // Another commit with the same files and version plans nothing new; a change to the module does.
        Assert.Equal(Hash(first), Hash(Read(CommitOf('b'), copy.Root)));
        copy.Write(TestWorkflows.Module, "resource \"terraform_data\" \"other\" {}");
        Assert.NotEqual(Hash(first), Hash(Read(CommitOf('b'), copy.Root)));
    }

    [Fact]
    public void A_release_that_cannot_be_read_says_so_on_what_names_it()
    {
        using var workflows = TestWorkflows.Create();
        using var config = workflows.Config();
        var repository = LabRepositoryLoader.Load(config.Root, new GitTagWorkflowSource(Repository,
            [new ResolvedWorkflow("test-vm@1", "test-vm", "1.0", CommitOf('a'), null)], new Dictionary<string, string>()));
        Assert.False(repository.IsValid(Guest));
        var messages = repository.IssuesFor(Guest).Select(i => i.Message).ToList();
        Assert.Contains($"test-vm@1.0: the commit aaaaaaaaaa of {Repository} could not be read.", messages);
        Assert.Contains("test-vm@1 resolved to test-vm@1.0 at aaaaaaaaaa, and its workflows/test-vm/workflow.yaml could not be read, see its errors.", messages);
    }

    [Fact]
    public void References_are_found_where_the_loader_looks_for_them()
    {
        using var config = ServiceDefinitionTests.Repository();
        Assert.Equal(["app@1", "web@1"], WorkflowReferences.In(config.Root).Select(r => r.ToString()));
    }
}
