using System.Net;
using Google.Protobuf;
using SeqTransit.Core.Ingestion;
using SeqTransit.Core.Tests.TestSupport;

namespace SeqTransit.Core.Tests;

public class SnapshotFetcherTests
{
    private static readonly Uri FeedUrl = new("https://example.test/TripUpdates");
    private static readonly DateTimeOffset Now = new(2026, 10, 11, 6, 0, 30, TimeSpan.Zero);
    private static readonly DateTimeOffset FeedTime = Now.AddSeconds(-20);

    [Fact]
    public async Task Stores_a_valid_feed_under_its_feed_timestamp()
    {
        var feed = new FeedBuilder().WithTimestamp(FeedTime).WithTrip("t1", [new(1, ArrivalDelay: 0)]).BuildBytes();
        var http = new QueuedHttpHandler().Respond(HttpStatusCode.OK, feed);
        var store = new InMemorySnapshotStore();

        var result = await Fetcher(http, store).FetchAsync(FeedUrl, CancellationToken.None);

        Assert.Equal(SnapshotKeys.For(FeedTime), result.Key);
        Assert.Equal(20d, result.FeedAgeSeconds);
        Assert.Equal(1, result.TripUpdateCount);
        Assert.False(result.UsedFallbackTimestamp);
        Assert.Equal(new[] { result.Key }, store.Keys);
    }

    [Fact]
    public async Task Fetching_the_same_feed_version_twice_keeps_one_snapshot()
    {
        var feed = new FeedBuilder().WithTimestamp(FeedTime).BuildBytes();
        var http = new QueuedHttpHandler().Respond(HttpStatusCode.OK, feed).Respond(HttpStatusCode.OK, feed);
        var store = new InMemorySnapshotStore();
        var fetcher = Fetcher(http, store);

        await fetcher.FetchAsync(FeedUrl, CancellationToken.None);
        await fetcher.FetchAsync(FeedUrl, CancellationToken.None);

        Assert.Single(store.Keys);
    }

    [Fact]
    public async Task Retries_transient_failures_then_succeeds()
    {
        var feed = new FeedBuilder().WithTimestamp(FeedTime).BuildBytes();
        var http = new QueuedHttpHandler()
            .Respond(HttpStatusCode.ServiceUnavailable)
            .Fail(new HttpRequestException("connection reset"))
            .Respond(HttpStatusCode.OK, feed);
        var waits = new List<TimeSpan>();

        var result = await Fetcher(http, new InMemorySnapshotStore(), waits).FetchAsync(FeedUrl, CancellationToken.None);

        Assert.Equal(3, result.Attempts);
        Assert.Equal(new[] { TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5) }, waits);
    }

    [Fact]
    public async Task Gives_up_after_the_last_retry()
    {
        var http = new QueuedHttpHandler()
            .Respond(HttpStatusCode.BadGateway)
            .Respond(HttpStatusCode.BadGateway)
            .Respond(HttpStatusCode.BadGateway);

        await Assert.ThrowsAsync<HttpRequestException>(
            () => Fetcher(http, new InMemorySnapshotStore()).FetchAsync(FeedUrl, CancellationToken.None));
        Assert.Equal(3, http.Requests);
    }

    [Fact]
    public async Task Does_not_retry_client_errors()
    {
        var http = new QueuedHttpHandler().Respond(HttpStatusCode.NotFound);

        await Assert.ThrowsAsync<HttpRequestException>(
            () => Fetcher(http, new InMemorySnapshotStore()).FetchAsync(FeedUrl, CancellationToken.None));
        Assert.Equal(1, http.Requests);
    }

    [Fact]
    public async Task Never_stores_a_payload_that_is_not_a_feed()
    {
        var http = new QueuedHttpHandler().Respond(HttpStatusCode.OK, "<html>maintenance</html>"u8.ToArray());
        var store = new InMemorySnapshotStore();

        await Assert.ThrowsAsync<InvalidProtocolBufferException>(
            () => Fetcher(http, store).FetchAsync(FeedUrl, CancellationToken.None));
        Assert.Equal(0, store.PutCount);
    }

    [Fact]
    public async Task Uses_fetch_time_when_the_feed_has_no_timestamp()
    {
        var http = new QueuedHttpHandler().Respond(HttpStatusCode.OK, new FeedBuilder().BuildBytes());

        var result = await Fetcher(http, new InMemorySnapshotStore()).FetchAsync(FeedUrl, CancellationToken.None);

        Assert.True(result.UsedFallbackTimestamp);
        Assert.Equal(Now, result.FeedTimestamp);
    }

    private static SnapshotFetcher Fetcher(QueuedHttpHandler handler, InMemorySnapshotStore store, List<TimeSpan>? waits = null) =>
        new(
            new HttpClient(handler),
            store,
            new ManualTimeProvider(Now),
            (wait, _) =>
            {
                waits?.Add(wait);
                return Task.CompletedTask;
            });
}
