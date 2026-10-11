using System.Globalization;
using Google.Protobuf;
using SeqTransit.Core.Model;
using TransitRealtime;
using StopRelationship = TransitRealtime.TripUpdate.Types.StopTimeUpdate.Types.ScheduleRelationship;
using TripRelationship = TransitRealtime.TripDescriptor.Types.ScheduleRelationship;

namespace SeqTransit.Core.Gtfs;

/// <summary>
/// Turns a GTFS-realtime TripUpdates message into stop observations and cancellations.
/// </summary>
public static class TripUpdateFeedParser
{
    /// <summary>Route id used when the feed leaves it out.</summary>
    public const string UnknownRoute = "unknown";

    /// <summary>Parses raw protobuf bytes. Throws <see cref="InvalidProtocolBufferException"/> on bad input.</summary>
    public static FeedMessage Parse(byte[] payload) => FeedMessage.Parser.ParseFrom(payload);

    /// <summary>The feed's own timestamp, or <paramref name="fallback"/> if the header has none.</summary>
    public static DateTimeOffset FeedTimestamp(FeedMessage message, DateTimeOffset fallback)
    {
        var header = message.Header;
        return header is { HasTimestamp: true, Timestamp: > 0 } && header.Timestamp <= long.MaxValue
            ? DateTimeOffset.FromUnixTimeSeconds((long)header.Timestamp)
            : fallback;
    }

    public static FeedSnapshot Extract(FeedMessage message, DateTimeOffset feedTimestamp)
    {
        var observations = new List<StopObservation>();
        var cancellations = new List<TripCancellation>();
        var running = new List<TripRunning>();
        var tripUpdates = 0;
        var stopUpdates = 0;
        var skippedStopUpdates = 0;

        foreach (var entity in message.Entity)
        {
            if (entity.IsDeleted || entity.TripUpdate is not { } tripUpdate)
            {
                continue;
            }

            var trip = tripUpdate.Trip;
            if (trip is null || !trip.HasTripId || trip.TripId.Length == 0)
            {
                // Without a trip id there is nothing stable to key the observation on.
                stopUpdates += tripUpdate.StopTimeUpdate.Count;
                skippedStopUpdates += tripUpdate.StopTimeUpdate.Count;
                continue;
            }

            tripUpdates++;
            var serviceDate = ServiceDate(trip, feedTimestamp);
            var routeId = trip.HasRouteId && trip.RouteId.Length > 0 ? trip.RouteId : UnknownRoute;

            if (trip.ScheduleRelationship is TripRelationship.Canceled or TripRelationship.Deleted)
            {
                cancellations.Add(new TripCancellation(serviceDate, trip.TripId, routeId, feedTimestamp, feedTimestamp));
                continue;
            }

            running.Add(new TripRunning(serviceDate, trip.TripId, feedTimestamp));
            var vehicleId = tripUpdate.Vehicle is { HasId: true } vehicle && vehicle.Id.Length > 0 ? vehicle.Id : null;

            foreach (var update in tripUpdate.StopTimeUpdate)
            {
                stopUpdates++;
                var observation = ToObservation(update, serviceDate, trip.TripId, routeId, vehicleId, feedTimestamp);
                if (observation is null)
                {
                    skippedStopUpdates++;
                }
                else
                {
                    observations.Add(observation);
                }
            }
        }

        return new FeedSnapshot(feedTimestamp, observations, cancellations, tripUpdates, stopUpdates, skippedStopUpdates, running);
    }

    private static StopObservation? ToObservation(
        TripUpdate.Types.StopTimeUpdate update,
        DateOnly serviceDate,
        string tripId,
        string routeId,
        string? vehicleId,
        DateTimeOffset feedTimestamp)
    {
        // Skipped stops are not served and NO_DATA means there is no real-time prediction.
        if (update.ScheduleRelationship is StopRelationship.Skipped or StopRelationship.NoData)
        {
            return null;
        }

        // stop_sequence is optional in GTFS-RT; without it two visits to one stop can't be told apart.
        if (!update.HasStopSequence || update.StopSequence > int.MaxValue)
        {
            return null;
        }

        int? arrivalDelay = update.Arrival is { HasDelay: true } arrival ? arrival.Delay : null;
        int? departureDelay = update.Departure is { HasDelay: true } departure ? departure.Delay : null;
        var eventTime = EventTime(update.Arrival) ?? EventTime(update.Departure);

        if (arrivalDelay is null && departureDelay is null && eventTime is null)
        {
            return null;
        }

        return new StopObservation(
            serviceDate,
            tripId,
            (int)update.StopSequence,
            routeId,
            update.HasStopId ? update.StopId : string.Empty,
            vehicleId,
            arrivalDelay,
            departureDelay,
            eventTime,
            FirstSeenAt: feedTimestamp,
            LastFeedAt: feedTimestamp);
    }

    private static DateTimeOffset? EventTime(TripUpdate.Types.StopTimeEvent? stopTimeEvent) =>
        stopTimeEvent is { HasTime: true, Time: > 0 } ? DateTimeOffset.FromUnixTimeSeconds(stopTimeEvent.Time) : null;

    /// <summary>
    /// The trip's service date from start_date (YYYYMMDD). If the feed leaves it out, falls back
    /// to the Brisbane date of the snapshot, which is wrong for trips that run past midnight.
    /// </summary>
    private static DateOnly ServiceDate(TripDescriptor trip, DateTimeOffset feedTimestamp) =>
        trip.HasStartDate
        && DateOnly.TryParseExact(trip.StartDate, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date
            : BrisbaneTime.DateOf(feedTimestamp);
}
