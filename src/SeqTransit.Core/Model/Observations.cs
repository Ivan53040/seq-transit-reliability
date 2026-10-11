namespace SeqTransit.Core.Model;

/// <summary>Identifies one stop of one trip on one service day.</summary>
public readonly record struct ObservationKey(DateOnly ServiceDate, string TripId, int StopSequence);

/// <summary>
/// The delay reported for one stop of one trip, as seen in one or more feed snapshots.
/// </summary>
/// <param name="FirstSeenAt">Feed timestamp of the earliest snapshot that contained this stop.</param>
/// <param name="LastFeedAt">Feed timestamp of the snapshot the delay values come from.</param>
public sealed record StopObservation(
    DateOnly ServiceDate,
    string TripId,
    int StopSequence,
    string RouteId,
    string StopId,
    string? VehicleId,
    int? ArrivalDelaySeconds,
    int? DepartureDelaySeconds,
    DateTimeOffset? EventTime,
    DateTimeOffset FirstSeenAt,
    DateTimeOffset LastFeedAt)
{
    public ObservationKey Key => new(ServiceDate, TripId, StopSequence);

    /// <summary>Arrival delay if reported, otherwise departure delay.</summary>
    public int? DelaySeconds => ArrivalDelaySeconds ?? DepartureDelaySeconds;
}

/// <summary>A trip the feed marked as cancelled (or deleted) on a service day.</summary>
public sealed record TripCancellation(
    DateOnly ServiceDate,
    string TripId,
    string RouteId,
    DateTimeOffset FirstSeenAt,
    DateTimeOffset LastFeedAt);

/// <summary>A trip the feed reported as running (not cancelled) at <paramref name="SeenAt"/>.</summary>
/// <remarks>Used to withdraw an earlier cancellation when Translink reinstates the trip.</remarks>
public sealed record TripRunning(DateOnly ServiceDate, string TripId, DateTimeOffset SeenAt);

/// <summary>Everything extracted from one GTFS-RT TripUpdates message.</summary>
/// <param name="RunningTrips">Trips present and not cancelled in this snapshot.</param>
public sealed record FeedSnapshot(
    DateTimeOffset FeedTimestamp,
    IReadOnlyList<StopObservation> Observations,
    IReadOnlyList<TripCancellation> Cancellations,
    int TripUpdateCount,
    int StopUpdateCount,
    int SkippedStopUpdateCount,
    IReadOnlyList<TripRunning>? RunningTrips = null);

/// <summary>Bookkeeping row written for every snapshot that was loaded.</summary>
public sealed record LoadedSnapshot(
    DateTimeOffset FeedTimestamp,
    string StorageKey,
    int TripUpdateCount,
    int StopUpdateCount,
    int CancelledTripCount);
