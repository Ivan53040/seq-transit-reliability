using SeqTransit.Core.Gtfs;
using SeqTransit.Core.Ingestion;
using SeqTransit.Data;

// Run the pipeline locally, no AWS needed:
//
//   dotnet run --project src/SeqTransit.Local -- fetch   # store one snapshot
//   dotnet run --project src/SeqTransit.Local -- load    # load stored snapshots into Postgres
//   dotnet run --project src/SeqTransit.Local -- run     # fetch every minute, load every 5 minutes (Ctrl+C to stop)
//
//   dotnet run --project src/SeqTransit.Local -- load-gtfs [zip]  # replace the static timetable
//
// Settings (environment variables): SNAPSHOT_DIR (default .local-snapshots), FEED_URL, and either
// DB_CONNECTION_STRING (default: the docker-compose database) or DB_HOST + DB_USER + AWS_REGION
// to sign in to Aurora with IAM (for example, to run load-gtfs against the deployed database).

const string DefaultConnection = "Host=localhost;Port=5433;Database=seq_transit;Username=postgres;Password=postgres";
const string TranslinkTripUpdates = "https://gtfsrt.api.translink.com.au/api/realtime/SEQ/TripUpdates";

var command = args.FirstOrDefault() ?? "run";
var snapshotDirectory = Environment.GetEnvironmentVariable("SNAPSHOT_DIR") ?? ".local-snapshots";
var feedUrl = new Uri(Environment.GetEnvironmentVariable("FEED_URL") ?? TranslinkTripUpdates);
var databaseOptions = DatabaseOptions.FromEnvironment();
if (databaseOptions.UsesIamAuthentication && string.IsNullOrWhiteSpace(databaseOptions.Host))
{
    databaseOptions.ConnectionString = DefaultConnection;
}

using var cancel = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cancel.Cancel();
};

var store = new FileSnapshotStore(snapshotDirectory);
using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
var fetcher = new SnapshotFetcher(http, store, TimeProvider.System);

await using var dataSource = TransitDataSource.Create(databaseOptions);
var loader = new SnapshotLoader(store, new ObservationWriter(dataSource), TimeProvider.System, new LoadOptions());

try
{
    switch (command)
    {
        case "fetch":
            await FetchOnce();
            break;
        case "load":
            await LoadAll();
            break;
        case "run":
            await RunContinuously();
            break;
        case "load-gtfs":
            var zipPath = args.ElementAtOrDefault(1) ?? ".gtfs-cache/SEQ_GTFS.zip";
            if (!await LoadGtfs(zipPath))
            {
                return 1;
            }
            break;
        default:
            Console.Error.WriteLine($"Unknown command '{command}'. Use fetch, load, run or load-gtfs.");
            return 1;
    }
}
catch (OperationCanceledException) when (cancel.IsCancellationRequested)
{
    Console.WriteLine("Stopped.");
}

return 0;

async Task FetchOnce()
{
    var result = await fetcher.FetchAsync(feedUrl, cancel.Token);
    Console.WriteLine(
        $"{DateTime.Now:T} stored {result.Key} ({result.PayloadBytes / 1024} KB, {result.TripUpdateCount} trips, feed {result.FeedAgeSeconds:F0}s old)");
}

async Task LoadAll()
{
    LoadResult batch;
    do
    {
        batch = await loader.LoadBatchAsync(cancel.Token);
        Console.WriteLine(
            $"{DateTime.Now:T} loaded {batch.SnapshotsLoaded} snapshot(s): {batch.ObservationRowsWritten} stop rows, " +
            $"{batch.CancellationRowsWritten} cancellations");
    }
    while (batch.MoreAvailable && batch.SnapshotsLoaded > 0);
}

async Task RunContinuously()
{
    Console.WriteLine($"Fetching every minute into {Path.GetFullPath(snapshotDirectory)}; loading every 5 minutes. Ctrl+C to stop.");
    using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
    var tick = 0;
    do
    {
        try
        {
            await FetchOnce();
            if (tick++ % 5 == 0)
            {
                await LoadAll();
            }
        }
        catch (Exception ex) when (!cancel.IsCancellationRequested)
        {
            Console.Error.WriteLine($"{DateTime.Now:T} {ex.GetType().Name}: {ex.Message}");
        }
    }
    while (await timer.WaitForNextTickAsync(cancel.Token));
}

async Task<bool> LoadGtfs(string path)
{
    if (!File.Exists(path))
    {
        Console.Error.WriteLine($"File not found: {path}");
        return false;
    }
    Console.WriteLine($"Reading static GTFS archive from {path}...");
    var package = StaticGtfsReader.ReadFile(path);
    Console.WriteLine($"Parsed {package.Routes.Count} routes, {package.Stops.Count} stops, {package.Trips.Count} trips.");

    Console.WriteLine("Loading into PostgreSQL with binary COPY...");
    var gtfsLoader = new StaticGtfsLoader(dataSource);
    var res = await gtfsLoader.LoadAsync(package, cancel.Token);
    Console.WriteLine($"Done: Loaded {res.RoutesLoaded} routes, {res.StopsLoaded} stops, {res.TripsLoaded} trips.");
    return true;
}

