namespace SeqTransit.Core.Ingestion;

/// <summary>
/// Keeps snapshots as files on disk, using the same keys as S3. Lets the whole pipeline run
/// on a laptop (see SeqTransit.Local) without an AWS account.
/// </summary>
public sealed class FileSnapshotStore(string rootDirectory) : ISnapshotStore
{
    public async Task PutAsync(string key, byte[] payload, CancellationToken cancellationToken)
    {
        var path = PathFor(key);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        // Write then rename, so a reader never sees a half-written file.
        var temporary = path + ".tmp";
        await File.WriteAllBytesAsync(temporary, payload, cancellationToken);
        File.Move(temporary, path, overwrite: true);
    }

    public Task<IReadOnlyList<string>> ListKeysAfterAsync(string startAfter, int maxKeys, CancellationToken cancellationToken)
    {
        var directory = PathFor(SnapshotKeys.Prefix);
        IReadOnlyList<string> keys = !Directory.Exists(directory)
            ? []
            : Directory.EnumerateFiles(directory, "*.pb")
                .Select(file => SnapshotKeys.Prefix + Path.GetFileName(file))
                .Where(key => string.CompareOrdinal(key, startAfter) > 0)
                .Order(StringComparer.Ordinal)
                .Take(maxKeys)
                .ToList();
        return Task.FromResult(keys);
    }

    public Task<byte[]> GetAsync(string key, CancellationToken cancellationToken) =>
        File.ReadAllBytesAsync(PathFor(key), cancellationToken);

    private string PathFor(string key) =>
        Path.Combine(rootDirectory, key.Replace('/', Path.DirectorySeparatorChar));
}
