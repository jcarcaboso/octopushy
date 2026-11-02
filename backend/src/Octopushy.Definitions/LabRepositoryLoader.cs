using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Json.Schema;
using Octopushy.Definitions.Schemas;
using Octopushy.Definitions.Yaml;

namespace Octopushy.Definitions;

/// <summary>
/// Reads a lab repository snapshot from a directory: every definition file is parsed, checked
/// against its schema, and cross-checked for references. A problem in one resource invalidates
/// that resource and what is below it, not its siblings.
/// </summary>
public sealed partial class LabRepositoryLoader
{
    /// <summary>Keys reserved for Octopushy's own credentials, which definitions may not use.</summary>
    public const string SystemScope = "system/";

    private readonly string _root;
    private readonly List<DefinitionIssue> _issues = [];
    private readonly List<StoreUse> _storeUses = [];
    private readonly HashSet<string> _invalidAddresses = new(StringComparer.Ordinal);
    private readonly HashSet<string> _invalidTemplates = new(StringComparer.Ordinal);
    private readonly HashSet<string> _invalidRecipes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, JsonSchema?> _inputSchemas = new(StringComparer.Ordinal);
    private readonly List<ServiceDefinition> _services = [];
    private bool _repositoryValid = true;

    private LabRepositoryLoader(string root)
    {
        _root = Path.GetFullPath(root);
    }

    public static LabRepository Load(string root) => new LabRepositoryLoader(root).LoadAll();

    private LabRepository LoadAll()
    {
        var settings = LoadSettings();
        var templates = LoadShared("templates", "template.yaml", DefinitionKind.Template, ParseTemplate, _invalidTemplates);
        var recipes = LoadShared("recipes", "recipe.yaml", DefinitionKind.Recipe, ParseRecipe, _invalidRecipes);

        var labs = new List<LabDefinition>();
        var installations = new List<InstallationDefinition>();
        var guests = new List<GuestDefinition>();
        var entries = new List<ResourceEntry>();
        var labsDirectory = Path.Combine(_root, "labs");
        foreach (var labDirectory in SubDirectories(labsDirectory))
        {
            LoadLab(labDirectory, labs, installations, guests, entries);
        }

        foreach (var recipe in recipes.Values)
        {
            CheckRecipe(recipe);
        }

        foreach (var installation in installations)
        {
            CheckInstallation(installation, guests.Where(g => g.InstallationAddress == installation.Address).ToList());
        }
        // Backend env is available at preview time: it must not be another route to
        // a configured writer, including a writer from a different installation.
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
        foreach (var backend in settings?.StateBackends.Values ?? [])
        {
            foreach (var (name, value) in backend.Env)
            {
                if (name.StartsWith("PROXMOX_", StringComparison.Ordinal))
                    Error(settings!.File, LineOf(settings.File, $"/stateBackends/{backend.Id}/env/{name}"),
                        $"The Proxmox provider environment '{name}' is reserved.");
                if (value.Store is { } reference && privilegedKeys.Contains(reference.Key))
                    Error(settings!.File, LineOf(settings.File, $"/stateBackends/{backend.Id}/env/{name}"),
                        $"A privileged Proxmox token cannot be used as backend environment '{name}'.");
            }
        }

        foreach (var guest in guests)
        {
            CheckGuest(guest, settings, installations, templates, recipes);
            CheckSetup(guest, recipes, installations, labs.Find(l => l.Id == guest.Lab)?.Timezone);
        }
        foreach (var service in _services)
        {
            if (guests.FirstOrDefault(g => g.Address == service.GuestAddress) is { } guest)
                CheckService(service, guest, recipes, installations);
        }
        foreach (var duplicate in guests.Where(g => g.ManagedWorker is not null)
                     .GroupBy(g => g.ManagedWorker!.Generation).Where(g => g.Count() > 1))
        {
            foreach (var guest in duplicate)
                Error(guest.File, LineOf(guest.File, "/worker/generation"),
                    "A worker generation must belong to exactly one guest.", guest.Address);
        }

        var validEntries = entries.Select(e => e with { Valid = IsValid(e, guests, settings) }).ToList();
        return new LabRepository(
            _root,
            _repositoryValid ? settings : null,
            labs,
            installations,
            guests,
            _services,
            templates,
            recipes,
            validEntries,
            _issues.ToList(),
            _storeUses.ToList(),
            _repositoryValid);
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
            return _services.FirstOrDefault(s => s.Address == entry.Address) is { } service
                && !service.Stages.Any(s => _invalidRecipes.Contains(s.Recipe));
        }

        if (entry.Kind != ResourceKind.Guest)
        {
            return true;
        }

        var guest = guests.FirstOrDefault(g => g.Address == entry.Address);
        return guest is not null
            && (guest.Template is null || !_invalidTemplates.Contains(guest.Template))
            && !_invalidRecipes.Contains(guest.Recipe)
            && (guest.SetupRecipe is null || !_invalidRecipes.Contains(guest.SetupRecipe));
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
        var defaults = json["defaults"] as JsonObject;
        var backends = new Dictionary<string, StateBackendDefinition>(StringComparer.Ordinal);
        if (json["stateBackends"] is JsonObject backendObjects)
        {
            foreach (var (id, node) in backendObjects)
            {
                var backend = (JsonObject)node!;
                var config = (backend["config"] as JsonObject)?.ToDictionary(p => p.Key, p => p.Value?.DeepClone())
                    ?? [];
                var env = new Dictionary<string, EnvValue>(StringComparer.Ordinal);
                if (backend["env"] is JsonObject envObject)
                {
                    foreach (var (name, value) in envObject)
                    {
                        env[name] = value is JsonObject reference
                            ? new EnvValue(null, ParseStoreReference(reference))
                            : new EnvValue(value!.GetValue<string>(), null);
                    }
                }

                backends[id] = new StateBackendDefinition(
                    id,
                    backend["type"]!.GetValue<string>(),
                    config,
                    backend["workspace"]?.GetValue<string>(),
                    env,
                    backend);
            }
        }

        return new RepositoryDefinition(
            file,
            json,
            ParsePolicy(defaults?["policy"]),
            ParseLabels(defaults?["execution"]),
            backends);
    }

    private Dictionary<string, T> LoadShared<T>(
        string folder,
        string fileName,
        DefinitionKind kind,
        Func<string, string, JsonObject, YamlDocument, T?> parse,
        HashSet<string> invalid)
    {
        var result = new Dictionary<string, T>(StringComparer.Ordinal);
        foreach (var directory in SubDirectories(Path.Combine(_root, folder)))
        {
            var id = Path.GetFileName(directory);
            var file = Relative(Path.Combine(directory, fileName));
            if (!IdPattern().IsMatch(id))
            {
                Error(file, null, $"'{id}' is not a valid id: use lowercase letters, digits, and hyphens.", $"{folder}/{id}");
                invalid.Add(id);
                continue;
            }

            if (!File.Exists(Path.Combine(directory, fileName)))
            {
                Warning(file, null, $"The folder {folder}/{id} has no {fileName} and is ignored.");
                continue;
            }

            var errorsBefore = ErrorCount();
            var (json, document) = ReadDefinition(file, kind, $"{folder}/{id}");
            if (json is null || document is null)
            {
                invalid.Add(id);
                continue;
            }

            CollectStoreUses(json, $"{folder}/{id}", file, document);
            var parsed = parse(id, file, json, document);
            if (parsed is not null)
            {
                result[id] = parsed;
            }

            if (parsed is null || ErrorCount() > errorsBefore)
            {
                invalid.Add(id);
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
        else if (json["source"]?["build"] is not null)
        {
            Warning(file, document.LineOf("/source/build"), "Building images is not supported yet; guests cannot use this template.", $"templates/{id}");
        }

        var availableOn = (json["availableOn"] as JsonArray)?.Select(n => n!.GetValue<string>()).ToList() ?? [];
        return new TemplateDefinition(id, file, json, json["os"]?.GetValue<string>(), image, availableOn);
    }

    private RecipeDefinition? ParseRecipe(string id, string file, JsonObject json, YamlDocument document)
    {
        var recipeDirectory = Path.GetDirectoryName(Path.Combine(_root, file))!;
        var root = ResolveInside(recipeDirectory, json["root"]!.GetValue<string>());
        if (root is null)
        {
            Error(file, document.LineOf("/root"), "The root must stay inside the repository.", $"recipes/{id}");
            return null;
        }

        string? inputs = null;
        if (json["inputs"] is JsonValue inputsValue)
        {
            inputs = ResolveInside(recipeDirectory, inputsValue.GetValue<string>());
            if (inputs is null)
            {
                Error(file, document.LineOf("/inputs"), "The inputs schema must stay inside the repository.", $"recipes/{id}");
                return null;
            }
        }

        return new RecipeDefinition(
            id,
            file,
            json,
            json["operation"]!.GetValue<string>(),
            json["executor"]!.GetValue<string>(),
            root,
            inputs,
            json["verify"]?.GetValue<string>() ?? "proxmox",
            json["image"]?.GetValue<string>(),
            json["guestFilesystem"]?.GetValue<string>(),
            ParseConfigure(json));
    }

    private static ConfigureRecipe? ParseConfigure(JsonObject json)
    {
        if (json["actions"] is not JsonObject actions)
        {
            return null;
        }

        return new ConfigureRecipe(
            actions.ToDictionary(
                p => p.Key,
                p => new RecipeAction(
                    p.Key,
                    p.Value!["description"]?.GetValue<string>(),
                    p.Value!["playbook"]!.GetValue<string>(),
                    p.Value!["prepare"]?.GetValue<string>(),
                    p.Value!["snapshot"] is not null,
                    p.Value!["destructive"]!.GetValue<bool>(),
                    p.Value!["onChange"]?.GetValue<bool>() ?? false,
                    p.Value!["timeoutMinutes"]?.GetValue<int>()),
                StringComparer.Ordinal),
            (json["outcomes"] as JsonObject)?.ToDictionary(
                p => p.Key,
                p => new RecipeOutcome(
                    p.Key,
                    p.Value!["description"]?.GetValue<string>(),
                    p.Value!["approve"]?["vars"]?.DeepClone().AsObject(),
                    p.Value!["result"]?.GetValue<string>() ?? "failed"),
                StringComparer.Ordinal) ?? [],
            (json["secrets"] as JsonArray)?.Select(s => s!.GetValue<string>()).ToList() ?? []);
    }

    private void CheckRecipe(RecipeDefinition recipe)
    {
        var address = $"recipes/{recipe.Id}";
        if (recipe.GuestFilesystem is not null && recipe.Verify != "proxmox")
        {
            Error(recipe.File, null, "guestFilesystem requires verify: proxmox.", address);
        }
        var rootPath = Path.Combine(_root, recipe.Root);
        if (!Directory.Exists(rootPath))
        {
            Error(recipe.File, null, $"The root {recipe.Root} does not exist.", address);
            _invalidRecipes.Add(recipe.Id);
            return;
        }

        if (recipe.Configure is { } configure)
        {
            CheckConfigureRecipe(recipe, configure, rootPath, address);
            CheckInputsSchema(recipe, address);
            return;
        }

        var tofuFiles = Directory.EnumerateFiles(rootPath)
            .Where(f => f.EndsWith(".tf", StringComparison.Ordinal) || f.EndsWith(".tf.json", StringComparison.Ordinal))
            .ToList();
        if (tofuFiles.Count == 0)
        {
            Error(recipe.File, null, $"The root {recipe.Root} holds no .tf files.", address);
            _invalidRecipes.Add(recipe.Id);
        }

        foreach (var tofuFile in tofuFiles.Where(f => f.EndsWith(".tf", StringComparison.Ordinal)))
        {
            if (BackendBlock().IsMatch(File.ReadAllText(tofuFile)))
            {
                Error(Relative(tofuFile), null, "The root must not declare a backend: Octopushy sets it from the guest's state backend.", address);
                _invalidRecipes.Add(recipe.Id);
            }
        }

        CheckInputsSchema(recipe, address);
    }

    private void CheckConfigureRecipe(RecipeDefinition recipe, ConfigureRecipe configure, string rootPath, string address)
    {
        void Invalid(string message)
        {
            Error(recipe.File, null, message, address);
            _invalidRecipes.Add(recipe.Id);
        }

        foreach (var action in configure.Actions.Values)
        {
            foreach (var (what, path) in new[] { ("playbook", action.Playbook), ("prepare playbook", action.Prepare) })
            {
                if (path is null)
                {
                    continue;
                }

                var full = Path.GetFullPath(Path.Combine(rootPath, path));
                if (!full.StartsWith(Path.GetFullPath(rootPath) + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                {
                    Invalid($"The {what} of {action.Id} must stay inside the root.");
                }
                else if (!File.Exists(full))
                {
                    Invalid($"The {what} {path} of {action.Id} does not exist in {recipe.Root}.");
                }
            }
        }

        if (configure.Actions.Values.Count(a => a.OnChange) > 1)
        {
            Invalid("At most one action may set onChange.");
        }

        if (configure.Actions.Values.Any(a => a.OnChange && a.UsesAppliedSnapshot))
        {
            Invalid("An onChange action cannot reuse a snapshot: a new machine has none.");
        }
    }

    private void CheckInputsSchema(RecipeDefinition recipe, string address)
    {
        if (recipe.InputsSchema is { } inputs)
        {
            _inputSchemas[recipe.Id] = null;
            var inputsPath = Path.Combine(_root, inputs);
            if (!File.Exists(inputsPath))
            {
                Error(recipe.File, null, $"The inputs schema {inputs} does not exist.", address);
                _invalidRecipes.Add(recipe.Id);
                return;
            }

            try
            {
                _inputSchemas[recipe.Id] = SchemaCatalog.BuildUntrusted(File.ReadAllText(inputsPath));
            }
            catch (Exception e) when (e is System.Text.Json.JsonException or JsonSchemaException or InvalidOperationException or ArgumentException)
            {
                Error(inputs, null, $"The inputs schema is not a valid JSON Schema: {e.Message}", address);
                _invalidRecipes.Add(recipe.Id);
            }
        }
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

        foreach (var installationDirectory in SubDirectories(Path.Combine(labDirectory, "proxmox")))
        {
            LoadInstallation(id, installationDirectory, installations, guests, entries);
        }

        if (Directory.Exists(Path.Combine(labDirectory, "clusters")))
            Warning(file, null, "Clusters are not supported yet: nothing under this lab's clusters folder is read.", id);
    }

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
                ParseLabels(json["execution"])));
        }

        foreach (var guestDirectory in SubDirectories(Path.Combine(directory, "guests")))
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
        foreach (var serviceDirectory in SubDirectories(Path.Combine(directory, "services")))
        {
            LoadService(address, serviceDirectory, entries);
        }

        if (json is null || document is null)
        {
            return;
        }

        CollectStoreUses(json, address, file, document);
        var provision = (JsonObject)json["provision"]!;
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
            provision["recipe"]!.GetValue<string>(),
            provision["state"]?["backend"]?.GetValue<string>(),
            ParsePolicy(json["policy"]),
            ParseLabels(json["execution"])));
    }

    private void LoadService(string guest, string directory, List<ResourceEntry> entries)
    {
        var id = Path.GetFileName(directory);
        var address = $"{guest}/{id}";
        var file = Relative(Path.Combine(directory, "pipeline.yaml"));
        if (!CheckId(id, file, address))
        {
            return;
        }

        if (!File.Exists(Path.Combine(directory, "pipeline.yaml")))
        {
            Warning(file, null, $"The service folder {Relative(directory)} has no pipeline.yaml and is ignored.", guest);
            return;
        }

        var (json, document) = ReadDefinition(file, DefinitionKind.Pipeline, address);
        entries.Add(new ResourceEntry(ResourceKind.Service, address, guest, file, json, false));
        if (json is null || document is null)
        {
            return;
        }

        CollectStoreUses(json, address, file, document);
        var shared = json["inputs"] as JsonObject;
        var stages = json["stages"]!.AsArray().Select(node =>
        {
            var inputs = shared?.DeepClone().AsObject() ?? [];
            foreach (var (name, value) in (node!["inputs"] as JsonObject) ?? [])
            {
                inputs[name] = value?.DeepClone();
            }

            return new StageDefinition(
                node["name"]!.GetValue<string>(),
                node["recipe"]!.GetValue<string>(),
                inputs,
                StageSecrets(node["secrets"]),
                StageSecrets(node["secretFiles"]));
        }).ToList();
        _services.Add(new ServiceDefinition(id, guest, file, Relative(directory), json, stages, ParsePolicy(json["policy"]), ParseLabels(json["execution"])));
    }

    private static Dictionary<string, StoreReference> StageSecrets(JsonNode? node) =>
        (node as JsonObject)?.ToDictionary(p => p.Key, p => ParseStoreReference(p.Value!), StringComparer.Ordinal) ?? [];

    private void CheckInstallation(InstallationDefinition installation, List<GuestDefinition> guests)
    {
        if (installation.Type == "standalone" && installation.Hosts.Count != 1)
        {
            Error(installation.File, null, "A standalone installation lists exactly one host.", installation.Address);
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

    private void CheckGuest(
        GuestDefinition guest,
        RepositoryDefinition? settings,
        List<InstallationDefinition> installations,
        Dictionary<string, TemplateDefinition> templates,
        Dictionary<string, RecipeDefinition> recipes)
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
            else if (template.Image is null)
            {
                Error(guest.File, LineOf(guest.File, "/template"), $"The template '{templateId}' has no downloadable image.", guest.Address);
            }
            else if (template.AvailableOn.Count > 0 && !template.AvailableOn.Contains(installation.Address))
            {
                Warning(guest.File, LineOf(guest.File, "/template"), $"The template '{templateId}' does not list {installation.Address} in availableOn.", guest.Address);
            }
        }

        if (!recipes.TryGetValue(guest.Recipe, out var recipe))
        {
            Error(guest.File, LineOf(guest.File, "/provision/recipe"), $"The recipe '{guest.Recipe}' does not exist under recipes/.", guest.Address);
        }
        else if (recipe.Operation != "provision")
        {
            Error(guest.File, LineOf(guest.File, "/provision/recipe"), $"The recipe '{guest.Recipe}' does not provision guests.", guest.Address);
        }
        else if (_inputSchemas.GetValueOrDefault(recipe.Id) is { } inputs)
        {
            var instance = new JsonObject
            {
                ["hardware"] = guest.Json["hardware"]?.DeepClone(),
                ["network"] = guest.Json["network"]?.DeepClone(),
            };
            foreach (var error in SchemaCatalog.Evaluate(inputs, instance))
            {
                Error(guest.File, LineOf(guest.File, error.Pointer), $"{Describe(error.Pointer)}{error.Message} (recipe {recipe.Id})", guest.Address);
            }
        }

        if (recipe?.GuestFilesystem is not null
            && (guest.Json["hardware"]?["disks"] is not JsonArray disks || disks.Count == 0
                || disks[0]?["size"] is not JsonValue size || !size.TryGetValue<int>(out var gib)
                || gib < 2 || gib > 65536))
        {
            Error(guest.File, null, "guestFilesystem requires hardware.disks[0].size in GiB (2–65536).", guest.Address);
        }

        if (settings is not null && guest.StateBackend is { } backend && !settings.StateBackends.ContainsKey(backend))
        {
            Error(guest.File, LineOf(guest.File, "/provision/state/backend"), $"The state backend '{backend}' is not defined in octopushy.yaml.", guest.Address);
        }

        if (guest.ManagedWorker is not null)
        {
            if (guest.Retired || guest.RenamedFrom is not null)
                Error(guest.File, null, "Managed worker retirement, replacement and rename are not supported. Preserve the current generation and its state disk.", guest.Address);
            if (guest.Template != "octopushy-worker-v1" || guest.Recipe != "octopushy-worker-v1"
                || recipe?.GuestFilesystem != "nixos-ext4-root" || recipe.Verify != "proxmox")
                Error(guest.File, null, "A managed worker requires the version 1 worker image, recipe, Proxmox verification and NixOS root checks.", guest.Address);
            if (guest.Policy?.Approval != "manual" || guest.Policy?.Destructive != "manual")
                Error(guest.File, null, "Managed workers require explicit manual approval and destructive policies.", guest.Address);
            var hardware = guest.Json["hardware"];
            if (hardware?["disks"] is not JsonArray workerDisks || workerDisks.Count != 2
                || !IntegerBetween((workerDisks[0] as JsonObject)?["size"], 8, 65536)
                || !IntegerBetween((workerDisks[1] as JsonObject)?["size"], 16, 65536))
                Error(guest.File, null, "A managed worker requires exactly two disks: boot (at least 8 GiB) and persistent agent state (at least 16 GiB).", guest.Address);
            if (!IntegerBetween(hardware?["sockets"], 1, 16)
                || !IntegerBetween(hardware?["cores"], 1, 128)
                || !IntegerBetween(hardware?["memory"], 512, 1048576)
                || hardware!["sockets"]!.GetValue<int>() * hardware["cores"]!.GetValue<int>() > 128)
                Error(guest.File, null, "A managed worker requires bounded CPU sockets, cores and RAM.", guest.Address);
        }
    }

    private void CheckSetup(GuestDefinition guest, Dictionary<string, RecipeDefinition> recipes, List<InstallationDefinition> installations, string? timezone)
    {
        if (guest.SetupRecipe is not { } id)
        {
            if (new[] { "inputs", "secrets", "secretFiles", "schedules" }.Any(f => guest.Json["setup"]?[f] is not null))
                Error(guest.File, LineOf(guest.File, "/setup"), "setup.inputs, setup secrets and setup.schedules need a setup.recipe.", guest.Address);
            return;
        }

        void Invalid(string pointer, string message) => Error(guest.File, LineOf(guest.File, pointer), message, guest.Address);

        if (guest.ManagedWorker is not null)
            Invalid("/setup/recipe", "A managed worker is configured by its image; it cannot have a setup recipe.");
        if (!recipes.TryGetValue(id, out var recipe))
        {
            Invalid("/setup/recipe", $"The recipe '{id}' does not exist under recipes/.");
            return;
        }

        if (recipe.Configure is not { } configure)
        {
            Invalid("/setup/recipe", $"The recipe '{id}' does not configure guests.");
            return;
        }

        if (ConfigureInputs.AddressOf(guest) is null)
            Invalid("/access", "A guest with a setup recipe needs access.address, or a static or reserved address.");
        foreach (var field in new[] { "user", "hostKey" })
        {
            if (guest.Json["access"]?[field] is null)
                Invalid("/access", $"A guest with a setup recipe needs access.{field}.");
        }

        var secrets = ConfigureInputs.Secrets(guest, "secrets");
        var files = ConfigureInputs.Secrets(guest, "secretFiles");
        foreach (var name in configure.Secrets.Where(n => !secrets.ContainsKey(n) && !files.ContainsKey(n)))
        {
            Invalid("/setup", $"The recipe '{id}' needs setup.secrets.{name} or setup.secretFiles.{name}.");
        }

        var inputs = (guest.Json["setup"]?["inputs"] as JsonObject)?.Select(p => p.Key).ToList() ?? [];
        foreach (var name in secrets.Keys.Intersect(files.Keys).Concat(secrets.Keys.Concat(files.Keys).Intersect(inputs)))
        {
            Invalid("/setup", $"{name} is set more than once in setup.");
        }

        // A token that can write to Proxmox is not an input to a playbook, and neither is one
        // that can read it: an installation's credentials go to its API and nowhere else.
        var connectionKeys = installations.SelectMany(i => new[] { i.Connection.ReadOnlyToken.Key, i.Connection.PrivilegedToken.Key })
            .ToHashSet(StringComparer.Ordinal);
        var delivered = secrets.Concat(files)
            .Append(new("access.sshKey", ConfigureInputs.Reference(guest, "sshKey") ?? new StoreReference("")));
        foreach (var (name, reference) in delivered)
        {
            if (connectionKeys.Contains(reference.Key))
                Invalid("/setup", $"{name} cannot use a Proxmox API token.");
        }

        var schedules = GuestSchedules.Of(guest);
        for (var index = 0; index < schedules.Count; index++)
        {
            var schedule = schedules[index];
            if (!configure.Actions.ContainsKey(schedule.Action))
                Invalid($"/setup/schedules/{index}/action", $"The schedule runs '{schedule.Action}', and the recipe '{id}' has no such action.");
            if (!GuestSchedules.IsCron(schedule.Cron))
                Invalid($"/setup/schedules/{index}/cron", $"'{schedule.Cron}' is not a cron expression: five fields, minute to day of the week.");
            else if (!GuestSchedules.EverFires(schedule.Cron))
                Invalid($"/setup/schedules/{index}/cron", $"'{schedule.Cron}' never falls due.");
        }

        if (schedules.Distinct().Count() != schedules.Count)
            Invalid("/setup/schedules", "The same schedule is listed twice.");
        if (schedules.Count > 0 && timezone is not null && !GuestSchedules.IsZone(timezone))
            Invalid("/setup/schedules", $"The lab's timezone '{timezone}' is not an IANA time zone, and a schedule needs one.");

        if (_inputSchemas.GetValueOrDefault(recipe.Id) is { } schema)
        {
            var instance = guest.Json["setup"]?["inputs"]?.DeepClone() ?? new JsonObject();
            foreach (var error in SchemaCatalog.Evaluate(schema, instance))
            {
                Invalid($"/setup/inputs{error.Pointer}", $"{Describe(error.Pointer)}{error.Message} (recipe {recipe.Id})");
            }
        }
    }

    /// <summary>
    /// What a pipeline may name, checked once every recipe is read. A stage runs on its guest's
    /// connection, so an Ansible stage needs what a setup recipe does. An error here is the
    /// service's alone: its guest and its siblings stay valid.
    /// </summary>
    private void CheckService(ServiceDefinition service, GuestDefinition guest, Dictionary<string, RecipeDefinition> recipes, List<InstallationDefinition> installations)
    {
        void Invalid(string pointer, string message) => Error(service.File, LineOf(service.File, pointer), message, service.Address);

        if (guest.ManagedWorker is not null)
            Invalid("", "A managed worker is configured by its image; it cannot have services.");
        foreach (var name in service.Stages.GroupBy(s => s.Name).Where(g => g.Count() > 1).Select(g => g.Key))
        {
            Invalid("/stages", $"The stage name '{name}' is used more than once.");
        }

        var connectionKeys = installations.SelectMany(i => new[] { i.Connection.ReadOnlyToken.Key, i.Connection.PrivilegedToken.Key })
            .ToHashSet(StringComparer.Ordinal);
        for (var index = 0; index < service.Stages.Count; index++)
        {
            var stage = service.Stages[index];
            var at = $"/stages/{index}";
            if (!recipes.TryGetValue(stage.Recipe, out var recipe))
            {
                Invalid($"{at}/recipe", $"The recipe '{stage.Recipe}' does not exist under recipes/.");
                continue;
            }

            if (recipe.Configure is { } configure)
            {
                if (configure.OnChange is null)
                    Invalid($"{at}/recipe", $"The recipe '{stage.Recipe}' marks no action with onChange, and that is the action a stage runs.");
                if (configure.Outcomes.Values.Any(o => o.ApproveVars is not null))
                    Invalid($"{at}/recipe", $"The recipe '{stage.Recipe}' has an outcome a person approves, and a pipeline has one approval, before anything runs.");
                if (ConfigureInputs.AddressOf(guest) is null)
                    Invalid($"{at}/recipe", $"Stage '{stage.Name}' runs on the guest, which needs access.address, or a static or reserved address.");
                foreach (var field in new[] { "user", "hostKey" })
                {
                    if (guest.Json["access"]?[field] is null)
                        Invalid($"{at}/recipe", $"Stage '{stage.Name}' runs on the guest, which needs access.{field}.");
                }

                if (guest.SetupRecipe is null && ConfigureInputs.Reference(guest, "sshKey") is null)
                    Invalid($"{at}/recipe", $"Stage '{stage.Name}' runs on the guest, which needs access.sshKey, or a setup recipe so that Octopushy gives it a key of its own.");

                foreach (var name in configure.Secrets.Where(n => !stage.Secrets.ContainsKey(n) && !stage.SecretFiles.ContainsKey(n)))
                {
                    Invalid(at, $"The recipe '{stage.Recipe}' needs secrets.{name} or secretFiles.{name}.");
                }

                var names = stage.Secrets.Keys.Concat(stage.SecretFiles.Keys).ToList();
                foreach (var name in stage.Secrets.Keys.Intersect(stage.SecretFiles.Keys).Concat(names.Intersect(stage.Inputs.Select(p => p.Key))))
                {
                    Invalid(at, $"{name} is set more than once in stage '{stage.Name}'.");
                }

                foreach (var (name, reference) in stage.Secrets.Concat(stage.SecretFiles))
                {
                    if (connectionKeys.Contains(reference.Key))
                        Invalid(at, $"{name} cannot use a Proxmox API token.");
                }
            }
            else if (recipe.Operation != "stage")
            {
                Invalid($"{at}/recipe", $"The recipe '{stage.Recipe}' cannot be a stage: it needs operation: configure or operation: stage.");
                continue;
            }
            else if (stage.Secrets.Count + stage.SecretFiles.Count > 0)
            {
                Invalid(at, $"Stage '{stage.Name}' is OpenTofu, which gets no secrets yet.");
            }

            if (_inputSchemas.GetValueOrDefault(recipe.Id) is { } schema)
            {
                foreach (var error in SchemaCatalog.Evaluate(schema, stage.Inputs))
                {
                    Invalid(at, $"{Describe(error.Pointer)}{error.Message} (stage {stage.Name}, recipe {recipe.Id})");
                }
            }
        }
    }

    private static bool IntegerBetween(JsonNode? node, int minimum, int maximum) =>
        node is JsonValue value && value.TryGetValue<int>(out var number) && number >= minimum && number <= maximum;

    private (JsonObject? Json, YamlDocument? Document) ReadDefinition(string file, DefinitionKind kind, string? address)
    {
        YamlDocument document;
        try
        {
            document = YamlDocumentReader.Read(File.ReadAllText(Path.Combine(_root, file)));
        }
        catch (YamlReadException e)
        {
            Error(file, e.Line, $"Invalid YAML: {e.Message}", address);
            return (null, null);
        }

        _documents[file] = document;
        if (document.Root is not JsonObject json)
        {
            Error(file, 1, "The file must hold a YAML mapping.", address);
            return (null, null);
        }

        var errors = SchemaCatalog.Instance.Validate(kind, json);
        foreach (var error in errors)
        {
            Error(file, document.LineOf(error.Pointer), Describe(error.Pointer) + error.Message, address);
        }

        return errors.Count == 0 ? (json, document) : (null, null);
    }

    private readonly Dictionary<string, YamlDocument> _documents = new(StringComparer.Ordinal);

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
        if (IdPattern().IsMatch(id))
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

    private string? ResolveInside(string baseDirectory, string relative)
    {
        var full = Path.GetFullPath(Path.Combine(baseDirectory, relative));
        if (full != _root && !full.StartsWith(_root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            return null;
        }

        return Relative(full);
    }

    private static string? FileNameFromUrl(string url)
    {
        var name = Uri.TryCreate(url, UriKind.Absolute, out var uri) ? Path.GetFileName(uri.AbsolutePath) : null;
        return string.IsNullOrEmpty(name) ? null : name;
    }

    private static string Describe(string pointer) => pointer.Length == 0 ? "" : $"{pointer.TrimStart('/').Replace('/', '.')}: ";

    private static IEnumerable<string> SubDirectories(string path) =>
        Directory.Exists(path)
            ? Directory.EnumerateDirectories(path)
                .Where(d => !Path.GetFileName(d).StartsWith('.'))
                .Order(StringComparer.Ordinal)
            : [];

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

    [GeneratedRegex("^[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?$")]
    private static partial Regex IdPattern();

    [GeneratedRegex(@"^\s*backend\s+""", RegexOptions.Multiline)]
    private static partial Regex BackendBlock();
}
