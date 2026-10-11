using Microsoft.AspNetCore.Mvc;
using SeqTransit.Core;
using SeqTransit.Core.Reliability;
using SeqTransit.Data;

namespace SeqTransit.Api.Endpoints;

public sealed record HealthResponse(string Status, DateTimeOffset? LatestSnapshot, double? SnapshotAgeSeconds);

public static class ReliabilityEndpoints
{
    public static IEndpointRouteBuilder MapReliabilityEndpoints(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api");

        // Data is loaded in batches, so some age is normal; Health:StaleAfterMinutes sets the limit.
        api.MapGet("/health", async (
            [FromServices] ReliabilityQueries queries,
            [FromServices] TimeProvider clock,
            [FromServices] IConfiguration configuration,
            CancellationToken cancellationToken) =>
        {
            var staleAfter = TimeSpan.FromMinutes(configuration.GetValue("Health:StaleAfterMinutes", 75));
            var latest = await queries.LatestSnapshotAsync(cancellationToken);
            var age = latest is { } value ? clock.GetUtcNow() - value : (TimeSpan?)null;
            var status = age is { } fresh && fresh < staleAfter ? "ok" : "stale";
            return TypedResults.Ok(new HealthResponse(status, latest, age is null ? null : Math.Round(age.Value.TotalSeconds)));
        });

        // List all routes from static timetable for search and filter.
        api.MapGet("/routes", async (
            [FromServices] ReliabilityQueries queries,
            CancellationToken cancellationToken) =>
            TypedResults.Ok(await queries.AllRoutesAsync(cancellationToken)));

        // On-time performance per route for one service day (default: today in Brisbane), worst first.
        api.MapGet("/routes/on-time", async (
            DateOnly? date,
            [FromServices] ReliabilityQueries queries,
            [FromServices] OnTimePolicy policy,
            [FromServices] TimeProvider clock,
            CancellationToken cancellationToken) =>
            TypedResults.Ok(await queries.OnTimeByRouteAsync(
                date ?? BrisbaneTime.Today(clock), policy, clock.GetUtcNow(), cancellationToken)));

        // Daily on-time trend for one route over the last N days (default 7, max 31).
        api.MapGet("/routes/{routeId}/daily", async (
            string routeId,
            int? days,
            [FromServices] ReliabilityQueries queries,
            [FromServices] OnTimePolicy policy,
            [FromServices] TimeProvider clock,
            CancellationToken cancellationToken) =>
        {
            var to = BrisbaneTime.Today(clock);
            var from = to.AddDays(-(Math.Clamp(days ?? 7, 1, 31) - 1));
            return TypedResults.Ok(await queries.DailyOnTimeAsync(
                routeId, from, to, policy, clock.GetUtcNow(), cancellationToken));
        });

        // Stops on one route for a service day, worst on-time rate first.
        api.MapGet("/routes/{routeId}/stops", async (
            string routeId,
            DateOnly? date,
            [FromServices] ReliabilityQueries queries,
            [FromServices] OnTimePolicy policy,
            [FromServices] TimeProvider clock,
            CancellationToken cancellationToken) =>
            TypedResults.Ok(await queries.StopsByRouteAsync(
                routeId, date ?? BrisbaneTime.Today(clock), policy, clock.GetUtcNow(), cancellationToken)));

        // Summary performance for one route over N days (default 7, max 31), including percentiles.
        api.MapGet("/routes/{routeId}", async (
            string routeId,
            int? days,
            [FromServices] ReliabilityQueries queries,
            [FromServices] OnTimePolicy policy,
            [FromServices] TimeProvider clock,
            CancellationToken cancellationToken) =>
        {
            var to = BrisbaneTime.Today(clock);
            var from = to.AddDays(-(Math.Clamp(days ?? 7, 1, 31) - 1));
            var summary = await queries.RouteSummaryAsync(
                routeId, from, to, policy, clock.GetUtcNow(), cancellationToken);
            return summary is not null
                ? Results.Ok(summary)
                : Results.NotFound();
        });

        // Hourly on-time performance for one route for a service day, Brisbane time.
        api.MapGet("/routes/{routeId}/hours", async (
            string routeId,
            DateOnly? date,
            [FromServices] ReliabilityQueries queries,
            [FromServices] OnTimePolicy policy,
            [FromServices] TimeProvider clock,
            CancellationToken cancellationToken) =>
            TypedResults.Ok(await queries.RouteHourlyOnTimeAsync(
                routeId, date ?? BrisbaneTime.Today(clock), policy, clock.GetUtcNow(), cancellationToken)));

        // Stops with the highest average delay across South East Queensland, with sample size guard.
        api.MapGet("/stops/worst", async (
            DateOnly? date,
            int? limit,
            int? minObservations,
            [FromServices] ReliabilityQueries queries,
            [FromServices] OnTimePolicy policy,
            [FromServices] TimeProvider clock,
            CancellationToken cancellationToken) =>
            TypedResults.Ok(await queries.WorstStopsAsync(
                date ?? BrisbaneTime.Today(clock),
                policy,
                clock.GetUtcNow(),
                Math.Clamp(limit ?? 20, 1, 100),
                Math.Clamp(minObservations ?? 20, 1, 1000),
                cancellationToken)));

        // On-time performance aggregated by transport mode (Bus, Train, Ferry, Tram) for a service day.
        api.MapGet("/modes/on-time", async (
            DateOnly? date,
            [FromServices] ReliabilityQueries queries,
            [FromServices] OnTimePolicy policy,
            [FromServices] TimeProvider clock,
            CancellationToken cancellationToken) =>
            TypedResults.Ok(await queries.ModesOnTimeAsync(
                date ?? BrisbaneTime.Today(clock), policy, clock.GetUtcNow(), cancellationToken)));

        // Cancelled trips per route for one service day.
        api.MapGet("/cancellations", async (
            DateOnly? date,
            [FromServices] ReliabilityQueries queries,
            [FromServices] TimeProvider clock,
            CancellationToken cancellationToken) =>
            TypedResults.Ok(await queries.CancellationsByRouteAsync(date ?? BrisbaneTime.Today(clock), cancellationToken)));

        // Most recent loaded snapshots, for checking the pipeline.
        api.MapGet("/ingestion/snapshots", async (
            int? limit,
            [FromServices] ReliabilityQueries queries,
            CancellationToken cancellationToken) =>
            TypedResults.Ok(await queries.RecentSnapshotsAsync(Math.Clamp(limit ?? 20, 1, 200), cancellationToken)));

        return app;
    }
}
