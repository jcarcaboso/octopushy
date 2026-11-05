using System.Collections.Concurrent;
using System.Security.Cryptography;
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
    // One gate per attempt, for as long as anything holds it: a writer, a close or a delete. An entry goes only when its last
    // user leaves and nothing is registered to mask with, so a second gate never runs beside a writer that still holds the first.
    private readonly Dictionary<Guid, LogGate> _gates = new();
    private readonly ConcurrentDictionary<Guid, (string[] Values, bool Complete)> _secrets = new();

    /// <summary>What a diagnostic becomes when an attempt's secrets could not all be recovered to mask it.</summary>
    public const string Withheld = "[withheld: Octopushy could not recover every secret this attempt holds to mask it, so it does not keep what the attempt reported]";

    /// <summary>
    /// Remembers the secret values sent with an attempt, so its logs and errors are masked here
    /// too, not only by the agent: each whole value, and each line of a multi-line one.
    /// <paramref name="complete"/> is false when they were rebuilt after a restart and some could not be read back.
    /// </summary>
    public void RegisterSecrets(Guid attemptId, IEnumerable<string> values, bool complete = true) =>
        _secrets[attemptId] = (values
            .SelectMany(v => v.Contains('\n') ? v.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Trim().Length >= 4).Prepend(v) : [v])
            .Where(v => v.Length >= 4).Distinct().OrderByDescending(v => v.Length).ToArray(), complete);

    /// <summary>Whether this process knows the attempt's secrets: it sent them, or rebuilt them since it started.</summary>
    public bool Knows(Guid attemptId) => _secrets.ContainsKey(attemptId);

    /// <summary>
    /// The values to mask an attempt's reports with, or null when they are not all known: never registered, forgotten when it
    /// was closed, or rebuilt after a restart with some that could not be read back. Each caller then withholds rather than
    /// passes the text through. An attempt that holds no secret is registered with none, and its reports are kept as they are.
    /// </summary>
    private string[]? Known(Guid attemptId) =>
        _secrets.TryGetValue(attemptId, out var known) && known.Complete ? known.Values : null;

    private static string Replace(string[] values, string text)
    {
        foreach (var value in values)
        {
            text = text.Replace(value, "***", StringComparison.Ordinal);
        }

        return text;
    }

    /// <summary>
    /// Masks a diagnostic, a log line or an error, that is kept and shown. Unless every secret the attempt holds is known,
    /// nothing it printed is kept: a line could hold the one that is missing.
    /// </summary>
    public string MaskDiagnostic(Guid attemptId, string text) =>
        Known(attemptId) is { } values ? Replace(values, text) : Withheld;

    /// <summary>
    /// Masks what an attempt returned as JSON data, or refuses to: false unless every secret the attempt holds is known, so
    /// outputs that might hold the one that is missing are never kept, and nothing shown is derived from them. The JSON is
    /// parsed before it is masked, so each secret is found as it reads once decoded, never only as it was escaped: in every
    /// string, every property name, and the text of every other value. Throws <see cref="JsonException"/> when it is not JSON.
    /// </summary>
    public bool TryMaskOutputs(Guid attemptId, string json, out JsonNode? masked)
    {
        masked = null;
        if (Known(attemptId) is not { } values)
            return false;
        try
        {
            masked = MaskNode(JsonNode.Parse(json), values);
        }
        catch (ArgumentException e)
        {
            // A property named twice: JSON this server does not keep.
            throw new JsonException(e.Message, e);
        }

        return true;
    }

    private static JsonNode? MaskNode(JsonNode? node, string[] values)
    {
        switch (node)
        {
            case null:
                return null;
            case JsonObject o:
                var result = new JsonObject();
                foreach (var (name, value) in o)
                {
                    // Two names can mask to the same text; each is kept, under a name of its own.
                    var key = Replace(values, name);
                    for (var n = 2; result.ContainsKey(key); n++)
                        key = $"{Replace(values, name)}#{n}";
                    result[key] = MaskNode(value, values);
                }

                return result;
            case JsonArray a:
                return new JsonArray(a.Select(item => MaskNode(item, values)).ToArray());
            case JsonValue v when v.GetValueKind() == JsonValueKind.String:
                return JsonValue.Create(Replace(values, v.GetValue<string>()));
            default:
                // A number or a literal that holds a secret's text becomes the masked text, as a string.
                var text = node.ToJsonString();
                var replaced = Replace(values, text);
                return replaced == text ? node.DeepClone() : JsonValue.Create(replaced);
        }
    }

    /// <summary>Appends the lines not stored yet; returns the number of lines stored in total.</summary>
    public Task<long> AppendAsync(Guid attemptId, long firstSequence, IReadOnlyList<(long UnixMs, string Stream, string Text)> lines, CancellationToken cancellationToken) =>
        UnderGateAsync(attemptId, async gate =>
        {
            var path = PathFor(attemptId);
            var next = gate.Next ?? CountLines(path);
            Directory.CreateDirectory(_directory);
            await using (var writer = new StreamWriter(path, append: true))
            {
                for (var i = 0; i < lines.Count; i++)
                {
                    var sequence = firstSequence + i;
                    if (sequence < next)
                    {
                        continue;
                    }

                    // Masked under the gate: a close waits for this write, so its values are not forgotten halfway through it.
                    var (unixMs, stream, text) = lines[i];
                    await writer.WriteLineAsync(JsonSerializer.Serialize(new LogLineRecord(next, unixMs, stream, MaskDiagnostic(attemptId, text)), Json));
                    next++;
                }
            }

            gate.Next = next;
            return next;
        }, cancellationToken);

    public async Task<List<LogLineRecord>> ReadAsync(Guid attemptId, long after, int limit, CancellationToken cancellationToken)
    {
        var path = PathFor(attemptId);
        var result = new List<LogLineRecord>();
        if (!File.Exists(path))
        {
            return result;
        }

        using var reader = new StreamReader(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite));
        while (await reader.ReadLineAsync(cancellationToken) is { } line && result.Count < limit)
        {
            var record = JsonSerializer.Deserialize<LogLineRecord>(line, Json);
            if (record is not null && record.Sequence > after)
            {
                result.Add(record);
            }
        }

        return result;
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
            File.Delete(PathFor(attemptId));
            gate.Next = 0;
            return Task.FromResult(0L);
        }, cancellationToken);

    /// <summary>Runs <paramref name="action"/> holding the attempt's one gate. It never waits for anything but that gate.</summary>
    private async Task<long> UnderGateAsync(Guid attemptId, Func<LogGate, Task<long>> action, CancellationToken cancellationToken)
    {
        LogGate gate;
        lock (_gates)
        {
            if (!_gates.TryGetValue(attemptId, out gate!))
                _gates[attemptId] = gate = new LogGate();
            gate.Users++;
        }

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
            lock (_gates)
            {
                // Kept while an attempt can still write and be masked; a line after its close counts again from the file.
                if (--gate.Users == 0 && !_secrets.ContainsKey(attemptId))
                    _gates.Remove(attemptId);
            }
        }
    }

    private sealed class LogGate
    {
        public readonly SemaphoreSlim Lock = new(1, 1);

        /// <summary>Who holds or waits for the gate.</summary>
        public int Users;

        /// <summary>The next line's sequence, once read from the file.</summary>
        public long? Next;
    }

    private string PathFor(Guid attemptId) => Path.Combine(_directory, $"{attemptId}.jsonl");

    private static long CountLines(string path) => File.Exists(path) ? File.ReadLines(path).LongCount() : 0;
}

/// <summary>
/// Artifacts from attempts, such as saved plans, encrypted at rest because a plan can hold
/// values from the configuration.
/// </summary>
public sealed class ArtifactStore(IOptions<OctopushyOptions> options, Envelope envelope)
{
    private readonly string _directory = Path.Combine(options.Value.DataDirectory, "artifacts");

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
