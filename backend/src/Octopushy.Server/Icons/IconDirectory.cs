using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using Octopushy.Server.Api;
using Octopushy.Server.Options;

namespace Octopushy.Server.Icons;

/// <summary>What the database holds about a service's icon, without the picture's bytes.</summary>
public sealed record StoredIcon(Guid ResourceId, string? Name, bool Picture, DateTimeOffset SetAt);

/// <summary>
/// The icons directory, <see cref="IconOptions.Directory"/>: the files the Services page may show by name. Only regular,
/// non-empty files directly in it with a valid name count. The listing is kept for a while, since the inventory asks often.
/// </summary>
public sealed partial class IconDirectory(IOptions<OctopushyOptions> options, TimeProvider clock)
{
    /// <summary>In the order a service's own name is tried.</summary>
    public static readonly IReadOnlyList<string> Extensions = [".svg", ".png", ".webp", ".jpg", ".jpeg"];

    private static readonly TimeSpan Fresh = TimeSpan.FromSeconds(30);

    private readonly Lock _lock = new();
    private (DateTimeOffset At, IReadOnlyList<string> Names, HashSet<string> Set)? _listing;

    public string Directory => Path.TrimEndingDirectorySeparator(options.Value.Icons.Resolve(options.Value.DataDirectory));

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$")]
    private static partial Regex NamePattern();

    /// <summary>The media type a file of this name is served as; null when the name is not one the directory may hold.</summary>
    public static string? ContentTypeOf(string name) => !NamePattern().IsMatch(name) ? null : Path.GetExtension(name) switch
    {
        ".svg" => "image/svg+xml",
        ".png" => "image/png",
        ".webp" => "image/webp",
        ".jpg" or ".jpeg" => "image/jpeg",
        _ => null,
    };

    /// <summary>The file names, sorted.</summary>
    public IReadOnlyList<string> Names() => Listing().Names;

    public bool Has(string name) => Listing().Set.Contains(name);

    /// <summary>Opens a listed file; null when the name is not listed or the file is gone.</summary>
    public (Stream Content, string ContentType)? Open(string name)
    {
        if (ContentTypeOf(name) is not { } type || !Has(name))
            return null;
        var path = Path.GetFullPath(Path.Combine(Directory, name));
        if (Path.GetDirectoryName(path) != Directory)
            return null;
        // The listing may be half a minute old: a file swapped for a link since then is not followed.
        if (new FileInfo(path) is not { Exists: true, LinkTarget: null })
            return null;
        try
        {
            return (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous), type);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// The service's icon: its uploaded picture, then the file chosen for it while it is still there, then the first file
    /// named after the service.
    /// </summary>
    public ServiceIconDto? Resolve(Guid serviceId, string serviceName, StoredIcon? stored)
    {
        if (stored is { Picture: true })
            return new ServiceIconDto($"/api/services/{serviceId}/icon?v={stored.SetAt.ToUnixTimeMilliseconds()}", "picture");
        if (stored?.Name is { } chosen && Has(chosen))
            return new ServiceIconDto($"/api/icons/{chosen}", "chosen");
        return Extensions.Select(e => serviceName + e).FirstOrDefault(Has) is { } named
            ? new ServiceIconDto($"/api/icons/{named}", "name")
            : null;
    }

    private (DateTimeOffset At, IReadOnlyList<string> Names, HashSet<string> Set) Listing()
    {
        lock (_lock)
        {
            var now = clock.GetUtcNow();
            if (_listing is { } listing && now - listing.At < Fresh && now >= listing.At)
                return listing;
            var names = List();
            _listing = (now, names, names.ToHashSet(StringComparer.Ordinal));
            return _listing.Value;
        }
    }

    private List<string> List()
    {
        var directory = new DirectoryInfo(Directory);
        try
        {
            // A link could point out of the directory; an empty length also leaves out pipes and devices.
            return directory.Exists
                ? directory.EnumerateFiles()
                    .Where(f => !f.Attributes.HasFlag(FileAttributes.ReparsePoint) && f.Length > 0 && ContentTypeOf(f.Name) is not null)
                    .Select(f => f.Name)
                    .Order(StringComparer.Ordinal)
                    .ToList()
                : [];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }
}
