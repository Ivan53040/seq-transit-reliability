using Npgsql;
using NpgsqlTypes;
using SeqTransit.Core.Model;

namespace SeqTransit.Data;

public sealed record StaticGtfsLoadResult(int RoutesLoaded, int StopsLoaded, int TripsLoaded);

/// <summary>
/// Bulk loads static GTFS timetable data into PostgreSQL using Npgsql binary COPY,
/// swapping data in a single transaction so the API never sees partial tables.
/// </summary>
public sealed class StaticGtfsLoader(NpgsqlDataSource dataSource)
{
    public async Task<StaticGtfsLoadResult> LoadAsync(StaticGtfsPackage package, CancellationToken cancellationToken = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        // 1. Create temporary staging tables
        await using (var cmd = new NpgsqlCommand("""
            CREATE TEMP TABLE stage_route (LIKE gtfs_route INCLUDING ALL) ON COMMIT DROP;
            CREATE TEMP TABLE stage_stop (LIKE gtfs_stop INCLUDING ALL) ON COMMIT DROP;
            CREATE TEMP TABLE stage_trip (LIKE gtfs_trip INCLUDING ALL) ON COMMIT DROP;
            """, connection, transaction))
        {
            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }

        // 2. Binary COPY routes
        await using (var writer = await connection.BeginBinaryImportAsync(
            "COPY stage_route (route_id, route_short_name, route_long_name, route_type) FROM STDIN (FORMAT BINARY)", cancellationToken))
        {
            foreach (var route in package.Routes)
            {
                await writer.StartRowAsync(cancellationToken);
                await writer.WriteAsync(route.RouteId, NpgsqlDbType.Text, cancellationToken);
                await writer.WriteAsync(route.RouteShortName, NpgsqlDbType.Text, cancellationToken);
                await writer.WriteAsync(route.RouteLongName, NpgsqlDbType.Text, cancellationToken);
                await writer.WriteAsync(route.RouteType, NpgsqlDbType.Integer, cancellationToken);
            }
            await writer.CompleteAsync(cancellationToken);
        }

        // 3. Binary COPY stops
        await using (var writer = await connection.BeginBinaryImportAsync(
            "COPY stage_stop (stop_id, stop_name, stop_lat, stop_lon) FROM STDIN (FORMAT BINARY)", cancellationToken))
        {
            foreach (var stop in package.Stops)
            {
                await writer.StartRowAsync(cancellationToken);
                await writer.WriteAsync(stop.StopId, NpgsqlDbType.Text, cancellationToken);
                await writer.WriteAsync(stop.StopName, NpgsqlDbType.Text, cancellationToken);
                if (stop.Latitude.HasValue)
                    await writer.WriteAsync(stop.Latitude.Value, NpgsqlDbType.Double, cancellationToken);
                else
                    await writer.WriteNullAsync(cancellationToken);

                if (stop.Longitude.HasValue)
                    await writer.WriteAsync(stop.Longitude.Value, NpgsqlDbType.Double, cancellationToken);
                else
                    await writer.WriteNullAsync(cancellationToken);
            }
            await writer.CompleteAsync(cancellationToken);
        }

        // 4. Binary COPY trips
        await using (var writer = await connection.BeginBinaryImportAsync(
            "COPY stage_trip (trip_id, route_id, trip_headsign, direction_id) FROM STDIN (FORMAT BINARY)", cancellationToken))
        {
            foreach (var trip in package.Trips)
            {
                await writer.StartRowAsync(cancellationToken);
                await writer.WriteAsync(trip.TripId, NpgsqlDbType.Text, cancellationToken);
                await writer.WriteAsync(trip.RouteId, NpgsqlDbType.Text, cancellationToken);
                if (trip.Headsign is not null)
                    await writer.WriteAsync(trip.Headsign, NpgsqlDbType.Text, cancellationToken);
                else
                    await writer.WriteNullAsync(cancellationToken);

                if (trip.DirectionId.HasValue)
                    await writer.WriteAsync(trip.DirectionId.Value, NpgsqlDbType.Integer, cancellationToken);
                else
                    await writer.WriteNullAsync(cancellationToken);
            }
            await writer.CompleteAsync(cancellationToken);
        }

        // 5. Swap live tables atomically in the transaction
        await using (var swapCmd = new NpgsqlCommand("""
            TRUNCATE gtfs_route, gtfs_stop, gtfs_trip;
            INSERT INTO gtfs_route SELECT * FROM stage_route ON CONFLICT (route_id) DO NOTHING;
            INSERT INTO gtfs_stop SELECT * FROM stage_stop ON CONFLICT (stop_id) DO NOTHING;
            INSERT INTO gtfs_trip SELECT * FROM stage_trip ON CONFLICT (trip_id) DO NOTHING;
            """, connection, transaction))
        {
            await swapCmd.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);

        return new StaticGtfsLoadResult(package.Routes.Count, package.Stops.Count, package.Trips.Count);
    }
}
