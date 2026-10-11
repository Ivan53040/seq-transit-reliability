using Google.Protobuf;
using TransitRealtime;
using StopRelationship = TransitRealtime.TripUpdate.Types.StopTimeUpdate.Types.ScheduleRelationship;
using TripRelationship = TransitRealtime.TripDescriptor.Types.ScheduleRelationship;

namespace SeqTransit.Core.Tests.TestSupport;

/// <summary>Builds small GTFS-RT TripUpdates messages for tests.</summary>
internal sealed class FeedBuilder
{
    private readonly FeedMessage _message = new()
    {
        Header = new FeedHeader { GtfsRealtimeVersion = "2.0" },
    };

    public FeedBuilder WithTimestamp(DateTimeOffset timestamp)
    {
        _message.Header.Timestamp = (ulong)timestamp.ToUnixTimeSeconds();
        return this;
    }

    public FeedBuilder WithTrip(
        string tripId,
        IEnumerable<StopTimeUpdateSpec>? stops = null,
        string? routeId = "66-1",
        string? startDate = "20261011",
        string? vehicleId = "V1",
        TripRelationship relationship = TripRelationship.Scheduled,
        bool isDeleted = false)
    {
        var trip = new TripDescriptor { ScheduleRelationship = relationship };
        if (tripId.Length > 0)
        {
            trip.TripId = tripId;
        }

        if (routeId is not null)
        {
            trip.RouteId = routeId;
        }

        if (startDate is not null)
        {
            trip.StartDate = startDate;
        }

        var tripUpdate = new TripUpdate { Trip = trip };
        if (vehicleId is not null)
        {
            tripUpdate.Vehicle = new VehicleDescriptor { Id = vehicleId };
        }

        foreach (var stop in stops ?? [])
        {
            tripUpdate.StopTimeUpdate.Add(stop.ToProto());
        }

        var entity = new FeedEntity { Id = $"e{_message.Entity.Count + 1}", TripUpdate = tripUpdate };
        if (isDeleted)
        {
            entity.IsDeleted = true;
        }

        _message.Entity.Add(entity);
        return this;
    }

    public FeedMessage Build() => _message.Clone();

    public byte[] BuildBytes() => _message.ToByteArray();
}

internal sealed record StopTimeUpdateSpec(
    uint? Sequence,
    string StopId = "stop-1",
    int? ArrivalDelay = null,
    int? DepartureDelay = null,
    long? ArrivalTime = null,
    StopRelationship Relationship = StopRelationship.Scheduled)
{
    public TripUpdate.Types.StopTimeUpdate ToProto()
    {
        var update = new TripUpdate.Types.StopTimeUpdate { StopId = StopId, ScheduleRelationship = Relationship };
        if (Sequence is { } sequence)
        {
            update.StopSequence = sequence;
        }

        if (ArrivalDelay is not null || ArrivalTime is not null)
        {
            update.Arrival = new TripUpdate.Types.StopTimeEvent();
            if (ArrivalDelay is { } delay)
            {
                update.Arrival.Delay = delay;
            }

            if (ArrivalTime is { } time)
            {
                update.Arrival.Time = time;
            }
        }

        if (DepartureDelay is { } departureDelay)
        {
            update.Departure = new TripUpdate.Types.StopTimeEvent { Delay = departureDelay };
        }

        return update;
    }
}
