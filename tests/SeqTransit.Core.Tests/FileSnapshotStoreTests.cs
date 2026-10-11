using SeqTransit.Core.Ingestion;

namespace SeqTransit.Core.Tests;

public sealed class FileSnapshotStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "seq-transit-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Lists_keys_after_a_starting_key_in_time_order()
    {
        var store = new FileSnapshotStore(_root);
        var t0 = new DateTimeOffset(2026, 10, 11, 0, 0, 0, TimeSpan.Zero);
        foreach (var minutes in new[] { 2, 0, 3, 1 })
        {
            await store.PutAsync(SnapshotKeys.For(t0.AddMinutes(minutes)), [(byte)minutes], CancellationToken.None);
        }

        var keys = await store.ListKeysAfterAsync(SnapshotKeys.For(t0), maxKeys: 2, CancellationToken.None);

        Assert.Equal(new[] { SnapshotKeys.For(t0.AddMinutes(1)), SnapshotKeys.For(t0.AddMinutes(2)) }, keys);
        Assert.Equal(new byte[] { 2 }, await store.GetAsync(keys[1], CancellationToken.None));
    }

    [Fact]
    public async Task An_empty_store_lists_nothing()
    {
        var keys = await new FileSnapshotStore(_root).ListKeysAfterAsync("", 10, CancellationToken.None);

        Assert.Empty(keys);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
