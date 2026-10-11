using SeqTransit.Core.Gtfs;

namespace SeqTransit.Core.Ingestion;

public sealed record FetchResult(
    string Key,
    DateTimeOffset FeedTimestamp,
    double FeedAgeSeconds,
    int PayloadBytes,
    int TripUpdateCount,
    bool UsedFallbackTimestamp,
    int Attempts);

/// <summary>
/// Downloads the TripUpdates feed, checks that it parses, and stores the raw bytes.
/// Runs every minute; it never touches the database.
/// </summary>
public sealed class SnapshotFetcher
{
    private static readonly TimeSpan[] RetryDelays = [TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5)];

    private readonly HttpClient _http;
    private readonly ISnapshotStore _store;
    private readonly TimeProvider _clock;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    public SnapshotFetcher(
        HttpClient http,
        ISnapshotStore store,
        TimeProvider clock,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _http = http;
        _store = store;
        _clock = clock;
        _delay = delay ?? ((wait, ct) => Task.Delay(wait, ct));
    }

    public async Task<FetchResult> FetchAsync(Uri feedUrl, CancellationToken cancellationToken)
    {
        var (payload, attempts) = await DownloadWithRetryAsync(feedUrl, cancellationToken);
        var fetchedAt = _clock.GetUtcNow();

        // Parse before storing so a broken response never reaches the snapshot store.
        var message = TripUpdateFeedParser.Parse(payload);
        var usedFallback = message.Header is not { HasTimestamp: true, Timestamp: > 0 };
        var feedTimestamp = TripUpdateFeedParser.FeedTimestamp(message, fetchedAt);
        var key = SnapshotKeys.For(feedTimestamp);

        await _store.PutAsync(key, payload, cancellationToken);

        return new FetchResult(
            key,
            feedTimestamp,
            FeedAgeSeconds: (fetchedAt - feedTimestamp).TotalSeconds,
            PayloadBytes: payload.Length,
            TripUpdateCount: message.Entity.Count(e => e.TripUpdate is not null),
            UsedFallbackTimestamp: usedFallback,
            Attempts: attempts);
    }

    private async Task<(byte[] Payload, int Attempts)> DownloadWithRetryAsync(Uri feedUrl, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return (await _http.GetByteArrayAsync(feedUrl, cancellationToken), attempt);
            }
            catch (Exception ex) when (IsTransient(ex, cancellationToken) && attempt <= RetryDelays.Length)
            {
                await _delay(RetryDelays[attempt - 1], cancellationToken);
            }
        }
    }

    private static bool IsTransient(Exception ex, CancellationToken cancellationToken) => ex switch
    {
        HttpRequestException { StatusCode: null } => true,                       // network failure
        HttpRequestException { StatusCode: { } status } => (int)status >= 500 || (int)status == 429,
        TaskCanceledException => !cancellationToken.IsCancellationRequested,    // HttpClient timeout
        _ => false,
    };
}
