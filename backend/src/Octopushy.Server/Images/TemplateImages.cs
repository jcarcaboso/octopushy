using Microsoft.EntityFrameworkCore;
using Octopushy.Definitions;
using Octopushy.Server.Data;

namespace Octopushy.Server.Images;

/// <summary>What a revision's built templates have: the image built for each one's key, if any.</summary>
public static class TemplateImages
{
    /// <summary>The executor and action of a build attempt: see contracts/agent/v1/executors.md, <c>nix</c>.</summary>
    public const string Executor = "nix";

    public const string Action = "build";

    /// <summary>Whether an attempt builds a template's image: it belongs to no run and no installation.</summary>
    public static bool IsBuild(Attempt attempt) => attempt is { Executor: Executor, Action: Action, StepId: null, ResourceId: null };

    /// <summary>The built templates of <paramref name="repository"/> whose image is built, by build key.</summary>
    public static async Task<IReadOnlyDictionary<string, TemplateImage>> BuiltAsync(OctopushyDb db, LabRepository repository, CancellationToken cancellationToken)
    {
        var keys = repository.Templates.Values.Select(t => t.Build?.Key).OfType<string>().Distinct(StringComparer.Ordinal).ToList();
        if (keys.Count == 0)
            return new Dictionary<string, TemplateImage>(StringComparer.Ordinal);
        var built = await db.ImageBuilds.AsNoTracking()
            .Where(b => keys.Contains(b.Key) && b.Status == ImageBuildStatuses.Built)
            .ToListAsync(cancellationToken);
        return built.ToDictionary(b => b.Key, Image, StringComparer.Ordinal);
    }

    /// <summary>The image a built image row gives a provision run.</summary>
    public static TemplateImage Image(ImageBuild build) => TemplateImage.Of(build.Sha256!, build.Size!.Value, build.FileName!);

    /// <summary>The image the guest's template is built as, or null for a template that is not built or not built yet.</summary>
    public static TemplateImage? For(LabRepository repository, GuestDefinition guest, IReadOnlyDictionary<string, TemplateImage> images) =>
        guest.Template is { } id && repository.Templates.GetValueOrDefault(id)?.Build is { } build ? images.GetValueOrDefault(build.Key) : null;

    /// <summary>As <see cref="For(LabRepository, GuestDefinition, IReadOnlyDictionary{string, TemplateImage})"/>, read for one guest.</summary>
    public static async Task<TemplateImage?> ForAsync(OctopushyDb db, LabRepository repository, GuestDefinition guest, CancellationToken cancellationToken)
    {
        if (guest.Template is not { } id || repository.Templates.GetValueOrDefault(id)?.Build is not { } build)
            return null;
        var row = await db.ImageBuilds.AsNoTracking()
            .FirstOrDefaultAsync(b => b.Key == build.Key && b.Status == ImageBuildStatuses.Built, cancellationToken);
        return row is null ? null : Image(row);
    }

    /// <summary>
    /// The built templates a revision needs built: valid, and named by at least one guest whose lifecycle is active. A template
    /// nothing uses is not built.
    /// </summary>
    public static IEnumerable<TemplateDefinition> Wanted(LabRepository repository) =>
        repository.Templates.Values
            .Where(t => t is { Build: not null, Valid: true } && repository.Guests.Any(g => g.Template == t.Id && !g.Retired))
            .OrderBy(t => t.Id, StringComparer.Ordinal);
}
