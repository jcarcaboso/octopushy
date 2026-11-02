using System.Text.Json.Nodes;
using Cronos;

namespace Octopushy.Definitions;

/// <summary>A configure action a guest runs on its own, at a time written in cron.</summary>
public sealed record GuestScheduleDefinition(string Action, string Cron);

/// <summary>Reads a guest's schedules, and finds when they fall in the lab's time zone.</summary>
public static class GuestSchedules
{
    /// <summary>What a guest declares. This also reads definitions that failed their schema, which report their own error, so what is malformed is left out.</summary>
    public static IReadOnlyList<GuestScheduleDefinition> Of(GuestDefinition guest) =>
        (guest.Json["setup"]?["schedules"] as JsonArray)?
            .OfType<JsonObject>()
            .Where(s => IsText(s["action"]) && IsText(s["cron"]))
            .Select(s => new GuestScheduleDefinition(s["action"]!.GetValue<string>(), s["cron"]!.GetValue<string>()))
            .ToList() ?? [];

    private static bool IsText(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out _);

    public static bool IsCron(string text) => CronExpression.TryParse(text, CronFormat.Standard, out _);

    /// <summary>The time zone maintenance times are written in: the lab's, or UTC if it names none.</summary>
    public static TimeZoneInfo ZoneOf(string? timezone) =>
        string.IsNullOrEmpty(timezone) ? TimeZoneInfo.Utc : TimeZoneInfo.FindSystemTimeZoneById(timezone);

    public static bool IsZone(string timezone)
    {
        try
        {
            ZoneOf(timezone);
            return true;
        }
        catch (Exception e) when (e is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return false;
        }
    }

    /// <summary>The first time after <paramref name="after"/> the schedule falls due.</summary>
    public static DateTimeOffset? Next(string cron, TimeZoneInfo zone, DateTimeOffset after) =>
        CronExpression.Parse(cron, CronFormat.Standard).GetNextOccurrence(after, zone);

    /// <summary>The newest time in the range the schedule fell due, if any.</summary>
    public static DateTimeOffset? Latest(string cron, TimeZoneInfo zone, DateTimeOffset from, DateTimeOffset to)
    {
        DateTimeOffset? latest = null;
        foreach (var occurrence in CronExpression.Parse(cron, CronFormat.Standard).GetOccurrences(from, to, zone, fromInclusive: false, toInclusive: true))
        {
            latest = occurrence;
        }

        return latest;
    }
}
