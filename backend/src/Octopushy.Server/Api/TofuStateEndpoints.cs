using Microsoft.AspNetCore.Http.HttpResults;
using Octopushy.Server.Orchestration;
using Octopushy.Server.Store;

namespace Octopushy.Server.Api;

/// <summary>A stored state's identity, never its content, which holds secrets.</summary>
public sealed record TofuStateVersionDto(int Version, long Serial, string Lineage, int Resources, string Sha256, int Size,
    Guid? AttemptId, int? RestoredFrom, string? RestoredBy, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

public sealed record TofuStateDto(int HeadVersion, bool LockHeld, Guid? LockAttemptId, DateTimeOffset? LockedAt, IReadOnlyList<TofuStateVersionDto> Versions);

public sealed record TofuStateRollback(int Version, int ExpectedHead);

public static class TofuStateEndpoints
{
    public static void MapTofuState(this RouteGroupBuilder api)
    {
        api.MapGet("/guests/{id:guid}/state", async (Guid id, TofuStateStore states, CancellationToken ct) =>
        {
            var (state, held, versions) = await states.DescribeAsync(id, ct);
            return TypedResults.Ok(new TofuStateDto(state?.HeadVersion ?? 0, held, state?.LockAttemptId, state?.LockedAt,
                versions.Select(v => new TofuStateVersionDto(v.Version, v.Serial, v.Lineage, v.ResourceCount, v.Sha256, v.Size,
                    v.AttemptId, v.RestoredFrom, v.RestoredBy, v.CreatedAt, v.UpdatedAt)).ToList()));
        })
        .WithTags("Inventory")
        .WithName("getGuestState");

        api.MapPost("/guests/{id:guid}/state/rollback", async Task<NoContent> (
            Guid id, TofuStateRollback request, HttpContext http, RunEngine engine, CancellationToken ct) =>
        {
            await engine.ExecuteAsync(async (_, services) =>
            {
                await services.GetRequiredService<TofuStateStore>().RollBackAsync(id, request.Version, request.ExpectedHead, http.User.Identity!.Name!, ct);
                return true;
            }, ct);
            return TypedResults.NoContent();
        })
        .WithTags("Inventory")
        .WithName("rollBackGuestState");
    }
}
