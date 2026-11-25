using Npgsql;
using SeqTransit.Core.Reliability;

namespace SeqTransit.Data;

public sealed record ArchiveResult(IReadOnlyList<DateOnly> ArchivedDays, int DetailRowsDeleted);

/// <summary>
/// Moves service days older than the retention window from per-stop detail to daily totals
/// (migration 0009, decision 14).
/// </summary>
/// <remarks>
/// Each day is one transaction: totals are written to stop_daily_stats and the detail rows are
/// deleted together, so a failure leaves the day as it was. Totals are added on conflict, so a
/// late snapshot loaded for an already archived day is folded in rather than lost.
/// </remarks>
public sealed class DetailArchiver(NpgsqlDataSource dataSource)
{
    internal const string OldestDaysSql = """
        SELECT DISTINCT service_date FROM stop_delay_observation
        WHERE service_date < @cutoff
        ORDER BY service_date
        LIMIT @max_days
        """;

    internal const string RollupSql = """
        INSERT INTO stop_daily_stats AS t (
            service_date, route_id, stop_id, observed_stops, on_time_stops, total_delay_s,
            early_tolerance_s, late_tolerance_s)
        SELECT service_date, route_id, stop_id,
               count(*),
               count(*) FILTER (WHERE delay_s >= -@early AND delay_s <= @late),
               sum(delay_s),
               @early, @late
        FROM reliable_stop_observation
        WHERE service_date = @day
        GROUP BY service_date, route_id, stop_id
        ON CONFLICT (service_date, route_id, stop_id) DO UPDATE SET
            observed_stops = t.observed_stops + EXCLUDED.observed_stops,
            on_time_stops  = t.on_time_stops + EXCLUDED.on_time_stops,
            total_delay_s  = t.total_delay_s + EXCLUDED.total_delay_s
        """;

    internal const string DeleteDetailSql = """
        DELETE FROM stop_delay_observation WHERE service_date = @day
        """;

    /// <param name="cutoff">Days before this date are archived.</param>
    /// <param name="maxDays">Upper bound per call, so one run stays well inside its time limit.</param>
    public async Task<ArchiveResult> ArchiveBeforeAsync(
        DateOnly cutoff, OnTimePolicy policy, int maxDays, CancellationToken cancellationToken)
    {
        var days = new List<DateOnly>();
        await using (var command = dataSource.CreateCommand(OldestDaysSql))
        {
            command.Parameters.AddWithValue("cutoff", cutoff);
            command.Parameters.AddWithValue("max_days", maxDays);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                days.Add(reader.GetFieldValue<DateOnly>(0));
            }
        }

        var deleted = 0;
        foreach (var day in days)
        {
            await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

            await using (var rollup = new NpgsqlCommand(RollupSql, connection, transaction))
            {
                rollup.Parameters.AddWithValue("day", day);
                rollup.Parameters.AddWithValue("early", policy.EarlyToleranceSeconds);
                rollup.Parameters.AddWithValue("late", policy.LateToleranceSeconds);
                await rollup.ExecuteNonQueryAsync(cancellationToken);
            }

            await using (var delete = new NpgsqlCommand(DeleteDetailSql, connection, transaction))
            {
                delete.Parameters.AddWithValue("day", day);
                deleted += await delete.ExecuteNonQueryAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
        }

        return new ArchiveResult(days, deleted);
    }
}
