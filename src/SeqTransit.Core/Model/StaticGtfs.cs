namespace SeqTransit.Core.Model;

/// <summary>Transit route from static GTFS routes.txt.</summary>
public sealed record GtfsRoute(
    string RouteId,
    string RouteShortName,
    string RouteLongName,
    int RouteType);

/// <summary>Transit stop from static GTFS stops.txt.</summary>
public sealed record GtfsStop(
    string StopId,
    string StopName,
    double? Latitude,
    double? Longitude);

/// <summary>Scheduled trip from static GTFS trips.txt.</summary>
public sealed record GtfsTrip(
    string TripId,
    string RouteId,
    string? Headsign,
    int? DirectionId);

/// <summary>A parsed static timetable package.</summary>
public sealed record StaticGtfsPackage(
    IReadOnlyList<GtfsRoute> Routes,
    IReadOnlyList<GtfsStop> Stops,
    IReadOnlyList<GtfsTrip> Trips);
