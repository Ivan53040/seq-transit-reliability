using Google.Protobuf;
using SeqTransit.Core.Gtfs;
using SeqTransit.Core.Model;

namespace SeqTransit.Core.Ingestion;

public sealed record LoadOptions
{
    /// <summary>Snapshots read per batch. The load function raises this to match its schedule.</summary>
    public int MaxSnapshotsPerBatch { get; init; } = 15;

    /// <summary>How far back to start when the database is empty.</summary>
    public TimeSpan InitialLookback { get; init; } = TimeSpan.FromHours(2);
}

public sealed record LoadResult(
    int SnapshotsLoaded,
    int SnapshotsUnreadable,
    int ObservationRowsWritten,
    int CancellationRowsWritten,
    DateTimeOffset? Watermark,
    bool MoreAvailable);

/// <summary>
/// Loads stored snapshots that are newer than the last loaded one into the database.
/// </summary>
/// <remarks>
/// Idempotent by design: the watermark comes from the database, each batch is saved in one
/// transaction, and the database never lets older data overwrite newer data. If a run fails
/// halfway, the next run simply picks up the same snapshots again.
/// </remarks>
public sealed class SnapshotLoader(
    ISnapshotStore store,
    IObservationStore observations,
    TimeProvider clock,
    LoadOptions options)
{
    public async Task<LoadResult> LoadBatchAsync(CancellationToken cancellationToken)
    {
        var watermark = await observations.GetLatestLoadedFeedTimestampAsync(cancellationToken)
                        ?? clock.GetUtcNow() - options.InitialLookback;

        // Ask for one extra key so we know whether another batch is waiting.
        var keys = await store.ListKeysAfterAsync(
            SnapshotKeys.For(watermark), options.MaxSnapshotsPerBatch + 1, cancellationToken);
        var moreAvailable = keys.Count > options.MaxSnapshotsPerBatch;

        var fold = new ObservationFold();
        var loaded = new List<LoadedSnapshot>();
        var skipped = new List<LoadedSnapshot>();
        var unreadable = 0;

        foreach (var key in keys.Take(options.MaxSnapshotsPerBatch))
        {
            if (!SnapshotKeys.TryParse(key, out var feedTimestamp))
            {
                unreadable++;
                continue;
            }

            FeedSnapshot snapshot;
            try
            {
                var payload = await store.GetAsync(key, cancellationToken);
                snapshot = TripUpdateFeedParser.Extract(TripUpdateFeedParser.Parse(payload), feedTimestamp);
            }
            catch (InvalidProtocolBufferException)
            {
                // The fetcher validates before storing, so this should not happen. Record it with zero
                // counts so the watermark still moves past it: a run of unreadable snapshots would
                // otherwise be listed again on every run and block everything after it.
                unreadable++;
                skipped.Add(new LoadedSnapshot(feedTimestamp, key, 0, 0, 0));
                continue;
            }

            fold.Add(snapshot);
            loaded.Add(new LoadedSnapshot(
                feedTimestamp,
                key,
                snapshot.TripUpdateCount,
                snapshot.StopUpdateCount,
                snapshot.Cancellations.Count));
        }

        var snapshots = loaded.Concat(skipped).ToList();
        if (snapshots.Count == 0)
        {
            return new LoadResult(0, unreadable, 0, 0, watermark, moreAvailable);
        }

        var saved = await observations.SaveAsync(
            new LoadBatch(fold.Observations, fold.Cancellations, snapshots, fold.Reinstated), cancellationToken);

        return new LoadResult(
            loaded.Count,
            unreadable,
            saved.ObservationRowsWritten,
            saved.CancellationRowsWritten,
            snapshots.Max(s => s.FeedTimestamp),
            moreAvailable);
    }
}
