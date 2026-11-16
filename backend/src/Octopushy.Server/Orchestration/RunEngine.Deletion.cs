using Microsoft.EntityFrameworkCore;
using Octopushy.Agent.V1;
using Octopushy.Server.Data;
using Octopushy.Server.Store;
using Octopushy.Server.Uptime;

namespace Octopushy.Server.Orchestration;

public sealed partial class RunEngine
{
    public Task<bool> DeleteInstallationAsync(Guid id, string address, CancellationToken ct) =>
        WithDbAsync(async (db, services) =>
        {
            var installation = await db.Resources.AsNoTracking()
                .FirstOrDefaultAsync(r => r.Id == id && r.Kind == ResourceKinds.Installation, ct);
            if (installation is null)
                return false;
            if (installation.Address != address)
                throw new RunException("The installation address does not match. Review the server before deleting it.");
            if (installation.MissingSince is null)
                throw new RunException("Remove the installation's folder from the repository and sync it before force deleting the server.");

            var prefix = address + "/";
            var resources = db.Resources.Where(r => r.Address == address || r.Address.StartsWith(prefix)
                || r.BoundInstallation == address || r.ReservedInstallation == address
                || db.Resources.Any(g => (g.BoundInstallation == address || g.ReservedInstallation == address)
                    && g.Address == r.ParentAddress));
            if (await resources.AnyAsync(r => r.MissingSince == null && r.RetiredAt == null, ct))
                throw new RunException("Some resources still exist in the repository. Remove their definitions and sync before deleting this server.");

            var resourceIds = await resources.Select(r => r.Id).ToListAsync(ct);
            var addresses = await resources.Select(r => r.Address).ToListAsync(ct);
            var runs = db.Runs.Where(r => resourceIds.Contains(r.ResourceId));
            var runIds = await runs.Select(r => r.Id).ToListAsync(ct);
            var attempts = db.Attempts.Where(a => resourceIds.Contains(a.ResourceId ?? Guid.Empty)
                || a.Step != null && runIds.Contains(a.Step.RunId));
            if (await attempts.AnyAsync(a => a.Mutating
                && (a.Status == AttemptStatuses.Assigned || a.Status == AttemptStatuses.Running), ct))
                throw new RunException("A change is still being applied. Force stop that run or wait for its lease to expire before deleting the server.");
            var pending = await attempts.Where(a => a.AgentId != null
                && (a.Status == AttemptStatuses.Assigned || a.Status == AttemptStatuses.Running))
                .Select(a => new { a.Id, a.AgentId }).ToListAsync(ct);
            var attemptIds = await attempts.Select(a => a.Id).ToListAsync(ct);
            var changes = db.Changes.Where(c => resourceIds.Contains(c.ResourceId ?? Guid.Empty)
                || c.Address == address || c.Address.StartsWith(prefix));
            if (await changes.AnyAsync(c => c.Status == ChangeStatuses.Committing || c.Status == ChangeStatuses.Uncertain
                || c.Status == ChangeStatuses.Unknown, ct))
                throw new RunException("A repository change for this server is unsettled. Settle it before deleting the server.");
            var changeIds = await changes.Select(c => c.Id).ToListAsync(ct);
            var internalKeys = resourceIds.SelectMany(r => new[] { GuestAccessCredentials.Key(r), GuestHostCredentials.Key(r), ContainerHostKeys.Key(r) })
                .Append(InstallationSnippetCredentials.Key(address)).ToList();

            return await services.GetRequiredService<UptimeMonitor>().ForgetInstallationAsync(address, async () =>
            {
                await using var transaction = await db.Database.BeginTransactionAsync(ct);
                await db.SecretResolutions.Where(r => attemptIds.Contains(r.AttemptId)).ExecuteDeleteAsync(ct);
                await db.Artifacts.Where(a => attemptIds.Contains(a.AttemptId)).ExecuteDeleteAsync(ct);
                await db.Observations.Where(o => resourceIds.Contains(o.ResourceId)).ExecuteDeleteAsync(ct);
                await db.RevisionIssues.Where(i => resourceIds.Contains(i.ResourceId)).ExecuteDeleteAsync(ct);
                await db.TofuStateVersions.Where(s => resourceIds.Contains(s.ResourceId)).ExecuteDeleteAsync(ct);
                await db.TofuStates.Where(s => resourceIds.Contains(s.ResourceId)).ExecuteDeleteAsync(ct);
                await attempts.ExecuteDeleteAsync(ct);
                await runs.ExecuteDeleteAsync(ct);
                // User variables can be shared with other installations, so they are not owned by this deletion.
                await db.EnrollmentTokens.Where(t => db.GeneratedValues.Any(v => changeIds.Contains(v.ChangeId) && v.TokenId == t.Id)
                    && t.UsedAt == null).ExecuteDeleteAsync(ct);
                await db.GeneratedValues.Where(v => changeIds.Contains(v.ChangeId)).ExecuteDeleteAsync(ct);
                await changes.ExecuteDeleteAsync(ct);
                await db.StoreVersions.Where(v => internalKeys.Contains(v.Key)).ExecuteDeleteAsync(ct);
                await db.StoreEntries.Where(e => internalKeys.Contains(e.Key)).ExecuteDeleteAsync(ct);
                await db.DeletedStoreKeys.Where(e => internalKeys.Contains(e.Key)).ExecuteDeleteAsync(ct);
                await db.InstallationSnippetKeys.Where(k => k.Installation == address).ExecuteDeleteAsync(ct);
                await db.InstallationTrusts.Where(t => t.Address == address).ExecuteDeleteAsync(ct);
                var checkPrefixes = new[] { $"proxmox-api|{prefix}", $"guest-ssh|{prefix}", $"service-http|{prefix}", $"service-tcp|{prefix}" }
                    .Concat(addresses.SelectMany(a => new[] { $"guest-ssh|{a}|", $"service-http|{a}|", $"service-tcp|{a}|" })).ToList();
                await db.UptimeBuckets.Where(b => checkPrefixes.Any(p => b.CheckKey.StartsWith(p))).ExecuteDeleteAsync(ct);
                await db.UptimeChecks.Where(c => checkPrefixes.Any(p => c.Key.StartsWith(p))).ExecuteDeleteAsync(ct);
                await resources.ExecuteDeleteAsync(ct);
                foreach (var attemptId in attemptIds)
                {
                    await logs.DeleteAsync(attemptId, ct);
                    artifacts.Delete(attemptId);
                }
                await transaction.CommitAsync(ct);
                // Log batches are kept outside the engine's gate: one that got in before the commit is removed now, under its
                // log's gate, and one after it finds its attempt gone.
                foreach (var attemptId in attemptIds)
                    await logs.DeleteAsync(attemptId, CancellationToken.None);
                foreach (var attempt in pending)
                    connections.Get(attempt.AgentId!.Value)?.TrySend(new ServerMessage
                    {
                        Cancel = new CancelAttempt { AttemptId = attempt.Id.ToString() },
                    });
                signal.Wake();
                return true;
            }, ct);
        }, ct);
}
