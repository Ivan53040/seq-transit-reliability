using System.IO.Compression;
using System.Text;
using SeqTransit.Core.Gtfs;

namespace SeqTransit.Core.Tests;

public class StaticGtfsReaderTests
{
    [Fact]
    public void Reads_routes_stops_and_trips_from_zip_archive()
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            AddEntry(archive, "routes.txt", """
                route_id,route_short_name,route_long_name,route_type
                100-5146,100,"Forest Lake/Inala - City",3
                CLSH-5167,CLSH,"Cleveland - Shorncliffe",2
                """);

            AddEntry(archive, "stops.txt", """
                stop_id,stop_name,stop_lat,stop_lon
                S1,"Central Station",-27.466,153.028
                S2,"Roma Street",-27.465,153.018
                S3,"South Bank",-27.481,153.022
                """);

            AddEntry(archive, "trips.txt", """
                route_id,service_id,trip_id,trip_headsign,direction_id
                100-5146,weekday,t1,"City",0
                CLSH-5167,weekday,t2,"Shorncliffe",1
                """);
        }

        stream.Position = 0;
        var package = StaticGtfsReader.Read(stream);

        Assert.Equal(2, package.Routes.Count);
        Assert.Equal("100-5146", package.Routes[0].RouteId);
        Assert.Equal("100", package.Routes[0].RouteShortName);
        Assert.Equal("Forest Lake/Inala - City", package.Routes[0].RouteLongName);
        Assert.Equal(3, package.Routes[0].RouteType);

        Assert.Equal("CLSH-5167", package.Routes[1].RouteId);
        Assert.Equal(2, package.Routes[1].RouteType);

        Assert.Equal(3, package.Stops.Count);
        Assert.Equal("S1", package.Stops[0].StopId);
        Assert.Equal("Central Station", package.Stops[0].StopName);
        Assert.Equal(-27.466, package.Stops[0].Latitude);

        Assert.Equal(2, package.Trips.Count);
        Assert.Equal("t1", package.Trips[0].TripId);
        Assert.Equal("100-5146", package.Trips[0].RouteId);
    }

    private static void AddEntry(ZipArchive archive, string name, string content)
    {
        var entry = archive.CreateEntry(name);
        using var writer = new StreamWriter(entry.Open(), Encoding.UTF8);
        writer.Write(content);
    }
}
