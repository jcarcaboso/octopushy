using System.Buffers;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using Octopushy.Server.Options;
using Octopushy.Server.Store;

namespace Octopushy.Server.Agents;

/// <summary>One log line of an attempt, as stored and served.</summary>
public sealed record LogLineRecord(long Sequence, long UnixMs, string Stream, string Text);

/// <summary>
/// Attempt logs, kept as JSON lines outside the database. Lines arrive numbered from the agent,
/// so lines resent after a reconnect are dropped.
/// </summary>
public sealed class AttemptLogStore(IOptions<OctopushyOptions> options)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly string _directory = Path.Combine(options.Value.DataDirectory, "logs");
    private readonly long _maxBytes = options.Value.AgentChannel.MaxLogBytesPerAttempt;

    /// <summary>The stream of a line the server writes into an attempt's log itself, not the agent.</summary>
    public const string ServerStream = "server";
    // One gate per attempt, for as long as anything holds it: a writer, a close or a delete. An entry goes only when its last
    // user leaves and nothing is registered to mask with, so a second gate never runs beside a writer that still holds the first.
    private readonly Dictionary<Guid, LogGate> _gates = new();

    /// <summary>Lines between two entries of a log's index.</summary>
    internal const int Stride = 1000;

    /// <summary>
    /// How many logs' indexes are kept, least recently used first to go. A log of 64 MiB, the default limit, holds at most
    /// 1.4 million lines of 49 bytes, the shortest a line can be: 1,370 entries of 8 bytes, about 11 KiB, or 22 KiB with what
    /// a list keeps spare. 1,024 of them take at most 22 MiB.
    /// </summary>
    internal const int IndexedAttempts = 1024;

    // Kept apart from the gates, which go when nothing holds them: a log is read through once, not once per batch or per view.
    private readonly Dictionary<Guid, LogIndex> _indexes = new();
    private long _uses;

    /// <summary>How many times a log was read through to index it.</summary>
    internal long Scans;

    /// <summary>The bytes reads of lines took from log files.</summary>
    internal long BytesRead;

    /// <summary>The lines reads parsed.</summary>
    internal long LinesParsed;
    private readonly ConcurrentDictionary<Guid, Registration> _secrets = new();

    /// <summary>What a diagnostic becomes when an attempt's secrets could not all be recovered to mask it.</summary>
    public const string Withheld = "[withheld: Octopushy could not recover every secret this attempt holds to mask it, so it does not keep what the attempt reported]";

    /// <summary>
    /// Remembers the secret values sent with an attempt, so its logs and errors are masked here
    /// too, not only by the agent: each whole value, and each line of a multi-line one.
    /// <paramref name="complete"/> is false when some could not be read back.
    /// </summary>
    public void RegisterSecrets(Guid attemptId, IEnumerable<string> values, bool complete = true) =>
        _secrets[attemptId] = new Registration(OutputMasking.Secrets.Of(Masks(values)), complete, Scoped: false);

    /// <summary>
    /// Registers masks read back from the store, for those who hold the attempt now (<see cref="Hold"/>) only: they are
    /// forgotten when the last of them lets go. Read back from a snapshot that may be stale by the time it is registered, they
    /// never outlive their holders, and never replace a registration made meanwhile.
    /// </summary>
    public void RegisterRecovered(Guid attemptId, IEnumerable<string> values, bool complete) =>
        _secrets.TryAdd(attemptId, new Registration(OutputMasking.Secrets.Of(Masks(values)), complete, Scoped: true));

    private static string[] Masks(IEnumerable<string> values) => values
        .SelectMany(v => v.Contains('\n') ? v.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Trim().Length >= 4).Prepend(v) : [v])
        .Where(v => v.Length >= 4).Distinct().OrderByDescending(v => v.Length).ToArray();

    /// <summary>Whether this process knows the attempt's secrets: it sent them, or rebuilt them since it started.</summary>
    public bool Knows(Guid attemptId) => _secrets.ContainsKey(attemptId);

    /// <summary>Whether anything of the attempt is still held in memory: its gate or what it is masked with.</summary>
    internal bool Holds(Guid attemptId)
    {
        lock (_gates)
            return _gates.ContainsKey(attemptId) || _secrets.ContainsKey(attemptId);
    }

    /// <summary>
    /// Holds the attempt's gate entry, and what it is masked with, until disposed: what is registered for those who hold it
    /// is not forgotten in between, and a gate is never replaced while it is held. Taking the gate is not part of it.
    /// </summary>
    public IDisposable Hold(Guid attemptId)
    {
        var gate = Enter(attemptId);
        return new Held(this, attemptId, gate);
    }

    /// <summary>
    /// The values to mask an attempt's reports with, or null when they are not all known: never registered, forgotten when it
    /// was closed, or rebuilt after a restart with some that could not be read back. Each caller then withholds rather than
    /// passes the text through. An attempt that holds no secret is registered with none, and its reports are kept as they are.
    /// </summary>
    private OutputMasking.Secrets? Known(Guid attemptId) =>
        _secrets.TryGetValue(attemptId, out var known) && known.Complete ? known.Secrets : null;

    /// <summary>
    /// Masks a diagnostic, a log line or an error, that is kept and shown. Unless every secret the attempt holds is known,
    /// nothing it printed is kept: a line could hold the one that is missing.
    /// </summary>
    public string MaskDiagnostic(Guid attemptId, string text) =>
        Known(attemptId) is { } secrets ? OutputMasking.Replace(secrets.Values, text) : Withheld;

    /// <summary>
    /// Masks what an attempt returned as JSON data, or withholds it whole: see <see cref="OutputMasking"/>. It is withheld
    /// before it is read at all unless every secret the attempt holds is known, so outputs that might hold the one that is
    /// missing are never kept, and nothing shown is derived from them. <paramref name="withheld"/> says why.
    /// </summary>
    public bool TryMaskOutputs(Guid attemptId, string json, out JsonNode? masked, out string? withheld)
    {
        masked = null;
        if (Known(attemptId) is not { } secrets)
        {
            withheld = Withheld;
            return false;
        }

        return OutputMasking.TryMask(json, secrets, out masked, out withheld);
    }

    /// <summary>
    /// Why a document assembled from this attempt's outputs, after they were masked, may not be kept or shown, or null when it
    /// may: see <see cref="OutputMasking.Check"/>. Unless every secret the attempt holds is known, it may not.
    /// </summary>
    public string? Withholds(Guid attemptId, JsonNode? document) =>
        Known(attemptId) is { } secrets ? OutputMasking.Check(document, secrets) : Withheld;

    /// <summary>
    /// Appends the lines not stored yet; returns the number of lines stored in total. <paramref name="stillKept"/>, when given, is
    /// asked under the attempt's gate first, and nothing is written when it says no. A log keeps at most
    /// <see cref="AgentChannelOptions.MaxLogBytesPerAttempt"/> bytes: the first line past it is replaced, once, by a line of
    /// <see cref="ServerStream"/> that says the rest was dropped, and every line after it is dropped.
    /// </summary>
    public Task<long> AppendAsync(Guid attemptId, long firstSequence, IReadOnlyList<(long UnixMs, string Stream, string Text)> lines, CancellationToken cancellationToken,
        Func<Task<bool>>? stillKept = null) =>
        UnderGateAsync(attemptId, async _ =>
        {
            if (stillKept is not null && !await stillKept())
                return IndexUnderGate(attemptId).Lines;
            var index = IndexUnderGate(attemptId);
            if (index.Full || firstSequence + lines.Count <= index.Lines)
                return index.Lines;
            Directory.CreateDirectory(_directory);
            await using var file = new FileStream(PathFor(attemptId), FileMode.OpenOrCreate, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
            if (file.Length < index.Bytes)
            {
                // Cut or replaced behind the store's back: what the file holds now is the log.
                Forget(attemptId);
                index = IndexUnderGate(attemptId);
                if (index.Full)
                    return index.Lines;
            }

            // Bytes past the last whole line are a line torn by a crash or a failed write. The next line starts where it began.
            if (file.Length > index.Bytes)
                file.SetLength(index.Bytes);
            file.Seek(index.Bytes, SeekOrigin.Begin);

            var (next, bytes, full) = (index.Lines, index.Bytes, false);
            var written = new ArrayBufferWriter<byte>();
            var sizes = new List<long>();
            for (var i = 0; i < lines.Count && !full; i++)
            {
                if (firstSequence + i < next)
                    continue;

                // Masked under the gate: a close waits for this write, so its values are not forgotten halfway through it.
                var (unixMs, stream, text) = lines[i];
                var record = JsonSerializer.Serialize(new LogLineRecord(next, unixMs, stream, MaskDiagnostic(attemptId, text)), Json);
                var size = Encoding.UTF8.GetByteCount(record) + 1;
                if (bytes + size > _maxBytes)
                {
                    record = JsonSerializer.Serialize(new LogLineRecord(next, unixMs, ServerStream,
                        $"Octopushy dropped the rest of this log: it reached {_maxBytes} bytes, the limit Octopushy:AgentChannel:MaxLogBytesPerAttempt. The attempt itself carries on."), Json);
                    size = Encoding.UTF8.GetByteCount(record) + 1;
                    full = true;
                }

                Encoding.UTF8.GetBytes(record, written);
                written.Write("\n"u8);
                sizes.Add(size);
                bytes += size;
                next++;
            }

            try
            {
                await file.WriteAsync(written.WrittenMemory, cancellationToken);
                await file.FlushAsync(cancellationToken);
            }
            catch
            {
                // How much of it reached the file is unknown: the file says, the next time it is needed.
                Forget(attemptId);
                throw;
            }

            // Readers see the lines once they are whole in the file, never before.
            index.Append(sizes, full);
            return next;
        }, cancellationToken);

    /// <summary>
    /// Up to <paramref name="limit"/> lines after the line numbered <paramref name="after"/>, in order: -1 for the first ones. It
    /// starts reading at the index entry before the first line wanted, so a line far into a long log costs no more than one
    /// near its start, and stops at the last line written whole when it started.
    /// </summary>
    public async Task<List<LogLineRecord>> ReadAsync(Guid attemptId, long after, int limit, CancellationToken cancellationToken)
    {
        // Each line's sequence is its number in the file: the store numbers them as it writes them.
        var first = after < 0 ? 0 : after == long.MaxValue ? after : after + 1;
        for (var tries = 0; ; tries++)
        {
            var index = await IndexAsync(attemptId, cancellationToken);
            var (start, end, skip) = (0L, 0L, 0L);
            lock (index)
            {
                if (first < index.Lines)
                    (start, end, skip) = (index.Starts[(int)(first / Stride)], index.Bytes, first % Stride);
            }

            var result = end > start && limit > 0
                ? await ReadLinesAsync(PathFor(attemptId), start, end, new LineCollector(skip, after, limit), cancellationToken)
                : [];
            // Deleted, or its index let go, while it was read: the file read may already be a new log. Read again.
            if (!index.Retired)
                return result;
            if (tries == 2)
                return [];
        }
    }

    /// <summary>How many lines the attempt's log holds, the marker of a full log included.</summary>
    public async Task<long> LineCountAsync(Guid attemptId, CancellationToken cancellationToken)
    {
        var index = await IndexAsync(attemptId, cancellationToken);
        lock (index)
            return index.Lines;
    }

    private async Task<List<LogLineRecord>> ReadLinesAsync(string path, long start, long end, LineCollector lines, CancellationToken cancellationToken)
    {
        FileStream file;
        try
        {
            file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.Asynchronous | FileOptions.SequentialScan);
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
        {
            return lines.Result;
        }

        var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            await using (file)
            {
                file.Seek(start, SeekOrigin.Begin);
                for (var remaining = end - start; remaining > 0;)
                {
                    var read = await file.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), cancellationToken);
                    // Shorter than its index says: cut behind the store's back.
                    if (read == 0)
                        break;
                    Interlocked.Add(ref BytesRead, read);
                    remaining -= read;
                    if (!lines.Feed(buffer.AsSpan(0, read), ref LinesParsed))
                        break;
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        return lines.Result;
    }

    /// <summary>
    /// Forgets what a finished attempt is masked with. It waits for a write of the attempt's lines under way, so a line is
    /// either masked before the values are forgotten or withheld after: never kept with nothing to mask it.
    /// </summary>
    public Task CloseAsync(Guid attemptId, CancellationToken cancellationToken) =>
        UnderGateAsync(attemptId, gate =>
        {
            _secrets.TryRemove(attemptId, out _);
            return Task.FromResult(0L);
        }, cancellationToken);

    /// <summary>Removes a finished attempt's log, for attempts that are not kept, under the same gate as its writes.</summary>
    public Task DeleteAsync(Guid attemptId, CancellationToken cancellationToken) =>
        UnderGateAsync(attemptId, gate =>
        {
            _secrets.TryRemove(attemptId, out _);
            try
            {
                File.Delete(PathFor(attemptId));
            }
            catch (DirectoryNotFoundException)
            {
                // An attempt that never logged anything has no log directory yet.
            }
            Forget(attemptId);
            return Task.FromResult(0L);
        }, cancellationToken);

    /// <summary>The attempt's index: the one kept, or one made from its file under its gate, so no line is written meanwhile.</summary>
    private async Task<LogIndex> IndexAsync(Guid attemptId, CancellationToken cancellationToken)
    {
        lock (_indexes)
        {
            if (_indexes.TryGetValue(attemptId, out var known))
            {
                known.Used = ++_uses;
                return known;
            }
        }

        return await UnderGateAsync(attemptId, _ => Task.FromResult(IndexUnderGate(attemptId)), cancellationToken);
    }

    /// <summary>The attempt's index, made from its file when none is kept. Only under the attempt's gate.</summary>
    private LogIndex IndexUnderGate(Guid attemptId)
    {
        lock (_indexes)
        {
            if (_indexes.TryGetValue(attemptId, out var known))
            {
                known.Used = ++_uses;
                return known;
            }
        }

        var index = Scan(PathFor(attemptId));
        lock (_indexes)
        {
            index.Used = ++_uses;
            _indexes[attemptId] = index;
            if (_indexes.Count > IndexedAttempts)
            {
                var oldest = _indexes.MinBy(e => e.Value.Used);
                _indexes.Remove(oldest.Key);
                oldest.Value.Retire();
            }
        }

        return index;
    }

    /// <summary>Lets the attempt's index go; the next use reads the file again. Only under the attempt's gate.</summary>
    private void Forget(Guid attemptId)
    {
        LogIndex? index;
        lock (_indexes)
            _indexes.Remove(attemptId, out index);
        index?.Retire();
    }

    /// <summary>Reads a log once, without parsing it: where every <see cref="Stride"/>th line starts, how many lines it holds, and whether it is full.</summary>
    private LogIndex Scan(string path)
    {
        Interlocked.Increment(ref Scans);
        var index = new LogIndex();
        FileStream file;
        try
        {
            file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.SequentialScan);
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
        {
            return index;
        }

        var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            using (file)
            {
                var (position, lineStart, last) = (0L, 0L, -1L);
                for (int read; (read = file.Read(buffer, 0, buffer.Length)) > 0; position += read)
                {
                    for (var at = 0; buffer.AsSpan(at, read - at).IndexOf((byte)'\n') is var found and >= 0; at += found + 1)
                    {
                        if (index.Lines % Stride == 0)
                            index.Starts.Add(lineStart);
                        index.Lines++;
                        last = lineStart;
                        lineStart = position + at + found + 1;
                    }
                }

                // A torn last line is not part of the log: the next write starts where it began.
                index.Bytes = lineStart;
                if (last >= 0)
                {
                    var tail = new byte[lineStart - last - 1];
                    file.Seek(last, SeekOrigin.Begin);
                    file.ReadExactly(tail);
                    index.Full = Parse(tail)?.Stream == ServerStream;
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        return index;
    }

    private static LogLineRecord? Parse(ReadOnlySpan<byte> line)
    {
        try
        {
            return JsonSerializer.Deserialize<LogLineRecord>(line, Json);
        }
        catch (JsonException)
        {
            // A line torn by a crash, followed by whole ones before torn lines were cut: it is skipped, and keeps its number.
            return null;
        }
    }

    internal IEnumerable<Guid> StoredAttempts() => Directory.Exists(_directory)
        ? Directory.EnumerateFiles(_directory, "*.jsonl").Select(Path.GetFileNameWithoutExtension)
            .Where(name => Guid.TryParse(name, out _)).Select(name => Guid.Parse(name!))
        : [];

    private LogGate Enter(Guid attemptId)
    {
        lock (_gates)
        {
            if (!_gates.TryGetValue(attemptId, out var gate))
                _gates[attemptId] = gate = new LogGate();
            gate.Users++;
            return gate;
        }
    }

    private void Leave(Guid attemptId, LogGate gate)
    {
        lock (_gates)
        {
            if (--gate.Users > 0)
                return;
            // The last user: what was registered only for those who held it goes, and the gate with it unless an attempt
            // that can still write is registered. The log's index is kept apart, and outlives the gate.
            if (_secrets.TryGetValue(attemptId, out var registration) && registration.Scoped)
                _secrets.TryRemove(attemptId, out _);
            if (!_secrets.ContainsKey(attemptId))
                _gates.Remove(attemptId);
        }
    }

    /// <summary>Runs <paramref name="action"/> holding the attempt's one gate. It never waits for anything but that gate.</summary>
    private async Task<T> UnderGateAsync<T>(Guid attemptId, Func<LogGate, Task<T>> action, CancellationToken cancellationToken)
    {
        var gate = Enter(attemptId);
        try
        {
            await gate.Lock.WaitAsync(cancellationToken);
            try
            {
                return await action(gate);
            }
            finally
            {
                gate.Lock.Release();
            }
        }
        finally
        {
            Leave(attemptId, gate);
        }
    }

    private sealed record Registration(OutputMasking.Secrets Secrets, bool Complete, bool Scoped);

    private sealed class Held(AttemptLogStore store, Guid attemptId, LogGate gate) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                store.Leave(attemptId, gate);
        }
    }

    private sealed class LogGate
    {
        public readonly SemaphoreSlim Lock = new(1, 1);

        /// <summary>Who holds or waits for the gate.</summary>
        public int Users;
    }

    /// <summary>
    /// Where a log's lines start, one entry every <see cref="Stride"/> lines, with its line count, its bytes and whether it is
    /// full. Changed only under the attempt's gate, after the lines are whole in the file; read under its own lock.
    /// </summary>
    private sealed class LogIndex
    {
        /// <summary>The byte offset of line k × <see cref="Stride"/>, at k.</summary>
        public readonly List<long> Starts = [];

        public long Lines;

        /// <summary>The bytes the whole lines take, where the next line starts.</summary>
        public long Bytes;

        /// <summary>Whether the log reached its limit, and says so in its last line.</summary>
        public bool Full;

        /// <summary>Last use, against the store's clock: the least recently used index goes first.</summary>
        public long Used;

        private bool _retired;

        /// <summary>Whether the log was deleted or the index let go: what a read made from it may belong to a newer log.</summary>
        public bool Retired
        {
            get
            {
                lock (this)
                    return _retired;
            }
        }

        public void Retire()
        {
            lock (this)
                _retired = true;
        }

        public void Append(List<long> sizes, bool full)
        {
            lock (this)
            {
                foreach (var size in sizes)
                {
                    if (Lines % Stride == 0)
                        Starts.Add(Bytes);
                    Bytes += size;
                    Lines++;
                }

                Full |= full;
            }
        }
    }

    /// <summary>Collects lines from chunks of a log: skips the first ones unparsed, then parses up to the limit.</summary>
    private sealed class LineCollector(long skip, long after, int limit)
    {
        private readonly ArrayBufferWriter<byte> _partial = new();

        public List<LogLineRecord> Result { get; } = [];

        /// <summary>Takes the next chunk; false once the limit is reached.</summary>
        public bool Feed(ReadOnlySpan<byte> chunk, ref long parsed)
        {
            for (var found = chunk.IndexOf((byte)'\n'); found >= 0; found = chunk.IndexOf((byte)'\n'))
            {
                if (skip > 0)
                    skip--;
                else
                {
                    var line = chunk[..found];
                    if (_partial.WrittenCount > 0)
                    {
                        _partial.Write(line);
                        line = _partial.WrittenSpan;
                    }

                    Interlocked.Increment(ref parsed);
                    if (Parse(line) is { } record && record.Sequence > after)
                        Result.Add(record);
                    if (Result.Count >= limit)
                        return false;
                }

                _partial.ResetWrittenCount();
                chunk = chunk[(found + 1)..];
            }

            // The start of a line that ends in the next chunk; one skipped is not kept.
            if (skip == 0)
                _partial.Write(chunk);
            return true;
        }
    }

    private string PathFor(Guid attemptId) => Path.Combine(_directory, $"{attemptId}.jsonl");
}

/// <summary>
/// Artifacts from attempts, such as saved plans, encrypted at rest because a plan can hold
/// values from the configuration.
/// </summary>
public sealed class ArtifactStore(IOptions<OctopushyOptions> options, Envelope envelope)
{
    private readonly string _directory = Path.Combine(options.Value.DataDirectory, "artifacts");

    public void Delete(Guid attemptId)
    {
        var directory = Path.Combine(_directory, attemptId.ToString());
        if (Directory.Exists(directory))
            Directory.Delete(directory, recursive: true);
    }

    internal IEnumerable<Guid> StoredAttempts() => Directory.Exists(_directory)
        ? Directory.EnumerateDirectories(_directory).Select(Path.GetFileName)
            .Where(name => Guid.TryParse(name, out _)).Select(name => Guid.Parse(name!))
        : [];

    public static string Sha256(ReadOnlySpan<byte> content) => Convert.ToHexStringLower(SHA256.HashData(content));

    public async Task SaveAsync(Guid attemptId, string name, byte[] content, CancellationToken cancellationToken)
    {
        var directory = Path.Combine(_directory, attemptId.ToString());
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, SafeName(name));
        await File.WriteAllBytesAsync(path + ".partial", envelope.SealBlob(content, Context(attemptId, name)), cancellationToken);
        File.Move(path + ".partial", path, overwrite: true);
    }

    public async Task<byte[]> LoadAsync(Guid attemptId, string name, CancellationToken cancellationToken)
    {
        var blob = await File.ReadAllBytesAsync(Path.Combine(_directory, attemptId.ToString(), SafeName(name)), cancellationToken);
        return envelope.OpenBlob(blob, Context(attemptId, name));
    }

    private static string Context(Guid attemptId, string name) => $"artifact\0{attemptId}\0{name}";

    private static string SafeName(string name)
    {
        if (name.Length == 0 || name.Length > 100 || name.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_')) || name.StartsWith('.'))
        {
            throw new ArgumentException($"'{name}' is not a valid artifact name.", nameof(name));
        }

        return name;
    }
}
