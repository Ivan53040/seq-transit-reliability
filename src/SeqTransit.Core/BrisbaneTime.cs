namespace SeqTransit.Core;

/// <summary>
/// Local time for South East Queensland.
/// </summary>
/// <remarks>
/// Queensland does not observe daylight saving, so a fixed UTC+10 offset is exact. Using it
/// avoids depending on time zone data being present in the Lambda runtime image.
/// </remarks>
public static class BrisbaneTime
{
    public static readonly TimeSpan Offset = TimeSpan.FromHours(10);

    /// <summary>The Brisbane calendar date of an instant.</summary>
    public static DateOnly DateOf(DateTimeOffset instant) =>
        DateOnly.FromDateTime(instant.ToOffset(Offset).DateTime);

    /// <summary>Today's date in Brisbane.</summary>
    public static DateOnly Today(TimeProvider clock) => DateOf(clock.GetUtcNow());
}
