using Microsoft.EntityFrameworkCore;
using Octopushy.Server.Api;
using Octopushy.Server.Data;

namespace Octopushy.Server.Uptime;

/// <summary>Builds <see cref="UptimeDto"/> from what the monitor checks and the answers stored about it.</summary>
public static class UptimeReport
{
    public const int Beats = 96;
    public const int Days = 30;

    private sealed record DayRow(string CheckKey, int Day, long Up, long Down);

    public static async Task<UptimeDto> BuildAsync(OctopushyDb db, UptimeMonitor monitor, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var targets = monitor.Targets;
        var keys = targets.Select(t => t.Key).ToList();
        var states = await db.UptimeChecks.AsNoTracking().Where(c => keys.Contains(c.Key)).ToDictionaryAsync(c => c.Key, cancellationToken);

        // The current quarter hour is the last beat; the first starts 95 quarter hours before it.
        var current = UptimeMonitor.BucketOf(now);
        var first = current - UptimeMonitor.BucketLength * (Beats - 1);
        var recent = await db.UptimeBuckets.AsNoTracking().Where(b => keys.Contains(b.CheckKey) && b.StartsAt >= first)
            .ToListAsync(cancellationToken);

        // Day 0 is the last 24 hours, day 29 the oldest.
        var since = now - TimeSpan.FromDays(Days);
        var days = await db.Database.SqlQuery<DayRow>($"""
            SELECT check_key,
                   floor(extract(epoch FROM ({now} - starts_at)) / 86400)::int AS day,
                   sum(up)::bigint AS up, sum(down)::bigint AS down
            FROM uptime_buckets
            WHERE starts_at > {since} AND check_key = ANY({keys.ToArray()})
            GROUP BY 1, 2
            """).ToListAsync(cancellationToken);

        var checks = targets.Select(t =>
        {
            var state = states.GetValueOrDefault(t.Key);
            var own = recent.Where(b => b.CheckKey == t.Key).ToDictionary(b => b.StartsAt);
            var beats = new char[Beats];
            var latency = new int?[Beats];
            for (var i = 0; i < Beats; i++)
            {
                var bucket = own.GetValueOrDefault(first + UptimeMonitor.BucketLength * i);
                beats[i] = bucket switch
                {
                    null or { Up: 0, Down: 0 } => '-',
                    { Down: 0 } => 'u',
                    { Up: 0 } => 'd',
                    _ => 'p',
                };
                latency[i] = bucket is { Up: > 0 } ? (int)(bucket.LatencyMsTotal / bucket.Up) : null;
            }

            var byDay = days.Where(d => d.CheckKey == t.Key && d.Day is >= 0 and < Days).ToDictionary(d => d.Day);
            double? Percent(int fromDay, int toDay)
            {
                long up = 0, down = 0;
                for (var d = fromDay; d < toDay; d++)
                {
                    if (byDay.TryGetValue(d, out var row))
                        (up, down) = (up + row.Up, down + row.Down);
                }

                return up + down == 0 ? null : Math.Round(100.0 * up / (up + down), 3);
            }

            return new UptimeCheckDto(
                t.Key,
                t.Kind,
                t.Installation,
                t.Host,
                t.Resource,
                t.Url,
                state is null ? "pending" : state.Up ? "up" : "down",
                state?.Detail,
                state?.Since,
                state?.CheckedAt,
                state?.ElapsedMs,
                state?.Agent,
                new UptimePercentagesDto(Percent(0, 1), Percent(0, 7), Percent(0, Days)),
                new string(beats),
                latency,
                Enumerable.Range(0, Days).Select(i => Percent(Days - 1 - i, Days - i)).ToList());
        }).ToList();

        return new UptimeDto(monitor.Checking, (int)UptimeMonitor.Interval.TotalSeconds, now, checks);
    }
}
