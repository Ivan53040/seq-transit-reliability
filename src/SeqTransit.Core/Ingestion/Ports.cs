using SeqTransit.Core.Model;

namespace SeqTransit.Core.Ingestion;

/// <summary>Where raw feed snapshots are kept (S3 in AWS, memory in tests).</summary>
public interface ISnapshotStore
{
    Task PutAsync(string key, byte[] payload, CancellationToken cancellationToken);

    /// <summary>Keys that sort after <paramref name="startAfter"/>, in ascending order.</summary>
    Task<IReadOnlyList<string>> ListKeysAfterAsync(string startAfter, int maxKeys, CancellationToken cancellationToken);

    Task<byte[]> GetAsync(string key, CancellationToken cancellationToken);
}

/// <summary>Where folded observations are written (PostgreSQL in AWS, memory in tests).</summary>
public interface IObservationStore
{
    /// <summary>Feed timestamp of the newest snapshot already loaded, or null if none.</summary>
    Task<DateTimeOffset?> GetLatestLoadedFeedTimestampAsync(CancellationToken cancellationToken);

    /// <summary>Writes a batch atomically: either every row lands or none do.</summary>
    Task<SaveResult> SaveAsync(LoadBatch batch, CancellationToken cancellationToken);
}

public sealed record LoadBatch(
    IReadOnlyCollection<StopObservation> Observations,
    IReadOnlyCollection<TripCancellation> Cancellations,
    IReadOnlyList<LoadedSnapshot> Snapshots);

/// <param name="ObservationRowsWritten">Rows inserted or updated. Older data never overwrites newer data.</param>
public sealed record SaveResult(int ObservationRowsWritten, int CancellationRowsWritten, int SnapshotRowsWritten);
