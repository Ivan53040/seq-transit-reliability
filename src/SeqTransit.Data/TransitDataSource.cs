using Amazon;
using Amazon.RDS.Util;
using Npgsql;

namespace SeqTransit.Data;

public static class TransitDataSource
{
    /// <summary>
    /// Builds a pooled data source. For Aurora it signs in with IAM tokens, refreshed every
    /// 10 minutes (each token is valid for 15), so no database password exists anywhere.
    /// </summary>
    public static NpgsqlDataSource Create(DatabaseOptions options)
    {
        if (!options.UsesIamAuthentication)
        {
            return NpgsqlDataSource.Create(options.ConnectionString!);
        }

        var host = Required(options.Host, "DB_HOST");
        var username = Required(options.Username, "DB_USER");
        var region = RegionEndpoint.GetBySystemName(Required(options.Region, "AWS_REGION"));
        var port = options.Port;

        var connectionString = new NpgsqlConnectionStringBuilder
        {
            Host = host,
            Port = port,
            Database = options.Database,
            Username = username,
            // The Aurora internet access gateway presents a certificate signed by a public AWS root CA.
            SslMode = SslMode.VerifyFull,
            // A paused Aurora Serverless cluster needs a few seconds to resume on first connect.
            Timeout = 30,
            CommandTimeout = 60,
            MaxPoolSize = 5,
        }.ConnectionString;

        var builder = new NpgsqlDataSourceBuilder(connectionString);
        builder.UsePeriodicPasswordProvider(
            async (_, _) => await RDSAuthTokenGenerator.GenerateAuthTokenAsync(region, host, port, username),
            successRefreshInterval: TimeSpan.FromMinutes(10),
            failureRefreshInterval: TimeSpan.FromSeconds(10));
        return builder.Build();
    }

    private static string Required(string? value, string name) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new InvalidOperationException($"{name} must be set when DB_CONNECTION_STRING is not.")
            : value;
}
