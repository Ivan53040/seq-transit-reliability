namespace SeqTransit.Core.Reliability;

public enum Punctuality
{
    Early,
    OnTime,
    Late,
}

/// <summary>
/// Decides whether a delay counts as on time.
/// </summary>
/// <remarks>
/// The default window (up to 1 minute early, up to 5 minutes late) is a common industry
/// convention, not Translink's official definition. Check what Translink reports publicly
/// and adjust the defaults so your numbers can be compared with theirs.
/// </remarks>
public sealed record OnTimePolicy(int EarlyToleranceSeconds = 60, int LateToleranceSeconds = 300)
{
    public static OnTimePolicy Default { get; } = new();

    /// <summary>
    /// The policy for code without ASP.NET configuration (the weekly summary Lambda). Reads the same
    /// OnTime__EarlyToleranceSeconds / OnTime__LateToleranceSeconds variables the API binds, so the
    /// dashboard and the weekly summary always use one definition.
    /// </summary>
    public static OnTimePolicy FromEnvironment(Func<string, string?>? read = null)
    {
        read ??= Environment.GetEnvironmentVariable;
        return new OnTimePolicy(
            int.TryParse(read("OnTime__EarlyToleranceSeconds"), out var early) ? early : Default.EarlyToleranceSeconds,
            int.TryParse(read("OnTime__LateToleranceSeconds"), out var late) ? late : Default.LateToleranceSeconds);
    }

    public Punctuality Classify(int delaySeconds) =>
        delaySeconds < -EarlyToleranceSeconds ? Punctuality.Early
        : delaySeconds > LateToleranceSeconds ? Punctuality.Late
        : Punctuality.OnTime;

    public bool IsOnTime(int delaySeconds) => Classify(delaySeconds) == Punctuality.OnTime;
}
