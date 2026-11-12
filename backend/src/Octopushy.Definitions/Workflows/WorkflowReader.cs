using System.Text.Json.Nodes;
using Json.Schema;
using Octopushy.Definitions.Schemas;
using Octopushy.Definitions.Yaml;

namespace Octopushy.Definitions;

/// <summary>
/// Reads and checks every <c>workflows/&lt;id&gt;/workflow.yaml</c> under a directory that also holds the shared
/// <c>modules/</c>. Every path a workflow names must exist and stay inside its own folder or, for a stage's root, under
/// <c>modules/</c>: that is what its hashes cover, so nothing else can change what it runs.
/// </summary>
public sealed class WorkflowReader
{
    private readonly string _root;
    private readonly List<DefinitionIssue> _issues = [];
    private readonly Dictionary<string, YamlDocument> _documents = new(StringComparer.Ordinal);

    public WorkflowReader(string sourceRoot)
    {
        _root = Path.GetFullPath(sourceRoot);
    }

    public IReadOnlyList<DefinitionIssue> Issues => _issues;

    /// <summary>Workflows whose <c>workflow.yaml</c> could not be read at all, so their version is unknown.</summary>
    public HashSet<string> Unreadable { get; } = new(StringComparer.Ordinal);

    public List<WorkflowDefinition> ReadAll()
    {
        var result = new List<WorkflowDefinition>();
        foreach (var directory in DefinitionFiles.SubDirectories(Path.Combine(_root, "workflows")))
        {
            var id = Path.GetFileName(directory);
            var address = $"workflows/{id}";
            var file = $"{address}/workflow.yaml";
            if (!DefinitionFiles.IsId(id))
            {
                Error(file, null, $"'{id}' is not a valid id: use lowercase letters, digits, and hyphens.", address);
                Unreadable.Add(id);
                continue;
            }

            if (!File.Exists(Path.Combine(_root, file)))
            {
                _issues.Add(new DefinitionIssue(IssueSeverity.Warning, file, null, $"The folder {address} has no workflow.yaml and is ignored.", address));
                continue;
            }

            if (Read(id, file, address) is { } workflow)
                result.Add(workflow);
            else
                Unreadable.Add(id);
        }

        return result;
    }

    /// <summary>One workflow, <c>workflows/&lt;id&gt;/workflow.yaml</c>, or null when it does not exist or cannot be read.</summary>
    public WorkflowDefinition? ReadOne(string id)
    {
        var address = $"workflows/{id}";
        var file = $"{address}/workflow.yaml";
        if (!DefinitionFiles.IsId(id) || !File.Exists(Path.Combine(_root, file)))
        {
            Unreadable.Add(id);
            return null;
        }

        var workflow = Read(id, file, address);
        if (workflow is null)
            Unreadable.Add(id);
        return workflow;
    }

    private WorkflowDefinition? Read(string id, string file, string address)
    {
        var (json, _) = DefinitionFiles.Read(_root, file, DefinitionKind.Workflow, _documents, (f, line, message) => Error(f, line, message, address));
        if (json is null)
            return null;

        var errors = 0;
        void Invalid(string pointer, string message)
        {
            Error(file, LineOf(file, pointer), message, address);
            errors++;
        }

        var directory = $"workflows/{id}";
        WorkflowVersion.TryParse(json["version"]!.GetValue<string>(), out var version);
        var manages = json["manages"]!.GetValue<string>();
        var secrets = (json["secrets"] as JsonArray)?.Select(s => s!.GetValue<string>()).ToList() ?? [];
        var outcomes = (json["outcomes"] as JsonObject)?.ToDictionary(
            p => p.Key,
            p => new WorkflowOutcome(
                p.Key,
                p.Value!["description"]?.GetValue<string>(),
                p.Value!["approve"]?["vars"]?.DeepClone().AsObject(),
                p.Value!["result"]?.GetValue<string>() ?? "failed",
                p.Value!["promote"]?.GetValue<bool>() ?? true),
            StringComparer.Ordinal) ?? [];
        var declared = (json["actions"] as JsonObject)?.ToDictionary(
            p => p.Key,
            p => (Stage: p.Value!["stage"]!.GetValue<string>(), Action: new WorkflowAction(
                p.Key,
                p.Value!["description"]?.GetValue<string>(),
                p.Value!["playbook"]!.GetValue<string>(),
                p.Value!["prepare"]?.GetValue<string>(),
                p.Value!["snapshot"] is not null,
                p.Value!["destructive"]!.GetValue<bool>(),
                false,
                p.Value!["timeoutMinutes"]?.GetValue<int>())
            {
                Approval = p.Value!["approval"]?.GetValue<string>(),
            }),
            StringComparer.Ordinal) ?? [];

        // The inputs schema is part of the workflow, so it lives in the workflow's folder.
        string? inputs = null;
        JsonSchema? schema = null;
        JsonObject? schemaJson = null;
        if (json["inputs"] is JsonValue inputsValue)
        {
            inputs = Resolve(directory, inputsValue.GetValue<string>(), allowModules: false, out var why);
            if (inputs is null)
                Invalid("/inputs", $"The inputs schema {inputsValue.GetValue<string>()} {why}");
            else if (!File.Exists(Path.Combine(_root, inputs)))
            {
                Invalid("/inputs", $"The inputs schema {inputs} does not exist.");
                inputs = null;
            }
            else
            {
                try
                {
                    var schemaText = File.ReadAllText(Path.Combine(_root, inputs));
                    schema = SchemaCatalog.BuildUntrusted(schemaText);
                    schemaJson = JsonNode.Parse(schemaText) as JsonObject;
                }
                catch (Exception e) when (e is System.Text.Json.JsonException or JsonSchemaException or InvalidOperationException or ArgumentException)
                {
                    Error(inputs, null, $"The inputs schema is not a valid JSON Schema: {e.Message}", address);
                    errors++;
                }
            }
        }

        var stageNodes = json["stages"]!.AsArray().Select(n => n!.AsObject()).ToList();
        foreach (var name in stageNodes.GroupBy(s => s["name"]!.GetValue<string>()).Where(g => g.Count() > 1).Select(g => g.Key))
        {
            Invalid("/stages", $"The stage name '{name}' is used more than once.");
        }

        var stages = new List<WorkflowStage>();
        for (var index = 0; index < stageNodes.Count; index++)
        {
            var node = stageNodes[index];
            var at = $"/stages/{index}";
            var name = node["name"]!.GetValue<string>();
            var executor = node["executor"]!.GetValue<string>();
            var written = node["root"]!.GetValue<string>();
            var root = Resolve(directory, written, allowModules: true, out var why);
            if (root is null)
            {
                Invalid($"{at}/root", $"The root {written} of stage '{name}' {why}");
                root = written;
            }
            else if (!Directory.Exists(Path.Combine(_root, root)))
                Invalid($"{at}/root", $"The root {root} of stage '{name}' does not exist.");
            else if (executor == Executors.OpenTofu)
                CheckOpenTofuRoot(root, name, at, Invalid, address, ref errors);

            var machine = WorkflowKinds.IsMachine(manages) && index == 0;
            if (node["verify"] is not null && !machine)
                Invalid($"{at}/verify", $"Stage '{name}' cannot verify: only the OpenTofu stage that makes a machine, the first of a workflow with manages: vm or manages: container, does.");
            // A VM's only: a container's host keys are Proxmox's, made when it creates the container.
            var makesVm = manages == WorkflowKinds.Vm && index == 0 && executor == Executors.OpenTofu;
            if (node["hostKey"] is not null && !makesVm)
                Invalid($"{at}/hostKey", manages == WorkflowKinds.Container
                    ? $"Stage '{name}' cannot have hostKey: managed: a container's SSH host keys are made by Proxmox when it creates the container, "
                      + "and Octopushy pins them from its create task. Only the OpenTofu stage that makes a VM, the first of a workflow with manages: vm, installs a managed host key."
                    : $"Stage '{name}' cannot install a managed host key: only the OpenTofu stage that makes a VM, the first of a workflow with manages: vm, does.");

            ConfigureActions? configure = null;
            if (executor == Executors.Ansible)
            {
                var playbook = node["playbook"]!.GetValue<string>();
                var prepare = node["prepare"]?.GetValue<string>();
                CheckPlaybook(root, playbook, $"the playbook of stage '{name}'", $"{at}/playbook", Invalid);
                if (prepare is not null)
                    CheckPlaybook(root, prepare, $"the prepare playbook of stage '{name}'", $"{at}/prepare", Invalid);
                var onChange = new WorkflowAction(
                    WorkflowKinds.IsMachine(manages) ? WorkflowDefinition.SetupAction : name,
                    node["description"]?.GetValue<string>(),
                    playbook,
                    prepare,
                    false,
                    node["destructive"]?.GetValue<bool>() ?? false,
                    true,
                    node["timeoutMinutes"]?.GetValue<int>());
                var actions = new Dictionary<string, WorkflowAction>(StringComparer.Ordinal) { [onChange.Id] = onChange };
                foreach (var (actionId, (_, action)) in declared.Where(a => a.Value.Stage == name))
                {
                    if (actionId == onChange.Id)
                        continue;
                    CheckPlaybook(root, action.Playbook, $"the playbook of action {actionId}", $"/actions/{actionId}/playbook", Invalid);
                    if (action.Prepare is { } actionPrepare)
                        CheckPlaybook(root, actionPrepare, $"the prepare playbook of action {actionId}", $"/actions/{actionId}/prepare", Invalid);
                    actions[actionId] = action;
                }

                configure = new ConfigureActions(actions, outcomes, secrets);
            }

            var slot = node["interface"]?.GetValue<string>();
            JsonSchema? slotSchema = null;
            if (slot is not null)
            {
                if (manages != WorkflowKinds.Service)
                    Invalid($"{at}/interface", $"Stage '{name}' declares the interface {slot}, and only a stage of a workflow with manages: service fills one.");
                else
                    slotSchema = ReadInterfaceSchema(slot, name, at, Invalid, address, ref errors);
            }

            stages.Add(new WorkflowStage(
                name,
                executor,
                root,
                machine ? node["verify"]?.GetValue<string>() ?? "proxmox" : "none",
                node["image"]?.GetValue<string>(),
                configure)
            {
                ManagedHostKey = makesVm && node["hostKey"]?.GetValue<string>() == "managed",
                Interface = slot,
                InterfaceSchema = slotSchema,
            });
        }

        var ansible = stages.Where(s => s.Executor == Executors.Ansible).Select(s => s.Name).ToHashSet(StringComparer.Ordinal);
        foreach (var (actionId, (stage, _)) in declared)
        {
            if (!ansible.Contains(stage))
                Invalid($"/actions/{actionId}/stage", $"Action {actionId} names the stage '{stage}', and the workflow has no Ansible stage of that name.");
        }

        if (WorkflowKinds.IsMachine(manages))
        {
            if (stages.Count == 0 || stages[0].Executor != Executors.OpenTofu)
                Invalid("/stages/0", $"A workflow with manages: {manages} starts with the OpenTofu stage that makes the machine.");
            if (stages.Count > 2 || stages.Skip(1).Any(s => s.Executor != Executors.Ansible))
                Invalid("/stages", $"A workflow with manages: {manages} has at most one stage after the machine's, and it is the Ansible stage that sets the machine up.");
            if (declared.ContainsKey(WorkflowDefinition.SetupAction))
                Invalid($"/actions/{WorkflowDefinition.SetupAction}", "setup is the action that runs the setup stage's playbook; give this action another name.");
            if (ansible.Count == 0)
            {
                foreach (var field in new[] { "secrets", "outcomes" }.Where(json.ContainsKey))
                {
                    Invalid($"/{field}", $"{field} are for an Ansible stage, and this workflow has none.");
                }
            }
        }
        else
        {
            if (declared.Count > 0)
                Invalid("/actions", "A service offers Deploy only, so a workflow with manages: service has no actions: each stage runs its playbook.");
            foreach (var outcome in outcomes.Values.Where(o => o.ApproveVars is not null))
            {
                Invalid($"/outcomes/{outcome.Id}/approve",
                    $"Outcome {outcome.Id} is approved by a person, and a service has one approval, before anything runs: declare a result instead.");
            }

            foreach (var outcome in outcomes.Values.Where(o => !o.Promote))
            {
                Invalid($"/outcomes/{outcome.Id}/promote",
                    $"Outcome {outcome.Id} says promote: false, and a service is at its inputs once every stage passes: leave promote out.");
            }

            if (ansible.Count == 0)
            {
                foreach (var field in new[] { "secrets", "outcomes" }.Where(json.ContainsKey))
                {
                    Invalid($"/{field}", $"{field} are for an Ansible stage, and this workflow has none: OpenTofu stages get no credentials.");
                }
            }
        }

        return new WorkflowDefinition(
            id,
            version,
            _root,
            file,
            directory,
            json,
            manages,
            json["description"]?.GetValue<string>(),
            inputs,
            json["ui"]?.DeepClone().AsObject(),
            secrets,
            stages,
            declared.ToDictionary(a => a.Key, a => a.Value.Action, StringComparer.Ordinal),
            outcomes)
        {
            Valid = errors == 0,
            Schema = schema,
            SchemaJson = schemaJson,
        };
    }

    /// <summary>
    /// The schema of an interface slot: <c>interfaces/&lt;slot&gt;.schema.json</c> of the workflow's source, beside <c>workflows/</c>,
    /// shared by every workflow there that fills the slot. It validates a binding's inputs, as <c>{ inputs }</c>.
    /// </summary>
    private JsonSchema? ReadInterfaceSchema(string slot, string stage, string at, Action<string, string> invalid, string address, ref int errors)
    {
        var path = $"interfaces/{slot}.schema.json";
        if (!File.Exists(Path.Combine(_root, path)))
        {
            invalid($"{at}/interface", $"Stage '{stage}' fills the interface {slot}, and {path}, which says what its bindings hold, does not exist.");
            return null;
        }

        try
        {
            return SchemaCatalog.BuildUntrusted(File.ReadAllText(Path.Combine(_root, path)));
        }
        catch (Exception e) when (e is System.Text.Json.JsonException or JsonSchemaException or InvalidOperationException or ArgumentException)
        {
            Error(path, null, $"The schema of the interface {slot} is not a valid JSON Schema: {e.Message}", address);
            errors++;
            return null;
        }
    }

    private void CheckOpenTofuRoot(string root, string stage, string at, Action<string, string> invalid, string address, ref int errors)
    {
        var rootPath = Path.Combine(_root, root);
        var tofuFiles = Directory.EnumerateFiles(rootPath)
            .Where(f => f.EndsWith(".tf", StringComparison.Ordinal) || f.EndsWith(".tf.json", StringComparison.Ordinal))
            .ToList();
        if (tofuFiles.Count == 0)
            invalid($"{at}/root", $"The root {root} of stage '{stage}' holds no .tf files.");
        foreach (var tofuFile in tofuFiles.Where(f => f.EndsWith(".tf", StringComparison.Ordinal)))
        {
            if (DefinitionFiles.DeclaresBackend(File.ReadAllText(tofuFile)))
            {
                Error(Relative(tofuFile), null, "The root must not declare a backend: Octopushy keeps the state.", address);
                errors++;
            }
        }
    }

    private void CheckPlaybook(string root, string path, string what, string pointer, Action<string, string> invalid)
    {
        var rootPath = Path.GetFullPath(Path.Combine(_root, root));
        var full = Path.GetFullPath(Path.Combine(rootPath, path));
        if (!full.StartsWith(rootPath + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            invalid(pointer, $"{Capitalized(what)} must stay inside the root {root}.");
        else if (!File.Exists(full))
            invalid(pointer, $"{Capitalized(what)}, {path}, does not exist in {root}.");
    }

    private static string Capitalized(string text) => char.ToUpperInvariant(text[0]) + text[1..];

    /// <summary>
    /// A path written relative to the workflow's folder, relative to the source root, if it stays inside that folder or,
    /// with <paramref name="allowModules"/>, under <c>modules/</c>, and goes through no symbolic link. Null otherwise, with why.
    /// </summary>
    private string? Resolve(string workflowDirectory, string written, bool allowModules, out string why)
    {
        var where = allowModules ? $"must be inside {workflowDirectory}/ or modules/" : $"must be inside {workflowDirectory}/";
        why = where + ".";
        if (Path.IsPathRooted(written))
            return null;
        var own = Path.GetFullPath(Path.Combine(_root, workflowDirectory));
        var modules = Path.Combine(_root, "modules");
        var full = Path.GetFullPath(Path.Combine(own, written));
        var inside = full == own || full.StartsWith(own + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            || (allowModules && full.StartsWith(modules + Path.DirectorySeparatorChar, StringComparison.Ordinal));
        if (!inside)
            return null;

        // A link could lead anywhere, including outside what the hashes cover.
        for (var current = full; current.Length > _root.Length; current = Path.GetDirectoryName(current)!)
        {
            FileSystemInfo info = Directory.Exists(current) ? new DirectoryInfo(current) : new FileInfo(current);
            if (info.Exists && info.LinkTarget is not null)
            {
                why = $"goes through the symbolic link {Relative(current)}, and {where}";
                return null;
            }
        }

        return Relative(full);
    }

    private int? LineOf(string file, string pointer) => _documents.TryGetValue(file, out var document) ? document.LineOf(pointer) : null;

    private string Relative(string path) => Path.GetRelativePath(_root, path).Replace(Path.DirectorySeparatorChar, '/');

    private void Error(string file, int? line, string message, string address) =>
        _issues.Add(new DefinitionIssue(IssueSeverity.Error, file, line, message, address));
}
