using System.Text;
using System.Text.Json.Nodes;
using Octopushy.Server.Agents;
using Octopushy.Server.Data;

namespace Octopushy.Server.Orchestration;

/// <summary>
/// The one document a pipeline's approval names, as <c>preview.json</c> is for a configure run. It lists
/// every stage with the digest of the saved plan or preview that stage will apply, so approving it covers
/// them all and nothing else. It is kept on the approval step, and the agent never sees it.
/// </summary>
public sealed record PipelineDocument(string Text, string Sha256, IReadOnlyList<string> Destructive, bool HasChanges)
{
    public static string Digest(string text) => ArtifactStore.Sha256(Encoding.UTF8.GetBytes(text));

    /// <summary>The saved artifact the document names for a stage; null for a plan with no changes.</summary>
    public static string? ArtifactOf(string text, string stage) =>
        (JsonNode.Parse(text)?["stages"] as JsonArray)?.FirstOrDefault(s => s?["name"]?.GetValue<string>() == stage)?["artifact"]?.GetValue<string>();

    /// <summary>Reads each stage's finished plan or preview into the document; throws when one left nothing to approve.</summary>
    public static PipelineDocument Build(Run run, RunContext context)
    {
        var destructive = new List<string>();
        var hasChanges = false;
        var stages = new JsonArray();
        foreach (var stage in context.Stages!)
        {
            var step = run.Steps.Single(s => s.Stage == stage.Name && s.Name is StepNames.Plan or StepNames.Preview);
            var outputs = step.Outputs is null ? null : JsonNode.Parse(step.Outputs);
            var artifact = outputs?["planSha256"]?.GetValue<string>();
            var entry = new JsonObject { ["name"] = stage.Name, ["workflow"] = stage.Workflow };
            if (stage.Ansible is { } ansible)
            {
                if (string.IsNullOrWhiteSpace(artifact))
                    throw new RunException($"Stage {stage.Name} has no saved preview for an approval to name.");
                hasChanges = true;
                if (ansible.Destructive)
                    destructive.Add($"{stage.Name} (the workflow declares {ansible.Action} destructive)");
                entry["kind"] = "ansible";
                entry["action"] = ansible.Action;
                entry["destructive"] = ansible.Destructive;
            }
            else
            {
                var changes = outputs?["hasChanges"]?.GetValue<bool>() ?? true;
                if (changes && string.IsNullOrWhiteSpace(artifact))
                    throw new RunException($"Stage {stage.Name} has no saved plan for an approval to name.");
                hasChanges |= changes;
                var counts = outputs?["counts"];
                var removed = (counts?["delete"]?.GetValue<int>() ?? 0) + (counts?["replace"]?.GetValue<int>() ?? 0);
                if (removed > 0)
                    destructive.Add($"{stage.Name} (the plan deletes or replaces {removed} resource(s))");
                entry["kind"] = "opentofu";
                entry["root"] = stage.OpenTofu!.Root;
                entry["image"] = stage.OpenTofu.Image;
                entry["variables"] = stage.OpenTofu.Variables.DeepClone();
                entry["hasChanges"] = changes;
                entry["counts"] = counts?.DeepClone();
                entry["changes"] = outputs?["changes"]?.DeepClone();
            }

            entry["artifact"] = artifact;
            stages.Add(entry);
        }

        var text = new JsonObject
        {
            ["schemaVersion"] = 1,
            ["address"] = context.Address,
            ["commit"] = context.Commit,
            ["inputsHash"] = run.InputsHash,
            ["stages"] = stages,
        }.ToJsonString();
        return new PipelineDocument(text, Digest(text), destructive, hasChanges);
    }
}
