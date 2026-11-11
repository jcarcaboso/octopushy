using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using Octopushy.Server.Options;

namespace Octopushy.Server.Images;

/// <summary>Why an upload was refused, with the status the agent is told.</summary>
public sealed class ImageUploadException(string message, bool full = false) : Exception(message)
{
    /// <summary>The store has no room for it once unreferenced images are gone.</summary>
    public bool Full { get; } = full;
}

/// <summary>
/// The image files of built templates, under <see cref="ImageOptions.Directory"/>: each at <c>&lt;sha256&gt;</c>, plain, not
/// envelope-encrypted. An image holds no secrets by construction (its build gets none), and streaming it through the envelope
/// would mean holding it in memory; its integrity is its SHA-256. An upload is written to <c>.partial/&lt;attempt&gt;</c>,
/// hashed and counted as it arrives, and renamed into place only once both match what the agent declared.
/// </summary>
public sealed partial class ImageStore(IOptions<OctopushyOptions> options, ILogger<ImageStore> logger)
{
    /// <summary>The largest chunk either way, as contracts/agent/v1/agent.proto says.</summary>
    public const int ChunkBytes = 4 * 1024 * 1024;

    /// <summary>Bytes promised to uploads under way, so two at once cannot pass the limit together.</summary>
    private readonly ConcurrentDictionary<Guid, long> _reserved = new();

    public string Directory => options.Value.Images.Resolve(options.Value.DataDirectory);

    private string PartialDirectory => Path.Combine(Directory, ".partial");

    public long MaxBytes => options.Value.Images.MaxBytes;

    public int KeepUnreferenced => options.Value.Images.KeepUnreferenced;

    /// <summary>Where the image with this SHA-256 is kept.</summary>
    public string PathOf(string sha256) => IsSha256(sha256) ? Path.Combine(Directory, sha256) : throw new ArgumentException("Not a SHA-256.", nameof(sha256));

    public bool Has(string sha256) => IsSha256(sha256) && File.Exists(PathOf(sha256));

    /// <summary>The SHA-256 of every image file kept, whatever the database says.</summary>
    public IReadOnlyList<string> Files() =>
        System.IO.Directory.Exists(Directory)
            ? System.IO.Directory.EnumerateFiles(Directory).Select(Path.GetFileName).OfType<string>().Where(IsSha256).ToList()
            : [];

    /// <summary>Bytes promised to uploads under way, except <paramref name="except"/>'s.</summary>
    public long Reserved(Guid except) => _reserved.Where(r => r.Key != except).Sum(r => r.Value);

    public void Reserve(Guid attemptId, long size) => _reserved[attemptId] = size;

    public void Release(Guid attemptId)
    {
        _reserved.TryRemove(attemptId, out _);
        try
        {
            File.Delete(PartialOf(attemptId));
        }
        catch (IOException)
        {
        }
    }

    /// <summary>
    /// Writes an upload's chunks to its partial file, the first already read: every chunk at most <see cref="ChunkBytes"/>, no
    /// more bytes than declared, and the last, which carries the SHA-256, ending the stream. Returns the digest once the bytes
    /// and their count match what was declared; refuses anything else, and the partial file goes.
    /// </summary>
    public async Task<string> ReceiveAsync(Guid attemptId, long size, Agent.V1.ImageChunk first, Func<CancellationToken, Task<Agent.V1.ImageChunk?>> next,
        CancellationToken cancellationToken)
    {
        System.IO.Directory.CreateDirectory(PartialDirectory);
        var path = PartialOf(attemptId);
        try
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long received = 0;
            string? declared;
            await using (var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true))
            {
                var chunk = first;
                while (true)
                {
                    if (chunk.Data.Length > ChunkBytes)
                        throw new ImageUploadException($"A chunk is larger than {ChunkBytes} bytes.");
                    received += chunk.Data.Length;
                    if (received > size)
                        throw new ImageUploadException($"The image is larger than the {size} bytes declared.");
                    hash.AppendData(chunk.Data.Span);
                    await file.WriteAsync(chunk.Data.Memory, cancellationToken);
                    if (!string.IsNullOrEmpty(chunk.Sha256))
                    {
                        declared = chunk.Sha256;
                        if (await next(cancellationToken) is not null)
                            throw new ImageUploadException("Only the last chunk carries the SHA-256.");
                        break;
                    }

                    chunk = await next(cancellationToken) ?? throw new ImageUploadException("The upload ended without the SHA-256 of the image.");
                }

                await file.FlushAsync(cancellationToken);
            }

            if (received != size)
                throw new ImageUploadException($"The image is {received} bytes, and {size} were declared.");
            var digest = Convert.ToHexStringLower(hash.GetHashAndReset());
            if (!string.Equals(digest, declared, StringComparison.OrdinalIgnoreCase))
                throw new ImageUploadException($"The image's SHA-256 is {digest}, and {declared} was declared.");
            return digest;
        }
        catch
        {
            File.Delete(path);
            throw;
        }
    }

    /// <summary>Moves an upload that was received whole into place, under its digest. An image already there is the same bytes.</summary>
    public void Commit(Guid attemptId, string sha256)
    {
        var target = PathOf(sha256);
        var partial = PartialOf(attemptId);
        if (File.Exists(target))
            File.Delete(partial);
        else
            File.Move(partial, target);
        _reserved.TryRemove(attemptId, out _);
    }

    /// <summary>Removes an image's file. The caller has decided nothing needs it.</summary>
    public void Delete(string sha256)
    {
        var path = PathOf(sha256);
        if (!File.Exists(path))
            return;
        File.Delete(path);
        logger.LogInformation("Deleted the image {Sha256} from {Directory}.", sha256, Directory);
    }

    /// <summary>A plain file name, as an image's is: letters, digits, <c>.</c>, <c>_</c> and <c>-</c>, not starting with a dot.</summary>
    public static bool IsPlainFileName(string? name) => name is { Length: > 0 and <= 200 } && PlainFileName().IsMatch(name);

    public static bool IsSha256(string? text) => text is { Length: 64 } && Sha256Pattern().IsMatch(text);

    private string PartialOf(Guid attemptId) => Path.Combine(PartialDirectory, attemptId.ToString("N"));

    [GeneratedRegex("^[A-Za-z0-9_-][A-Za-z0-9._-]*\\z")]
    private static partial Regex PlainFileName();

    [GeneratedRegex("^[0-9a-f]{64}\\z")]
    private static partial Regex Sha256Pattern();
}

/// <summary>An image kept on the server, as retention weighs it.</summary>
/// <param name="Id">Its row.</param>
public sealed record StoredImage(Guid Id, string TemplateId, string Sha256, long Size, DateTimeOffset BuiltAt);

/// <summary>What retention removes, and whether what stays, with an upload of <c>incoming</c> bytes, is within the limit.</summary>
public sealed record RetentionPlan(IReadOnlyList<StoredImage> Delete, long Total, bool Fits);

/// <summary>
/// Which images the server keeps. An image is referenced when a built template of the newest valid revision resolves to it,
/// or an unfinished run's plan or apply needs it; one is never deleted. Of the rest, the newest <c>keepUnreferenced</c> of each
/// template stay, for a rollback, and the oldest of those go first while the total is above <c>maxBytes</c>.
/// </summary>
public static class ImageRetention
{
    public static RetentionPlan Plan(IReadOnlyList<StoredImage> stored, IReadOnlySet<string> referenced, int keepUnreferenced, long maxBytes,
        long incoming = 0)
    {
        var delete = new List<StoredImage>();
        var kept = new List<StoredImage>();
        foreach (var template in stored.Where(i => !referenced.Contains(i.Sha256)).GroupBy(i => i.TemplateId, StringComparer.Ordinal))
        {
            var newestFirst = template.OrderByDescending(i => i.BuiltAt).ThenBy(i => i.Id).ToList();
            kept.AddRange(newestFirst.Take(Math.Max(0, keepUnreferenced)));
            delete.AddRange(newestFirst.Skip(Math.Max(0, keepUnreferenced)));
        }

        // Bytes on disk: two rows of the same image are one file.
        long Total() => stored.Except(delete).DistinctBy(i => i.Sha256).Sum(i => i.Size) + incoming;
        foreach (var oldest in kept.OrderBy(i => i.BuiltAt).ThenBy(i => i.Id))
        {
            if (Total() <= maxBytes)
                break;
            delete.Add(oldest);
        }

        var total = Total();
        return new RetentionPlan(delete, total, total <= maxBytes);
    }
}
