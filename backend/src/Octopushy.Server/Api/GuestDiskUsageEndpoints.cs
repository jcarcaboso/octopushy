using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Octopushy.Server.Agents;
using Octopushy.Server.Data;
using Octopushy.Server.Store;

namespace Octopushy.Server.Api;

public sealed record DiskReporterRequest(int IntervalSeconds = 3600);

public sealed record DiskReporterToken(string Token, string ReportPath, int IntervalSeconds);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record DiskUsageReport(long? UsedBytes);

public sealed record GuestDiskUsageDto(
    bool Enabled,
    int IntervalSeconds,
    long? ConfiguredBytes,
    long? UsedBytes,
    DateTimeOffset? ReceivedAt,
    DateTimeOffset? StaleAfter);

public static class GuestDiskUsageEndpoints
{
    public const string RateLimit = "disk-reports";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        NumberHandling = JsonNumberHandling.Strict,
    };

    public static void MapGuestDiskUsage(this RouteGroupBuilder api)
    {
        var group = api.MapGroup("/guests/{id:guid}/disk-usage").WithTags("Inventory");
        group.MapPost("/token", CreateTokenAsync).WithName("createDiskReporterToken");
        group.MapDelete("/token", async Task<NoContent> (Guid id, OctopushyDb db, CancellationToken ct) =>
        {
            await db.GuestDiskUsages.Where(d => d.ResourceId == id)
                .ExecuteUpdateAsync(set => set.SetProperty(d => d.TokenHash, (string?)null), ct);
            return TypedResults.NoContent();
        }).WithName("revokeDiskReporterToken");

        // Browser sessions never authorize a report: only the per-guest token does.
        group.MapPost("", ReceiveAsync)
            .AllowAnonymous()
            .RequireRateLimiting(RateLimit)
            .Accepts<DiskUsageReport>("application/json")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status413PayloadTooLarge)
            .WithName("reportGuestDiskUsage");
    }

    private static async Task<Results<Ok<DiskReporterToken>, NotFound, ProblemHttpResult>> CreateTokenAsync(
        Guid id, DiskReporterRequest request, HttpResponse response, OctopushyDb db, VariableStore store, CancellationToken ct)
    {
        if (request.IntervalSeconds is < 60 or > 604800)
        {
            return TypedResults.Problem("IntervalSeconds must be between 60 and 604800.", statusCode: 400);
        }
        if (!await db.Resources.AnyAsync(r => r.Id == id && r.Kind == ResourceKinds.Guest
                && r.RetiredAt == null && r.MissingSince == null, ct))
        {
            return TypedResults.NotFound();
        }

        var token = await GuestReporterCredentials.EnsureAsync(db, store, id, request.IntervalSeconds, rotate: true, ct);
        response.Headers.CacheControl = "no-store";
        return TypedResults.Ok(new DiskReporterToken(token, $"/api/guests/{id}/disk-usage", request.IntervalSeconds));
    }

    private static async Task<IResult> ReceiveAsync(
        Guid id, HttpRequest request, OctopushyDb db, TimeProvider clock, CancellationToken ct)
    {
        if (!request.IsHttps)
        {
            return Results.Problem("Disk reports require HTTPS.", statusCode: 400);
        }
        var authorization = request.Headers.Authorization.ToString();
        if (authorization.Length != 71 || !authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            || authorization.AsSpan(7).IndexOfAnyExcept("0123456789abcdefABCDEF") >= 0)
        {
            return Results.Unauthorized();
        }
        var hash = EnrollmentService.TokenHash(authorization[7..]);
        var authorized = db.GuestDiskUsages.Where(d => d.ResourceId == id && d.TokenHash == hash
            && db.Resources.Any(r => r.Id == id && r.Kind == ResourceKinds.Guest
                && r.RetiredAt == null && r.MissingSince == null));
        if (!await authorized.AnyAsync(ct))
        {
            return Results.Unauthorized();
        }
        if (!request.HasJsonContentType())
        {
            return Results.Problem("Expected application/json.", statusCode: 415);
        }

        // Bound even chunked bodies, before JSON parsing. Reports contain one integer.
        var body = new byte[1025];
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        int length;
        try
        {
            length = await request.Body.ReadAtLeastAsync(body, body.Length, throwOnEndOfStream: false, timeout.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return Results.StatusCode(StatusCodes.Status408RequestTimeout);
        }
        if (length == body.Length)
        {
            return Results.Problem("Disk reports must not exceed 1024 bytes.", statusCode: 413);
        }
        DiskUsageReport? report;
        try
        {
            report = JsonSerializer.Deserialize<DiskUsageReport>(body.AsSpan(0, length), Json);
        }
        catch (JsonException)
        {
            return Results.Problem("Expected a JSON object with a non-negative integer usedBytes.", statusCode: 400);
        }
        if (report?.UsedBytes is not { } used || used < 0)
        {
            return Results.Problem("usedBytes must be a non-negative integer.", statusCode: 400);
        }
        // Recheck the credential in the update itself: revocation/retirement during
        // body upload must not resurrect a reporter or overwrite its configuration.
        var received = clock.GetUtcNow();
        var updated = await authorized.ExecuteUpdateAsync(set => set
            .SetProperty(d => d.UsedBytes, used)
            .SetProperty(d => d.ReceivedAt, received), ct);
        return updated == 1 ? Results.NoContent() : Results.Unauthorized();
    }

    public static GuestDiskUsageDto View(Resource resource, GuestDiskUsage? usage)
    {
        // ponytail: root filesystem only, mapped to the example recipe's first disk.
        // Add explicit filesystem-to-disk mappings when data disks need reporting.
        var definition = resource.Definition is null ? null : JsonNode.Parse(resource.Definition) as JsonObject;
        long? capacity = null;
        if (definition?["hardware"] is JsonObject hardware
            && hardware["disks"] is JsonArray { Count: > 0 } disks
            && disks[0] is JsonObject disk && disk["size"] is JsonValue size
            && size.TryGetValue<long>(out var gib) && gib is > 0 and <= long.MaxValue / (1024L * 1024 * 1024))
        {
            capacity = gib * 1024L * 1024 * 1024;
        }
        return new GuestDiskUsageDto(
            usage?.TokenHash is not null && resource.RetiredAt is null && resource.MissingSince is null,
            usage?.IntervalSeconds ?? 3600,
            capacity,
            usage?.UsedBytes,
            usage?.ReceivedAt,
            usage?.ReceivedAt?.AddSeconds(2L * usage.IntervalSeconds + 120));
    }
}
