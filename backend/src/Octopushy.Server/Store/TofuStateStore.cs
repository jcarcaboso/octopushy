using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Google.Protobuf;
using Microsoft.EntityFrameworkCore;
using Octopushy.Agent.V1;
using Octopushy.Server.Agents;
using Octopushy.Server.Data;
using Octopushy.Server.Orchestration;

namespace Octopushy.Server.Store;

/// <summary>Why an attempt has to wait for a stack; it stays queued and is not a failure.</summary>
public sealed class StateBusyException(string message) : Exception(message);

/// <summary>
/// The OpenTofu state Octopushy keeps for guests without an external backend. State leaves only in
/// the assignment of an attempt on that guest, after the gates that release its credentials. It
/// comes back only from the apply holding the lock, which it gets after its plan was approved, so
/// a plan cannot change state. Calls run under the engine's gate.
/// </summary>
public sealed class TofuStateStore(OctopushyDb db, Envelope envelope, TimeProvider clock, ILogger<TofuStateStore> logger)
{
    /// <summary>What agents advertise, so a run never reaches one that would refuse the backend.</summary>
    public const string AgentCapability = "opentofu-builtin-state";

    public const string ArtifactName = "terraform.tfstate";

    /// <summary>Well below the 64 MiB message limit, which an assignment shares with the plan and files.</summary>
    public const int MaxBytes = 32 * 1024 * 1024;

    public const int RetainedVersions = 50;

    /// <summary>
    /// Prepares an OpenTofu attempt: refuses a state the guest does not have, takes the lock for an
    /// apply, and returns the state to send. An external backend gets none.
    /// </summary>
    public async Task<Artifact?> CheckOutAsync(Attempt attempt, Run run, RunContext context, bool apply, CancellationToken cancellationToken)
    {
        var state = await db.TofuStates.FirstOrDefaultAsync(s => s.ResourceId == run.ResourceId, cancellationToken);
        var head = await HeadAsync(state, cancellationToken);
        var last = await LastApplyAsync(run.ResourceId, cancellationToken);
        if (!context.OpenTofu!.Backend.IsBuiltIn)
        {
            // An uncertain built-in apply can have created what no stored state records.
            if (head?.ResourceCount > 0 || last is { BuiltIn: true, StateKnown: false })
            {
                throw new AssignmentException("This guest's OpenTofu state is in Octopushy, and an external backend would not include it and could plan to recreate what it tracks. "
                    + "Remove provision.state; moving state between backends is not automatic.");
            }
            return null;
        }

        if (state?.LockAttemptId is { } holder && holder != attempt.Id && await IsRunningAsync(holder, cancellationToken))
        {
            throw new StateBusyException("Waiting for the apply that holds this guest's state lock.");
        }

        if (last is { BuiltIn: false, Retired: false })
        {
            throw new AssignmentException("This guest's last apply used an external state backend, and built-in state would not include what it created and could plan to recreate it. "
                + "Restore provision.state; moving state between backends is not automatic.");
        }

        if (apply)
        {
            var planned = await db.Attempts.AsNoTracking()
                .Where(a => a.StepId != null && a.Step!.RunId == run.Id && a.Step.Name == StepNames.Plan && a.Status == AttemptStatuses.Succeeded)
                .OrderByDescending(a => a.Number)
                .Select(a => a.StateVersion)
                .FirstOrDefaultAsync(cancellationToken);
            if (planned != (head?.Version ?? 0))
            {
                throw new AssignmentException("The state changed after this plan was made. Plan again.");
            }

            if (state is null)
            {
                state = new TofuState { ResourceId = run.ResourceId };
                db.TofuStates.Add(state);
            }
            if (state.LockAttemptId is { } stale && stale != attempt.Id)
            {
                logger.LogWarning("Attempt {Attempt} takes the state lock of resource {Resource} from attempt {Stale}, which is no longer running.",
                    attempt.Id, run.ResourceId, stale);
            }
            state.LockAttemptId = attempt.Id;
            state.LockedAt = clock.GetUtcNow();
        }

        attempt.StateVersion = head?.Version ?? 0;
        var content = head is null ? Seed(run.ResourceId) : Open(head);
        return new Artifact
        {
            Name = ArtifactName,
            Content = ByteString.CopyFrom(content),
            Sha256 = ArtifactStore.Sha256(content),
        };
    }

    /// <summary>
    /// Stores the state of the apply holding the lock, replacing its own earlier snapshot, and
    /// returns why it was refused. The caller saves the changes.
    /// </summary>
    public async Task<string?> WriteAsync(Guid attemptId, Guid resourceId, byte[] content, CancellationToken cancellationToken)
    {
        var state = await db.TofuStates.FirstOrDefaultAsync(s => s.ResourceId == resourceId, cancellationToken);
        var head = await HeadAsync(state, cancellationToken);
        var sha256 = ArtifactStore.Sha256(content);
        if (sha256 == (head?.Sha256 ?? ArtifactStore.Sha256(Seed(resourceId))))
        {
            return null;
        }

        if (state?.LockAttemptId != attemptId)
        {
            return "Another apply holds this guest's state lock.";
        }

        if (content.Length > MaxBytes)
        {
            return $"It is larger than {MaxBytes} bytes.";
        }

        if (Parse(content) is not { } parsed)
        {
            return "It is not an OpenTofu state.";
        }

        if (head is not null && parsed.Lineage != head.Lineage)
        {
            return "Its lineage differs from the stored state.";
        }

        if (head is not null && parsed.Serial < head.Serial)
        {
            return "Its serial is older than the stored state.";
        }

        if (head?.AttemptId == attemptId)
        {
            Fill(head, content, sha256, parsed);
            return null;
        }

        var version = NewVersion(resourceId, state.HeadVersion + 1, parsed.Lineage, sha256);
        version.AttemptId = attemptId;
        Fill(version, content, sha256, parsed);
        await AddAsync(state, version, cancellationToken);
        return null;
    }

    /// <summary>Records what an apply left behind and frees its lock; returns why its state was refused.</summary>
    public async Task<string?> SettleAsync(Attempt attempt, Guid resourceId, byte[]? content, CancellationToken cancellationToken)
    {
        var state = await db.TofuStates.FirstOrDefaultAsync(s => s.ResourceId == resourceId, cancellationToken);
        // An apply that started may have changed something, whatever it reports, and only its
        // state would say what. One that never started has nothing to lose.
        var refused = content is not null
            ? await WriteAsync(attempt.Id, resourceId, content, cancellationToken)
            : attempt.Status == AttemptStatuses.Succeeded || attempt.StartedAt is not null ? "The agent returned no state." : null;
        if (state?.LockAttemptId == attempt.Id)
        {
            state.LockAttemptId = null;
            state.LockedAt = null;
        }

        return refused;
    }

    /// <summary>What the API may show about a guest's state: everything but its content.</summary>
    public async Task<(TofuState? State, bool LockHeld, List<TofuStateVersion> Versions)> DescribeAsync(Guid resourceId, CancellationToken cancellationToken)
    {
        var state = await db.TofuStates.AsNoTracking().FirstOrDefaultAsync(s => s.ResourceId == resourceId, cancellationToken);
        var versions = await db.TofuStateVersions.AsNoTracking()
            .Where(v => v.ResourceId == resourceId)
            .OrderByDescending(v => v.Version)
            .Select(v => new TofuStateVersion
            {
                Version = v.Version, Serial = v.Serial, Lineage = v.Lineage, ResourceCount = v.ResourceCount,
                Sha256 = v.Sha256, Size = v.Size, Ciphertext = Array.Empty<byte>(), WrappedKey = Array.Empty<byte>(),
                AttemptId = v.AttemptId, RestoredFrom = v.RestoredFrom, RestoredBy = v.RestoredBy,
                CreatedAt = v.CreatedAt, UpdatedAt = v.UpdatedAt,
            })
            .ToListAsync(cancellationToken);
        var held = state?.LockAttemptId is { } holder && await IsRunningAsync(holder, cancellationToken);
        return (state, held, versions);
    }

    /// <summary>
    /// Makes an earlier version current again, as a new version with the next serial so OpenTofu
    /// and later writes take it as newer. Saved plans of the state it replaces become stale.
    /// </summary>
    public async Task RollBackAsync(Guid resourceId, int version, int expectedHead, string user, CancellationToken cancellationToken)
    {
        var state = await db.TofuStates.FirstOrDefaultAsync(s => s.ResourceId == resourceId, cancellationToken);
        var head = await HeadAsync(state, cancellationToken) ?? throw new RunException("This guest has no built-in state.");
        if (head.Version != expectedHead)
        {
            throw new RunException("The state changed since it was shown. Review its versions again.");
        }

        if (version == head.Version)
        {
            throw new RunException("That version is already the current one.");
        }

        if (await db.Runs.AnyAsync(r => r.ResourceId == resourceId && r.Status != RunStatuses.Succeeded
                && r.Status != RunStatuses.Failed && r.Status != RunStatuses.Cancelled, cancellationToken))
        {
            throw new RunException("Finish or close this guest's runs before restoring its state.");
        }

        var source = await db.TofuStateVersions.FirstOrDefaultAsync(v => v.ResourceId == resourceId && v.Version == version, cancellationToken)
            ?? throw new RunException("There is no such state version.");
        byte[] original;
        try
        {
            original = Open(source);
        }
        catch (AssignmentException e)
        {
            throw new RunException(e.Message);
        }

        var document = JsonNode.Parse(original)!.AsObject();
        document["serial"] = head.Serial + 1;
        var content = JsonSerializer.SerializeToUtf8Bytes(document);
        var sha256 = ArtifactStore.Sha256(content);
        var restored = NewVersion(resourceId, state!.HeadVersion + 1, source.Lineage, sha256);
        restored.RestoredFrom = version;
        restored.RestoredBy = user;
        Fill(restored, content, sha256, Parse(content)!.Value);
        await AddAsync(state, restored, cancellationToken);
        // A crashed apply may still report late; it must not replace what was just restored.
        state.LockAttemptId = null;
        state.LockedAt = null;
        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// What a guest starts from until its first apply writes a state. With no state at all,
    /// OpenTofu's local backend leaves an apply's first resource out of its intermediate writes,
    /// and those are what survives a crash. The lineage is the guest's, so a plan and its apply
    /// agree on it. It is never stored.
    /// </summary>
    private static byte[] Seed(Guid resourceId) => JsonSerializer.SerializeToUtf8Bytes(new
    {
        version = 4,
        serial = 0,
        lineage = resourceId.ToString(),
        outputs = new { },
        resources = Array.Empty<object>(),
    });

    private TofuStateVersion NewVersion(Guid resourceId, int version, string lineage, string sha256)
    {
        var now = clock.GetUtcNow();
        return new TofuStateVersion
        {
            Id = Guid.CreateVersion7(),
            ResourceId = resourceId,
            Version = version,
            Lineage = lineage,
            Sha256 = sha256,
            Ciphertext = [],
            WrappedKey = [],
            CreatedAt = now,
            UpdatedAt = now,
        };
    }

    private void Fill(TofuStateVersion version, byte[] content, string sha256, (long Serial, string Lineage, int Resources) parsed)
    {
        (version.Ciphertext, version.WrappedKey) = envelope.Seal(content, Context(version));
        version.Sha256 = sha256;
        version.Size = content.Length;
        version.Serial = parsed.Serial;
        version.ResourceCount = parsed.Resources;
        version.UpdatedAt = clock.GetUtcNow();
    }

    private async Task AddAsync(TofuState state, TofuStateVersion version, CancellationToken cancellationToken)
    {
        db.TofuStateVersions.Add(version);
        state.HeadVersion = version.Version;
        await db.TofuStateVersions
            .Where(v => v.ResourceId == state.ResourceId && v.Version <= version.Version - RetainedVersions)
            .ExecuteDeleteAsync(cancellationToken);
    }

    private byte[] Open(TofuStateVersion version)
    {
        try
        {
            return envelope.Open(version.Ciphertext, version.WrappedKey, Context(version));
        }
        catch (CryptographicException)
        {
            // One unreadable state must fail its own guest, not stop dispatch for all of them.
            throw new AssignmentException($"Version {version.Version} of this guest's OpenTofu state cannot be decrypted. Restore an earlier version, or the original encryption key.");
        }
    }

    private static string Context(TofuStateVersion version) => $"tofu-state\0{version.ResourceId}\0{version.Id}";

    private async Task<TofuStateVersion?> HeadAsync(TofuState? state, CancellationToken cancellationToken) =>
        state is null || state.HeadVersion == 0 ? null
            : await db.TofuStateVersions.SingleAsync(v => v.ResourceId == state.ResourceId && v.Version == state.HeadVersion, cancellationToken);

    private Task<bool> IsRunningAsync(Guid attemptId, CancellationToken cancellationToken) =>
        db.Attempts.AnyAsync(a => a.Id == attemptId && (a.Status == AttemptStatuses.Assigned || a.Status == AttemptStatuses.Running), cancellationToken);

    /// <summary>
    /// The latest apply that ran. A failed or uncertain one counts: it can have created
    /// resources before it stopped, so its backend may hold them. A failed one returned its
    /// state, or it would be uncertain, so what the built-in state knows about it is complete.
    /// </summary>
    private async Task<(bool BuiltIn, bool StateKnown, bool Retired)?> LastApplyAsync(Guid resourceId, CancellationToken cancellationToken)
    {
        var last = await db.Attempts.AsNoTracking()
            .Where(a => a.Executor == "opentofu" && a.Action == "apply" && a.StartedAt != null && a.Step!.Run!.ResourceId == resourceId)
            .OrderByDescending(a => a.StartedAt)
            .Select(a => new { a.Status, a.Step!.Run!.Operation, a.Step.Run.Context })
            .FirstOrDefaultAsync(cancellationToken);
        if (last is null)
        {
            return null;
        }

        return (RunContext.Parse(last.Context).OpenTofu?.Backend.IsBuiltIn == true,
            last.Status is AttemptStatuses.Succeeded or AttemptStatuses.Failed,
            last.Status == AttemptStatuses.Succeeded && last.Operation == Operations.Retire);
    }

    private static (long Serial, string Lineage, int Resources)? Parse(byte[] content)
    {
        try
        {
            using var document = JsonDocument.Parse(content);
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("version", out var version) && version.ValueKind == JsonValueKind.Number
                && root.TryGetProperty("serial", out var serial) && serial.TryGetInt64(out var number) && number >= 0
                && root.TryGetProperty("lineage", out var lineage) && lineage.GetString() is { Length: > 0 } text)
            {
                var resources = root.TryGetProperty("resources", out var list) && list.ValueKind == JsonValueKind.Array ? list.GetArrayLength() : 0;
                return (number, text, resources);
            }

            return null;
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or FormatException)
        {
            return null;
        }
    }
}
