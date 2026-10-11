using System.Security.Cryptography;
using System.Text;
using Npgsql;

namespace SeqTransit.Data;

/// <summary>
/// Applies db/migrations/*.sql in file-name order, each in its own transaction, and records
/// them in <c>schema_migration</c>. Refuses to run if an applied file was edited afterwards.
/// </summary>
public sealed class SchemaMigrator(NpgsqlDataSource dataSource)
{
    // Arbitrary constant so two migrators (say, CI and a laptop) never run at the same time.
    private const long AdvisoryLockKey = 7_316_201_026;

    private const string CreateHistorySql = """
        CREATE TABLE IF NOT EXISTS schema_migration (
            version    text        PRIMARY KEY,
            checksum   text        NOT NULL,
            applied_at timestamptz NOT NULL DEFAULT now()
        )
        """;

    public async Task<IReadOnlyList<string>> ApplyAsync(string migrationsDirectory, CancellationToken cancellationToken)
    {
        var files = Directory.GetFiles(migrationsDirectory, "*.sql")
            .OrderBy(file => Path.GetFileName(file), StringComparer.Ordinal)
            .ToList();
        if (files.Count == 0)
        {
            throw new InvalidOperationException($"No .sql files found in {migrationsDirectory}.");
        }

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await ExecuteAsync(connection, null, $"SELECT pg_advisory_lock({AdvisoryLockKey})", cancellationToken);
        try
        {
            await ExecuteAsync(connection, null, CreateHistorySql, cancellationToken);
            var applied = await LoadHistoryAsync(connection, cancellationToken);
            var newlyApplied = new List<string>();

            foreach (var file in files)
            {
                var version = Path.GetFileNameWithoutExtension(file);
                var sql = (await File.ReadAllTextAsync(file, cancellationToken)).ReplaceLineEndings("\n");
                var checksum = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sql)));

                if (applied.TryGetValue(version, out var appliedChecksum))
                {
                    if (!string.Equals(appliedChecksum, checksum, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidOperationException(
                            $"Migration {version} changed after it was applied. Add a new migration instead of editing it.");
                    }

                    continue;
                }

                await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
                await ExecuteAsync(connection, transaction, sql, cancellationToken);
                await using (var record = new NpgsqlCommand(
                    "INSERT INTO schema_migration (version, checksum) VALUES (@version, @checksum)", connection, transaction))
                {
                    record.Parameters.AddWithValue("version", version);
                    record.Parameters.AddWithValue("checksum", checksum);
                    await record.ExecuteNonQueryAsync(cancellationToken);
                }

                await transaction.CommitAsync(cancellationToken);
                newlyApplied.Add(version);
            }

            return newlyApplied;
        }
        finally
        {
            await ExecuteAsync(connection, null, $"SELECT pg_advisory_unlock({AdvisoryLockKey})", CancellationToken.None);
        }
    }

    private static async Task<Dictionary<string, string>> LoadHistoryAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        var history = new Dictionary<string, string>(StringComparer.Ordinal);
        await using var command = new NpgsqlCommand("SELECT version, checksum FROM schema_migration", connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            history[reader.GetString(0)] = reader.GetString(1);
        }

        return history;
    }

    private static async Task ExecuteAsync(
        NpgsqlConnection connection, NpgsqlTransaction? transaction, string sql, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
