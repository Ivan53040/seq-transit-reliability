using Npgsql;
using SeqTransit.Core.Ingestion;

namespace SeqTransit.Data;

/// <summary>
/// Writes folded observations to PostgreSQL with bulk upserts.
/// </summary>
/// <remarks>
/// Each column is sent as one array parameter and expanded with <c>unnest</c>, so a batch of
/// tens of thousands of rows is a single round trip. The <c>WHERE</c> on each upsert means older
/// data never replaces newer data, which keeps re-runs and out-of-order loads safe.
/// Rows in one batch must have unique keys (PostgreSQL rejects an upsert that touches the same
/// row twice); <see cref="ObservationFold"/> guarantees that.
/// </remarks>
public sealed class ObservationWriter(NpgsqlDataSource dataSource) : IObservationStore
{
    internal const string LatestSnapshotSql = """
        SELECT max(feed_timestamp) FROM feed_snapshot
        """;

    internal const string UpsertObservationsSql = """
        INSERT INTO stop_delay_observation AS t (
            service_date, trip_id, stop_sequence, route_id, stop_id, vehicle_id,
            arrival_delay_s, departure_delay_s, event_time, first_seen_at, last_feed_at)
        SELECT * FROM unnest(
            @service_date::date[], @trip_id::text[], @stop_sequence::int[], @route_id::text[],
            @stop_id::text[], @vehicle_id::text[], @arrival_delay_s::int[], @departure_delay_s::int[],
            @event_time::timestamptz[], @first_seen_at::timestamptz[], @last_feed_at::timestamptz[])
        ON CONFLICT (service_date, trip_id, stop_sequence) DO UPDATE SET
            route_id          = EXCLUDED.route_id,
            stop_id           = EXCLUDED.stop_id,
            vehicle_id        = EXCLUDED.vehicle_id,
            arrival_delay_s   = EXCLUDED.arrival_delay_s,
            departure_delay_s = EXCLUDED.departure_delay_s,
            event_time        = EXCLUDED.event_time,
            first_seen_at     = LEAST(t.first_seen_at, EXCLUDED.first_seen_at),
            last_feed_at      = EXCLUDED.last_feed_at
        WHERE EXCLUDED.last_feed_at > t.last_feed_at
        """;

    internal const string UpsertCancellationsSql = """
        INSERT INTO trip_cancellation AS t (service_date, trip_id, route_id, first_seen_at, last_feed_at)
        SELECT * FROM unnest(
            @service_date::date[], @trip_id::text[], @route_id::text[],
            @first_seen_at::timestamptz[], @last_feed_at::timestamptz[])
        ON CONFLICT (service_date, trip_id) DO UPDATE SET
            route_id      = EXCLUDED.route_id,
            first_seen_at = LEAST(t.first_seen_at, EXCLUDED.first_seen_at),
            last_feed_at  = EXCLUDED.last_feed_at
        WHERE EXCLUDED.last_feed_at > t.last_feed_at
        """;

    // Runs after the cancellation upsert, so a cancellation newer than the "running" sighting survives.
    internal const string WithdrawCancellationsSql = """
        DELETE FROM trip_cancellation AS t
        USING unnest(@service_date::date[], @trip_id::text[], @seen_at::timestamptz[]) AS r(service_date, trip_id, seen_at)
        WHERE t.service_date = r.service_date
          AND t.trip_id = r.trip_id
          AND t.last_feed_at < r.seen_at
        """;

    internal const string InsertSnapshotsSql = """
        INSERT INTO feed_snapshot (feed_timestamp, s3_key, trip_update_count, stop_update_count, cancelled_trip_count)
        SELECT * FROM unnest(
            @feed_timestamp::timestamptz[], @s3_key::text[], @trip_update_count::int[],
            @stop_update_count::int[], @cancelled_trip_count::int[])
        ON CONFLICT (feed_timestamp) DO NOTHING
        """;

    public async Task<DateTimeOffset?> GetLatestLoadedFeedTimestampAsync(CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand(LatestSnapshotSql);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);
        return reader.IsDBNull(0) ? null : reader.GetFieldValue<DateTimeOffset>(0);
    }

    public async Task<SaveResult> SaveAsync(LoadBatch batch, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        var observationRows = await UpsertObservationsAsync(connection, transaction, batch, cancellationToken);
        var cancellationRows = await UpsertCancellationsAsync(connection, transaction, batch, cancellationToken);
        await WithdrawCancellationsAsync(connection, transaction, batch, cancellationToken);
        var snapshotRows = await InsertSnapshotsAsync(connection, transaction, batch, cancellationToken);

        await transaction.CommitAsync(cancellationToken);
        return new SaveResult(observationRows, cancellationRows, snapshotRows);
    }

    private static async Task<int> UpsertObservationsAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, LoadBatch batch, CancellationToken cancellationToken)
    {
        if (batch.Observations.Count == 0)
        {
            return 0;
        }

        var rows = batch.Observations;
        await using var command = new NpgsqlCommand(UpsertObservationsSql, connection, transaction);
        command.Parameters.Add(new NpgsqlParameter<DateOnly[]>("service_date", rows.Select(r => r.ServiceDate).ToArray()));
        command.Parameters.Add(new NpgsqlParameter<string[]>("trip_id", rows.Select(r => r.TripId).ToArray()));
        command.Parameters.Add(new NpgsqlParameter<int[]>("stop_sequence", rows.Select(r => r.StopSequence).ToArray()));
        command.Parameters.Add(new NpgsqlParameter<string[]>("route_id", rows.Select(r => r.RouteId).ToArray()));
        command.Parameters.Add(new NpgsqlParameter<string[]>("stop_id", rows.Select(r => r.StopId).ToArray()));
        command.Parameters.Add(new NpgsqlParameter<string?[]>("vehicle_id", rows.Select(r => r.VehicleId).ToArray()));
        command.Parameters.Add(new NpgsqlParameter<int?[]>("arrival_delay_s", rows.Select(r => r.ArrivalDelaySeconds).ToArray()));
        command.Parameters.Add(new NpgsqlParameter<int?[]>("departure_delay_s", rows.Select(r => r.DepartureDelaySeconds).ToArray()));
        command.Parameters.Add(new NpgsqlParameter<DateTime?[]>("event_time", rows.Select(r => r.EventTime?.UtcDateTime).ToArray()));
        command.Parameters.Add(new NpgsqlParameter<DateTime[]>("first_seen_at", rows.Select(r => r.FirstSeenAt.UtcDateTime).ToArray()));
        command.Parameters.Add(new NpgsqlParameter<DateTime[]>("last_feed_at", rows.Select(r => r.LastFeedAt.UtcDateTime).ToArray()));
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<int> UpsertCancellationsAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, LoadBatch batch, CancellationToken cancellationToken)
    {
        if (batch.Cancellations.Count == 0)
        {
            return 0;
        }

        var rows = batch.Cancellations;
        await using var command = new NpgsqlCommand(UpsertCancellationsSql, connection, transaction);
        command.Parameters.Add(new NpgsqlParameter<DateOnly[]>("service_date", rows.Select(r => r.ServiceDate).ToArray()));
        command.Parameters.Add(new NpgsqlParameter<string[]>("trip_id", rows.Select(r => r.TripId).ToArray()));
        command.Parameters.Add(new NpgsqlParameter<string[]>("route_id", rows.Select(r => r.RouteId).ToArray()));
        command.Parameters.Add(new NpgsqlParameter<DateTime[]>("first_seen_at", rows.Select(r => r.FirstSeenAt.UtcDateTime).ToArray()));
        command.Parameters.Add(new NpgsqlParameter<DateTime[]>("last_feed_at", rows.Select(r => r.LastFeedAt.UtcDateTime).ToArray()));
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task WithdrawCancellationsAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, LoadBatch batch, CancellationToken cancellationToken)
    {
        if (batch.Reinstated is not { Count: > 0 } rows)
        {
            return;
        }

        await using var command = new NpgsqlCommand(WithdrawCancellationsSql, connection, transaction);
        command.Parameters.Add(new NpgsqlParameter<DateOnly[]>("service_date", rows.Select(r => r.ServiceDate).ToArray()));
        command.Parameters.Add(new NpgsqlParameter<string[]>("trip_id", rows.Select(r => r.TripId).ToArray()));
        command.Parameters.Add(new NpgsqlParameter<DateTime[]>("seen_at", rows.Select(r => r.SeenAt.UtcDateTime).ToArray()));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<int> InsertSnapshotsAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, LoadBatch batch, CancellationToken cancellationToken)
    {
        var rows = batch.Snapshots;
        await using var command = new NpgsqlCommand(InsertSnapshotsSql, connection, transaction);
        command.Parameters.Add(new NpgsqlParameter<DateTime[]>("feed_timestamp", rows.Select(r => r.FeedTimestamp.UtcDateTime).ToArray()));
        command.Parameters.Add(new NpgsqlParameter<string[]>("s3_key", rows.Select(r => r.StorageKey).ToArray()));
        command.Parameters.Add(new NpgsqlParameter<int[]>("trip_update_count", rows.Select(r => r.TripUpdateCount).ToArray()));
        command.Parameters.Add(new NpgsqlParameter<int[]>("stop_update_count", rows.Select(r => r.StopUpdateCount).ToArray()));
        command.Parameters.Add(new NpgsqlParameter<int[]>("cancelled_trip_count", rows.Select(r => r.CancelledTripCount).ToArray()));
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
