using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
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
    private readonly ConcurrentDictionary<Guid, (SemaphoreSlim Gate, long Next)> _state = new();
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

    /// <summary>Masks what an attempt returned as data, such as outputs, with every secret known for it.</summary>
    public string Mask(Guid attemptId, string text)
    {
        if (_secrets.TryGetValue(attemptId, out var known))
        {
            foreach (var value in known.Values)
            {
                text = text.Replace(value, "***", StringComparison.Ordinal);
            }
        }

        return text;
    }

    /// <summary>
    /// Masks a diagnostic, a log line or an error, that is kept and shown. When the attempt's secrets could not all be
    /// recovered, nothing it printed is kept: a line could hold the one that is missing.
    /// </summary>
    public string MaskDiagnostic(Guid attemptId, string text) =>
        _secrets.TryGetValue(attemptId, out var known) && !known.Complete ? Withheld : Mask(attemptId, text);

    /// <summary>Appends the lines not stored yet; returns the number of lines stored in total.</summary>
    public async Task<long> AppendAsync(Guid attemptId, long firstSequence, IReadOnlyList<(long UnixMs, string Stream, string Text)> lines, CancellationToken cancellationToken)
    {
        var path = PathFor(attemptId);
        var (gate, _) = _state.GetOrAdd(attemptId, id => (new SemaphoreSlim(1, 1), CountLines(PathFor(id))));
        await gate.WaitAsync(cancellationToken);
        try
        {
            var next = _state[attemptId].Next;
            Directory.CreateDirectory(_directory);
            await using var writer = new StreamWriter(path, append: true);
            for (var i = 0; i < lines.Count; i++)
            {
                var sequence = firstSequence + i;
                if (sequence < next)
                {
                    continue;
                }

                var (unixMs, stream, text) = lines[i];
                await writer.WriteLineAsync(JsonSerializer.Serialize(new LogLineRecord(next, unixMs, stream, MaskDiagnostic(attemptId, text)), Json));
                next++;
            }

            _state[attemptId] = (gate, next);
            return next;
        }
        finally
        {
            gate.Release();
        }
    }

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

    /// <summary>Forgets in-memory state for a finished attempt.</summary>
    public void Close(Guid attemptId)
    {
        _state.TryRemove(attemptId, out _);
        _secrets.TryRemove(attemptId, out _);
    }

    /// <summary>Removes a finished attempt's log, for attempts that are not kept.</summary>
    public void Delete(Guid attemptId)
    {
        Close(attemptId);
        File.Delete(PathFor(attemptId));
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
