using System.Globalization;
using System.IO.Compression;
using CsvHelper;
using CsvHelper.Configuration;
using SeqTransit.Core.Model;

namespace SeqTransit.Core.Gtfs;

/// <summary>
/// Reads static GTFS zip archives containing routes.txt, stops.txt, and trips.txt.
/// </summary>
public static class StaticGtfsReader
{
    private static readonly CsvConfiguration CsvConfig = new(CultureInfo.InvariantCulture)
    {
        HeaderValidated = null,
        MissingFieldFound = null,
        TrimOptions = TrimOptions.Trim,
    };

    public static StaticGtfsPackage Read(Stream zipStream)
    {
        using var archive = new ZipArchive(zipStream, ZipArchiveMode.Read, leaveOpen: true);

        var routes = ReadRoutes(archive);
        var stops = ReadStops(archive);
        var trips = ReadTrips(archive);

        return new StaticGtfsPackage(routes, stops, trips);
    }

    public static StaticGtfsPackage ReadFile(string zipFilePath)
    {
        using var stream = File.OpenRead(zipFilePath);
        return Read(stream);
    }

    private static List<GtfsRoute> ReadRoutes(ZipArchive archive)
    {
        var entry = archive.GetEntry("routes.txt")
            ?? throw new FileNotFoundException("routes.txt was not found in the GTFS archive.");

        using var stream = entry.Open();
        using var reader = new StreamReader(stream);
        using var csv = new CsvReader(reader, CsvConfig);

        csv.Read();
        csv.ReadHeader();

        var routes = new List<GtfsRoute>();
        while (csv.Read())
        {
            var routeId = csv.GetField("route_id");
            if (string.IsNullOrWhiteSpace(routeId)) continue;

            var shortName = csv.GetField("route_short_name") ?? string.Empty;
            var longName = csv.GetField("route_long_name") ?? string.Empty;
            var routeTypeStr = csv.GetField("route_type");
            var routeType = int.TryParse(routeTypeStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out var t) ? t : 3;

            routes.Add(new GtfsRoute(
                routeId,
                string.IsNullOrWhiteSpace(shortName) ? routeId : shortName,
                string.IsNullOrWhiteSpace(longName) ? shortName : longName,
                routeType));
        }

        return routes;
    }

    private static List<GtfsStop> ReadStops(ZipArchive archive)
    {
        var entry = archive.GetEntry("stops.txt")
            ?? throw new FileNotFoundException("stops.txt was not found in the GTFS archive.");

        using var stream = entry.Open();
        using var reader = new StreamReader(stream);
        using var csv = new CsvReader(reader, CsvConfig);

        csv.Read();
        csv.ReadHeader();

        var stops = new List<GtfsStop>();
        while (csv.Read())
        {
            var stopId = csv.GetField("stop_id");
            if (string.IsNullOrWhiteSpace(stopId)) continue;

            var stopName = csv.GetField("stop_name") ?? stopId;
            var latStr = csv.GetField("stop_lat");
            var lonStr = csv.GetField("stop_lon");

            double? lat = double.TryParse(latStr, NumberStyles.Float, CultureInfo.InvariantCulture, out var la) ? la : null;
            double? lon = double.TryParse(lonStr, NumberStyles.Float, CultureInfo.InvariantCulture, out var lo) ? lo : null;

            stops.Add(new GtfsStop(stopId, stopName, lat, lon));
        }

        return stops;
    }

    private static List<GtfsTrip> ReadTrips(ZipArchive archive)
    {
        var entry = archive.GetEntry("trips.txt");
        if (entry is null) return [];

        using var stream = entry.Open();
        using var reader = new StreamReader(stream);
        using var csv = new CsvReader(reader, CsvConfig);

        csv.Read();
        csv.ReadHeader();

        var trips = new List<GtfsTrip>();
        while (csv.Read())
        {
            var tripId = csv.GetField("trip_id");
            var routeId = csv.GetField("route_id");
            if (string.IsNullOrWhiteSpace(tripId) || string.IsNullOrWhiteSpace(routeId)) continue;

            var headsign = csv.GetField("trip_headsign");
            var dirStr = csv.GetField("direction_id");
            int? dir = int.TryParse(dirStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out var d) ? d : null;

            trips.Add(new GtfsTrip(tripId, routeId, headsign, dir));
        }

        return trips;
    }
}
