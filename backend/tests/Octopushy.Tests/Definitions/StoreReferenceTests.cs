using System.Text.Json.Nodes;
using Octopushy.Definitions;
using Octopushy.Definitions.Schemas;
using Octopushy.Definitions.Yaml;

namespace Octopushy.Tests.Definitions;

public class StoreReferenceTests
{
    private const string InstallationFile = "labs/home/proxmox/korriban/installation.yaml";
    private const string GuestFile = "labs/home/proxmox/korriban/guests/krb-app-01/guest.yaml";
    private const string LabFile = "labs/home/lab.yaml";

    private static readonly (DefinitionKind Kind, string File, string Consumer, string[] Path)[] ReferenceLocations =
    [
        (DefinitionKind.Repository, "octopushy.yaml", "octopushy.yaml", ["stateBackends", "postgres", "env", "PG_CONN_STR"]),
        (DefinitionKind.Installation, InstallationFile, "home/korriban", ["connection", "readOnlyToken"]),
        (DefinitionKind.Installation, InstallationFile, "home/korriban", ["connection", "privilegedToken"]),
        (DefinitionKind.Installation, InstallationFile, "home/korriban", ["connection", "tls", "caCertificate"]),
        (DefinitionKind.Guest, GuestFile, "home/korriban/krb-app-01", ["access", "sshKey"]),
    ];

    [Fact]
    public void Existing_reference_and_use_constructors_remain_unversioned()
    {
        Assert.Null(new StoreReference("credentials/token").Version);
        Assert.Null(new StoreUse("credentials/token", "home/korriban", InstallationFile).Version);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(int.MaxValue)]
    public void Loaded_references_and_uses_preserve_optional_exact_versions(int? version)
    {
        using var test = TestRepository.Minimal();
        var suffix = version is null ? "" : $", version: {version}";
        foreach (var location in ReferenceLocations)
        {
            var document = WithReference(test, location.File, location.Path,
                $"{{ store: credentials/{location.Path[^1]}{suffix} }}");
            test.Write(location.File, document.ToJsonString());
        }

        var settings = ReadDefinition(test, "octopushy.yaml");
        settings["stateBackends"]!["postgres"]!["env"]!["PG_USER"] = "octopushy";
        test.Write("octopushy.yaml", settings.ToJsonString());

        var repository = LabRepositoryLoader.Load(test.Root);
        Assert.Empty(repository.Issues);
        Assert.All(repository.Resources, resource => Assert.True(resource.Valid));
        var connection = Assert.Single(repository.Installations).Connection;
        Assert.Equal(new StoreReference("credentials/readOnlyToken", version), connection.ReadOnlyToken);
        Assert.Equal(new StoreReference("credentials/privilegedToken", version), connection.PrivilegedToken);
        Assert.Equal(new StoreReference("credentials/caCertificate", version), connection.CaCertificate);
        var env = repository.Settings!.StateBackends["postgres"].Env;
        Assert.Equal(new StoreReference("credentials/PG_CONN_STR", version), env["PG_CONN_STR"].Store);
        Assert.Null(env["PG_CONN_STR"].Value);
        Assert.Equal(new EnvValue("octopushy", null), env["PG_USER"]);
        var sshKey = Assert.Single(repository.Guests).Json["access"]!["sshKey"]!;
        Assert.Equal("credentials/sshKey", sshKey["store"]!.GetValue<string>());
        Assert.Equal(version, sshKey["version"]?.GetValue<int>());

        Assert.Equal(ReferenceLocations.Length, repository.StoreUses.Count);
        foreach (var location in ReferenceLocations)
        {
            Assert.Contains(new StoreUse($"credentials/{location.Path[^1]}", location.Consumer, location.File, version),
                repository.StoreUses);
        }
    }

    [Theory]
    [InlineData("{ store: credentials/token, version: 0 }")]
    [InlineData("{ store: credentials/token, version: -1 }")]
    [InlineData("{ store: credentials/token, version: 1.5 }")]
    [InlineData("{ store: credentials/token, version: '2' }")]
    [InlineData("{ store: credentials/token, version: null }")]
    [InlineData("{ store: credentials/token, version: true }")]
    [InlineData("{ store: credentials/token, version: [] }")]
    [InlineData("{ store: credentials/token, version: {} }")]
    [InlineData("{ store: credentials/token, version: 2147483648 }")]
    [InlineData("{ store: credentials/token, extra: true }")]
    [InlineData("{ store: credentials/token, version: 2, extra: true }")]
    [InlineData("{ version: 2 }")]
    public void Schemas_reject_invalid_versions_and_reference_shapes(string reference)
    {
        foreach (var location in ReferenceLocations)
        {
            using var test = TestRepository.Minimal();
            var document = WithReference(test, location.File, location.Path, reference);
            Assert.NotEmpty(SchemaCatalog.Instance.Validate(location.Kind, document));
            test.Write(location.File, document.ToJsonString());

            // Invalid references must produce definition issues, not reach the typed parser and throw.
            var repository = LabRepositoryLoader.Load(test.Root);
            Assert.Contains(repository.Issues, issue => issue.Severity == IssueSeverity.Error && issue.File == location.File);
            Assert.False(repository.IsValid("home/korriban/krb-app-01"));
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData(3)]
    public void System_scope_is_rejected_at_every_schema_reference_location(int? version)
    {
        foreach (var location in ReferenceLocations)
        {
            using var test = TestRepository.Minimal();
            var suffix = version is null ? "" : $", version: {version}";
            var document = WithReference(test, location.File, location.Path,
                $"{{ store: system/agent-ca/key{suffix} }}");
            test.Write(location.File, document.ToJsonString());

            var repository = LabRepositoryLoader.Load(test.Root);
            var issue = Assert.Single(repository.Issues);
            Assert.Contains("system scope", issue.Message);
            Assert.Equal(location.File, issue.File);
            Assert.NotNull(issue.Line);
            Assert.False(repository.IsValid("home/korriban/krb-app-01"));
            Assert.Contains(new StoreUse("system/agent-ca/key", location.Consumer, location.File, version), repository.StoreUses);
            if (location.Kind == DefinitionKind.Repository)
            {
                Assert.False(repository.RepositoryValid);
                Assert.Null(repository.Settings);
                Assert.All(repository.Resources, resource => Assert.False(resource.Valid));
            }
        }
    }

    [Fact]
    public void Uses_in_permissive_sections_are_collected_through_nested_objects_and_arrays()
    {
        using var test = TestRepository.Minimal();
        test.Write(LabFile, """
            schemaVersion: 1
            integrations:
              example:
                credentials:
                  - { store: integrations/first, version: 2 }
                  - { store: integrations/second }
            dns:
              token: { store: dns/token, version: 3 }
            """);
        test.Write(GuestFile, File.ReadAllText(Path.Combine(test.Root, GuestFile)) + """

            setup:
              steps:
                - credentials: { store: setup/token, version: 4 }
            """);
        var guest = ReadDefinition(test, GuestFile);
        guest["hardware"]!["credentials"] = JsonNode.Parse("""{"store":"hardware/token","version":5}""");
        guest["network"]!["credentials"] = JsonNode.Parse("""{"store":"network/token","version":6}""");
        test.Write(GuestFile, guest.ToJsonString());

        var repository = LabRepositoryLoader.Load(test.Root);
        Assert.Empty(repository.Issues);
        Assert.Equal(9, repository.StoreUses.Count);
        Assert.Contains(new StoreUse("integrations/first", "home", LabFile, 2), repository.StoreUses);
        Assert.Contains(new StoreUse("integrations/second", "home", LabFile), repository.StoreUses);
        Assert.Contains(new StoreUse("dns/token", "home", LabFile, 3), repository.StoreUses);
        Assert.Contains(new StoreUse("setup/token", "home/korriban/krb-app-01", GuestFile, 4), repository.StoreUses);
        Assert.Contains(new StoreUse("hardware/token", "home/korriban/krb-app-01", GuestFile, 5), repository.StoreUses);
        Assert.Contains(new StoreUse("network/token", "home/korriban/krb-app-01", GuestFile, 6), repository.StoreUses);
    }

    [Theory]
    [InlineData("{ store: system/agent-ca/key }")]
    [InlineData("{ store: system/agent-ca/key, version: 3 }")]
    [InlineData("{ store: system/agent-ca/key, version: 3, extra: true }")]
    [InlineData("{ store: system/agent-ca/key, version: 'invalid', extra: true }")]
    [InlineData("{ store: credentials/outer, version: 2, nested: { store: system/agent-ca/key, version: 3 } }")]
    public void Permissive_sections_cannot_hide_system_scope_references(string reference)
    {
        using var test = TestRepository.Minimal();
        test.Write(LabFile, $$"""
            schemaVersion: 1
            integrations:
              example:
                credentials:
                  - {{reference}}
            """);
        test.Write(GuestFile, File.ReadAllText(Path.Combine(test.Root, GuestFile)) + $$"""

            setup:
              steps:
                - credentials: {{reference}}
            """);

        var repository = LabRepositoryLoader.Load(test.Root);
        Assert.Collection(repository.Issues,
            issue =>
            {
                Assert.Contains("system scope", issue.Message);
                Assert.Equal(LabFile, issue.File);
                Assert.Equal(5, issue.Line);
            },
            issue =>
            {
                Assert.Contains("system scope", issue.Message);
                Assert.Equal(GuestFile, issue.File);
                Assert.NotNull(issue.Line);
            });
        Assert.All(repository.Resources, resource => Assert.False(resource.Valid));
        Assert.Equal(2, repository.StoreUses.Count(use => use.Key == "system/agent-ca/key"));
    }

    private static JsonObject WithReference(TestRepository test, string file, string[] path, string reference)
    {
        var document = ReadDefinition(test, file);
        JsonNode parent = document;
        foreach (var segment in path[..^1])
        {
            parent = parent[segment] ??= new JsonObject();
        }

        parent[path[^1]] = YamlDocumentReader.Read(reference).Root;
        return document;
    }

    private static JsonObject ReadDefinition(TestRepository test, string file) =>
        (JsonObject)YamlDocumentReader.Read(File.ReadAllText(Path.Combine(test.Root, file))).Root!;
}
