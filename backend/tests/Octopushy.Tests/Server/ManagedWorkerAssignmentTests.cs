using Octopushy.Server.Orchestration;

namespace Octopushy.Tests.Server;

public class ManagedWorkerAssignmentTests
{
    [Theory]
    [InlineData("prepare-worker", "prepare")]
    [InlineData("install-worker", "install")]
    public void Bootstrap_is_mutating_and_requires_privileged_authority(string step, string action)
    {
        Assert.Equal(new StepAction("managed-worker", action, true), AssignmentBuilder.ActionFor(step));
        Assert.Equal(SecretNames.ProxmoxPrivilegedToken, AssignmentBuilder.TokenFor(step));
    }
    [Theory]
    [InlineData("https://localhost")]
    [InlineData("https://localhost.")]
    [InlineData("https://127.0.0.1:8443")]
    [InlineData("https://[::1]:8443")]
    [InlineData("https://[::ffff:127.0.0.1]:8443")]
    [InlineData("https://[::]:8443")]
    [InlineData("https://0.0.0.0:8443")]
    [InlineData("http://worker.example:8443")]
    public void Managed_workers_require_a_remote_https_endpoint(string url) =>
        Assert.Throws<RunException>(() => ManagedWorkerSafety.PublicUrl(url));

}
