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
