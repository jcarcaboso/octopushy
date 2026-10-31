namespace Octopushy.Server.Options;

/// <summary>Server settings, bound from the <c>Octopushy</c> configuration section.</summary>
public sealed class OctopushyOptions
{
    public const string Section = "Octopushy";

    /// <summary>Where the server keeps its Git clone, snapshots, logs, artifacts, and keys.</summary>
    public string DataDirectory { get; set; } = "/var/lib/octopushy";

    /// <summary>Port of the REST API, reached through the frontend's web server.</summary>
    public int ApiPort { get; set; } = 8080;

    /// <summary>Address the REST API listens on. It has no TLS of its own, so keep it private.</summary>
    public string ApiBindAddress { get; set; } = "127.0.0.1";

    /// <summary>Trust X-Forwarded-* headers on the API port, when it is reached only through the frontend.</summary>
    public bool TrustForwardedHeaders { get; set; }

    /// <summary>Apply database migrations at start; otherwise run <c>octopushy-server migrate</c> first.</summary>
    public bool MigrateOnStart { get; set; }

    /// <summary>Run repository polling and the orchestrator loop; tests turn this off to drive them.</summary>
    public bool RunBackgroundServices { get; set; } = true;

    public AgentChannelOptions AgentChannel { get; set; } = new();

    public StoreOptions Store { get; set; } = new();

    public ToolOptions Tools { get; set; } = new();

    public SchedulingOptions Scheduling { get; set; } = new();

    public WorkerDeploymentOptions WorkerDeployment { get; set; } = new();

    public DiskReporterOptions DiskReporter { get; set; } = new();

    public WorkerUpdateOptions WorkerUpdates { get; set; } = new();

    public RepositoryEditorOptions RepositoryEditor { get; set; } = new();
}

/// <summary>The integrated editor. Drafts always work; pushing them to the lab repository is opt-in.</summary>
public sealed class RepositoryEditorOptions
{
    /// <summary>Explicitly allow publishing drafts as commits pushed with the configured repository credential.</summary>
    public bool PublishEnabled { get; set; }
}

/// <summary>One operator-published NixOS closure export. Disabled until image/boot qualification.</summary>
public sealed class WorkerUpdateOptions
{
    public bool Enabled { get; set; }
    public string? ArchiveUrl { get; set; }
    public string? ArchiveSha256 { get; set; }
    public string? SystemPath { get; set; }
    public string? ImageSha256 { get; set; }
    public string? CaCertificatePem { get; set; }
}

/// <summary>Operator-published, tested worker image. Never selected from an untrusted request.</summary>
public sealed class WorkerDeploymentOptions
{
    public string? ImageUrl { get; set; }
    public string? ImageSha256 { get; set; }
    /// <summary>Explicitly enable authenticated Git additions using the configured repository credential.</summary>
    public bool RepositoryWritesEnabled { get; set; }
}

/// <summary>One pinned Linux reporter release, installed on guests after provisioning.</summary>
public sealed class DiskReporterOptions
{
    public bool Enabled { get; set; }

    /// <summary>Explicit capability attestation for one qualified native-v1 worker image, not the downloadable reporter hash.</summary>
    public string? ManagedNixosImageSha256 { get; set; }

    public bool SupportsManagedNixosImage(string? hash) => hash is { Length: 64 }
        && hash.All(char.IsAsciiHexDigit) && string.Equals(hash, ManagedNixosImageSha256, StringComparison.OrdinalIgnoreCase);

    /// <summary>HTTPS web/API origin reachable from the VM, not the execution-agent endpoint.</summary>
    public string PublicUrl { get; set; } = "";

    public string BinaryUrl { get; set; } = "";

    public string BinarySha256 { get; set; } = "";

    public int IntervalSeconds { get; set; } = 3600;

    /// <summary>Optional private CA PEM for the reporting and download endpoints; never a private key.</summary>
    public string? CaCertificatePem { get; set; }

    public void Validate()
    {
        if (!Enabled)
        {
            return;
        }

        if (!HttpsUrl(PublicUrl) || new Uri(PublicUrl).AbsolutePath != "/" || !HttpsUrl(BinaryUrl)
            || BinarySha256.Length != 64 || BinarySha256.AsSpan().IndexOfAnyExcept("0123456789abcdefABCDEF") >= 0
            || ManagedNixosImageSha256 is not null && (ManagedNixosImageSha256.Length != 64 || !ManagedNixosImageSha256.All(char.IsAsciiHexDigit))
            || IntervalSeconds is < 60 or > 604800 || CaCertificatePem?.Length > 16384
            || CaCertificatePem?.Contains("PRIVATE KEY", StringComparison.Ordinal) == true)
        {
            throw new InvalidOperationException(
                "DiskReporter requires an HTTPS PublicUrl origin, an HTTPS BinaryUrl without credentials/query/fragment, "
                + "a 64-character BinarySha256, and IntervalSeconds between 60 and 604800. "
                + "CaCertificatePem may contain only a public CA certificate (at most 16 KiB).");
        }
    }

    private static bool HttpsUrl(string url) => Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && uri.Scheme == "https" && uri.UserInfo.Length == 0 && uri.Query.Length == 0 && uri.Fragment.Length == 0;
}

public sealed class AgentChannelOptions
{
    /// <summary>Port of the agent endpoint: gRPC over TLS, with client certificates for agents.</summary>
    public int Port { get; set; } = 8443;

    public string BindAddress { get; set; } = "0.0.0.0";

    /// <summary>
    /// Names and addresses agents use to reach the server, written into its TLS certificate.
    /// The machine's host name, localhost, and 127.0.0.1 are always included.
    /// </summary>
    public List<string> Hostnames { get; set; } = [];

    /// <summary>The URL shown in enrollment commands; defaults to the first host name.</summary>
    public string? PublicUrl { get; set; }

    public int HeartbeatSeconds { get; set; } = 10;

    public int LeaseSeconds { get; set; } = 60;

    public int EnrollmentTokenMinutes { get; set; } = 60;

    public int CertificateDays { get; set; } = 30;
}

public sealed class StoreOptions
{
    /// <summary>The key-encryption key: 32 random bytes, created on first start when missing.</summary>
    public string? KeyFile { get; set; }
}

public sealed class ToolOptions
{
    /// <summary>The pinned image agents run OpenTofu in, unless a recipe names another.</summary>
    public string OpenTofuImage { get; set; } = "ghcr.io/opentofu/opentofu:1.13.1";
}

public sealed class SchedulingOptions
{
    /// <summary>How often each installation is observed through the Proxmox API.</summary>
    public int ObserveMinutes { get; set; } = 5;

    /// <summary>How long verify-guest waits for the VM to run and report its address.</summary>
    public int VerifyWaitSeconds { get; set; } = 300;

    /// <summary>How many times a read-only attempt whose agent stopped reporting is retried.</summary>
    public int ReadOnlyRetries { get; set; } = 2;
}
