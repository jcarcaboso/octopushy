using Microsoft.EntityFrameworkCore;

namespace Octopushy.Server.Data;

public sealed class OctopushyDb(DbContextOptions<OctopushyDb> options) : DbContext(options)
{
    public DbSet<RepositorySettings> RepositorySettings => Set<RepositorySettings>();

    public DbSet<DefinitionRevision> Revisions => Set<DefinitionRevision>();

    public DbSet<Resource> Resources => Set<Resource>();

    public DbSet<GuestDiskUsage> GuestDiskUsages => Set<GuestDiskUsage>();

    public DbSet<Run> Runs => Set<Run>();

    public DbSet<Step> Steps => Set<Step>();

    public DbSet<Attempt> Attempts => Set<Attempt>();

    public DbSet<ArtifactRecord> Artifacts => Set<ArtifactRecord>();

    public DbSet<ManagedWorkerBinding> ManagedWorkers => Set<ManagedWorkerBinding>();

    public DbSet<AgentRecord> Agents => Set<AgentRecord>();

    public DbSet<EnrollmentToken> EnrollmentTokens => Set<EnrollmentToken>();

    public DbSet<StoreEntry> StoreEntries => Set<StoreEntry>();

    public DbSet<StoreVersion> StoreVersions => Set<StoreVersion>();

    public DbSet<SecretResolution> SecretResolutions => Set<SecretResolution>();

    public DbSet<Observation> Observations => Set<Observation>();

    public DbSet<User> Users => Set<User>();

    protected override void OnModelCreating(ModelBuilder model)
    {
        model.Entity<RepositorySettings>(e =>
        {
            e.Property(x => x.Id).ValueGeneratedNever();
        });

        model.Entity<DefinitionRevision>(e =>
        {
            e.Property(x => x.Sequence).UseIdentityAlwaysColumn();
            e.HasIndex(x => x.Sequence).IsUnique();
            e.HasIndex(x => x.Commit);
            e.Property(x => x.Issues).HasColumnType("jsonb");
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

        model.Entity<ManagedWorkerBinding>(e =>
        {
            e.HasKey(x => x.ResourceId);
            e.HasIndex(x => x.Generation).IsUnique();
            e.HasIndex(x => x.AgentId).IsUnique();
            e.HasOne<Resource>().WithOne(x => x.ManagedWorker).HasForeignKey<ManagedWorkerBinding>(x => x.ResourceId).OnDelete(DeleteBehavior.Restrict);
        });
        model.Entity<Resource>().Navigation(x => x.ManagedWorker).AutoInclude();

        model.Entity<Run>(e =>
        {
            e.Property(x => x.Number).UseIdentityAlwaysColumn();
            e.HasIndex(x => x.Number).IsUnique();
            e.HasIndex(x => new { x.ResourceId, x.CreatedAt });
            e.HasIndex(x => x.Status);
            e.Property(x => x.Context).HasColumnType("jsonb");
            e.HasMany(x => x.Steps).WithOne(x => x.Run).HasForeignKey(x => x.RunId).OnDelete(DeleteBehavior.Cascade);
        });

        model.Entity<GuestDiskUsage>(e =>
        {
            e.HasKey(x => x.ResourceId);
            e.HasOne<Resource>().WithOne().HasForeignKey<GuestDiskUsage>(x => x.ResourceId).OnDelete(DeleteBehavior.Cascade);
            e.Property(x => x.TokenHash).HasMaxLength(64);
        });

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

        model.Entity<Observation>(e =>
        {
            e.HasIndex(x => new { x.ResourceId, x.ObservedAt });
            e.Property(x => x.Data).HasColumnType("jsonb");
        });

        model.Entity<User>(e =>
        {
            e.HasIndex(x => x.Username).IsUnique();
        });
    }
}
