using Octopushy.Definitions;

namespace Octopushy.Tests.Definitions;

/// <summary><c>reports</c> and <c>peers</c> of a workflow: what its setup may hand back, and whose reports a guest gets.</summary>
public class WorkflowReportTests
{
    private const string Guest = "home/korriban/krb-app-01";

    private static string Errors(TestRepository test) =>
        string.Join("\n", LabRepositoryLoader.Load(test.Root).Issues.Where(i => i.Severity == IssueSeverity.Error).Select(i => i.Message));

    [Fact]
    public void A_setup_stage_declares_what_it_reports_and_whose_reports_it_gets()
    {
        using var test = GuestConfigurationTests.Repository(extraWorkflow: """
            reports:
              certificate_sha256: { pattern: "[0-9a-f]{64}", description: The certificate }
            peers: inputs.members[].name
            """);
        var repository = LabRepositoryLoader.Load(test.Root);
        Assert.Empty(Errors(test));
        var workflow = repository.Guest(Guest)!.Workflow!;
        Assert.Equal("[0-9a-f]{64}", workflow.Reports["certificate_sha256"].Pattern);
        Assert.Equal("The certificate", workflow.Reports["certificate_sha256"].Description);
        Assert.Equal("inputs.members[].name", workflow.Peers);
    }

    [Theory]
    [InlineData("reports:\n  certificate_sha256: { pattern: \"[0-9a-f\" }", "is not a valid regular expression")]
    [InlineData("reports:\n  certificate_sha256: { description: no pattern }", "pattern")]
    [InlineData("reports:\n  Certificate: { pattern: x }", "Certificate")]
    [InlineData("peers: members", "peers")]
    public void A_report_needs_a_valid_pattern_and_peers_a_path_into_the_inputs(string extra, string error)
    {
        using var test = GuestConfigurationTests.Repository(extraWorkflow: extra);
        Assert.Contains(error, Errors(test), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("reports:\n  certificate_sha256: { pattern: x }")]
    [InlineData("peers: inputs.members[]")]
    public void Only_a_machine_workflow_with_a_setup_stage_reports_or_has_peers(string extra)
    {
        using var test = TestRepository.Minimal();
        test.Write("workflows/test-vm/workflow.yaml", File.ReadAllText(Path.Combine(test.Root, "workflows/test-vm/workflow.yaml")) + extra);
        Assert.Contains("for the setup stage of a workflow with manages: vm or manages: container", Errors(test), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("[0-9a-f]{64}", "abababababababababababababababababababababababababababababababab", true)]
    [InlineData("[0-9a-f]{64}", "abababababababababababababababababababababababababababababababab\n", false)]
    [InlineData("[0-9a-f]{64}", "xabababababababababababababababababababababababababababababababab", false)]
    [InlineData("primary|secondary", "primary", true)]
    [InlineData("primary|secondary", "primary-x", false)]
    [InlineData("primary|secondary", "x-secondary", false)]
    [InlineData(".*", "", false)]
    public void A_value_is_kept_only_when_its_pattern_matches_the_whole_of_it(string pattern, string value, bool kept) =>
        Assert.Equal(kept, new WorkflowReport("r", null, pattern).Accepts(value));

    [Fact]
    public void A_value_longer_than_a_fingerprint_or_state_is_not_kept() =>
        Assert.False(new WorkflowReport("r", null, ".*").Accepts(new string('a', WorkflowReport.MaxLength + 1)));

    [Theory]
    [InlineData("secrets:\n  tailscale_key:", "secrets:\n  join_confirmed: { store: app/yes }\n  tailscale_key:", "secrets.join_confirmed")]
    [InlineData("secretFiles:\n  git_key_src:", "secretFiles:\n  join_confirmed: { store: app/yes }\n  git_key_src:", "secretFiles.join_confirmed")]
    [InlineData("inputs: { greeting: hello }", "inputs: { greeting: hello, join_confirmed: true }", "inputs.join_confirmed")]
    public void A_guest_cannot_set_what_approving_an_outcome_sets(string from, string to, string named)
    {
        using var test = GuestConfigurationTests.Repository(extraWorkflow: """
            outcomes:
              join-ready:
                approve: { vars: { join_confirmed: true } }
            """);
        Assert.Empty(Errors(test));
        const string file = "labs/home/proxmox/korriban/guests/krb-app-01/guest.yaml";
        var text = File.ReadAllText(Path.Combine(test.Root, file));
        Assert.Contains(from, text);
        test.Write(file, text.Replace(from, to));
        Assert.Contains($"{named} is what approving an outcome of app@1.0 sets", Errors(test), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("(?x)primary # a comment")]
    [InlineData("primary)|(secondary")]
    public void A_pattern_that_validates_is_one_that_can_be_matched_whole(string pattern)
    {
        using var test = GuestConfigurationTests.Repository(extraWorkflow: $$"""
            reports:
              role: { pattern: "{{pattern}}" }
            """);
        Assert.Contains("is not a valid regular expression", Errors(test), StringComparison.Ordinal);
    }

    [Fact]
    public void Peers_are_read_through_arrays_and_only_texts_count()
    {
        var root = System.Text.Json.Nodes.JsonNode.Parse("""{ "inputs": { "members": [ { "name": "a" }, { "name": 3 }, { "other": "b" }, { "name": "c" } ] } }""")!;
        Assert.Equal(["a", "c"], InputPath.Strings(root, "inputs.members[].name"));
    }
}

/// <summary>What a finished playbook step's reports leave on its guest.</summary>
public class GuestReportRecordTests
{
    private static Octopushy.Server.Orchestration.AnsibleContext Context(Dictionary<string, string>? reports) =>
        new("app@1.0", "setup", "modules/ansible/app", "site.yml", null, false, false, null, "image", "krb-app-01",
            "labs/home/proxmox/korriban/guests/krb-app-01", "10.1.0.31", 22, "ops", [], [], [], [], null)
        { Reports = reports };

    [Fact]
    public void A_new_value_replaces_its_name_only_and_what_is_not_declared_is_named_without_its_value()
    {
        var configuration = new Octopushy.Server.Data.GuestConfiguration();
        var context = Context(new() { ["certificate_sha256"] = "[0-9a-f]{64}", ["role"] = "primary|secondary" });
        var run = Guid.NewGuid();
        var refused = Octopushy.Server.Orchestration.GuestReports.Record(configuration, context,
            System.Text.Json.Nodes.JsonNode.Parse($$"""{ "reports": { "certificate_sha256": "{{new string('a', 64)}}", "role": "primary" } }"""), run, DateTimeOffset.UnixEpoch);
        Assert.Empty(refused);

        refused = Octopushy.Server.Orchestration.GuestReports.Record(configuration, context,
            System.Text.Json.Nodes.JsonNode.Parse("""{ "reports": { "role": "s3cret-ish", "password": "hunter2" } }"""), Guid.NewGuid(), DateTimeOffset.UnixEpoch);
        Assert.Equal(["role", "a name the workflow does not declare"], refused);
        Assert.DoesNotContain("hunter2", string.Join(",", refused), StringComparison.Ordinal);
        var kept = System.Text.Json.Nodes.JsonNode.Parse(configuration.Reports!)!;
        Assert.Equal("primary", kept["role"]!["value"]!.GetValue<string>());
        Assert.Equal(new string('a', 64), kept["certificate_sha256"]!["value"]!.GetValue<string>());
        Assert.Equal("10.1.0.31", kept["certificate_sha256"]!["address"]!.GetValue<string>());
        Assert.Equal(run.ToString(), kept["role"]!["run"]!.GetValue<string>());
        Assert.Null(kept["password"]);
    }

    [Fact]
    public void A_run_of_a_workflow_that_declares_no_reports_keeps_none()
    {
        var configuration = new Octopushy.Server.Data.GuestConfiguration();
        var refused = Octopushy.Server.Orchestration.GuestReports.Record(configuration, Context(null),
            System.Text.Json.Nodes.JsonNode.Parse("""{ "reports": { "role": "primary" } }"""), Guid.NewGuid(), DateTimeOffset.UnixEpoch);
        Assert.Single(refused);
        Assert.Null(configuration.Reports);
    }
}
