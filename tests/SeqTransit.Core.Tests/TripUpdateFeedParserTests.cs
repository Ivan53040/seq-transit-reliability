using SeqTransit.Core.Gtfs;
using SeqTransit.Core.Tests.TestSupport;
using StopRelationship = TransitRealtime.TripUpdate.Types.StopTimeUpdate.Types.ScheduleRelationship;
using TripRelationship = TransitRealtime.TripDescriptor.Types.ScheduleRelationship;

namespace SeqTransit.Core.Tests;

public class TripUpdateFeedParserTests
{
    private static readonly DateTimeOffset FeedTime = new(2026, 10, 11, 0, 30, 0, TimeSpan.Zero);

    [Fact]
    public void Extracts_delays_for_each_stop_of_a_trip()
    {
        var bytes = new FeedBuilder()
            .WithTimestamp(FeedTime)
            .WithTrip("trip-1", [
                new(Sequence: 1, StopId: "S1", ArrivalDelay: 30, ArrivalTime: FeedTime.AddMinutes(-2).ToUnixTimeSeconds()),
                new(Sequence: 2, StopId: "S2", DepartureDelay: 120),
            ])
            .BuildBytes();

        var message = TripUpdateFeedParser.Parse(bytes);
        var snapshot = TripUpdateFeedParser.Extract(message, TripUpdateFeedParser.FeedTimestamp(message, DateTimeOffset.MinValue));

        Assert.Equal(FeedTime, snapshot.FeedTimestamp);
        Assert.Equal(1, snapshot.TripUpdateCount);
        Assert.Equal(2, snapshot.Observations.Count);

        var first = snapshot.Observations[0];
        Assert.Equal(new DateOnly(2026, 10, 11), first.ServiceDate);
        Assert.Equal("trip-1", first.TripId);
        Assert.Equal(1, first.StopSequence);
        Assert.Equal("66-1", first.RouteId);
        Assert.Equal("S1", first.StopId);
        Assert.Equal("V1", first.VehicleId);
        Assert.Equal(30, first.ArrivalDelaySeconds);
        Assert.Equal(30, first.DelaySeconds);
        Assert.Equal(FeedTime.AddMinutes(-2), first.EventTime);
        Assert.Equal(FeedTime, first.FirstSeenAt);
        Assert.Equal(FeedTime, first.LastFeedAt);

        var second = snapshot.Observations[1];
        Assert.Null(second.ArrivalDelaySeconds);
        Assert.Equal(120, second.DepartureDelaySeconds);
        Assert.Equal(120, second.DelaySeconds);
        Assert.Null(second.EventTime);
    }

    [Fact]
    public void Skips_stop_updates_that_carry_no_usable_prediction()
    {
        var message = new FeedBuilder()
            .WithTimestamp(FeedTime)
            .WithTrip("trip-1", [
                new(Sequence: 1, ArrivalDelay: 10),
                new(Sequence: 2, ArrivalDelay: 10, Relationship: StopRelationship.Skipped),
                new(Sequence: 3, Relationship: StopRelationship.NoData),
                new(Sequence: null, ArrivalDelay: 10),
                new(Sequence: 5),
            ])
            .Build();

        var snapshot = TripUpdateFeedParser.Extract(message, FeedTime);

        var only = Assert.Single(snapshot.Observations);
        Assert.Equal(1, only.StopSequence);
        Assert.Equal(5, snapshot.StopUpdateCount);
        Assert.Equal(4, snapshot.SkippedStopUpdateCount);
    }

    [Fact]
    public void Records_cancelled_and_deleted_trips_as_cancellations()
    {
        var message = new FeedBuilder()
            .WithTrip("cancelled", relationship: TripRelationship.Canceled, stops: [new(Sequence: 1, ArrivalDelay: 0)])
            .WithTrip("deleted", relationship: TripRelationship.Deleted)
            .Build();

        var snapshot = TripUpdateFeedParser.Extract(message, FeedTime);

        Assert.Empty(snapshot.Observations);
        Assert.Equal(new[] { "cancelled", "deleted" }, snapshot.Cancellations.Select(c => c.TripId));
        Assert.All(snapshot.Cancellations, c => Assert.Equal("66-1", c.RouteId));
    }

    [Fact]
    public void Ignores_deleted_entities_and_trips_without_an_id()
    {
        var message = new FeedBuilder()
            .WithTrip("gone", isDeleted: true, stops: [new(Sequence: 1, ArrivalDelay: 5)])
            .WithTrip("", stops: [new(Sequence: 1, ArrivalDelay: 5)])
            .Build();

        var snapshot = TripUpdateFeedParser.Extract(message, FeedTime);

        Assert.Empty(snapshot.Observations);
        Assert.Equal(0, snapshot.TripUpdateCount);
        Assert.Equal(1, snapshot.SkippedStopUpdateCount);
    }

    [Fact]
    public void Falls_back_to_the_brisbane_date_and_unknown_route_when_the_feed_omits_them()
    {
        // 14:30 UTC is 00:30 the next day in Brisbane.
        var lateUtc = new DateTimeOffset(2026, 10, 11, 14, 30, 0, TimeSpan.Zero);
        var message = new FeedBuilder()
            .WithTrip("trip-1", routeId: null, startDate: null, vehicleId: null, stops: [new(Sequence: 1, ArrivalDelay: 0)])
            .Build();

        var observation = Assert.Single(TripUpdateFeedParser.Extract(message, lateUtc).Observations);

        Assert.Equal(new DateOnly(2026, 10, 12), observation.ServiceDate);
        Assert.Equal(TripUpdateFeedParser.UnknownRoute, observation.RouteId);
        Assert.Null(observation.VehicleId);
    }

    [Fact]
    public void Uses_the_fallback_timestamp_when_the_header_has_none()
    {
        var message = new FeedBuilder().Build();

        Assert.Equal(FeedTime, TripUpdateFeedParser.FeedTimestamp(message, FeedTime));
    }
}
