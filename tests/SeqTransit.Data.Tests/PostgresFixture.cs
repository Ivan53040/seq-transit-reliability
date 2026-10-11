using Microsoft.EntityFrameworkCore;
using Npgsql;
using Testcontainers.PostgreSql;

namespace SeqTransit.Data.Tests;

/// <summary>One PostgreSQL container for the whole test run, migrated once.</summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:17-alpine").Build();

    public NpgsqlDataSource DataSource { get; private set; } = null!;

    public static string MigrationsDirectory => Path.Combine(AppContext.BaseDirectory, "migrations");

    public async Task InitializeAsync()
    {
        await _container.StartAsync();
        DataSource = NpgsqlDataSource.Create(_container.GetConnectionString());
        await new SchemaMigrator(DataSource).ApplyAsync(MigrationsDirectory, CancellationToken.None);
    }

    public async Task DisposeAsync()
    {
        await DataSource.DisposeAsync();
        await _container.DisposeAsync();
    }

    public TransitDbContext CreateDbContext() =>
        new(new DbContextOptionsBuilder<TransitDbContext>().UseNpgsql(DataSource).Options);

    public async Task ResetAsync()
    {
        await using var command = DataSource.CreateCommand(
            "TRUNCATE feed_snapshot, stop_delay_observation, trip_cancellation");
        await command.ExecuteNonQueryAsync();
    }
}

[CollectionDefinition(Name)]
public sealed class PostgresCollection : ICollectionFixture<PostgresFixture>
{
    public const string Name = "postgres";
}
