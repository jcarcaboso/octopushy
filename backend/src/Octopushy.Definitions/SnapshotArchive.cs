using System.Formats.Tar;

namespace Octopushy.Definitions;

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

    /// <exception cref="InvalidDataException">An entry names a path outside <paramref name="destination"/>.</exception>
    public static void Extract(Stream tarStream, string destination)
    {
        var root = Path.GetFullPath(destination) + Path.DirectorySeparatorChar;
        using var reader = new TarReader(tarStream);
        while (reader.GetNextEntry() is { } entry)
        {
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
                    entry.ExtractToFile(full, overwrite: false);
                    break;
            }
        }
    }
}
