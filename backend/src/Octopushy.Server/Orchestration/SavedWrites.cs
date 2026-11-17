using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Octopushy.Server.Data;

namespace Octopushy.Server.Orchestration;

/// <summary>
/// Counts the saves that change something, from any context and whoever makes them. The engine leaves a run that only
/// waits alone until this count moves: advancing such a run reads only the database, so it cannot change until a save
/// does. See <see cref="RunEngine.TickAsync"/>.
/// <para>
/// A save is counted once it is done, so a tick that read the count before reading the database never takes old rows
/// for new. A save of only notifications, uptime checks or the webhook's state is not counted: no run reads them.
/// Bulk updates and deletes are not seen here; a run they affect is advanced on the engine's next full pass.
/// </para>
/// </summary>
public sealed class SavedWrites : SaveChangesInterceptor
{
    private static readonly object Pending = new();
    private readonly ConditionalWeakTable<DbContext, object> _saving = [];
    private long _count;

    public long Count => Interlocked.Read(ref _count);

    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        Note(eventData.Context);
        return result;
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        Note(eventData.Context);
        return ValueTask.FromResult(result);
    }

    public override int SavedChanges(SaveChangesCompletedEventData eventData, int result)
    {
        Saved(eventData.Context);
        return result;
    }

    public override ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default)
    {
        Saved(eventData.Context);
        return ValueTask.FromResult(result);
    }

    // A save that failed may still have written part of what it had: counted all the same.
    public override void SaveChangesFailed(DbContextErrorEventData eventData) => Saved(eventData.Context);

    public override Task SaveChangesFailedAsync(DbContextErrorEventData eventData, CancellationToken cancellationToken = default)
    {
        Saved(eventData.Context);
        return Task.CompletedTask;
    }

    private void Note(DbContext? context)
    {
        if (context is not null && context.ChangeTracker.Entries().Any(e => e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted
                && e.Entity is not (Notification or UptimeCheck or WebhookState)))
            _saving.AddOrUpdate(context, Pending);
    }

    private void Saved(DbContext? context)
    {
        if (context is not null && _saving.Remove(context))
            Interlocked.Increment(ref _count);
    }
}
