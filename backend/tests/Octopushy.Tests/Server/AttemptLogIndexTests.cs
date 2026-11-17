using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Octopushy.Server.Agents;
using Octopushy.Server.Options;

namespace Octopushy.Tests.Server;

/// <summary>Reads of an attempt's log seek to the line asked for, through an index of where its lines start.</summary>
public sealed class AttemptLogIndexTests : IDisposable
{
    private readonly TestRepository _data = new();

    public void Dispose() => _data.Dispose();

    private AttemptLogStore Store(Guid attempt, long maxBytes = 64L * 1024 * 1024)
    {
        var store = new AttemptLogStore(Options.Create(new OctopushyOptions
        {
            DataDirectory = _data.Root,
            AgentChannel = new AgentChannelOptions { MaxLogBytesPerAttempt = maxBytes },
        }));
        store.RegisterSecrets(attempt, []);
        return store;
    }

    private string PathOf(Guid attempt) => Path.Combine(_data.Root, "logs", $"{attempt}.jsonl");

    private static List<(long, string, string)> Lines(long first, int count) =>
        Enumerable.Range(0, count).Select(i => (0L, "stdout", $"line {first + i} ünïcode")).ToList();

    private static async Task FillAsync(AttemptLogStore store, Guid attempt, int count, int batch = 1000)
    {
        for (var first = 0; first < count; first += batch)
            await store.AppendAsync(attempt, first, Lines(first, Math.Min(batch, count - first)), CancellationToken.None);
    }

    /// <summary>The read before the index: every line parsed from the first one. Kept as the oracle.</summary>
    private static List<LogLineRecord> FullScan(string path, long after, int limit)
    {
        var result = new List<LogLineRecord>();
        if (!File.Exists(path))
            return result;
        foreach (var line in File.ReadLines(path))
        {
            if (result.Count >= limit)
                break;
            var record = JsonSerializer.Deserialize<LogLineRecord>(line, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            if (record is not null && record.Sequence > after)
                result.Add(record);
        }

        return result;
    }

    [Fact]
    public async Task A_read_far_into_a_long_log_does_not_parse_the_lines_before_it()
    {
        var attempt = Guid.CreateVersion7();
        var store = Store(attempt);
        await FillAsync(store, attempt, 50_000);
        var size = new FileInfo(PathOf(attempt)).Length;
        Interlocked.Exchange(ref store.BytesRead, 0);
        Interlocked.Exchange(ref store.LinesParsed, 0);

        var lines = await store.ReadAsync(attempt, 40_122, 10, CancellationToken.None);

        Assert.Equal(Enumerable.Range(40_123, 10).Select(i => (long)i), lines.Select(l => l.Sequence));
        Assert.Equal("line 40123 ünïcode", lines[0].Text);
        Assert.Equal(10, Interlocked.Read(ref store.LinesParsed));
        // From the index entry at line 40,000: one buffer of 64 KiB, not the 40,000 lines before it.
        Assert.InRange(Interlocked.Read(ref store.BytesRead), 1, 64 * 1024);
        Assert.True(size > 20 * 64 * 1024, $"The log is {size} bytes.");
        Assert.Equal(50_000, await store.LineCountAsync(attempt, CancellationToken.None));
    }

    [Fact]
    public async Task Reads_around_the_index_entries_are_those_of_a_full_scan()
    {
        var attempt = Guid.CreateVersion7();
        var writer = Store(attempt);
        await FillAsync(writer, attempt, 3_500, batch: 333);
        long[] afters = [long.MinValue, -5, -1, 0, 998, 999, 1000, 1001, 1998, 1999, 2000, 2001, 3_498, 3_499, 3_500, 1_000_000, long.MaxValue];
        int[] limits = [-1, 0, 1, 2, 999, 1000, 1001, 2000, 10_000];
        // The store that wrote the log, with the index its appends kept, and one that indexes it from its file, as after a restart.
        foreach (var store in new[] { writer, Store(attempt) })
        {
            foreach (var after in afters)
            foreach (var limit in limits)
            {
                var expected = FullScan(PathOf(attempt), after, limit);
                var actual = await store.ReadAsync(attempt, after, limit, CancellationToken.None);
                Assert.True(expected.SequenceEqual(actual), $"after {after}, limit {limit}: {actual.Count} lines, not {expected.Count}");
            }
        }
    }

    [Fact]
    public async Task A_full_log_counts_and_serves_its_marker_line_as_a_full_scan_does()
    {
        var attempt = Guid.CreateVersion7();
        var store = Store(attempt, maxBytes: 100_000);
        await FillAsync(store, attempt, 5_000, batch: 700);
        var all = FullScan(PathOf(attempt), -1, int.MaxValue);
        Assert.Equal(AttemptLogStore.ServerStream, all[^1].Stream);
        foreach (var reader in new[] { store, Store(attempt, maxBytes: 100_000) })
        {
            Assert.Equal(all.Count, await reader.LineCountAsync(attempt, CancellationToken.None));
            Assert.Equal(all.TakeLast(3), await reader.ReadAsync(attempt, all.Count - 4, 10, CancellationToken.None));
            // Full: nothing more is written, and the count stands.
            Assert.Equal(all.Count, await reader.AppendAsync(attempt, 5_000, Lines(5_000, 5), CancellationToken.None));
        }
    }

    [Fact]
    public async Task A_deleted_log_is_indexed_again_from_its_first_new_line()
    {
        var attempt = Guid.CreateVersion7();
        var store = Store(attempt);
        await FillAsync(store, attempt, 2_500);
        Assert.Equal(10, (await store.ReadAsync(attempt, 2_000, 10, CancellationToken.None)).Count);

        await store.DeleteAsync(attempt, CancellationToken.None);
        Assert.Equal(0, await store.LineCountAsync(attempt, CancellationToken.None));
        Assert.Empty(await store.ReadAsync(attempt, -1, 10, CancellationToken.None));

        store.RegisterSecrets(attempt, []);
        await store.AppendAsync(attempt, 0, [(0, "stdout", "again 0"), (0, "stdout", "again 1"), (0, "stdout", "again 2")], CancellationToken.None);
        Assert.Equal(3, await store.LineCountAsync(attempt, CancellationToken.None));
        Assert.Equal(["again 0", "again 1", "again 2"], (await store.ReadAsync(attempt, -1, 10, CancellationToken.None)).Select(l => l.Text));
        Assert.Equal(["again 2"], (await store.ReadAsync(attempt, 1, 10, CancellationToken.None)).Select(l => l.Text));
        Assert.Empty(await store.ReadAsync(attempt, 2_000, 10, CancellationToken.None));
        Assert.Equal(FullScan(PathOf(attempt), -1, 10), await store.ReadAsync(attempt, -1, 10, CancellationToken.None));
    }

    [Fact]
    public async Task A_read_while_lines_are_appended_never_returns_a_partial_skipped_or_repeated_line()
    {
        var attempt = Guid.CreateVersion7();
        var store = Store(attempt);
        const int total = 20_000;
        var writer = Task.Run(async () =>
        {
            for (var first = 0; first < total; first += 7)
                await store.AppendAsync(attempt, first, Lines(first, Math.Min(7, total - first)), CancellationToken.None);
        });

        var cursor = -1L;
        var reads = 0;
        while (cursor < total - 1)
        {
            var done = writer.IsCompleted;
            var lines = await store.ReadAsync(attempt, cursor, 500, CancellationToken.None);
            reads++;
            foreach (var line in lines)
            {
                Assert.Equal(cursor + 1, line.Sequence);
                Assert.Equal($"line {line.Sequence} ünïcode", line.Text);
                cursor = line.Sequence;
            }

            Assert.False(done && lines.Count == 0 && cursor < total - 1, $"Stopped at line {cursor}.");
        }

        await writer;
        Assert.True(reads > 1);
        Assert.Equal(total, await store.LineCountAsync(attempt, CancellationToken.None));
    }

    [Fact]
    public async Task A_line_torn_by_a_crash_is_cut_and_the_next_line_takes_its_place()
    {
        var attempt = Guid.CreateVersion7();
        await FillAsync(Store(attempt), attempt, 3);
        await File.AppendAllTextAsync(PathOf(attempt), "{\"sequence\":3,\"unixMs\":0,\"str", Encoding.UTF8);

        var store = Store(attempt);
        Assert.Equal(3, await store.LineCountAsync(attempt, CancellationToken.None));
        Assert.Equal(3, (await store.ReadAsync(attempt, -1, 10, CancellationToken.None)).Count);
        Assert.Equal(4, await store.AppendAsync(attempt, 3, Lines(3, 1), CancellationToken.None));
        Assert.Equal(FullScan(PathOf(attempt), -1, 10), await store.ReadAsync(attempt, -1, 10, CancellationToken.None));
        Assert.Equal([0L, 1, 2, 3], (await store.ReadAsync(attempt, -1, 10, CancellationToken.None)).Select(l => l.Sequence));
    }
}
