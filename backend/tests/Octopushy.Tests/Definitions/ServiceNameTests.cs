using Octopushy.Definitions;

namespace Octopushy.Tests.Definitions;

/// <summary>
/// Names: a stage of <c>manages: service</c> says which of its inputs are the host names it sets, with <c>names</c>. Where the
/// stage runs, each is where people open the service and what uptime checks, over https, beside its <c>links</c>. Octopushy
/// knows nothing of what sets them. See docs/workflows.md#names-a-stage-sets.
/// </summary>
public class ServiceNameTests
{
    private const string Address = ServiceDefinitionTests.Address;
    private const string Names = ", names: 'inputs.records[].name'";

    private static string ServiceWith(string records, string links = "") => $$"""
        schemaVersion: 1
        workflow: web-dns@1
        inputs: { port: 9000, records: {{records}} }
        secrets:
          web_password: { store: web/password }
        {{links}}
        """;

    private static string Errors(LabRepository repository) =>
        string.Join("\n", repository.Issues.Where(i => i.Severity == IssueSeverity.Error).Select(i => $"{i.File}: {i.Message}"));

    [Fact]
    public void The_names_a_stage_sets_are_where_the_service_is_opened_after_its_links()
    {
        using var test = InterfaceTests.Repository(
            ServiceWith("[{ name: web.example.com, type: CNAME, value: proxy.example.com }, { name: Second.Example.com., type: A, value: 10.1.0.9 }]",
                "links: [\"http://10.1.0.31:8080/status\"]"),
            dnsStage: Names);
        var repository = LabRepositoryLoader.Load(test.Root);
        Assert.Equal("", Errors(repository));
        var service = repository.Service(Address)!;
        Assert.Equal(
            [new ServiceName("web.example.com", "dns", "https://web.example.com/"), new ServiceName("second.example.com", "dns", "https://second.example.com/")],
            service.Names);
        Assert.Equal(["http://10.1.0.31:8080/status", "https://web.example.com/", "https://second.example.com/"], service.Links);
    }

    [Fact]
    public void A_stage_that_does_not_run_sets_no_names()
    {
        using var test = InterfaceTests.Repository(bound: false, dnsStage: Names);
        var service = LabRepositoryLoader.Load(test.Root).Service(Address)!;
        Assert.Empty(service.Names);
        Assert.Empty(service.Links);
    }

    [Fact]
    public void A_name_a_link_already_opens_is_not_checked_twice()
    {
        using var test = InterfaceTests.Repository(
            ServiceWith("[{ name: web.example.com, type: CNAME, value: proxy.example.com }]", "links: [\"https://web.example.com/login\"]"),
            dnsStage: Names);
        var service = LabRepositoryLoader.Load(test.Root).Service(Address)!;
        Assert.Equal(["web.example.com"], service.Names.Select(n => n.Name));
        Assert.Equal(["https://web.example.com/login"], service.Links);
    }

    [Fact]
    public void What_is_not_a_host_name_is_left_out()
    {
        using var test = InterfaceTests.Repository(
            ServiceWith("[{ name: _dmarc.example.com, type: CNAME, value: x.example.com }, { name: '*.example.com', type: A, value: 10.1.0.9 }, { name: web.example.com, type: A, value: 10.1.0.9 }, { name: web.example.com, type: AAAA, value: '::1' }]"),
            dnsStage: Names);
        var service = LabRepositoryLoader.Load(test.Root).Service(Address)!;
        Assert.Equal(["web.example.com"], service.Names.Select(n => n.Name));
    }

    [Fact]
    public void Names_change_nothing_a_run_does()
    {
        using var without = InterfaceTests.Repository();
        using var with = InterfaceTests.Repository(dnsStage: Names);
        var stage = LabRepositoryLoader.Load(with.Root).Service(Address)!.Stages[1];
        Assert.Equal(LabRepositoryLoader.Load(without.Root).Service(Address)!.Stages[1].Inputs.ToJsonString(), stage.Inputs.ToJsonString());
        Assert.Equal("inputs.records[].name", stage.Definition.Names);
    }

    [Theory]
    [InlineData(", names: 'records[].name'")]
    [InlineData(", names: 'inputs.records[]..name'")]
    [InlineData(", names: 'inputs.records[0].name'")]
    public void Names_are_a_path_into_the_inputs(string names)
    {
        using var test = InterfaceTests.Repository(dnsStage: names);
        var repository = LabRepositoryLoader.Load(test.Root);
        Assert.False(repository.IsValid(Address));
        Assert.Contains("names", Errors(repository));
    }

    [Fact]
    public void Only_a_services_stage_sets_names()
    {
        using var test = InterfaceTests.Repository();
        test.Write("workflows/app/workflow.yaml", File.ReadAllText(Path.Combine(test.Root, "workflows/app/workflow.yaml"))
            .Replace("    prepare: prepare.yml", "    prepare: prepare.yml\n    names: inputs.records[].name"));
        Assert.Contains("only a stage of a workflow with manages: service sets names", Errors(LabRepositoryLoader.Load(test.Root)));
    }
}
