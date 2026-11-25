using Npgsql;

namespace SeqTransit.Data;

/// <summary>
/// Makes sure only one load runs at a time, without reserving Lambda concurrency.
/// </summary>
/// <remarks>
/// A PostgreSQL session advisory lock, held on its own connection for the whole run. If the
/// lambda dies, the connection closes and PostgreSQL releases the lock. Loading is idempotent
/// anyway (decision 3), so the lock only saves a second run from repeating the same work.
/// </remarks>
public sealed class LoadLock : IAsyncDisposable
{
    // Different from SchemaMigrator's key, so a migration and a load can never block each other.
    private const long AdvisoryLockKey = 7_316_201_027;

    private readonly NpgsqlConnection _connection;

    private LoadLock(NpgsqlConnection connection) => _connection = connection;

    /// <summary>Returns the lock, or null when another load already holds it.</summary>
    public static async Task<LoadLock?> TryAcquireAsync(NpgsqlDataSource dataSource, CancellationToken cancellationToken)
    {
        var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        try
        {
            await using var command = new NpgsqlCommand($"SELECT pg_try_advisory_lock({AdvisoryLockKey})", connection);
            if (await command.ExecuteScalarAsync(cancellationToken) is true)
            {
                return new LoadLock(connection);
            }
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }

        await connection.DisposeAsync();
        return null;
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            // The connection goes back to the pool, so release the session lock explicitly.
            await using var command = new NpgsqlCommand($"SELECT pg_advisory_unlock({AdvisoryLockKey})", _connection);
            await command.ExecuteScalarAsync(CancellationToken.None);
        }
        finally
        {
            await _connection.DisposeAsync();
        }
    }
}
