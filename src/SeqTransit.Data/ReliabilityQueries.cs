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
                OnTime = g.Count(o => (o.ArrivalDelaySeconds ?? o.DepartureDelaySeconds) >= early
                                      && (o.ArrivalDelaySeconds ?? o.DepartureDelaySeconds) <= late),
                AverageDelay = g.Average(o => (double)(o.ArrivalDelaySeconds ?? o.DepartureDelaySeconds ?? 0)),
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
                var routeType = staticRoute?.RouteType ?? 3;
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
                OnTime = g.Count(o => (o.ArrivalDelaySeconds ?? o.DepartureDelaySeconds) >= early
                                      && (o.ArrivalDelaySeconds ?? o.DepartureDelaySeconds) <= late),
                AverageDelay = g.Average(o => (double)(o.ArrivalDelaySeconds ?? o.DepartureDelaySeconds ?? 0)),
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
                OnTime = g.Count(o => (o.ArrivalDelaySeconds ?? o.DepartureDelaySeconds) >= early
                                      && (o.ArrivalDelaySeconds ?? o.DepartureDelaySeconds) <= late),
                AverageDelay = g.Average(o => (double)(o.ArrivalDelaySeconds ?? o.DepartureDelaySeconds ?? 0)),
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

    public const int MaxPlausibleDelaySeconds = 3600;

    private IQueryable<StopDelayObservationRow> PassedStops(DateTimeOffset now) =>
        db.StopDelayObservations
            .AsNoTracking()
            .Where(o => o.EventTime != null && o.EventTime < now)
            .Where(o => o.ArrivalDelaySeconds != null || o.DepartureDelaySeconds != null)
            .Where(o => (o.ArrivalDelaySeconds ?? o.DepartureDelaySeconds) >= -MaxPlausibleDelaySeconds
                     && (o.ArrivalDelaySeconds ?? o.DepartureDelaySeconds) <= MaxPlausibleDelaySeconds);

    private static double Rate(int part, int total) => total == 0 ? 0 : Math.Round((double)part / total, 4);
}
