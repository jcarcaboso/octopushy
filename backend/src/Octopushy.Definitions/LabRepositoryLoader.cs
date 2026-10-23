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
        foreach (var installation in installations)
        {
            if (privilegedKeys.Contains(installation.Connection.ReadOnlyToken.Key))
                Error(installation.File, LineOf(installation.File, "/connection/readOnlyToken"),
                    "Read-only and privileged Proxmox tokens require distinct store keys across installations.", installation.Address);
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
        }

        var validEntries = entries.Select(e => e with { Valid = IsValid(e, guests, settings) }).ToList();
        return new LabRepository(
            _root,
            _repositoryValid ? settings : null,
            labs,
            installations,
            guests,
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

        if (entry.Kind != ResourceKind.Guest)
        {
            return true;
        }

        var guest = guests.FirstOrDefault(g => g.Address == entry.Address);
        return guest is not null
            && (guest.Template is null || !_invalidTemplates.Contains(guest.Template))
            && !_invalidRecipes.Contains(guest.Recipe);
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
            json["guestFilesystem"]?.GetValue<string>());
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
                    ParseStoreReference(connection["readOnlyToken"]!),
                    ParseStoreReference(connection["privilegedToken"]!),
                    connection["tls"]?["caCertificate"] is JsonObject ca ? ParseStoreReference(ca) : null),
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
            provision["state"]!["backend"]!.GetValue<string>(),
            ParsePolicy(json["policy"]),
            ParseLabels(json["execution"])));
    }

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

        if (settings is not null && !settings.StateBackends.ContainsKey(guest.StateBackend))
        {
            Error(guest.File, LineOf(guest.File, "/provision/state/backend"), $"The state backend '{guest.StateBackend}' is not defined in octopushy.yaml.", guest.Address);
        }
    }

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

        if (kind == DefinitionKind.Installation && json["connection"] is JsonObject connection && connection.ContainsKey("token"))
        {
            Error(file, document.LineOf("/connection/token"),
                "connection.token is no longer supported. Configure distinct readOnlyToken and privilegedToken store references; credentials are not copied automatically.", address);
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
