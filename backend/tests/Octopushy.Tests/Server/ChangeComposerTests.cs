using System.Text.Json.Nodes;
using Octopushy.Server.Changes;
using Octopushy.Server.Data;

namespace Octopushy.Tests.Server;

/// <summary>What a form's values become in the file, without a server.</summary>
public class ChangeComposerTests
{
    private const string File = "labs/home/proxmox/korriban/guests/krb-app-01/guest.yaml";

    [Fact]
    public void An_untouched_null_in_a_section_the_form_sends_back_stays()
    {
        const string text = "schemaVersion: 1\nworkflow: test-vm@1\ninputs: { feature: null, note: old }\n";
        var inputs = new JsonObject { ["feature"] = null, ["note"] = "new" };
        var composed = ChangeComposer.Compose(new ChangeRequest(Guid.NewGuid(), new string('a', 40), Inputs: inputs), Guid.NewGuid(),
            ResourceKinds.Guest, "home/korriban/krb-app-01", File, "labs/home/proxmox/korriban/guests/krb-app-01", text, []);
        Assert.Equal("schemaVersion: 1\nworkflow: test-vm@1\ninputs: { feature: null, note: new }\n", composed.NewText);
        Assert.Equal("krb-app-01: note old → new", composed.Subject);
    }
}
