using Octopushy.Definitions;

namespace Octopushy.Tests.Definitions;

public class GuestLinkTests
{
    private const string GuestFile = "labs/home/proxmox/korriban/guests/krb-app-01/guest.yaml";
    private const string Address = "home/korriban/krb-app-01";

    private const string Links = """
        links:
          - { label: Open DNS admin, url: "https://10.15.30.11:53443/" }
          - { label: Status, url: "http://dns-01.lab.test/status?full=1", check: true }

        """;

    private static void Append(TestRepository test, string text) =>
        test.Write(GuestFile, File.ReadAllText(Path.Combine(test.Root, GuestFile)) + "\n" + text);

    private static string Errors(TestRepository test) =>
        string.Join("\n", LabRepositoryLoader.Load(test.Root).Issues.Where(i => i.Severity == IssueSeverity.Error).Select(i => i.Message));

    [Fact]
    public void Labeled_links_are_read_in_order()
    {
        using var test = GuestConfigurationTests.Repository();
        Append(test, Links);
        var repository = LabRepositoryLoader.Load(test.Root);
        Assert.True(repository.IsValid(Address), Errors(test));
        Assert.Equal(
            [new GuestLink("Open DNS admin", "https://10.15.30.11:53443/"), new GuestLink("Status", "http://dns-01.lab.test/status?full=1", true)],
            GuestDefinition.LinksOf(repository.Guest(Address)!.Json));
    }

    [Fact]
    public void A_guest_without_links_has_none()
    {
        using var test = TestRepository.Minimal();
        var repository = LabRepositoryLoader.Load(test.Root);
        Assert.Empty(GuestDefinition.LinksOf(repository.Guests[0].Json));
    }

    [Theory]
    [InlineData("true")]
    [InlineData("false")]
    public void A_guest_link_can_opt_into_an_uptime_check(string check)
    {
        using var test = TestRepository.Minimal();
        Append(test, $"links:\n  - {{ label: HA, url: \"http://10.15.40.180/\", check: {check} }}\n");
        var repository = LabRepositoryLoader.Load(test.Root);
        Assert.True(repository.IsValid(Address), Errors(test));
        Assert.Equal(check == "true", Assert.Single(GuestDefinition.LinksOf(repository.Guest(Address)!.Json)).Check);
    }

    [Theory]
    [InlineData("""{ label: Admin, url: "ftp://10.15.30.11/" }""")]
    [InlineData("""{ label: Admin, url: "javascript:alert(1)" }""")]
    [InlineData("""{ label: Admin, url: "/admin" }""")]
    [InlineData("""{ label: Admin, url: "10.15.30.11:53443" }""")]
    [InlineData("""{ label: Admin, url: "https://admin:secret@10.15.30.11:53443/" }""")]
    [InlineData("""{ label: Admin, url: "https://admin@10.15.30.11/" }""")]
    [InlineData("""{ label: Admin, url: "https://10.15.30.11/a b" }""")]
    [InlineData("""{ url: "https://10.15.30.11:53443/" }""")]
    [InlineData("""{ label: Admin }""")]
    [InlineData("""{ label: "", url: "https://10.15.30.11:53443/" }""")]
    [InlineData("""{ label: "  ", url: "https://10.15.30.11:53443/" }""")]
    [InlineData("""{ label: Admin, url: "https://10.15.30.11:53443/", uptime: true }""")]
    [InlineData("""{ label: Admin, url: "https://10.15.30.11/", check: "true" }""")]
    [InlineData("\"https://10.15.30.11:53443/\"")]
    public void A_link_must_be_a_labeled_http_url_without_credentials(string link)
    {
        using var test = TestRepository.Minimal();
        Append(test, $"links:\n  - {link}\n");
        Assert.Contains("links", Errors(test));
    }

    [Fact]
    public void Links_held_by_an_invalid_definition_are_still_filtered()
    {
        var json = System.Text.Json.Nodes.JsonNode.Parse("""
            { "links": [
              { "label": "Admin", "url": "https://admin:secret@10.15.30.11/" },
              { "label": "Script", "url": "javascript:alert(1)" },
              { "label": " ", "url": "https://10.15.30.11/" },
              "https://10.15.30.11/",
              { "label": "Admin", "url": "https://10.15.30.11:53443/" }
            ] }
            """);
        Assert.Equal([new GuestLink("Admin", "https://10.15.30.11:53443/")], GuestDefinition.LinksOf(json));
    }

    [Fact]
    public void Links_change_neither_the_provision_nor_the_configure_hash()
    {
        using var test = GuestConfigurationTests.Repository();
        var before = Hashes(test);
        Append(test, Links);
        Assert.True(LabRepositoryLoader.Load(test.Root).IsValid(Address), Errors(test));
        Assert.Equal(before, Hashes(test));
    }

    private static (string Provision, string Configure) Hashes(TestRepository test)
    {
        var repository = LabRepositoryLoader.Load(test.Root);
        var guest = repository.Guest(Address)!;
        return (ProvisionInputs.Build(repository, guest, "rev").Hash, ConfigureInputs.Build(repository, guest).Hash);
    }
}
