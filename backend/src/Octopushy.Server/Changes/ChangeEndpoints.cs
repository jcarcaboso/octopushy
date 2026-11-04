using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Octopushy.Server.Data;

namespace Octopushy.Server.Changes;

public static class ChangeEndpoints
{
    public static void MapChanges(this RouteGroupBuilder api)
    {
        api.MapGet("/workflows", async (string? id, OctopushyDb db, WorkflowCatalog catalog, CancellationToken ct) =>
                TypedResults.Ok(await catalog.ListAsync(db, string.IsNullOrWhiteSpace(id) ? null : id.Trim(), ct)))
            .WithTags("Workflows")
            .WithName("listWorkflows");

        var group = api.MapGroup("/changes").WithTags("Changes");

        group.MapGet("", async (Guid? resourceId, int? limit, ChangeService changes, CancellationToken ct) =>
                TypedResults.Ok(await changes.ListAsync(resourceId, limit ?? 20, ct)))
            .WithName("listChanges");

        group.MapGet("/{id:guid}", async Task<Results<Ok<ChangeDto>, NotFound>> (Guid id, ChangeService changes, CancellationToken ct) =>
                await changes.GetAsync(id, ct) is { } change ? TypedResults.Ok(change) : TypedResults.NotFound())
            .WithName("getChange");

        group.MapPost("/{id:guid}/dismiss", async (Guid id, HttpContext http, ChangeService changes, CancellationToken ct) =>
                TypedResults.Ok(await changes.DismissAsync(id, http.User.Identity!.Name!, ct)))
            .WithName("dismissChange");

        group.MapGet("/form/{resourceId:guid}", async Task<Results<Ok<ChangeFormDto>, NotFound>> (Guid resourceId, OctopushyDb db,
                ChangeService changes, CancellationToken ct) =>
                await changes.FormAsync(db, resourceId, ct) is { } form ? TypedResults.Ok(form) : TypedResults.NotFound())
            .WithName("getChangeForm");

        group.MapPost("/preview", async Task<Results<Ok<ChangePreviewDto>, Conflict<ChangeRefusedDto>, ProblemHttpResult>> (
            ChangeRequest request, OctopushyDb db, ChangeService changes, CancellationToken ct) =>
        {
            if (Malformed(request) is { } problem)
                return TypedResults.Problem(statusCode: 400, title: problem);
            try
            {
                return TypedResults.Ok(await changes.PreviewAsync(db, request, ct));
            }
            catch (ChangeRefusal refusal)
            {
                return TypedResults.Conflict(refusal.Dto());
            }
        }).WithName("previewChange");

        group.MapPost("", async Task<Results<Ok<ChangeDto>, Conflict<ChangeRefusedDto>, ProblemHttpResult>> (
            ChangeRequest request, HttpContext http, ChangeService changes, CancellationToken ct) =>
        {
            if (Malformed(request) is { } problem)
                return TypedResults.Problem(statusCode: 400, title: problem);
            var actor = http.User.Identity?.Name;
            if (string.IsNullOrWhiteSpace(actor))
                return TypedResults.Problem(statusCode: 400, title: "An authenticated operator is required.");
            var (change, refused) = await changes.CommitAsync(request, actor, ct);
            return change is not null ? TypedResults.Ok(change) : TypedResults.Conflict(refused!);
        }).WithName("commitChange");
    }

    /// <summary>The workflow wizard: where a guest or service can be deployed, the next free VMID, and the deploy itself.</summary>
    public static void MapDeploy(this RouteGroupBuilder api)
    {
        var group = api.MapGroup("/deploy").WithTags("Deploy");

        group.MapGet("/targets", async (OctopushyDb db, ChangeService changes, CancellationToken ct) =>
                TypedResults.Ok(await changes.TargetsAsync(db, ct)))
            .WithName("getDeployTargets");

        group.MapGet("/vmid", async Task<Results<Ok<NextVmidDto>, NotFound>> (string installation, int? from, OctopushyDb db, ChangeService changes,
                CancellationToken ct) =>
                await changes.NextVmidAsync(db, installation, from, ct) is { } next ? TypedResults.Ok(next) : TypedResults.NotFound())
            .WithName("getNextVmid");

        group.MapPost("/preview", async Task<Results<Ok<ChangePreviewDto>, Conflict<ChangeRefusedDto>, ProblemHttpResult>> (
            DeployRequest request, OctopushyDb db, ChangeService changes, CancellationToken ct) =>
        {
            try
            {
                return TypedResults.Ok(await changes.PreviewDeployAsync(db, request, ct));
            }
            catch (ChangeRefusal refusal)
            {
                return TypedResults.Conflict(refusal.Dto());
            }
        }).WithName("previewDeploy");

        group.MapPost("", async Task<Results<Ok<ChangeDto>, Conflict<ChangeRefusedDto>, ProblemHttpResult>> (
            DeployRequest request, HttpContext http, ChangeService changes, CancellationToken ct) =>
        {
            var actor = http.User.Identity?.Name;
            if (string.IsNullOrWhiteSpace(actor))
                return TypedResults.Problem(statusCode: 400, title: "An authenticated operator is required.");
            var (change, refused) = await changes.DeployAsync(request, actor, ct);
            return change is not null ? TypedResults.Ok(change) : TypedResults.Conflict(refused!);
        }).WithName("deploy");
    }

    private static string? Malformed(ChangeRequest request) =>
        request.ResourceId == Guid.Empty ? "Name the guest or service to change."
        : request.Kind is not (ChangeKinds.Edit or ChangeKinds.Upgrade or ChangeKinds.Remove) ? "A change is an edit, an upgrade or a removal."
        : request.Kind == ChangeKinds.Remove && (request.Workflow is not null || request.Hardware is not null || request.Network is not null || request.Inputs is not null)
            ? "A removal takes no values."
        : null;
}
