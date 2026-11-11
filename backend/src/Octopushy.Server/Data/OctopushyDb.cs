using Microsoft.EntityFrameworkCore;

namespace Octopushy.Server.Data;

public sealed class OctopushyDb(DbContextOptions<OctopushyDb> options) : DbContext(options)
{
    public DbSet<RepositorySettings> RepositorySettings => Set<RepositorySettings>();

    public DbSet<DefinitionRevision> Revisions => Set<DefinitionRevision>();

    public DbSet<RepositoryDraft> RepositoryDrafts => Set<RepositoryDraft>();

    public DbSet<ResourceChange> Changes => Set<ResourceChange>();

    public DbSet<GeneratedValue> GeneratedValues => Set<GeneratedValue>();

    public DbSet<RevisionIssues> RevisionIssues => Set<RevisionIssues>();

    public DbSet<InstallationTrust> InstallationTrusts => Set<InstallationTrust>();

    public DbSet<WorkflowReleaseRecord> WorkflowReleases => Set<WorkflowReleaseRecord>();

    public DbSet<Resource> Resources => Set<Resource>();


    public DbSet<GuestConfiguration> GuestConfigurations => Set<GuestConfiguration>();
    public DbSet<GuestScheduleState> GuestSchedules => Set<GuestScheduleState>();
    public DbSet<GuestAccessKey> GuestAccessKeys => Set<GuestAccessKey>();

    public DbSet<GuestHostKey> GuestHostKeys => Set<GuestHostKey>();

    public DbSet<InstallationSnippetKey> InstallationSnippetKeys => Set<InstallationSnippetKey>();

    public DbSet<Run> Runs => Set<Run>();

    public DbSet<Step> Steps => Set<Step>();

    public DbSet<Attempt> Attempts => Set<Attempt>();

    public DbSet<ArtifactRecord> Artifacts => Set<ArtifactRecord>();

    public DbSet<ImageBuild> ImageBuilds => Set<ImageBuild>();


    public DbSet<AgentRecord> Agents => Set<AgentRecord>();

    public DbSet<EnrollmentToken> EnrollmentTokens => Set<EnrollmentToken>();

    public DbSet<StoreEntry> StoreEntries => Set<StoreEntry>();

    public DbSet<StoreVersion> StoreVersions => Set<StoreVersion>();

    public DbSet<SecretResolution> SecretResolutions => Set<SecretResolution>();

    public DbSet<DeletedStoreKey> DeletedStoreKeys => Set<DeletedStoreKey>();

    public DbSet<TofuState> TofuStates => Set<TofuState>();

    public DbSet<TofuStateVersion> TofuStateVersions => Set<TofuStateVersion>();

    public DbSet<Observation> Observations => Set<Observation>();

    public DbSet<User> Users => Set<User>();

    public DbSet<OperatorToken> OperatorTokens => Set<OperatorToken>();

    protected override void OnModelCreating(ModelBuilder model)
    {
        model.Entity<RepositorySettings>(e =>
        {
            e.Property(x => x.Id).ValueGeneratedNever();
        });

        model.Entity<RepositoryDraft>(e =>
        {
            e.Property(x => x.Title).HasMaxLength(120);
            e.HasIndex(x => x.UpdatedAt);
        });

        model.Entity<GeneratedValue>(e =>
        {
            e.ToTable("generated_values");
            e.HasIndex(x => x.ChangeId);
            e.HasIndex(x => new { x.State, x.Key });
        });

        model.Entity<ResourceChange>(e =>
        {
            e.ToTable("changes");
            e.Property(x => x.Message).HasMaxLength(500);
            e.Property(x => x.Edited).HasColumnType("jsonb");
            e.HasIndex(x => x.CheckedAt);
            e.HasIndex(x => new { x.ResourceId, x.CreatedAt });
            e.HasIndex(x => x.Status);
            e.HasIndex(x => x.Commit);
        });

        model.Entity<RevisionIssues>(e =>
        {
            e.ToTable("revision_issues");
            e.HasKey(x => new { x.ResourceId, x.RevisionId });
            e.Property(x => x.Issues).HasColumnType("jsonb");
        });

        model.Entity<DefinitionRevision>(e =>
        {
            e.Property(x => x.Sequence).UseIdentityAlwaysColumn();
            e.HasIndex(x => x.Sequence).IsUnique();
            e.HasIndex(x => new { x.Commit, x.WorkflowsDigest });
            e.Property(x => x.Issues).HasColumnType("jsonb");
            e.Property(x => x.Resolutions).HasColumnType("jsonb");
        });

        model.Entity<WorkflowReleaseRecord>(e =>
        {
            e.ToTable("workflow_releases");
            e.HasKey(x => new { x.Repository, x.WorkflowId, x.Major, x.Minor });
            e.Ignore(x => x.Version);
            e.Ignore(x => x.Tag);
        });

        model.Entity<InstallationTrust>(e =>
        {
            e.HasKey(x => x.Address);
        });

        model.Entity<Resource>(e =>
        {
            // A retired resource keeps its address for history; a new folder with the same
            // name later is a new resource.
            e.HasIndex(x => x.Address).IsUnique().HasFilter("retired_at IS NULL");
            e.HasIndex(x => new { x.BoundInstallation, x.BoundVmid })
                .IsUnique()
                .HasFilter("bound_vmid IS NOT NULL AND retired_at IS NULL");
            e.Property(x => x.Definition).HasColumnType("jsonb");
            e.Property(x => x.Issues).HasColumnType("jsonb");
            e.Property(x => x.Outputs).HasColumnType("jsonb");
        });

        model.Entity<Run>(e =>
        {
            e.Property(x => x.Number).UseIdentityAlwaysColumn();
            e.HasIndex(x => x.Number).IsUnique();
            e.HasIndex(x => new { x.ResourceId, x.CreatedAt });
            e.HasIndex(x => x.Status);
            e.Property(x => x.Context).HasColumnType("jsonb");
            e.HasMany(x => x.Steps).WithOne(x => x.Run).HasForeignKey(x => x.RunId).OnDelete(DeleteBehavior.Cascade);
        });

        model.Entity<GuestConfiguration>(e =>
        {
            e.HasKey(x => x.ResourceId);
            e.HasOne<Resource>().WithOne().HasForeignKey<GuestConfiguration>(x => x.ResourceId).OnDelete(DeleteBehavior.Cascade);
            e.Property(x => x.Snapshot).HasColumnType("jsonb");
            e.Property(x => x.SecretVersions).HasColumnType("jsonb");
        });

        model.Entity<GuestScheduleState>(e =>
        {
            e.HasKey(x => new { x.ResourceId, x.Action, x.Cron });
            e.HasOne<Resource>().WithMany().HasForeignKey(x => x.ResourceId).OnDelete(DeleteBehavior.Cascade);
        });

        model.Entity<GuestAccessKey>(e =>
        {
            e.HasKey(x => new { x.ResourceId, x.Version });
            e.HasOne<Resource>().WithMany().HasForeignKey(x => x.ResourceId).OnDelete(DeleteBehavior.Cascade);
        });

        model.Entity<GuestHostKey>(e =>
        {
            e.HasKey(x => new { x.ResourceId, x.Version });
            e.HasOne<Resource>().WithMany().HasForeignKey(x => x.ResourceId).OnDelete(DeleteBehavior.Cascade);
        });

        model.Entity<InstallationSnippetKey>(e => e.HasKey(x => x.Installation));

        model.Entity<Step>(e =>
        {
            e.HasIndex(x => new { x.RunId, x.Ordinal }).IsUnique();
            e.Property(x => x.Outputs).HasColumnType("jsonb");
            e.HasMany(x => x.Attempts).WithOne(x => x.Step).HasForeignKey(x => x.StepId).OnDelete(DeleteBehavior.Cascade);
        });

        model.Entity<Attempt>(e =>
        {
            e.HasIndex(x => new { x.Status, x.CreatedAt });
            e.HasIndex(x => x.AgentId);
            e.HasIndex(x => new { x.ResourceId, x.CreatedAt }).HasFilter("resource_id IS NOT NULL");
            e.Property(x => x.Outputs).HasColumnType("jsonb");
            e.Property(x => x.Context).HasColumnType("jsonb");
        });

        model.Entity<ArtifactRecord>(e =>
        {
            e.HasIndex(x => new { x.AttemptId, x.Name }).IsUnique();
        });

        model.Entity<ImageBuild>(e =>
        {
            e.ToTable("image_builds");
            e.HasIndex(x => x.Key).IsUnique();
            e.HasIndex(x => x.TemplateId);
            e.HasIndex(x => x.AttemptId);
            e.Property(x => x.Key).HasMaxLength(64);
            e.Property(x => x.Sha256).HasMaxLength(64);
        });

        model.Entity<AgentRecord>(e =>
        {
            e.HasIndex(x => x.CertificateSerial).IsUnique();
        });

        model.Entity<EnrollmentToken>(e =>
        {
            e.HasIndex(x => x.TokenHash).IsUnique();
        });

        model.Entity<StoreEntry>(e =>
        {
            e.HasKey(x => x.Key);
            e.Property(x => x.CurrentVersion).IsConcurrencyToken();
            e.Property(x => x.LatestVersion).IsConcurrencyToken();
        });

        model.Entity<StoreVersion>(e =>
        {
            e.HasKey(x => new { x.Key, x.Version });
            e.Property(x => x.RevokedAt).IsConcurrencyToken();
        });

        model.Entity<SecretResolution>(e =>
        {
            e.HasIndex(x => x.AttemptId);
        });

        model.Entity<DeletedStoreKey>(e => e.HasKey(x => x.Key));

        model.Entity<TofuState>(e =>
        {
            e.HasKey(x => new { x.ResourceId, x.Stage });
            e.Property(x => x.Stage).HasDefaultValue("");
            e.HasOne<Resource>().WithMany().HasForeignKey(x => x.ResourceId).OnDelete(DeleteBehavior.Restrict);
        });

        model.Entity<TofuStateVersion>(e =>
        {
            e.HasIndex(x => new { x.ResourceId, x.Stage, x.Version }).IsUnique();
            e.Property(x => x.Stage).HasDefaultValue("");
            e.Property(x => x.Sha256).HasMaxLength(64);
            e.HasOne<TofuState>().WithMany().HasForeignKey(x => new { x.ResourceId, x.Stage }).OnDelete(DeleteBehavior.Restrict);
        });

        model.Entity<Observation>(e =>
        {
            e.HasIndex(x => new { x.ResourceId, x.ObservedAt });
            e.Property(x => x.Data).HasColumnType("jsonb");
        });

        model.Entity<User>(e =>
        {
            e.HasIndex(x => x.Username).IsUnique();
        });
        model.Entity<OperatorToken>(e =>
        {
            e.Property(x => x.Name).HasMaxLength(80);
            e.Property(x => x.TokenHash).HasMaxLength(64);
            e.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });
    }
}
