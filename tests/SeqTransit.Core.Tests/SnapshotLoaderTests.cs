using SeqTransit.Core.Ingestion;
using SeqTransit.Core.Tests.TestSupport;

namespace SeqTransit.Core.Tests;

public class SnapshotLoaderTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 11, 6, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Loads_new_snapshots_in_one_save_and_reports_the_new_watermark()
    {
        var store = new InMemorySnapshotStore();
        await PutSnapshot(store, Now.AddMinutes(-3), delay: 30);
        await PutSnapshot(store, Now.AddMinutes(-2), delay: 60);
        await PutSnapshot(store, Now.AddMinutes(-1), delay: 90);
        var database = new InMemoryObservationStore();

        var result = await Loader(store, database).LoadBatchAsync(CancellationToken.None);

        Assert.Equal(3, result.SnapshotsLoaded);
        Assert.Equal(1, result.ObservationRowsWritten);
        Assert.Equal(Now.AddMinutes(-1), result.Watermark);
        Assert.False(result.MoreAvailable);
        Assert.Equal(1, database.SaveCalls);
        Assert.Equal(90, Assert.Single(database.Observations.Values).ArrivalDelaySeconds);
    }

    [Fact]
    public async Task Running_again_with_nothing_new_writes_nothing()
    {
        var store = new InMemorySnapshotStore();
        await PutSnapshot(store, Now.AddMinutes(-1), delay: 30);
        var database = new InMemoryObservationStore();
        var loader = Loader(store, database);

        await loader.LoadBatchAsync(CancellationToken.None);
        var second = await loader.LoadBatchAsync(CancellationToken.None);

        Assert.Equal(0, second.SnapshotsLoaded);
        Assert.Equal(1, database.SaveCalls);
    }

    [Fact]
    public async Task Splits_a_backlog_into_batches()
    {
        var store = new InMemorySnapshotStore();
        for (var minute = 10; minute >= 1; minute--)
        {
            await PutSnapshot(store, Now.AddMinutes(-minute), delay: minute);
        }

        var database = new InMemoryObservationStore();
        var loader = Loader(store, database, batchSize: 4);

        var batches = new List<LoadResult>();
        do
        {
            batches.Add(await loader.LoadBatchAsync(CancellationToken.None));
        }
        while (batches[^1].MoreAvailable);

        Assert.Equal(new[] { 4, 4, 2 }, batches.Select(b => b.SnapshotsLoaded));
        Assert.Equal(10, database.Snapshots.Count);
        Assert.Equal(1, Assert.Single(database.Observations.Values).ArrivalDelaySeconds);
    }

    [Fact]
    public async Task Starts_from_the_lookback_window_when_the_database_is_empty()
    {
        var store = new InMemorySnapshotStore();
        await PutSnapshot(store, Now.AddHours(-3), delay: 1);
        await PutSnapshot(store, Now.AddMinutes(-5), delay: 2);

        var result = await Loader(store, new InMemoryObservationStore()).LoadBatchAsync(CancellationToken.None);

        Assert.Equal(1, result.SnapshotsLoaded);
    }

    [Fact]
    public async Task Skips_unreadable_objects_without_blocking_later_snapshots()
    {
        var store = new InMemorySnapshotStore();
        await store.PutAsync(SnapshotKeys.For(Now.AddMinutes(-2)), [0xFF, 0xFF, 0xFF], CancellationToken.None);
        await PutSnapshot(store, Now.AddMinutes(-1), delay: 5);

        var result = await Loader(store, new InMemoryObservationStore()).LoadBatchAsync(CancellationToken.None);

        Assert.Equal(1, result.SnapshotsUnreadable);
        Assert.Equal(1, result.SnapshotsLoaded);
        Assert.Equal(Now.AddMinutes(-1), result.Watermark);
    }

    private static SnapshotLoader Loader(InMemorySnapshotStore store, InMemoryObservationStore database, int batchSize = 15) =>
        new(store, database, new ManualTimeProvider(Now), new LoadOptions { MaxSnapshotsPerBatch = batchSize });

    private static Task PutSnapshot(InMemorySnapshotStore store, DateTimeOffset feedTime, int delay)
    {
        var bytes = new FeedBuilder()
            .WithTimestamp(feedTime)
            .WithTrip("t1", [new(1, ArrivalDelay: delay)])
            .BuildBytes();
        return store.PutAsync(SnapshotKeys.For(feedTime), bytes, CancellationToken.None);
    }
}
