using System.Text.Json.Nodes;
using Octopushy.Server.Api;
using Octopushy.Server.Options;
using Octopushy.Server.Orchestration;

namespace Octopushy.Tests.Server;

public class WorkerUpdatePolicyTests
{
    private const string SystemPath = "/nix/store/00000000000000000000000000000000-nixos-system-worker-test";

    [Theory]
    [InlineData("http://release.test/worker.nar.xz")]
    [InlineData("https://user:password@release.test/worker.nar.xz")]
    [InlineData("https://release.test/worker.nar.xz?secret=foo")]
    [InlineData("https://release.test/worker.nar.xz#fragment")]
    [InlineData("https://localhost/worker.nar.xz")]
    [InlineData("https://127.0.0.1/worker.nar.xz")]
    public void Release_configuration_rejects_unsafe_destinations(string url)
    {
        var options = Options();
        options.ArchiveUrl = url;
        Assert.Throws<RunException>(() => ManagedWorkerUpdates.Target(options, Guid.NewGuid()));
    }

    [Theory]
    [InlineData("/nix/store/00000000000000000000000000000000-nixos-system-worker-test\n")]
    [InlineData("/nix/store/00000000000000000000000000000000-nixos-system-worker-test;sh")]
    [InlineData("/nix/store/00000000000000000000000000000000-nixos-system-worker/../foreign")]
    public void Release_configuration_rejects_nonliteral_system_paths(string path)
    {
        var options = Options();
        options.SystemPath = path;
        Assert.Throws<RunException>(() => ManagedWorkerUpdates.Target(options, Guid.NewGuid()));
    }

    [Fact]
    public void A_complete_release_requires_enablement_and_both_fingerprints()
    {
        var options = Options();
        var agent = Guid.NewGuid();
        Assert.Equal(agent, ManagedWorkerUpdates.Target(options, agent).AgentId);
        options.Enabled = false;
        Assert.Throws<RunException>(() => ManagedWorkerUpdates.Target(options, agent));
        options.Enabled = true;
        options.ImageSha256 = null;
        Assert.Throws<RunException>(() => ManagedWorkerUpdates.Target(options, agent));
        options.ImageSha256 = new string('a', 64);
        options.ArchiveSha256 = null;
        Assert.Throws<RunException>(() => ManagedWorkerUpdates.Target(options, agent));
    }

    [Fact]
    public void An_OS_update_needs_an_explicitly_unchanged_plan_not_just_zero_destructive_counts()
    {
        var plan = FakeAgent.Plan();
        Assert.True(ManagedWorkerSafety.UnchangedPlan(plan));
        Assert.False(ManagedWorkerSafety.UnchangedPlan(null));
        plan["counts"]!["create"] = 1;
        Assert.False(ManagedWorkerSafety.UnchangedPlan(plan));
        plan["counts"]!["create"] = 0;
        plan["changes"] = new JsonArray(new JsonObject { ["actions"] = new JsonArray("forget") });
        Assert.False(ManagedWorkerSafety.UnchangedPlan(plan));
        plan["changes"] = new JsonArray();
        plan["hasChanges"] = true;
        Assert.False(ManagedWorkerSafety.UnchangedPlan(plan));
    }

    private static WorkerUpdateOptions Options() => new()
    {
        Enabled = true,
        ArchiveUrl = "https://release.test/worker.nar.xz",
        ArchiveSha256 = new string('c', 64),
        ImageSha256 = new string('a', 64),
        SystemPath = SystemPath,
    };
}
