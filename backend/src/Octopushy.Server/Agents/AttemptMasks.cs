using Microsoft.EntityFrameworkCore;
using Octopushy.Server.Data;
using Octopushy.Server.Store;

namespace Octopushy.Server.Agents;

/// <summary>
/// Makes sure the controller can mask what an attempt reports, however long ago it went out. The values an attempt was
/// sent are remembered in memory when it is handed out; a restart, or an attempt closed and then reporting late, loses
/// them. Every value is also recorded in <see cref="SecretResolution"/>, so they are read back from there, from the
/// encrypted store, before anything the attempt reports is kept. Where one cannot be read back, its diagnostics are
/// withheld rather than kept unmasked: see <see cref="AttemptLogStore.MaskDiagnostic"/>.
/// </summary>
public sealed class AttemptMasks(IServiceScopeFactory scopes, AttemptLogStore logs, ILogger<AttemptMasks> logger)
{
    /// <summary>
    /// Makes sure the attempt's masks are known, and holds them until the result is disposed: the caller masks under it. Masks
    /// read back from the store are for this caller and any that hold it at the same time, and are forgotten with the gate
    /// when the last of them lets go, whether the attempt still runs or not: what it was is not read, so a close that comes
    /// while they are read cannot leave them behind. Only masks registered when the attempt was handed out last until its close.
    /// </summary>
    public async Task<IDisposable> EnsureAsync(Guid attemptId, CancellationToken cancellationToken)
    {
        var held = logs.Hold(attemptId);
        try
        {
            if (logs.Knows(attemptId))
                return held;
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<OctopushyDb>();
            var store = scope.ServiceProvider.GetRequiredService<VariableStore>();
            var released = await db.SecretResolutions.AsNoTracking().Where(r => r.AttemptId == attemptId)
                .Select(r => new { r.Key, r.Version }).Distinct().ToListAsync(cancellationToken);
            var values = new List<string>();
            var complete = true;
            foreach (var release in released)
            {
                if (await store.OpenForMaskingAsync(release.Key, release.Version, cancellationToken) is { } value)
                    values.Add(value);
                else
                    complete = false;
            }

            if (!complete)
                logger.LogWarning("Attempt {Attempt} reported after its secrets were forgotten, and some could not be read back; its diagnostics are withheld.", attemptId);
            logs.RegisterRecovered(attemptId, values, complete);
            return held;
        }
        catch
        {
            held.Dispose();
            throw;
        }
    }
}
