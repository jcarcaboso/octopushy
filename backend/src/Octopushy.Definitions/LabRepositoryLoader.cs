using System.Text.Json.Nodes;
using Octopushy.Definitions.Schemas;
using Octopushy.Definitions.Yaml;

namespace Octopushy.Definitions;

/// <summary>
/// Reads a lab repository snapshot from a directory: every definition file is parsed, checked
/// against its schema, and cross-checked for references, and every workflow a guest or service
/// names is resolved. A problem in one resource invalidates that resource and what is below it,
/// not its siblings.
/// </summary>
public sealed partial class LabRepositoryLoader
{
    /// <summary>Keys reserved for Octopushy's own credentials, which definitions may not use.</summary>
    public const string SystemScope = "system/";

    /// <summary>Where the format is described, for messages about files in an earlier one.</summary>
    public const string FormatDocument = "docs/workflows.md";

    private readonly string _root;
    private readonly IWorkflowSource _workflows;
    /// <summary>Whether workflows come from somewhere else than this repository's own folders.</summary>
    private readonly bool _external;
    private readonly List<DefinitionIssue> _issues = [];
    private readonly List<StoreUse> _storeUses = [];
    private readonly HashSet<string> _invalidAddresses = new(StringComparer.Ordinal);
    private readonly HashSet<string> _invalidTemplates = new(StringComparer.Ordinal);
    private readonly List<ServiceDefinition> _services = [];
    private readonly List<InterfaceBinding> _bindings = [];
    private readonly Dictionary<string, YamlDocument> _documents = new(StringComparer.Ordinal);
    private bool _repositoryValid = true;

    private LabRepositoryLoader(string root, IWorkflowSource? workflows)
    {
        _root = Path.GetFullPath(root);
        _workflows = workflows ?? (WorkflowsSetting.Read(_root).Repository is { } repository
            ? new UnresolvedWorkflowSource(repository)
            : new LocalWorkflowSource(_root));
        _external = _workflows is not LocalWorkflowSource local || local.Root != _root;
    }

    /// <summary>
    /// Reads a repository as its <c>octopushy.yaml</c> says: with its own <c>workflows/</c>, or, when it names a workflows
    /// repository, with every reference unresolved, since resolving takes that repository's tags.
    /// </summary>
    public static LabRepository Load(string root) => new LabRepositoryLoader(root, null).LoadAll();

    /// <summary>Reads a repository, resolving the workflows it names from <paramref name="workflows"/>.</summary>
    public static LabRepository Load(string root, IWorkflowSource workflows) => new LabRepositoryLoader(root, workflows).LoadAll();

    private LabRepository LoadAll()
    {
        var settings = LoadSettings();
        CheckEarlierFormat();
        CheckWorkflowsElsewhere();
        var templates = LoadTemplates();

        var labs = new List<LabDefinition>();
        var installations = new List<InstallationDefinition>();
        var guests = new List<GuestDefinition>();
        var entries = new List<ResourceEntry>();
        var labsDirectory = Path.Combine(_root, "labs");
        foreach (var labDirectory in DefinitionFiles.SubDirectories(labsDirectory))
        {
            LoadLab(labDirectory, labs, installations, guests, entries);
        }

        foreach (var installation in installations)
        {
            CheckInstallation(installation, guests.Where(g => g.InstallationAddress == installation.Address).ToList());
        }
        // A token must never reach a role it was not declared for, including through another installation.
        var privilegedKeys = installations.Select(i => i.Connection.PrivilegedToken.Key).ToHashSet(StringComparer.Ordinal);
        var splitPrivilegedKeys = installations.Where(i => !i.Connection.SharedToken)
            .Select(i => i.Connection.PrivilegedToken.Key).ToHashSet(StringComparer.Ordinal);
        foreach (var installation in installations)
        {
            if (!installation.Connection.SharedToken && privilegedKeys.Contains(installation.Connection.ReadOnlyToken.Key))
                Error(installation.File, LineOf(installation.File, "/connection/readOnlyToken"),
                    "Read-only and privileged Proxmox tokens require distinct store keys across installations.", installation.Address);
            if (installation.Connection.SharedToken && splitPrivilegedKeys.Contains(installation.Connection.PrivilegedToken.Key))
                Error(installation.File, LineOf(installation.File, "/connection/token"),
                    "A shared Proxmox token cannot alias another installation's split privileged token.", installation.Address);
            if (installation.Connection.CaCertificate is { } ca && privilegedKeys.Contains(ca.Key))
                Error(installation.File, LineOf(installation.File, "/connection/tls/caCertificate"),
                    "A privileged Proxmox token cannot be used as a CA certificate.", installation.Address);
        }
        CheckInterfaces(installations);
        BindInterfaces(guests, installations);
        foreach (var guest in guests)
        {
            CheckGuest(guest, installations, templates);
            CheckSetup(guest, installations, labs.Find(l => l.Id == guest.Lab)?.Timezone);
        }
        foreach (var service in _services)
        {
            if (guests.FirstOrDefault(g => g.Address == service.GuestAddress) is { } guest)
                CheckService(service, guest, installations);
        }

        // After every reference is resolved: a source may read a workflow only when something names it.
        _issues.AddRange(_workflows.Issues);

        // An installation Octopushy only observes holds nothing it would change: see docs/safety-boundary.md.
        foreach (var installation in installations.Where(i => !i.Managed))
        {
            foreach (var entry in entries.Where(e => e.Kind is ResourceKind.Guest or ResourceKind.Service
                         && e.Address.StartsWith(installation.Address + "/", StringComparison.Ordinal)))
            {
                var what = entry.Kind == ResourceKind.Guest ? "guest" : "service";
                Error(entry.File, null,
                    $"{installation.Address} is observed only (manage: false in {installation.File}): Octopushy changes nothing there, "
                    + $"so no {what} may be defined under it. Remove this {what}, or set manage: true if Octopushy should manage the installation.",
                    entry.Address);
            }
        }

        var validEntries = entries.Select(e => e with
        {
            Valid = IsValid(e, guests, settings),
            Workflow = e.Kind switch
            {
                ResourceKind.Guest => guests.Find(g => g.Address == e.Address)?.Workflow?.Label,
                ResourceKind.Service => _services.Find(s => s.Address == e.Address)?.Workflow?.Label,
                _ => null,
            },
        }).ToList();
        return new LabRepository(
            _root,
            _repositoryValid ? settings : null,
            labs,
            installations,
            guests,
            _services,
            templates,
            _workflows.Workflows,
            validEntries,
            _issues.ToList(),
            _storeUses.ToList(),
            _repositoryValid)
        {
            WorkflowsRepository = _external ? _workflows.Name : null,
            WorkflowResolutions = (_workflows as GitTagWorkflowSource)?.Resolutions ?? [],
        };
    }

    private bool IsValid(ResourceEntry entry, List<GuestDefinition> guests, RepositoryDefinition? settings)
    {
        if (!_repositoryValid || settings is null || entry.Json is null)
        {
            return false;
        }

        for (var address = entry.Address; ; address = address[..address.LastIndexOf('/')])
        {
            if (_invalidAddresses.Contains(address))
            {
                return false;
            }

            if (!address.Contains('/'))
            {
                break;
            }
        }

        if (entry.Kind == ResourceKind.Service)
        {
            return _services.FirstOrDefault(s => s.Address == entry.Address) is { Workflow.Valid: true };
        }

        if (entry.Kind != ResourceKind.Guest)
        {
            return true;
        }

        var guest = guests.FirstOrDefault(g => g.Address == entry.Address);
        return guest is { Workflow.Valid: true }
            && (guest.Template is null || !_invalidTemplates.Contains(guest.Template));
    }

    private RepositoryDefinition? LoadSettings()
    {
        const string file = "octopushy.yaml";
        if (!File.Exists(Path.Combine(_root, file)))
        {
            Error(file, null, "This is not a lab repository: octopushy.yaml is missing at its root.");
            _repositoryValid = false;
            return null;
        }

        var (json, document) = ReadDefinition(file, DefinitionKind.Repository, address: null);
        if (json is null || document is null)
        {
            _repositoryValid = false;
            return null;
        }

        CollectStoreUses(json, "octopushy.yaml", file, document);
        if (json["workflows"] is JsonObject workflows)
        {
            if (workflows.ContainsKey("source") && workflows.ContainsKey("repository"))
                Error(file, document.LineOf("/workflows"), "workflows: name either source: local or a repository, not both.");
            if (workflows["repository"]?.GetValue<string>() is { } repository && GitUrls.Scheme(repository.Trim()) is null)
                Error(file, document.LineOf("/workflows/repository"),
                    $"workflows.repository: '{repository}' is not a repository Octopushy can fetch: use https://, ssh://, user@host:path, file:// or an absolute path.");
        }

        var defaults = json["defaults"] as JsonObject;
        return new RepositoryDefinition(
            file,
            json,
            ParsePolicy(defaults?["policy"]),
            ParseLabels(defaults?["execution"]));
    }

    /// <summary>Recipes and pipelines were the format before workflows. Nothing reads them, so a repository that has them is refused.</summary>
    private void CheckEarlierFormat()
    {
        if (Directory.Exists(Path.Combine(_root, "recipes")))
            Error("recipes", null,
                $"recipes/ is the format before workflows, and nothing reads it: move each recipe into a workflow, workflows/<id>/workflow.yaml. See {FormatDocument}.");
    }

    /// <summary>
    /// With a workflows repository, this repository holds data only: one place holds workflows and modules, so what a run
    /// executes is never a mix of two repositories.
    /// </summary>
    private void CheckWorkflowsElsewhere()
    {
        if (!_external)
            return;
        foreach (var folder in new[] { "workflows", "modules" }.Where(f => Directory.Exists(Path.Combine(_root, f))))
        {
            Error(folder, null, $"Workflows come from {_workflows.Name}; remove workflows/ and modules/ here.");
        }
    }

    private Dictionary<string, TemplateDefinition> LoadTemplates()
    {
        var result = new Dictionary<string, TemplateDefinition>(StringComparer.Ordinal);
        foreach (var directory in DefinitionFiles.SubDirectories(Path.Combine(_root, "templates")))
        {
            var id = Path.GetFileName(directory);
            var file = Relative(Path.Combine(directory, "template.yaml"));
            var address = $"templates/{id}";
            if (!DefinitionFiles.IsId(id))
            {
                Error(file, null, $"'{id}' is not a valid id: use lowercase letters, digits, and hyphens.", address);
                _invalidTemplates.Add(id);
                continue;
            }

            if (!File.Exists(Path.Combine(directory, "template.yaml")))
            {
                Warning(file, null, $"The folder templates/{id} has no template.yaml and is ignored.");
                continue;
            }

            var errorsBefore = ErrorCount();
            var (json, document) = ReadDefinition(file, DefinitionKind.Template, address);
            if (json is null || document is null)
            {
                _invalidTemplates.Add(id);
                continue;
            }

            CollectStoreUses(json, address, file, document);
            var parsed = ParseTemplate(id, file, json, document);
            var valid = parsed is not null && ErrorCount() == errorsBefore;
            if (parsed is not null)
            {
                result[id] = parsed with { Valid = valid };
            }

            if (!valid)
            {
                _invalidTemplates.Add(id);
            }
        }

        return result;
    }

    private TemplateDefinition? ParseTemplate(string id, string file, JsonObject json, YamlDocument document)
    {
        ImageSource? image = null;
        if (json["source"] is JsonObject source && source["url"] is JsonValue urlValue)
        {
            var url = urlValue.GetValue<string>();
            var checksum = source["checksum"]!.GetValue<string>();
            var colon = checksum.IndexOf(':');
            var fileName = source["fileName"]?.GetValue<string>() ?? FileNameFromUrl(url);
            if (fileName is null)
            {
                Error(file, document.LineOf("/source/url"), "The image URL does not end in a file name; set source.fileName.", $"templates/{id}");
                return null;
            }

            image = new ImageSource(url, checksum[..colon], checksum[(colon + 1)..], fileName);
        }

        var build = json["source"]?["build"] is JsonObject declared ? ParseBuild(id, file, declared, document) : null;
        var availableOn = (json["availableOn"] as JsonArray)?.Select(n => n!.GetValue<string>()).ToList() ?? [];
        return new TemplateDefinition(id, file, json, json["os"]?.GetValue<string>(), image, availableOn, build);
    }

    /// <summary>
    /// <c>source.build</c>: its workflow resolved as a guest's is, its lock read, and its build key. Null, with an error, when the
    /// workflow does not resolve, has no flake, or the lock it names is missing. See docs/built-templates.md.
    /// </summary>
    private BuildSource? ParseBuild(string id, string file, JsonObject build, YamlDocument document)
    {
        var address = $"templates/{id}";
        var reference = WorkflowReference.Parse(build["workflow"]!.GetValue<string>())!;
        var output = build["output"]!.GetValue<string>();
        var resolution = _workflows.Resolve(reference);
        string? lockPath = null;
        string? lockSha256 = null;
        if (build["lock"]?.GetValue<string>() is { } lockName)
        {
            lockPath = $"templates/{id}/{lockName}";
            var full = Path.Combine(_root, "templates", id, lockName);
            if (!File.Exists(full))
            {
                Error(file, document.LineOf("/source/build/lock"), $"source.build.lock names {lockPath}, which does not exist.", address);
                return null;
            }

            lockSha256 = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(full)));
        }

        if (resolution.Workflow is not { } workflow)
        {
            Error(file, document.LineOf("/source/build/workflow"), resolution.Problem!, address);
            return null;
        }

        if (!workflow.Valid)
        {
            Error(file, document.LineOf("/source/build/workflow"), $"{reference} resolved to {workflow.Label}, which has errors: see its issues.", address);
            return null;
        }

        if (!File.Exists(Path.Combine(workflow.SourceRoot, BuildSource.Flake, "flake.nix")))
        {
            Error(file, document.LineOf("/source/build/workflow"),
                $"{reference} resolved to {workflow.Label}, whose {(workflow.Repository is null ? "repository" : "release")} has no {BuildSource.Flake}/flake.nix to build {output} from.",
                address);
            return null;
        }

        // A workflow kept here has no release commit: the flake's own files stand in for it, so an unrelated commit builds nothing.
        var commit = workflow.Commit ?? "tree:" + ProvisionInputs.TreeHash(workflow.SourceRoot, BuildSource.Flake);
        return new BuildSource(reference.ToString(), workflow.Id, workflow.Version.ToString(), commit, workflow.Repository, output, lockPath, lockSha256,
            BuildSource.KeyOf(commit, output, lockSha256));
    }

    private void LoadLab(
        string labDirectory,
        List<LabDefinition> labs,
        List<InstallationDefinition> installations,
        List<GuestDefinition> guests,
        List<ResourceEntry> entries)
    {
        var id = Path.GetFileName(labDirectory);
        var file = Relative(Path.Combine(labDirectory, "lab.yaml"));
        if (!CheckId(id, file, id))
        {
            return;
        }

        if (!File.Exists(Path.Combine(labDirectory, "lab.yaml")))
        {
            Error(file, null, $"The lab folder labs/{id} has no lab.yaml.", id);
            entries.Add(new ResourceEntry(ResourceKind.Lab, id, null, file, null, false));
            return;
        }

        var (json, document) = ReadDefinition(file, DefinitionKind.Lab, id);
        entries.Add(new ResourceEntry(ResourceKind.Lab, id, null, file, json, false));
        if (json is not null && document is not null)
        {
            CollectStoreUses(json, id, file, document);
            labs.Add(new LabDefinition(
                id,
                file,
                json,
                json["name"]?.GetValue<string>() ?? id,
                json["timezone"]?.GetValue<string>(),
                ParsePolicy(json["policy"]),
                ParseLabels(json["execution"])));
        }

        LoadInterfaces(id, labDirectory);
        foreach (var installationDirectory in DefinitionFiles.SubDirectories(Path.Combine(labDirectory, "proxmox")))
        {
            LoadInstallation(id, installationDirectory, installations, guests, entries);
        }

        if (Directory.Exists(Path.Combine(labDirectory, "clusters")))
            Warning(file, null, "Clusters are not supported yet: nothing under this lab's clusters folder is read.", id);
    }

    /// <summary>
    /// The lab's interface bindings, <c>interfaces/&lt;slot&gt;/&lt;name&gt;.yaml</c>. An error in one is its own: the services whose
    /// stages it would fill are invalid then, and nothing else is.
    /// </summary>
    private void LoadInterfaces(string lab, string labDirectory)
    {
        foreach (var slotDirectory in DefinitionFiles.SubDirectories(Path.Combine(labDirectory, "interfaces")))
        {
            var slot = Path.GetFileName(slotDirectory);
            if (!SlotName().IsMatch(slot))
            {
                Error(Relative(slotDirectory), null,
                    $"'{slot}' is not a valid interface slot: use lowercase letters, digits and underscores, starting with a letter, as an extra var is named.",
                    $"{lab}/interfaces/{slot}");
                continue;
            }

            foreach (var path in Directory.EnumerateFiles(slotDirectory).Order(StringComparer.Ordinal))
            {
                var file = Relative(path);
                var fileName = Path.GetFileName(path);
                if (fileName.StartsWith('.'))
                    continue;
                if (!fileName.EndsWith(".yaml", StringComparison.Ordinal))
                {
                    Warning(file, null, $"Only .yaml files are interface bindings: {file} is ignored.", lab);
                    continue;
                }

                var name = fileName[..^".yaml".Length];
                var address = $"{lab}/interfaces/{slot}/{name}";
                if (!CheckId(name, file, address))
                    continue;
                var (json, document) = ReadDefinition(file, DefinitionKind.Interface, address);
                if (json is null || document is null)
                {
                    // Its errors are filed under its address; the services it would fill say they are invalid because of it.
                    _bindings.Add(new InterfaceBinding(lab, slot, name, file, [], [], new Dictionary<string, StoreReference>(StringComparer.Ordinal)));
                    continue;
                }

                CollectStoreUses(json, address, file, document);
                _bindings.Add(new InterfaceBinding(lab, slot, name, file, json,
                    json["inputs"]?.DeepClone().AsObject() ?? [], StoreReferences(json["secrets"])));
            }
        }
    }

    [System.Text.RegularExpressions.GeneratedRegex("^[a-z][a-z0-9_]{0,62}$")]
    private static partial System.Text.RegularExpressions.Regex SlotName();

    private void LoadInstallation(
        string lab,
        string directory,
        List<InstallationDefinition> installations,
        List<GuestDefinition> guests,
        List<ResourceEntry> entries)
    {
        var id = Path.GetFileName(directory);
        var address = $"{lab}/{id}";
        var file = Relative(Path.Combine(directory, "installation.yaml"));
        if (!CheckId(id, file, address))
        {
            return;
        }

        if (!File.Exists(Path.Combine(directory, "installation.yaml")))
        {
            Error(file, null, $"The installation folder {Relative(directory)} has no installation.yaml.", address);
            entries.Add(new ResourceEntry(ResourceKind.Installation, address, lab, file, null, false));
            return;
        }

        var (json, document) = ReadDefinition(file, DefinitionKind.Installation, address);
        entries.Add(new ResourceEntry(ResourceKind.Installation, address, lab, file, json, false));
        if (json is not null && document is not null)
        {
            CollectStoreUses(json, address, file, document);
            var connection = (JsonObject)json["connection"]!;
            var hosts = new Dictionary<string, HostDefinition>(StringComparer.Ordinal);
            foreach (var (hostId, hostNode) in (JsonObject)json["hosts"]!)
            {
                var host = (JsonObject)hostNode!;
                hosts[hostId] = new HostDefinition(
                    hostId,
                    host["endpoint"]?.GetValue<string>(),
                    (host["datastores"] as JsonObject)?.ToDictionary(p => p.Key, p => p.Value!.GetValue<string>()) ?? [],
                    (host["bridges"] as JsonArray)?.Select(b => b!.GetValue<string>()).ToList() ?? [],
                    host["limits"]?["concurrentOperations"]?.GetValue<int>(),
                    host);
            }

            installations.Add(new InstallationDefinition(
                id,
                lab,
                file,
                json,
                json["type"]!.GetValue<string>(),
                new ConnectionDefinition(
                    connection["endpoint"]!.GetValue<string>(),
                    connection["tls"]?["verify"]?.GetValue<bool>() ?? true,
                    ParseStoreReference(connection["token"] ?? connection["readOnlyToken"]!),
                    ParseStoreReference(connection["token"] ?? connection["privilegedToken"]!),
                    connection["tls"]?["caCertificate"] is JsonObject ca ? ParseStoreReference(ca) : null,
                    connection.ContainsKey("token")),
                hosts,
                ParsePolicy(json["policy"]),
                ParseLabels(json["execution"]),
                json["manage"]?.GetValue<bool>() ?? true)
            {
                Interfaces = (json["interfaces"] as JsonObject)?.ToDictionary(p => p.Key, p => p.Value!.GetValue<string>(), StringComparer.Ordinal)
                    ?? new Dictionary<string, string>(StringComparer.Ordinal),
            });
        }

        foreach (var guestDirectory in DefinitionFiles.SubDirectories(Path.Combine(directory, "guests")))
        {
            LoadGuest(lab, id, guestDirectory, guests, entries);
        }
    }

    private void LoadGuest(string lab, string installation, string directory, List<GuestDefinition> guests, List<ResourceEntry> entries)
    {
        var id = Path.GetFileName(directory);
        var address = $"{lab}/{installation}/{id}";
        var file = Relative(Path.Combine(directory, "guest.yaml"));
        if (!CheckId(id, file, address))
        {
            return;
        }

        if (!File.Exists(Path.Combine(directory, "guest.yaml")))
        {
            Warning(file, null, $"The guest folder {Relative(directory)} has no guest.yaml and is ignored.", address);
            return;
        }

        var (json, document) = ReadDefinition(file, DefinitionKind.Guest, address);
        entries.Add(new ResourceEntry(ResourceKind.Guest, address, $"{lab}/{installation}", file, json, false));
        // Whatever the state of guest.yaml: an error there must not read as the guest's services having gone.
        foreach (var serviceDirectory in DefinitionFiles.SubDirectories(Path.Combine(directory, "services")))
        {
            LoadService(address, serviceDirectory, entries);
        }

        if (json is null || document is null)
        {
            return;
        }

        CollectStoreUses(json, address, file, document);
        var reference = WorkflowReference.Parse(json["workflow"]!.GetValue<string>())!;
        var type = json["type"]!.GetValue<string>();
        guests.Add(new GuestDefinition(
            id,
            lab,
            installation,
            file,
            Relative(directory),
            json,
            json["lifecycle"]?.GetValue<string>() ?? "active",
            json["renamedFrom"]?.GetValue<string>(),
            json["host"]!.GetValue<string>(),
            json["vmid"]!.GetValue<int>(),
            json["template"]?.GetValue<string>(),
            reference,
            ResolveWorkflow(reference, WorkflowKinds.ForGuestType(type), $"guest of type: {type}", file, address),
            ParsePolicy(json["policy"]),
            ParseLabels(json["execution"])));
    }

    /// <summary>The workflow a reference names, if it resolves to one that manages what names it; an error otherwise.</summary>
    private WorkflowDefinition? ResolveWorkflow(WorkflowReference reference, string manages, string what, string file, string address)
    {
        var resolution = _workflows.Resolve(reference);
        if (resolution.Workflow is not { } workflow)
        {
            Error(file, LineOf(file, "/workflow"), resolution.Problem!, address);
            return null;
        }

        if (workflow.Manages != manages)
        {
            // A guest's type and its workflow's kind are one choice, written twice: say which to change.
            var hint = WorkflowKinds.IsMachine(manages) && WorkflowKinds.IsMachine(workflow.Manages)
                ? $" Set type: {workflow.Manages} to use it, or name a workflow that makes a {(manages == WorkflowKinds.Container ? "container" : "VM")}."
                : "";
            Error(file, LineOf(file, "/workflow"),
                $"{reference} resolved to {workflow.Label}, which has manages: {workflow.Manages}, and a {what} needs a workflow with manages: {manages}.{hint}", address);
            return null;
        }

        return workflow;
    }

    private void LoadService(string guest, string directory, List<ResourceEntry> entries)
    {
        var id = Path.GetFileName(directory);
        var address = $"{guest}/{id}";
        var file = Relative(Path.Combine(directory, "service.yaml"));
        if (!CheckId(id, file, address))
        {
            return;
        }

        if (File.Exists(Path.Combine(directory, "pipeline.yaml")))
        {
            var earlier = Relative(Path.Combine(directory, "pipeline.yaml"));
            Error(earlier, null,
                $"pipeline.yaml is the format before workflows, and nothing reads it: a service is service.yaml naming a workflow with manages: service. See {FormatDocument}.",
                address);
            entries.Add(new ResourceEntry(ResourceKind.Service, address, guest, earlier, null, false));
            return;
        }

        if (!File.Exists(Path.Combine(directory, "service.yaml")))
        {
            Warning(file, null, $"The service folder {Relative(directory)} has no service.yaml and is ignored.", guest);
            return;
        }

        var (json, document) = ReadDefinition(file, DefinitionKind.Service, address);
        entries.Add(new ResourceEntry(ResourceKind.Service, address, guest, file, json, false));
        if (json is null || document is null)
        {
            return;
        }

        CollectStoreUses(json, address, file, document);
        var reference = WorkflowReference.Parse(json["workflow"]!.GetValue<string>())!;
        var workflow = ResolveWorkflow(reference, WorkflowKinds.Service, "service", file, address);
        var inputs = json["inputs"] as JsonObject ?? [];
        var secrets = StoreReferences(json["secrets"]);
        var secretFiles = StoreReferences(json["secretFiles"]);
        var stages = workflow?.Stages.Select(stage => new StageDefinition(
            stage.Name,
            stage,
            inputs.DeepClone().AsObject(),
            stage.Executor == Executors.Ansible ? secrets : [],
            stage.Executor == Executors.Ansible ? secretFiles : [])).ToList() ?? [];
        _services.Add(new ServiceDefinition(id, guest, file, Relative(directory), json, reference, workflow, stages,
            ParsePolicy(json["policy"]), ParseLabels(json["execution"])));
    }

    /// <summary>Each slot an installation binds names a binding of its lab's.</summary>
    private void CheckInterfaces(List<InstallationDefinition> installations)
    {
        foreach (var installation in installations)
        {
            foreach (var (slot, name) in installation.Interfaces)
            {
                if (Binding(installation.Lab, slot, name) is null)
                    Error(installation.File, LineOf(installation.File, $"/interfaces/{slot}"),
                        $"interfaces.{slot} names {name}, and labs/{installation.Lab}/interfaces/{slot}/{name}.yaml does not exist.", installation.Address);
            }
        }
    }

    private InterfaceBinding? Binding(string lab, string slot, string name) =>
        _bindings.Find(b => b.Lab == lab && b.Slot == slot && b.Name == name);

    /// <summary>
    /// Gives each stage that declares an interface slot what its service's installation binds to it: the binding's inputs as
    /// the extra var named after the slot, its secrets as extra vars. A stage whose slot is not bound there is not run.
    /// </summary>
    private void BindInterfaces(List<GuestDefinition> guests, List<InstallationDefinition> installations)
    {
        for (var index = 0; index < _services.Count; index++)
        {
            var service = _services[index];
            if (service.Stages.All(s => s.Definition.Interface is null))
                continue;
            var installation = guests.Find(g => g.Address == service.GuestAddress) is { } guest
                ? installations.Find(i => i.Address == guest.InstallationAddress)
                : null;
            var stages = new List<StageDefinition>();
            foreach (var stage in service.Stages)
            {
                if (stage.Definition.Interface is not { } slot)
                {
                    stages.Add(stage);
                    continue;
                }

                if (installation is null || !installation.Interfaces.TryGetValue(slot, out var name)
                    || Binding(installation.Lab, slot, name) is not { } binding)
                    continue;
                var inputs = stage.Inputs.DeepClone().AsObject();
                inputs[slot] = binding.Inputs.DeepClone();
                stages.Add(stage with
                {
                    Inputs = inputs,
                    Secrets = stage.Secrets.Concat(binding.Secrets).GroupBy(p => p.Key, StringComparer.Ordinal)
                        .ToDictionary(g => g.Key, g => g.Last().Value, StringComparer.Ordinal),
                    Binding = binding,
                });
            }

            _services[index] = service with { Stages = stages };
        }
    }

    private static Dictionary<string, StoreReference> StoreReferences(JsonNode? node) =>
        (node as JsonObject)?.ToDictionary(p => p.Key, p => ParseStoreReference(p.Value!), StringComparer.Ordinal) ?? [];

    private void CheckInstallation(InstallationDefinition installation, List<GuestDefinition> guests)
    {
        if (installation.Type == "standalone" && installation.Hosts.Count != 1)
        {
            Error(installation.File, null, "A standalone installation lists exactly one host.", installation.Address);
        }

        foreach (var (id, host) in (installation.Json["snippets"]?["hosts"] as JsonObject) ?? [])
        {
            if (!installation.Hosts.ContainsKey(id))
                Error(installation.File, LineOf(installation.File, $"/snippets/hosts/{id}"), $"snippets.hosts.{id} is not a host of this installation.", installation.Address);
            // The upload's pin is public and an API token is not; neither stands in for the other.
            var key = host?["hostKey"]?["store"]?.GetValue<string>();
            if (key is not null && (key == installation.Connection.ReadOnlyToken.Key || key == installation.Connection.PrivilegedToken.Key))
                Error(installation.File, LineOf(installation.File, $"/snippets/hosts/{id}/hostKey"), "A host's SSH host key cannot be the installation's API token.", installation.Address);
        }

        foreach (var group in guests.GroupBy(g => g.Vmid).Where(g => g.Count() > 1))
        {
            foreach (var guest in group)
            {
                var others = string.Join(", ", group.Where(g => g != guest).Select(g => g.Id));
                Error(guest.File, LineOf(guest.File, "/vmid"), $"VMID {guest.Vmid} is also declared by {others} in this installation.", guest.Address);
            }
        }
    }

    private void CheckGuest(GuestDefinition guest, List<InstallationDefinition> installations, Dictionary<string, TemplateDefinition> templates)
    {
        var installation = installations.FirstOrDefault(i => i.Address == guest.InstallationAddress);
        if (installation is null)
        {
            return;
        }

        if (!installation.Hosts.TryGetValue(guest.Host, out var host))
        {
            Error(guest.File, LineOf(guest.File, "/host"), $"The host '{guest.Host}' is not listed in {installation.File}.", guest.Address);
        }
        else if (guest.Json["network"]?["bridge"] is JsonValue bridgeValue
                 && bridgeValue.TryGetValue<string>(out var bridge)
                 && host.Bridges.Count > 0
                 && !host.Bridges.Contains(bridge))
        {
            Error(guest.File, LineOf(guest.File, "/network/bridge"), $"The bridge '{bridge}' is not listed for host {host.Id}.", guest.Address);
        }

        if (guest.RenamedFrom == guest.Id)
        {
            Error(guest.File, LineOf(guest.File, "/renamedFrom"), "renamedFrom names the guest itself.", guest.Address);
        }
        else if (guest.RenamedFrom is { } previous
                 && Directory.Exists(Path.Combine(_root, Path.GetDirectoryName(guest.Directory)!, previous)))
        {
            Error(guest.File, LineOf(guest.File, "/renamedFrom"), $"renamedFrom names '{previous}', which still exists.", guest.Address);
        }

        if (guest.Template is { } templateId)
        {
            if (!templates.TryGetValue(templateId, out var template))
            {
                Error(guest.File, LineOf(guest.File, "/template"), $"The template '{templateId}' does not exist under templates/.", guest.Address);
            }
            else if (template.Image is null && template.Build is null && template.Json["source"]?["build"] is null)
            {
                // A build that did not resolve says why in the template's own issues.
                Error(guest.File, LineOf(guest.File, "/template"), $"The template '{templateId}' has no downloadable image.", guest.Address);
            }
            else if (template.AvailableOn.Count > 0 && !template.AvailableOn.Contains(installation.Address))
            {
                Warning(guest.File, LineOf(guest.File, "/template"), $"The template '{templateId}' does not list {installation.Address} in availableOn.", guest.Address);
            }
        }

        if (guest.Workflow?.Schema is { } schema)
        {
            var instance = new JsonObject
            {
                ["hardware"] = guest.Json["hardware"]?.DeepClone(),
                ["network"] = guest.Json["network"]?.DeepClone(),
                ["inputs"] = guest.Json["inputs"]?.DeepClone() ?? new JsonObject(),
            };
            foreach (var error in SchemaCatalog.Evaluate(schema, instance))
            {
                Error(guest.File, LineOf(guest.File, error.Pointer), $"{DefinitionFiles.Describe(error.Pointer)}{error.Message} (workflow {guest.Workflow.Label})", guest.Address);
            }
        }
    }

    /// <summary>What a guest needs for its workflow's Ansible stage: a way in, the secrets the workflow asks for, and sound schedules.</summary>
    private void CheckSetup(GuestDefinition guest, List<InstallationDefinition> installations, string? timezone)
    {
        void Invalid(string pointer, string message) => Error(guest.File, LineOf(guest.File, pointer), message, guest.Address);

        if (guest.Workflow is not { } workflow)
            return;
        if (workflow.Setup?.Configure is not { } configure)
        {
            foreach (var field in new[] { "secrets", "secretFiles", "schedules" }.Where(f => guest.Json[f] is not null))
            {
                Invalid($"/{field}", $"{field} are for a workflow's Ansible stage, and {workflow.Label} has none.");
            }

            if (guest.Json["policy"]?["actions"] is not null)
                Invalid("/policy/actions", $"policy.actions are for a workflow's Ansible stage, and {workflow.Label} has none.");
            return;
        }

        // A misspelt action would leave the one it meant under the guest's approval, silently.
        foreach (var action in PolicyResolver.GuestActionApprovals(guest).Keys.Where(a => !configure.Actions.ContainsKey(a)))
        {
            Invalid($"/policy/actions/{action}", $"policy.actions names '{action}', and the workflow {workflow.Label} has no such action.");
        }

        if (ConfigureInputs.AddressOf(guest) is null)
            Invalid("/access", $"{workflow.Label} sets the guest up over SSH, so it needs access.address, or a static or reserved address.");
        if (guest.Json["access"]?["user"] is null)
            Invalid("/access", $"{workflow.Label} sets the guest up over SSH, so it needs access.user.");
        if (ManagedHostKey.TrustOf(installations.Find(i => i.Address == guest.InstallationAddress), guest) == HostKeyTrust.None)
            Invalid("/access", $"{workflow.Label} sets the guest up over SSH, so it needs access.hostKey, unless its machine stage has "
                + $"hostKey: managed and its installation lists host {guest.Host} under snippets.hosts. A container needs none: its keys are pinned from its create task.");

        var secrets = ConfigureInputs.Secrets(guest, "secrets");
        var files = ConfigureInputs.Secrets(guest, "secretFiles");
        foreach (var name in configure.Secrets.Where(n => !secrets.ContainsKey(n) && !files.ContainsKey(n)))
        {
            Invalid("/secrets", $"The workflow {workflow.Label} needs secrets.{name} or secretFiles.{name}.");
        }

        var inputs = (guest.Json["inputs"] as JsonObject)?.Select(p => p.Key).ToList() ?? [];
        foreach (var name in secrets.Keys.Intersect(files.Keys).Concat(secrets.Keys.Concat(files.Keys).Intersect(inputs)))
        {
            Invalid("/secrets", $"{name} is set more than once in inputs, secrets and secretFiles.");
        }

        // A token that can write to Proxmox is not an input to a playbook, and neither is one
        // that can read it: an installation's credentials go to its API and nowhere else.
        var connectionKeys = ConnectionKeys(installations);
        var delivered = secrets.Concat(files)
            .Append(new("access.sshKey", ConfigureInputs.Reference(guest, "sshKey") ?? new StoreReference("")));
        foreach (var (name, reference) in delivered)
        {
            if (connectionKeys.Contains(reference.Key))
                Invalid(name == "access.sshKey" ? "/access/sshKey" : "/secrets", $"{name} cannot use a Proxmox API token.");
        }

        var schedules = GuestSchedules.Of(guest);
        for (var index = 0; index < schedules.Count; index++)
        {
            var schedule = schedules[index];
            if (!configure.Actions.ContainsKey(schedule.Action))
                Invalid($"/schedules/{index}/action", $"The schedule runs '{schedule.Action}', and the workflow {workflow.Label} has no such action.");
            if (!GuestSchedules.IsCron(schedule.Cron))
                Invalid($"/schedules/{index}/cron", $"'{schedule.Cron}' is not a cron expression: five fields, minute to day of the week.");
            else if (!GuestSchedules.EverFires(schedule.Cron))
                Invalid($"/schedules/{index}/cron", $"'{schedule.Cron}' never falls due.");
        }

        if (schedules.Distinct().Count() != schedules.Count)
            Invalid("/schedules", "The same schedule is listed twice.");
        if (schedules.Count > 0 && timezone is not null && !GuestSchedules.IsZone(timezone))
            Invalid("/schedules", $"The lab's timezone '{timezone}' is not an IANA time zone, and a schedule needs one.");
    }

    /// <summary>
    /// What a service needs for its workflow, checked once every guest is read. An Ansible stage runs on its guest's
    /// connection, so it needs what a guest's setup does. An error here is the service's alone: its guest and its siblings
    /// stay valid.
    /// </summary>
    private void CheckService(ServiceDefinition service, GuestDefinition guest, List<InstallationDefinition> installations)
    {
        void Invalid(string pointer, string message) => Error(service.File, LineOf(service.File, pointer), message, service.Address);

        if (service.Workflow is not { } workflow)
            return;
        var secrets = StoreReferences(service.Json["secrets"]);
        var files = StoreReferences(service.Json["secretFiles"]);
        var ansible = workflow.Stages.Where(s => s.Executor == Executors.Ansible).ToList();
        if (ansible.Count == 0)
        {
            foreach (var field in new[] { "secrets", "secretFiles" }.Where(f => service.Json[f] is not null))
            {
                Invalid($"/{field}", $"{field} are for a workflow's Ansible stages, and {workflow.Label} has none: OpenTofu stages get no credentials.");
            }
        }
        else
        {
            // No access.sshKey means Octopushy's own key, as for a guest's setup: see ManagedAccess.
            if (ConfigureInputs.AddressOf(guest) is null)
                Invalid("/workflow", $"Stage '{ansible[0].Name}' runs on the guest, which needs access.address, or a static or reserved address.");
            if (guest.Json["access"]?["user"] is null)
                Invalid("/workflow", $"Stage '{ansible[0].Name}' runs on the guest, which needs access.user.");
            if (ManagedHostKey.TrustOf(installations.Find(i => i.Address == guest.InstallationAddress), guest) == HostKeyTrust.None)
                Invalid("/workflow", $"Stage '{ansible[0].Name}' runs on the guest, which needs access.hostKey, unless the guest's machine stage "
                    + $"has hostKey: managed and its installation lists host {guest.Host} under snippets.hosts. A container needs none: its keys are pinned from its create task.");

            foreach (var name in workflow.Secrets.Where(n => !secrets.ContainsKey(n) && !files.ContainsKey(n)))
            {
                Invalid("/secrets", $"The workflow {workflow.Label} needs secrets.{name} or secretFiles.{name}.");
            }

            var inputs = (service.Json["inputs"] as JsonObject)?.Select(p => p.Key).ToList() ?? [];
            foreach (var name in secrets.Keys.Intersect(files.Keys).Concat(secrets.Keys.Concat(files.Keys).Intersect(inputs)))
            {
                Invalid("/secrets", $"{name} is set more than once in inputs, secrets and secretFiles.");
            }

            var connectionKeys = ConnectionKeys(installations);
            foreach (var (name, reference) in secrets.Concat(files))
            {
                if (connectionKeys.Contains(reference.Key))
                    Invalid("/secrets", $"{name} cannot use a Proxmox API token.");
            }
        }

        if (workflow.Schema is { } schema)
        {
            var instance = new JsonObject { ["inputs"] = service.Json["inputs"]?.DeepClone() ?? new JsonObject() };
            foreach (var error in SchemaCatalog.Evaluate(schema, instance))
            {
                Invalid(error.Pointer, $"{DefinitionFiles.Describe(error.Pointer)}{error.Message} (workflow {workflow.Label})");
            }
        }

        CheckBindings(service, workflow, secrets, files, installations);
    }

    /// <summary>
    /// What the bindings a service's stages got must hold: inputs its workflows repository's slot schema accepts, extra vars no
    /// other part of the service sets, and no Proxmox token. An error is the service's: the binding is shared, and another
    /// workflow's schema may accept it.
    /// </summary>
    private void CheckBindings(
        ServiceDefinition service,
        WorkflowDefinition workflow,
        Dictionary<string, StoreReference> secrets,
        Dictionary<string, StoreReference> files,
        List<InstallationDefinition> installations)
    {
        var inputs = (service.Json["inputs"] as JsonObject)?.Select(p => p.Key).ToHashSet(StringComparer.Ordinal) ?? [];
        var connectionKeys = ConnectionKeys(installations);
        foreach (var binding in service.Stages.Select(s => s.Binding).OfType<InterfaceBinding>().DistinctBy(b => b.Address))
        {
            void Invalid(string message) => Error(service.File, LineOf(service.File, "/workflow"), message, service.Address);
            var stage = service.Stages.First(s => s.Binding == binding).Definition;
            if (_invalidAddresses.Contains(binding.Address))
            {
                Invalid($"Stage '{stage.Name}' fills the interface {binding.Slot} with {binding.File}, which has errors.");
                continue;
            }

            if (inputs.Contains(binding.Slot))
                Invalid($"inputs.{binding.Slot} is the extra var stage '{stage.Name}' gets {binding.File} as: give the input another name.");
            foreach (var name in binding.Secrets.Keys.Where(n => n == binding.Slot || inputs.Contains(n) || secrets.ContainsKey(n) || files.ContainsKey(n)))
            {
                Invalid($"{name}, a secret of {binding.File}, is set again by this service: give one of them another name.");
            }

            foreach (var (name, reference) in binding.Secrets.Where(p => connectionKeys.Contains(p.Value.Key)))
            {
                Error(binding.File, LineOf(binding.File, $"/secrets/{name}"), $"{name} cannot use a Proxmox API token.", service.Address);
            }

            if (stage.InterfaceSchema is { } slotSchema)
            {
                var instance = new JsonObject { ["inputs"] = binding.Inputs.DeepClone() };
                foreach (var error in SchemaCatalog.Evaluate(slotSchema, instance))
                {
                    Error(binding.File, LineOf(binding.File, error.Pointer),
                        $"{DefinitionFiles.Describe(error.Pointer)}{error.Message} (interface {binding.Slot} of {workflow.Label}, for {service.Address})", service.Address);
                }
            }
        }
    }

    private static HashSet<string> ConnectionKeys(List<InstallationDefinition> installations) =>
        installations.SelectMany(i => new[] { i.Connection.ReadOnlyToken.Key, i.Connection.PrivilegedToken.Key }).ToHashSet(StringComparer.Ordinal);

    private (JsonObject? Json, YamlDocument? Document) ReadDefinition(string file, DefinitionKind kind, string? address) =>
        DefinitionFiles.Read(_root, file, kind, _documents, (f, line, message) => Error(f, line, message, address));

    private int? LineOf(string file, string pointer) =>
        _documents.TryGetValue(file, out var document) ? document.LineOf(pointer) : null;

    private void CollectStoreUses(JsonNode node, string consumer, string file, YamlDocument document, string pointer = "")
    {
        switch (node)
        {
            case JsonObject obj:
                // Permissive sections are scanned too: extra fields must not hide system-scope keys.
                if (obj["store"] is JsonValue key && key.TryGetValue<string>(out var storeKey))
                {
                    var version = obj["version"] is JsonValue value && value.TryGetValue<int>(out var number) && number > 0
                        ? number
                        : (int?)null;
                    _storeUses.Add(new StoreUse(storeKey, consumer, file, version));
                    if (storeKey.StartsWith(SystemScope, StringComparison.Ordinal))
                    {
                        Error(file, document.LineOf(pointer), $"The key '{storeKey}' is in the system scope, which definitions cannot use.",
                            consumer == "octopushy.yaml" ? null : consumer);
                    }
                }

                foreach (var (name, child) in obj)
                {
                    if (child is not null)
                    {
                        CollectStoreUses(child, consumer, file, document, pointer + "/" + YamlDocumentReader.EscapePointer(name));
                    }
                }

                break;
            case JsonArray array:
                for (var i = 0; i < array.Count; i++)
                {
                    if (array[i] is { } child)
                    {
                        CollectStoreUses(child, consumer, file, document, $"{pointer}/{i}");
                    }
                }

                break;
        }
    }

    private bool CheckId(string id, string file, string address)
    {
        if (DefinitionFiles.IsId(id))
        {
            return true;
        }

        Error(file, null, $"'{id}' is not a valid id: use lowercase letters, digits, and hyphens.", address);
        return false;
    }

    private static PolicySettings? ParsePolicy(JsonNode? node) =>
        node is JsonObject policy
            ? new PolicySettings(policy["approval"]?.GetValue<string>(), policy["destructive"]?.GetValue<string>())
            : null;

    private static List<string>? ParseLabels(JsonNode? execution) =>
        (execution?["agentLabels"] as JsonArray)?.Select(l => l!.GetValue<string>()).ToList();

    private static StoreReference ParseStoreReference(JsonNode node) =>
        new(node["store"]!.GetValue<string>(), node["version"]?.GetValue<int>());

    private static string? FileNameFromUrl(string url)
    {
        var name = Uri.TryCreate(url, UriKind.Absolute, out var uri) ? Path.GetFileName(uri.AbsolutePath) : null;
        return string.IsNullOrEmpty(name) ? null : name;
    }

    private string Relative(string path) => Path.GetRelativePath(_root, path).Replace(Path.DirectorySeparatorChar, '/');

    private int ErrorCount() => _issues.Count(i => i.Severity == IssueSeverity.Error);

    private void Error(string file, int? line, string message, string? address = null)
    {
        _issues.Add(new DefinitionIssue(IssueSeverity.Error, file, line, message, address));
        if (address is null)
        {
            _repositoryValid = false;
        }
        else
        {
            _invalidAddresses.Add(address);
        }
    }

    private void Warning(string file, int? line, string message, string? address = null) =>
        _issues.Add(new DefinitionIssue(IssueSeverity.Warning, file, line, message, address));
}
