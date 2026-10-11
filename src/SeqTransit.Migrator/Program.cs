using SeqTransit.Data;

// Usage: dotnet run --project src/SeqTransit.Migrator [migrations-directory]
//
// Local:  DB_CONNECTION_STRING="Host=localhost;Database=seq_transit;Username=postgres;Password=postgres"
// Aurora: DB_HOST=<cluster endpoint> DB_USER=postgres AWS_REGION=ap-southeast-2 (plus AWS credentials)
var directory = args.Length > 0 ? args[0] : Path.Combine(AppContext.BaseDirectory, "migrations");
var options = DatabaseOptions.FromEnvironment();

Console.WriteLine(options.UsesIamAuthentication
    ? $"Migrating {options.Host} as {options.Username} (IAM sign-in)"
    : "Migrating using DB_CONNECTION_STRING");

await using var dataSource = TransitDataSource.Create(options);
var applied = await new SchemaMigrator(dataSource).ApplyAsync(directory, CancellationToken.None);

Console.WriteLine(applied.Count == 0
    ? "Database is already up to date."
    : $"Applied {applied.Count} migration(s): {string.Join(", ", applied)}");
