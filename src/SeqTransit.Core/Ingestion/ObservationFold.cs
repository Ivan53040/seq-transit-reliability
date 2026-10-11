using SeqTransit.Core.Model;

namespace SeqTransit.Core.Ingestion;

/// <summary>
/// Merges many snapshots into one set of rows, so a batch of snapshots becomes a single
/// database write instead of one write per snapshot.
/// </summary>
/// <remarks>
/// For each stop, the values from the newest snapshot win and the earliest "first seen" time
/// is kept. Merging is order-independent: adding snapshots out of order gives the same result.
/// </remarks>
public sealed class ObservationFold
{
    private readonly Dictionary<ObservationKey, StopObservation> _observations = [];
    private readonly Dictionary<(DateOnly ServiceDate, string TripId), TripCancellation> _cancellations = [];

    public int SnapshotCount { get; private set; }

    public IReadOnlyCollection<StopObservation> Observations => _observations.Values;

    public IReadOnlyCollection<TripCancellation> Cancellations => _cancellations.Values;

    public void Add(FeedSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        SnapshotCount++;

        foreach (var observation in snapshot.Observations)
        {
            _observations[observation.Key] = _observations.TryGetValue(observation.Key, out var existing)
                ? Merge(existing, observation)
                : observation;
        }

        foreach (var cancellation in snapshot.Cancellations)
        {
            var key = (cancellation.ServiceDate, cancellation.TripId);
            _cancellations[key] = _cancellations.TryGetValue(key, out var existing)
                ? Merge(existing, cancellation)
                : cancellation;
        }
    }

    internal static StopObservation Merge(StopObservation existing, StopObservation incoming)
    {
        var newest = incoming.LastFeedAt >= existing.LastFeedAt ? incoming : existing;
        return newest with { FirstSeenAt = Min(existing.FirstSeenAt, incoming.FirstSeenAt) };
    }

    internal static TripCancellation Merge(TripCancellation existing, TripCancellation incoming)
    {
        var newest = incoming.LastFeedAt >= existing.LastFeedAt ? incoming : existing;
        return newest with { FirstSeenAt = Min(existing.FirstSeenAt, incoming.FirstSeenAt) };
    }

    private static DateTimeOffset Min(DateTimeOffset a, DateTimeOffset b) => a <= b ? a : b;
}
