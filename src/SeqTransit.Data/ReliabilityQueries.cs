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

    public async Task<IReadOnlyList<RouteOnTime>> OnTimeByRouteAsync(
        DateOnly serviceDate, OnTimePolicy policy, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var early = -policy.EarlyToleranceSeconds;
        var late = policy.LateToleranceSeconds;

        var groups = await PassedStops(now)
            .Where(o => o.ServiceDate == serviceDate)
            .GroupBy(o => o.RouteId)
            .Select(g => new
            {
                RouteId = g.Key,
                Observed = g.Count(),
                OnTime = g.Count(o => o.DelaySeconds >= early && o.DelaySeconds <= late),
                AverageDelay = g.Average(o => (double)o.DelaySeconds),
            })
            .ToListAsync(cancellationToken);

        var routeIds = groups.Select(g => g.RouteId).Distinct().ToList();
        var staticRoutes = await db.GtfsRoutes
            .AsNoTracking()
            .Where(r => routeIds.Contains(r.RouteId))
            .ToDictionaryAsync(r => r.RouteId, cancellationToken);

        return groups
            .Select(g =>
            {
                var staticRoute = staticRoutes.GetValueOrDefault(g.RouteId);
                var shortName = staticRoute?.RouteShortName ?? g.RouteId;
                var longName = staticRoute?.RouteLongName ?? string.Empty;
                var routeType = staticRoute?.RouteType ?? UnknownRouteType;
                return new RouteOnTime(
                    g.RouteId,
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

        var days = await PassedStops(now)
            .Where(o => o.RouteId == routeId && o.ServiceDate >= from && o.ServiceDate <= to)
            .GroupBy(o => o.ServiceDate)
            .Select(g => new
            {
                ServiceDate = g.Key,
                Observed = g.Count(),
                OnTime = g.Count(o => o.DelaySeconds >= early && o.DelaySeconds <= late),
                AverageDelay = g.Average(o => (double)o.DelaySeconds),
            })
            .ToListAsync(cancellationToken);

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

        var groups = await PassedStops(now)
            .Where(o => o.RouteId == routeId && o.ServiceDate == serviceDate)
            .GroupBy(o => o.StopId)
            .Select(g => new
            {
                StopId = g.Key,
                Observed = g.Count(),
                OnTime = g.Count(o => o.DelaySeconds >= early && o.DelaySeconds <= late),
                AverageDelay = g.Average(o => (double)o.DelaySeconds),
            })
            .ToListAsync(cancellationToken);

        var stopIds = groups.Select(g => g.StopId).Distinct().ToList();
        var staticStops = await db.GtfsStops
            .AsNoTracking()
            .Where(s => stopIds.Contains(s.StopId))
            .ToDictionaryAsync(s => s.StopId, cancellationToken);

        return groups
            .Select(g =>
            {
                var staticStop = staticStops.GetValueOrDefault(g.StopId);
                var stopName = staticStop?.StopName ?? g.StopId;
                return new RouteStopPerformance(
                    g.StopId,
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
    // reliable_stop_observation view (db/migrations/0006); only "already passed" is applied here.
    private IQueryable<ReliableObservationRow> PassedStops(DateTimeOffset now) =>
        db.ReliableObservations
            .AsNoTracking()
            .Where(o => o.EventTime < now);

    private static double Rate(int part, int total) => total == 0 ? 0 : Math.Round((double)part / total, 4);

    public async Task<RouteSummaryResponse?> RouteSummaryAsync(
        string routeId, DateOnly from, DateOnly to, OnTimePolicy policy, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var early = -policy.EarlyToleranceSeconds;
        var late = policy.LateToleranceSeconds;

        var stat = await db.Database.SqlQuery<RouteSummaryStatsRow>($"""
            SELECT
                count(*)::int AS "ObservedStops",
                count(*) FILTER (WHERE delay_s >= {early}
                                  AND delay_s <= {late})::int AS "OnTimeStops",
                coalesce(avg(delay_s), 0)::float8 AS "AverageDelaySeconds",
                coalesce(percentile_cont(0.5) WITHIN GROUP (ORDER BY delay_s), 0)::float8 AS "MedianDelaySeconds",
                coalesce(percentile_cont(0.9) WITHIN GROUP (ORDER BY delay_s), 0)::float8 AS "P90DelaySeconds"
            FROM reliable_stop_observation
            WHERE route_id = {routeId}
              AND service_date >= {from}
              AND service_date <= {to}
              AND event_time < {now}
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
            hasData ? Math.Round(stat.MedianDelaySeconds, 1) : null,
            hasData ? Math.Round(stat.P90DelaySeconds, 1) : null,
            cancelledTrips);
    }

    public async Task<IReadOnlyList<RouteHourPerformance>> RouteHourlyOnTimeAsync(
        string routeId, DateOnly serviceDate, OnTimePolicy policy, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var early = -policy.EarlyToleranceSeconds;
        var late = policy.LateToleranceSeconds;

        var rows = await db.Database.SqlQuery<RouteHourStatsRow>($"""
            SELECT
                extract(hour FROM event_time AT TIME ZONE 'Australia/Brisbane')::int AS "Hour",
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

        var rows = await db.Database.SqlQuery<WorstStopStatsRow>($"""
            SELECT
                o.stop_id AS "StopId",
                coalesce(s.stop_name, o.stop_id) AS "StopName",
                s.stop_lat AS "Latitude",
                s.stop_lon AS "Longitude",
                count(*)::int AS "ObservedStops",
                count(*) FILTER (WHERE o.delay_s >= {early}
                                  AND o.delay_s <= {late})::int AS "OnTimeStops",
                round(avg(o.delay_s)::numeric, 1)::float8 AS "AverageDelaySeconds"
            FROM reliable_stop_observation o
            LEFT JOIN gtfs_stop s ON s.stop_id = o.stop_id
            WHERE o.service_date = {serviceDate}
              AND o.event_time < {now}
            GROUP BY o.stop_id, s.stop_name, s.stop_lat, s.stop_lon
            HAVING count(*) >= {minObservations}
            ORDER BY "AverageDelaySeconds" DESC
            LIMIT {limit}
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

        var rows = await db.Database.SqlQuery<ModeReliabilityStatsRow>($"""
            SELECT
                coalesce(r.route_type, {UnknownRouteType})::int AS "RouteType",
                count(*)::int AS "ObservedStops",
                count(*) FILTER (WHERE o.delay_s >= {early}
                                  AND o.delay_s <= {late})::int AS "OnTimeStops",
                round(coalesce(avg(o.delay_s), 0)::numeric, 1)::float8 AS "AverageDelaySeconds"
            FROM reliable_stop_observation o
            LEFT JOIN gtfs_route r ON r.route_id = o.route_id
            WHERE o.service_date = {serviceDate}
              AND o.event_time < {now}
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

    public static string FormatModeName(int routeType) => routeType switch
    {
        0 => "Tram",
        1 => "Subway",
        2 => "Train",
        3 => "Bus",
        4 => "Ferry",
        UnknownRouteType => "Unknown",
        _ => "Other"
    };
}

internal sealed class RouteSummaryStatsRow
{
    public int ObservedStops { get; set; }
    public int OnTimeStops { get; set; }
    public double AverageDelaySeconds { get; set; }
    public double MedianDelaySeconds { get; set; }
    public double P90DelaySeconds { get; set; }
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
