using System.Net;
using SeqTransit.Core.Ingestion;
using SeqTransit.Core.Model;

namespace SeqTransit.Core.Tests.TestSupport;

internal sealed class ManualTimeProvider(DateTimeOffset now) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = now;

    public override DateTimeOffset GetUtcNow() => Now;
}

internal sealed class InMemorySnapshotStore : ISnapshotStore
{
    private readonly SortedDictionary<string, byte[]> _objects = new(StringComparer.Ordinal);

    public int PutCount { get; private set; }

    public IReadOnlyCollection<string> Keys => _objects.Keys;

    public Task PutAsync(string key, byte[] payload, CancellationToken cancellationToken)
    {
        PutCount++;
        _objects[key] = payload;
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<string>> ListKeysAfterAsync(string startAfter, int maxKeys, CancellationToken cancellationToken)
    {
        IReadOnlyList<string> keys = _objects.Keys
            .Where(k => string.CompareOrdinal(k, startAfter) > 0)
            .Take(maxKeys)
            .ToList();
        return Task.FromResult(keys);
    }

    public Task<byte[]> GetAsync(string key, CancellationToken cancellationToken) =>
        Task.FromResult(_objects[key]);
}

/// <summary>Mimics the database rules: newest data wins and snapshots are recorded once.</summary>
internal sealed class InMemoryObservationStore : IObservationStore
{
    public Dictionary<ObservationKey, StopObservation> Observations { get; } = [];

    public Dictionary<DateTimeOffset, LoadedSnapshot> Snapshots { get; } = [];

    public int SaveCalls { get; private set; }

    public Task<DateTimeOffset?> GetLatestLoadedFeedTimestampAsync(CancellationToken cancellationToken) =>
        Task.FromResult<DateTimeOffset?>(Snapshots.Count == 0 ? null : Snapshots.Keys.Max());

    public Task<SaveResult> SaveAsync(LoadBatch batch, CancellationToken cancellationToken)
    {
        SaveCalls++;
        var written = 0;
        foreach (var observation in batch.Observations)
        {
            if (!Observations.TryGetValue(observation.Key, out var existing) || observation.LastFeedAt > existing.LastFeedAt)
            {
                Observations[observation.Key] = observation;
                written++;
            }
        }

        var snapshotsWritten = 0;
        foreach (var snapshot in batch.Snapshots)
        {
            if (Snapshots.TryAdd(snapshot.FeedTimestamp, snapshot))
            {
                snapshotsWritten++;
            }
        }

        return Task.FromResult(new SaveResult(written, batch.Cancellations.Count, snapshotsWritten));
    }
}

/// <summary>Returns queued responses in order and counts requests.</summary>
internal sealed class QueuedHttpHandler : HttpMessageHandler
{
    private readonly Queue<Func<HttpResponseMessage>> _responses = new();

    public int Requests { get; private set; }

    public QueuedHttpHandler Respond(HttpStatusCode status, byte[]? body = null)
    {
        _responses.Enqueue(() => new HttpResponseMessage(status) { Content = new ByteArrayContent(body ?? []) });
        return this;
    }

    public QueuedHttpHandler Fail(Exception exception)
    {
        _responses.Enqueue(() => throw exception);
        return this;
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests++;
        return Task.FromResult(_responses.Dequeue()());
    }
}
