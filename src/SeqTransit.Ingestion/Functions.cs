using Amazon.Lambda.Core;
using Amazon.Lambda.Serialization.SystemTextJson;
using Amazon.S3;
using Npgsql;
using SeqTransit.Core;
using SeqTransit.Core.Ingestion;
using SeqTransit.Core.Reliability;
using SeqTransit.Data;

[assembly: LambdaSerializer(typeof(DefaultLambdaJsonSerializer))]

namespace SeqTransit.Ingestion;

/// <summary>
/// Runs every minute: downloads the TripUpdates feed and stores it in S3. No database access,
/// so the database can stay paused between loads.
/// </summary>
/// <remarks>
/// Log lines start with a fixed marker (SNAPSHOT_STORED, FEED_STALE) that CloudWatch metric
/// filters count; the alarms in the CDK stack are built on those counts.
/// </remarks>
public sealed class FetchFunction
{
    private const string TranslinkTripUpdates = "https://gtfsrt.api.translink.com.au/api/realtime/SEQ/TripUpdates";

    // Created once per Lambda container and reused across invocations.
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    private readonly SnapshotFetcher _fetcher;
    private readonly Uri _feedUrl;
    private readonly int _staleAfterSeconds;

    public FetchFunction()
    {
        var bucket = Settings.Required("SNAPSHOT_BUCKET");
        _feedUrl = new Uri(Settings.Optional("FEED_URL") ?? TranslinkTripUpdates);
        _staleAfterSeconds = Settings.Int("STALE_AFTER_SECONDS", 300);
        _fetcher = new SnapshotFetcher(Http, new S3SnapshotStore(new AmazonS3Client(), bucket), TimeProvider.System);
    }

    public async Task<FetchResult> HandleAsync(Stream input, ILambdaContext context)
    {
        using var timeout = new CancellationTokenSource(Settings.Budget(context, TimeSpan.FromSeconds(3)));
        var result = await _fetcher.FetchAsync(_feedUrl, timeout.Token);

        context.Logger.LogInformation(
            $"SNAPSHOT_STORED key={result.Key} feedAgeSeconds={result.FeedAgeSeconds:F0} bytes={result.PayloadBytes} " +
            $"tripUpdates={result.TripUpdateCount} attempts={result.Attempts} fallbackTimestamp={result.UsedFallbackTimestamp}");

        if (result.FeedAgeSeconds > _staleAfterSeconds)
        {
            context.Logger.LogWarning($"FEED_STALE feedAgeSeconds={result.FeedAgeSeconds:F0} key={result.Key}");
        }

        return result;
    }
}

public sealed record LoadSummary(
    int Batches,
    int SnapshotsLoaded,
    int SnapshotsUnreadable,
    int ObservationRowsWritten,
    int CancellationRowsWritten,
    DateTimeOffset? Watermark,
    bool BacklogRemaining,
    IReadOnlyList<DateOnly>? ArchivedDays = null);

/// <summary>
/// Runs on a schedule (every 30 minutes by default): loads every stored snapshot newer than the database watermark,
/// in batches, until it has caught up or is close to its time limit.
/// </summary>
public sealed class LoadFunction
{
    // One connection pool per Lambda container.
    private static readonly Lazy<NpgsqlDataSource> DataSource =
        new(() => TransitDataSource.Create(DatabaseOptions.FromEnvironment()));

    private readonly SnapshotLoader _loader;
    private readonly DetailArchiver _archiver;
    private readonly int _retentionDays;

    public LoadFunction()
    {
        var bucket = Settings.Required("SNAPSHOT_BUCKET");
        var options = new LoadOptions { MaxSnapshotsPerBatch = Settings.Int("MAX_SNAPSHOTS_PER_BATCH", 15) };
        _loader = new SnapshotLoader(
            new S3SnapshotStore(new AmazonS3Client(), bucket),
            new ObservationWriter(DataSource.Value),
            TimeProvider.System,
            options);
        _archiver = new DetailArchiver(DataSource.Value);
        _retentionDays = Math.Max(7, Settings.Int("DETAIL_RETENTION_DAYS", 90));
    }

    public async Task<LoadSummary> HandleAsync(Stream input, ILambdaContext context)
    {
        await using var loadLock = await LoadLock.TryAcquireAsync(DataSource.Value, CancellationToken.None);
        if (loadLock is null)
        {
            context.Logger.LogWarning("LOAD_SKIPPED another load is still running; this run does nothing");
            return new LoadSummary(0, 0, 0, 0, 0, null, false);
        }

        var batches = 0;
        var snapshots = 0;
        var unreadable = 0;
        var observationRows = 0;
        var cancellationRows = 0;
        LoadResult batch;

        do
        {
            using var timeout = new CancellationTokenSource(Settings.Budget(context, TimeSpan.FromSeconds(10)));
            batch = await _loader.LoadBatchAsync(timeout.Token);

            batches++;
            snapshots += batch.SnapshotsLoaded;
            unreadable += batch.SnapshotsUnreadable;
            observationRows += batch.ObservationRowsWritten;
            cancellationRows += batch.CancellationRowsWritten;

            context.Logger.LogInformation(
                $"BATCH_LOADED snapshots={batch.SnapshotsLoaded} observationRows={batch.ObservationRowsWritten} " +
                $"cancellationRows={batch.CancellationRowsWritten} unreadable={batch.SnapshotsUnreadable} watermark={batch.Watermark:O}");
        }
        while (batch.MoreAvailable && batch.SnapshotsLoaded > 0 && context.RemainingTime > TimeSpan.FromSeconds(60));

        if (batch.MoreAvailable)
        {
            context.Logger.LogWarning("LOAD_BACKLOG more snapshots are waiting; the next run continues from the watermark");
        }

        // Retention (decision 14): archive at most one old service day per run, and only once the
        // load has caught up, so archiving never delays fresh data.
        IReadOnlyList<DateOnly> archived = [];
        if (!batch.MoreAvailable && context.RemainingTime > TimeSpan.FromMinutes(2))
        {
            using var archiveTimeout = new CancellationTokenSource(Settings.Budget(context, TimeSpan.FromSeconds(30)));
            var cutoff = BrisbaneTime.Today(TimeProvider.System).AddDays(-_retentionDays);
            var result = await _archiver.ArchiveBeforeAsync(cutoff, OnTimePolicy.FromEnvironment(), maxDays: 1, archiveTimeout.Token);
            archived = result.ArchivedDays;
            foreach (var day in result.ArchivedDays)
            {
                context.Logger.LogInformation($"DAY_ARCHIVED serviceDate={day:yyyy-MM-dd} detailRowsDeleted={result.DetailRowsDeleted}");
            }
        }

        return new LoadSummary(
            batches, snapshots, unreadable, observationRows, cancellationRows, batch.Watermark, batch.MoreAvailable, archived);
    }
}

internal static class Settings
{
    public static string Required(string name) =>
        Optional(name) ?? throw new InvalidOperationException($"Environment variable {name} is not set.");

    public static string? Optional(string name) =>
        Environment.GetEnvironmentVariable(name) is { Length: > 0 } value ? value : null;

    public static int Int(string name, int fallback) =>
        int.TryParse(Optional(name), out var value) ? value : fallback;

    /// <summary>Time left in this invocation, minus a safety margin, never below one second.</summary>
    public static TimeSpan Budget(ILambdaContext context, TimeSpan margin)
    {
        var budget = context.RemainingTime - margin;
        return budget > TimeSpan.FromSeconds(1) ? budget : TimeSpan.FromSeconds(1);
    }
}
