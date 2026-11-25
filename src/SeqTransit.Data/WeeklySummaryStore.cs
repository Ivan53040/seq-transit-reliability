using System.Text.Json;
using Npgsql;
using NpgsqlTypes;
using SeqTransit.Core.Reliability;
using SeqTransit.Core.Summaries;

namespace SeqTransit.Data;

public sealed record WeeklySummaryResponse(
    DateOnly WeekStart,
    string SummaryText,
    JsonElement InputFigures,
    string ModelId,
    DateTimeOffset CreatedAt);

/// <summary>
/// Computes all weekly reliability figures in PostgreSQL SQL and stores validated AI summaries.
/// </summary>
public sealed class WeeklySummaryStore(NpgsqlDataSource dataSource)
{
    public async Task<WeeklyFigures> ComputeWeeklyFiguresAsync(
        DateOnly weekStart,
        OnTimePolicy policy,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var weekEnd = weekStart.AddDays(6);
        var prevStart = weekStart.AddDays(-7);
        var prevEnd = weekStart.AddDays(-1);
        var early = -policy.EarlyToleranceSeconds;
        var late = policy.LateToleranceSeconds;

        await using var conn = await dataSource.OpenConnectionAsync(cancellationToken);

        // 1. Overall week totals
        int totalObserved = 0;
        int totalRoutes = 0;
        double onTimePercent = 0;
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                SELECT
                    count(*)::int,
                    count(DISTINCT route_id)::int,
                    coalesce(round(100.0 * count(*) FILTER (WHERE delay_s >= $4 AND delay_s <= $5) / NULLIF(count(*), 0), 1), 0)::float8
                FROM reliable_stop_observation
                WHERE service_date >= $1 AND service_date <= $2 AND event_time < $3
                """;
            cmd.Parameters.AddWithValue(weekStart);
            cmd.Parameters.AddWithValue(weekEnd);
            cmd.Parameters.AddWithValue(now);
            cmd.Parameters.AddWithValue(early);
            cmd.Parameters.AddWithValue(late);

            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                totalObserved = reader.GetInt32(0);
                totalRoutes = reader.GetInt32(1);
                onTimePercent = reader.GetDouble(2);
            }
        }

        // 2. Previous week overall on-time % (for week-over-week change)
        double? prevOnTimePercent = null;
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                SELECT
                    count(*)::int,
                    coalesce(round(100.0 * count(*) FILTER (WHERE delay_s >= $4 AND delay_s <= $5) / NULLIF(count(*), 0), 1), 0)::float8
                FROM reliable_stop_observation
                WHERE service_date >= $1 AND service_date <= $2 AND event_time < $3
                """;
            cmd.Parameters.AddWithValue(prevStart);
            cmd.Parameters.AddWithValue(prevEnd);
            cmd.Parameters.AddWithValue(now);
            cmd.Parameters.AddWithValue(early);
            cmd.Parameters.AddWithValue(late);

            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken) && reader.GetInt32(0) > 0)
            {
                prevOnTimePercent = reader.GetDouble(1);
            }
        }

        double? changePp = prevOnTimePercent is { } prev
            ? Math.Round(onTimePercent - prev, 1)
            : null;

        // 3. Best and worst routes (require at least 20 observations; fallback to 1 if sparse)
        var routeRows = await QueryRoutesAsync(conn, weekStart, weekEnd, now, early, late, minObs: 20, cancellationToken);
        if (routeRows.Count == 0 && totalObserved > 0)
        {
            routeRows = await QueryRoutesAsync(conn, weekStart, weekEnd, now, early, late, minObs: 1, cancellationToken);
        }

        var worstRoute = routeRows.FirstOrDefault();
        var bestRoute = routeRows.Count > 1 ? routeRows[^1] : routeRows.FirstOrDefault();

        // 4. Worst hour of day (Brisbane time, require at least 20 observations; fallback to 1 if sparse)
        var worstHour = await QueryWorstHourAsync(conn, weekStart, weekEnd, now, early, late, minObs: 20, cancellationToken)
            ?? (totalObserved > 0
                ? await QueryWorstHourAsync(conn, weekStart, weekEnd, now, early, late, minObs: 1, cancellationToken)
                : null);

        // 5. Total cancellations for the week
        int totalCancellations = 0;
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                SELECT count(*)::int
                FROM trip_cancellation
                WHERE service_date >= $1 AND service_date <= $2
                """;
            cmd.Parameters.AddWithValue(weekStart);
            cmd.Parameters.AddWithValue(weekEnd);
            var scalar = await cmd.ExecuteScalarAsync(cancellationToken);
            totalCancellations = scalar is int c ? c : 0;
        }

        return new WeeklyFigures(
            weekStart,
            weekEnd,
            totalObserved,
            totalRoutes,
            onTimePercent,
            prevOnTimePercent,
            changePp,
            bestRoute,
            worstRoute,
            worstHour,
            totalCancellations);
    }

    public async Task SaveAsync(
        DateOnly weekStart,
        string summaryText,
        WeeklyFigures figures,
        string modelId,
        DateTimeOffset createdAt,
        CancellationToken cancellationToken)
    {
        var json = WeeklySummaryPrompt.SerializeFigures(figures);
        await using var conn = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO weekly_summary (week_start, summary_text, input_figures, model_id, created_at)
            VALUES ($1, $2, $3, $4, $5)
            ON CONFLICT (week_start) DO UPDATE SET
                summary_text  = EXCLUDED.summary_text,
                input_figures = EXCLUDED.input_figures,
                model_id      = EXCLUDED.model_id,
                created_at    = EXCLUDED.created_at
            """;
        cmd.Parameters.AddWithValue(weekStart);
        cmd.Parameters.AddWithValue(summaryText);
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Jsonb, Value = json });
        cmd.Parameters.AddWithValue(modelId);
        cmd.Parameters.AddWithValue(createdAt);

        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<List<WeeklyRouteHighlight>> QueryRoutesAsync(
        NpgsqlConnection conn,
        DateOnly weekStart,
        DateOnly weekEnd,
        DateTimeOffset now,
        int early,
        int late,
        int minObs,
        CancellationToken cancellationToken)
    {
        var list = new List<WeeklyRouteHighlight>();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT
                o.route_id,
                coalesce(r.route_short_name, o.route_id) AS short_name,
                coalesce(r.route_long_name, '') AS long_name,
                count(*)::int AS obs,
                round(100.0 * count(*) FILTER (WHERE o.delay_s >= $4 AND o.delay_s <= $5) / count(*), 1)::float8 AS on_time_pct,
                round(avg(o.delay_s)::numeric / 60.0, 1)::float8 AS avg_delay_min
            FROM reliable_stop_observation o
            LEFT JOIN gtfs_route r ON r.route_id = o.route_id
            WHERE o.service_date >= $1 AND o.service_date <= $2 AND o.event_time < $3
            GROUP BY o.route_id, r.route_short_name, r.route_long_name
            HAVING count(*) >= $6
            ORDER BY on_time_pct ASC, obs DESC
            """;
        cmd.Parameters.AddWithValue(weekStart);
        cmd.Parameters.AddWithValue(weekEnd);
        cmd.Parameters.AddWithValue(now);
        cmd.Parameters.AddWithValue(early);
        cmd.Parameters.AddWithValue(late);
        cmd.Parameters.AddWithValue(minObs);

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            list.Add(new WeeklyRouteHighlight(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetInt32(3),
                reader.GetDouble(4),
                reader.GetDouble(5)));
        }

        return list;
    }

    private static async Task<WeeklyHourHighlight?> QueryWorstHourAsync(
        NpgsqlConnection conn,
        DateOnly weekStart,
        DateOnly weekEnd,
        DateTimeOffset now,
        int early,
        int late,
        int minObs,
        CancellationToken cancellationToken)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT
                {ReliabilityQueries.BrisbaneServiceHourSql} AS hr,
                count(*)::int AS obs,
                round(100.0 * count(*) FILTER (WHERE delay_s >= $4 AND delay_s <= $5) / count(*), 1)::float8 AS on_time_pct,
                round(avg(delay_s)::numeric / 60.0, 1)::float8 AS avg_delay_min
            FROM reliable_stop_observation
            WHERE service_date >= $1 AND service_date <= $2 AND event_time < $3
            GROUP BY 1
            HAVING count(*) >= $6
            ORDER BY on_time_pct ASC, obs DESC
            LIMIT 1
            """;
        cmd.Parameters.AddWithValue(weekStart);
        cmd.Parameters.AddWithValue(weekEnd);
        cmd.Parameters.AddWithValue(now);
        cmd.Parameters.AddWithValue(early);
        cmd.Parameters.AddWithValue(late);
        cmd.Parameters.AddWithValue(minObs);

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        // Hours 24 and later are trips past midnight on the same service day (GTFS style).
        var hour = reader.GetInt32(0);
        var clockHour = hour % 24;
        var label = clockHour switch
        {
            0 => "12 am",
            < 12 => $"{clockHour} am",
            12 => "12 pm",
            _ => $"{clockHour - 12} pm",
        } + (hour >= 24 ? " (after midnight)" : "");
        return new WeeklyHourHighlight(hour, label, reader.GetInt32(1), reader.GetDouble(2), reader.GetDouble(3));
    }
}
