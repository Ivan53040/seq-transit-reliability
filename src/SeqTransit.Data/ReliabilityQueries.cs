using Microsoft.EntityFrameworkCore;
using SeqTransit.Core.Reliability;

namespace SeqTransit.Data;

public sealed record RouteOnTime(
    string RouteId,
    string RouteShortName,
    string RouteLongName,
    int RouteType,
    int ObservedStops,
    int OnTimeStops,
    double OnTimeRate,
    double AverageDelaySeconds);

public sealed record DailyOnTime(DateOnly ServiceDate, int ObservedStops, int OnTimeStops, double OnTimeRate, double AverageDelaySeconds);

public sealed record RouteCancellations(string RouteId, int CancelledTrips);

public sealed record RouteStopPerformance(
    string StopId,
    string StopName,
    int ObservedStops,
    int OnTimeStops,
    double OnTimeRate,
    double AverageDelaySeconds);

public sealed record RouteSearchItem(
    string RouteId,
    string RouteShortName,
    string RouteLongName,
    int RouteType);

public sealed record RouteSummaryResponse(
    string RouteId,
    string RouteShortName,
    string RouteLongName,
    int RouteType,
    DateOnly FromDate,
    DateOnly ToDate,
    int ObservedStops,
    int OnTimeStops,
    double? OnTimeRate,
    double? AverageDelaySeconds,
    double? MedianDelaySeconds,
    double? P90DelaySeconds,
    int CancelledTrips);

public sealed record RouteHourPerformance(
    int Hour,
    int ObservedStops,
    int OnTimeStops,
    double OnTimeRate,
    double AverageDelaySeconds);

public sealed record WorstStopPerformance(
    string StopId,
    string StopName,
    double? Latitude,
    double? Longitude,
    int ObservedStops,
    int OnTimeStops,
    double OnTimeRate,
    double AverageDelaySeconds);

public sealed record ModeReliability(
    int RouteType,
    string ModeName,
    int ObservedStops,
    int OnTimeStops,
    double OnTimeRate,
    double AverageDelaySeconds);

public sealed record SnapshotSummary(
    DateTimeOffset FeedTimestamp,
    DateTimeOffset LoadedAt,
    int TripUpdateCount,
    int StopUpdateCount,
    int CancelledTripCount);

/// <summary>
/// Read-side queries for the API.
/// </summary>
/// <remarks>
/// Only stops the vehicle has already passed are counted: predictions for upcoming stops keep
/// changing, so they say little about how the service actually ran.
/// </remarks>
public sealed class ReliabilityQueries(TransitDbContext db)
{
    /// <summary>Route type reported for routes missing from the static timetable (added trips, stale GTFS).</summary>
    public const int UnknownRouteType = -1;

    /// <summary>
    /// Hour of a stop event on its service day, Brisbane time, GTFS style: a trip that runs past
    /// midnight is counted as 24, 25 and so on, so it is not mixed up with early-morning trips.
    /// </summary>
    internal const string BrisbaneServiceHourSql = """
        (extract(hour FROM event_time AT TIME ZONE 'Australia/Brisbane')
            + 24 * ((event_time AT TIME ZONE 'Australia/Brisbane')::date - service_date))::int
        """;

    public async Task<IReadOnlyList<RouteOnTime>> OnTimeByRouteAsync(
        DateOnly serviceDate, OnTimePolicy policy, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var early = -policy.EarlyToleranceSeconds;
        var late = policy.LateToleranceSeconds;

        var groups = await db.Database.SqlQuery<KeyStatsRow>($"""
            SELECT
                route_id AS "Key",
                sum(observed_stops)::int AS "Observed",
                sum(on_time_stops)::int AS "OnTime",
                (sum(total_delay_s)::float8 / sum(observed_stops)) AS "AverageDelay"
            FROM stop_day_figures({serviceDate}, {serviceDate}, {now}, {early}, {late})
            GROUP BY route_id
            """).ToListAsync(cancellationToken);

        var routeIds = groups.Select(g => g.Key).Distinct().ToList();
        var staticRoutes = await db.GtfsRoutes
            .AsNoTracking()
            .Where(r => routeIds.Contains(r.RouteId))
            .ToDictionaryAsync(r => r.RouteId, cancellationToken);

        return groups
            .Select(g =>
            {
                var staticRoute = staticRoutes.GetValueOrDefault(g.Key);
                var shortName = staticRoute?.RouteShortName ?? g.Key;
                var longName = staticRoute?.RouteLongName ?? string.Empty;
                var routeType = staticRoute?.RouteType ?? UnknownRouteType;
                return new RouteOnTime(
                    g.Key,
                    shortName,
                    longName,
                    routeType,
                    g.Observed,
                    g.OnTime,
                    Rate(g.OnTime, g.Observed),
                    Math.Round(g.AverageDelay, 1));
            })
            .OrderBy(r => r.OnTimeRate)
            .ThenByDescending(r => r.ObservedStops)
            .ToList();
    }

    public async Task<IReadOnlyList<DailyOnTime>> DailyOnTimeAsync(
        string routeId, DateOnly from, DateOnly to, OnTimePolicy policy, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var early = -policy.EarlyToleranceSeconds;
        var late = policy.LateToleranceSeconds;

        var days = await db.Database.SqlQuery<DayStatsRow>($"""
            SELECT
                service_date AS "ServiceDate",
                sum(observed_stops)::int AS "Observed",
                sum(on_time_stops)::int AS "OnTime",
                (sum(total_delay_s)::float8 / sum(observed_stops)) AS "AverageDelay"
            FROM stop_day_figures({from}, {to}, {now}, {early}, {late})
            WHERE route_id = {routeId}
            GROUP BY service_date
            """).ToListAsync(cancellationToken);

        return days
            .OrderBy(d => d.ServiceDate)
            .Select(d => new DailyOnTime(d.ServiceDate, d.Observed, d.OnTime, Rate(d.OnTime, d.Observed), Math.Round(d.AverageDelay, 1)))
            .ToList();
    }

    public async Task<IReadOnlyList<RouteStopPerformance>> StopsByRouteAsync(
        string routeId, DateOnly serviceDate, OnTimePolicy policy, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var early = -policy.EarlyToleranceSeconds;
        var late = policy.LateToleranceSeconds;

        var groups = await db.Database.SqlQuery<KeyStatsRow>($"""
            SELECT
                stop_id AS "Key",
                sum(observed_stops)::int AS "Observed",
                sum(on_time_stops)::int AS "OnTime",
                (sum(total_delay_s)::float8 / sum(observed_stops)) AS "AverageDelay"
            FROM stop_day_figures({serviceDate}, {serviceDate}, {now}, {early}, {late})
            WHERE route_id = {routeId}
            GROUP BY stop_id
            """).ToListAsync(cancellationToken);

        var stopIds = groups.Select(g => g.Key).Distinct().ToList();
        var staticStops = await db.GtfsStops
            .AsNoTracking()
            .Where(s => stopIds.Contains(s.StopId))
            .ToDictionaryAsync(s => s.StopId, cancellationToken);

        return groups
            .Select(g =>
            {
                var staticStop = staticStops.GetValueOrDefault(g.Key);
                var stopName = staticStop?.StopName ?? g.Key;
                return new RouteStopPerformance(
                    g.Key,
                    stopName,
                    g.Observed,
                    g.OnTime,
                    Rate(g.OnTime, g.Observed),
                    Math.Round(g.AverageDelay, 1));
            })
            .OrderBy(s => s.OnTimeRate)
            .ThenByDescending(s => s.AverageDelaySeconds)
            .ToList();
    }

    public async Task<IReadOnlyList<RouteSearchItem>> AllRoutesAsync(CancellationToken cancellationToken) =>
        await db.GtfsRoutes
            .AsNoTracking()
            .OrderBy(r => r.RouteShortName)
            .ThenBy(r => r.RouteId)
            .Select(r => new RouteSearchItem(r.RouteId, r.RouteShortName, r.RouteLongName, r.RouteType))
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<RouteCancellations>> CancellationsByRouteAsync(
        DateOnly serviceDate, CancellationToken cancellationToken) =>
        await db.TripCancellations
            .AsNoTracking()
            .Where(c => c.ServiceDate == serviceDate)
            .GroupBy(c => c.RouteId)
            .Select(g => new { RouteId = g.Key, Count = g.Count() })
            .OrderByDescending(r => r.Count)
            .Select(r => new RouteCancellations(r.RouteId, r.Count))
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<SnapshotSummary>> RecentSnapshotsAsync(int limit, CancellationToken cancellationToken) =>
        await db.FeedSnapshots
            .AsNoTracking()
            .OrderByDescending(s => s.FeedTimestamp)
            .Take(limit)
            .Select(s => new SnapshotSummary(s.FeedTimestamp, s.LoadedAt, s.TripUpdateCount, s.StopUpdateCount, s.CancelledTripCount))
            .ToListAsync(cancellationToken);

    public Task<DateTimeOffset?> LatestSnapshotAsync(CancellationToken cancellationToken) =>
        db.FeedSnapshots.MaxAsync(s => (DateTimeOffset?)s.FeedTimestamp, cancellationToken);

    public async Task<WeeklySummaryResponse?> LatestWeeklySummaryAsync(CancellationToken cancellationToken)
    {
        var row = await db.WeeklySummaries
            .AsNoTracking()
            .OrderByDescending(s => s.WeekStart)
            .FirstOrDefaultAsync(cancellationToken);

        if (row is null)
        {
            return null;
        }

        using var doc = System.Text.Json.JsonDocument.Parse(row.InputFiguresJson);
        return new WeeklySummaryResponse(
            row.WeekStart,
            row.SummaryText,
            doc.RootElement.Clone(),
            row.ModelId,
            row.CreatedAt);
    }

    // The trusted-data rules (has a time and a delay, |delay| <= 1 hour) live in the
    // reliable_stop_observation view (migrations 0006, 0008). Counts are read through
    // stop_day_figures (migration 0009), which adds archived days, so a day reports the same
    // numbers before and after its detail is archived. "Already passed" is the as_of argument.

    private static double Rate(int part, int total) => total == 0 ? 0 : Math.Round((double)part / total, 4);

    public async Task<RouteSummaryResponse?> RouteSummaryAsync(
        string routeId, DateOnly from, DateOnly to, OnTimePolicy policy, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var early = -policy.EarlyToleranceSeconds;
        var late = policy.LateToleranceSeconds;

        // Counts come from detail plus archived days. Percentiles need the individual delays, so
        // they are only reported when every day in the range still has its detail.
        var stat = await db.Database.SqlQuery<RouteSummaryStatsRow>($"""
            WITH totals AS (
                SELECT
                    coalesce(sum(observed_stops), 0)::int AS observed,
                    coalesce(sum(on_time_stops), 0)::int AS on_time,
                    coalesce(sum(total_delay_s)::float8 / nullif(sum(observed_stops), 0), 0) AS average_delay
                FROM stop_day_figures({from}, {to}, {now}, {early}, {late})
                WHERE route_id = {routeId}
            ),
            detail AS (
                SELECT
                    percentile_cont(0.5) WITHIN GROUP (ORDER BY delay_s) AS median,
                    percentile_cont(0.9) WITHIN GROUP (ORDER BY delay_s) AS p90
                FROM reliable_stop_observation
                WHERE route_id = {routeId}
                  AND service_date >= {from}
                  AND service_date <= {to}
                  AND event_time < {now}
            )
            SELECT
                t.observed AS "ObservedStops",
                t.on_time AS "OnTimeStops",
                t.average_delay AS "AverageDelaySeconds",
                CASE WHEN archived.any THEN NULL ELSE coalesce(d.median, 0) END::float8 AS "MedianDelaySeconds",
                CASE WHEN archived.any THEN NULL ELSE coalesce(d.p90, 0) END::float8 AS "P90DelaySeconds"
            FROM totals t
            CROSS JOIN detail d
            CROSS JOIN (
                SELECT EXISTS (
                    SELECT 1 FROM stop_daily_stats
                    WHERE route_id = {routeId} AND service_date >= {from} AND service_date <= {to}
                ) AS any
            ) archived
            """).FirstOrDefaultAsync(cancellationToken);

        var staticRoute = await db.GtfsRoutes
            .AsNoTracking()
            .FirstOrDefaultAsync(r => r.RouteId == routeId, cancellationToken);

        if (stat is null || (staticRoute is null && stat.ObservedStops == 0))
        {
            return null;
        }

        // With no observed stops there is no rate or delay to report: return null rather than a
        // misleading 0% (for example, a weekday-only route viewed over a weekend).
        var hasData = stat.ObservedStops > 0;

        var cancelledTrips = await db.TripCancellations
            .AsNoTracking()
            .Where(c => c.RouteId == routeId && c.ServiceDate >= from && c.ServiceDate <= to)
            .CountAsync(cancellationToken);

        return new RouteSummaryResponse(
            routeId,
            staticRoute?.RouteShortName ?? routeId,
            staticRoute?.RouteLongName ?? string.Empty,
            staticRoute?.RouteType ?? UnknownRouteType,
            from,
            to,
            stat.ObservedStops,
            stat.OnTimeStops,
            hasData ? Rate(stat.OnTimeStops, stat.ObservedStops) : null,
            hasData ? Math.Round(stat.AverageDelaySeconds, 1) : null,
            hasData && stat.MedianDelaySeconds is { } median ? Math.Round(median, 1) : null,
            hasData && stat.P90DelaySeconds is { } p90 ? Math.Round(p90, 1) : null,
            cancelledTrips);
    }

    public async Task<IReadOnlyList<RouteHourPerformance>> RouteHourlyOnTimeAsync(
        string routeId, DateOnly serviceDate, OnTimePolicy policy, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var early = -policy.EarlyToleranceSeconds;
        var late = policy.LateToleranceSeconds;

        var rows = await db.Database.SqlQuery<RouteHourStatsRow>($"""
            SELECT
                -- Same expression as BrisbaneServiceHourSql. Written out because an interpolated value
                -- here would become a query parameter, not SQL text.
                (extract(hour FROM event_time AT TIME ZONE 'Australia/Brisbane')
                    + 24 * ((event_time AT TIME ZONE 'Australia/Brisbane')::date - service_date))::int AS "Hour",
                count(*)::int AS "ObservedStops",
                count(*) FILTER (WHERE delay_s >= {early}
                                  AND delay_s <= {late})::int AS "OnTimeStops",
                round(coalesce(avg(delay_s), 0)::numeric, 1)::float8 AS "AverageDelaySeconds"
            FROM reliable_stop_observation
            WHERE route_id = {routeId}
              AND service_date = {serviceDate}
              AND event_time < {now}
            GROUP BY 1
            ORDER BY 1
            """).ToListAsync(cancellationToken);

        return rows
            .Select(r => new RouteHourPerformance(
                r.Hour,
                r.ObservedStops,
                r.OnTimeStops,
                Rate(r.OnTimeStops, r.ObservedStops),
                r.AverageDelaySeconds))
            .ToList();
    }

    public async Task<IReadOnlyList<WorstStopPerformance>> WorstStopsAsync(
        DateOnly serviceDate, OnTimePolicy policy, DateTimeOffset now, int limit, int minObservations, CancellationToken cancellationToken)
    {
        var early = -policy.EarlyToleranceSeconds;
        var late = policy.LateToleranceSeconds;

        // Aggregate per stop first and join gtfs_stop only for the rows returned: joining before the
        // GROUP BY looked up the stop name for every observation (about 160,000 a day).
        var rows = await db.Database.SqlQuery<WorstStopStatsRow>($"""
            WITH per_stop AS (
                SELECT
                    stop_id,
                    sum(observed_stops) AS observed,
                    sum(on_time_stops) AS on_time,
                    round(sum(total_delay_s)::numeric / sum(observed_stops), 1) AS average_delay
                FROM stop_day_figures({serviceDate}, {serviceDate}, {now}, {early}, {late})
                GROUP BY stop_id
                HAVING sum(observed_stops) >= {minObservations}
                ORDER BY average_delay DESC
                LIMIT {limit}
            )
            SELECT
                p.stop_id AS "StopId",
                coalesce(s.stop_name, p.stop_id) AS "StopName",
                s.stop_lat AS "Latitude",
                s.stop_lon AS "Longitude",
                p.observed::int AS "ObservedStops",
                p.on_time::int AS "OnTimeStops",
                p.average_delay::float8 AS "AverageDelaySeconds"
            FROM per_stop p
            LEFT JOIN gtfs_stop s ON s.stop_id = p.stop_id
            ORDER BY p.average_delay DESC
            """).ToListAsync(cancellationToken);

        return rows
            .Select(r => new WorstStopPerformance(
                r.StopId,
                r.StopName,
                r.Latitude,
                r.Longitude,
                r.ObservedStops,
                r.OnTimeStops,
                Rate(r.OnTimeStops, r.ObservedStops),
                r.AverageDelaySeconds))
            .ToList();
    }

    public async Task<IReadOnlyList<ModeReliability>> ModesOnTimeAsync(
        DateOnly serviceDate, OnTimePolicy policy, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var early = -policy.EarlyToleranceSeconds;
        var late = policy.LateToleranceSeconds;

        // Aggregate per route first, then join the ~1,200 static routes once per route instead of
        // once per observation.
        var rows = await db.Database.SqlQuery<ModeReliabilityStatsRow>($"""
            WITH per_route AS (
                SELECT
                    route_id,
                    sum(observed_stops) AS observed,
                    sum(on_time_stops) AS on_time,
                    sum(total_delay_s) AS total_delay
                FROM stop_day_figures({serviceDate}, {serviceDate}, {now}, {early}, {late})
                GROUP BY route_id
            )
            SELECT
                coalesce(r.route_type, {UnknownRouteType})::int AS "RouteType",
                sum(p.observed)::int AS "ObservedStops",
                sum(p.on_time)::int AS "OnTimeStops",
                round(coalesce(sum(p.total_delay)::numeric / nullif(sum(p.observed), 0), 0), 1)::float8 AS "AverageDelaySeconds"
            FROM per_route p
            LEFT JOIN gtfs_route r ON r.route_id = p.route_id
            GROUP BY 1
            ORDER BY 1
            """).ToListAsync(cancellationToken);

        return rows
            .Select(r => new ModeReliability(
                r.RouteType,
                FormatModeName(r.RouteType),
                r.ObservedStops,
                r.OnTimeStops,
                Rate(r.OnTimeStops, r.ObservedStops),
                r.AverageDelaySeconds))
            .OrderByDescending(m => m.OnTimeRate)
            .ToList();
    }

    // Keep in step with formatMode() in web/src/api.ts.
    public static string FormatModeName(int routeType) => routeType switch
    {
        0 => "Tram",
        1 => "Metro",
        2 => "Train",
        3 => "Bus",
        4 => "Ferry",
        UnknownRouteType => "Unknown",
        _ => "Transit"
    };
}

internal sealed class RouteSummaryStatsRow
{
    public int ObservedStops { get; set; }
    public int OnTimeStops { get; set; }
    public double AverageDelaySeconds { get; set; }
    public double? MedianDelaySeconds { get; set; }
    public double? P90DelaySeconds { get; set; }
}

internal sealed class RouteHourStatsRow
{
    public int Hour { get; set; }
    public int ObservedStops { get; set; }
    public int OnTimeStops { get; set; }
    public double AverageDelaySeconds { get; set; }
}

internal sealed class WorstStopStatsRow
{
    public string StopId { get; set; } = string.Empty;
    public string StopName { get; set; } = string.Empty;
    public double? Latitude { get; set; }
    public double? Longitude { get; set; }
    public int ObservedStops { get; set; }
    public int OnTimeStops { get; set; }
    public double AverageDelaySeconds { get; set; }
}

internal sealed class ModeReliabilityStatsRow
{
    public int RouteType { get; set; }
    public int ObservedStops { get; set; }
    public int OnTimeStops { get; set; }
    public double AverageDelaySeconds { get; set; }
}

internal sealed class KeyStatsRow
{
    public string Key { get; set; } = string.Empty;
    public int Observed { get; set; }
    public int OnTime { get; set; }
    public double AverageDelay { get; set; }
}

internal sealed class DayStatsRow
{
    public DateOnly ServiceDate { get; set; }
    public int Observed { get; set; }
    public int OnTime { get; set; }
    public double AverageDelay { get; set; }
}
