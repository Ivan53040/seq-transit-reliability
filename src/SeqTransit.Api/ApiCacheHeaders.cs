using SeqTransit.Core;

namespace SeqTransit.Api;

/// <summary>
/// Sets Cache-Control on every /api response so CloudFront can keep settled days for a long time
/// and still refresh today's numbers soon after each load.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>Health, pipeline status and every 5xx: <c>no-store</c>. A cached 503 would defeat the
/// dashboard's retry while Aurora resumes.</item>
/// <item>A <c>date</c> whose service day has settled: one day. Trips of a service day can run past
/// midnight and are loaded up to one load interval later, so a day counts as settled from 06:00
/// Brisbane time on the next day.</item>
/// <item>Everything else (today, static lists, 4xx): five minutes.</item>
/// </list>
/// </remarks>
public static class ApiCacheHeaders
{
    public const string NoStore = "no-store";
    public const string Settled = "public, max-age=86400";
    public const string Recent = "public, max-age=300";

    private static readonly TimeSpan SettledAfterMidnight = TimeSpan.FromHours(6);

    private static readonly string[] UncachedPaths = ["/api/health", "/api/ingestion/snapshots"];

    public static IApplicationBuilder UseApiCacheHeaders(this IApplicationBuilder app) =>
        app.Use(async (context, next) =>
        {
            if (context.Request.Path.StartsWithSegments("/api"))
            {
                context.Response.OnStarting(() =>
                {
                    var clock = context.RequestServices.GetRequiredService<TimeProvider>();
                    context.Response.Headers.CacheControl = Choose(
                        context.Request.Path,
                        context.Request.Query["date"].ToString(),
                        context.Response.StatusCode,
                        clock.GetUtcNow());
                    return Task.CompletedTask;
                });
            }

            await next(context);
        });

    /// <summary>Picks the Cache-Control value for one response. Public for unit tests.</summary>
    public static string Choose(PathString path, string? date, int statusCode, DateTimeOffset now)
    {
        if (statusCode >= 500 || UncachedPaths.Any(p => path.StartsWithSegments(p)))
        {
            return NoStore;
        }

        if (statusCode == StatusCodes.Status200OK
            && DateOnly.TryParseExact(date, "yyyy-MM-dd", out var serviceDate)
            && IsSettled(serviceDate, now))
        {
            return Settled;
        }

        return Recent;
    }

    /// <summary>True once no more observations can arrive for the service day.</summary>
    public static bool IsSettled(DateOnly serviceDate, DateTimeOffset now)
    {
        var nextMidnightBrisbane = new DateTimeOffset(serviceDate.AddDays(1).ToDateTime(TimeOnly.MinValue), BrisbaneTime.Offset);
        return now >= nextMidnightBrisbane + SettledAfterMidnight;
    }
}
