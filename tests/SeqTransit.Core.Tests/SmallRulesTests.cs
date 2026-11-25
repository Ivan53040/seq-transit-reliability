using SeqTransit.Core.Ingestion;
using SeqTransit.Core.Reliability;

namespace SeqTransit.Core.Tests;

public class OnTimePolicyTests
{
    [Theory]
    [InlineData(-61, Punctuality.Early)]
    [InlineData(-60, Punctuality.OnTime)]
    [InlineData(0, Punctuality.OnTime)]
    [InlineData(300, Punctuality.OnTime)]
    [InlineData(301, Punctuality.Late)]
    public void Classifies_delays_against_the_default_window(int delaySeconds, Punctuality expected)
    {
        Assert.Equal(expected, OnTimePolicy.Default.Classify(delaySeconds));
    }

    [Fact]
    public void Custom_window_is_respected()
    {
        var strict = new OnTimePolicy(EarlyToleranceSeconds: 0, LateToleranceSeconds: 59);

        Assert.False(strict.IsOnTime(-1));
        Assert.True(strict.IsOnTime(59));
        Assert.False(strict.IsOnTime(60));
    }

    [Fact]
    public void Environment_overrides_use_the_same_names_as_the_api_configuration()
    {
        var values = new Dictionary<string, string?>
        {
            ["OnTime__EarlyToleranceSeconds"] = "0",
            ["OnTime__LateToleranceSeconds"] = "180",
        };

        Assert.Equal(new OnTimePolicy(0, 180), OnTimePolicy.FromEnvironment(name => values.GetValueOrDefault(name)));
        Assert.Equal(OnTimePolicy.Default, OnTimePolicy.FromEnvironment(_ => null));
    }
}

public class SnapshotKeysTests
{
    [Fact]
    public void Round_trips_a_timestamp()
    {
        var timestamp = new DateTimeOffset(2026, 10, 11, 5, 59, 1, TimeSpan.Zero);

        var key = SnapshotKeys.For(timestamp);

        Assert.Equal("trip-updates/v1/1791698341.pb", key);
        Assert.True(SnapshotKeys.TryParse(key, out var parsed));
        Assert.Equal(timestamp, parsed);
    }

    [Fact]
    public void Text_order_matches_time_order()
    {
        var earlier = SnapshotKeys.For(DateTimeOffset.FromUnixTimeSeconds(999_999_999));
        var later = SnapshotKeys.For(DateTimeOffset.FromUnixTimeSeconds(1_000_000_000));

        Assert.True(string.CompareOrdinal(earlier, later) < 0);
    }

    [Theory]
    [InlineData("trip-updates/v1/123.pb")]
    [InlineData("trip-updates/v1/abcdefghij.pb")]
    [InlineData("other/1791698341.pb")]
    [InlineData("trip-updates/v1/1791698341.json")]
    public void Rejects_keys_that_are_not_snapshots(string key)
    {
        Assert.False(SnapshotKeys.TryParse(key, out _));
    }
}

public class BrisbaneTimeTests
{
    [Theory]
    [InlineData("2026-10-11T13:59:59Z", "2026-10-11")]
    [InlineData("2026-10-11T14:00:00Z", "2026-10-12")]
    public void Converts_utc_instants_to_brisbane_dates(string utc, string expectedDate)
    {
        Assert.Equal(DateOnly.Parse(expectedDate), BrisbaneTime.DateOf(DateTimeOffset.Parse(utc)));
    }
}
