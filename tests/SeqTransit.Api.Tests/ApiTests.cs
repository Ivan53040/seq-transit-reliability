using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using SeqTransit.Core;
using SeqTransit.Core.Ingestion;
using SeqTransit.Core.Model;
using SeqTransit.Core.Reliability;
using SeqTransit.Data;
using SeqTransit.Data.Tests;

namespace SeqTransit.Api.Tests;

public sealed class SeqTransitApiFactory(PostgresFixture fixture) : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        Environment.SetEnvironmentVariable("DB_CONNECTION_STRING", fixture.DataSource.ConnectionString);
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<NpgsqlDataSource>();
            services.RemoveAll<DbContextOptions<TransitDbContext>>();
            services.RemoveAll<TransitDbContext>();

            services.AddSingleton(fixture.DataSource);
            services.AddDbContext<TransitDbContext>(options => options.UseNpgsql(fixture.DataSource));
        });
    }
}

[CollectionDefinition(Name)]
public sealed class PostgresApiCollection : ICollectionFixture<PostgresFixture>
{
    public const string Name = "postgres-api";
}

[Collection(PostgresApiCollection.Name)]
[Trait("Category", "Integration")]
public sealed class ApiTests : IAsyncLifetime
{
    // The API decides "today" in Brisbane time (UTC+10), so the seeded day must too; a UTC date
    // would fall on "yesterday" between 00:00 and 10:00 Brisbane time.
    private static readonly DateOnly Day = BrisbaneTime.Today(TimeProvider.System);
    private static readonly DateTimeOffset T0 = DateTimeOffset.UtcNow;

    private readonly PostgresFixture _fixture;
    private readonly SeqTransitApiFactory _factory;
    private readonly HttpClient _client;

    public ApiTests(PostgresFixture fixture)
    {
        _fixture = fixture;
        _factory = new SeqTransitApiFactory(fixture);
        _client = _factory.CreateClient();
    }

    public async Task InitializeAsync()
    {
        await _fixture.ResetAsync();

        // Seed static GTFS data
        var package = new StaticGtfsPackage(
            [
                new GtfsRoute("66-1", "66", "RBWH - UQ Lakes", 3),
                new GtfsRoute("QR-1", "BNIP", "Beenleigh - Ipswich", 2),
            ],
            [
                new GtfsStop("STOP-1", "UQ Lakes Station", -27.497, 153.013),
                new GtfsStop("STOP-2", "Roma Street Station", -27.465, 153.018),
            ],
            []);
        await new StaticGtfsLoader(_fixture.DataSource).LoadAsync(package, CancellationToken.None);

        // Seed real-time observations
        var writer = new ObservationWriter(_fixture.DataSource);
        var observations = new List<StopObservation>();
        for (var i = 1; i <= 25; i++)
        {
            observations.Add(new StopObservation(
                Day, "trip-1", i, "66-1", "STOP-1", "V1", 45, null, T0.AddMinutes(-i), T0, T0));
        }

        observations.Add(new StopObservation(
            Day, "trip-2", 1, "QR-1", "STOP-2", null, 10, null, T0.AddMinutes(-5), T0, T0));

        await writer.SaveAsync(
            new LoadBatch(
                observations,
                [new TripCancellation(Day, "cancelled-1", "66-1", T0, T0)],
                [new LoadedSnapshot(T0, SnapshotKeys.For(T0), 2, observations.Count, 1)]),
            CancellationToken.None);
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();
    }

    [Theory]
    [InlineData("/openapi/v1.json")]
    [InlineData("/api/openapi/v1.json")]
    public async Task OpenApi_endpoint_returns_valid_spec(string path)
    {
        var response = await _client.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        Assert.True(doc.RootElement.TryGetProperty("openapi", out var openApiVersion));
        Assert.StartsWith("3.", openApiVersion.GetString());
        Assert.True(doc.RootElement.TryGetProperty("paths", out var paths));
        Assert.True(paths.TryGetProperty("/api/routes/{routeId}", out _));
        Assert.True(paths.TryGetProperty("/api/modes/on-time", out _));
    }

    [Fact]
    public async Task Health_endpoint_returns_ok_status()
    {
        var response = await _client.GetAsync("/api/health");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var doc = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("ok", doc.GetProperty("status").GetString());
        Assert.True(doc.TryGetProperty("latestSnapshot", out _));
    }

    [Fact]
    public async Task Route_summary_endpoint_returns_honest_statistics_and_percentiles()
    {
        var response = await _client.GetAsync("/api/routes/66-1?days=1");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var doc = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("66-1", doc.GetProperty("routeId").GetString());
        Assert.Equal("66", doc.GetProperty("routeShortName").GetString());
        Assert.Equal("RBWH - UQ Lakes", doc.GetProperty("routeLongName").GetString());
        Assert.Equal(3, doc.GetProperty("routeType").GetInt32());
        Assert.Equal(25, doc.GetProperty("observedStops").GetInt32());
        Assert.Equal(25, doc.GetProperty("onTimeStops").GetInt32());
        Assert.Equal(1.0, doc.GetProperty("onTimeRate").GetDouble());
        Assert.Equal(45.0, doc.GetProperty("averageDelaySeconds").GetDouble());
        Assert.Equal(45.0, doc.GetProperty("medianDelaySeconds").GetDouble());
        Assert.Equal(1, doc.GetProperty("cancelledTrips").GetInt32());
    }

    [Fact]
    public async Task Route_summary_and_daily_trend_end_on_the_requested_date()
    {
        var earlier = Day.AddDays(-10);

        var summary = await _client.GetFromJsonAsync<JsonElement>($"/api/routes/66-1?days=7&date={earlier:yyyy-MM-dd}");
        Assert.Equal($"{earlier:yyyy-MM-dd}", summary.GetProperty("toDate").GetString());
        Assert.Equal(0, summary.GetProperty("observedStops").GetInt32());
        Assert.Equal(JsonValueKind.Null, summary.GetProperty("onTimeRate").ValueKind);

        var daily = await _client.GetFromJsonAsync<JsonElement>($"/api/routes/66-1/daily?days=7&date={earlier:yyyy-MM-dd}");
        Assert.Equal(0, daily.GetArrayLength());
    }

    [Fact]
    public async Task Route_summary_returns_not_found_for_unknown_route()
    {
        var response = await _client.GetAsync("/api/routes/NON_EXISTENT_ROUTE?days=1");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Route_hourly_endpoint_returns_hourly_shape()
    {
        var response = await _client.GetAsync($"/api/routes/66-1/hours?date={Day:yyyy-MM-dd}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var doc = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(JsonValueKind.Array, doc.ValueKind);
        Assert.True(doc.GetArrayLength() > 0);

        var first = doc[0];
        Assert.True(first.TryGetProperty("hour", out _));
        Assert.True(first.TryGetProperty("observedStops", out _));
        Assert.True(first.TryGetProperty("onTimeRate", out _));
        Assert.True(first.TryGetProperty("averageDelaySeconds", out _));
    }

    [Fact]
    public async Task Worst_stops_endpoint_returns_stops_meeting_min_observations()
    {
        var response = await _client.GetAsync($"/api/stops/worst?date={Day:yyyy-MM-dd}&limit=10&minObservations=20");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var doc = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(JsonValueKind.Array, doc.ValueKind);
        var stop = Assert.Single(doc.EnumerateArray());

        Assert.Equal("STOP-1", stop.GetProperty("stopId").GetString());
        Assert.Equal("UQ Lakes Station", stop.GetProperty("stopName").GetString());
        Assert.Equal(25, stop.GetProperty("observedStops").GetInt32());
        Assert.Equal(45.0, stop.GetProperty("averageDelaySeconds").GetDouble());
    }

    [Fact]
    public async Task Modes_on_time_endpoint_returns_modes_breakdown()
    {
        var response = await _client.GetAsync($"/api/modes/on-time?date={Day:yyyy-MM-dd}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var doc = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(JsonValueKind.Array, doc.ValueKind);
        Assert.Equal(2, doc.GetArrayLength());

        var modes = doc.EnumerateArray().ToList();
        Assert.Contains(modes, m => m.GetProperty("modeName").GetString() == "Bus" && m.GetProperty("routeType").GetInt32() == 3);
        Assert.Contains(modes, m => m.GetProperty("modeName").GetString() == "Train" && m.GetProperty("routeType").GetInt32() == 2);
    }

    [Fact]
    public async Task Latest_summary_endpoint_returns_404_when_empty_and_200_after_save()
    {
        var missing = await _client.GetAsync("/api/summaries/latest");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);

        var store = new WeeklySummaryStore(_fixture.DataSource);
        var figures = await store.ComputeWeeklyFiguresAsync(Day, OnTimePolicy.Default, T0, CancellationToken.None);
        await store.SaveAsync(Day, "Test weekly summary.", figures, "amazon.nova-micro-v1:0", T0, CancellationToken.None);

        var found = await _client.GetAsync("/api/summaries/latest");
        Assert.Equal(HttpStatusCode.OK, found.StatusCode);

        var doc = await found.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Test weekly summary.", doc.GetProperty("summaryText").GetString());
        Assert.Equal("amazon.nova-micro-v1:0", doc.GetProperty("modelId").GetString());
        Assert.Equal(26, doc.GetProperty("inputFigures").GetProperty("totalObservedStops").GetInt32());
    }

    [Fact]
    public async Task Board_endpoints_return_todays_routes_and_cancellations()
    {
        var routes = await _client.GetFromJsonAsync<JsonElement>($"/api/routes/on-time?date={Day:yyyy-MM-dd}");
        Assert.Equal(2, routes.GetArrayLength());
        var bus = routes.EnumerateArray().Single(r => r.GetProperty("routeId").GetString() == "66-1");
        Assert.Equal(25, bus.GetProperty("observedStops").GetInt32());
        Assert.Equal(1.0, bus.GetProperty("onTimeRate").GetDouble());

        var cancellations = await _client.GetFromJsonAsync<JsonElement>($"/api/cancellations?date={Day:yyyy-MM-dd}");
        var cancelled = Assert.Single(cancellations.EnumerateArray());
        Assert.Equal("66-1", cancelled.GetProperty("routeId").GetString());
        Assert.Equal(1, cancelled.GetProperty("cancelledTrips").GetInt32());
    }

    [Fact]
    public async Task Route_list_and_snapshot_list_return_seeded_rows()
    {
        var routes = await _client.GetFromJsonAsync<JsonElement>("/api/routes");
        Assert.Equal(2, routes.GetArrayLength());

        var snapshots = await _client.GetFromJsonAsync<JsonElement>("/api/ingestion/snapshots?limit=500");
        var snapshot = Assert.Single(snapshots.EnumerateArray());
        Assert.Equal(1, snapshot.GetProperty("cancelledTripCount").GetInt32());
    }

    [Fact]
    public async Task Day_ranges_are_clamped_to_31_days()
    {
        var summary = await _client.GetFromJsonAsync<JsonElement>($"/api/routes/66-1?days=365&date={Day:yyyy-MM-dd}");
        Assert.Equal($"{Day.AddDays(-30):yyyy-MM-dd}", summary.GetProperty("fromDate").GetString());
    }

    [Fact]
    public async Task Responses_carry_cache_headers_for_cloudfront()
    {
        var health = await _client.GetAsync("/api/health");
        Assert.Equal(TimeSpan.FromMinutes(1), health.Headers.CacheControl?.MaxAge);

        var today = await _client.GetAsync($"/api/routes/on-time?date={Day:yyyy-MM-dd}");
        Assert.Equal(TimeSpan.FromMinutes(5), today.Headers.CacheControl?.MaxAge);

        var settled = await _client.GetAsync($"/api/routes/on-time?date={Day.AddDays(-10):yyyy-MM-dd}");
        Assert.Equal(TimeSpan.FromDays(1), settled.Headers.CacheControl?.MaxAge);
    }

    [Fact]
    public async Task Requests_without_the_cloudfront_header_are_rejected_when_a_secret_is_set()
    {
        using var guarded = _factory.WithWebHostBuilder(b => b.UseSetting(OriginVerification.SettingName, "test-secret"));
        using var client = guarded.CreateClient();

        var direct = await client.GetAsync("/api/health");
        Assert.Equal(HttpStatusCode.Forbidden, direct.StatusCode);
        Assert.True(direct.Headers.CacheControl?.NoStore);

        using var wrong = new HttpRequestMessage(HttpMethod.Get, "/api/health");
        wrong.Headers.Add(OriginVerification.HeaderName, "guess");
        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(wrong)).StatusCode);

        using var viaCloudFront = new HttpRequestMessage(HttpMethod.Get, "/api/health");
        viaCloudFront.Headers.Add(OriginVerification.HeaderName, "test-secret");
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(viaCloudFront)).StatusCode);
    }
}
