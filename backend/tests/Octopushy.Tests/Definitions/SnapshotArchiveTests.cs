using System.Formats.Tar;
using System.Text;
using Octopushy.Definitions;

namespace Octopushy.Tests.Definitions;

public sealed class SnapshotArchiveTests : IDisposable
{
    private readonly TestRepository _destination = new();
    private readonly TestRepository _archives = new();

    public void Dispose()
    {
        _destination.Dispose();
        _archives.Dispose();
    }

    /// <summary>The archive as a file, as the server reads one.</summary>
    private FileStream OnDisk(MemoryStream tar)
    {
        var path = Path.Combine(_archives.Root, Guid.NewGuid().ToString("N") + ".tar");
        File.WriteAllBytes(path, tar.ToArray());
        return File.OpenRead(path);
    }

    private static MemoryStream Tar(params (string Name, int Bytes)[] files) => Tar((byte)0, files);

    private static MemoryStream Tar(byte fill, params (string Name, int Bytes)[] files)
    {
        var tar = new MemoryStream();
        using (var writer = new TarWriter(tar, TarEntryFormat.Pax, leaveOpen: true))
        {
            foreach (var (name, bytes) in files)
                writer.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, name) { DataStream = new MemoryStream(Enumerable.Repeat(fill, bytes).ToArray()) });
        }

        tar.Position = 0;
        return tar;
    }

    private SnapshotLimitException Refused(Stream tar, SnapshotLimits limits) =>
        Assert.Throws<SnapshotLimitException>(() => SnapshotArchive.Extract(tar, _destination.Root, limits));

    [Fact]
    public void Extracts_an_archive_within_its_limits()
    {
        SnapshotArchive.Extract(Tar(("a.yaml", 10), ("dir/b.yaml", 10)), _destination.Root, new SnapshotLimits(20, 10, 2));
        Assert.Equal(10, new FileInfo(Path.Combine(_destination.Root, "dir", "b.yaml")).Length);
    }

    [Fact]
    public void Refuses_many_tiny_entries()
    {
        var error = Refused(Tar(Enumerable.Range(0, 1000).Select(i => ($"f{i}", 0)).ToArray()), new SnapshotLimits(1 << 20, 1 << 20, 100));
        Assert.Equal(SnapshotLimitException.MaxEntries, error.Limit);
        Assert.Contains("more than 100 entries", error.Message);
        Assert.True(Directory.GetFiles(_destination.Root).Length <= 100);
    }

    [Fact]
    public void Refuses_one_huge_entry()
    {
        var error = Refused(Tar(("small", 10), ("huge.iso", 4096)), new SnapshotLimits(1 << 20, 1024, 100));
        Assert.Equal(SnapshotLimitException.MaxFileBytes, error.Limit);
        Assert.Contains("huge.iso", error.Message);
        Assert.Contains("1024 bytes", error.Message);
    }

    [Fact]
    public void Refuses_files_that_are_too_large_together()
    {
        var error = Refused(Tar(("a", 600), ("b", 600)), new SnapshotLimits(1000, 1000, 100));
        Assert.Equal(SnapshotLimitException.MaxTotalBytes, error.Limit);
    }

    [Fact]
    public void Refuses_a_header_that_claims_more_than_the_limit_without_reading_it()
    {
        // The size field says 8 GiB; only 16 bytes follow. Refused on what the header says, before anything is read.
        var tar = Tar(("liar", 16));
        SetSize(tar, 8L * 1024 * 1024 * 1024 - 1);
        using var file = OnDisk(tar);
        var error = Refused(file, new SnapshotLimits(1 << 20, 1 << 20, 100));
        Assert.Equal(SnapshotLimitException.MaxFileBytes, error.Limit);
    }

    [Fact]
    public void Counts_what_it_reads_when_a_header_understates_the_rest()
    {
        // The header says 10 bytes; 2000 follow. What follows its 10 bytes is read as the next header, never as one file past the limit.
        var tar = Tar((byte)'x', ("liar", 2000));
        SetSize(tar, 10);
        using var file = OnDisk(tar);
        Assert.Throws<InvalidDataException>(() => SnapshotArchive.Extract(file, _destination.Root, new SnapshotLimits(1 << 20, 100, 100)));
        Assert.Equal(10, new FileInfo(Path.Combine(_destination.Root, "liar")).Length);
    }

    [Fact]
    public void A_truncated_archive_is_malformed_data()
    {
        var tar = Tar(("cut", 4096));
        tar.SetLength(2048);
        using var file = OnDisk(tar);
        Assert.Throws<InvalidDataException>(() => SnapshotArchive.Extract(file, _destination.Root, SnapshotLimits.Default));
    }

    /// <summary>Rewrites the size field of the last ustar header in <paramref name="tar"/>, with its checksum.</summary>
    private static void SetSize(MemoryStream tar, long size)
    {
        var bytes = tar.GetBuffer();
        // The file's own header follows the PAX extended header and its one block of records.
        var header = 1024;
        while (bytes[header + 156] != (byte)'0')
            header += 512;
        var field = Encoding.ASCII.GetBytes(Convert.ToString(size, 8).PadLeft(11, '0') + "\0");
        field.CopyTo(bytes, header + 124);
        Array.Fill(bytes, (byte)' ', header + 148, 8);
        var sum = 0;
        for (var i = 0; i < 512; i++)
            sum += bytes[header + i];
        Encoding.ASCII.GetBytes(Convert.ToString(sum, 8).PadLeft(6, '0') + "\0 ").CopyTo(bytes, header + 148);
        tar.Position = 0;
    }
}
