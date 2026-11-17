using System.Globalization;
using System.Text;
using System.Text.Json;
using Octopushy.Agent.V1;

namespace Octopushy.Server.Agents;

/// <summary>
/// Free space of the file system holding one of an agent's folders: <c>state</c>, <c>work</c> or <c>nix-store</c>.
/// </summary>
public sealed record AgentFreeSpace(string Name, long FreeBytes, long TotalBytes);

/// <summary>
/// The size of one of an agent's caches: <c>providers</c> or <c>nix-cache</c>. <c>Complete</c> is false when the agent's
/// walk stopped at its limit, and <c>Bytes</c> is then a lower bound.
/// </summary>
public sealed record AgentCacheSize(string Name, long Bytes, bool Complete);

/// <summary>The room an agent reported last on its current connection, and when.</summary>
public sealed record AgentStorage(DateTimeOffset ReportedAt, IReadOnlyList<AgentFreeSpace> Free, IReadOnlyList<AgentCacheSize> Caches);

/// <summary>
/// Why an agent does not offer an executor, and how much room it has, as the agent says. Both are hostile text and
/// numbers: only known names are kept, and every reason is cleaned and bounded here whatever the agent did.
/// </summary>
public static class AgentAvailability
{
    /// <summary>The executors this server dispatches to. A reason for any other is dropped.</summary>
    public static readonly IReadOnlySet<string> KnownExecutors = new HashSet<string>(StringComparer.Ordinal)
    {
        "opentofu", "proxmox", "ansible", "nix",
    };

    /// <summary>The longest reason kept, in characters, as the protocol promises.</summary>
    public const int MaxReasonChars = 300;

    /// <summary>The most agents a waiting text names.</summary>
    public const int MaxAgentsNamed = 5;

    /// <summary>The longest list of agents and reasons a waiting text carries: five agents with long reasons fit.</summary>
    public const int MaxLackingChars = 1800;

    private static readonly IReadOnlySet<string> FreeSpaceNames = new HashSet<string>(StringComparer.Ordinal) { "state", "work", "nix-store" };
    private static readonly IReadOnlySet<string> CacheNames = new HashSet<string>(StringComparer.Ordinal) { "providers", "nix-cache" };

    /// <summary>
    /// <paramref name="text"/> on one line, without control or formatting characters (such as bidirectional overrides),
    /// at most <see cref="MaxReasonChars"/> characters.
    /// </summary>
    public static string Clean(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return "";
        var line = new StringBuilder();
        var space = false;
        // Only the start is read: a reason longer than this is cut anyway.
        foreach (var c in text.AsSpan(0, Math.Min(text.Length, MaxReasonChars * 8)))
        {
            var category = char.GetUnicodeCategory(c);
            if (char.IsWhiteSpace(c) || char.IsControl(c)
                || category is UnicodeCategory.Format or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator)
            {
                space = line.Length > 0;
                continue;
            }

            if (space)
                line.Append(' ');
            space = false;
            line.Append(c);
        }

        var cleaned = line.ToString();
        var info = new StringInfo(cleaned);
        if (info.LengthInTextElements <= MaxReasonChars)
            return cleaned;
        return info.SubstringByTextElements(0, MaxReasonChars - 1) + "…";
    }

    /// <summary>The reasons of known executors, cleaned and bounded, by executor.</summary>
    public static SortedDictionary<string, string> Unavailable(IEnumerable<KeyValuePair<string, string>> reported)
    {
        var kept = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var (executor, reason) in reported)
            if (KnownExecutors.Contains(executor))
                kept[executor] = Clean(reason);
        return kept;
    }

    /// <summary>Executor names as an agent sends them, bounded: at most 32, each at most 64 characters.</summary>
    public static List<string> Executors(IEnumerable<string> reported) =>
        reported.Where(e => e.Length is > 0 and <= 64).Distinct(StringComparer.Ordinal).Take(32).ToList();

    /// <summary>The reasons as stored on the agent's record: null when there are none.</summary>
    public static string? Serialize(IReadOnlyDictionary<string, string> unavailable) =>
        unavailable.Count == 0 ? null : JsonSerializer.Serialize(unavailable);

    /// <summary>The reasons stored on an agent's record, cleaned again. Empty when there are none or they cannot be read.</summary>
    public static IReadOnlyDictionary<string, string> Deserialize(string? stored)
    {
        if (string.IsNullOrEmpty(stored))
            return new SortedDictionary<string, string>();
        try
        {
            return Unavailable(JsonSerializer.Deserialize<Dictionary<string, string>>(stored) ?? []);
        }
        catch (JsonException)
        {
            return new SortedDictionary<string, string>();
        }
    }

    /// <summary>A storage report with only known names, each once, and sizes that fit a <c>long</c>.</summary>
    public static AgentStorage Storage(StorageReport report, DateTimeOffset now) => new(
        now,
        report.Free.Where(f => FreeSpaceNames.Contains(f.Name)).DistinctBy(f => f.Name)
            .Select(f => new AgentFreeSpace(f.Name, Bytes(f.FreeBytes), Bytes(f.TotalBytes))).ToList(),
        report.Caches.Where(c => CacheNames.Contains(c.Name)).DistinctBy(c => c.Name)
            .Select(c => new AgentCacheSize(c.Name, Bytes(c.Bytes), c.Complete)).ToList());

    private static long Bytes(ulong n) => n > long.MaxValue ? long.MaxValue : (long)n;

    /// <summary>
    /// For a waiting text: the agents that lack <paramref name="executor"/> and why, by name, at most
    /// <see cref="MaxAgentsNamed"/>. Empty when there are none. The same agents give the same text, so a run that waits
    /// is not written again while nothing changes.
    /// </summary>
    public static string Lacking(string executor, IEnumerable<AgentSession> sessions)
    {
        var lacking = sessions.Where(s => !s.Executors.Contains(executor))
            .OrderBy(s => s.Name, StringComparer.Ordinal).ThenBy(s => s.AgentId).ToList();
        if (lacking.Count == 0)
            return "";
        var named = lacking.Take(MaxAgentsNamed).Select(s => $"{s.Name}: " + (s.Unavailable.TryGetValue(executor, out var reason) && reason.Length > 0
            ? reason.TrimEnd('.')
            : "gives no reason (it may be older)"));
        var more = lacking.Count > MaxAgentsNamed ? $"; and {lacking.Count - MaxAgentsNamed} more" : "";
        var text = $" Without {executor}: {string.Join("; ", named)}{more}.";
        return text.Length <= MaxLackingChars ? text : text[..(MaxLackingChars - 1)] + "…";
    }
}
