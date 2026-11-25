using Microsoft.AspNetCore.Http;
using SeqTransit.Api;

namespace SeqTransit.Api.Tests;

public class ApiCacheHeadersTests
{
    // 2026-11-25 12:00 Brisbane time.
    private static readonly DateTimeOffset Noon = new(2026, 11, 25, 12, 0, 0, TimeSpan.FromHours(10));

    [Theory]
    [InlineData("/api/health", null, 503)]
    [InlineData("/api/routes/on-time", "2026-11-01", 503)]
    [InlineData("/api/routes/on-time", "2026-11-01", 500)]
    public void Server_errors_are_never_cached(string path, string? date, int status)
    {
        Assert.Equal(ApiCacheHeaders.NoStore, ApiCacheHeaders.Choose(new PathString(path), date, status, Noon));
    }

    [Theory]
    [InlineData("/api/health")]
    [InlineData("/api/ingestion/snapshots")]
    public void Health_and_pipeline_status_are_cached_for_a_minute(string path)
    {
        Assert.Equal(ApiCacheHeaders.Status, ApiCacheHeaders.Choose(new PathString(path), null, 200, Noon));
    }

    [Theory]
    [InlineData("2026-11-24")] // yesterday, after 06:00 today
    [InlineData("2026-10-13")]
    public void Settled_days_are_cached_for_a_day(string date)
    {
        Assert.Equal(ApiCacheHeaders.Settled, ApiCacheHeaders.Choose(new PathString("/api/routes/on-time"), date, 200, Noon));
    }

    [Theory]
    [InlineData("2026-11-25")] // today
    [InlineData(null)]         // defaults to today
    [InlineData("not-a-date")]
    public void Todays_numbers_are_cached_briefly(string? date)
    {
        Assert.Equal(ApiCacheHeaders.Recent, ApiCacheHeaders.Choose(new PathString("/api/routes/on-time"), date, 200, Noon));
    }

    [Fact]
    public void Not_found_is_cached_briefly_even_for_a_settled_day()
    {
        Assert.Equal(ApiCacheHeaders.Recent, ApiCacheHeaders.Choose(new PathString("/api/routes/X"), "2026-10-13", 404, Noon));
    }

    [Fact]
    public void Yesterday_is_not_settled_until_six_in_the_morning()
    {
        var yesterday = new DateOnly(2026, 11, 24);

        Assert.False(ApiCacheHeaders.IsSettled(yesterday, new DateTimeOffset(2026, 11, 25, 5, 59, 0, TimeSpan.FromHours(10))));
        Assert.True(ApiCacheHeaders.IsSettled(yesterday, new DateTimeOffset(2026, 11, 25, 6, 0, 0, TimeSpan.FromHours(10))));
    }
}
