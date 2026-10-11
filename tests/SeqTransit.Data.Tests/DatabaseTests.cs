using SeqTransit.Core.Ingestion;
using SeqTransit.Core.Model;
using SeqTransit.Core.Reliability;

namespace SeqTransit.Data.Tests;

[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class DatabaseTests(PostgresFixture database) : IAsyncLifetime
{
    private static readonly DateOnly Day = new(2026, 10, 11);
    private static readonly DateTimeOffset T0 = new(2026, 10, 11, 0, 0, 0, TimeSpan.Zero);

    public Task InitializeAsync() => database.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Migrations_do_nothing_when_already_applied()
    {
        var applied = await new SchemaMigrator(database.DataSource)
            .ApplyAsync(PostgresFixture.MigrationsDirectory, CancellationToken.None);

        Assert.Empty(applied);
    }

    [Fact]
    public async Task Saving_the_same_batch_twice_writes_once()
    {
        var writer = new ObservationWriter(database.DataSource);
        var batch = Batch(T0, [Observation(1, delay: 30, feedAt: T0)]);

        var first = await writer.SaveAsync(batch, CancellationToken.None);
        var second = await writer.SaveAsync(batch, CancellationToken.None);

        Assert.Equal(new SaveResult(1, 0, 1), first);
        Assert.Equal(new SaveResult(0, 0, 0), second);
        Assert.Equal(T0, await writer.GetLatestLoadedFeedTimestampAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Older_data_never_overwrites_newer_data()
    {
        var writer = new ObservationWriter(database.DataSource);
        await writer.SaveAsync(Batch(T0.AddMinutes(5), [Observation(1, delay: 90, feedAt: T0.AddMinutes(5))]), CancellationToken.None);

        var result = await writer.SaveAsync(Batch(T0, [Observation(1, delay: 10, feedAt: T0)]), CancellationToken.None);

        Assert.Equal(0, result.ObservationRowsWritten);
        await using var db = database.CreateDbContext();
        var row = Assert.Single(db.StopDelayObservations);
        Assert.Equal(90, row.ArrivalDelaySeconds);
    }

    [Fact]
    public async Task On_time_rate_counts_only_stops_already_passed()
    {
        var writer = new ObservationWriter(database.DataSource);
        await writer.SaveAsync(
            Batch(T0, [
                Observation(1, delay: 0, feedAt: T0, eventTime: T0.AddMinutes(-10)),    // on time
                Observation(2, delay: 400, feedAt: T0, eventTime: T0.AddMinutes(-5)),   // late
                Observation(3, delay: 30, feedAt: T0, eventTime: T0.AddMinutes(10)),    // not reached yet
            ]),
            CancellationToken.None);

        await using var db = database.CreateDbContext();
        var routes = await new ReliabilityQueries(db).OnTimeByRouteAsync(Day, OnTimePolicy.Default, T0, CancellationToken.None);

        var route = Assert.Single(routes);
        Assert.Equal("66-1", route.RouteId);
        Assert.Equal(2, route.ObservedStops);
        Assert.Equal(1, route.OnTimeStops);
        Assert.Equal(0.5, route.OnTimeRate);
        Assert.Equal(200, route.AverageDelaySeconds);
    }

    [Fact]
    public async Task Cancellations_are_counted_per_route()
    {
        var writer = new ObservationWriter(database.DataSource);
        await writer.SaveAsync(
            new LoadBatch(
                [],
                [
                    new TripCancellation(Day, "t1", "66-1", T0, T0),
                    new TripCancellation(Day, "t2", "66-1", T0, T0),
                    new TripCancellation(Day, "t3", "130-1", T0, T0),
                ],
                [new LoadedSnapshot(T0, "k", 3, 0, 3)]),
            CancellationToken.None);

        await using var db = database.CreateDbContext();
        var counts = await new ReliabilityQueries(db).CancellationsByRouteAsync(Day, CancellationToken.None);

        Assert.Equal(new[] { new RouteCancellations("66-1", 2), new RouteCancellations("130-1", 1) }, counts);
    }

    [Fact]
    public async Task PassedStops_excludes_absurd_delays_beyond_one_hour()
    {
        var writer = new ObservationWriter(database.DataSource);
        await writer.SaveAsync(
            Batch(T0, [
                Observation(1, delay: 0, feedAt: T0, eventTime: T0.AddMinutes(-10)),       // normal (on time)
                Observation(2, delay: 4000, feedAt: T0, eventTime: T0.AddMinutes(-5)),    // absurd delay > 3600s
                Observation(3, delay: -4000, feedAt: T0, eventTime: T0.AddMinutes(-5)),   // absurd early < -3600s
            ]),
            CancellationToken.None);

        await using var db = database.CreateDbContext();
        var routes = await new ReliabilityQueries(db).OnTimeByRouteAsync(Day, OnTimePolicy.Default, T0, CancellationToken.None);

        var route = Assert.Single(routes);
        Assert.Equal(1, route.ObservedStops);
        Assert.Equal(1, route.OnTimeStops);
        Assert.Equal(0, route.AverageDelaySeconds);
    }

    [Fact]
    public async Task StopsByRouteAsync_returns_performance_per_stop()
    {
        var writer = new ObservationWriter(database.DataSource);
        await writer.SaveAsync(
            Batch(T0, [
                Observation(1, delay: 0, feedAt: T0, eventTime: T0.AddMinutes(-10)),     // S1 on time
                Observation(2, delay: 400, feedAt: T0, eventTime: T0.AddMinutes(-5)),    // S2 late
            ]),
            CancellationToken.None);

        await using var db = database.CreateDbContext();
        var stops = await new ReliabilityQueries(db).StopsByRouteAsync("66-1", Day, OnTimePolicy.Default, T0, CancellationToken.None);

        Assert.Equal(2, stops.Count);
        // Sorted worst on-time rate first
        var s2 = stops[0];
        Assert.Equal("S2", s2.StopId);
        Assert.Equal(0.0, s2.OnTimeRate);
        Assert.Equal(400, s2.AverageDelaySeconds);

        var s1 = stops[1];
        Assert.Equal("S1", s1.StopId);
        Assert.Equal(1.0, s1.OnTimeRate);
        Assert.Equal(0, s1.AverageDelaySeconds);
    }

    [Fact]
    public async Task StaticGtfsLoader_loading_twice_is_idempotent_and_joins_names()
    {
        var package = new StaticGtfsPackage(
            [new GtfsRoute("66-1", "66", "RBWH - UQ Lakes", 3)],
            [new GtfsStop("S1", "UQ Lakes station", -27.497, 153.013)],
            [new GtfsTrip("trip-1", "66-1", "UQ Lakes", 0)]);

        var loader = new StaticGtfsLoader(database.DataSource);
        var first = await loader.LoadAsync(package, CancellationToken.None);
        var second = await loader.LoadAsync(package, CancellationToken.None);

        Assert.Equal(new StaticGtfsLoadResult(1, 1, 1), first);
        Assert.Equal(new StaticGtfsLoadResult(1, 1, 1), second);

        var writer = new ObservationWriter(database.DataSource);
        await writer.SaveAsync(
            Batch(T0, [
                Observation(1, delay: 0, feedAt: T0, eventTime: T0.AddMinutes(-10)),
            ]),
            CancellationToken.None);

        await using var db = database.CreateDbContext();
        var routes = await new ReliabilityQueries(db).OnTimeByRouteAsync(Day, OnTimePolicy.Default, T0, CancellationToken.None);
        var route = Assert.Single(routes);
        Assert.Equal("66-1", route.RouteId);
        Assert.Equal("66", route.RouteShortName);
        Assert.Equal("RBWH - UQ Lakes", route.RouteLongName);
        Assert.Equal(3, route.RouteType);

        var stops = await new ReliabilityQueries(db).StopsByRouteAsync("66-1", Day, OnTimePolicy.Default, T0, CancellationToken.None);
        var stop = Assert.Single(stops);
        Assert.Equal("S1", stop.StopId);
        Assert.Equal("UQ Lakes station", stop.StopName);
    }

    private static StopObservation Observation(int sequence, int delay, DateTimeOffset feedAt, DateTimeOffset? eventTime = null) =>
        new(Day, "trip-1", sequence, "66-1", $"S{sequence}", "V1", delay, null, eventTime ?? feedAt, feedAt, feedAt);

    private static LoadBatch Batch(DateTimeOffset feedAt, IReadOnlyCollection<StopObservation> observations) =>
        new(observations, [], [new LoadedSnapshot(feedAt, SnapshotKeys.For(feedAt), 1, observations.Count, 0)]);
}
