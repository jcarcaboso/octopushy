using Microsoft.EntityFrameworkCore;
using Octopushy.Server.Agents;
using Octopushy.Server.Data;

namespace Octopushy.Server.Orchestration;

/// <summary>
/// Text the server writes from what an attempt reported, such as "It reported pending-reboot: A reboot is due.". Each value
/// in it was masked on its own when the attempt's outputs were kept, but joined with others and with the server's words it
/// can hold a secret none of them held. So the whole text is masked again, with the masks of the attempt the outputs came
/// from, before it is kept as a step's or a run's detail or an error.
/// </summary>
public static class OutputDiagnostics
{
    /// <summary>
    /// <paramref name="text"/>, built from <paramref name="source"/>'s outputs, masked whole with the masks of the attempt
    /// those outputs came from. Withheld when that attempt is not known, or its masks cannot all be read back.
    /// </summary>
    public static async Task<string> MaskAsync(OctopushyDb db, AttemptMasks masks, AttemptLogStore logs, Step source, string text,
        CancellationToken cancellationToken)
    {
        // Tracked, so the attempt whose result is being handled counts with the outputs it has not saved yet.
        var attempts = await db.Attempts.Where(a => a.StepId == source.Id).ToListAsync(cancellationToken);
        if (attempts.Where(a => a.Outputs is not null).MaxBy(a => a.Number) is not { } attempt)
            return AttemptLogStore.Withheld;
        using var held = await masks.EnsureAsync(attempt.Id, cancellationToken);
        return logs.MaskDiagnostic(attempt.Id, text);
    }
}
