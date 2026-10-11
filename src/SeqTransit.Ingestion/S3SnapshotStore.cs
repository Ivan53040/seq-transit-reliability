using Amazon.S3;
using Amazon.S3.Model;
using SeqTransit.Core.Ingestion;

namespace SeqTransit.Ingestion;

/// <summary>Raw feed snapshots in S3, one object per feed version.</summary>
public sealed class S3SnapshotStore(IAmazonS3 s3, string bucketName) : ISnapshotStore
{
    public async Task PutAsync(string key, byte[] payload, CancellationToken cancellationToken)
    {
        using var body = new MemoryStream(payload, writable: false);
        await s3.PutObjectAsync(
            new PutObjectRequest
            {
                BucketName = bucketName,
                Key = key,
                InputStream = body,
                ContentType = "application/x-protobuf",
            },
            cancellationToken);
    }

    public async Task<IReadOnlyList<string>> ListKeysAfterAsync(string startAfter, int maxKeys, CancellationToken cancellationToken)
    {
        var keys = new List<string>();
        string? continuationToken = null;

        do
        {
            var response = await s3.ListObjectsV2Async(
                new ListObjectsV2Request
                {
                    BucketName = bucketName,
                    Prefix = SnapshotKeys.Prefix,
                    StartAfter = startAfter,
                    MaxKeys = maxKeys - keys.Count,
                    ContinuationToken = continuationToken,
                },
                cancellationToken);

            // AWS SDK v4 returns null rather than an empty list when nothing matches.
            keys.AddRange((response.S3Objects ?? []).Select(o => o.Key));
            continuationToken = response.IsTruncated == true ? response.NextContinuationToken : null;
        }
        while (continuationToken is not null && keys.Count < maxKeys);

        return keys;
    }

    public async Task<byte[]> GetAsync(string key, CancellationToken cancellationToken)
    {
        using var response = await s3.GetObjectAsync(bucketName, key, cancellationToken);
        using var buffer = new MemoryStream();
        await response.ResponseStream.CopyToAsync(buffer, cancellationToken);
        return buffer.ToArray();
    }
}
