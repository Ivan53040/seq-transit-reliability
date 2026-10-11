using SeqTransit.Core.Ingestion;
using SeqTransit.Core.Model;

namespace SeqTransit.Core.Tests;

public class ObservationFoldTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 11, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Newest_snapshot_wins_and_earliest_first_seen_is_kept()
    {
        var fold = new ObservationFold();

        fold.Add(Snapshot(T0, delay: 30));
        fold.Add(Snapshot(T0.AddMinutes(1), delay: 90));

        var merged = Assert.Single(fold.Observations);
        Assert.Equal(90, merged.ArrivalDelaySeconds);
        Assert.Equal(T0, merged.FirstSeenAt);
        Assert.Equal(T0.AddMinutes(1), merged.LastFeedAt);
        Assert.Equal(2, fold.SnapshotCount);
    }

    [Fact]
    public void Order_of_snapshots_does_not_change_the_result()
    {
        var inOrder = new ObservationFold();
        inOrder.Add(Snapshot(T0, delay: 30));
        inOrder.Add(Snapshot(T0.AddMinutes(1), delay: 90));

        var reversed = new ObservationFold();
        reversed.Add(Snapshot(T0.AddMinutes(1), delay: 90));
        reversed.Add(Snapshot(T0, delay: 30));

        Assert.Equal(Assert.Single(inOrder.Observations), Assert.Single(reversed.Observations));
    }

    [Fact]
    public void Different_stops_are_kept_apart()
    {
        var fold = new ObservationFold();
        fold.Add(Snapshot(T0, delay: 30, stopSequence: 1));
        fold.Add(Snapshot(T0, delay: 45, stopSequence: 2));

        Assert.Equal(2, fold.Observations.Count);
    }

    [Fact]
    public void Cancellations_are_merged_per_trip()
    {
        var fold = new ObservationFold();
        var date = new DateOnly(2026, 10, 11);
        fold.Add(new FeedSnapshot(T0, [], [new TripCancellation(date, "t1", "66-1", T0, T0)], 1, 0, 0));
        fold.Add(new FeedSnapshot(T0.AddMinutes(1), [], [new TripCancellation(date, "t1", "66-1", T0.AddMinutes(1), T0.AddMinutes(1))], 1, 0, 0));

        var cancellation = Assert.Single(fold.Cancellations);
        Assert.Equal(T0, cancellation.FirstSeenAt);
        Assert.Equal(T0.AddMinutes(1), cancellation.LastFeedAt);
    }

    [Fact]
    public void A_trip_seen_running_after_its_cancellation_is_reinstated()
    {
        var fold = new ObservationFold();
        var date = new DateOnly(2026, 10, 11);
        fold.Add(new FeedSnapshot(T0, [], [new TripCancellation(date, "t1", "66-1", T0, T0)], 1, 0, 0));
        fold.Add(new FeedSnapshot(T0.AddMinutes(1), [], [], 1, 0, 0, [new TripRunning(date, "t1", T0.AddMinutes(1))]));

        Assert.Empty(fold.Cancellations);
        var reinstated = Assert.Single(fold.Reinstated);
        Assert.Equal(T0.AddMinutes(1), reinstated.SeenAt);
    }

    [Fact]
    public void A_cancellation_newer_than_the_running_sighting_stands_whatever_the_order()
    {
        var fold = new ObservationFold();
        var date = new DateOnly(2026, 10, 11);
        fold.Add(new FeedSnapshot(T0.AddMinutes(1), [], [new TripCancellation(date, "t1", "66-1", T0.AddMinutes(1), T0.AddMinutes(1))], 1, 0, 0));
        fold.Add(new FeedSnapshot(T0, [], [], 1, 0, 0, [new TripRunning(date, "t1", T0)]));

        Assert.Single(fold.Cancellations);
        Assert.Empty(fold.Reinstated);
    }

    private static FeedSnapshot Snapshot(DateTimeOffset feedTime, int delay, int stopSequence = 1) =>
        new(
            feedTime,
            [new StopObservation(new DateOnly(2026, 10, 11), "t1", stopSequence, "66-1", "S1", "V1", delay, null, feedTime, feedTime, feedTime)],
            [],
            TripUpdateCount: 1,
            StopUpdateCount: 1,
            SkippedStopUpdateCount: 0);
}
