namespace Octopushy.Server.Options;

/// <summary>Server settings, bound from the <c>Octopushy</c> configuration section.</summary>
public sealed class OctopushyOptions
{
    public const string Section = "Octopushy";

    /// <summary>Where the server keeps its Git clone, snapshots, logs, artifacts, and keys.</summary>
    public string DataDirectory { get; set; } = "/var/lib/octopushy";

    /// <summary>Port of the REST API, reached through the frontend's web server.</summary>
    public int ApiPort { get; set; } = 8080;

    /// <summary>
    /// Address the REST API listens on, or a host name resolved at start. It has no TLS of its own, so keep it private.
    /// </summary>
    public string ApiBindAddress { get; set; } = "127.0.0.1";

    /// <summary>Trust X-Forwarded-* headers on the API port from <see cref="TrustedProxies"/>.</summary>
    public bool TrustForwardedHeaders { get; set; }

    /// <summary>
    /// Networks (<c>10.1.0.0/24</c>) or addresses the frontend's proxy connects from. Empty: the networks of the
    /// addresses the API listens on, which in the Compose project is the frontend's network alone.
    /// </summary>
    public List<string> TrustedProxies { get; set; } = [];

    /// <summary>Apply database migrations at start; otherwise run <c>octopushy-server migrate</c> first.</summary>
    public bool MigrateOnStart { get; set; }

    /// <summary>Run repository polling and the orchestrator loop; tests turn this off to drive them.</summary>
    public bool RunBackgroundServices { get; set; } = true;

    public AgentChannelOptions AgentChannel { get; set; } = new();

    public StoreOptions Store { get; set; } = new();

    public SessionOptions Sessions { get; set; } = new();

    public ToolOptions Tools { get; set; } = new();

    public SchedulingOptions Scheduling { get; set; } = new();

    /// <summary>Allow repository-declared workflow sources to read server-local paths or file:// URLs. Off by default.</summary>
    public bool AllowLocalWorkflowSources { get; set; }

    public RepositoryEditorOptions RepositoryEditor { get; set; } = new();

    public SnapshotOptions Snapshots { get; set; } = new();

    public ChangeOptions Changes { get; set; } = new();

    public DeployOptions Deploy { get; set; } = new();

    public ImageOptions Images { get; set; } = new();

    public IconOptions Icons { get; set; } = new();

    /// <summary>Finished run history is eligible for deletion after this many days.</summary>
    public int RunRetentionDays { get; set; } = 180;

    /// <summary>At least this many newest finished runs per resource are kept regardless of age.</summary>
    public int RunRetentionKeep { get; set; } = 50;

    /// <summary>
    /// The address people open the web app at, such as <c>https://octopushy.example.org</c>: the base of the links a webhook
    /// sends. Unset, a webhook's events carry no link. Never taken from a request's headers.
    /// </summary>
    public string? PublicUrl { get; set; }

    public NotificationOptions Notifications { get; set; } = new();

    public AgentUpdateOptions AgentUpdates { get; set; } = new();
}

/// <summary>Agent updates from the Agents page. See docs/worker-routing.md.</summary>
public sealed class AgentUpdateOptions
{
    /// <summary>
    /// How long, in minutes, an agent has to connect again on the version its update sets, counted from when the last step
    /// that may restart it was handed out. Its setup pulls an image and restarts a container, which takes minutes. The time a
    /// run of the update waits for approval or is blocked does not count.
    /// </summary>
    public int ComeBackMinutes { get; set; } = 30;
}

/// <summary>Notifications sent out of the app. See docs/notifications.md.</summary>
public sealed class NotificationOptions
{
    /// <summary>
    /// Accept an <c>http://</c> webhook URL, for a receiver on the local network without TLS. Off by default: the events and
    /// their signature then cross the network in the clear.
    /// </summary>
    public bool AllowHttpWebhook { get; set; }
}

/// <summary>The icons the Services page may show by file name. Octopushy ships none. See docs/service-icons.md.</summary>
public sealed class IconOptions
{
    /// <summary>Where they are; by default <c>icons</c> under <see cref="OctopushyOptions.DataDirectory"/>. It may be its own mount point, missing, or empty.</summary>
    public string? Directory { get; set; }

    /// <summary>The directory in use: <see cref="Directory"/>, or <c>icons</c> under the data directory.</summary>
    public string Resolve(string dataDirectory) => Path.GetFullPath(Directory ?? Path.Combine(dataDirectory, "icons"), dataDirectory);
}

/// <summary>The images Octopushy builds for built templates, kept on the server. See docs/built-templates.md.</summary>
public sealed class ImageOptions
{
    /// <summary>Where images are kept; by default <c>images</c> under <see cref="OctopushyOptions.DataDirectory"/>. It may be its own mount point.</summary>
    public string? Directory { get; set; }

    /// <summary>How many images per template that nothing refers to any more are kept, newest first, for a rollback.</summary>
    public int KeepUnreferenced { get; set; } = 1;

    /// <summary>The most the images may take together. Unreferenced images go first; a build that would pass it otherwise fails.</summary>
    public long MaxBytes { get; set; } = 10L * 1024 * 1024 * 1024;

    /// <summary>The directory in use: <see cref="Directory"/>, or <c>images</c> under the data directory.</summary>
    public string Resolve(string dataDirectory) => Path.GetFullPath(Directory ?? Path.Combine(dataDirectory, "images"), dataDirectory);
}

/// <summary>Changes made in the UI.</summary>
public sealed class ChangeOptions
{
    /// <summary>How many changes each sync checks against the branch, the longest unchecked first.</summary>
    public int ReconcileBatch { get; set; } = 100;
}

/// <summary>Deploys from the workflow wizard.</summary>
public sealed class DeployOptions
{
    /// <summary>
    /// How old, in minutes, an installation's last successful observation may be for a deploy to allocate or commit a VMID
    /// there. By default three observation intervals (<see cref="SchedulingOptions.ObserveMinutes"/>), 15 minutes.
    /// </summary>
    public int? ObservationMaxAgeMinutes { get; set; }
}

/// <summary>
/// How much of a commit is read as a snapshot, from the lab repository or a workflows repository. A commit over one is not
/// read: the sync records why, as a fetch error. The defaults are far above any lab Octopushy was built for.
/// </summary>
public sealed class SnapshotOptions
{
    /// <summary>The most bytes a commit's files may hold together.</summary>
    public long MaxTotalBytes { get; set; } = Octopushy.Definitions.SnapshotLimits.Default.MaxTotalBytes;

    /// <summary>The most bytes one file may hold.</summary>
    public long MaxFileBytes { get; set; } = Octopushy.Definitions.SnapshotLimits.Default.MaxFileBytes;

    /// <summary>The most files and folders a commit may hold.</summary>
    public int MaxEntries { get; set; } = Octopushy.Definitions.SnapshotLimits.Default.MaxEntries;

    public Octopushy.Definitions.SnapshotLimits Limits => new(MaxTotalBytes, MaxFileBytes, MaxEntries);
}

public sealed class RepositoryEditorOptions
{
    /// <summary>Explicitly allow publishing drafts as commits pushed with the configured repository credential.</summary>
    public bool PublishEnabled { get; set; }

    /// <summary>
    /// How long a push that a request started may take, fetches, merges and retries included. Shorter than a Git command's own
    /// five minutes, so a request is not held that long; a sync's fetch keeps the longer limit.
    /// </summary>
    public int PushTimeoutSeconds { get; set; } = 90;
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

    /// <summary>
    /// The most bytes of log, as stored, kept for one attempt. Past it the rest is dropped, and the log says so in one line;
    /// the attempt itself carries on.
    /// </summary>
    public long MaxLogBytesPerAttempt { get; set; } = 64L * 1024 * 1024;

    public int EnrollmentTokenMinutes { get; set; } = 60;

    /// <summary>
    /// How long an enrollment token a deploy generates lives: long enough for the guest to be approved, provisioned and set
    /// up, which can wait on a person. A week at most.
    /// </summary>
    public int GeneratedEnrollmentTokenMinutes { get; set; } = 3 * 24 * 60;

    public int CertificateDays { get; set; } = 30;
}

public sealed class StoreOptions
{
    /// <summary>The key-encryption key: 32 random bytes, created on first start when missing.</summary>
    public string? KeyFile { get; set; }
}

/// <summary>Web sessions. One expires after 12 hours unused, and ends at its absolute lifetime even while in use.</summary>
public sealed class SessionOptions
{
    /// <summary>How long after signing in a session ends, however often it is used.</summary>
    public int AbsoluteLifetimeDays { get; set; } = 7;
}

public sealed class ToolOptions
{
    /// <summary>The pinned image agents run OpenTofu in, unless a workflow's stage names another.</summary>
    public string OpenTofuImage { get; set; } = "ghcr.io/opentofu/opentofu:1.13.1";

    /// <summary>Its tag names the content of agent/ansible; scripts/checks/offline.sh keeps this up to date.</summary>
    public string AnsibleImage { get; set; } = "docker.io/skorcius/octopushy:ansible-2.21.5-55176118cb4a";
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
