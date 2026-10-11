namespace SeqTransit.Data;

/// <summary>
/// How to reach the database. Either a full connection string (local Docker Postgres), or a
/// host and user that sign in to Aurora with short-lived IAM tokens instead of a password.
/// </summary>
public sealed class DatabaseOptions
{
    public const string SectionName = "Database";

    /// <summary>Full connection string for local development. When set, IAM sign-in is not used.</summary>
    public string? ConnectionString { get; set; }

    public string? Host { get; set; }

    public int Port { get; set; } = 5432;

    public string Database { get; set; } = "postgres";

    public string? Username { get; set; }

    /// <summary>AWS region of the cluster, for example ap-southeast-2.</summary>
    public string? Region { get; set; }

    public bool UsesIamAuthentication => string.IsNullOrWhiteSpace(ConnectionString);

    /// <summary>Reads DB_CONNECTION_STRING, or DB_HOST, DB_PORT, DB_NAME, DB_USER and AWS_REGION.</summary>
    public static DatabaseOptions FromEnvironment() => new()
    {
        ConnectionString = Environment.GetEnvironmentVariable("DB_CONNECTION_STRING"),
        Host = Environment.GetEnvironmentVariable("DB_HOST"),
        Port = int.TryParse(Environment.GetEnvironmentVariable("DB_PORT"), out var port) ? port : 5432,
        Database = Environment.GetEnvironmentVariable("DB_NAME") ?? "postgres",
        Username = Environment.GetEnvironmentVariable("DB_USER"),
        Region = Environment.GetEnvironmentVariable("AWS_REGION"),
    };
}
