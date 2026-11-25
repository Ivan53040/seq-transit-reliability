using Microsoft.EntityFrameworkCore;
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
    public async Task A_reinstated_trip_is_no_longer_counted_as_cancelled()
    {
        var writer = new ObservationWriter(database.DataSource);
        await writer.SaveAsync(
            new LoadBatch([], [new TripCancellation(Day, "t1", "66-1", T0, T0)], [new LoadedSnapshot(T0, "k1", 1, 0, 1)]),
            CancellationToken.None);

        // A later batch sees the trip running again, and an older sighting must not undo a newer cancellation.
        var later = T0.AddMinutes(5);
        await writer.SaveAsync(
            new LoadBatch(
                [],
                [new TripCancellation(Day, "t2", "66-1", later, later)],
                [new LoadedSnapshot(later, "k2", 2, 0, 1)],
                [new TripRunning(Day, "t1", later), new TripRunning(Day, "t2", T0)]),
            CancellationToken.None);

        await using var db = database.CreateDbContext();
        var counts = await new ReliabilityQueries(db).CancellationsByRouteAsync(Day, CancellationToken.None);

        Assert.Equal(new[] { new RouteCancellations("66-1", 1) }, counts);
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
    public async Task PassedStops_retains_untracked_trips_and_falls_back_to_departure_delay()
    {
        var writer = new ObservationWriter(database.DataSource);
        await writer.SaveAsync(Batch(T0, [
            Observation(1, delay: 60, feedAt: T0, eventTime: T0.AddMinutes(-5)) with
            { VehicleId = null, ArrivalDelaySeconds = null, DepartureDelaySeconds = 60 },
            Observation(2, delay: 400, feedAt: T0, eventTime: T0.AddMinutes(-5)) with { VehicleId = null },
        ]), CancellationToken.None);

        await using var db = database.CreateDbContext();
        var route = Assert.Single(await new ReliabilityQueries(db)
            .OnTimeByRouteAsync(Day, OnTimePolicy.Default, T0, CancellationToken.None));
        Assert.Equal(2, route.ObservedStops);
        Assert.Equal(1, route.OnTimeStops);
        Assert.Equal(230, route.AverageDelaySeconds);
    }

    [Fact]
    public async Task PassedStops_requires_time_and_delay_and_includes_one_hour_boundaries()
    {
        var writer = new ObservationWriter(database.DataSource);
        await writer.SaveAsync(Batch(T0, [
            Observation(1, delay: -3600, feedAt: T0, eventTime: T0.AddMinutes(-5)),
            Observation(2, delay: 3600, feedAt: T0, eventTime: T0.AddMinutes(-5)),
            Observation(3, delay: -3601, feedAt: T0, eventTime: T0.AddMinutes(-5)),
            Observation(4, delay: 3601, feedAt: T0, eventTime: T0.AddMinutes(-5)),
            Observation(5, delay: 0, feedAt: T0) with { EventTime = null },
            Observation(6, delay: 0, feedAt: T0, eventTime: T0.AddMinutes(-5)) with { ArrivalDelaySeconds = null },
        ]), CancellationToken.None);

        await using var db = database.CreateDbContext();
        var route = Assert.Single(await new ReliabilityQueries(db)
            .OnTimeByRouteAsync(Day, OnTimePolicy.Default, T0, CancellationToken.None));
        Assert.Equal(2, route.ObservedStops);
        Assert.Equal(0, route.OnTimeStops);
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

    [Fact]
    public async Task RouteSummaryAsync_returns_percentiles_and_cancellations()
    {
        var package = new StaticGtfsPackage(
            [new GtfsRoute("66-1", "66", "RBWH - UQ Lakes", 3)],
            [],
            []);
        await new StaticGtfsLoader(database.DataSource).LoadAsync(package, CancellationToken.None);

        var writer = new ObservationWriter(database.DataSource);
        await writer.SaveAsync(
            new LoadBatch(
                [
                    Observation(1, delay: 0, feedAt: T0, eventTime: T0.AddMinutes(-20)),
                    Observation(2, delay: 100, feedAt: T0, eventTime: T0.AddMinutes(-15)),
                    Observation(3, delay: 200, feedAt: T0, eventTime: T0.AddMinutes(-10)),
                    Observation(4, delay: 400, feedAt: T0, eventTime: T0.AddMinutes(-5)), // late
                ],
                [
                    new TripCancellation(Day, "trip-cancelled", "66-1", T0, T0),
                ],
                [new LoadedSnapshot(T0, SnapshotKeys.For(T0), 1, 4, 1)]),
            CancellationToken.None);

        await using var db = database.CreateDbContext();
        var summary = await new ReliabilityQueries(db).RouteSummaryAsync("66-1", Day, Day, OnTimePolicy.Default, T0, CancellationToken.None);

        Assert.NotNull(summary);
        Assert.Equal("66-1", summary.RouteId);
        Assert.Equal("66", summary.RouteShortName);
        Assert.Equal(4, summary.ObservedStops);
        Assert.Equal(3, summary.OnTimeStops);
        Assert.Equal(0.75, summary.OnTimeRate);
        Assert.Equal(175.0, summary.AverageDelaySeconds);
        Assert.Equal(150.0, summary.MedianDelaySeconds);
        Assert.True(summary.P90DelaySeconds >= 200);
        Assert.Equal(1, summary.CancelledTrips);
    }

    [Fact]
    public async Task RouteHourlyOnTimeAsync_groups_by_hour_in_brisbane_time()
    {
        // Brisbane is UTC+10.
        // 08:30 Brisbane time on 2026-10-11 is 2026-10-10 22:30 UTC.
        // 17:15 Brisbane time on 2026-10-11 is 2026-10-11 07:15 UTC.
        var tBrisbaneMorningUtc = new DateTimeOffset(2026, 10, 10, 22, 30, 0, TimeSpan.Zero);
        var tBrisbaneEveningUtc = new DateTimeOffset(2026, 10, 11, 7, 15, 0, TimeSpan.Zero);
        var tQuery = new DateTimeOffset(2026, 10, 11, 10, 0, 0, TimeSpan.Zero);

        var writer = new ObservationWriter(database.DataSource);
        await writer.SaveAsync(
            Batch(tQuery, [
                Observation(1, delay: 30, feedAt: tQuery, eventTime: tBrisbaneMorningUtc),
                Observation(2, delay: 350, feedAt: tQuery, eventTime: tBrisbaneMorningUtc.AddMinutes(5)),
                Observation(3, delay: 10, feedAt: tQuery, eventTime: tBrisbaneEveningUtc),
            ]),
            CancellationToken.None);

        await using var db = database.CreateDbContext();
        var hourly = await new ReliabilityQueries(db).RouteHourlyOnTimeAsync("66-1", Day, OnTimePolicy.Default, tQuery, CancellationToken.None);

        Assert.Equal(2, hourly.Count);
        var h8 = Assert.Single(hourly, h => h.Hour == 8);
        Assert.Equal(2, h8.ObservedStops);
        Assert.Equal(1, h8.OnTimeStops);
        Assert.Equal(0.5, h8.OnTimeRate);
        Assert.Equal(190.0, h8.AverageDelaySeconds);

        var h17 = Assert.Single(hourly, h => h.Hour == 17);
        Assert.Equal(1, h17.ObservedStops);
        Assert.Equal(1, h17.OnTimeStops);
        Assert.Equal(1.0, h17.OnTimeRate);
        Assert.Equal(10.0, h17.AverageDelaySeconds);
    }

    [Fact]
    public async Task WorstStopsAsync_filters_by_sample_size_and_orders_by_delay()
    {
        var package = new StaticGtfsPackage(
            [],
            [
                new GtfsStop("STOP-BAD", "Congested Junction", -27.4, 153.0),
                new GtfsStop("STOP-GOOD", "Smooth Station", -27.5, 153.1),
                new GtfsStop("STOP-FEW", "Rare Stop", -27.6, 153.2),
            ],
            []);
        await new StaticGtfsLoader(database.DataSource).LoadAsync(package, CancellationToken.None);

        var observations = new List<StopObservation>();
        // STOP-BAD: 25 observations with 300s delay
        for (var i = 1; i <= 25; i++)
        {
            observations.Add(Observation(i, delay: 300, feedAt: T0, eventTime: T0.AddMinutes(-i), stopId: "STOP-BAD"));
        }
        // STOP-GOOD: 25 observations with 10s delay
        for (var i = 1; i <= 25; i++)
        {
            observations.Add(Observation(i + 50, delay: 10, feedAt: T0, eventTime: T0.AddMinutes(-i), stopId: "STOP-GOOD"));
        }
        // STOP-FEW: only 3 observations with 900s delay (should be filtered out by minObservations=20)
        for (var i = 1; i <= 3; i++)
        {
            observations.Add(Observation(i + 100, delay: 900, feedAt: T0, eventTime: T0.AddMinutes(-i), stopId: "STOP-FEW"));
        }

        var writer = new ObservationWriter(database.DataSource);
        await writer.SaveAsync(Batch(T0, observations), CancellationToken.None);

        await using var db = database.CreateDbContext();
        var worst = await new ReliabilityQueries(db).WorstStopsAsync(Day, OnTimePolicy.Default, T0, limit: 10, minObservations: 20, CancellationToken.None);

        // STOP-FEW must be excluded because it has only 3 observations < 20 minObservations
        Assert.Equal(2, worst.Count);
        Assert.Equal("STOP-BAD", worst[0].StopId);
        Assert.Equal("Congested Junction", worst[0].StopName);
        Assert.Equal(300.0, worst[0].AverageDelaySeconds);
        Assert.Equal(25, worst[0].ObservedStops);

        Assert.Equal("STOP-GOOD", worst[1].StopId);
        Assert.Equal("Smooth Station", worst[1].StopName);
        Assert.Equal(10.0, worst[1].AverageDelaySeconds);
        Assert.Equal(25, worst[1].ObservedStops);
    }

    [Fact]
    public async Task ModesOnTimeAsync_groups_by_route_type()
    {
        var package = new StaticGtfsPackage(
            [
                new GtfsRoute("TRAIN-1", "BNIP", "Beenleigh - Ipswich", 2),
                new GtfsRoute("BUS-1", "66", "RBWH - UQ", 3),
            ],
            [],
            []);
        await new StaticGtfsLoader(database.DataSource).LoadAsync(package, CancellationToken.None);

        var writer = new ObservationWriter(database.DataSource);
        await writer.SaveAsync(
            Batch(T0, [
                Observation(1, delay: 0, feedAt: T0, eventTime: T0.AddMinutes(-10), routeId: "TRAIN-1"),
                Observation(2, delay: 400, feedAt: T0, eventTime: T0.AddMinutes(-5), routeId: "BUS-1"),
            ]),
            CancellationToken.None);

        await using var db = database.CreateDbContext();
        var modes = await new ReliabilityQueries(db).ModesOnTimeAsync(Day, OnTimePolicy.Default, T0, CancellationToken.None);

        Assert.Equal(2, modes.Count);
        var train = Assert.Single(modes, m => m.RouteType == 2);
        Assert.Equal("Train", train.ModeName);
        Assert.Equal(1, train.ObservedStops);
        Assert.Equal(1, train.OnTimeStops);
        Assert.Equal(1.0, train.OnTimeRate);

        var bus = Assert.Single(modes, m => m.RouteType == 3);
        Assert.Equal("Bus", bus.ModeName);
        Assert.Equal(1, bus.ObservedStops);
        Assert.Equal(0, bus.OnTimeStops);
        Assert.Equal(0.0, bus.OnTimeRate);
    }

    [Fact]
    public async Task WeeklySummaryStore_computes_sql_figures_and_saves_summary()
    {
        var package = new StaticGtfsPackage(
            [
                new GtfsRoute("BEST-1", "196", "New Farm - Fairfield", 3),
                new GtfsRoute("WORST-1", "574", "Loganholme - Springwood", 3),
            ],
            [],
            []);
        await new StaticGtfsLoader(database.DataSource).LoadAsync(package, CancellationToken.None);

        var writer = new ObservationWriter(database.DataSource);
        await writer.SaveAsync(
            Batch(T0, [
                Observation(1, delay: 0, feedAt: T0, eventTime: T0.AddMinutes(-20), routeId: "BEST-1"),
                Observation(2, delay: 30, feedAt: T0, eventTime: T0.AddMinutes(-15), routeId: "BEST-1"),
                Observation(3, delay: 420, feedAt: T0, eventTime: T0.AddMinutes(-10), routeId: "WORST-1"),
            ]),
            CancellationToken.None);

        var store = new WeeklySummaryStore(database.DataSource);
        var figures = await store.ComputeWeeklyFiguresAsync(Day, OnTimePolicy.Default, T0, CancellationToken.None);

        Assert.Equal(3, figures.TotalObservedStops);
        Assert.Equal(2, figures.TotalRoutes);
        // The week is still in progress at T0, so it is reported up to that day, not to Sunday.
        Assert.Equal(Day, figures.WeekStart);
        Assert.Equal(SeqTransit.Core.BrisbaneTime.DateOf(T0), figures.WeekEnd);
        Assert.Equal(66.7, figures.OnTimePercent);
        Assert.NotNull(figures.BestRoute);
        Assert.Equal("196", figures.BestRoute.RouteShortName);
        Assert.NotNull(figures.WorstRoute);
        Assert.Equal("574", figures.WorstRoute.RouteShortName);
        Assert.NotNull(figures.WorstHour);

        const string text = "Across 2 routes and 3 stops, 66.7% ran on time.";
        await store.SaveAsync(Day, text, figures, "amazon.nova-micro-v1:0", T0, CancellationToken.None);

        await using var db = database.CreateDbContext();
        var latest = await new ReliabilityQueries(db).LatestWeeklySummaryAsync(CancellationToken.None);
        Assert.NotNull(latest);
        Assert.Equal(Day, latest.WeekStart);
        Assert.Equal(text, latest.SummaryText);
        Assert.Equal("amazon.nova-micro-v1:0", latest.ModelId);
        Assert.Equal(3, latest.InputFigures.GetProperty("totalObservedStops").GetInt32());
    }

    [Fact]
    public async Task RouteSummaryAsync_reports_no_rate_when_nothing_was_observed()
    {
        await new StaticGtfsLoader(database.DataSource).LoadAsync(
            new StaticGtfsPackage([new GtfsRoute("66-1", "66", "RBWH - UQ Lakes", 3)], [], []),
            CancellationToken.None);

        await using var db = database.CreateDbContext();
        var summary = await new ReliabilityQueries(db).RouteSummaryAsync("66-1", Day, Day, OnTimePolicy.Default, T0, CancellationToken.None);

        Assert.NotNull(summary);
        Assert.Equal(0, summary.ObservedStops);
        Assert.Null(summary.OnTimeRate);
        Assert.Null(summary.MedianDelaySeconds);
        Assert.Null(summary.P90DelaySeconds);
    }

    [Fact]
    public async Task Routes_missing_from_the_timetable_are_reported_as_unknown_mode()
    {
        var writer = new ObservationWriter(database.DataSource);
        await writer.SaveAsync(
            Batch(T0, [Observation(1, delay: 0, feedAt: T0, eventTime: T0.AddMinutes(-10), routeId: "ADDED-1")]),
            CancellationToken.None);

        await using var db = database.CreateDbContext();
        var queries = new ReliabilityQueries(db);

        var route = Assert.Single(await queries.OnTimeByRouteAsync(Day, OnTimePolicy.Default, T0, CancellationToken.None));
        Assert.Equal(ReliabilityQueries.UnknownRouteType, route.RouteType);

        var mode = Assert.Single(await queries.ModesOnTimeAsync(Day, OnTimePolicy.Default, T0, CancellationToken.None));
        Assert.Equal(ReliabilityQueries.UnknownRouteType, mode.RouteType);
        Assert.Equal("Unknown", mode.ModeName);
    }

    private static StopObservation Observation(
        int sequence,
        int delay,
        DateTimeOffset feedAt,
        DateTimeOffset? eventTime = null,
        string routeId = "66-1",
        string? stopId = null,
        DateOnly? serviceDate = null) =>
        new(serviceDate ?? Day, "trip-1", sequence, routeId, stopId ?? $"S{sequence}", "V1", delay, null, eventTime ?? feedAt, feedAt, feedAt);

    private static LoadBatch Batch(DateTimeOffset feedAt, IReadOnlyCollection<StopObservation> observations) =>
        new(observations, [], [new LoadedSnapshot(feedAt, SnapshotKeys.For(feedAt), 1, observations.Count, 0)]);

    [Fact]
    public async Task Only_one_load_lock_can_be_held_at_a_time()
    {
        var first = await LoadLock.TryAcquireAsync(database.DataSource, CancellationToken.None);
        Assert.NotNull(first);

        Assert.Null(await LoadLock.TryAcquireAsync(database.DataSource, CancellationToken.None));

        await first.DisposeAsync();
        await using var again = await LoadLock.TryAcquireAsync(database.DataSource, CancellationToken.None);
        Assert.NotNull(again);
    }

    [Fact]
    public async Task RouteHourlyOnTimeAsync_counts_trips_past_midnight_as_hour_24_and_later()
    {
        // 00:40 Brisbane time on 2026-10-12 is still service day 2026-10-11 (Day): hour 24, not hour 0.
        var afterMidnightUtc = new DateTimeOffset(2026, 10, 11, 14, 40, 0, TimeSpan.Zero);
        var tQuery = new DateTimeOffset(2026, 10, 11, 20, 0, 0, TimeSpan.Zero);

        var writer = new ObservationWriter(database.DataSource);
        await writer.SaveAsync(
            Batch(tQuery, [Observation(1, delay: 30, feedAt: tQuery, eventTime: afterMidnightUtc)]),
            CancellationToken.None);

        await using var db = database.CreateDbContext();
        var hourly = await new ReliabilityQueries(db).RouteHourlyOnTimeAsync("66-1", Day, OnTimePolicy.Default, tQuery, CancellationToken.None);

        Assert.Equal(24, Assert.Single(hourly).Hour);
    }

    [Fact]
    public async Task Delay_column_is_generated_from_arrival_then_departure_delay()
    {
        await using var command = database.DataSource.CreateCommand(
            "SELECT attgenerated FROM pg_attribute WHERE attrelid = 'stop_delay_observation'::regclass AND attname = 'delay_s'");
        Assert.Equal('s', (char)await command.ExecuteScalarAsync());
    }

    [Fact]
    public async Task Archiving_a_day_keeps_its_figures_and_removes_its_detail()
    {
        // Four stops on one route: on time, late, on time, and one with an untrusted delay.
        var writer = new ObservationWriter(database.DataSource);
        await writer.SaveAsync(
            Batch(T0, [
                Observation(1, delay: 30, feedAt: T0),
                Observation(2, delay: 400, feedAt: T0),
                Observation(3, delay: 60, feedAt: T0, stopId: "STOP-2"),
                Observation(4, delay: 5000, feedAt: T0),
            ]),
            CancellationToken.None);

        async Task<object> Snapshot()
        {
            await using var db = database.CreateDbContext();
            var q = new ReliabilityQueries(db);
            var routes = await q.OnTimeByRouteAsync(Day, OnTimePolicy.Default, T0.AddDays(1), CancellationToken.None);
            var stops = await q.StopsByRouteAsync("66-1", Day, OnTimePolicy.Default, T0.AddDays(1), CancellationToken.None);
            var daily = await q.DailyOnTimeAsync("66-1", Day.AddDays(-6), Day, OnTimePolicy.Default, T0.AddDays(1), CancellationToken.None);
            var worst = await q.WorstStopsAsync(Day, OnTimePolicy.Default, T0.AddDays(1), 10, 1, CancellationToken.None);
            var modes = await q.ModesOnTimeAsync(Day, OnTimePolicy.Default, T0.AddDays(1), CancellationToken.None);
            var summary = await q.RouteSummaryAsync("66-1", Day, Day, OnTimePolicy.Default, T0.AddDays(1), CancellationToken.None);
            return System.Text.Json.JsonSerializer.Serialize(new
            {
                routes,
                stops = stops.OrderBy(s => s.StopId),
                daily,
                worst = worst.OrderBy(w => w.StopId),
                modes,
                summary!.ObservedStops,
                summary.OnTimeStops,
                summary.OnTimeRate,
                summary.AverageDelaySeconds,
            });
        }

        var before = await Snapshot();

        var result = await new DetailArchiver(database.DataSource)
            .ArchiveBeforeAsync(Day.AddDays(1), OnTimePolicy.Default, maxDays: 5, CancellationToken.None);

        Assert.Equal([Day], result.ArchivedDays);
        Assert.Equal(4, result.DetailRowsDeleted);
        Assert.Equal(before, await Snapshot());

        await using var db = database.CreateDbContext();
        Assert.Equal(0, await db.StopDelayObservations.CountAsync());
        var summaryAfter = await new ReliabilityQueries(db)
            .RouteSummaryAsync("66-1", Day, Day, OnTimePolicy.Default, T0.AddDays(1), CancellationToken.None);
        Assert.Null(summaryAfter!.MedianDelaySeconds);
    }

    [Fact]
    public async Task Archiving_leaves_days_inside_the_retention_window_alone()
    {
        var writer = new ObservationWriter(database.DataSource);
        await writer.SaveAsync(Batch(T0, [Observation(1, delay: 30, feedAt: T0)]), CancellationToken.None);

        var result = await new DetailArchiver(database.DataSource)
            .ArchiveBeforeAsync(Day, OnTimePolicy.Default, maxDays: 5, CancellationToken.None);

        Assert.Empty(result.ArchivedDays);
        await using var db = database.CreateDbContext();
        Assert.Equal(1, await db.StopDelayObservations.CountAsync());
    }
}
