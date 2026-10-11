using System.Globalization;

namespace SeqTransit.Core.Ingestion;

/// <summary>
/// Storage keys for raw feed snapshots: <c>trip-updates/v1/{unix-seconds:D10}.pb</c>.
/// </summary>
/// <remarks>
/// The timestamp is zero-padded, so sorting keys as text sorts them by time. That lets the
/// loader ask storage for "every key after the last one I loaded" in a single listing call.
/// The same feed version always maps to the same key, so storing it twice is harmless.
/// </remarks>
public static class SnapshotKeys
{
    public const string Prefix = "trip-updates/v1/";
    private const string Extension = ".pb";

    public static string For(DateTimeOffset feedTimestamp)
    {
        var seconds = feedTimestamp.ToUnixTimeSeconds();
        if (seconds < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(feedTimestamp), "Feed timestamps before 1970 are not supported.");
        }

        return $"{Prefix}{seconds.ToString("D10", CultureInfo.InvariantCulture)}{Extension}";
    }

    public static bool TryParse(string key, out DateTimeOffset feedTimestamp)
    {
        feedTimestamp = default;
        if (!key.StartsWith(Prefix, StringComparison.Ordinal) || !key.EndsWith(Extension, StringComparison.Ordinal))
        {
            return false;
        }

        var digits = key.AsSpan(Prefix.Length, key.Length - Prefix.Length - Extension.Length);
        if (digits.Length != 10 || !long.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds))
        {
            return false;
        }

        feedTimestamp = DateTimeOffset.FromUnixTimeSeconds(seconds);
        return true;
    }
}
