namespace Octopushy.Tests;

/// <summary>Builds lab repositories in temporary directories for tests.</summary>
public sealed class TestRepository : IDisposable
{
    public TestRepository()
    {
        Root = Path.Combine(Path.GetTempPath(), "octopushy-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
    }

    public string Root { get; }

    /// <summary>The repository root of Octopushy's own source tree.</summary>
    public static string SourceRoot
    {
        get
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "contracts")))
            {
                directory = directory.Parent;
            }

            return directory?.FullName ?? throw new InvalidOperationException("The source tree was not found.");
        }
    }

    public static string ExampleLab => Path.Combine(SourceRoot, "examples", "lab");

    /// <summary>A minimal valid repository: one lab, one standalone installation, one guest, and a test recipe.</summary>
    public static TestRepository Minimal()
    {
        var repository = new TestRepository();
        repository.Write("octopushy.yaml", """
            schemaVersion: 1
            stateBackends:
              postgres:
                type: pg
                config: { schema_name: octopushy_state }
                workspace: "{stack}"
                env:
                  PG_CONN_STR: { store: opentofu/pg }
            """);
        repository.Write("labs/home/lab.yaml", """
            schemaVersion: 1
            name: Home
            execution:
              agentLabels: [home]
            """);
        repository.Write("labs/home/proxmox/korriban/installation.yaml", """
            schemaVersion: 1
            type: standalone
            connection:
              endpoint: https://10.1.0.10:8006/
              tls: { verify: false }
              readOnlyToken: { store: proxmox/korriban/read-only-token }
              privilegedToken: { store: proxmox/korriban/privileged-token }
            hosts:
              korriban:
                datastores: { images: local, disks: local-lvm }
                bridges: [vmbr0]
                limits: { concurrentOperations: 2 }
            """);
        repository.Write("labs/home/proxmox/korriban/guests/krb-app-01/guest.yaml", """
            schemaVersion: 1
            type: vm
            host: korriban
            vmid: 310
            hardware: { cores: 2, memory: 2048 }
            network: { bridge: vmbr0, mode: static, address: 10.1.0.31/24, gateway: 10.1.0.1 }
            provision:
              recipe: test-vm
              state: { backend: postgres }
            """);
        repository.Write("recipes/test-vm/recipe.yaml", """
            schemaVersion: 1
            operation: provision
            executor: opentofu
            root: ../../modules/opentofu/test-vm
            verify: proxmox
            """);
        repository.Write("modules/opentofu/test-vm/main.tf", """
            variable "guest" {
              type = any
            }

            resource "terraform_data" "guest" {
              input = var.guest
            }
            """);
        return repository;
    }

    /// <summary><see cref="Minimal"/> with no state backend anywhere, so its guest uses the built-in state.</summary>
    public static TestRepository BuiltInState()
    {
        var repository = Minimal();
        repository.Write("octopushy.yaml", "schemaVersion: 1");
        var guest = "labs/home/proxmox/korriban/guests/krb-app-01/guest.yaml";
        repository.Write(guest, File.ReadAllText(Path.Combine(repository.Root, guest)).Replace("  state: { backend: postgres }\n", ""));
        return repository;
    }

    public void Write(string path, string content)
    {
        var full = Path.Combine(Root, path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content.ReplaceLineEndings("\n") + "\n");
    }

    public void Delete(string path)
    {
        var full = Path.Combine(Root, path);
        if (Directory.Exists(full))
        {
            Directory.Delete(full, recursive: true);
        }
        else
        {
            File.Delete(full);
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(Root))
        {
            Directory.Delete(Root, recursive: true);
        }
    }
}
