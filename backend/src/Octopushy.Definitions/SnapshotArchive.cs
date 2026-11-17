using System.Formats.Tar;

namespace Octopushy.Definitions;

/// <summary>How much of a commit Octopushy extracts as a snapshot. Repository content is hostile, so each is bounded.</summary>
/// <param name="MaxTotalBytes">The most bytes the files may hold together.</param>
/// <param name="MaxFileBytes">The most bytes one file may hold.</param>
/// <param name="MaxEntries">The most entries, files and folders, the archive may hold.</param>
public sealed record SnapshotLimits(long MaxTotalBytes, long MaxFileBytes, int MaxEntries)
{
    /// <summary>Generous: the example lab is 22 files and 24 KB, and no fixture file is over 26 KB.</summary>
    public static readonly SnapshotLimits Default = new(256L * 1024 * 1024, 64L * 1024 * 1024, 50_000);

    /// <summary>The most bytes the tar holding them may take: the files, and up to 2 KiB of headers and padding per entry.</summary>
    public long MaxArchiveBytes => MaxTotalBytes + MaxEntries * 2048L;
}

/// <summary>A snapshot that goes over one of its <see cref="SnapshotLimits"/>; <see cref="Limit"/> names which.</summary>
public sealed class SnapshotLimitException(string limit, string message) : Exception(message)
{
    public const string MaxTotalBytes = nameof(SnapshotLimits.MaxTotalBytes);
    public const string MaxFileBytes = nameof(SnapshotLimits.MaxFileBytes);
    public const string MaxEntries = nameof(SnapshotLimits.MaxEntries);

    /// <summary>One of the constants above.</summary>
    public string Limit { get; } = limit;

    /// <summary>The tar <c>git archive</c> writes is larger than <see cref="SnapshotLimits.MaxArchiveBytes"/>: too many bytes, or entries.</summary>
    public static SnapshotLimitException Archive(long max) =>
        new(MaxTotalBytes, $"The commit's archive is larger than {max} bytes, more than its files and their headers may take within the snapshot limits {MaxTotalBytes} and {MaxEntries}.");

    internal static SnapshotLimitException Total(long max) =>
        new(MaxTotalBytes, $"The commit's files hold more than {max} bytes together, over the snapshot limit {MaxTotalBytes}.");
}

/// <summary>
/// Extracts a commit's files from the tar <c>git archive</c> writes, the way Octopushy reads every snapshot: regular files and
/// folders only. Symbolic links and other special entries are left out, so nothing in a snapshot can point outside it, and the
/// server and the offline validator read the same files.
/// </summary>
public static class SnapshotArchive
{
    /// <summary>
    /// What a mirror's <c>info/attributes</c> holds before <c>git archive</c>. It outranks every <c>.gitattributes</c> in the
    /// repository, so the archive holds the literal reviewed blobs, not commit-message substitutions, checkout conversions or
    /// an export-ignore subset.
    /// </summary>
    public const string LiteralAttributes = "* -export-subst -export-ignore -text -eol -crlf -ident -filter -working-tree-encoding\n";

    /// <summary>
    /// Extracts within <paramref name="limits"/>, <see cref="SnapshotLimits.Default"/> when null. Bytes are counted as they are
    /// read, whatever an entry's header says.
    /// </summary>
    /// <exception cref="SnapshotLimitException">The archive goes over a limit.</exception>
    /// <exception cref="InvalidDataException">An entry names a path outside <paramref name="destination"/>, or the archive is malformed.</exception>
    public static void Extract(Stream tarStream, string destination, SnapshotLimits? limits = null)
    {
        limits ??= SnapshotLimits.Default;
        var root = Path.GetFullPath(destination) + Path.DirectorySeparatorChar;
        var entries = 0;
        var total = 0L;
        var buffer = new byte[81920];
        using var reader = new TarReader(tarStream);
        try
        {
            while (reader.GetNextEntry() is { } entry)
            {
                if (++entries > limits.MaxEntries)
                {
                    throw new SnapshotLimitException(SnapshotLimitException.MaxEntries,
                        $"The commit holds more than {limits.MaxEntries} entries, over the snapshot limit {SnapshotLimitException.MaxEntries}.");
                }

                if (entry.EntryType is TarEntryType.GlobalExtendedAttributes or TarEntryType.ExtendedAttributes)
                {
                    continue;
                }

                var full = Path.GetFullPath(Path.Combine(destination, entry.Name));
                if (!full.StartsWith(root, StringComparison.Ordinal) && full + Path.DirectorySeparatorChar != root)
                {
                    throw new InvalidDataException($"The repository contains a path outside itself: {entry.Name}");
                }

                switch (entry.EntryType)
                {
                    case TarEntryType.Directory:
                        Directory.CreateDirectory(full);
                        break;
                    case TarEntryType.RegularFile or TarEntryType.V7RegularFile:
                        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                        total = Write(entry, full, limits, total, buffer);
                        break;
                }
            }
        }
        catch (EndOfStreamException)
        {
            throw new InvalidDataException("The commit's archive ends in the middle of an entry.");
        }
    }

    /// <summary>Writes one file, counting the bytes it reads against both limits; returns the new total.</summary>
    private static long Write(TarEntry entry, string path, SnapshotLimits limits, long total, byte[] buffer)
    {
        SnapshotLimitException TooLarge() => new(SnapshotLimitException.MaxFileBytes,
            $"The file {entry.Name} is larger than {limits.MaxFileBytes} bytes, over the snapshot limit {SnapshotLimitException.MaxFileBytes}.");

        // A header that says it is too large is refused before anything is read; one that understates is caught by the count.
        if (entry.Length > limits.MaxFileBytes)
            throw TooLarge();
        if (total + entry.Length > limits.MaxTotalBytes)
            throw SnapshotLimitException.Total(limits.MaxTotalBytes);

        // The permission bits the entry gives, as extracting it whole would; never set-id or sticky bits.
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows())
            options.UnixCreateMode = entry.Mode & (UnixFileMode)0b111_111_111;
        using var output = new FileStream(path, options);
        if (entry.DataStream is not { } data)
            return total;
        var written = 0L;
        int read;
        while ((read = data.Read(buffer, 0, buffer.Length)) > 0)
        {
            written += read;
            total += read;
            if (written > limits.MaxFileBytes)
                throw TooLarge();
            if (total > limits.MaxTotalBytes)
                throw SnapshotLimitException.Total(limits.MaxTotalBytes);
            output.Write(buffer, 0, read);
        }

        return total;
    }
}
