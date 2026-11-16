using Microsoft.EntityFrameworkCore;
using Octopushy.Server.Data;
using Octopushy.Server.Store;

namespace Octopushy.Server.Orchestration;

/// <summary>
/// Detaching a guest: Octopushy forgets it and leaves its machine as it is. Nothing is sent to Proxmox. The guest's
/// binding, reservation, OpenTofu state and Octopushy's keys for it go; its runs, observations and history stay. The
/// machine keeps running with whatever protection it has, and the installation lists it as a VM Octopushy does not manage.
/// </summary>
public sealed partial class RunEngine
{
    /// <summary>The detach the guest page offers for a guest whose folder was deleted, in place of its removal.</summary>
    public Task DetachGuestAsync(Guid id, string address, string actor, CancellationToken ct) =>
        WithDbAsync(async db =>
        {
            var guest = await db.Resources.FirstOrDefaultAsync(r => r.Id == id && r.Kind == ResourceKinds.Guest, ct)
                ?? throw new RunException("There is no such guest.");
            if (guest.Address != address)
                throw new RunException("The guest's address does not match. Review the guest before detaching it.");
            if (guest.RetiredAt is not null)
                throw new RunException(guest.DetachedAt is not null ? "The guest is already detached." : "The guest is already retired.");
            if (guest.MissingSince is null)
                throw new RunException("Remove the guest's folder from the repository first: use Detach on its page, which does both.");
            await DetachInternalAsync(db, guest, actor, ct);
            await db.SaveChangesAsync(ct);
        }, ct);

    /// <summary>
    /// Detaches <paramref name="guest"/>, recorded as done by <paramref name="by"/>; the caller saves. Refused while one of its
    /// runs may be changing something, as an apply that was sent, or one that is uncertain: what it did has to be known first.
    /// Any other run of the guest or its services is cancelled.
    /// </summary>
    internal async Task DetachInternalAsync(OctopushyDb db, Resource guest, string by, CancellationToken ct)
    {
        // Everything is checked before anything changes: a detach is done whole, or not at all.
        var services = await db.Resources.Where(r => r.Kind == ResourceKinds.Service && r.ParentAddress == guest.Address && r.RetiredAt == null).ToListAsync(ct);
        var owners = services.Select(s => s.Id).Append(guest.Id).ToList();
        var active = await db.Runs.Include(r => r.Steps).ThenInclude(s => s.Attempts).AsSplitQuery()
            .Where(r => owners.Contains(r.ResourceId) && r.Status != RunStatuses.Succeeded && r.Status != RunStatuses.Failed && r.Status != RunStatuses.Cancelled)
            .ToListAsync(ct);
        if (active.FirstOrDefault(r => !CanSupersede(r)) is { } busy)
            throw new RunException(busy.ResourceId == guest.Id
                ? $"Run #{busy.Number} may be changing the guest's machine. Let it finish, or force stop it and close it, before detaching."
                : $"Run #{busy.Number} of one of its services may be changing something. Let it finish, or force stop it and close it, before detaching.");
        foreach (var run in active)
            await CancelInternalAsync(db, run.Id, $"{guest.Address} was detached by {by}.", ct);

        var now = clock.GetUtcNow();
        foreach (var service in services)
            service.RetiredAt = now;

        guest.BoundVmid = null;
        guest.BoundInstallation = null;
        (guest.ReservedInstallation, guest.ReservedNode, guest.ReservedVmid, guest.ReservedAt, guest.ReservationAppliedAt) = (null, null, null, null, null);
        await db.TofuStateVersions.Where(s => s.ResourceId == guest.Id).ExecuteDeleteAsync(ct);
        await db.TofuStates.Where(s => s.ResourceId == guest.Id).ExecuteDeleteAsync(ct);
        await GuestAccessCredentials.RemoveAsync(db, guest.Id, ct);
        await GuestHostCredentials.RemoveAsync(db, guest.Id, ct);
        await ContainerHostKeys.RemoveAsync(db, guest.Id, ct);
        guest.RetiredAt = now;
        guest.DetachedAt = now;
        guest.DetachedBy = by;
        signal.Wake();
    }
}
